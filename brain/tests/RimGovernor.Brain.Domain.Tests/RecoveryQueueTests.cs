using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The recovery queue (RankRecovery) and its roof-first batch (PlanRecoveryBatch): demand never holds a thing,
/// a census-wide threat or an unsafe route does, a covered loot stack is still worked, and a ruin cluster under
/// a thin roof takes the roof down before any of its ruins come down.
/// </summary>
public sealed class RecoveryQueueTests
{
    private static readonly Resource Steel = new("Steel");

    // A ruin yielding steel the colony has storage room for; route known safe.
    private static RecoveryThing Ruin(string id) => new()
    {
        ID = id,
        Def = "AncientWall",
        Kind = RemoteWorkKind.Salvage,
        Cell = new Cell(10, 10),
        Evidenced = true,
        Route = RouteSafety.Safe,
        Candidate = CandidateRules.SourceCandidate(
            CandidateKind.Salvage,
            id,
            0,
            new CandidatePath.Unwalked(),
            true,
            0,
            CandidateRules.SourceYield(new ResourceKey { Def = Steel }, 20, 0, new YieldStorage.Accepts(100))),
    };

    [Fact]
    public void RuinWithCoveredYieldIsStillAdmitted()
    {
        // Nothing short: stock covers demand.
        var q = RecoveryRules.RankRecovery(new RecoveryRequest { Things = [Ruin("Thing_AncientWall1")], Threat = new HostileCensus.Quiet(), Urgent = false });

        var e = Assert.Single(q.Entries);
        Assert.Equal(RecoveryTier.Rest, e.Tier);
        Assert.Empty(e.Short);
        Assert.Equal(RecoveryStatus.Admitted, e.Status);
        Assert.Equal("", e.Reason);
        Assert.Equal("Thing_AncientWall1", q.Admitted);
    }

    [Fact]
    public void CensusWideThreatHoldsRuinAndAdmitsNothing()
    {
        var q = RecoveryRules.RankRecovery(new RecoveryRequest { Things = [Ruin("Thing_AncientWall1")], Threat = new HostileCensus.Active(1) });

        var e = Assert.Single(q.Entries);
        Assert.Equal(RecoveryStatus.Held, e.Status);
        Assert.Equal(RemoteRules.RemoteHoldThreat, e.Reason);
        Assert.Equal("", q.Admitted);
    }

    [Theory]
    [InlineData(RouteSafety.Unsafe)]
    [InlineData(RouteSafety.Unread)]
    public void AnUnsafeOrUnreadRouteHoldsThing(RouteSafety route)
    {
        var q = RecoveryRules.RankRecovery(new RecoveryRequest { Things = [Ruin("Thing_AncientWall1") with { Route = route }], Threat = new HostileCensus.Quiet() });

        var e = Assert.Single(q.Entries);
        Assert.Equal(RecoveryStatus.Held, e.Status);
        Assert.Equal(RemoteRules.RemoteHoldRouteUnsafe, e.Reason);
        Assert.Equal("", q.Admitted);
    }

    [Fact]
    public void ALootStackWhoseSafetyWasNotReadHasAnUnreadRoute()
    {
        var row = LootFixtures.Stack("Thing_Steel7", "Steel", new Cell(30, 30), true, new RimGovernor.Contracts.Wire.Observations.HaulSafety.Unreached(), 50, 0, [], null);
        Assert.Equal(RouteSafety.Unread, RecoveryRules.RecoveryLootThing(row).Thing.Route);
        Assert.Equal(RouteSafety.Safe, RecoveryRules.RecoveryLootThing(row with { Observed = row.Observed with { Safety = new RimGovernor.Contracts.Wire.Observations.HaulSafety.Safe { PathLength = 0 } } }).Thing.Route);
        Assert.Equal(RouteSafety.Unsafe, RecoveryRules.RecoveryLootThing(row with { Observed = row.Observed with { Safety = new RimGovernor.Contracts.Wire.Observations.HaulSafety.Unsafe() } }).Thing.Route);
    }

    [Fact]
    public void LootStackWithCoveredStockIsAdmitted()
    {
        var row = LootFixtures.Stack("Thing_Steel7", "Steel", new Cell(30, 30), true,
            new RimGovernor.Contracts.Wire.Observations.HaulSafety.Safe { PathLength = 40.0 }, 50, 200, [], null);
        var (thing, ok) = RecoveryRules.RecoveryLootThing(row);
        Assert.True(ok);
        Assert.Equal(RemoteWorkKind.Loot, thing.Kind);

        var q = RecoveryRules.RankRecovery(new RecoveryRequest { Things = [thing], Threat = new HostileCensus.Quiet(), Urgent = false });

        var e = Assert.Single(q.Entries);
        Assert.Equal(RecoveryTier.Rest, e.Tier);
        Assert.Equal(RecoveryStatus.Admitted, e.Status);
        Assert.Equal("Thing_Steel7", q.Admitted);
    }

    private const string ThinRoof = "RoofConstructed";

    // A 10x10 known grid: two adjacent roof-holding ruin walls at (4,4) and (5,4) carrying a thin roof on (4,5) and
    // (5,5). Each wall alone leaves the other holding the roof; together they leave it unsupported.
    private static RoofSupportGrid ClusterGrid(bool roofed)
    {
        var walls = new Dictionary<Cell, Thing>
        {
            [new Cell(4, 4)] = Wall(1),
            [new Cell(5, 4)] = Wall(2),
        };
        var roofCells = new HashSet<Cell> { new(4, 5), new(5, 5) };
        var bounds = new Rectangle(0, 0, 10, 10);
        return new RoofSupportGrid
        {
            Bounds = bounds,
            Radius = 2,
            Cell = c =>
            {
                if (c.X < 0 || c.X >= bounds.Width || c.Z < 0 || c.Z >= bounds.Height)
                {
                    return null;
                }
                var onRoof = roofed && roofCells.Contains(c);
                return SiteCellFixtures.At(c, roof: onRoof ? ThinRoof : null, things: walls.TryGetValue(c, out var w) ? [w] : []);
            },
        };
    }

    private static Thing Wall(ulong id) => new()
    {
        Def = "AncientWall",
        Id = id,
        Category = ThingCategory.Building,
        Faction = ThingFaction.Neutral,
        Flags = ThingMarks.Edifice | ThingMarks.HoldsRoof | ThingMarks.Deconstructible | ThingMarks.Impassable,
    };

    [Fact]
    public void RuinClusterUnderThinRoofTakesRoofDownBeforeRemovingEveryRuin()
    {
        var rules = new RoofRules(new Dictionary<string, RoofRule> { [ThinRoof] = new RoofRule(Thick: false, Natural: false) });
        IReadOnlyList<RecoveryBatchTarget> targets =
        [
            new(Wall(1).LoadId(), new Cell(4, 4)),
            new(Wall(2).LoadId(), new Cell(5, 4)),
        ];

        // Roof up: the stage is roof, the thin roof cells come off and no ruin is admitted yet.
        var first = RecoveryRules.PlanRecoveryBatch(new RecoveryBatchRequest { Grid = ClusterGrid(roofed: true), Rules = rules, Targets = targets });

        Assert.Equal(RecoveryBatchStage.Roof, first.Stage);
        Assert.Equal(new[] { new Cell(4, 5), new Cell(5, 5) }, first.Roof);
        Assert.Empty(first.Batch);
        Assert.Empty(first.Held);
        Assert.Equal(RoofSupportRules.RoofBlockerUnsupported, first.Blocker);

        // Roof down: every ruin of the cluster comes down in one batch.
        var second = RecoveryRules.PlanRecoveryBatch(new RecoveryBatchRequest { Grid = ClusterGrid(roofed: false), Rules = rules, Targets = targets });

        Assert.Equal(RecoveryBatchStage.Removal, second.Stage);
        Assert.Empty(second.Roof);
        Assert.Equal(new[] { Wall(1).LoadId(), Wall(2).LoadId() }, second.Batch);
        Assert.Empty(second.Held);
    }
}
