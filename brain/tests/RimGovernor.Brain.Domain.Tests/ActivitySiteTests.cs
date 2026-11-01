using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Store.Codec;
using Xunit;
using ActivityAccess = RimGovernor.Brain.GameFacts.Observations.ActivityAccess;
using ActivityRoom = RimGovernor.Brain.GameFacts.Observations.ActivityRoom;
using ComfortHost = RimGovernor.Brain.GameFacts.Observations.ComfortHost;
using DiningSeat = RimGovernor.Brain.GameFacts.Observations.DiningSeat;
using Engagement = RimGovernor.Brain.GameFacts.Observations.Engagement;
using RecreationSource = RimGovernor.Brain.GameFacts.Observations.RecreationSource;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Habitat 3.3: party, marriage and ritual sites are derived from facts, and where each stands (a spot in an existing
/// room, a new site room, or none) is a deviation the shared layout comparator weighs in its one batch, by the canonical
/// ledger's activity-site term (ForecastActivitySites: attendance from access and capacity, ritual surroundings,
/// competing uses). The placement step binds the adopted plan's site. A site has no retire/upgrade lifecycle, and an
/// activity's completion is not its site's provision.
/// </summary>
public sealed class ActivitySiteTests
{
    private static readonly PawnId A = new("a"), B = new("b"), C = new("c"), D = new("d");
    private static readonly EquatableArray<PawnId> Four = [A, B, C, D];
    private static readonly ActivitySiteDefs Defs = ComfortTests.TestSiteDefs;
    private const double Day = GameTime.TicksPerDay;

    private static ActivityAccess Access(EquatableArray<PawnId> colonists, params ActivityRoom[] rooms) =>
        new() { Colonists = colonists, Rooms = EquatableArray.From(rooms), Engaged = [] };

    private static ActivityDemand Party() =>
        Assert.Single(ActivitySiteRules.Demands(Access(Four), Defs, new WorshipFacts()));

    private static readonly RitualOutcomeModel Model = new()
    {
        StartingQuality = 0.2, MinQuality = 0, MaxQuality = 1,
        Comps = [new RitualQualityComp(RitualQualityInput.Impressiveness, [new CurvePoint(0, 0), new CurvePoint(100, 0.6)])],
        Chances = [new RitualOutcomeChance(1, true, 10, 3), new RitualOutcomeChance(1, false, -1, 3)],
    };

    private static readonly RitualDef Ritual = new() { IntervalDaysMin = 10, RequiredBuildings = ["TestRitualSpot"], CanStartAnytime = true, Outcome = Model };

    private static (RoomState Room, Cell[] Cells) Room(string id, int x, RoomRole role = RoomRole.Room) =>
        (RoomFixtures.State(id, role), [new Cell(x, 1), new Cell(x + 1, 1)]);

    private static ActivitySiteAsk Ask(ActivityDemand demand, (RoomState Room, Cell[] Cells)[] rooms, ActivityRoom[] access,
        CurrentBuilding[]? built = null, ConstructionSite[]? sites = null, IReadOnlySet<string>? spent = null, LayoutPlan? plan = null) =>
        ActivitySiteRules.Ask([demand], Census(rooms), Construction(built, sites),
            [.. rooms.SelectMany(r => r.Cells).Select(c => SiteCellFixtures.At(c, roof: "RoofConstructed", indoors: true))],
            ComfortTests.NoComfort with { Activities = Access(demand.Participants, access) }, plan, _ => true, _ => spent ?? new HashSet<string>());

    private static RoomObservation Census((RoomState Room, Cell[] Cells)[] rooms) =>
        RoomFixtures.Census([.. rooms.Select(r => (r.Room, (IEnumerable<Cell>)r.Cells))]);

    private static CurrentConstruction Construction(CurrentBuilding[]? built = null, ConstructionSite[]? sites = null) =>
        new() { Buildings = EquatableArray.From(built ?? []), Sites = EquatableArray.From(sites ?? []), Deficit = new Dictionary<Resource, long>() };

    [Fact]
    public void EachKindsDemandIsDerivedFromFactsAndTheRitualIsAbsentWithoutIdeology()
    {
        Assert.Empty(ActivitySiteRules.Demands(Access([A, B, C]), Defs, new WorshipFacts()));
        var party = Party();
        Assert.Equal((ActivityKind.Party, 3, "TestPartySpot"), (party.Kind, party.Minimum, party.Place));
        Assert.Equal(["TestGatherSpot", "TestPartySpot"], party.Accepted);
        var engaged = Access([A, B]) with { Engaged = [new Engagement(A, B)] };
        var marriage = Assert.Single(ActivitySiteRules.Demands(engaged, Defs, new WorshipFacts()));
        Assert.Equal((ActivityKind.Marriage, 2, "TestMarriageSpot"), (marriage.Kind, marriage.Minimum, marriage.Place));
        var ritual = Assert.Single(ActivitySiteRules.Demands(Access([A]), Defs, new WorshipFacts { Believers = [A], Rituals = [Ritual] }));
        Assert.Equal((ActivityKind.Ritual, "TestRitualSpot"), (ritual.Kind, ritual.Place));
        Assert.Empty(ActivitySiteRules.Demands(Access([A]), Defs, WorshipRules.FactsOf(new IdeologyMode.NotInstalled())));
        Assert.Empty(ActivitySiteRules.Demands(Access([A]), Defs, new WorshipFacts { Believers = [A], Rituals = [Ritual with { Outcome = null }] }));
    }

    [Fact]
    public void TheAskOffersEachEligibleRoomANewRoomAndNoneBeyondTheTrialBound()
    {
        var ask = Ask(Party(), [Room("den", 5), Room("hall", 1)], [new("den", 10, [A]), new("hall", 10, Four)]);
        var party = Assert.Single(ask.Kinds);
        // The room most participants reach is tried first; the new site room holds every participant and the spot.
        Assert.Equal(["hall", "den"], party.Rooms.Cast<ActivityPlacement.InRoom>().Select(r => r.Room));
        Assert.Equal(new ActivityPlacement.InRoom(ActivityKind.Party, "TestPartySpot", "hall", new Cell(1, 1)), party.Rooms[0]);
        Assert.Equal(new ActivityPlacement.NewRoom(ActivityKind.Party, "TestPartySpot", 16), Assert.Single(party.NewRoom));
        Assert.Equal([ActivityAlternative.PlaceInRoom, ActivityAlternative.PlaceInRoom, ActivityAlternative.NewRoom],
            ActivitySiteRules.Deviations(ask).Select(d => d.Deviation.Alternative));
        // A spent room is never offered again; an unread party spot list offers nothing and its state names the fact.
        var spent = Assert.Single(Ask(Party(), [Room("den", 5), Room("hall", 1)], [new("den", 10, [A]), new("hall", 10, Four)], spent: new HashSet<string>(["hall"])).Kinds);
        Assert.Equal(["den"], spent.Rooms.Cast<ActivityPlacement.InRoom>().Select(r => r.Room));
        var unread = Assert.Single(ActivitySiteRules.Demands(Access(Four), Defs with { PartySpots = new SiteDefsRead.Unread("gathering_def:Party") }, new WorshipFacts()));
        Assert.Empty(ActivitySiteRules.Deviations(Ask(unread, [Room("hall", 1)], [new("hall", 10, Four)])));
        Assert.Equal(new ActivitySiteState.Unplanned(ActivityKind.Party, "gathering_def:Party"),
            Assert.Single(ActivitySiteRules.States([unread], Construction(), Census([Room("hall", 1)]), new LayoutPlan(), _ => new HashSet<string>())));
        // Rooms past the bound are one truncation.
        var rooms = Enumerable.Range(0, ActivitySiteRules.MaxRoomTrials + 2).Select(i => Room("r" + i, i * 3)).ToArray();
        var bounded = Ask(Party(), rooms, [.. rooms.Select(r => new ActivityRoom(r.Room.Id, 10, Four))]);
        Assert.Equal(ActivitySiteRules.MaxRoomTrials, Assert.Single(bounded.Kinds).Rooms.Count);
        Assert.Equal(new SiteTruncation(ActivityKind.Party, 2), Assert.Single(ActivitySiteRules.Truncations(bounded)));
    }

    [Fact]
    public void AStandingOrInFlightSiteIsNeverDeviatedFrom()
    {
        var spot = ConstructionObservationFixtures.Built(new Building("TestPartySpot", new Cell(1, 1), Rotation.North, ""), id: "spot", cells: [new Cell(1, 1)]);
        var kept = Assert.Single(Ask(Party(), [Room("hall", 1), Room("den", 5)], [new("hall", 10, Four), new("den", 10, Four)], built: [spot]).Kinds);
        Assert.Equal(new ActivityPlacement.Standing(ActivityKind.Party, "TestPartySpot", "hall", new Cell(1, 1)), Assert.Single(kept.Standing));
        Assert.DoesNotContain(kept.Rooms, r => ((ActivityPlacement.InRoom)r).Room == "hall");
        var bp = ConstructionObservationFixtures.Site(new Building("TestPartySpot", new Cell(1, 1), Rotation.North, ""), id: "bp");
        var flight = Ask(Party(), [Room("hall", 1)], [new("hall", 10, Four)], sites: [bp]);
        Assert.True(Assert.Single(flight.Kinds).Building);
        Assert.Empty(ActivitySiteRules.Deviations(flight));
    }

    private static StationFacts Prepared(StationFacts f) => LayoutDeriveRules.FirstPlanFacts(f.Survey, f, []);

    private static readonly ActivityDemand OnePawn = new()
    {
        Kind = ActivityKind.Party, Participants = [new PawnId("pawn")], Minimum = 1, Accepted = ["TestPartySpot"], Place = "TestPartySpot",
    };

    private static readonly ActivitySiteAsk PartyAsk = new()
    {
        Kinds =
        [
            new ActivitySiteOptions
            {
                Demand = OnePawn,
                Rooms = [new ActivityPlacement.InRoom(ActivityKind.Party, "TestPartySpot", "hall", new Cell(1, 1))],
                Untried = 2,
                NewRoom = [new ActivityPlacement.NewRoom(ActivityKind.Party, "TestPartySpot", 16)],
            },
        ],
    };

    private static HostedRegroupInputs Inputs()
    {
        var f = Prepared(FirstRoomPlanTests.Facts(BedMergePriceTests.Temperate, "mild")) with { ActivitySites = PartyAsk };
        var inputs = LayoutStationPlanRules.HostedRegroupInputsOf(f);
        return inputs with { Alternatives = [.. inputs.Alternatives.Select(h => h with { Input = h.Input with { Price = new() { Work = new(8000, LayoutRegroupRules.CellWalkDays(60)) } } })] };
    }

    private static ForecastConsequenceVector Vector(double investment)
    {
        var ledger = new ForecastValueResult(new LedgerTerm.Priced(investment), [])
        {
            Hunger = new LedgerTerm.Priced(0), Rest = new LedgerTerm.Priced(0), FoodPoisoning = new LedgerTerm.Priced(0), Thermal = new LedgerTerm.Priced(0),
            MoodBreaks = new LedgerTerm.Priced(0), Wellbeing = new LedgerTerm.Priced(0), Harm = new LedgerTerm.Priced(0), Investment = investment,
        };
        var value = new ForecastValueExplanation(ledger, new()) { UrgentZero = new(true, 0), BreakRisk = new(true, 0), Death = new(true, 0) };
        return new(EquatableArray.From(Enumerable.Repeat<ForecastTerm>(new ForecastTerm.Assessed(0d), 5).ToArray()), EquatableArray.From(Enumerable.Repeat<ForecastTerm>(new ForecastTerm.Assessed(0d), 6).ToArray()),
            new ForecastTerm.Assessed(0d), new ForecastTerm.Assessed(0d), 0, []) { Value = value };
    }

    /// <summary>The comparator over the inputs, each trial's value its sites' (<paramref name="value"/>), as the ledger's site term stands in.</summary>
    private static Task<Regrouping> Compare(HostedRegroupInputs inputs, Func<IReadOnlyList<ActivityPlacement>, double> value) =>
        LayoutRegroupRules.RegroupAsync(inputs, (candidates, _) => Task.FromResult<IReadOnlyList<RegroupValuation>>([.. candidates.Select(candidate =>
            new RegroupValuation.Evaluated(candidate.Id, Vector(value(candidate.Layout.Sites)), []))]), TestContext.Current.CancellationToken);

    [Fact]
    public void EachSiteAlternativeIsADeviationOfTheSharedRegroupBatch()
    {
        var inputs = Inputs();
        Assert.IsType<SiteDeviation.None>(inputs.Alternatives[0].Site);
        Assert.Empty(inputs.Alternatives[0].Input.Sites);
        var sites = inputs.Alternatives.Where(a => a.Site is SiteDeviation.Move).ToList();
        Assert.Equal(["site:party:place_in_room:hall", "site:party:new_room"], sites.Select(s => s.Host.Id));
        Assert.Equal([PartyAsk.Kinds[0].Rooms[0]], sites[0].Input.Sites);
        // The new site room is a room node of its own trial's clustering, keyed to its kind.
        Assert.Contains(sites[1].Input.Cluster.Graph.Rooms, n => n.Activity == "party" && n.Role == ActivitySiteRules.SiteRoomRole && n.Area >= 16);
        Assert.DoesNotContain(inputs.Alternatives[0].Input.Cluster.Graph.Rooms, n => n.Activity.Length != 0);
        Assert.Equal(new SiteTruncation(ActivityKind.Party, 2), Assert.Single(inputs.SitesOmitted));
    }

    [Fact]
    public async Task TheConsequenceDecidesTheSiteAndTheBaselineKeepsATie()
    {
        // The new site room's attendance is worth 50 capability-days; the hall's spot nothing: the new room wins.
        var won = await Compare(Inputs(), sites => sites.Any(s => s is ActivityPlacement.NewRoom) ? -50 : 0);
        Assert.IsType<ActivityPlacement.NewRoom>(Assert.Single(won.Sites));
        Assert.Contains(won.Clustering.Groups, g => g.Activity == "party");
        var decisions = won.Decisions.Where(d => d.Kind == ActivitySiteRules.SiteMove).ToList();
        Assert.Equal(LayoutRegroupRules.RegroupApplied, Assert.Single(decisions, d => d.B == "new_room").Verdict);
        Assert.Equal(LayoutRegroupRules.RegroupKept, Assert.Single(decisions, d => d.B == "place_in_room:hall").Verdict);
        // The rooms past the trial bound are one finding.
        var skipped = Assert.Single(decisions, d => d.Verdict == LayoutRegroupRules.RegroupSkipped);
        Assert.Equal(("party", "2 rooms", LayoutHostRules.HostTruncated), (skipped.A, skipped.B, skipped.Reason));

        // A spot that earns exactly what no spot earns ties, and the baseline keeps it.
        var tied = await Compare(Inputs(), sites => sites.Any(s => s is ActivityPlacement.NewRoom) ? 50 : 0);
        Assert.Empty(tied.Sites);
        Assert.All(tied.Decisions.Where(d => d.Kind == ActivitySiteRules.SiteMove && d.Verdict != LayoutRegroupRules.RegroupSkipped),
            d => Assert.Equal(LayoutRegroupRules.RegroupKept, d.Verdict));
        // A spot that earns more wins.
        var hall = await Compare(Inputs(), sites => sites.Any(s => s is ActivityPlacement.InRoom) ? -1 : 0);
        Assert.Equal("hall", Assert.IsType<ActivityPlacement.InRoom>(Assert.Single(hall.Sites)).Room);
    }

    private static ForecastCoupledContext SiteContext(PlannedActivitySite site, ActivityRoom access, AttendedThought? party = null) =>
        ForecastValueTests.Context(ForecastValueTests.Pawn()) with
        {
            LayoutRooms = new Dictionary<int, PlannedRoom> { [1] = new() { Id = 1, Role = PlannedRole.Rec, Activity = "party", Region = RegionRules.RectRegion(new Policy.Rectangle(0, 0, 4, 4)) } },
            InvestmentHorizonDays = 10,
            ActivitySites = new ActivitySiteContext
            {
                Demands = [OnePawn],
                Sites = [site],
                Access = [access],
                Party = party ?? new AttendedThought.Read(8, 1.5),
                Wedding = new AttendedThought.Read(40, 10),
            },
        };

    private static ForecastCoupledOutcome Served() =>
        new(new([], 0, 0), true, new Dictionary<string, ForecastNeedsResult>
        {
            ["pawn"] = new([], new(new("pawn", "", new ForecastCapacityWindows.Unread(), 0), []), [])
            {
                Mood = MoodResults.Flat(0d, [Day, 0, 0], [new(0.08 * 1.5 / 40, [0, 0, 0])]),
            },
        }, [])
        { ServedRooms = [new(1, "", 0)] };

    private static ForecastInvestmentContribution[] SiteRows(ForecastCoupledContext context) =>
        [.. ForecastValue.Of(Served(), context, Day).InvestmentContributions.Where(r => r.Key.StartsWith("activity_site/", StringComparison.Ordinal))];

    private static readonly PlannedActivitySite InHall = new() { Kind = ActivityKind.Party, Definition = "TestPartySpot", NativeRoom = "hall", Anchor = new Cell(1, 1) };

    private static readonly PlannedActivitySite InNewRoom = new() { Kind = ActivityKind.Party, Definition = "TestPartySpot", Room = 1, Anchor = new Cell(0, 0) };

    [Fact]
    public void ANewRoomEarnsTheAttendanceAnExistingRoomWithoutCapacityCannot()
    {
        // The hall stands no attendee: its spot earns nothing. The new room holds its interior less the spot and earns the
        // attendee's AttendedParty mood from its service day, priced through the shared mood gain.
        var full = new ActivityRoom("hall", 0, [new PawnId("pawn")]);
        Assert.Empty(SiteRows(SiteContext(InHall, full)));
        var row = Assert.Single(SiteRows(SiteContext(InNewRoom, full)));
        Assert.Equal(("pawn", "activity_site/party/planned1/pawn", 10.0), (row.Pawn, row.Key, row.Units));
        Assert.True(row.Value < 0);
        // A hall the attendee reaches with room to stand earns the same mood from now.
        var open = Assert.Single(SiteRows(SiteContext(InHall, full with { StandableCells = 10 })));
        Assert.Equal(row.Value, open.Value, 9);
        // Its attendance memory unread, the trial is unassessed naming the catalog row.
        Assert.Equal(ForecastActivitySites.PartyThought,
            Assert.Throws<RequiredFactException>(() => SiteRows(SiteContext(InNewRoom, full, new AttendedThought.Unread(ForecastActivitySites.PartyThought)))).Fact);
        // An unread existing room's reach names the room.
        Assert.Equal("activity_room_reach:hall", Assert.Throws<RequiredFactException>(() => SiteRows(SiteContext(InHall, full with { Room = "den" }))).Fact);
    }

    [Fact]
    public void TheAdoptedPlanCarriesItsSitesThroughTheStore()
    {
        var plan = new LayoutPlan
        {
            Rooms = [new PlannedRoom { Id = 1, Role = PlannedRole.Rec, Activity = "party", Region = RegionRules.RectRegion(new Policy.Rectangle(0, 0, 4, 4)) }],
            ActivitySites = [InNewRoom, InHall with { Standing = true }],
        };
        var back = StoreJson.Deserialize<LayoutPlan>(StoreJson.Serialize(plan));
        Assert.Equal(plan.ActivitySites, back.ActivitySites);
        Assert.Equal("party", Assert.Single(back.Rooms).Activity);
        // Tiling resolves a new site room's spot on its first free interior cell.
        Assert.Equal(new Cell(0, 0), Assert.Single(plan.WithActivitySites([new ActivityPlacement.NewRoom(ActivityKind.Party, "TestPartySpot", 16)]).ActivitySites).Anchor);
    }

    [Fact]
    public void TheStateReadsTheAdoptedPlanAndAStandingSite()
    {
        var rooms = Census([Room("hall", 1)]);
        var none = new HashSet<string>();
        Assert.Equal(new ActivitySiteState.Unplanned(ActivityKind.Party, ActivitySiteRules.LayoutPlanPending),
            Assert.Single(ActivitySiteRules.States([Party()], Construction(), rooms, null, _ => none)));
        Assert.Equal(new ActivitySiteState.Unplanned(ActivityKind.Party, ActivitySiteRules.Unplanned),
            Assert.Single(ActivitySiteRules.States([Party()], Construction(), rooms, new LayoutPlan(), _ => none)));
        var planned = new LayoutPlan { ActivitySites = [InHall] };
        Assert.Equal(new ActivitySiteState.Planned(ActivityKind.Party), Assert.Single(ActivitySiteRules.States([Party()], Construction(), rooms, planned, _ => none)));
        // A spent room is never retried; a standing spot meets provision.
        Assert.Equal(new ActivitySiteState.Unplanned(ActivityKind.Party, ActivitySiteRules.SpentRoom),
            Assert.Single(ActivitySiteRules.States([Party()], Construction(), rooms, planned, _ => new HashSet<string>(["hall"]))));
        var spot = ConstructionObservationFixtures.Built(new Building("TestPartySpot", new Cell(1, 1), Rotation.North, ""), id: "spot", cells: [new Cell(1, 1)]);
        Assert.Equal(new ActivitySiteState.Standing(ActivityKind.Party), Assert.Single(ActivitySiteRules.States([Party()], Construction([spot]), rooms, planned, _ => none)));
    }

    [Fact]
    public void ThePlacementMethodIsOnePerKindAndRoomAndItsRoomIsReadSpent()
    {
        var hall = BuildingPlanner.SitePlaceMethod(ActivityKind.Party, "hall");
        Assert.StartsWith("party-site-place-", hall.Value);
        Assert.Equal(hall, BuildingPlanner.SitePlaceMethod(ActivityKind.Party, "hall"));
        Assert.NotEqual(hall, BuildingPlanner.SitePlaceMethod(ActivityKind.Party, "den"));
        Assert.StartsWith("ritual-site-place-", BuildingPlanner.SitePlaceMethod(ActivityKind.Ritual, "hall").Value);
        var census = RoomFixtures.Census(RoomFixtures.State("hall"), RoomFixtures.State("den"));
        Assert.Equal(["hall"], BuildingPlanner.SiteSpent(ActivityKind.Party, [hall], census));
        Assert.Empty(BuildingPlanner.SiteSpent(ActivityKind.Marriage, [hall], census));
    }

    [Fact]
    public void ASiteHasNoRetireOrUpgradeLifecycle()
    {
        // The state has no retire, move or upgrade member, the room reconcile never removes a site definition, and the
        // host-retirement rule never retires a standing site even when the plan hosts a recipe at another bench.
        Assert.Equal(["Building", "Planned", "Standing", "Unplanned"], typeof(ActivitySiteState).GetNestedTypes().Select(t => t.Name).Order());
        var carried = ReconcileRules.CarriedOver([], ComfortTests.NoComfortObservation, new ItemFacts(), [], []);
        Assert.All(Defs.Placed.Append("TestGatherSpot"), d => Assert.True(carried(d)));
        var plan = new LayoutPlan { Rooms = [new PlannedRoom { Id = 1, Role = PlannedRole.Workshop, Production = [new PlannedProduction("Smithy", "MakeKnife", 1)] }] };
        var view = WorkRules.NewLedgerView(WorkRules.LedgerViewReconciled) with
        {
            Orders = [new OrderSpec { Recipe = "MakeKnife", Target = 1, Mode = BillMode.StockTarget }],
            Benches = [new HostBench("smithy", "Smithy", true, ["MakeKnife"], []), new HostBench("spot", "TestPartySpot", true, [], [])],
        };
        Assert.Empty(PlannedRules.HostRetirementsOf(plan, view, ["MakeKnife"], _ => false, null));
    }

    private static readonly PlannedActivitySite RitualInHall = new() { Kind = ActivityKind.Ritual, Definition = "TestRitualSpot", NativeRoom = "hall", Anchor = new Cell(1, 1) };

    private static ActivityDemand RitualDemand(params PawnId[] believers) => new()
    {
        Kind = ActivityKind.Ritual, Participants = EquatableArray.From(believers), Minimum = 1, Accepted = ["TestRitualSpot"], Place = "TestRitualSpot", Rituals = [Ritual],
    };

    private static UpkeepRoom HallQuality(double impressiveness) => new()
    {
        StatKeys = SleepingNativeFixtures.StatKeys, Native = SleepingNativeFixtures.Room("hall", RoomRole.Room, new RoomQuality(30, 0, 0, 0, impressiveness), 12), Beds = [],
    };

    private static ForecastCoupledContext RitualContext(double impressiveness) =>
        ForecastValueTests.Context(ForecastValueTests.Pawn()) with
        {
            InvestmentHorizonDays = 10,
            ActivitySites = new ActivitySiteContext
            {
                Demands = [RitualDemand(new PawnId("pawn"))], Sites = [RitualInHall],
                Access = [new ActivityRoom("hall", 10, [new PawnId("pawn")])], Qualities = [HallQuality(impressiveness)],
            },
        };

    [Fact]
    public void ASitesValueFollowsWhatStandsAroundItItsRoofItsCapacityItsReachAndTheRoomsImpressiveness()
    {
        // The roof and the room's use: only an enclosed, roofed, proper room that is no one's rest or prison is offered a spot.
        var den = Room("den", 5);
        var open = (Room: RoomFixtures.State("open", roofed: false), Cells: new[] { new Cell(9, 1), new Cell(10, 1) });
        var bedroom = Room("bedroom", 13, RoomRole.Bedroom);
        var ask = Ask(Party(), [den, open, bedroom], [new("den", 10, Four), new("open", 10, Four), new("bedroom", 10, Four)]);
        Assert.Equal(["den"], Assert.Single(ask.Kinds).Rooms.Cast<ActivityPlacement.InRoom>().Select(r => r.Room));

        // Capacity and reach: the same spot earns the attendee in a room with space and reach, nothing where there is no
        // standing room or the participant cannot reach it.
        var pawn = new PawnId("pawn");
        Assert.Single(SiteRows(SiteContext(InHall, new ActivityRoom("hall", 10, [pawn]))));
        Assert.Empty(SiteRows(SiteContext(InHall, new ActivityRoom("hall", 0, [pawn]))));
        Assert.Empty(SiteRows(SiteContext(InHall, new ActivityRoom("hall", 10, []))));

        // A ritual's surroundings: the same believer at the same spot expects more from a more impressive room.
        var plain = Assert.Single(ForecastActivitySites.Gains(RitualContext(0))).Delta;
        var grand = Assert.Single(ForecastActivitySites.Gains(RitualContext(100))).Delta;
        Assert.True(grand > plain, $"{grand} should exceed {plain}");
        // A room whose quality was never read leaves the ritual unassessed naming the room.
        var unread = RitualContext(100) with { ActivitySites = RitualContext(100).ActivitySites with { Qualities = [] } };
        Assert.Equal("activity_room_quality:hall", Assert.Throws<RequiredFactException>(() => ForecastActivitySites.Gains(unread)).Fact);
    }

    [Fact]
    public void AnActivityInARoomOthersUseCostsThoseUsersTheGatheringAndAnIdleRoomCostsNobody()
    {
        var (a, b, c) = (new PawnId("a"), new PawnId("b"), new PawnId("c"));
        // The room's other uses: each distinct user of a dining seat or recreation source in it, plus one for a work room.
        var comfort = ComfortTests.NoComfort with
        {
            Dining = [new DiningSeat("chair", "hall", [a, b, c], [a, b])],
            Recreation = [new RecreationSource("chess", "Gaming_Cerebral", new ComfortHost.Indoors("hall"), [b, c], [b, c])],
        };
        var uses = ActivitySiteRules.CompetingUses(comfort, Census([Room("hall", 1, RoomRole.Workshop), Room("den", 5)]));
        var only = Assert.Single(uses);
        Assert.Equal(("hall", 3 + 1), (only.Key, only.Value));

        // The loss is priced to the room the spot stands in and to no idle one.
        var pawn = new PawnId("pawn");
        var reach = new ActivityRoom("hall", 10, [pawn]);
        var quiet = SiteContext(InHall, reach);
        var contested = quiet with { ActivitySites = quiet.ActivitySites with { Uses = uses } };
        var loss = Assert.Single(ForecastActivitySites.Gains(contested), g => g.Loss != 0);
        Assert.Equal(("hall/uses", 4 * ForecastActivitySites.GatheringDays / ForecastActivitySites.PartyIntervalDays), (loss.Site, loss.Loss));
        Assert.DoesNotContain(ForecastActivitySites.Gains(quiet), g => g.Loss != 0);
        var idle = SiteContext(InHall with { NativeRoom = "den" }, new ActivityRoom("den", 10, [pawn]));
        Assert.DoesNotContain(ForecastActivitySites.Gains(idle with { ActivitySites = idle.ActivitySites with { Uses = uses } }), g => g.Loss != 0);

        // The ledger carries it as a positive row beside the attendee's gain, so a busy room is a worse place for the same party.
        var rows = SiteRows(contested);
        Assert.Contains(rows, r => r.Key.Contains("/hall/uses", StringComparison.Ordinal) && r.Value > 0);
        Assert.Contains(rows, r => r.Pawn == "pawn" && r.Value < 0);
        Assert.DoesNotContain(SiteRows(quiet), r => r.Value > 0);
    }

    [Fact]
    public async Task AnActivityNobodyCanAttendEarnsNothingAndIsNotBuiltWhileAnUnreadReachIsUnassessedNotZero()
    {
        var nobody = OnePawn with { Participants = [] };
        // A colony too small for a party has no demand, so no site is offered or owed.
        Assert.Empty(ActivitySiteRules.Demands(Access([A, B, C]), Defs, new WorshipFacts()));
        Assert.Empty(ActivitySiteRules.Demands(Access([A]), Defs, new WorshipFacts()));
        Assert.Empty(ActivitySiteRules.States([], Construction(), Census([Room("hall", 1)]), new LayoutPlan(), _ => new HashSet<string>()));

        // A site whose demand names no participant earns nothing at an existing room or a new one: a known zero, no exception.
        ForecastCoupledContext Context(PlannedActivitySite site, ActivityRoom access)
        {
            var context = SiteContext(site, access);
            return context with { ActivitySites = context.ActivitySites with { Demands = [nobody] } };
        }
        var hall = new ActivityRoom("hall", 10, [new PawnId("pawn")]);
        Assert.Empty(ForecastActivitySites.Gains(Context(InHall, hall)));
        Assert.Empty(SiteRows(Context(InHall, hall)));
        Assert.Empty(SiteRows(Context(InNewRoom, hall)));

        // So the comparator values a spot exactly as no spot, they tie, and the baseline keeps no site.
        var tied = await Compare(Inputs(), sites => sites.Sum(s => SiteRows(Context(s is ActivityPlacement.NewRoom ? InNewRoom : InHall, hall)).Sum(r => r.Value)));
        Assert.Empty(tied.Sites);

        // An unread reach, memory or site list is not a known zero: the same site is unassessed naming the missing fact.
        Assert.Equal("activity_room_reach:hall", Assert.Throws<RequiredFactException>(() => SiteRows(SiteContext(InHall, hall with { Room = "den" }))).Fact);
        Assert.Equal(ForecastActivitySites.PartyThought,
            Assert.Throws<RequiredFactException>(() => SiteRows(SiteContext(InNewRoom, hall, new AttendedThought.Unread(ForecastActivitySites.PartyThought)))).Fact);
        var unreadDemand = Assert.Single(ActivitySiteRules.Demands(Access(Four), Defs with { PartySpots = new SiteDefsRead.Unread("gathering_def:Party") }, new WorshipFacts()));
        Assert.Equal(new ActivitySiteState.Unplanned(ActivityKind.Party, "gathering_def:Party"),
            Assert.Single(ActivitySiteRules.States([unreadDemand], Construction(), Census([Room("hall", 1)]), new LayoutPlan(), _ => new HashSet<string>())));
    }

    [Fact]
    public void CompletionAndProvisionAreDistinctInBothDirections()
    {
        var placing = new ActivitySiteState.Planned(ActivityKind.Party);
        var kept = new ActivitySiteState.Standing(ActivityKind.Party);
        // A party owed with no site: the gatherings Need is unmet and waits on no site Need, while the site is still owed.
        var owed = RoomsFamily.GatheringsNeed(new RoomsFacts { Gathering = new GatheringPlan.Ready(A), Sites = [placing] });
        Assert.Equal(Satisfaction.Unmet, owed.Satisfaction);
        Assert.Empty(owed.Preconditions);
        Assert.Equal(Satisfaction.Unmet, RoomsFamily.SiteNeedOf(placing).Satisfaction);
        // A provided site does not complete a party, and a party not owed does not provide a site.
        Assert.Equal(Satisfaction.Met, RoomsFamily.SiteNeedOf(kept).Satisfaction);
        Assert.Equal(Satisfaction.Unmet, RoomsFamily.GatheringsNeed(new RoomsFacts { Gathering = new GatheringPlan.Ready(A), Sites = [kept] }).Satisfaction);
        Assert.Equal(Satisfaction.Met, RoomsFamily.GatheringsNeed(new RoomsFacts { Gathering = new GatheringPlan.NoParty(), Sites = [placing] }).Satisfaction);
    }
}
