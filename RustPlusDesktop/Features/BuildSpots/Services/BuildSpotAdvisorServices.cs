using RustPlusDesk.Features.BuildSpots.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RustPlusDesk.Features.BuildSpots.Services;

public interface IBuildSpotAdvisorService
{
    Task<IReadOnlyList<BuildSpotRecommendation>> RecommendAsync(BuildSpotPreferences preferences, CancellationToken ct);
}

public interface IBuildCandidateGenerator
{
    IReadOnlyList<BuildCandidate> GenerateCandidates(RustMapData map, BuildSpotPreferences preferences);
}

public interface IBuildSpotScorer
{
    BuildSpotScores Score(BuildCandidate candidate, RustMapData map, BuildSpotPreferences preferences, OptionalAppSignals appSignals);
}

public interface IPuzzleAccessScorer
{
    PuzzleAccessScore ScorePuzzleAccess(BuildCandidate candidate, RustMapData map, BuildSpotPreferences preferences);
}

public interface IMapProviderClient
{
    Task<RustMapData?> FindMapAsync(int seed, int size, CancellationToken ct);
}

public sealed class BuildSpotAdvisorService : IBuildSpotAdvisorService
{
    public const string ScoringVersion = "build-spots-mvp-1";

    private readonly RustMapData _map;
    private readonly IBuildCandidateGenerator _candidateGenerator;
    private readonly IBuildSpotScorer _scorer;
    private readonly BuildSpotExplanationService _explanations;

    public BuildSpotAdvisorService(RustMapData map)
        : this(map, new BuildCandidateGenerator(), new BuildSpotScorer(new PuzzleAccessScorer()), new BuildSpotExplanationService())
    {
    }

    public BuildSpotAdvisorService(RustMapData map, IBuildCandidateGenerator candidateGenerator, IBuildSpotScorer scorer, BuildSpotExplanationService explanations)
    {
        _map = map;
        _candidateGenerator = candidateGenerator;
        _scorer = scorer;
        _explanations = explanations;
    }

    public Task<IReadOnlyList<BuildSpotRecommendation>> RecommendAsync(BuildSpotPreferences preferences, CancellationToken ct)
    {
        var resolvedPreferences = preferences.WithResolvedAutoMode();
        var candidates = _candidateGenerator.GenerateCandidates(_map, resolvedPreferences);
        var ranked = candidates
            .Select(candidate =>
            {
                var scores = _scorer.Score(candidate, _map, resolvedPreferences, OptionalAppSignals.Empty);
                var puzzle = _scorer is BuildSpotScorer buildSpotScorer ? buildSpotScorer.LastPuzzleScore ?? new PuzzleAccessScore() : new PuzzleAccessScorer().ScorePuzzleAccess(candidate, _map, resolvedPreferences);
                var overall = BuildSpotScorer.CalculateOverall(scores, resolvedPreferences.Mode);
                return new { candidate, scores, puzzle, overall };
            })
            .Where(x => resolvedPreferences.IncludeRiskySpots || x.scores.TrafficRisk < 82)
            .OrderByDescending(x => x.overall)
            .Take(Math.Clamp(resolvedPreferences.ResultCount, 5, 25))
            .Select((x, index) =>
            {
                var nearby = BuildSpotMath.NearestMonuments(x.candidate.WorldPosition, _map.Monuments, 4)
                    .Select(m => new NearbyMonument
                    {
                        Name = m.monument.Name,
                        DistanceMeters = Math.Round(m.distance),
                        Role = BuildSpotMath.MonumentRole(m.monument)
                    }).ToArray();
                var pros = _explanations.BuildPros(x.scores, x.puzzle, nearby, resolvedPreferences.Mode).ToArray();
                var cons = _explanations.BuildCons(x.scores, x.puzzle, nearby, resolvedPreferences.Mode).ToArray();

                return new BuildSpotRecommendation
                {
                    Rank = index + 1,
                    Grid = x.candidate.Grid,
                    WorldPosition = x.candidate.WorldPosition,
                    RadiusMeters = x.candidate.RadiusMeters,
                    ModeFit = BuildSpotPreferencesExtensions.ToWireName(resolvedPreferences.Mode),
                    OverallScore = Math.Round(x.overall, 1),
                    Scores = x.scores,
                    PuzzleAccess = x.puzzle,
                    NearbyMonuments = nearby,
                    Pros = pros,
                    Cons = cons,
                    AiSummary = _explanations.Explain(x.candidate, x.scores, x.puzzle, resolvedPreferences, nearby)
                };
            })
            .ToArray();

        return Task.FromResult<IReadOnlyList<BuildSpotRecommendation>>(ranked);
    }
}

public static class BuildSpotPreferencesExtensions
{
    public static BuildSpotPreferences WithResolvedAutoMode(this BuildSpotPreferences preferences)
    {
        if (preferences.Mode != BuildSpotMode.Auto)
            return preferences;

        var mode = BuildSpotMode.DuoBalanced;
        if (preferences.TeamSize == 1 && preferences.SafetyPriority == PriorityLevel.High)
            mode = BuildSpotMode.SoloStealth;
        else if (preferences.PuzzlePreference == PuzzlePreference.PreferFullProgression && preferences.TeamSize <= 3)
            mode = BuildSpotMode.DuoBalanced;
        else if (preferences.PuzzlePreference == PuzzlePreference.PreferRed && preferences.SafetyPriority != PriorityLevel.High)
            mode = BuildSpotMode.MonumentRush;
        else if (preferences.ResourcePreference is ResourcePreference.Stone or ResourcePreference.Metal or ResourcePreference.Sulfur && preferences.LootPriority != PriorityLevel.High)
            mode = BuildSpotMode.FarmBase;
        else if (preferences.LootPriority == PriorityLevel.High && preferences.SafetyPriority == PriorityLevel.Low)
            mode = BuildSpotMode.MonumentRush;
        else if (preferences.TeamSize >= 4)
            mode = BuildSpotMode.ClanHighTraffic;
        else if (preferences.TeamSize <= 1 && preferences.LootPriority == PriorityLevel.Low)
            mode = BuildSpotMode.StarterBase;

        return new BuildSpotPreferences
        {
            Mode = mode,
            TeamSize = preferences.TeamSize,
            PreferredMonuments = preferences.PreferredMonuments,
            AvoidMonuments = preferences.AvoidMonuments,
            LootPriority = preferences.LootPriority,
            SafetyPriority = preferences.SafetyPriority,
            ResourcePreference = preferences.ResourcePreference,
            FlatnessRequirement = preferences.FlatnessRequirement,
            PuzzlePreference = preferences.PuzzlePreference,
            MaxGreenDistanceMeters = preferences.MaxGreenDistanceMeters,
            MaxBlueDistanceMeters = preferences.MaxBlueDistanceMeters,
            MaxRedDistanceMeters = preferences.MaxRedDistanceMeters,
            MaxHighTierDistanceMeters = preferences.MaxHighTierDistanceMeters,
            MaxSafeZoneDistanceMeters = preferences.MaxSafeZoneDistanceMeters,
            MinSpawnBeachDistanceMeters = preferences.MinSpawnBeachDistanceMeters,
            IncludeRiskySpots = preferences.IncludeRiskySpots,
            AvoidHighTrafficPuzzleMonuments = preferences.AvoidHighTrafficPuzzleMonuments,
            PreferRecyclerRoute = preferences.PreferRecyclerRoute,
            ResultCount = preferences.ResultCount
        };
    }

    public static string ToWireName(BuildSpotMode mode) => mode switch
    {
        BuildSpotMode.Auto => "auto",
        BuildSpotMode.SoloStealth => "solo_stealth",
        BuildSpotMode.DuoBalanced => "duo_balanced",
        BuildSpotMode.StarterBase => "starter_base",
        BuildSpotMode.FarmBase => "farm_base",
        BuildSpotMode.MonumentRush => "monument_rush",
        BuildSpotMode.ClanHighTraffic => "clan_high_traffic",
        BuildSpotMode.LowPopHidden => "low_pop_hidden",
        _ => "duo_balanced"
    };
}

public sealed class BuildCandidateGenerator : IBuildCandidateGenerator
{
    public IReadOnlyList<BuildCandidate> GenerateCandidates(RustMapData map, BuildSpotPreferences preferences)
    {
        if (map.MapSize <= 0) return Array.Empty<BuildCandidate>();

        var step = map.MapSize >= 4500 ? 300 : 250;
        var radius = preferences.Mode switch
        {
            BuildSpotMode.ClanHighTraffic => 55,
            BuildSpotMode.FarmBase => 45,
            BuildSpotMode.StarterBase => 25,
            _ => 35
        };

        var candidates = new List<BuildCandidate>();
        var margin = Math.Max(180, radius * 3);
        for (double x = margin; x <= map.MapSize - margin; x += step)
        {
            for (double z = margin; z <= map.MapSize - margin; z += step)
            {
                if (preferences.MinSpawnBeachDistanceMeters is { } minBeach)
                {
                    var beachDistance = Math.Min(Math.Min(x, z), Math.Min(map.MapSize - x, map.MapSize - z));
                    if (beachDistance < minBeach) continue;
                }

                var nearest = BuildSpotMath.NearestMonuments(new WorldPosition(x, z), map.Monuments, 1).FirstOrDefault();
                if (nearest.monument != null && nearest.distance < 185) continue;

                var terrain = EstimateTerrain(map.MapSize, x, z, preferences);
                if (terrain.IsBuildBlocked) continue;

                candidates.Add(new BuildCandidate
                {
                    CandidateId = $"{Math.Round(x)}:{Math.Round(z)}",
                    WorldPosition = new WorldPosition(x, z),
                    Grid = BuildSpotMath.ToGrid(x, z, map.MapSize),
                    RadiusMeters = radius,
                    Terrain = terrain,
                    BuildablePatchMeters = Math.Clamp(radius * 2.4 - terrain.MaxHeightDeltaMeters * 5, 20, 150)
                });
            }
        }

        return candidates;
    }

    private static TerrainSummary EstimateTerrain(int mapSize, double x, double z, BuildSpotPreferences preferences)
    {
        var nx = x / mapSize;
        var nz = z / mapSize;
        var slope = Math.Abs(Math.Sin(nx * 19.7) + Math.Cos(nz * 17.3)) * 3.2 + Math.Abs(Math.Sin((nx + nz) * 31.0)) * 2.1;
        var heightDelta = slope * 0.42 + Math.Abs(Math.Cos(nx * 11.0 - nz * 7.0)) * 1.4;
        var biome = z > mapSize * 0.66 ? "snow" : x < mapSize * 0.33 ? "forest" : z < mapSize * 0.25 ? "desert" : "temperate";
        var maxSlope = preferences.FlatnessRequirement switch
        {
            FlatnessRequirement.VeryStrict => 3.0,
            FlatnessRequirement.Strict => 5.0,
            _ => 8.0
        };
        var maxDelta = preferences.FlatnessRequirement switch
        {
            FlatnessRequirement.VeryStrict => 1.5,
            FlatnessRequirement.Strict => 3.0,
            _ => 5.0
        };

        return new TerrainSummary
        {
            AverageSlopeDegrees = Math.Round(slope, 1),
            MaxHeightDeltaMeters = Math.Round(heightDelta, 1),
            Biome = biome,
            TopologyFlags = biome == "forest" ? new[] { "Field", "Forest" } : new[] { "Field" },
            IsBuildBlocked = slope > maxSlope || heightDelta > maxDelta
        };
    }
}

public sealed class BuildSpotScorer : IBuildSpotScorer
{
    private readonly PuzzleAccessScorer _puzzleAccessScorer;
    public PuzzleAccessScore? LastPuzzleScore { get; private set; }

    public BuildSpotScorer(PuzzleAccessScorer puzzleAccessScorer)
    {
        _puzzleAccessScorer = puzzleAccessScorer;
    }

    public BuildSpotScores Score(BuildCandidate candidate, RustMapData map, BuildSpotPreferences preferences, OptionalAppSignals appSignals)
    {
        var nearest = BuildSpotMath.NearestMonuments(candidate.WorldPosition, map.Monuments, 6).ToArray();
        var terrain = new TerrainScorer().Score(candidate, preferences);
        var loot = new MonumentAccessScorer().Score(candidate, nearest, preferences);
        var resource = new ResourceAccessScorer().Score(candidate, nearest, preferences);
        var traffic = new TrafficRiskEstimator().Score(candidate, map, nearest, preferences);
        var raid = new TrafficRiskEstimator().RaidRisk(candidate, nearest, traffic, preferences);
        var mobility = new MobilityScorer().Score(candidate, map, nearest, preferences);
        var preference = new MonumentAccessScorer().PreferenceScore(nearest, preferences);
        LastPuzzleScore = _puzzleAccessScorer.ScorePuzzleAccess(candidate, map, preferences);
        var safety = Math.Clamp(100 - traffic * 0.68 - raid * 0.32 + (candidate.Terrain.Biome == "forest" ? 8 : 0), 0, 100);

        return new BuildSpotScores
        {
            Flatness = terrain.flatness,
            BuildableArea = terrain.buildable,
            LootAccess = loot,
            ResourceAccess = resource,
            Safety = Math.Round(safety),
            Mobility = mobility,
            PuzzleAccess = LastPuzzleScore.OverallPuzzleScore,
            MonumentPreference = preference,
            TrafficRisk = traffic,
            RaidRisk = raid
        };
    }

    public static double CalculateOverall(BuildSpotScores s, BuildSpotMode mode)
    {
        var w = ModeWeights.For(mode);
        var positive = w.Flatness * s.Flatness + w.BuildableArea * s.BuildableArea + w.LootAccess * s.LootAccess +
                       w.ResourceAccess * s.ResourceAccess + w.Safety * s.Safety + w.Mobility * s.Mobility +
                       w.PuzzleAccess * s.PuzzleAccess + w.MonumentPreference * s.MonumentPreference;
        var risk = w.TrafficRisk * s.TrafficRisk + w.RaidRisk * s.RaidRisk;
        return Math.Clamp((positive - risk + 40) / 1.35, 0, 100);
    }
}

public sealed class TerrainScorer
{
    public (double flatness, double buildable) Score(BuildCandidate candidate, BuildSpotPreferences preferences)
    {
        var clutterPenalty = candidate.Terrain.TopologyFlags.Contains("Forest") ? 4 : 0;
        var flatness = 100 - Math.Clamp(candidate.Terrain.AverageSlopeDegrees * 8, 0, 45) - Math.Clamp(candidate.Terrain.MaxHeightDeltaMeters * 6, 0, 35) - clutterPenalty;
        var area = Math.Clamp(candidate.BuildablePatchMeters / (preferences.Mode == BuildSpotMode.ClanHighTraffic ? 115 : 85) * 100, 0, 100);
        return (Math.Round(Math.Clamp(flatness, 0, 100)), Math.Round(area));
    }
}

public sealed class MonumentAccessScorer
{
    public double Score(BuildCandidate candidate, IReadOnlyList<(MapMonument monument, double distance)> nearest, BuildSpotPreferences preferences)
    {
        var score = 0.0;
        foreach (var item in nearest.Take(5))
        {
            var distanceScore = BuildSpotMath.DistanceBandScore(item.distance, preferences.Mode, item.monument);
            var trafficAdjustedLoot = item.monument.LootValue * (1 - item.monument.TrafficRisk / 180.0);
            score += distanceScore * trafficAdjustedLoot / 100.0;
            if (preferences.PreferRecyclerRoute && item.monument.HasRecycler && item.distance < 1400) score += 9;
        }
        return Math.Round(Math.Clamp(score, 0, 100));
    }

    public double PreferenceScore(IReadOnlyList<(MapMonument monument, double distance)> nearest, BuildSpotPreferences preferences)
    {
        var score = 70.0;
        foreach (var item in nearest)
        {
            if (preferences.PreferredMonuments.Any(p => BuildSpotMath.NameMatches(item.monument, p))) score += Math.Max(0, 24 - item.distance / 90);
            if (preferences.AvoidMonuments.Any(p => BuildSpotMath.NameMatches(item.monument, p))) score -= Math.Max(0, 35 - item.distance / 70);
        }
        return Math.Round(Math.Clamp(score, 0, 100));
    }
}

public sealed class PuzzleAccessScorer : IPuzzleAccessScorer
{
    public PuzzleAccessScore ScorePuzzleAccess(BuildCandidate candidate, RustMapData map, BuildSpotPreferences preferences)
    {
        if (preferences.PuzzlePreference == PuzzlePreference.Ignore)
            return new PuzzleAccessScore();

        var green = BestRoute(candidate, map.Monuments.Where(m => m.Puzzle.IsGreenCardSource), preferences.MaxGreenDistanceMeters);
        var blue = BestRoute(candidate, map.Monuments.Where(m => m.Puzzle.IsBlueCardSource), preferences.MaxBlueDistanceMeters);
        var red = BestRoute(candidate, map.Monuments.Where(m => m.Puzzle.IsRedCardSource), preferences.MaxRedDistanceMeters);
        var high = BestRoute(candidate, map.Monuments.Where(m => m.Tier.Contains("high", StringComparison.OrdinalIgnoreCase) || m.Puzzle.RequiresRedCard), preferences.MaxHighTierDistanceMeters);
        var chain = ChainScore(green, blue, red, high);
        var weights = PuzzleWeights.For(preferences.Mode, preferences.PuzzlePreference);
        var overall = green.Score * weights.green + blue.Score * weights.blue + red.Score * weights.red + chain * weights.chain;
        var weightTotal = weights.green + weights.blue + weights.red + weights.chain;

        return new PuzzleAccessScore
        {
            GreenCardAccess = Math.Round(green.Score),
            BlueCardAccess = Math.Round(blue.Score),
            RedCardAccess = Math.Round(red.Score),
            ProgressionChainScore = Math.Round(chain),
            OverallPuzzleScore = Math.Round(Math.Clamp(overall / Math.Max(0.01, weightTotal), 0, 100)),
            BestGreenRoute = green.Name.Length == 0 ? null : green,
            BestBlueRoute = blue.Name.Length == 0 ? null : blue,
            BestRedRoute = red.Name.Length == 0 ? null : red,
            BestProgressionChain = new PuzzleProgressionChain
            {
                BestGreenSource = green.Name.Length == 0 ? null : green,
                BestBlueSource = blue.Name.Length == 0 ? null : blue,
                BestRedSource = red.Name.Length == 0 ? null : red,
                BestHighTierTarget = high.Name.Length == 0 ? null : high,
                ChainScore = Math.Round(chain),
                ChainSummary = chain >= 75 ? "Strong card loop with practical green, blue, and red progression." : chain >= 45 ? "Partial card loop; useful progression exists but one step is slower." : "Limited card progression from this position."
            }
        };
    }

    private static PuzzleRoute BestRoute(BuildCandidate candidate, IEnumerable<MapMonument> monuments, double targetDistance)
    {
        return monuments.Select(m =>
            {
                var d = BuildSpotMath.Distance(candidate.WorldPosition, m.Position);
                var distanceScore = Math.Clamp(100 - Math.Abs(d - targetDistance * 0.65) / Math.Max(1, targetDistance) * 90, 0, 100);
                var riskPenalty = m.TrafficRisk * 0.22;
                var recyclerBonus = m.HasRecycler ? 6 : 0;
                return new PuzzleRoute { Name = m.Name, DistanceMeters = Math.Round(d), Score = Math.Round(Math.Clamp(distanceScore + recyclerBonus - riskPenalty, 0, 100)) };
            })
            .OrderByDescending(r => r.Score)
            .FirstOrDefault() ?? new PuzzleRoute();
    }

    private static double ChainScore(PuzzleRoute green, PuzzleRoute blue, PuzzleRoute red, PuzzleRoute high)
    {
        var available = new[] { green.Score, blue.Score, red.Score, high.Score }.Where(s => s > 0).ToArray();
        if (available.Length == 0) return 0;
        var completeness = available.Length / 4.0;
        return Math.Clamp(available.Average() * (0.55 + completeness * 0.45), 0, 100);
    }
}

public sealed class ResourceAccessScorer
{
    public double Score(BuildCandidate candidate, IReadOnlyList<(MapMonument monument, double distance)> nearest, BuildSpotPreferences preferences)
    {
        var biome = candidate.Terrain.Biome;
        var baseScore = preferences.ResourcePreference switch
        {
            ResourcePreference.Wood => biome == "forest" ? 92 : biome == "temperate" ? 70 : 42,
            ResourcePreference.Stone => biome is "snow" or "temperate" ? 82 : 62,
            ResourcePreference.Metal => biome == "snow" ? 88 : 68,
            ResourcePreference.Sulfur => biome == "snow" ? 90 : biome == "desert" ? 78 : 58,
            _ => biome == "temperate" ? 78 : 72
        };
        var hotPenalty = nearest.Take(2).Sum(x => Math.Max(0, (900 - x.distance) / 900) * x.monument.TrafficRisk * 0.08);
        return Math.Round(Math.Clamp(baseScore - hotPenalty, 0, 100));
    }
}

public sealed class TrafficRiskEstimator
{
    public double Score(BuildCandidate candidate, RustMapData map, IReadOnlyList<(MapMonument monument, double distance)> nearest, BuildSpotPreferences preferences)
    {
        var edgeDistance = Math.Min(Math.Min(candidate.WorldPosition.X, candidate.WorldPosition.Z), Math.Min(map.MapSize - candidate.WorldPosition.X, map.MapSize - candidate.WorldPosition.Z));
        var spawnRouteRisk = Math.Max(0, 500 - edgeDistance) / 500 * 25;
        var monumentRisk = nearest.Take(4).Sum(x => Math.Max(0, 1300 - x.distance) / 1300 * x.monument.TrafficRisk * 0.22);
        var openTerrainPenalty = candidate.Terrain.Biome == "desert" ? 12 : 0;
        var concealmentBonus = candidate.Terrain.Biome == "forest" ? 10 : 0;
        return Math.Round(Math.Clamp(spawnRouteRisk + monumentRisk + openTerrainPenalty - concealmentBonus, 0, 100));
    }

    public double RaidRisk(BuildCandidate candidate, IReadOnlyList<(MapMonument monument, double distance)> nearest, double trafficRisk, BuildSpotPreferences preferences)
    {
        var sulfurAttention = preferences.ResourcePreference == ResourcePreference.Sulfur ? 12 : 0;
        var patchVisibility = candidate.BuildablePatchMeters > 90 ? 8 : 0;
        var highTierPull = nearest.Take(3).Where(x => x.monument.Tier.Contains("high", StringComparison.OrdinalIgnoreCase)).Sum(x => Math.Max(0, 1100 - x.distance) / 1100 * 18);
        return Math.Round(Math.Clamp(trafficRisk * 0.55 + sulfurAttention + patchVisibility + highTierPull - (candidate.Terrain.Biome == "forest" ? 7 : 0), 0, 100));
    }
}

public sealed class MobilityScorer
{
    public double Score(BuildCandidate candidate, RustMapData map, IReadOnlyList<(MapMonument monument, double distance)> nearest, BuildSpotPreferences preferences)
    {
        var recycler = nearest.Where(x => x.monument.HasRecycler).Select(x => x.distance).DefaultIfEmpty(2500).Min();
        var monumentAccess = nearest.Select(x => x.distance).DefaultIfEmpty(2500).Min();
        var target = preferences.Mode is BuildSpotMode.MonumentRush or BuildSpotMode.ClanHighTraffic ? 500 : 900;
        var score = 100 - Math.Abs(monumentAccess - target) / 18;
        if (preferences.PreferRecyclerRoute) score += Math.Max(0, 20 - recycler / 80);
        return Math.Round(Math.Clamp(score, 0, 100));
    }
}

public sealed class BuildSpotExplanationService
{
    public string Explain(BuildCandidate candidate, BuildSpotScores scores, PuzzleAccessScore puzzle, BuildSpotPreferences preferences, IReadOnlyList<NearbyMonument> nearby)
    {
        var mode = BuildSpotPreferencesExtensions.ToWireName(preferences.Mode).Replace('_', ' ');
        var nearest = nearby.FirstOrDefault()?.Name ?? "nearby monuments";
        var puzzleText = puzzle.OverallPuzzleScore >= 70 ? "strong card progression" : puzzle.OverallPuzzleScore >= 40 ? "workable but incomplete card progression" : "limited card progression";
        var riskText = scores.TrafficRisk >= 65 ? "high static traffic-risk proxies" : scores.TrafficRisk >= 35 ? "moderate static traffic-risk proxies" : "low static traffic-risk proxies";
        return $"Fits {mode} with {scores.Flatness:0}/100 flatness, {scores.BuildableArea:0}/100 build room, {puzzleText}, and {riskText}. Nearest useful landmark: {nearest}. This uses static map and monument data only; it does not claim live enemy or hidden-base locations.";
    }

    public IEnumerable<string> BuildPros(BuildSpotScores scores, PuzzleAccessScore puzzle, IReadOnlyList<NearbyMonument> nearby, BuildSpotMode mode)
    {
        if (scores.Flatness >= 80) yield return "Large, flat buildable patch";
        if (scores.LootAccess >= 70) yield return "Good low-to-mid tier loot access";
        if (scores.ResourceAccess >= 75) yield return "Strong resource support for the selected preference";
        if (puzzle.GreenCardAccess >= 60 || puzzle.BlueCardAccess >= 60) yield return "Useful green/blue card progression";
        if (puzzle.RedCardAccess >= 60 && mode is BuildSpotMode.MonumentRush or BuildSpotMode.ClanHighTraffic or BuildSpotMode.DuoBalanced) yield return "Red-card progression is available without relying on live activity data";
        if (scores.TrafficRisk <= 35) yield return "Lower expected traffic from static map proxies";
        if (!nearby.Any()) yield return "Quiet area with no close monument cluster";
    }

    public IEnumerable<string> BuildCons(BuildSpotScores scores, PuzzleAccessScore puzzle, IReadOnlyList<NearbyMonument> nearby, BuildSpotMode mode)
    {
        if (scores.TrafficRisk >= 60) yield return "Likely higher traffic because of nearby static objectives/routes";
        if (scores.RaidRisk >= 55) yield return "Higher raid attention risk from visibility, resources, or high-value proximity";
        if (scores.Flatness < 65) yield return "Terrain may require a smaller or more careful footprint";
        if (puzzle.OverallPuzzleScore < 40) yield return "Puzzle-card progression is weak from this location";
        if (mode == BuildSpotMode.SoloStealth && nearby.Any(n => n.Role.Contains("red", StringComparison.OrdinalIgnoreCase))) yield return "May be hotter than ideal for solo stealth due to red-card progression nearby";
    }
}

public static class BuildSpotMath
{
    public static double Distance(WorldPosition a, WorldPosition b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    public static IReadOnlyList<(MapMonument monument, double distance)> NearestMonuments(WorldPosition position, IReadOnlyList<MapMonument> monuments, int count)
        => monuments.Select(m => (m, Distance(position, m.Position))).OrderBy(x => x.Item2).Take(count).ToArray();

    public static string ToGrid(double x, double z, int mapSize)
    {
        if (mapSize <= 0) return "?";
        var cells = Math.Max(1, (int)Math.Round(mapSize / 150.0));
        var cell = mapSize / (double)cells;
        var col = Math.Clamp((int)Math.Floor(x / cell), 0, cells - 1);
        var row = Math.Clamp((int)Math.Floor((mapSize - z) / cell), 0, cells - 1);
        return $"{ColumnLabel(col)}{row}";
    }

    public static double DistanceBandScore(double distance, BuildSpotMode mode, MapMonument monument)
    {
        var key = BuildSpotPreferencesExtensions.ToWireName(mode);
        var band = monument.IdealDistanceByMode.TryGetValue(key, out var explicitBand) && explicitBand.Length >= 2
            ? explicitBand
            : DefaultBand(mode, monument);
        if (distance >= band[0] && distance <= band[1]) return 100;
        var outside = distance < band[0] ? band[0] - distance : distance - band[1];
        return Math.Clamp(100 - outside / 8, 0, 100);
    }

    public static bool NameMatches(MapMonument monument, string name)
        => monument.Name.Contains(name, StringComparison.OrdinalIgnoreCase) || monument.Aliases.Any(a => a.Contains(name, StringComparison.OrdinalIgnoreCase) || name.Contains(a, StringComparison.OrdinalIgnoreCase));

    public static string MonumentRole(MapMonument monument)
    {
        if (monument.Puzzle.IsGreenCardSource) return "green access";
        if (monument.Puzzle.IsBlueCardSource) return monument.HasRecycler ? "blue access / recycler" : "blue access";
        if (monument.Puzzle.IsRedCardSource) return "red progression";
        if (monument.HasRecycler) return "recycler";
        return monument.Tier;
    }

    private static double[] DefaultBand(BuildSpotMode mode, MapMonument monument)
    {
        var high = monument.Tier.Contains("high", StringComparison.OrdinalIgnoreCase) || monument.TrafficRisk >= 75;
        return mode switch
        {
            BuildSpotMode.SoloStealth => high ? new[] { 950d, 1800d } : new[] { 650d, 1250d },
            BuildSpotMode.DuoBalanced => high ? new[] { 900d, 1700d } : new[] { 400d, 950d },
            BuildSpotMode.StarterBase => high ? new[] { 1200d, 2200d } : new[] { 450d, 1200d },
            BuildSpotMode.FarmBase => new[] { 900d, 1900d },
            BuildSpotMode.MonumentRush => new[] { 180d, 700d },
            BuildSpotMode.ClanHighTraffic => new[] { 250d, 900d },
            BuildSpotMode.LowPopHidden => new[] { 1400d, 2400d },
            _ => new[] { 500d, 1200d }
        };
    }

    private static string ColumnLabel(int index)
    {
        var s = "";
        index++;
        while (index > 0)
        {
            index--;
            s = (char)('A' + (index % 26)) + s;
            index /= 26;
        }
        return s;
    }
}

internal readonly record struct ModeWeights(double Flatness, double BuildableArea, double LootAccess, double ResourceAccess, double Safety, double Mobility, double PuzzleAccess, double MonumentPreference, double TrafficRisk, double RaidRisk)
{
    public static ModeWeights For(BuildSpotMode mode) => mode switch
    {
        BuildSpotMode.SoloStealth => new(.18, .13, .13, .14, .28, .05, .09, .05, .18, .25),
        BuildSpotMode.DuoBalanced => new(.16, .14, .16, .13, .20, .08, .13, .07, .16, .20),
        BuildSpotMode.StarterBase => new(.20, .15, .18, .14, .23, .04, .06, .05, .14, .22),
        BuildSpotMode.FarmBase => new(.18, .16, .08, .28, .20, .04, .06, .04, .15, .22),
        BuildSpotMode.MonumentRush => new(.10, .10, .20, .08, .08, .14, .25, .12, .10, .15),
        BuildSpotMode.ClanHighTraffic => new(.12, .22, .16, .12, .08, .12, .18, .12, .08, .12),
        BuildSpotMode.LowPopHidden => new(.18, .12, .10, .14, .32, .04, .06, .04, .22, .25),
        _ => new(.16, .14, .16, .13, .20, .08, .13, .07, .16, .20)
    };
}

internal static class PuzzleWeights
{
    public static (double green, double blue, double red, double chain) For(BuildSpotMode mode, PuzzlePreference preference)
    {
        if (preference == PuzzlePreference.PreferGreen) return (3, 1, .4, .6);
        if (preference == PuzzlePreference.PreferBlue) return (1, 3, 1, 1.4);
        if (preference == PuzzlePreference.PreferRed) return (.8, 1.8, 3.2, 2.2);
        if (preference == PuzzlePreference.PreferFullProgression) return (1.2, 1.8, 2.2, 3.5);
        return mode switch
        {
            BuildSpotMode.SoloStealth => (2.5, 1.3, .45, .7),
            BuildSpotMode.DuoBalanced => (1.4, 2.5, 1.4, 2.3),
            BuildSpotMode.StarterBase => (3, .8, .25, .5),
            BuildSpotMode.FarmBase => (.8, .55, .25, .35),
            BuildSpotMode.MonumentRush => (1.2, 2.4, 3.4, 3.0),
            BuildSpotMode.ClanHighTraffic => (.7, 1.4, 2.7, 2.5),
            BuildSpotMode.LowPopHidden => (1.6, .9, .35, .5),
            _ => (1.4, 2.5, 1.4, 2.3)
        };
    }
}

public static class MonumentPuzzleMetadataLoader
{
    public static IReadOnlyList<MapMonument> MergeMetadata(IReadOnlyList<MapMonument> mapMonuments, string metadataPath)
    {
        if (!File.Exists(metadataPath)) return mapMonuments;
        var json = File.ReadAllText(metadataPath);
        var document = JsonSerializer.Deserialize<MetadataDocument>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (document?.Monuments == null) return mapMonuments;

        return mapMonuments.Select(m =>
        {
            var metadata = document.Monuments.FirstOrDefault(md =>
                m.Name.Contains(md.Name, StringComparison.OrdinalIgnoreCase) ||
                md.Aliases.Any(a => m.Name.Contains(a, StringComparison.OrdinalIgnoreCase)));
            if (metadata == null) return m;
            return new MapMonument
            {
                Name = string.IsNullOrWhiteSpace(metadata.Name) ? m.Name : metadata.Name,
                Aliases = metadata.Aliases,
                Tier = metadata.Tier,
                Position = m.Position,
                HasRecycler = metadata.HasRecycler,
                HasRefinery = metadata.HasRefinery,
                HasResearchTable = metadata.HasResearchTable,
                LootValue = metadata.LootValue,
                TrafficRisk = metadata.TrafficRisk,
                Puzzle = metadata.Puzzle,
                IdealDistanceByMode = metadata.IdealDistanceByMode
            };
        }).ToArray();
    }

    private sealed class MetadataDocument
    {
        public IReadOnlyList<MapMonument> Monuments { get; init; } = Array.Empty<MapMonument>();
    }
}

public sealed class ActiveMapResolverService
{
    public RustMapData? ResolveFromCurrentMap(string serverId, string serverName, int mapSize, IReadOnlyList<MapMonument> monuments, int? mapSeed = null, string? wipeId = null)
    {
        if (mapSize <= 0) return null;
        return new RustMapData
        {
            ServerId = serverId,
            ServerName = serverName,
            MapSeed = mapSeed,
            MapSize = mapSize,
            WipeId = wipeId,
            Monuments = monuments
        };
    }

    public static string BuildCacheKey(RustMapData map, string puzzleMetadataVersion = "local", string scoringVersion = BuildSpotAdvisorService.ScoringVersion)
        => $"{map.ServerId}:{map.WipeId}:{map.MapSeed}:{map.MapSize}:{scoringVersion}:{puzzleMetadataVersion}";
}

public sealed class MapProviderClient : IMapProviderClient
{
    public Task<RustMapData?> FindMapAsync(int seed, int size, CancellationToken ct)
    {
        // MVP abstraction point: wire this to RustMaps or a compatible structured provider when API credentials/config are available.
        return Task.FromResult<RustMapData?>(null);
    }
}
