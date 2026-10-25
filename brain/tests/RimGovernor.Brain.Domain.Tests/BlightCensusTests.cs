using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class BlightCensusTests
{
    private static readonly Tables Empty = new(default, default, default, []);

    [Fact]
    public void AnEmptyCensusWithoutPlanningIsNoBlight()
    {
        var snapshot = ColonyFactsCoreTests.Snapshot();
        Assert.Empty(ColonyFramesReads.BlightCensus(snapshot, Empty));
    }

    [Fact]
    public void AnAbsentCensusIsAContractFailure()
    {
        Assert.Throws<BridgeContractException>(() => ColonyFramesReads.BlightCensus(ColonyFactsCoreTests.Snapshot() with { Blight = null! }, Empty));
    }

    [Fact]
    public void AnUnresolvedOrUnplacedPlantIsAContractFailure()
    {
        var snapshot = WithPlant(false);
        Assert.Throws<BridgeContractException>(() => ColonyFramesReads.BlightCensus(snapshot, Empty));
        var things = Things.Of(Plant(new RimGovernor.Brain.GameFacts.Observations.EntityPlace.Unplaced()));
        Assert.Throws<BridgeContractException>(() => ColonyFramesReads.BlightCensus(snapshot, Empty with { Things = things }));
        var place = System.Text.Json.Nodes.JsonNode.Parse(OrdersJson.Serialize<RimGovernor.Brain.GameFacts.Observations.EntityPlace>(new RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap(0, new(1, 2))))!.AsObject();
        Assert.True(place.Remove("cell"));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.EntityPlace>(place.ToJsonString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APlantOutsideThePlanningWindowStillProducesTheBlightNeed(bool designated)
    {
        var snapshot = WithPlant(designated);
        var things = Things.Of(Plant(new RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap(0, new RimGovernor.Brain.Domain.Cell { X = 180, Z = 190 })));
        var census = ColonyFramesReads.BlightCensus(snapshot, Empty with { Things = things });
        var row = Assert.Single(census);
        Assert.Equal(("plant", "Plant_Rice", new Cell(180, 190), designated), (row.ID, row.Definition, row.Cell, row.Designated));
        var family = new FoodFamily(false, false, blight: true);
        Assert.Same(snapshot.Blight.Plants[0], row.Observed);
        var facts = new FoodFacts { Blight = census };
        var need = Assert.Single(family.Derive(facts));
        Assert.Equal(Satisfaction.Unmet, need.Satisfaction);
        Assert.Equal(designated ? 0 : 1, family.Propose([need], facts).Count);
        var json = OrdersJson.Serialize(snapshot);
        Assert.Equal(json, OrdersJson.Serialize(OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot>(json)));
    }

    [Theory]
    [InlineData("blight")]
    [InlineData("plants")]
    public void RequiredWireCensusMembersCannotBeOmitted(string member)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(OrdersJson.Serialize(ColonyFactsCoreTests.Snapshot()))!.AsObject();
        var parent = member == "blight" ? json : json["blight"]!.AsObject();
        Assert.True(parent.Remove(member));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot>(json.ToJsonString()));
    }

    private static RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot WithPlant(bool designated) => ColonyFactsCoreTests.Snapshot() with
    {
        Blight = new() { Plants = [new() { Thing = new() { Id = "plant" }, Designated = designated }] },
    };

    private static RimGovernor.Brain.GameFacts.Observations.Thing Plant(RimGovernor.Brain.GameFacts.Observations.EntityPlace place) => new()
    {
        Entity = new() { Id = "plant", DefName = "Plant_Rice", Label = "rice plant", Place = place },
        Forbidden = false, Corpse = false,
    };
}
