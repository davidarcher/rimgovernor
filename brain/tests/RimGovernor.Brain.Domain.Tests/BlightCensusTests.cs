using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire;
using Xunit;
using C = RimGovernor.Contracts.Wire.Common;
using O = RimGovernor.Contracts.Wire.Observations;

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
        var things = Things.Of(Plant(new O.EntityPlace.Unplaced()));
        Assert.Throws<BridgeContractException>(() => ColonyFramesReads.BlightCensus(snapshot, Empty with { Things = things }));
        var missingCell = Things.Of(Plant(new O.EntityPlace.OnMap(0, null!)));
        Assert.Throws<BridgeContractException>(() => ColonyFramesReads.BlightCensus(snapshot, Empty with { Things = missingCell }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APlantOutsideThePlanningWindowStillProducesTheBlightNeed(bool designated)
    {
        var snapshot = WithPlant(designated);
        var things = Things.Of(Plant(new O.EntityPlace.OnMap(0, new C.Cell { X = 180, Z = 190 })));
        var census = ColonyFramesReads.BlightCensus(snapshot, Empty with { Things = things });
        var row = Assert.Single(census);
        Assert.Equal(("plant", "Plant_Rice", new Cell(180, 190), designated, true), (row.ID, row.Definition, row.Cell, row.Designated, row.Eligible));
        var family = new FoodFamily(false, false, blight: true);
        var facts = new FoodFacts { Blight = census };
        var need = Assert.Single(family.Derive(facts));
        Assert.Equal(Satisfaction.Unmet, need.Satisfaction);
        Assert.Equal(designated ? 0 : 1, family.Propose([need], facts).Count);
        var json = WireJson.Serialize(snapshot);
        Assert.Equal(json, WireJson.Serialize(WireJson.Deserialize<O.ColonyFactsSnapshot>(json)));
    }

    [Theory]
    [InlineData("blight")]
    [InlineData("plants")]
    public void RequiredWireCensusMembersCannotBeOmitted(string member)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(WireJson.Serialize(ColonyFactsCoreTests.Snapshot()))!.AsObject();
        var parent = member == "blight" ? json : json["blight"]!.AsObject();
        Assert.True(parent.Remove(member));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => WireJson.Deserialize<O.ColonyFactsSnapshot>(json.ToJsonString()));
    }

    private static O.ColonyFactsSnapshot WithPlant(bool designated) => ColonyFactsCoreTests.Snapshot() with
    {
        Blight = new() { Plants = [new() { Thing = new() { Id = "plant" }, Designated = designated }] },
    };

    private static O.Thing Plant(O.EntityPlace place) => new()
    {
        Entity = new() { Id = "plant", DefName = "Plant_Rice", Label = "rice plant", Place = place },
        StackCount = 1, Forbidden = false, Corpse = false, Food = new O.ThingFood.NotFood(),
    };
}
