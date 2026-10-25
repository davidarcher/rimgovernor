using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>The zone census and the cell read are whole on the wire: each round-trips exactly and a missing member does not decode.</summary>
public sealed class ZoneCellsWireTests
{
    private static readonly RimGovernor.Brain.GameFacts.Common.ObservationContext Context = new() { NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.UnclaimedCase(), Identity = new RimGovernor.Brain.GameFacts.Common.Identity { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 7 };

    private static readonly RimGovernor.Brain.GameFacts.Observations.ZonesSnapshot Census = new()
    {
        Context = Context,
        Zones =
        [
            ZoneFixtures.Growing("Zone_1", "Plant_Rice", usableCells: 12, edible: true) with { PlantedCells = 4, FertilePlantedCells = 3, BlightedPlants = 1, GrowthTotal = 1.5, FertilityFactorTotal = 3.2 },
            ZoneFixtures.Stockpile("Zone_2", "Important", foodStorage: true) with
            {
                Filter = new RimGovernor.Brain.GameFacts.Observations.StockpileFilter { AllowedDefNames = ["MealSimple"], HitPointsMin = 0, HitPointsMax = 1, QualityMin = Quality.Awful, QualityMax = Quality.Legendary, AllowFresh = true, AllowRotten = false },
            },
            new RimGovernor.Brain.GameFacts.Observations.ZoneState.Other { Id = "Zone_3", Label = "Fishing zone 1", Bounds = ZoneFixtures.Origin },
        ],
    };

    private static RimGovernor.Brain.GameFacts.Observations.CellsSnapshot Cells() => new()
    {
        Context = Context,
        MapSize = new RimGovernor.Brain.GameFacts.Observations.MapSize { Width = 250, Height = 250 },
        Grid = new RimGovernor.Brain.GameFacts.Mirror.CellGrid
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
        var json = OrdersJson.Serialize(Census);
        var back = OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.ZonesSnapshot>(json);

        Assert.Equal(json, OrdersJson.Serialize(back));
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
        var node = JsonNode.Parse(OrdersJson.Serialize(Census))!.AsObject();
        Assert.True(node["zones"]![row]!.AsObject().Remove(member));

        Assert.ThrowsAny<JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.ZonesSnapshot>(node.ToJsonString()));
    }

    [Fact]
    public void TheCellReadRoundTripsExactly()
    {
        var json = OrdersJson.Serialize(Cells());

        Assert.Equal(json, OrdersJson.Serialize(OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.CellsSnapshot>(json)));
    }

    [Theory]
    [InlineData("mapSize")]
    [InlineData("grid")]
    public void ACellReadMissingAMemberDoesNotDecode(string member)
    {
        var node = JsonNode.Parse(OrdersJson.Serialize(Cells()))!.AsObject();
        Assert.True(node.Remove(member));

        Assert.ThrowsAny<JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.CellsSnapshot>(node.ToJsonString()));
    }

    [Fact]
    public void AGridMissingAnArrayDoesNotDecode()
    {
        var node = JsonNode.Parse(OrdersJson.Serialize(Cells()))!.AsObject();
        Assert.True(node["grid"]!.AsObject().Remove("walkable"));

        Assert.ThrowsAny<JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.CellsSnapshot>(node.ToJsonString()));
    }
}
