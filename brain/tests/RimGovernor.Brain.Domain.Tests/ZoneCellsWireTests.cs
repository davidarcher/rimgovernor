using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Contracts.Wire;
using Xunit;
using C = RimGovernor.Contracts.Wire.Common;
using Mirror = RimGovernor.Contracts.Wire.Mirror;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>The zone census and the cell read are whole on the wire: each round-trips exactly and a missing member does not decode.</summary>
public sealed class ZoneCellsWireTests
{
    private static readonly C.ObservationContext Context = new() { Identity = new C.Identity { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 7 };

    private static readonly O.ZonesSnapshot Census = new()
    {
        Context = Context,
        Zones =
        [
            ZoneFixtures.Growing("Zone_1", "Plant_Rice", usableCells: 12, edible: true) with { PlantedCells = 4, FertilePlantedCells = 3, BlightedPlants = 1, GrowthTotal = 1.5, FertilityFactorTotal = 3.2 },
            ZoneFixtures.Stockpile("Zone_2", "Important", foodStorage: true) with
            {
                Filter = new O.StockpileFilter { AllowedDefNames = ["MealSimple"], HitPointsMin = 0, HitPointsMax = 1, QualityMin = "Awful", QualityMax = "Legendary" },
            },
            new O.ZoneState.Other { Id = "Zone_3", Label = "Fishing zone 1", Bounds = ZoneFixtures.Origin },
        ],
    };

    private static O.CellsSnapshot Cells() => new()
    {
        Context = Context,
        MapSize = new O.MapSize { Width = 250, Height = 250 },
        Grid = new Mirror.CellGrid
        {
            X = 3, Z = 4, Width = 1, Height = 1,
            Held = [true], Walkable = [true], Indoors = [false], Doorway = [false], Polluted = [false], InHome = [true],
            TopLayerRemovable = [false], FoundationRemoval = [false], StorageStacks = [0], StorageCapacity = [1],
            Fertility = [1f], Glow = [0f], SnowDepth = [0f], Terrain = ["Soil"], BaseTerrain = ["Soil"],
            Roof = [null], ZoneId = ["Zone_1"], Room = [null], Things = [null],
        },
    };

    [Fact]
    public void TheZoneCensusRoundTripsExactly()
    {
        var json = WireJson.Serialize(Census);
        var back = WireJson.Deserialize<O.ZonesSnapshot>(json);

        Assert.Equal(json, WireJson.Serialize(back));
        Assert.Equal(Census.Zones, back.Zones);
        Assert.Equal(["growing", "stockpile", "other"], JsonNode.Parse(json)!["zones"]!.AsArray().Select(z => (string?)z!["type"]));
    }

    [Theory]
    [InlineData(0, "crop")]
    [InlineData(0, "edibleCrop")]
    [InlineData(0, "temperature")]
    [InlineData(1, "priority")]
    [InlineData(1, "foodStorage")]
    [InlineData(2, "bounds")]
    public void AZoneMissingAMemberDoesNotDecode(int row, string member)
    {
        var node = JsonNode.Parse(WireJson.Serialize(Census))!.AsObject();
        Assert.True(node["zones"]![row]!.AsObject().Remove(member));

        Assert.ThrowsAny<JsonException>(() => WireJson.Deserialize<O.ZonesSnapshot>(node.ToJsonString()));
    }

    [Fact]
    public void TheCellReadRoundTripsExactly()
    {
        var json = WireJson.Serialize(Cells());

        Assert.Equal(json, WireJson.Serialize(WireJson.Deserialize<O.CellsSnapshot>(json)));
    }

    [Theory]
    [InlineData("mapSize")]
    [InlineData("grid")]
    public void ACellReadMissingAMemberDoesNotDecode(string member)
    {
        var node = JsonNode.Parse(WireJson.Serialize(Cells()))!.AsObject();
        Assert.True(node.Remove(member));

        Assert.ThrowsAny<JsonException>(() => WireJson.Deserialize<O.CellsSnapshot>(node.ToJsonString()));
    }

    [Fact]
    public void AGridMissingAnArrayDoesNotDecode()
    {
        var node = JsonNode.Parse(WireJson.Serialize(Cells()))!.AsObject();
        Assert.True(node["grid"]!.AsObject().Remove("walkable"));

        Assert.ThrowsAny<JsonException>(() => WireJson.Deserialize<O.CellsSnapshot>(node.ToJsonString()));
    }
}
