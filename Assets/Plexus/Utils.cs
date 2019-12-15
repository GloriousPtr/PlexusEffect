using Unity.Mathematics;

public static class Utils
{
    public static float SqrMagnitude(this float3 f)
    {
        return f.x * f.x + f.y * f.y + f.z * f.z;
    }
}