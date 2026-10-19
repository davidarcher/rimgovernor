using RimGovernor.Brain.Acceptance;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Store.Codec;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The defense tier nodes are stored in the layout plan (#3583): combat, the pins of the tier siting and the combat
/// fixtures read the firing line and the trap lane from the plan's DefFiring and DefLane nodes, which survive a
/// round-trip through the store and a replan over the same killbox.
/// </summary>
public sealed class DefenseTierNodesTests
{
    private static readonly FiringPosition[] Firing =
    [
        new(new Cell(28, 26), new Cell(28, 27), new Cell(28, 22)),
        new(new Cell(32, 26), new Cell(32, 27), new Cell(32, 22)),
    ];

    private static readonly Cell[] Lane = [new Cell(30, 40), new Cell(30, 35), new Cell(30, 30)];

    private static LayoutPlan Staged(Rotation toward = Rotation.South) => LayoutPlan.OpeningPlan(toward).WithDefenseTierGeometry(Firing, Lane);

    [Fact]
    public void ThePlanRoundTripsItsTierNodes()
    {
        var plan = Staged();
        Assert.True(plan.Valid());
        var back = StoreJson.Deserialize<LayoutPlan>(StoreJson.Serialize(plan));
        Assert.True(back.Valid());
        var g = back.DefenseTierGeometry();
        Assert.Equal(Firing, g.Firing.ToArray());
        Assert.Equal(Lane, g.TrapLane.ToArray());
        var (killbox, _, _) = LayoutPlan.KillboxNode(back);
        Assert.All(back.Sites.Where(s => DefenseTiersPolicy.IsDefenseTierNode(s.Kind)), s => Assert.Equal(killbox, s.Tier!.Under));
    }

    [Fact]
    public void CombatFightsFromThePlansFiringAndLaneNodes()
    {
        Assert.True(RoundsDefensePlanner.CombatLayoutOf(Staged()).TryGetValue(out var layout));
        Assert.Equal(Firing.Select(f => f.Cell), layout.Firing.ToArray());
        Assert.Equal(Firing.Select(f => f.Retreat), layout.Retreat.ToArray());
        Assert.Equal(Known(Lane[^1]), layout.Choke);
        Assert.Equal(Rotation.South, layout.Toward);

        // A plan without tier nodes gives combat no firing line and no choke.
        Assert.True(RoundsDefensePlanner.CombatLayoutOf(LayoutPlan.OpeningPlan(Rotation.South)).TryGetValue(out var bare));
        Assert.Empty(bare.Firing);
        Assert.False(bare.Choke.TryGetValue(out _));
    }

    [Fact]
    public void ThePinsTakeTheirGeometryFromThePlan()
    {
        var record = new DefenseLayoutRecord
        {
            Tiers =
            [
                new DefenseTierRecord { Name = DefenseLayout.TierChokepoint, Reserved = [.. Lane] },
                new DefenseTierRecord { Name = DefenseLayout.TierFiringLine, Built = true },
            ],
        };
        var pins = RoundsDefenseLayoutPlanner.DefenseTierPins(record, Staged(), null);
        Assert.True(pins.Tiers.ContainsKey(DefenseLayout.TierFiringLine));
        Assert.Equal(Firing, pins.Firing.ToArray());
        Assert.Equal(Lane, pins.TrapLane.ToArray());
    }

    [Fact]
    public void AReplanKeepsTheTierNodesOnlyOverTheSameKillbox()
    {
        var prev = Staged();
        var same = LayoutPlan.OpeningPlan(Rotation.South).WithDefenseTiersOf(prev);
        Assert.Equal(Firing, same.DefenseTierGeometry().Firing.ToArray());
        var moved = LayoutPlan.OpeningPlan(Rotation.North).WithDefenseTiersOf(prev);
        Assert.Empty(moved.DefenseTierGeometry().Firing);
        Assert.Empty(moved.DefenseTierGeometry().TrapLane);
    }

    [Fact]
    public void ASitingIsRecordedInThePlanOnlyWhenTheRecordTakesItAndTheNodesChanged()
    {
        var plan = LayoutPlan.OpeningPlan(Rotation.South);
        var staged = Staged();
        var nodes = staged.Sites.Where(s => DefenseTiersPolicy.IsDefenseTierNode(s.Kind)).ToList();
        var sited = new DefenseTiers { Nodes = EquatableArray.From(nodes), Layout = new DefenseLayout { Firing = EquatableArray.From(Firing) } };
        Assert.Equal(Firing, RoundsDefenseLayoutPlanner.DefenseTiersStored(plan, sited)!.DefenseTierGeometry().Firing.ToArray());
        Assert.Null(RoundsDefenseLayoutPlanner.DefenseTiersStored(staged, sited));
        Assert.Null(RoundsDefenseLayoutPlanner.DefenseTiersStored(plan, sited with { Refusal = "no corridor" }));
    }

    [Fact]
    public void AFixtureStagesItsExactCellsInThePlan()
    {
        var l = new LabLayout { Toward = "south", Choke = new LabCell(50, 57) };
        l.Firing.Add(new LabCell(48, 53));
        l.Firing.Add(new LabCell(52, 53));
        l.Retreat.Add(new LabCell(48, 45));
        l.Retreat.Add(new LabCell(52, 45));
        Assert.True(RoundsDefensePlanner.CombatLayoutOf(CombatLabLayout.Plan(l)).TryGetValue(out var layout));
        Assert.Equal(new[] { new Cell(48, 53), new Cell(52, 53) }, layout.Firing.ToArray());
        Assert.Equal(new[] { new Cell(48, 45), new Cell(52, 45) }, layout.Retreat.ToArray());
        Assert.Equal(Known(new Cell(50, 57)), layout.Choke);
        Assert.Equal(Rotation.South, layout.Toward);
    }
}
