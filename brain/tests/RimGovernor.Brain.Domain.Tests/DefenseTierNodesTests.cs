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
                new DefenseTierRecord { Name = DefenseLayout.TierChokepoint },
                new DefenseTierRecord { Name = DefenseLayout.TierFiringLine, Built = true },
            ],
        };
        var pins = RoundsDefenseLayoutPlanner.DefenseTierPins(record, Staged(), null);
        Assert.True(pins.Tiers.ContainsKey(DefenseLayout.TierFiringLine));
        Assert.Equal(Firing, pins.Firing.ToArray());
        Assert.Equal(Lane, pins.TrapLane.ToArray());
    }

    private static readonly DefenseDefinitions Defs = RoundsDefenseLayoutPlanner.DefenseDefinitionNames;

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
            record.SetPolicyTier(t);
        }
        return record;
    }

    private static LayoutPlan ProposedPlan() => LayoutPlan.OpeningPlan(Rotation.South).WithProposedTiers(Defs, Proposed());

    [Fact]
    public void AFreshlyProposedLayoutHasItsTierNodesInThePlan()
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
    public void AStartedTierStaysPinnedFromItsPlanNodesAcrossAReSite()
    {
        var plan = ProposedPlan();
        var record = RecordOf(Proposed());
        // The firing line was admitted and every building of it is destroyed; one funnel wall stands with no attempt
        // admitted; the turrets are unstarted.
        record.Tiers.Single(t => t.Name == DefenseLayout.TierFiringLine).Attempts = 1;
        var census = new DefenseCensus { Edifice = { [Walls[0]] = Defs.Wall } };
        var pins = RoundsDefenseLayoutPlanner.DefenseTierPins(record, plan, census);
        var planned = plan.PlannedDefenseTiers();

        Assert.Equal(
            new[] { DefenseLayout.TierChokepoint, DefenseLayout.TierFiringLine, DefenseLayout.TierFunnel }.Select(n => n.Value).Order(),
            pins.Tiers.Keys.Select(n => n.Value).Order());
        foreach (var (name, pin) in pins.Tiers)
        {
            // The whole tier is pinned as its nodes hold it, its destroyed placements included.
            Assert.Equal(planned[name], pin);
        }
        Assert.Equal(Walls.Length, pins.Tiers[DefenseLayout.TierFunnel].Buildings.Count);
        Assert.Equal(Firing.Length * 2, pins.Tiers[DefenseLayout.TierFiringLine].Buildings.Count);

        // A re-site that would move every tier keeps the pinned ones where the plan holds them and moves the unstarted turrets.
        var (resited, pinned) = DefenseTiersPolicy.ApplyPins(Proposed(shift: 3), pins);
        Assert.Equal(planned[DefenseLayout.TierFunnel].Buildings, resited.Tiers.Single(t => t.Name == DefenseLayout.TierFunnel).Buildings);
        Assert.Equal(3, pinned.Count);
        RoundsDefenseLayoutPlanner.SiteUnstartedTiers(record, new DefenseTiers { Layout = resited }, pins);
        Assert.Equal(Walls, record.Tiers.Single(t => t.Name == DefenseLayout.TierFunnel).Buildings.Select(b => b.Cell));
        Assert.Equal(1, record.Tiers.Single(t => t.Name == DefenseLayout.TierFiringLine).Attempts);
        Assert.Equal(new Cell(MiniTurret.X + 3, MiniTurret.Z), record.Tiers.Single(t => t.Name == DefenseTurretsPolicy.TierTurrets).Buildings.Single().Cell);
    }

    [Fact]
    public void CoverAndTurretReplacementReadThePlansReservedCells()
    {
        var plan = ProposedPlan();
        var record = RecordOf(Proposed());
        var killbox = new DefenseKillbox { Toward = Rotation.South, Entry = Lane[0] };
        var planned = plan.PlannedDefenseTiers();

        var layout = RoundsDefenseLayoutPlanner.DefenseRecordLayout(record, plan, killbox);
        foreach (var t in layout.Tiers)
        {
            Assert.Equal(planned[t.Name].Reserved, t.Reserved);
        }
        var geometry = RoundsDefenseLayoutPlanner.DefenseRecordGeometry(record, plan, killbox);
        var expected = planned.Where(kv => kv.Key != DefenseTurretsPolicy.TierTurrets).SelectMany(kv => kv.Value.Reserved).ToHashSet();
        Assert.True(expected.SetEquals(geometry.Reserved));
        Assert.DoesNotContain(MiniTurret, geometry.Reserved);

        // The replacement anchors on the same cell; the turret node's reservation becomes the new footprint by its size.
        var old = At(DefenseTurretsPolicy.TurretMini, MiniTurret, "Steel");
        var replacement = At(DefenseTurretsPolicy.TurretAutocannon, MiniTurret, "Steel");
        var size = new Bounds(2, 2);
        var standing = new[] { new StandingTurret(old, [.. DefenseTurretsPolicy.TurretFootprint(MiniTurret, new Bounds(1, 1))]) };
        var replaced = RoundsDefenseLayoutPlanner.ApplyTurretReplacement(record, plan, old, replacement, standing, size);
        var turrets = replaced.PlannedDefenseTiers()[DefenseTurretsPolicy.TierTurrets];
        Assert.Equal(replacement, turrets.Buildings.Single());
        Assert.Equal(DefenseTurretsPolicy.TurretFootprint(MiniTurret, size).ToHashSet(), turrets.Reserved.ToHashSet());
        Assert.Equal(replacement.Definition, record.Tiers.Single(t => t.Name == DefenseTurretsPolicy.TierTurrets).Buildings.Single().Definition);
        var after = RoundsDefenseLayoutPlanner.DefenseRecordLayout(record, replaced, killbox);
        Assert.Equal(turrets.Reserved, after.Tiers.Single(t => t.Name == DefenseTurretsPolicy.TierTurrets).Reserved);
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
