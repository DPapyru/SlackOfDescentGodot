using System.Text.Json.Serialization;

namespace SlackOfDescent;

public sealed class GmGraphicsStats
{
    [JsonPropertyName("surfaces")]public int Surfaces{get;init;}
    [JsonPropertyName("textures")]public int Textures{get;init;}
    [JsonPropertyName("pixelCaches")]public int PixelCaches{get;init;}
    [JsonPropertyName("generated")]public int Generated{get;init;}
    [JsonPropertyName("triangleCacheEntries")]public int TriangleCacheEntries{get;init;}
    [JsonPropertyName("cpuFallbacks")]public int CpuFallbacks{get;init;}
    [JsonPropertyName("triangleCacheBytes")]public long TriangleCacheBytes{get;init;}
    [JsonPropertyName("triangleCacheHits")]public long TriangleCacheHits{get;init;}
    [JsonPropertyName("triangleCacheMisses")]public long TriangleCacheMisses{get;init;}
    [JsonPropertyName("builds")]public long Builds{get;init;}
    [JsonPropertyName("buildTotalMs")]public double BuildTotalMs{get;init;}
    [JsonPropertyName("lastBuildMs")]public double LastBuildMs{get;init;}
    [JsonPropertyName("effects")]public object? Effects{get;init;}
}
