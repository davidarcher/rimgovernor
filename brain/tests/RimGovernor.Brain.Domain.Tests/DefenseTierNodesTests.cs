using RimGovernor.Brain.Acceptance;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Store.Codec;
using Xunit;

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
        var layout = Assert.IsType<CombatLayout>(DefensePlanner.CombatLayoutOf(Staged()));
        Assert.Equal(Firing.Select(f => f.Cell), layout.Firing.ToArray());
        Assert.Equal(Firing.Select(f => f.Retreat), layout.Retreat.ToArray());
        Assert.Equal(new CombatChoke.Corridor(Lane[^1]), layout.Choke);
        Assert.Equal(Rotation.South, layout.Toward);

        // A plan without tier nodes gives combat no firing line and no choke.
        var bare = Assert.IsType<CombatLayout>(DefensePlanner.CombatLayoutOf(LayoutPlan.OpeningPlan(Rotation.South)));
        Assert.Empty(bare.Firing);
        Assert.IsType<CombatChoke.NoCorridor>(bare.Choke);
    }

    private static readonly DefenseDefinitions Defs = DefenseLayoutPlanner.DefenseDefinitionNames;

    private static readonly Cell[] Walls = [new Cell(26, 30), new Cell(34, 30)];

    private static readonly Cell MiniTurret = new(24, 26);

    private static Building At(string definition, Cell c, string stuff = "WoodLog") => new(definition, c, Rotation.North, stuff);

    /// <summary>A proposed layout: the corridor's reservation, the funnel walls, the firing line's cover and floors, and one mini turret.</summary>
    private static DefenseLayout Proposed(int shift = 0)
    {
        Cell S(Cell c) => new(c.X + shift, c.Z);
        var firingReserved = Firing.SelectMany(f => new[] { f.Cover, f.Cell, f.Retreat }).ToList();
        return new DefenseLayout
        {
            Toward = Rotation.South,
            TrapLane = [.. Lane],
            Firing = [.. Firing],
            Tiers =
            [
                new DefenseTier { Name = DefenseLayout.TierChokepoint, Reserved = [.. Lane] },
                new DefenseTier { Name = DefenseLayout.TierFiringLine, Buildings = [.. Firing.SelectMany(f => new[] { At(Defs.Sandbag, f.Cover), At(Defs.Floor, f.Cell, "") })], Reserved = [.. firingReserved] },
                new DefenseTier { Name = DefenseLayout.TierFunnel, Buildings = [.. Walls.Select(c => At(Defs.Wall, S(c)))], Reserved = [.. Walls.Select(S)] },
                new DefenseTier
                {
                    Name = DefenseTurretsPolicy.TierTurrets,
                    Buildings = [At(DefenseTurretsPolicy.TurretMini, S(MiniTurret), "Steel")],
                    Reserved = [.. DefenseTurretsPolicy.TurretFootprint(S(MiniTurret), new Bounds(1, 1))],
                },
            ],
        };
    }

    private static DefenseLayoutRecord RecordOf(DefenseLayout layout)
    {
        var record = new DefenseLayoutRecord();
        foreach (var t in layout.Tiers)
        {
            record.Tiers.Add(new DefenseTierRecord
            {
                Name = t.Name,
                Buildings = [.. t.Buildings.Select(b => new DefenseBuilding { Definition = b.Definition, Cell = b.Cell, Rotation = b.Rotation, Stuff = b.Stuff })],
            });
        }
        return record;
    }

    /// <summary>The opening plan with the proposed layout cut into tier nodes under its killbox, nothing pinned.</summary>
    private static LayoutPlan ProposedPlan()
    {
        var plan = LayoutPlan.OpeningPlan(Rotation.South);
        var (under, _, _) = LayoutPlan.KillboxNode(plan);
        return plan.WithDefenseTiers(DefenseTiersPolicy.TierNodes(under, Defs, Proposed(), []));
    }

    [Fact]
    public void ALayoutCutIntoTierNodesRoundTripsInThePlan()
    {
        var layout = Proposed();
        var plan = ProposedPlan();
        var back = StoreJson.Deserialize<LayoutPlan>(StoreJson.Serialize(plan));
        var planned = back.PlannedDefenseTiers();
        foreach (var t in layout.Tiers)
        {
            Assert.True(planned.TryGetValue(t.Name, out var nodes), t.Name.Value);
            Assert.Equal(t.Buildings.OrderBy(b => b.Cell.X).ThenBy(b => b.Cell.Z).ThenBy(b => b.Definition), nodes.Buildings.OrderBy(b => b.Cell.X).ThenBy(b => b.Cell.Z).ThenBy(b => b.Definition));
            Assert.Equal(t.Reserved.ToHashSet(), nodes.Reserved.ToHashSet());
        }
        Assert.Equal(Firing, back.DefenseTierGeometry().Firing.ToArray());
        Assert.Equal(Lane, back.DefenseTierGeometry().TrapLane.ToArray());
    }

    [Fact]
    public void APinnedTierKeepsItsPlanNodesAcrossAReSite()
    {
        var planned = ProposedPlan().PlannedDefenseTiers();
        var pins = new DefenseTierPins
        {
            Tiers = new Dictionary<DefenseTierName, DefenseTier> { [DefenseLayout.TierFunnel] = planned[DefenseLayout.TierFunnel] },
        };

        // A re-site that would move every tier keeps the pinned funnel where the plan holds it and moves the rest.
        var (resited, pinned) = DefenseTiersPolicy.ApplyPins(Proposed(shift: 3), pins);
        Assert.Equal(planned[DefenseLayout.TierFunnel].Buildings, resited.Tiers.Single(t => t.Name == DefenseLayout.TierFunnel).Buildings);
        Assert.Single(pinned);
        Assert.Equal(new Cell(MiniTurret.X + 3, MiniTurret.Z), resited.Tiers.Single(t => t.Name == DefenseTurretsPolicy.TierTurrets).Buildings.Single().Cell);
    }

    [Fact]
    public void TheRecordLayoutReadsThePlansReservedCells()
    {
        var plan = ProposedPlan();
        var record = RecordOf(Proposed());
        var killbox = new DefenseKillbox { Toward = Rotation.South, Entry = Lane[0] };
        var planned = plan.PlannedDefenseTiers();

        var layout = DefenseLayoutPlanner.DefenseRecordLayout(record, plan, killbox);
        foreach (var t in layout.Tiers)
        {
            Assert.Equal(planned[t.Name].Reserved, t.Reserved);
        }
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
    public void AFixtureStagesItsExactCellsInThePlan()
    {
        var l = new LabLayout { Toward = "south", Choke = new LabCell(50, 57) };
        l.Firing.Add(new LabCell(48, 53));
        l.Firing.Add(new LabCell(52, 53));
        l.Retreat.Add(new LabCell(48, 45));
        l.Retreat.Add(new LabCell(52, 45));
        var layout = Assert.IsType<CombatLayout>(DefensePlanner.CombatLayoutOf(CombatLabLayout.Plan(l)));
        Assert.Equal(new[] { new Cell(48, 53), new Cell(52, 53) }, layout.Firing.ToArray());
        Assert.Equal(new[] { new Cell(48, 45), new Cell(52, 45) }, layout.Retreat.ToArray());
        Assert.Equal(new CombatChoke.Corridor(new Cell(50, 57)), layout.Choke);
        Assert.Equal(Rotation.South, layout.Toward);
    }
}
