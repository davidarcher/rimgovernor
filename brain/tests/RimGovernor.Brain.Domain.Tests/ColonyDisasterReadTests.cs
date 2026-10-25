using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using Xunit;
using C = RimGovernor.Contracts.Wire.Common;
using O = RimGovernor.Contracts.Wire.Observations;

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
            Observed = new O.EnvironmentCondition { Id = "flare", DefName = "SolarFlare", Duration = new O.ConditionDuration.Timed { Ticks = 700 } },
            DisablesPower = true,
        };
        var snapshot = ColonyFactsCoreTests.Snapshot() with
        {
            Recovery = new() { Restrictions = [], Buildings = [new C.Ref { Id = building.Building.Id }] },
        };

        var facts = ColonyFramesReads.ColonyDisaster(snapshot, new RoundsFacts(), Buildings.Of(building), [condition]);

        Assert.Same(building, Assert.Single(facts.RecoveryBuildings));
        Assert.Same(condition, Assert.Single(facts.DisasterConditions));
        Assert.Same(condition.Observed, facts.DisasterConditions[0].Observed);
        Assert.Equal(snapshot.Context.Tick, facts.DisasterTick.Value);
    }

    [Fact]
    public void AnEmptyRecoverySectionReadsAsNoRestrictionsAndNoBuildings()
    {
        var snapshot = ColonyFactsCoreTests.Snapshot();
        var facts = ColonyFramesReads.ColonyDisaster(snapshot, new RoundsFacts(), default, default);

        Assert.Empty(facts.RecoverySafety.Restrictions);
        Assert.Empty(facts.RecoveryBuildings);
        Assert.Empty(facts.DisasterConditions);
    }

    [Fact]
    public void ARestrictionWithoutAnAreaRetainsItsActualNativeRow()
    {
        var snapshot = ColonyFactsCoreTests.Snapshot() with
        {
            Recovery = new() { Restrictions = [new O.RecoveryRestriction { Pawn = new C.Ref { Id = "pawn-1" }, AreaId = null }], Buildings = [] },
        };
        var facts = ColonyFramesReads.ColonyDisaster(snapshot, new RoundsFacts(), default, default);

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
            Recovery = new() { Restrictions = [], Buildings = [new C.Ref { Id = "building-1" }] },
        };

        Assert.Throws<BridgeContractException>(() => ColonyFramesReads.ColonyDisaster(snapshot, new RoundsFacts(), default, default));
    }
}
