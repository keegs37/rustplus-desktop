using System;
using System.Collections.Generic;

namespace RustPlusDesk.Features.BuildSpots.Models;

public enum BuildSpotMode
{
    Auto,
    SoloStealth,
    DuoBalanced,
    StarterBase,
    FarmBase,
    MonumentRush,
    ClanHighTraffic,
    LowPopHidden
}

public enum PriorityLevel { Low, Medium, High }
public enum ResourcePreference { Wood, Stone, Metal, Sulfur, Balanced }
public enum FlatnessRequirement { Normal, Strict, VeryStrict }
public enum PuzzlePreference { Auto, Ignore, PreferGreen, PreferBlue, PreferRed, PreferFullProgression }

public readonly record struct WorldPosition(double X, double Z);

public sealed class BuildSpotPreferences
{
    public BuildSpotMode Mode { get; init; } = BuildSpotMode.Auto;
    public int TeamSize { get; init; } = 2;
    public IReadOnlyList<string> PreferredMonuments { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AvoidMonuments { get; init; } = Array.Empty<string>();
    public PriorityLevel LootPriority { get; init; } = PriorityLevel.Medium;
    public PriorityLevel SafetyPriority { get; init; } = PriorityLevel.Medium;
    public ResourcePreference ResourcePreference { get; init; } = ResourcePreference.Balanced;
    public FlatnessRequirement FlatnessRequirement { get; init; } = FlatnessRequirement.Normal;
    public PuzzlePreference PuzzlePreference { get; init; } = PuzzlePreference.Auto;
    public double MaxGreenDistanceMeters { get; init; } = 800;
    public double MaxBlueDistanceMeters { get; init; } = 1200;
    public double MaxRedDistanceMeters { get; init; } = 1800;
    public double MaxHighTierDistanceMeters { get; init; } = 2200;
    public double? MaxSafeZoneDistanceMeters { get; init; }
    public double? MinSpawnBeachDistanceMeters { get; init; } = 500;
    public bool IncludeRiskySpots { get; init; }
    public bool AvoidHighTrafficPuzzleMonuments { get; init; } = true;
    public bool PreferRecyclerRoute { get; init; } = true;
    public int ResultCount { get; init; } = 10;
}

public sealed class RustMapData
{
    public string ServerId { get; init; } = string.Empty;
    public string ServerName { get; init; } = string.Empty;
    public int? MapSeed { get; init; }
    public int MapSize { get; init; }
    public string? WipeId { get; init; }
    public DateTime GeneratedAt { get; init; } = DateTime.UtcNow;
    public IReadOnlyList<MapMonument> Monuments { get; init; } = Array.Empty<MapMonument>();
    public RustMapTerrainData? TerrainData { get; init; }
}

public sealed class RustMapTerrainData
{
    public MapRasterMetadata Metadata { get; init; } = new();
    public HeightMapRaster? HeightMap { get; init; }
    public TopologyRaster? Topology { get; init; }
    public BiomeRaster? Biomes { get; init; }
    public MaskRaster? OceanMask { get; init; }
    public MaskRaster? DeepWaterMask { get; init; }
    public MaskRaster? RiverMask { get; init; }
    public MaskRaster? LakeMask { get; init; }
    public MaskRaster? RoadMask { get; init; }
    public MaskRaster? RailMask { get; init; }
    public MaskRaster? NoBuildMask { get; init; }
    public MaskRaster? MonumentBlockMask { get; init; }

    public bool HasHeightLayer => HeightMap?.HasData == true;
    public bool HasTopologyLayer => Topology?.HasData == true;
    public bool HasBiomeLayer => Biomes?.HasData == true;
    public bool HasBlockingLayers => HasTopologyLayer || OceanMask?.HasData == true || DeepWaterMask?.HasData == true ||
                                     RiverMask?.HasData == true || LakeMask?.HasData == true || RoadMask?.HasData == true ||
                                     RailMask?.HasData == true || NoBuildMask?.HasData == true || MonumentBlockMask?.HasData == true;
    public bool HasAnyTerrainLayer => HasHeightLayer || HasTopologyLayer || HasBiomeLayer || HasBlockingLayers;
}

public sealed class MapRasterMetadata
{
    public int Width { get; init; }
    public int Height { get; init; }
    public double WorldMinX { get; init; }
    public double WorldMinZ { get; init; }
    public double WorldMaxX { get; init; }
    public double WorldMaxZ { get; init; }
    public bool OriginAtTopLeft { get; init; } = true;
}

public abstract class MapRasterLayer
{
    public int Width { get; init; }
    public int Height { get; init; }
    public bool HasData => Width > 0 && Height > 0;
}

public sealed class HeightMapRaster : MapRasterLayer
{
    public IReadOnlyList<double> HeightMeters { get; init; } = Array.Empty<double>();
}

public sealed class TopologyRaster : MapRasterLayer
{
    public IReadOnlyList<int> Flags { get; init; } = Array.Empty<int>();
    public IReadOnlyDictionary<int, string> FlagNames { get; init; } = new Dictionary<int, string>();
}

public sealed class BiomeRaster : MapRasterLayer
{
    public IReadOnlyList<string> Biomes { get; init; } = Array.Empty<string>();
}

public sealed class MaskRaster : MapRasterLayer
{
    public IReadOnlyList<bool> Mask { get; init; } = Array.Empty<bool>();
}

[Flags]
public enum KnownTopologyFlags
{
    None = 0,
    Field = 1 << 0,
    Forest = 1 << 1,
    Ocean = 1 << 2,
    DeepWater = 1 << 3,
    River = 1 << 4,
    Lake = 1 << 5,
    Road = 1 << 6,
    Rail = 1 << 7,
    NoBuild = 1 << 8,
    MonumentBlocked = 1 << 9,
    Cliff = 1 << 10
}

public sealed class MapMonument
{
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();
    public string Tier { get; init; } = "unknown";
    public WorldPosition Position { get; init; }
    public bool HasRecycler { get; init; }
    public bool HasRefinery { get; init; }
    public bool HasResearchTable { get; init; }
    public double LootValue { get; init; } = 35;
    public double TrafficRisk { get; init; } = 45;
    public MonumentPuzzleMetadata Puzzle { get; init; } = new();
    public Dictionary<string, double[]> IdealDistanceByMode { get; init; } = new();
}

public sealed class MonumentPuzzleMetadata
{
    public bool IsGreenCardSource { get; init; }
    public bool IsBlueCardSource { get; init; }
    public bool IsRedCardSource { get; init; }
    public bool RequiresGreenCard { get; init; }
    public bool RequiresBlueCard { get; init; }
    public bool RequiresRedCard { get; init; }
    public double EstimatedPuzzleValue { get; init; }
}

public sealed class BuildCandidate
{
    public string CandidateId { get; init; } = string.Empty;
    public WorldPosition WorldPosition { get; init; }
    public string Grid { get; init; } = string.Empty;
    public double RadiusMeters { get; init; } = 30;
    public TerrainSummary Terrain { get; init; } = new();
    public double BuildablePatchMeters { get; init; }
}

public sealed class TerrainSummary
{
    public double AverageSlopeDegrees { get; init; }
    public double MaxHeightDeltaMeters { get; init; }
    public string Biome { get; init; } = "unknown";
    public IReadOnlyList<string> TopologyFlags { get; init; } = Array.Empty<string>();
    public bool IsBuildBlocked { get; init; }
    public bool HasHeightData { get; init; }
    public bool HasTopologyData { get; init; }
    public bool HasBiomeData { get; init; }
    public bool HasBlockingData { get; init; }
    public double BuildableSampleRatio { get; init; }
    public int BuildableSampleCount { get; init; }
    public int TotalSampleCount { get; init; }
    public string DataQuality { get; init; } = "terrain_unknown";
    public IReadOnlyList<string> MissingLayers { get; init; } = Array.Empty<string>();
}

public sealed class BuildSpotScores
{
    public double Flatness { get; init; }
    public double BuildableArea { get; init; }
    public double LootAccess { get; init; }
    public double ResourceAccess { get; init; }
    public double Safety { get; init; }
    public double Mobility { get; init; }
    public double PuzzleAccess { get; init; }
    public double MonumentPreference { get; init; }
    public double TrafficRisk { get; init; }
    public double RaidRisk { get; init; }
}

public sealed class PuzzleRoute
{
    public string Name { get; init; } = string.Empty;
    public double DistanceMeters { get; init; }
    public double Score { get; init; }
}

public sealed class PuzzleProgressionChain
{
    public PuzzleRoute? BestGreenSource { get; init; }
    public PuzzleRoute? BestBlueSource { get; init; }
    public PuzzleRoute? BestRedSource { get; init; }
    public PuzzleRoute? BestHighTierTarget { get; init; }
    public double ChainScore { get; init; }
    public string ChainSummary { get; init; } = string.Empty;
}

public sealed class PuzzleAccessScore
{
    public double GreenCardAccess { get; init; }
    public double BlueCardAccess { get; init; }
    public double RedCardAccess { get; init; }
    public double ProgressionChainScore { get; init; }
    public double OverallPuzzleScore { get; init; }
    public PuzzleRoute? BestGreenRoute { get; init; }
    public PuzzleRoute? BestBlueRoute { get; init; }
    public PuzzleRoute? BestRedRoute { get; init; }
    public PuzzleProgressionChain? BestProgressionChain { get; init; }
}

public sealed class NearbyMonument
{
    public string Name { get; init; } = string.Empty;
    public double DistanceMeters { get; init; }
    public string Role { get; init; } = string.Empty;
}

public sealed class BuildSpotRecommendation
{
    public int Rank { get; init; }
    public string Grid { get; init; } = string.Empty;
    public WorldPosition WorldPosition { get; init; }
    public double RadiusMeters { get; init; }
    public string ModeFit { get; init; } = string.Empty;
    public double OverallScore { get; init; }
    public BuildSpotScores Scores { get; init; } = new();
    public TerrainSummary Terrain { get; init; } = new();
    public PuzzleAccessScore PuzzleAccess { get; init; } = new();
    public IReadOnlyList<NearbyMonument> NearbyMonuments { get; init; } = Array.Empty<NearbyMonument>();
    public IReadOnlyList<string> Pros { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Cons { get; init; } = Array.Empty<string>();
    public string AiSummary { get; init; } = string.Empty;
}

public sealed class OptionalAppSignals
{
    public static OptionalAppSignals Empty { get; } = new();
}
