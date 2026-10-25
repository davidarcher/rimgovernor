using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The colony read's disaster section is total (#3931): the frame always carries its recovery restrictions and building
/// references, so the disaster tick, the recovery safety and the recovery buildings are plain values, and a building
/// reference the frame's table does not hold is a contract break, never an unread census.
/// </summary>
public sealed class ColonyDisasterReadTests
{
    [Fact]
    public void RecoveryUsesTheCanonicalBuildingAndConditionInstances()
    {
        var building = PowerBuildingFixtures.Trader("generator", "Generator", new(3, 4), 1000, 800, true);
        var condition = new DisasterCondition
        {
            Observed = new RimGovernor.Brain.GameFacts.Observations.EnvironmentCondition { Id = "flare", DefName = "SolarFlare", Duration = new RimGovernor.Brain.GameFacts.Observations.ConditionDuration.Timed { Ticks = 700 } },
            DisablesPower = true,
        };
        var snapshot = ColonyFactsCoreTests.Snapshot() with
        {
            Recovery = new() { Restrictions = [], Buildings = [new RimGovernor.Brain.GameFacts.Common.Ref { Id = building.Building.Id }] },
        };

        var facts = ColonyFramesReads.ColonyDisaster(snapshot, new ReviewFacts(), Buildings.Of(building), [condition]);

        Assert.Same(building, Assert.Single(facts.RecoveryBuildings));
        Assert.Same(condition, Assert.Single(facts.DisasterConditions));
        Assert.Same(condition.Observed, facts.DisasterConditions[0].Observed);
        Assert.Equal(snapshot.Context.Tick, facts.DisasterTick.Value);
    }

    [Fact]
    public void AnEmptyRecoverySectionReadsAsNoRestrictionsAndNoBuildings()
    {
        var snapshot = ColonyFactsCoreTests.Snapshot();
        var facts = ColonyFramesReads.ColonyDisaster(snapshot, new ReviewFacts(), default, default);

        Assert.Empty(facts.RecoverySafety.Restrictions);
        Assert.Empty(facts.RecoveryBuildings);
        Assert.Empty(facts.DisasterConditions);
    }

    [Fact]
    public void ARestrictionWithoutAnAreaRetainsItsActualNativeRow()
    {
        var snapshot = ColonyFactsCoreTests.Snapshot() with
        {
            Recovery = new() { Restrictions = [new RimGovernor.Brain.GameFacts.Observations.RecoveryRestriction { Pawn = new RimGovernor.Brain.GameFacts.Common.Ref { Id = "pawn-1" }, AreaId = null }], Buildings = [] },
        };
        var facts = ColonyFramesReads.ColonyDisaster(snapshot, new ReviewFacts(), default, default);

        var restriction = Assert.Single(facts.RecoverySafety.Restrictions);
        Assert.Same(snapshot.Recovery.Restrictions[0], restriction);
        Assert.Equal("pawn-1", restriction.Pawn.Id);
        Assert.Null(restriction.AreaId);
    }

    [Fact]
    public void ARecoveryBuildingTheFrameHoldsNoRowForIsAContractBreak()
    {
        var snapshot = ColonyFactsCoreTests.Snapshot() with
        {
            Recovery = new() { Restrictions = [], Buildings = [new RimGovernor.Brain.GameFacts.Common.Ref { Id = "building-1" }] },
        };

        Assert.Throws<BridgeContractException>(() => ColonyFramesReads.ColonyDisaster(snapshot, new ReviewFacts(), default, default));
    }
}
