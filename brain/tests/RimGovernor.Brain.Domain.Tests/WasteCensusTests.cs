using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire;
using Xunit;
using C = RimGovernor.Contracts.Wire.Common;
using O = RimGovernor.Contracts.Wire.Observations;

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
            Waste = new() { Items = [new O.WasteItem { Thing = new() { Id = "corpse" }, CorpseClass = C.CorpseClass.Colonist, Grave = null }] },
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
                new O.WasteItem { Thing = new() { Id = "colonist" }, CorpseClass = C.CorpseClass.Colonist, Grave = null },
                new O.WasteItem { Thing = new() { Id = "stranger" }, CorpseClass = C.CorpseClass.Stranger, Grave = new() { Id = "grave" } },
            ] },
        };
        var tables = Empty with { Things = Things.Of(Corpse("colonist"), Corpse("stranger")) };
        var waste = ColonyFramesReads.ColonyWaste(snapshot, tables);
        Assert.Same(snapshot.Waste.Items[0], waste[0]);
        Assert.Equal(C.CorpseClass.Colonist, waste[0].CorpseClass);
        Assert.Null(waste[0].Grave);
        Assert.Same(snapshot.Waste.Items[1], waste[1]);
        Assert.Equal(C.CorpseClass.Stranger, waste[1].CorpseClass);
        Assert.Equal("grave", waste[1].Grave!.Id);
        Assert.True(MorgueRules.MorgueWaiting(waste));
    }

    [Theory]
    [InlineData("waste")]
    [InlineData("items")]
    public void RequiredWireCensusMembersCannotBeOmitted(string member)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(WireJson.Serialize(ColonyFactsCoreTests.Snapshot()))!.AsObject();
        var parent = member == "waste" ? json : json["waste"]!.AsObject();
        Assert.True(parent.Remove(member));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => WireJson.Deserialize<O.ColonyFactsSnapshot>(json.ToJsonString()));
    }

    private static O.Thing Corpse(string id) => new()
    {
        Entity = new() { Id = id, DefName = "Corpse_Human", Label = "corpse", Place = new O.EntityPlace.Unplaced() },
        Forbidden = false, Corpse = true,
    };
}
