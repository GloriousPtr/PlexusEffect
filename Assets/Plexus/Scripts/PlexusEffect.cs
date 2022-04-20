using System;
using Unity.Burst;
using Unity.Collections;
using UnityEngine;
using Unity.Jobs;
using Unity.Mathematics;
using System.Runtime.CompilerServices;

using Unity.Profiling;

[RequireComponent(typeof(ParticleSystem))]
public class PlexusEffect : MonoBehaviour
{
    public bool useJobs;

    public float maxDistance = 6.0f;
    public int maxConnections = 20;
    public int maxLineRendererPerJob = 250;
    public int jobWorkers = 8;
    
    public LineRenderer linePrefab;

    private new ParticleSystem particleSystem;
    private ParticleSystem.Particle[] particles;
    private ParticleSystem.MainModule particleSystemMainModule;
    private LineRenderer[] lineRenderers;
    private Transform _transform;

    private NativeArray<float3> nonJobPositions;

    private NativeArray<float3> positions;
    private NativeArray<Line>[] linesData;
    private PlexusJob[] jobs;
    private NativeArray<JobHandle> jobHandles;

    static readonly ProfilerMarker PrepareDataMarker = new ProfilerMarker("Plexus::PrpareData");
    static readonly ProfilerMarker PrepareJobsMarker = new ProfilerMarker("Plexus::PrpareJobs");
    static readonly ProfilerMarker RunCalculations = new ProfilerMarker("Plexus::RunCalculations");
    static readonly ProfilerMarker UpdateLineRenderersMarker = new ProfilerMarker("Plexus::UpdateLineRenderers");

    private void Start()
    {
        _transform = transform;
        particleSystem = GetComponent<ParticleSystem>();
        particleSystemMainModule = particleSystem.main;
        SetupSimulationSpace();

        int maxParticles = particleSystemMainModule.maxParticles;
        if (particles == null || particles.Length < maxParticles)
            particles = new ParticleSystem.Particle[maxParticles];

        int maxLineRenderers = maxLineRendererPerJob * jobWorkers;
        lineRenderers = new LineRenderer[maxLineRenderers];
        for (int i = 0; i < maxLineRenderers; i++)
            lineRenderers[i] = Instantiate(linePrefab, _transform, false);

        nonJobPositions = new NativeArray<float3>(particles.Length, Allocator.Persistent);

        positions = new NativeArray<float3>(particles.Length, Allocator.Persistent);
        jobHandles = new NativeArray<JobHandle>(jobWorkers, Allocator.Persistent);

        linesData = new NativeArray<Line>[jobWorkers];
        jobs = new PlexusJob[jobWorkers];
        int particleCountPerJob = particleSystemMainModule.maxParticles / jobWorkers;
        for (int i = 0; i < jobWorkers; i++)
        {
            linesData[i] = new NativeArray<Line>(maxLineRendererPerJob, Allocator.Persistent);
            jobs[i] = new PlexusJob()
            {
                maxDistanceSquared = maxDistance * maxDistance,
                maxConnections = maxConnections,
                lineRendererCount = maxLineRendererPerJob,
                minParticleIndex = i * particleCountPerJob,
                maxParticleIndex = (i + 1) * particleCountPerJob,
                positions = positions,
                lines = linesData[i],
            };
        }

        PrepareJobs();
    }

    private void OnDestroy()
    {
        JobHandle.CompleteAll(jobHandles);
        jobHandles.Dispose();
        positions.Dispose();
        for (int i = 0; i < jobWorkers; i++)
            linesData[i].Dispose();

        nonJobPositions.Dispose();
    }

    private void Update()
    {
        if (useJobs)
            JobifiedPlexus();
        else
            Plexus();
    }

    private void SetupSimulationSpace()
    {
        switch (particleSystemMainModule.simulationSpace)
        {
            case ParticleSystemSimulationSpace.Local:
                _transform = transform;
                linePrefab.useWorldSpace = false;
                break;
            case ParticleSystemSimulationSpace.Custom:
                _transform = particleSystemMainModule.customSimulationSpace;
                linePrefab.useWorldSpace = false;
                break;
            case ParticleSystemSimulationSpace.World:
                linePrefab.useWorldSpace = true;
                break;
            default:
                throw new NotSupportedException("Unsupported simulation space");
        }
    }

    private void PrepareJobs()
    {
        PrepareDataMarker.Begin();

        int maxParticles = particles.Length;
        particleSystem.GetParticles(particles);
        for (int i = 0; i < maxParticles; i++)
            positions[i] = particles[i].position;

        PrepareDataMarker.End();

        PrepareJobsMarker.Begin();

        for (int i = 0; i < jobWorkers; i++)
            jobHandles[i] = jobs[i].Schedule();

        PrepareJobsMarker.End();
    }

    private void JobifiedPlexus()
    {
        // Complete
        RunCalculations.Begin();
        JobHandle.CompleteAll(jobHandles);
        RunCalculations.End();

        // Render
        UpdateLineRenderersMarker.Begin();
        for (int i = 0; i < jobWorkers; i++)
        {
            NativeArray<Line> lineData = linesData[i];
            int offset = i * maxLineRendererPerJob;

            for (int j = 0; j < maxLineRendererPerJob; j++)
            {
                LineRenderer lr = lineRenderers[offset + j];
                Line line = lineData[j];

                bool lrEnabled = line.enabled;
                lr.enabled = lrEnabled;

                if (!lrEnabled)
                    continue;

                lr.SetPosition(0, positions[line.position0]);
                lr.SetPosition(1, positions[line.position1]);
            }
        }

        UpdateLineRenderersMarker.End();

        PrepareJobs();
    }

    private void Plexus()
    {
        PrepareDataMarker.Begin();

        int lrIndex = 0;
        int maxParticles = particleSystemMainModule.maxParticles;
        int maxLineRenderers = maxLineRendererPerJob * jobWorkers;
        float maxDistanceSquared = maxDistance * maxDistance;

        particleSystem.GetParticles(particles);
        for (int i = 0; i < maxParticles; i++)
            nonJobPositions[i] = particles[i].position;

        PrepareDataMarker.End();

        RunCalculations.Begin();
        UpdateLineRenderersMarker.Begin();

        for (int i = 0; i < maxParticles; i++)
        {
            if (lrIndex >= maxLineRenderers)
                break;

            int connections = 0;
            float3 p0_position = nonJobPositions[i];
            for (int j = i + 1; j < maxParticles; j++)
            {
                float3 p1_position = nonJobPositions[j];
                float distanceSqr = math.lengthsq(p0_position - p1_position);

                LineRenderer lr = lineRenderers[lrIndex];

                if (distanceSqr > maxDistanceSquared)
                {
                    lr.enabled = false;
                    continue;
                }

                lr.enabled = true;
                lr.SetPosition(0, p0_position);
                lr.SetPosition(1, p1_position);

                lrIndex++;
                connections++;

                if (connections >= maxConnections || lrIndex >= maxLineRenderers)
                    break;
            }
        }

        UpdateLineRenderersMarker.End();
        RunCalculations.End();
    }
}

[BurstCompile]
struct Line
{
    public short position0;
    public short position1;
    public bool enabled;

    public Line(in short p0, in short p1, in bool enable)
    {
        position0 = p0;
        position1 = p1;
        enabled = enable;
    }
}

[BurstCompile(FloatPrecision = FloatPrecision.Low, FloatMode = FloatMode.Fast)]
struct PlexusJob : IJob
{
    [ReadOnly] public float maxDistanceSquared;
    [ReadOnly] public int maxConnections;
    [ReadOnly] public int lineRendererCount;
    [ReadOnly] public int minParticleIndex;
    [ReadOnly] public int maxParticleIndex;
    [ReadOnly] public NativeArray<float3> positions;

    [WriteOnly] public NativeArray<Line> lines;

    public void Execute()
    {
        int lrIndex = 0;

        for (int i = minParticleIndex; i < maxParticleIndex; i++)
        {
            if (lrIndex >= lineRendererCount)
                break;

            int connections = 0;
            for (int j = i + 1; j < maxParticleIndex; j++)
            {
                float3 distance = positions[i] - positions[j];
                float distanceMagnitudeSqr = math.lengthsq(distance);
                //float distanceMagnitudeSqr = SqrMagnitude(ref distance);

                if (distanceMagnitudeSqr > maxDistanceSquared)
                    continue;

                lines[lrIndex] = new Line((short) i, (short) j, true);

                lrIndex++;
                connections++;
                
                if (connections >= maxConnections || lrIndex >= lineRendererCount)
                    break;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float SqrMagnitude(ref float3 value) => value.x * value.x + value.y * value.y + value.z * value.z;
}
