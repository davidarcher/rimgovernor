using RimGovernor.Brain.Policy;
using Xunit;
using Planner = RimGovernor.Brain.Runtime.DefenseLayoutPlanner;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The defense alternatives are generated, never admitted (task 7.1): no style is mandatory, every candidate stands on
/// the shared ground ledger, a closed civilian route or blocked access excludes it by name, a longer route prices it
/// by the added cells, retreat holds the standing doors only, sandbags need no research and turrets do, and the order
/// is by kind and cells.
/// </summary>
public sealed class DefenseCandidatesTests
{
    private static readonly Bounds MapBounds = new(200, 200);

    private static MapSurvey OpenSurvey()
    {
        var cells = new List<SurveyCell>(MapBounds.Width * MapBounds.Height);
        for (var z = 0; z < MapBounds.Height; z++)
        {
            for (var x = 0; x < MapBounds.Width; x++)
            {
                cells.Add(new SurveyCell { Cell = new Cell(x, z), Walkable = true, Fertility = 1 });
            }
        }
        return new MapSurvey { Bounds = MapBounds, Cells = cells };
    }

    private static PlannedRoom Room(PlannedRole role, int x, int z, int w, int h, Cell door, Rotation side) => new()
    {
        Role = role,
        Region = RegionRules.RectRegion(new Rectangle(x, z, w, h)),
        Door = door,
        DoorSide = new DoorSide.Doored(side),
    };

    /// <summary>A bedroom whose only door opens into the dining room beside it (interiors x 10–12 and 14–20, z 10–12), no entrances.</summary>
    private static LayoutPlan TwoRooms() => new LayoutPlan
    {
        Rooms =
        [
            Room(LayoutSurveyRules.PlannedBedroom, 10, 10, 3, 3, new Cell(13, 11), Rotation.East),
            Room(LayoutSurveyRules.PlannedDining, 14, 10, 7, 3, new Cell(17, 13), Rotation.North),
        ],
    }.WithRoomIDs();

    /// <summary>A small core (barracks, workshop, kitchen in a row) at the middle of the map, with no defense of any kind.</summary>
    private static LayoutPlan Core()
    {
        int cx = (MapBounds.Width / 2) - 12, cz = (MapBounds.Height / 2) - 3;
        return new LayoutPlan
        {
            Rooms =
            [
                Room(PlannedRole.Barracks, cx, cz, 6, 5, new Cell(cx + 3, cz + 5), Rotation.North),
                Room(PlannedRole.Workshop, cx + 9, cz, 6, 5, new Cell(cx + 12, cz + 5), Rotation.North),
                Room(LayoutSurveyRules.PlannedKitchen, cx + 18, cz, 6, 5, new Cell(cx + 21, cz + 5), Rotation.North),
            ],
        }.WithRoomIDs();
    }

    /// <summary>The core with one entrance north of the barracks door.</summary>
    private static LayoutPlan Entered()
    {
        var plan = Core();
        var door = plan.Rooms[0].Door;
        return plan with { Entrances = [new Cell(door.X, door.Z + 1)] };
    }

    private static DefenseCandidate Draft(DefenseCandidateKind kind, params (string Def, Cell Cell, bool Impassable)[] structures) => new()
    {
        Kind = kind,
        Structures = [.. structures.Select(s => new DefenseStructure(s.Def, s.Cell, "", s.Impassable))],
        Cells = [.. structures.Select(s => s.Cell).OrderBy(c => c.Z).ThenBy(c => c.X)],
    };

    private static DefenseCandidateInputs Inputs(LayoutPlan plan, ReviewFacts? facts = null) => new()
    {
        Plan = plan,
        Survey = OpenSurvey(),
        Facts = facts ?? new ReviewFacts(),
        Threat = DefenseCandidateRules.ThreatOf(facts ?? new ReviewFacts()),
        Definitions = Planner.DefenseDefinitionNames,
    };

    private static DefenseCandidate Of(IEnumerable<DefenseCandidate> candidates, DefenseCandidateKind kind) => Assert.Single(candidates, c => c.Kind == kind);

    /// <summary>
    /// (a) With no threat read and no tier census, the generator still offers retain and a non-killbox alternative,
    /// and lays no killbox: the killbox stands unassessed for its census and builds nothing. The threat names its gaps.
    /// </summary>
    [Fact]
    public void WithNoThreatReadNoKillboxIsLaidAndAlternativesStand()
    {
        var plan = Core();
        var candidates = DefenseCandidateRules.Generate(Inputs(plan));

        var retain = Of(candidates, DefenseCandidateKind.Retain);
        Assert.Empty(retain.Sites);
        Assert.IsType<DefenseCandidateStanding.Generated>(retain.Standing);
        Assert.Contains(candidates, c => c.Kind is not (DefenseCandidateKind.Retain or DefenseCandidateKind.Killbox) && c.Cells.Count > 0 && c.Standing is DefenseCandidateStanding.Generated);

        var killbox = Of(candidates, DefenseCandidateKind.Killbox);
        Assert.Equal(new DefenseCandidateStanding.Unassessed(DefenseCandidateRules.MissingTiers), killbox.Standing);
        Assert.Empty(killbox.Structures);
        Assert.DoesNotContain(plan.Sites, s => s.Kind == NodeKind.Killbox);

        Assert.All(candidates, c =>
        {
            Assert.Contains(RaidExposure.LayoutUnassessed, c.Threat.Missing);
            Assert.Contains("threat:colony census", c.Threat.Missing);
            Assert.Equal(new DefenseExposure.Unread("threat:colony census"), c.Threat.Exposure);
            Assert.Empty(c.Threat.ArrivalEdges);
        });
    }

    /// <summary>(b) A candidate over a field block or a planned bedroom is refused by the shared ground ledger, naming the holder.</summary>
    [Fact]
    public void GroundAFieldOrABedroomHoldsRefusesTheCandidate()
    {
        var field = new LayoutZone { Kind = LayoutZoneKind.Field, Runs = [new RowRun(40, 40, 5)] };
        var plan = TwoRooms() with { Zones = [field] };
        var ground = DefenseGround.Of(plan);
        var bedroom = plan.Rooms[0];

        var overField = DefenseCandidateRules.Constrain(Draft(DefenseCandidateKind.TemporaryCover, ("Sandbags", new Cell(42, 40), false)), ground, plan);
        Assert.Equal(new DefenseCandidateStanding.Excluded("ground:field:0"), overField.Standing);

        var overBedroom = DefenseCandidateRules.Constrain(Draft(DefenseCandidateKind.TemporaryCover, ("Sandbags", new Cell(11, 11), false)), ground, plan);
        Assert.Equal(new DefenseCandidateStanding.Excluded($"ground:room:{bedroom.Id}:Bedroom"), overBedroom.Standing);

        Assert.Null(ground.Refusal([new Cell(60, 60)]));
    }

    /// <summary>
    /// (c) Walling the only door between the bedroom and the dining room closes the civilian trip and the door: both
    /// are named. A door variant on the same cell keeps the trip; a wall that makes it detour prices it by the added
    /// cells; ground the plan's walk does not hold is named unprobed.
    /// </summary>
    [Fact]
    public void ClosingTheOnlyDoorExcludesByRouteAndALongerRouteIsPriced()
    {
        var plan = TwoRooms();
        var free = DefenseGround.Of(new LayoutPlan());
        var door = plan.Rooms[0].Door;

        var walled = DefenseCandidateRules.Constrain(Draft(DefenseCandidateKind.PerimeterDoored, ("Wall", door, true)), free, plan);
        Assert.Equal(new DefenseCandidateStanding.Excluded("route:Bedroom→Dining"), walled.Standing);
        Assert.Contains(new DefenseRouteChange("route:Bedroom→Dining", new DefenseRouteEffect.Closed()), walled.Routes);
        Assert.Contains("access:door@13,11", walled.Exclusions);

        var doored = DefenseCandidateRules.Constrain(Draft(DefenseCandidateKind.PerimeterDoored, ("Door", door, false)), free, plan);
        Assert.IsType<DefenseCandidateStanding.Generated>(doored.Standing);
        Assert.Empty(doored.Routes);
        Assert.Equal([door], doored.Narrows);

        var detour = DefenseCandidateRules.Constrain(Draft(DefenseCandidateKind.PerimeterDoored, ("Wall", new Cell(15, 10), true), ("Wall", new Cell(15, 11), true)), free, plan);
        Assert.IsType<DefenseCandidateStanding.Generated>(detour.Standing);
        Assert.Equal([new DefenseRouteChange("route:Bedroom→Dining", new DefenseRouteEffect.Longer(2))], detour.Routes);

        var outside = DefenseCandidateRules.Constrain(Draft(DefenseCandidateKind.PerimeterDoored, ("Wall", new Cell(60, 60), true)), free, plan);
        Assert.IsType<DefenseCandidateStanding.Generated>(outside.Standing);
        Assert.Equal([new DefenseRouteChange(DefenseCandidateRules.RouteMapEdge, new DefenseRouteEffect.Unprobed())], outside.Routes);
    }

    /// <summary>(d) Retreat holds the standing rooms' existing doors: no site, no structure, no new ground; unread construction leaves it unassessed.</summary>
    [Fact]
    public void RetreatUsesTheStandingInteriorDoorsAndClaimsNoGround()
    {
        var plan = TwoRooms();
        var built = plan.Rooms.Select(r => r.Id).ToHashSet();
        var candidates = DefenseCandidateRules.Generate(Inputs(plan) with { Built = built });

        var retreat = Of(candidates, DefenseCandidateKind.Retreat);
        Assert.IsType<DefenseCandidateStanding.Generated>(retreat.Standing);
        Assert.Equal(plan.Rooms.SelectMany(plan.DoorsOf).Distinct().OrderBy(c => c.Z).ThenBy(c => c.X), retreat.Cells);
        Assert.Empty(retreat.Sites);
        Assert.Empty(retreat.Structures);
        Assert.Empty(retreat.Closes);

        var unread = Of(DefenseCandidateRules.Generate(Inputs(plan)), DefenseCandidateKind.Retreat);
        Assert.Equal(new DefenseCandidateStanding.Unassessed(DefenseCandidateRules.MissingBuilt), unread.Standing);
        var none = Of(DefenseCandidateRules.Generate(Inputs(plan) with { Built = new HashSet<int>() }), DefenseCandidateKind.Retreat);
        Assert.IsType<DefenseCandidateStanding.Excluded>(none.Standing);
    }

    /// <summary>(e) Sandbag cover stands with no research read; the turret emplacements on its firing positions wait on the turret's research.</summary>
    [Fact]
    public void SandbagsNeedNoResearchAndTurretsDo()
    {
        var plan = Entered();
        var turret = DefenseTurretsPolicy.TurretMini;
        ReviewFacts Facts(PlanningAvailability turrets) => new()
        {
            Definitions =
            [
                new PlanningDefinition { Name = DefenseCoverStock.DefenseSandbags, Availability = PlanningAvailability.Ready },
                new PlanningDefinition { Name = turret, Availability = turrets, Research = ["GunTurrets"] },
            ],
        };
        var inputs = Inputs(plan, Facts(PlanningAvailability.ResearchLocked)) with { Turret = turret };

        var locked = DefenseCandidateRules.Generate(inputs);
        var cover = Of(locked, DefenseCandidateKind.TemporaryCover);
        Assert.IsType<DefenseCandidateStanding.Generated>(cover.Standing);
        Assert.NotEmpty(cover.Structures);
        Assert.All(cover.Structures, s => Assert.False(s.Impassable));
        Assert.Equal(new DefenseCandidateStanding.Excluded("research:GunTurrets"), Of(locked, DefenseCandidateKind.Turrets).Standing);

        var ready = DefenseCandidateRules.Generate(inputs with { Facts = Facts(PlanningAvailability.Ready) });
        Assert.IsType<DefenseCandidateStanding.Generated>(Of(ready, DefenseCandidateKind.TemporaryCover).Standing);
        Assert.IsType<DefenseCandidateStanding.Generated>(Of(ready, DefenseCandidateKind.Turrets).Standing);
    }

    /// <summary>(f) The order is the kind, then the first cell (z, x), then the key: the same inputs give the same list, and a shuffle sorts back.</summary>
    [Fact]
    public void CandidateOrderIsByKindAndCellsNotTierIndex()
    {
        var inputs = Inputs(Entered());
        var once = DefenseCandidateRules.Generate(inputs).Select(c => c.Key).ToList();
        Assert.Equal(once, DefenseCandidateRules.Generate(inputs).Select(c => c.Key).ToList());
        Assert.Equal(once.Count, once.Distinct().Count());

        var a = Draft(DefenseCandidateKind.TemporaryCover, ("Sandbags", new Cell(9, 2), false)) with { Key = "a" };
        var b = Draft(DefenseCandidateKind.TemporaryCover, ("Sandbags", new Cell(1, 5), false)) with { Key = "b" };
        var c = Draft(DefenseCandidateKind.Retreat, ("Door", new Cell(50, 50), false)) with { Key = "c" };
        var d = Draft(DefenseCandidateKind.Killbox, ("Wall", new Cell(0, 0), true)) with { Key = "d" };
        Assert.Equal(["c", "a", "b", "d"], DefenseCandidateRules.Ordered([d, b, a, c]).Select(x => x.Key));
    }
}
