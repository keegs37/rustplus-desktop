using RustPlusDesk.Features.BuildSpots.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RustPlusDesk.Features.BuildSpots.Services;

public sealed class MapRasterDecoder
{
    private static readonly string[] BlockingTopologyNames =
    {
        "Ocean", "DeepWater", "River", "Lake", "Road", "Rail", "NoBuild", "MonumentBlocked"
    };

    public (int x, int y) WorldToPixel(RustMapData map, double worldX, double worldZ)
    {
        var metadata = ResolveMetadata(map);
        if (metadata.Width <= 0 || metadata.Height <= 0)
            return (0, 0);

        var spanX = Math.Max(1, metadata.WorldMaxX - metadata.WorldMinX);
        var spanZ = Math.Max(1, metadata.WorldMaxZ - metadata.WorldMinZ);
        var nx = Math.Clamp((worldX - metadata.WorldMinX) / spanX, 0, 1);
        var nz = Math.Clamp((worldZ - metadata.WorldMinZ) / spanZ, 0, 1);
        var px = (int)Math.Round(nx * (metadata.Width - 1), MidpointRounding.AwayFromZero);
        var pyNorm = metadata.OriginAtTopLeft ? 1 - nz : nz;
        var py = (int)Math.Round(pyNorm * (metadata.Height - 1), MidpointRounding.AwayFromZero);
        return (Math.Clamp(px, 0, metadata.Width - 1), Math.Clamp(py, 0, metadata.Height - 1));
    }

    public double? SampleHeight(RustMapData map, double worldX, double worldZ)
    {
        var heightMap = map.TerrainData?.HeightMap;
        if (heightMap?.HasData != true || heightMap.HeightMeters.Count < heightMap.Width * heightMap.Height)
            return null;

        var pixel = ClampToLayer(WorldToPixel(map, worldX, worldZ), heightMap);
        return heightMap.HeightMeters[pixel.y * heightMap.Width + pixel.x];
    }

    public IReadOnlyList<string> SampleTopologyFlags(RustMapData map, double worldX, double worldZ)
    {
        var terrain = map.TerrainData;
        var topology = terrain?.Topology;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (topology?.HasData == true && topology.Flags.Count >= topology.Width * topology.Height)
        {
            var pixel = ClampToLayer(WorldToPixel(map, worldX, worldZ), topology);
            var raw = topology.Flags[pixel.y * topology.Width + pixel.x];
            foreach (KnownTopologyFlags known in Enum.GetValues(typeof(KnownTopologyFlags)))
            {
                if (known != KnownTopologyFlags.None && (raw & (int)known) != 0)
                    names.Add(known.ToString());
            }

            foreach (var item in topology.FlagNames)
            {
                if ((raw & item.Key) != 0 && !string.IsNullOrWhiteSpace(item.Value))
                    names.Add(item.Value);
            }
        }

        AddMaskFlag(terrain?.OceanMask, "Ocean", names, map, worldX, worldZ);
        AddMaskFlag(terrain?.DeepWaterMask, "DeepWater", names, map, worldX, worldZ);
        AddMaskFlag(terrain?.RiverMask, "River", names, map, worldX, worldZ);
        AddMaskFlag(terrain?.LakeMask, "Lake", names, map, worldX, worldZ);
        AddMaskFlag(terrain?.RoadMask, "Road", names, map, worldX, worldZ);
        AddMaskFlag(terrain?.RailMask, "Rail", names, map, worldX, worldZ);
        AddMaskFlag(terrain?.NoBuildMask, "NoBuild", names, map, worldX, worldZ);
        AddMaskFlag(terrain?.MonumentBlockMask, "MonumentBlocked", names, map, worldX, worldZ);
        return names.OrderBy(x => x).ToArray();
    }

    public string? SampleBiome(RustMapData map, double worldX, double worldZ)
    {
        var biomes = map.TerrainData?.Biomes;
        if (biomes?.HasData != true || biomes.Biomes.Count < biomes.Width * biomes.Height)
            return null;

        var pixel = ClampToLayer(WorldToPixel(map, worldX, worldZ), biomes);
        var biome = biomes.Biomes[pixel.y * biomes.Width + pixel.x];
        return string.IsNullOrWhiteSpace(biome) ? null : biome;
    }

    public bool IsBuildBlocked(RustMapData map, double worldX, double worldZ)
        => SampleTopologyFlags(map, worldX, worldZ).Any(IsBlockingTopologyFlag);

    public TerrainSummary AnalyzeCandidate(RustMapData map, WorldPosition center, double radiusMeters, BuildSpotPreferences preferences)
    {
        var terrain = map.TerrainData;
        var missing = new List<string>();
        if (terrain?.HasHeightLayer != true) missing.Add("height");
        if (terrain?.HasBlockingLayers != true) missing.Add("topology/build-blocking");
        if (terrain?.HasBiomeLayer != true) missing.Add("biome");

        var sampleStep = Math.Clamp(radiusMeters / 2.0, 8, 20);
        var samples = BuildSampleGrid(center, radiusMeters, sampleStep).ToArray();
        if (samples.Length == 0)
            samples = new[] { center };

        var heights = new List<double>();
        var slopes = new List<double>();
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var biomeVotes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var cellStates = new List<bool>();

        foreach (var sample in samples)
        {
            var height = SampleHeight(map, sample.X, sample.Z);
            if (height is { } h)
            {
                heights.Add(h);
                var slope = CalculateSlopeAt(map, sample.X, sample.Z, sampleStep);
                if (slope is { } s) slopes.Add(s);
            }

            foreach (var flag in SampleTopologyFlags(map, sample.X, sample.Z))
                flags.Add(flag);

            var biome = SampleBiome(map, sample.X, sample.Z);
            if (!string.IsNullOrWhiteSpace(biome))
                biomeVotes[biome] = biomeVotes.TryGetValue(biome, out var count) ? count + 1 : 1;

            cellStates.Add(!IsBuildBlocked(map, sample.X, sample.Z));
        }

        var avgSlope = slopes.Count == 0 ? 0 : slopes.Average();
        var heightDelta = heights.Count == 0 ? 0 : heights.Max() - heights.Min();
        var exceedsFlatness = heights.Count > 0 && (avgSlope > MaxSlope(preferences.FlatnessRequirement) || heightDelta > MaxDelta(preferences.FlatnessRequirement));
        var blockedByTopology = terrain?.HasBlockingLayers == true && flags.Any(IsBlockingTopologyFlag);
        var buildableCount = terrain?.HasBlockingLayers == true ? LargestContiguousBuildableComponent(samples, cellStates, sampleStep) : 0;
        var buildableRatio = terrain?.HasBlockingLayers == true ? buildableCount / (double)Math.Max(1, samples.Length) : 0;
        var quality = terrain?.HasHeightLayer == true && terrain.HasBlockingLayers
            ? "terrain_backed"
            : terrain?.HasAnyTerrainLayer == true ? "terrain_partial" : "terrain_unknown";

        return new TerrainSummary
        {
            AverageSlopeDegrees = Math.Round(avgSlope, 1),
            MaxHeightDeltaMeters = Math.Round(heightDelta, 1),
            Biome = biomeVotes.Count == 0 ? "unknown" : biomeVotes.OrderByDescending(x => x.Value).First().Key,
            TopologyFlags = flags.OrderBy(x => x).ToArray(),
            IsBuildBlocked = blockedByTopology || exceedsFlatness,
            HasHeightData = terrain?.HasHeightLayer == true,
            HasTopologyData = terrain?.HasTopologyLayer == true,
            HasBiomeData = terrain?.HasBiomeLayer == true,
            HasBlockingData = terrain?.HasBlockingLayers == true,
            BuildableSampleRatio = Math.Round(buildableRatio, 3),
            BuildableSampleCount = buildableCount,
            TotalSampleCount = samples.Length,
            DataQuality = quality,
            MissingLayers = missing.ToArray()
        };
    }

    public static bool IsBlockingTopologyFlag(string flag)
        => BlockingTopologyNames.Any(x => x.Equals(flag, StringComparison.OrdinalIgnoreCase));

    private static MapRasterMetadata ResolveMetadata(RustMapData map)
    {
        var metadata = map.TerrainData?.Metadata;
        if (metadata is { Width: > 0, Height: > 0 })
            return metadata;

        var layer = map.TerrainData?.HeightMap as MapRasterLayer ?? map.TerrainData?.Topology ?? map.TerrainData?.Biomes;
        return new MapRasterMetadata
        {
            Width = layer?.Width ?? 0,
            Height = layer?.Height ?? 0,
            WorldMinX = 0,
            WorldMinZ = 0,
            WorldMaxX = Math.Max(1, map.MapSize),
            WorldMaxZ = Math.Max(1, map.MapSize)
        };
    }

    private double? CalculateSlopeAt(RustMapData map, double worldX, double worldZ, double step)
    {
        var hL = SampleHeight(map, worldX - step, worldZ);
        var hR = SampleHeight(map, worldX + step, worldZ);
        var hD = SampleHeight(map, worldX, worldZ - step);
        var hU = SampleHeight(map, worldX, worldZ + step);
        if (hL is not { } left || hR is not { } right || hD is not { } down || hU is not { } up)
            return null;

        var dx = (right - left) / (step * 2);
        var dz = (up - down) / (step * 2);
        return Math.Atan(Math.Sqrt(dx * dx + dz * dz)) * 180 / Math.PI;
    }

    private static IEnumerable<WorldPosition> BuildSampleGrid(WorldPosition center, double radius, double step)
    {
        for (var x = center.X - radius; x <= center.X + radius + 0.001; x += step)
        {
            for (var z = center.Z - radius; z <= center.Z + radius + 0.001; z += step)
            {
                var dx = x - center.X;
                var dz = z - center.Z;
                if (Math.Sqrt(dx * dx + dz * dz) <= radius)
                    yield return new WorldPosition(x, z);
            }
        }
    }

    private static int LargestContiguousBuildableComponent(IReadOnlyList<WorldPosition> samples, IReadOnlyList<bool> buildable, double step)
    {
        var visited = new bool[samples.Count];
        var best = 0;
        for (var i = 0; i < samples.Count; i++)
        {
            if (!buildable[i] || visited[i]) continue;
            var count = 0;
            var queue = new Queue<int>();
            queue.Enqueue(i);
            visited[i] = true;
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                count++;
                for (var n = 0; n < samples.Count; n++)
                {
                    if (visited[n] || !buildable[n]) continue;
                    var dx = Math.Abs(samples[current].X - samples[n].X);
                    var dz = Math.Abs(samples[current].Z - samples[n].Z);
                    if ((dx <= step + 0.001 && dz < 0.001) || (dz <= step + 0.001 && dx < 0.001))
                    {
                        visited[n] = true;
                        queue.Enqueue(n);
                    }
                }
            }
            best = Math.Max(best, count);
        }
        return best;
    }

    private void AddMaskFlag(MaskRaster? mask, string name, ISet<string> flags, RustMapData map, double worldX, double worldZ)
    {
        if (mask?.HasData != true || mask.Mask.Count < mask.Width * mask.Height) return;
        var pixel = ClampToLayer(WorldToPixel(map, worldX, worldZ), mask);
        if (mask.Mask[pixel.y * mask.Width + pixel.x])
            flags.Add(name);
    }

    private static (int x, int y) ClampToLayer((int x, int y) pixel, MapRasterLayer layer)
        => (Math.Clamp(pixel.x, 0, layer.Width - 1), Math.Clamp(pixel.y, 0, layer.Height - 1));

    private static double MaxSlope(FlatnessRequirement requirement) => requirement switch
    {
        FlatnessRequirement.VeryStrict => 3.0,
        FlatnessRequirement.Strict => 5.0,
        _ => 8.0
    };

    private static double MaxDelta(FlatnessRequirement requirement) => requirement switch
    {
        FlatnessRequirement.VeryStrict => 1.5,
        FlatnessRequirement.Strict => 3.0,
        _ => 5.0
    };
}
