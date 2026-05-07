using System.Linq;
using RustPlusDesk.Features.BuildSpots.Models;
using RustPlusDesk.Features.BuildSpots.Services;
using Xunit;

namespace RustPlusDesktop.Tests.Features.BuildSpots;

public sealed class MapRasterDecoderTests
{
    [Fact]
    public void WorldToPixel_ConvertsWorldCoordinatesToRasterPixels()
    {
        var decoder = new MapRasterDecoder();
        var map = CreateMap(heightMap: FlatHeights(4, 4, 10));

        var topLeft = decoder.WorldToPixel(map, 0, 1000);
        var bottomRight = decoder.WorldToPixel(map, 1000, 0);

        Assert.Equal((0, 0), topLeft);
        Assert.Equal((3, 3), bottomRight);
    }

    [Fact]
    public void AnalyzeCandidate_ComputesHeightDeltaAndSlopeFromHeightMap()
    {
        var decoder = new MapRasterDecoder();
        var map = CreateMap(heightMap: new HeightMapRaster
        {
            Width = 5,
            Height = 5,
            HeightMeters = new double[]
            {
                0, 10, 20, 30, 40,
                0, 10, 20, 30, 40,
                0, 10, 20, 30, 40,
                0, 10, 20, 30, 40,
                0, 10, 20, 30, 40
            }
        });

        var terrain = decoder.AnalyzeCandidate(map, new WorldPosition(500, 500), 250, new BuildSpotPreferences());

        Assert.True(terrain.HasHeightData);
        Assert.True(terrain.AverageSlopeDegrees > 0);
        Assert.True(terrain.MaxHeightDeltaMeters >= 20);
        Assert.Equal("terrain_partial", terrain.DataQuality);
    }

    [Theory]
    [InlineData(KnownTopologyFlags.Ocean, "Ocean")]
    [InlineData(KnownTopologyFlags.DeepWater, "DeepWater")]
    [InlineData(KnownTopologyFlags.River, "River")]
    [InlineData(KnownTopologyFlags.Lake, "Lake")]
    [InlineData(KnownTopologyFlags.Road, "Road")]
    [InlineData(KnownTopologyFlags.Rail, "Rail")]
    [InlineData(KnownTopologyFlags.NoBuild, "NoBuild")]
    [InlineData(KnownTopologyFlags.MonumentBlocked, "MonumentBlocked")]
    public void AnalyzeCandidate_HardRejectsBlockingTopology(KnownTopologyFlags blockingFlag, string flagName)
    {
        var decoder = new MapRasterDecoder();
        var map = CreateMap(
            heightMap: FlatHeights(3, 3, 10),
            topology: new TopologyRaster
            {
                Width = 3,
                Height = 3,
                Flags = Enumerable.Repeat((int)blockingFlag, 9).ToArray()
            });

        var terrain = decoder.AnalyzeCandidate(map, new WorldPosition(500, 500), 100, new BuildSpotPreferences());

        Assert.True(terrain.IsBuildBlocked);
        Assert.Contains(flagName, terrain.TopologyFlags);
    }

    [Fact]
    public void GenerateCandidates_MissingTerrainLayersProducesLowConfidenceRatherThanFakeTerrain()
    {
        var generator = new BuildCandidateGenerator(new MapRasterDecoder());
        var map = new RustMapData { MapSize = 1000 };

        var candidates = generator.GenerateCandidates(map, new BuildSpotPreferences { MinSpawnBeachDistanceMeters = null });

        Assert.NotEmpty(candidates);
        Assert.All(candidates, candidate =>
        {
            Assert.Equal("terrain_unknown", candidate.Terrain.DataQuality);
            Assert.False(candidate.Terrain.HasHeightData);
            Assert.False(candidate.Terrain.HasBlockingData);
            Assert.Equal("unknown", candidate.Terrain.Biome);
        });
    }

    private static RustMapData CreateMap(HeightMapRaster? heightMap = null, TopologyRaster? topology = null)
        => new()
        {
            MapSize = 1000,
            TerrainData = new RustMapTerrainData
            {
                Metadata = new MapRasterMetadata
                {
                    Width = heightMap?.Width ?? topology?.Width ?? 1,
                    Height = heightMap?.Height ?? topology?.Height ?? 1,
                    WorldMinX = 0,
                    WorldMinZ = 0,
                    WorldMaxX = 1000,
                    WorldMaxZ = 1000,
                    OriginAtTopLeft = true
                },
                HeightMap = heightMap,
                Topology = topology
            }
        };

    private static HeightMapRaster FlatHeights(int width, int height, double meters)
        => new()
        {
            Width = width,
            Height = height,
            HeightMeters = Enumerable.Repeat(meters, width * height).ToArray()
        };
}
