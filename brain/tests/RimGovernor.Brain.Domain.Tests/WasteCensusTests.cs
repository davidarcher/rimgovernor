using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class WasteCensusTests
{
    private static readonly Tables Empty = new(default, default, default, []);

    [Fact]
    public void AnEmptyCensusIsNoCorpses()
    {
        Assert.Empty(ColonyFramesReads.ColonyWaste(ColonyFactsCoreTests.Snapshot(), Empty));
    }

    [Fact]
    public void AMissingCensusIsAContractFailure()
    {
        var snapshot = ColonyFactsCoreTests.Snapshot() with { Waste = null! };
        Assert.Throws<BridgeContractException>(() => ColonyFramesReads.ColonyWaste(snapshot, Empty));
    }

    [Fact]
    public void AMissingCorpseRowIsAContractFailure()
    {
        var snapshot = ColonyFactsCoreTests.Snapshot() with
        {
            Waste = new() { Items = [new RimGovernor.Brain.GameFacts.Observations.WasteItem { Thing = new() { Id = "corpse" }, CorpseClass = RimGovernor.Brain.GameFacts.Common.CorpseClass.Colonist, Grave = null }] },
        };
        Assert.Throws<BridgeContractException>(() => ColonyFramesReads.ColonyWaste(snapshot, Empty));
    }

    [Fact]
    public void SpawnedAndBuriedCorpsesRetainTheirBurialDemand()
    {
        var snapshot = ColonyFactsCoreTests.Snapshot() with
        {
            Waste = new() { Items =
            [
                new RimGovernor.Brain.GameFacts.Observations.WasteItem { Thing = new() { Id = "colonist" }, CorpseClass = RimGovernor.Brain.GameFacts.Common.CorpseClass.Colonist, Grave = null },
                new RimGovernor.Brain.GameFacts.Observations.WasteItem { Thing = new() { Id = "stranger" }, CorpseClass = RimGovernor.Brain.GameFacts.Common.CorpseClass.Stranger, Grave = new() { Id = "grave" } },
            ] },
        };
        var tables = Empty with { Things = Things.Of(Corpse("colonist"), Corpse("stranger")) };
        var waste = ColonyFramesReads.ColonyWaste(snapshot, tables);
        Assert.Same(snapshot.Waste.Items[0], waste[0]);
        Assert.Equal(RimGovernor.Brain.GameFacts.Common.CorpseClass.Colonist, waste[0].CorpseClass);
        Assert.Null(waste[0].Grave);
        Assert.Same(snapshot.Waste.Items[1], waste[1]);
        Assert.Equal(RimGovernor.Brain.GameFacts.Common.CorpseClass.Stranger, waste[1].CorpseClass);
        Assert.Equal("grave", waste[1].Grave!.Id);
        Assert.True(MorgueRules.MorgueWaiting(waste));
    }

    [Theory]
    [InlineData("waste")]
    [InlineData("items")]
    public void RequiredWireCensusMembersCannotBeOmitted(string member)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(OrdersJson.Serialize(ColonyFactsCoreTests.Snapshot()))!.AsObject();
        var parent = member == "waste" ? json : json["waste"]!.AsObject();
        Assert.True(parent.Remove(member));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot>(json.ToJsonString()));
    }

    private static RimGovernor.Brain.GameFacts.Observations.Thing Corpse(string id) => new()
    {
        Entity = new() { Id = id, DefName = "Corpse_Human", Label = "corpse", Place = new RimGovernor.Brain.GameFacts.Observations.EntityPlace.Unplaced() },
        Forbidden = false, Corpse = true,
    };
}
