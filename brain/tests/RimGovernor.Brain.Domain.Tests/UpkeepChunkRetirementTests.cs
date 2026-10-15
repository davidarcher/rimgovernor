using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class UpkeepChunkRetirementTests
{
    [Fact]
    public void KnownEmptyBuildingCensusClearsPreviousChunkOnlyDeficit()
    {
        var result = PolicyFunctions.ReviewUpkeep(new UpkeepObservation
        {
            Clearance = Fact.Known(default(EquatableArray<ClearanceTarget>)),
            Ground = Fact.Known(default(EquatableArray<string>)),
        }, new UpkeepHistory { Clearance = true }, null);
        var need = Assert.Single(result.Needs, n => n.Concern == PolicyFunctions.ClearHomeObstructions);
        Assert.False(need.Active);
        Assert.True(need.Targets.IsKnown);
        Assert.Empty(need.Targets.Value);
    }

    [Fact]
    public void BuildingAndPlannedGroundWorkRemainClearanceDemand()
    {
        var result = PolicyFunctions.ReviewUpkeep(new UpkeepObservation
        {
            Clearance = Fact.Known(EquatableArray.From(new[] { new ClearanceTarget { EntityId = "wall", Deconstructible = true, InHome = true } })),
            Ground = Fact.Known<EquatableArray<string>>(["floor"]),
        }, new UpkeepHistory(), null);
        var need = Assert.Single(result.Needs, n => n.Concern == PolicyFunctions.ClearHomeObstructions);
        Assert.True(need.Active);
        Assert.Equal<string>(["wall", "floor"], need.Targets.Value);
    }

    [Fact]
    public void UnknownBuildingCensusCannotCertifyPriorClearanceComplete()
    {
        var result = PolicyFunctions.ReviewUpkeep(new UpkeepObservation(), new UpkeepHistory { Clearance = true }, null);
        var need = Assert.Single(result.Needs, n => n.Concern == PolicyFunctions.ClearHomeObstructions);
        Assert.True(need.Active);
        Assert.False(need.Targets.IsKnown);
    }
}
