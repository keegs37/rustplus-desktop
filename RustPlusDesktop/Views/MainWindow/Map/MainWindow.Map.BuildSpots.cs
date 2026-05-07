using RustPlusDesk.Features.BuildSpots.Models;
using RustPlusDesk.Features.BuildSpots.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using IOPath = System.IO.Path;

namespace RustPlusDesk.Views;

public partial class MainWindow
{
    private readonly List<FrameworkElement> _buildSpotEls = new();
    private CancellationTokenSource? _buildSpotCts;

    private void BtnToggleBuildSpots_Click(object sender, RoutedEventArgs e)
    {
        BuildSpotAdvisorPanel.Visibility = BuildSpotAdvisorPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void BtnCloseBuildSpots_Click(object sender, RoutedEventArgs e)
    {
        BuildSpotAdvisorPanel.Visibility = Visibility.Collapsed;
    }

    private async void BtnRunBuildSpotAdvisor_Click(object sender, RoutedEventArgs e)
    {
        _buildSpotCts?.Cancel();
        _buildSpotCts = new CancellationTokenSource();

        try
        {
            if (_worldSizeS <= 0 || _monData.Count == 0)
            {
                BuildSpotStatusText.Text = "I could not identify the active map automatically. Connect to a Rust+ server map first; manual seed/size fallback can be wired to the map-provider client later.";
                return;
            }

            BtnRunBuildSpotAdvisor.IsEnabled = false;
            BuildSpotStatusText.Text = "Generating candidates and sampling provider terrain/topology layers when available...";
            BuildSpotResultsList.ItemsSource = null;
            ClearBuildSpotMarkers();

            var preferences = ReadBuildSpotPreferences();
            var map = BuildCurrentRustMapData();
            var service = new BuildSpotAdvisorService(map);
            var recommendations = await service.RecommendAsync(preferences, _buildSpotCts.Token);

            BuildSpotResultsList.ItemsSource = recommendations.Select(BuildSpotResultCard.FromRecommendation).ToArray();
            RenderBuildSpotMarkers(recommendations);
            BuildSpotStatusText.Text = recommendations.Count == 0
                ? "No build spots matched the selected constraints. If terrain layers are missing, try Normal flatness or include risky spots."
                : BuildTerrainStatusText(map, recommendations);
        }
        catch (OperationCanceledException)
        {
            BuildSpotStatusText.Text = "Build spot analysis canceled.";
        }
        catch (Exception ex)
        {
            BuildSpotStatusText.Text = "Build spot analysis failed: " + ex.Message;
            AppendLog("BuildSpotAdvisor error: " + ex);
        }
        finally
        {
            BtnRunBuildSpotAdvisor.IsEnabled = true;
        }
    }

    private BuildSpotPreferences ReadBuildSpotPreferences()
    {
        return new BuildSpotPreferences
        {
            Mode = ReadComboEnum(BuildSpotModeCombo, BuildSpotMode.Auto),
            TeamSize = ReadComboInt(BuildSpotTeamCombo, 2),
            LootPriority = ReadComboEnum(BuildSpotLootCombo, PriorityLevel.Medium),
            SafetyPriority = ReadComboEnum(BuildSpotSafetyCombo, PriorityLevel.Medium),
            FlatnessRequirement = ReadComboEnum(BuildSpotFlatnessCombo, FlatnessRequirement.Normal),
            ResourcePreference = ReadComboEnum(BuildSpotResourceCombo, ResourcePreference.Balanced),
            PuzzlePreference = ReadComboEnum(BuildSpotPuzzleCombo, PuzzlePreference.Auto),
            ResultCount = ReadComboInt(BuildSpotResultsCombo, 10),
            IncludeRiskySpots = BuildSpotRiskyCheck.IsChecked == true,
            PreferRecyclerRoute = BuildSpotRecyclerCheck.IsChecked == true,
            AvoidHighTrafficPuzzleMonuments = BuildSpotAvoidPuzzleTrafficCheck.IsChecked == true
        };
    }

    private RustMapData BuildCurrentRustMapData()
    {
        var monuments = _monData.Select(m => new MapMonument
        {
            Name = Beautify(m.Name),
            Position = new WorldPosition(m.X, m.Y),
            Tier = InferMonumentTier(m.Name),
            HasRecycler = HasLikelyRecycler(m.Name),
            LootValue = InferLootValue(m.Name),
            TrafficRisk = InferTrafficRisk(m.Name),
            Puzzle = InferPuzzle(m.Name)
        }).ToArray();

        var metadataPath = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "Features", "BuildSpots", "Data", "monument_puzzle_metadata.json");
        if (!File.Exists(metadataPath))
            metadataPath = IOPath.Combine(AppContext.BaseDirectory, "monument_puzzle_metadata.json");

        var merged = MonumentPuzzleMetadataLoader.MergeMetadata(monuments, metadataPath);
        return new RustMapData
        {
            ServerId = _rust?.Host ?? "current-server",
            ServerName = _rust?.Host ?? "Current Rust+ server",
            MapSize = _worldSizeS,
            Monuments = merged
        };
    }

    private static string BuildTerrainStatusText(RustMapData map, IReadOnlyList<BuildSpotRecommendation> recommendations)
    {
        if (map.TerrainData?.HasHeightLayer == true && map.TerrainData.HasBlockingLayers)
            return $"Showing top {recommendations.Count} recommendations. Confidence: high for terrain (provider height/topology layers sampled); traffic remains static map-proxy only.";

        var missing = recommendations.SelectMany(r => r.Terrain.MissingLayers).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var missingText = missing.Length == 0 ? "height/topology" : string.Join(", ", missing);
        return $"Showing top {recommendations.Count} recommendations with limited confidence: provider {missingText} data is missing, so full strict terrain/build-blocking confidence is disabled instead of using procedural terrain.";
    }

    private void RenderBuildSpotMarkers(IReadOnlyList<BuildSpotRecommendation> recommendations)
    {
        if (Overlay == null || _worldSizeS <= 0 || _worldRectPx.Width <= 0) return;

        foreach (var recommendation in recommendations)
        {
            var p = WorldToImagePx(recommendation.WorldPosition.X, recommendation.WorldPosition.Z);
            var marker = CreateBuildSpotMarker(recommendation);
            Overlay.Children.Add(marker);
            Panel.SetZIndex(marker, 1050);
            Canvas.SetLeft(marker, p.X - 17);
            Canvas.SetTop(marker, p.Y - 17);
            _buildSpotEls.Add(marker);
            ApplyCurrentOverlayScale(marker);
        }
    }

    private FrameworkElement CreateBuildSpotMarker(BuildSpotRecommendation recommendation)
    {
        var root = new Grid
        {
            Width = 34,
            Height = 34,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = BuildBuildSpotTooltip(recommendation),
            Tag = recommendation
        };
        root.Children.Add(new Ellipse
        {
            Fill = recommendation.Scores.TrafficRisk > 65 ? new SolidColorBrush(Color.FromRgb(255, 151, 74)) : new SolidColorBrush(Color.FromRgb(79, 195, 247)),
            Stroke = Brushes.White,
            StrokeThickness = 2,
            Opacity = 0.92
        });
        root.Children.Add(new TextBlock
        {
            Text = recommendation.Rank.ToString(),
            Foreground = Brushes.Black,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        root.MouseLeftButtonUp += (_, __) =>
        {
            BuildSpotStatusText.Text = $"Selected #{recommendation.Rank} {recommendation.Grid}: {recommendation.AiSummary}";
        };
        return root;
    }

    private string BuildBuildSpotTooltip(BuildSpotRecommendation r)
    {
        var green = r.PuzzleAccess.BestGreenRoute is null ? "n/a" : $"{r.PuzzleAccess.BestGreenRoute.Name} ({r.PuzzleAccess.BestGreenRoute.DistanceMeters:0}m)";
        var blue = r.PuzzleAccess.BestBlueRoute is null ? "n/a" : $"{r.PuzzleAccess.BestBlueRoute.Name} ({r.PuzzleAccess.BestBlueRoute.DistanceMeters:0}m)";
        var red = r.PuzzleAccess.BestRedRoute is null ? "n/a" : $"{r.PuzzleAccess.BestRedRoute.Name} ({r.PuzzleAccess.BestRedRoute.DistanceMeters:0}m)";
        return $"#{r.Rank} — {r.Grid} — {r.ModeFit} — {r.OverallScore:0}/100\n" +
               $"Flatness {r.Scores.Flatness:0} | Buildable {r.Scores.BuildableArea:0} | Loot {r.Scores.LootAccess:0} | Resources {r.Scores.ResourceAccess:0}\n" +
               $"Safety {r.Scores.Safety:0} | Puzzle {r.Scores.PuzzleAccess:0} | Traffic risk {r.Scores.TrafficRisk:0} | Raid risk {r.Scores.RaidRisk:0}\n" +
               $"Green: {green}\nBlue: {blue}\nRed: {red}\n\n{r.AiSummary}";
    }

    private void ClearBuildSpotMarkers()
    {
        if (Overlay != null)
        {
            foreach (var el in _buildSpotEls)
                Overlay.Children.Remove(el);
        }
        _buildSpotEls.Clear();
    }

    private static T ReadComboEnum<T>(ComboBox combo, T fallback) where T : struct
    {
        if (combo.SelectedItem is ComboBoxItem item && item.Tag is string tag && Enum.TryParse<T>(tag, out var value))
            return value;
        return fallback;
    }

    private static int ReadComboInt(ComboBox combo, int fallback)
    {
        if (combo.SelectedItem is ComboBoxItem item && item.Tag is string tag && int.TryParse(tag, out var value))
            return value;
        return fallback;
    }

    private static string InferMonumentTier(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("launch") || n.Contains("military") || n.Contains("oil")) return "high";
        if (n.Contains("train") || n.Contains("airfield") || n.Contains("power") || n.Contains("water") || n.Contains("sewer")) return "medium_high";
        if (n.Contains("harbor") || n.Contains("satellite") || n.Contains("dome")) return "low_medium";
        return "low";
    }

    private static bool HasLikelyRecycler(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("supermarket") || n.Contains("gas") || n.Contains("harbor") || n.Contains("satellite") ||
               n.Contains("sewer") || n.Contains("train") || n.Contains("water") || n.Contains("power") ||
               n.Contains("airfield") || n.Contains("launch") || n.Contains("outpost") || n.Contains("bandit");
    }

    private static double InferLootValue(string name)
    {
        var tier = InferMonumentTier(name);
        return tier switch
        {
            "high" => 92,
            "medium_high" => 78,
            "low_medium" => 55,
            _ => 35
        };
    }

    private static double InferTrafficRisk(string name)
    {
        var tier = InferMonumentTier(name);
        return tier switch
        {
            "high" => 90,
            "medium_high" => 76,
            "low_medium" => 55,
            _ => 45
        };
    }

    private static MonumentPuzzleMetadata InferPuzzle(string name)
    {
        var n = name.ToLowerInvariant();
        return new MonumentPuzzleMetadata
        {
            IsGreenCardSource = n.Contains("gas") || n.Contains("supermarket"),
            IsBlueCardSource = n.Contains("harbor") || n.Contains("satellite") || n.Contains("sewer"),
            IsRedCardSource = n.Contains("train") || n.Contains("airfield") || n.Contains("water") || n.Contains("power"),
            RequiresGreenCard = n.Contains("harbor") || n.Contains("satellite") || n.Contains("sewer"),
            RequiresBlueCard = n.Contains("train") || n.Contains("airfield") || n.Contains("water") || n.Contains("power"),
            RequiresRedCard = n.Contains("launch") || n.Contains("military"),
            EstimatedPuzzleValue = InferLootValue(name)
        };
    }

    private sealed class BuildSpotResultCard
    {
        public string Title { get; init; } = string.Empty;
        public string ScoreLine { get; init; } = string.Empty;
        public string NearbyLine { get; init; } = string.Empty;
        public string Summary { get; init; } = string.Empty;

        public static BuildSpotResultCard FromRecommendation(BuildSpotRecommendation r)
        {
            var nearby = r.NearbyMonuments.Count == 0
                ? "Nearby: none"
                : "Nearby: " + string.Join(" • ", r.NearbyMonuments.Take(3).Select(m => $"{m.Name} {m.DistanceMeters:0}m ({m.Role})"));
            var terrainLine = r.Terrain.DataQuality == "terrain_backed"
                ? $"Terrain: slope {r.Terrain.AverageSlopeDegrees:0.0}° | Δh {r.Terrain.MaxHeightDeltaMeters:0.0}m | biome {r.Terrain.Biome} | topology {string.Join(", ", r.Terrain.TopologyFlags.DefaultIfEmpty("none"))}"
                : $"Terrain: unknown/limited confidence — missing {string.Join(", ", r.Terrain.MissingLayers.DefaultIfEmpty("height/topology"))}";
            return new BuildSpotResultCard
            {
                Title = $"#{r.Rank} — {r.Grid} — {r.ModeFit.Replace('_', ' ')} — {r.OverallScore:0}/100",
                ScoreLine = $"Flat {r.Scores.Flatness:0} | Build {r.Scores.BuildableArea:0} | Loot {r.Scores.LootAccess:0} | Res {r.Scores.ResourceAccess:0} | Safety {r.Scores.Safety:0} | Puzzle {r.Scores.PuzzleAccess:0} | Traffic {r.Scores.TrafficRisk:0} | Raid {r.Scores.RaidRisk:0} | {terrainLine}",
                NearbyLine = nearby,
                Summary = r.AiSummary
            };
        }
    }
}
