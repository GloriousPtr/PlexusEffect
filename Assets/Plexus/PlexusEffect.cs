using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using UnityEngine;
using Unity.Jobs;
using Unity.Mathematics;

[RequireComponent(typeof(ParticleSystem))]
public class PlexusEffect : MonoBehaviour
{
    public float maxDistance = 1.0f;
    public int maxConnections = 10;
    public int maxLineRenderers = 100;
    public LineRenderer linePrefab;

    private new ParticleSystem particleSystem;
    private ParticleSystem.Particle[] particles;
    private ParticleSystem.MainModule particleSystemMainModule;

    private List<LineRenderer> lineRenderers = new List<LineRenderer>();
    private Transform _transform;

    private void Start()
    {
        _transform = transform;
        particleSystem = GetComponent<ParticleSystem>();
        particleSystemMainModule = particleSystem.main;
        SetupSimulationSpace();
    }
    
    private void Update()
    {
        int maxParticles = particleSystemMainModule.maxParticles;
        if (particles == null || particles.Length < maxParticles)
            particles = new ParticleSystem.Particle[maxParticles];
        particleSystem.GetParticles(particles);
        
        NativeArray<float3> positions = new NativeArray<float3>(maxParticles, Allocator.Persistent);
        
        for (int i = 0; i < maxParticles; i++)
        {
            positions[i] = particles[i].position;
        }

        NativeArray<int> lineRendererCount = new NativeArray<int>(1, Allocator.TempJob) {[0] = lineRenderers.Count};

        NativeArray<float3> positionsOut0 = new NativeArray<float3>(maxLineRenderers, Allocator.TempJob);
        NativeArray<float3> positionsOut1 = new NativeArray<float3>(maxLineRenderers, Allocator.TempJob);
        NativeArray<bool> lines = new NativeArray<bool>(maxLineRenderers, Allocator.TempJob);
        NativeArray<int> lrIndex = new NativeArray<int>(1, Allocator.TempJob);

        PlexusJob j = new PlexusJob()
        {
            maxDistance = maxDistance,
            maxConnections = maxConnections,
            maxLineRenderers = maxLineRenderers,
            positions = positions,
            lineRendererCount = lineRendererCount,
            particleCount = particleSystem.particleCount,
            positionsOut0 = positionsOut0,
            positionsOut1 = positionsOut1,
            lines = lines,
            lrIndex = lrIndex
        };
        JobHandle handle = j.Schedule();
        handle.Complete();
        
        // Instantiate.
        for (int i = 0; i < lrIndex[0] - lineRendererCount[0]; i++)
        {
            LineRenderer lr = Instantiate(linePrefab, _transform, false);
            lineRenderers.Add(lr);
            lineRendererCount[0]++;
        }
        
        // SetPos
        for (int i = 0; i < lineRendererCount[0]; i++)
        {
            LineRenderer lr = lineRenderers[i];
            lr.enabled = lines[i];
            lr.SetPosition(0, positionsOut0[i]);
            lr.SetPosition(1, positionsOut1[i]);
        }

        lineRendererCount.Dispose();
        positions.Dispose();
        positionsOut0.Dispose();
        positionsOut1.Dispose();
        lines.Dispose();
        lrIndex.Dispose();
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
}

[BurstCompile]
struct PlexusJob : IJob
{
    // Inputs
    public float maxDistance;
    public int maxConnections;
    public int maxLineRenderers;

    public NativeArray<float3> positions;
    
    public NativeArray<int> lineRendererCount;
    public int particleCount;
    
    // Outputs
    public NativeArray<float3> positionsOut0;
    public NativeArray<float3> positionsOut1;
    public NativeArray<bool> lines;

    public NativeArray<int> lrIndex;
    
    public void Execute()
    {
        if (maxConnections < 0 && maxLineRenderers < 0)
            return;
        
        float maxDistanceSqr = maxDistance * maxDistance;
        
        for (int i = 0; i < particleCount; i++)
        {
            if (lrIndex[0] == maxLineRenderers)
            {
                break;
            }
            int connections = 0;
            float3 p1_position = positions[i];
            for (int j = i + 1; j < particleCount; j++)
            {
                float3 p2_position = positions[j];
                float distanceSqr = (p1_position - p2_position).SqrMagnitude();

                if (distanceSqr <= maxDistanceSqr)
                {
                    lines[lrIndex[0]] = true;
                    
                    positionsOut0[lrIndex[0]] = p1_position;
                    positionsOut1[lrIndex[0]] = p2_position;
                    lrIndex[0]++;
                    connections++;
                    
                    if (connections == maxConnections || lrIndex[0] == maxLineRenderers)
                    {
                        break;
                    }
                }
            }
        }

        for (int i = lrIndex[0]; i < lineRendererCount[0]; i++)
        {
            lines[i] = false;
        }
    }
}