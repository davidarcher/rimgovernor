using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>The home-upkeep clearance Need (#3253) retires on a known empty census and never on an unknown one.</summary>
public sealed class UpkeepChunkRetirementTests
{
    private static Need Clearance(HomeUpkeepFacts facts) =>
        Assert.Single(new HomeUpkeepFamily(repairs: false).Derive(facts), n => n.Id == HomeUpkeepFamily.Clearance);

    [Fact]
    public void KnownEmptyBuildingCensusClearsPreviousChunkOnlyDeficit()
    {
        var need = Clearance(new HomeUpkeepFacts
        {
            Clearance = Fact.Known(default(EquatableArray<ClearanceTarget>)),
            Ground = Fact.Known(default(EquatableArray<string>)),
        });
        Assert.Equal(Satisfaction.Met, need.Satisfaction);
        Assert.Equal(0.0, need.Deficit.Value);
    }

    [Fact]
    public void BuildingAndPlannedGroundWorkRemainClearanceDemand()
    {
        var need = Clearance(new HomeUpkeepFacts
        {
            Clearance = Fact.Known(EquatableArray.From(new[] { new ClearanceTarget { EntityId = "wall", Deconstructible = true } })),
            Ground = Fact.Known<EquatableArray<string>>(["floor"]),
        });
        Assert.Equal(Satisfaction.Unmet, need.Satisfaction);
        Assert.Equal(2.0, need.Deficit.Value);
        Assert.Equal<string>(["wall", "floor"], HomeUpkeepFamily.ClearanceTargets(
            [new ClearanceTarget { EntityId = "wall", Deconstructible = true }], Fact.Known<EquatableArray<string>>(["floor"])));
    }

    [Fact]
    public void UnknownBuildingCensusCannotCertifyPriorClearanceComplete()
    {
        var need = Clearance(new HomeUpkeepFacts { Ground = Fact.Known(default(EquatableArray<string>)) });
        Assert.Equal(Satisfaction.Unknown, need.Satisfaction);
        Assert.False(need.Deficit.IsKnown);
    }
}
