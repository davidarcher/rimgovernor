using RimGovernor.Brain.Policy;
using Xunit;
using Passion = RimGovernor.Brain.GameFacts.Observations.Passion;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The defense comparison (task 7.3): complete alternatives priced by their construction over the free builders, their
/// materials against the claimed stock, their turrets' network, the protection lost while they are built and the raid harm
/// the approach they leave open costs, against retaining the plan's defense. A temporary line beats a perimeter, a
/// retreat beats firing lines the colony cannot staff, removing standing pieces is charged their protection, and a build
/// receipt alone establishes no protection.
/// </summary>
public sealed class DefenseChoiceTests
{
    private static readonly Resource Wood = new("WoodLog");

    /// <summary>A day of an open approach costs one capability-day.</summary>
    private const double Loss = 1;

    private static readonly DefenseCandidate Retain = new() { Kind = DefenseCandidateKind.Retain, Key = "retain" };

    private static DefensePiece.Priced Piece(double work, long wood) => new DefensePiece.Priced(work, [new Amount(Wood, wood)]);

    /// <summary>An open sandbag line at z 52 (x 21 to 23), each bag 600 ticks and 5 wood.</summary>
    private static readonly DefenseCandidate Cover = new()
    {
        Kind = DefenseCandidateKind.TemporaryCover,
        Key = "temporary_cover@21,52",
        Structures = [.. new[] { 21, 22, 23 }.Select(x => new DefenseStructure("Sandbags", new Cell(x, 52), "", false) { Piece = Piece(600, 5) })],
        Firing = [new Cell(21, 53), new Cell(23, 53)],
    };

    /// <summary>A doored perimeter at z 52 (x 20 to 24), each piece 1800 ticks and 15 wood: five times the line's work and wood.</summary>
    private static readonly DefenseCandidate Perimeter = new()
    {
        Kind = DefenseCandidateKind.PerimeterDoored,
        Key = "perimeter_doored@20,52",
        Structures =
        [
            .. new[] { 20, 21, 23, 24 }.Select(x => new DefenseStructure("Wall", new Cell(x, 52), "WoodLog", true) { Piece = Piece(1800, 15) }),
            new DefenseStructure("Door", new Cell(22, 52), "WoodLog", false) { Piece = Piece(1800, 15) },
        ],
        Firing = [new Cell(22, 52)],
    };

    private static readonly DefenseCandidate Retreat = new() { Kind = DefenseCandidateKind.Retreat, Key = "retreat@10,10", Firing = [new Cell(10, 10)] };

    private static readonly DefenseCandidate Turrets = new()
    {
        Kind = DefenseCandidateKind.Turrets,
        Key = "turrets@22,53",
        Structures = [new DefenseStructure("Turret_MiniTurret", new Cell(22, 53), "", true) { Piece = Piece(6000, 70) }],
    };

    private static DefenseProtection Guard(string candidate, double covered, int staffing = 1, int exposed = 0) => new()
    {
        Candidate = candidate,
        Covered = new DefenseTerm.Assessed(covered),
        Shots = new DefenseTerm.Assessed(covered),
        Cover = new DefenseTerm.Assessed(0),
        Staffing = new DefenseTerm.Assessed(staffing),
        Ammunition = new DefenseTerm.Assessed(0),
        Power = new DefenseTerm.Assessed(0),
        Exposure = new DefenseTerm.Assessed(exposed),
    };

    private static DefenseChoiceInputs Inputs(int builders = 1, DefenseHarm? harm = null, IReadOnlyDictionary<Cell, string>? standing = null) => new()
    {
        Harm = harm ?? new DefenseHarm.Read(Loss),
        Builders = new DefenseTerm.Assessed(builders),
        Materials = new MaterialAvailability.Assessed(new Dictionary<Resource, MaterialUnits> { [Wood] = new MaterialUnits.Exact(1000) }),
        Values = new Dictionary<Resource, ConstructionMaterialValue> { [Wood] = new(MaterialValueBasis.Labor, 0.01) },
        Standing = standing ?? new Dictionary<Cell, string>(),
    };

    private static double Days(DefenseChoiceRow row) => Assert.IsType<DefensePrice.Assessed>(row.Price).Days;

    private static double Value(DefenseTerm t) => Assert.IsType<DefenseTerm.Assessed>(t).Value;

    private static DefenseChoiceRow Row(DefenseDecision d, DefenseCandidate c) => d.Rows.Single(r => r.Key == c.Key);

    // (a) A sandbag line and a doored perimeter cover the approach alike; the line costs a fifth of the perimeter's
    // construction, so it wins. With the line's coverage unassessed (fogged sight), the perimeter wins instead.
    [Fact]
    public void ASandbagLineBeatsADooredPerimeterAtTheSameProtectionUntilItsSightIsFogged()
    {
        DefenseAlternative[] alternatives =
        [
            new(Retain, Guard("retain", 0, 0), null),
            new(Cover, Guard(Cover.Key, 0.8, 2), Guard(Cover.Key + DefenseChoiceRules.StandingSuffix, 0, 2)),
            new(Perimeter, Guard(Perimeter.Key, 0.8, 1), Guard(Perimeter.Key + DefenseChoiceRules.StandingSuffix, 0, 1)),
        ];
        var d = DefenseChoiceRules.Choose(alternatives, Inputs());
        Assert.Equal(DefenseCandidateKind.TemporaryCover, d.Kind);
        Assert.Equal(Cover.Key, d.Reason);
        Assert.Equal(5 * Value(Row(d, Cover).Construction), Value(Row(d, Perimeter).Construction), 9);
        Assert.True(Days(Row(d, Cover)) < Days(Row(d, Perimeter)));
        Assert.Equal(60.0, Days(Row(d, Retain)), 9);

        var fogged = alternatives[1] with { Protection = alternatives[1].Protection with { Covered = new DefenseTerm.Unassessed(DefenseProtectionRules.Fogged) } };
        var again = DefenseChoiceRules.Choose([alternatives[0], fogged, alternatives[2]], Inputs());
        Assert.Equal(DefenseCandidateKind.PerimeterDoored, again.Kind);
        Assert.Equal(new DefensePrice.Unassessed(DefenseProtectionRules.Fogged), Row(again, Cover).Price);
    }

    // (b) With the staffing fact unread, every firing line is unassessed; the retreat behind a standing door is priced and wins.
    [Fact]
    public void RetreatWinsWhenEveryFiringLineLeavesItsStaffingUnread()
    {
        var unstaffed = new DefenseTerm.Unassessed(DefenseProtectionRules.ProbeUnread);
        DefenseAlternative[] alternatives =
        [
            new(Retain, Guard("retain", 0, 0), null),
            new(Retreat, Guard(Retreat.Key, 0.4, 1), null),
            new(Cover, Guard(Cover.Key, 0.9) with { Staffing = unstaffed }, null),
            new(Perimeter, Guard(Perimeter.Key, 0.9) with { Staffing = unstaffed }, null),
        ];
        var d = DefenseChoiceRules.Choose(alternatives, Inputs());
        Assert.Equal(DefenseCandidateKind.Retreat, d.Kind);
        Assert.Equal(new DefensePrice.Unassessed(DefenseProtectionRules.ProbeUnread), Row(d, Cover).Price);
        Assert.Equal(new DefensePrice.Unassessed(DefenseProtectionRules.ProbeUnread), Row(d, Perimeter).Price);
        Assert.Equal((1 - 0.4) * Loss * 60, Days(Row(d, Retreat)), 9);
        Assert.Equal(0, d.Deficit);
    }

    // An understaffed line is priced on what its staffed seats cover (staffing is an assessed count of the able shooters, the
    // shortfall a named state), not left unassessed: it beats the retreat when the staffed seats still cover more.
    [Fact]
    public void AnUnderstaffedLineIsPricedOnItsStaffedSeatsCoverage()
    {
        var short1 = Guard(Cover.Key, 0.9, 1) with { States = ["staffing:2/1"] };
        DefenseAlternative[] alternatives =
        [
            new(Retain, Guard("retain", 0, 0), null),
            new(Retreat, Guard(Retreat.Key, 0.4, 1), null),
            new(Cover, short1, Guard(Cover.Key + DefenseChoiceRules.StandingSuffix, 0, 1)),
        ];
        var d = DefenseChoiceRules.Choose(alternatives, Inputs());
        var row = Row(d, Cover);
        Assert.IsType<DefensePrice.Assessed>(row.Price);
        Assert.Equal(1, Value(row.Alternative.Protection.Staffing));
        Assert.Contains("staffing:2/1", row.Alternative.Protection.States);
        Assert.Equal(DefenseCandidateKind.TemporaryCover, d.Kind);
        Assert.Equal(Value(row.Construction) + (0.1 * Loss * (60 - Value(row.LeadDays))) + (Loss * Value(row.LeadDays)), Days(row), 9);
    }

    // Rows of an equal assessed price are ordered by more shots, then more cover, then the generator's order.
    [Fact]
    public void EqualPricesOrderByMoreShotsThenMoreCover()
    {
        DefenseAlternative Twin(string key, double shots, double cover) =>
            new(Cover with { Key = key }, Guard(key, 0.8, 2) with { Shots = new DefenseTerm.Assessed(shots), Cover = new DefenseTerm.Assessed(cover) },
                Guard(key + DefenseChoiceRules.StandingSuffix, 0, 2));
        var baseline = new DefenseAlternative(Retain, Guard("retain", 0, 0), null);

        var byShots = DefenseChoiceRules.Choose([baseline, Twin("a", 1, 0.5), Twin("b", 2, 0.1)], Inputs());
        Assert.Equal("b", byShots.Chosen.Key);
        var byCover = DefenseChoiceRules.Choose([baseline, Twin("a", 2, 0.1), Twin("b", 2, 0.5)], Inputs());
        Assert.Equal("b", byCover.Chosen.Key);
        var first = DefenseChoiceRules.Choose([baseline, Twin("a", 2, 0.5), Twin("b", 2, 0.5)], Inputs());
        Assert.Equal("a", first.Chosen.Key);
    }

    // A bound choice whose pieces are not all built pays its routes as the same candidate does as an alternative.
    [Fact]
    public void ABoundUnbuiltBaselinePaysItsRoutesLikeAnAlternative()
    {
        var route = new DefenseRouteChange(DefenseCandidateRules.RouteMapEdge, new DefenseRouteEffect.Longer(2));
        var trips = new Dictionary<string, double> { [DefenseCandidateRules.RouteMapEdge] = 6 };
        var inputs = Inputs() with { Trips = trips };
        var expected = 2 * FieldPlacementRules.WalkTicksPerCell * 6 / GameTime.TicksPerDay * ColonyModelPolicy.DefaultHorizonDays;

        var bound = Cover with { Kind = DefenseCandidateKind.Retain, Key = "retain", Routes = [route] };
        var retained = DefenseChoiceRules.Choose([new(bound, Guard("retain", 0.8, 2), Guard("retain" + DefenseChoiceRules.StandingSuffix, 0, 2))], inputs);
        var row = Row(retained, bound);
        Assert.Equal(expected, Value(row.Routes), 9);

        var alt = Cover with { Routes = [route] };
        var asAlternative = DefenseChoiceRules.Choose(
            [new(Retain, Guard("retain", 0, 0), null), new(alt, Guard(alt.Key, 0.8, 2), Guard(alt.Key + DefenseChoiceRules.StandingSuffix, 0, 2))], inputs);
        var other = Row(asAlternative, alt);
        Assert.Equal(Value(other.Routes), Value(row.Routes), 9);
        Assert.Equal(Days(other), Days(row), 9);

        var unprobed = bound with { Routes = [new DefenseRouteChange(DefenseCandidateRules.RouteMapEdge, new DefenseRouteEffect.Unprobed())] };
        var blind = DefenseChoiceRules.Choose([new(unprobed, Guard("retain", 0.8, 2), Guard("retain" + DefenseChoiceRules.StandingSuffix, 0, 2))], inputs);
        Assert.Equal(new DefensePrice.Unassessed(DefenseCandidateRules.RouteMapEdge + ":unprobed"), Row(blind, unprobed).Price);
    }

    // (c) A perimeter laid over a standing killbox's traps replaces them: while it is built the killbox's protection is lost,
    // its downtime the exposure times that protection times the build window. With the threat timing unread the downtime is
    // unassessed and the killbox is retained.
    [Fact]
    public void ReplacingAKillboxWithAPerimeterCarriesItsDowntimeAndUnreadTimingRetains()
    {
        var traps = new Dictionary<Cell, string> { [new Cell(21, 52)] = "TrapSpike", [new Cell(23, 52)] = "TrapSpike" };
        DefenseAlternative[] alternatives =
        [
            new(Retain with { Kind = DefenseCandidateKind.Retain }, Guard("retain", 0.6), null),
            new(Perimeter, Guard(Perimeter.Key, 0.95), null),
        ];
        var d = DefenseChoiceRules.Choose(alternatives, Inputs(standing: traps));
        var row = Row(d, Perimeter);
        Assert.Equal(["TrapSpike@21,52", "TrapSpike@23,52"], row.Removes);
        var window = 5 * 1800.0 / GameTime.TicksPerDay;
        Assert.Equal(window, Value(row.LeadDays), 9);
        Assert.Equal(0.6 * Loss * window, Value(row.Downtime), 9);

        var unread = DefenseChoiceRules.Choose(alternatives, Inputs(standing: traps, harm: new DefenseHarm.Unread("threat_timing:storyteller")));
        Assert.Equal(new DefenseTerm.Unassessed("threat_timing:storyteller"), Row(unread, Perimeter).Downtime);
        Assert.Equal(DefenseCandidateKind.Retain, unread.Kind);
        Assert.Equal("threat_timing:storyteller", unread.Reason);
    }

    // (d) A perimeter over standing sandbags removes them: the price carries the protection they gave over the build window,
    // which the same perimeter on open ground does not pay.
    [Fact]
    public void RemovingStandingSandbagsIsChargedTheirProtection()
    {
        var bags = new Dictionary<Cell, string> { [new Cell(21, 52)] = "Sandbags", [new Cell(23, 52)] = "Sandbags" };
        DefenseAlternative[] alternatives = [new(Retain, Guard("retain", 0.75), null), new(Perimeter, Guard(Perimeter.Key, 0.9), null)];
        var over = Row(DefenseChoiceRules.Choose(alternatives, Inputs(standing: bags)), Perimeter);
        var open = Row(DefenseChoiceRules.Choose(alternatives, Inputs()), Perimeter);
        Assert.Empty(open.Removes);
        Assert.Equal(0, Value(open.Downtime));
        var window = Value(over.LeadDays);
        Assert.True(window > 0);
        Assert.Equal(0.75 * Loss * window, Value(over.Downtime), 9);
        Assert.Equal(Days(open) + Value(over.Downtime), Days(over), 9);
    }

    private static WorkPawn Builder(string id) => PawnFixtures.Worker(id) with
    {
        Work = PawnFixtures.WorkTable(true, [WorkPriority.Planned(WorkType.Construction, 1) with { Skill = "Construction", Order = 5 }]),
        Observed = PawnFixtures.Row(id) with
        {
            Biography = PawnFixtures.Row(id).Biography with { Skills = [new() { DefName = "Construction", Level = 8, Passion = Passion.None }] },
        },
    };

    // (e) A builder a hospital build claims is not free: the line's lead over the one builder left is twice its lead over two.
    // A turret drawing on a short network carries that network's unserved share (6.4's UnservedShare).
    [Fact]
    public void ClaimedBuildersLengthenTheLeadAndAShortNetworkInterruptsATurret()
    {
        var pawns = new WorkPawnsRead([Builder("a"), Builder("b")]);
        Assert.Equal(2, DefenseChoiceRules.FreeBuilders(pawns, new HashSet<string>()));
        var free = DefenseChoiceRules.FreeBuilders(pawns, new HashSet<string> { "b" });
        Assert.Equal(1, free);
        DefenseAlternative[] alternatives = [new(Retain, Guard("retain", 0), null), new(Cover, Guard(Cover.Key, 0.8, 2), null)];
        var two = Row(DefenseChoiceRules.Choose(alternatives, Inputs(builders: 2)), Cover);
        var one = Row(DefenseChoiceRules.Choose(alternatives, Inputs(builders: free)), Cover);
        Assert.Equal(2 * Value(two.LeadDays), Value(one.LeadDays), 9);
        Assert.True(Days(one) > Days(two));
        Assert.Equal(new DefensePrice.Excluded(DefenseChoiceRules.NoBuilders), Row(DefenseChoiceRules.Choose(alternatives, Inputs(builders: 0)), Cover).Price);

        const string Generator = "WoodFiredGenerator";
        var inputs = Inputs() with
        {
            Power = new PowerTopology
            {
                Buildings = [PowerBuildingFixtures.Trader("gen", Generator, new(30, 30), 100, 100, true), PowerBuildingFixtures.Trader("lamp", "StandingLamp", new(31, 30), -50, 0, true)],
            },
            Sources = new Dictionary<string, PowerSourceProfile> { [Generator] = PowerRules.ConstantPowerProfile },
            Battery = new PowerBattery(600, 0.5),
        };
        var turret = new DefenseAlternative(Turrets, Guard(Turrets.Key, 0.9) with { Draws = [new DefenseDraw("net", 200)] }, null);
        var row = Row(DefenseChoiceRules.Choose([alternatives[0], turret], inputs), Turrets);
        var share = UtilityChoiceRules.UnservedShare([new PowerProducer { NominalW = 100, Profile = PowerRules.ConstantPowerProfile }], 0, 0, 250, 0.5, false);
        Assert.Equal(0.6, share, 9);
        Assert.Equal(share, Value(row.Interruption), 9);
        var unplugged = Row(DefenseChoiceRules.Choose([alternatives[0], turret with { Protection = turret.Protection with { Draws = [] } }], inputs), Turrets);
        Assert.True(Days(row) > Days(unplugged));
    }

    // (f) The deficit is the coverage the chosen line owes beyond what of it the probe of the standing structures reads. An
    // accepted build order adds no standing piece, so the deficit holds; once the bags stand it is gone. A turret standing
    // unpowered still owes its coverage though nothing is left to build.
    [Fact]
    public void AnAcceptedBuildLeavesTheDeficitUntilThePieceStandsAndDraws()
    {
        DefenseAlternative Line(double stood) => new(Cover, Guard(Cover.Key, 0.8, 2), Guard(Cover.Key + DefenseChoiceRules.StandingSuffix, stood, 2));
        var before = DefenseChoiceRules.Choose([new(Retain, Guard("retain", 0), null), Line(0)], Inputs());
        Assert.Equal(DefenseCandidateKind.TemporaryCover, before.Kind);
        Assert.Equal(0.8, before.Deficit, 9);
        Assert.Equal(3, before.Chosen.Unbuilt.Count);

        // Accepted: the blueprints are no standing building and the probe of what stands reads what it read before.
        var accepted = DefenseChoiceRules.Choose([new(Retain, Guard("retain", 0), null), Line(0)], Inputs());
        Assert.Equal(before.Deficit, accepted.Deficit);

        var bags = Cover.Structures.ToDictionary(s => s.Cell, s => s.Definition);
        var stood = DefenseChoiceRules.Choose([new(Retain, Guard("retain", 0), null), Line(0.8)], Inputs(standing: bags));
        Assert.Equal(DefenseCandidateKind.TemporaryCover, stood.Kind);
        Assert.Empty(stood.Chosen.Unbuilt);
        Assert.Equal(0, stood.Deficit);

        var gun = new Dictionary<Cell, string> { [new Cell(22, 53)] = "Turret_MiniTurret" };
        var dark = DefenseChoiceRules.Choose(
            [new(Retain, Guard("retain", 0), null), new(Turrets, Guard(Turrets.Key, 0.9), Guard(Turrets.Key + DefenseChoiceRules.StandingSuffix, 0) with { States = ["power:turret"] })],
            Inputs(standing: gun));
        Assert.Equal(DefenseCandidateKind.Turrets, dark.Kind);
        Assert.Empty(dark.Chosen.Unbuilt);
        Assert.Equal(0.9, dark.Deficit, 9);

        // The standing probe rows ride beside the candidates' and pair back by key.
        var shadows = DefenseChoiceRules.Shadows([Retain, Cover]);
        Assert.Equal([Cover.Key + DefenseChoiceRules.StandingSuffix], shadows.Select(s => s.Key));
        Assert.Empty(shadows[0].Structures);
        var paired = DefenseChoiceRules.Alternatives([Retain, Cover], [Guard("retain", 0), Guard(Cover.Key, 0.8), Guard(shadows[0].Key, 0.1)]);
        Assert.Null(paired[0].Standing);
        Assert.Equal(new DefenseTerm.Assessed(0.1), paired[1].Standing!.Covered);
    }

    // (g) An alternative that costs exactly what retaining does leaves the plan's defense in place.
    [Fact]
    public void RetainKeepsTies()
    {
        var d = DefenseChoiceRules.Choose([new(Retain, Guard("retain", 0.5), null), new(Retreat, Guard(Retreat.Key, 0.5), null)], Inputs());
        Assert.Equal(DefenseCandidateKind.Retain, d.Kind);
        Assert.Equal("retain below every alternative", d.Reason);
        Assert.Equal(Days(Row(d, Retain)), Days(Row(d, Retreat)), 9);
        Assert.Equal(0, d.Deficit);
    }

    /// <summary>A workshop (interior x 20 to 25, z 20 to 24) whose door opens north onto the entrance at 22,26, on a 40 by 40 open map; its rooms walk outside three times a day.</summary>
    private static LayoutPlan Workshop()
    {
        var plan = new LayoutPlan
        {
            Rooms = [new PlannedRoom { Role = PlannedRole.Workshop, Region = RegionRules.RectRegion(new Rectangle(20, 20, 6, 5)), Door = new Cell(22, 25), DoorSide = new DoorSide.Doored(Rotation.North) }],
            Entrances = [new Cell(22, 26)],
        }.WithRoomIDs();
        return plan with { OutsideTrips = [new OutsideTrip(plan.Rooms[0].Id, 3)] };
    }

    private static MapSurvey OpenMap() => new()
    {
        Bounds = new Bounds(40, 40),
        Cells = [.. Enumerable.Range(0, 40 * 40).Select(i => new SurveyCell { Cell = new Cell(i % 40, i / 40), Walkable = true })],
    };

    /// <summary>The review's coupled context at one raid a day: 1000 raid points against 990 of defense (SparringCreditTests' exposure).</summary>
    private static ForecastCoupledContext RaidContext() =>
        PlannedRoomCreditTests.Context(PlannedRoomCredits.Shipped) with
        {
            Needs = NeedsFixtures.Empty with { Pawns = [ForecastValueTests.HarmPawn()] },
            CapacityPenalties = ForecastValueTests.HarmTables(),
            Harm = ForecastValueTests.RaidDay(new ExposureTable(RaidExposure.Schema,
                [new ExposureCell("1-2", ExposureRoles.Unarmed, 100, 5, 3, 20, 0.5, 0.4, 0.6, 100, 100)])) with { Raid = new RaidDefense(1000, 990) },
            Outfits = new Dictionary<string, EquatableArray<GearOption>> { ["pawn"] = ForecastValueTests.Cloth },
        };

    // (h) The step's reading as the game gives it: the review's coupled context prices the raid harm, the tiled plan's
    // outside trips give the walks a day, the map survey probes the entrance's walk to the map edge, and the threat timing
    // is read. A sandbag line in front of the entrance narrows no walk, covers the approach cheaply and wins; a line of
    // impassable pieces makes the walk to the edge detour and is charged it at the plan's trips. Without the coupled
    // context, the threat timing or the survey, the comparison retains, naming what it lacks.
    [Fact]
    public void ATemporaryLineWinsOnTheGameShapedReadingAndRetainsWhileAnyOfItIsUnread()
    {
        var plan = Workshop();
        var ground = DefenseGround.Of(plan);
        DefenseCandidate Line(bool impassable) => DefenseCandidateRules.Constrain(Cover with
        {
            Key = "temporary_cover@21,28",
            Structures = [.. Enumerable.Range(21, 3).Select(x => new DefenseStructure("Sandbags", new Cell(x, 28), "", impassable) { Piece = Piece(600, 5) })],
            Firing = [new Cell(21, 29), new Cell(23, 29)],
        }, ground, plan, OpenMap());

        var bags = Line(false);
        Assert.IsType<DefenseCandidateStanding.Generated>(bags.Standing);
        Assert.Empty(bags.Routes);
        var wall = Line(true);
        Assert.Equal([new DefenseRouteChange(DefenseCandidateRules.RouteMapEdge, new DefenseRouteEffect.Longer(2))], wall.Routes);

        var trips = DefenseChoiceRules.TripsOf(plan);
        Assert.Equal(6, trips[DefenseCandidateRules.RouteMapEdge], 9);
        Assert.Equal(0, trips[DefenseCandidateRules.RouteName(PlannedRole.None, LayoutSurveyRules.PlannedStorage)], 9);

        var harm = Assert.IsType<DefenseHarm.Read>(DefenseChoiceRules.HarmOf(RaidContext()));
        Assert.True(harm.LossPerDay > 0, "a raid a day at the 1-2 bin costs the colony");

        DefenseChoiceInputs Read(DefenseHarm h) => Inputs(harm: h) with { Trips = trips };
        DefenseAlternative[] Alternatives(DefenseCandidate line) =>
            [new(Retain, Guard("retain", 0, 0), null), new(line, Guard(line.Key, 0.8, 2), Guard(line.Key + DefenseChoiceRules.StandingSuffix, 0, 2))];

        var d = DefenseChoiceRules.Choose(Alternatives(bags), Read(harm));
        Assert.Equal(DefenseCandidateKind.TemporaryCover, d.Kind);
        Assert.Equal(0.8, d.Deficit, 9);
        Assert.Equal(0, Value(Row(d, bags).Routes));

        var walled = DefenseChoiceRules.Choose(Alternatives(wall), Read(harm));
        Assert.Equal(2 * FieldPlacementRules.WalkTicksPerCell * 6 / GameTime.TicksPerDay * ColonyModelPolicy.DefaultHorizonDays, Value(Row(walled, wall).Routes), 9);

        // No coupled context in the review: the harm is unread and the plan's defense is retained.
        var none = DefenseChoiceRules.HarmOf((ForecastCoupledContext?)null);
        Assert.Equal(new DefenseHarm.Unread(DefenseChoiceRules.CoupledContextUnread), none);
        var blind = DefenseChoiceRules.Choose(Alternatives(bags), Read(none));
        Assert.Equal(DefenseCandidateKind.Retain, blind.Kind);
        Assert.Equal(DefenseChoiceRules.CoupledContextUnread, blind.Reason);

        // Unread threat timing retains the same way.
        var untimed = Assert.IsType<DefenseHarm.Unread>(DefenseChoiceRules.HarmOf(new ReviewFacts()));
        Assert.StartsWith("threat_timing:", untimed.Fact, StringComparison.Ordinal);
        Assert.Equal(DefenseCandidateKind.Retain, DefenseChoiceRules.Choose(Alternatives(bags), Read(untimed)).Kind);

        // No survey: the walk to the map edge is unprobed and the line cannot be priced.
        var unprobed = DefenseCandidateRules.Constrain(bags with { Routes = [] }, ground, plan);
        Assert.Equal([new DefenseRouteChange(DefenseCandidateRules.RouteMapEdge, new DefenseRouteEffect.Unprobed())], unprobed.Routes);
        Assert.Equal(DefenseCandidateKind.Retain, DefenseChoiceRules.Choose(Alternatives(unprobed), Read(harm)).Kind);

        // An untiled plan carries no trip frequencies: a lengthened walk is unpriced.
        Assert.Empty(DefenseChoiceRules.TripsOf(plan with { OutsideTrips = [] }));
        var untripped = DefenseChoiceRules.Choose(Alternatives(wall), Inputs(harm: harm));
        Assert.Equal(new DefensePrice.Unassessed("route_trips:" + DefenseCandidateRules.RouteMapEdge), Row(untripped, wall).Price);
        Assert.Equal(DefenseCandidateKind.Retain, untripped.Kind);
    }
}
