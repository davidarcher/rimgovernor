using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3796: the per-role planned-room credit seam. A room serves from the day its one reconcile owes nothing (epic #3650 P2),
/// its credit runs perDay from then to tier 2's horizon, and a role is priced exactly when it has a credit (P3). The
/// storage credit here is a test-only one of a capability-day a day, standing in for the shipped one (StorageCreditTests).
/// </summary>
public sealed class PlannedRoomCreditTests
{
    private const double Day = GameTime.TicksPerDay;
    private const double Horizon = 10;
    private const string Wall = "planned/room1/wall/WoodLog", Shelf = "planned/room1/shelf", Roof = "planned/room1/roof";
    private const string Delivery = "planned/room1/wall/WoodLog/delivery";
    /// <summary>An unplaced shelf candidate's unit (room 0): no planned room's, so it counts as development (#3828).</summary>
    private const string ZoneShelf = "planned/shelf/zone-1/4/4";

    /// <summary>The test-only storage credit: one capability-day a served day, booked to no pawn.</summary>
    private static readonly IReadOnlyDictionary<PlannedRole, RoomCredit> StorageCredit = new Dictionary<PlannedRole, RoomCredit>
    {
        [PlannedRole.Storage] = (_, _, _, _) => [new("shelves", "", 1)],
    };

    private static ForecastPlannedSite Site(string id, string stuff = "") => new(id, 1, stuff, id == Wall ? [Delivery] : [], [id + "/finish"]);

    /// <summary>
    /// Room 1 a storage room, started, one builder: its wood wall ring two builder-days, a stuff-less roof and shelves a
    /// builder-day, a floor blueprint and a removal on it, and a removal on its shared wall with room 2.
    /// </summary>
    internal static ForecastCoupledContext Context(IReadOnlyDictionary<PlannedRole, RoomCredit> credits, double shelves = Day, double removal = 0) =>
        ForecastValueTests.Context(ForecastValueTests.Pawn()) with
        {
            Activities = new() { Activities = [new(Delivery, "stack", "planned/room1", []) { Inputs = [new(new("WoodLog"), Known(10d))] }] },
            PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.Storage, [2] = PlannedRole.Workshop },
            RoomCredits = credits,
            PlannedSites = [Site(Wall, "WoodLog"), Site(Roof), Site(Shelf)],
            Closures = [new(1, "WoodLog", [new(1, 1)], [Wall], [Roof], PlannedRoomPredictionTests.Closed("1", "WoodLog", "Storage", 10d, 1, 4, 0, false))],
            RoomItems = [new("floor-1", [1], ["site-0-finish"]), new("rubble-1", [1], ["removal-1"]), new("shared-1", [1, 2], ["removal-2"])],
            OwedWork = new Dictionary<string, double>
            {
                [Wall] = 2 * Day, [Roof] = 0, [Shelf] = shelves, ["floor-1"] = 0, ["rubble-1"] = removal, ["shared-1"] = 0,
            },
            OpenRooms = new HashSet<int> { 1 },
            PlannedBuilders = 1,
            InvestmentHorizonDays = Horizon,
        };

    internal static ForecastCoupledOutcome Outcome(ForecastCoupledContext context, IReadOnlyDictionary<string, double>? finished = null)
    {
        var service = PlannedRoomCredits.ServiceDays(context, finished ?? new Dictionary<string, double>(), Day);
        return new(new([], 0, 0), Known(true), new Dictionary<string, ForecastNeedsResult>(), [])
        {
            ServedRooms = service,
            RequestedDevelopment = [Wall, Shelf, Roof, ZoneShelf],
        };
    }

    private static ForecastInvestmentContribution[] Credits(ForecastCoupledContext context, ForecastCoupledOutcome outcome) =>
        [.. ForecastValue.Of(outcome, context, Day).InvestmentContributions.Where(r => r.Key.StartsWith("storage/", StringComparison.Ordinal))];

    private static readonly string[] ReliefNeeds = ["flooring/floor-1", "removal/rubble-1", "removal/shared-1", "flooring/elsewhere"];

    /// <summary>A floor's and two removals' relief, as the placed-site and removal roots request it, and a floor of no planned room.</summary>
    private static ForecastCommitment[] Relief() =>
        [.. ReliefNeeds.Select(need => new ForecastCommitment(need, [])
        {
            Outcome = new(need, "construction-method", need, Known(Day), default)
            { Requirements = new([new(need, ProductionNeedPriority.Maintenance, 1.0)], default, default) },
        })];

    internal static double Development(ForecastCoupledContext context) =>
        ForecastCoupledSelection.CoupledConsequences(Outcome(context), Relief(), context, Day, Outcome(context)).Development;

    [Fact]
    public void AStorageRoomClosingAtDayTwoWithADayOfShelvesIsCreditedFromDayThree()
    {
        var context = Context(StorageCredit);
        var outcome = Outcome(context);
        Assert.Equal(new ForecastRoomService(1, "WoodLog", 3 * Day), Assert.Single(outcome.ServedRooms));
        var row = Assert.Single(Credits(context, outcome));
        Assert.Equal(("", "storage/1/shelves", Horizon - 3), (row.Pawn, row.Key, row.Units));
        Assert.Equal(-(Horizon - 3), row.Value, 12);
        // A removal's work is the room's owed work too: a day of it serves the room a day later.
        Assert.Equal(Horizon - 4, Assert.Single(Credits(Context(StorageCredit, removal: Day), Outcome(Context(StorageCredit, removal: Day)))).Units, 9);
    }

    [Fact]
    public void NoPlannedRoomsSitesOrRoofCountAsDevelopmentAndAPricedRoomsFloorAndRemovalsLeaveIt()
    {
        // #3828: room 1's three units count in neither, the shelf of no room in both. Unpriced: the four reliefs count too.
        // Priced: only the floor elsewhere, the shared wall's removal leaving with the priced room.
        Assert.Equal(5, Development(Context(new Dictionary<PlannedRole, RoomCredit>())));
        Assert.Equal(2, Development(Context(StorageCredit)));
        Assert.Contains("removal/shared-1", PlannedRoomCredits.PricedRelief(Context(StorageCredit)));
        Assert.Equal([Roof, Shelf, Wall], PlannedRoomCredits.PlannedUnits(Context(StorageCredit)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ARoleWithNoCreditKeepsItsReliefAsDevelopmentAndIsChargedNoMaterial()
    {
        var unpriced = Context(new Dictionary<PlannedRole, RoomCredit>());
        Assert.False(unpriced.Priced(1));
        Assert.Empty(PlannedRoomCredits.PricedRelief(unpriced));
        Assert.Empty(ForecastValue.ChargedMaterials(unpriced));
        Assert.Empty(Credits(unpriced, Outcome(unpriced)));
        var priced = Context(StorageCredit);
        Assert.True(priced.Priced(1));
        Assert.Equal([new Resource("WoodLog")], ForecastValue.ChargedMaterials(priced));
    }

    [Fact]
    public void ARinglessRoomServesWhenItsSitesAreDone()
    {
        // Room 1 owes no ring: no closure, only its shelves and the floor.
        var context = Context(StorageCredit) with { Closures = [], PlannedSites = [Site(Shelf)], RoomItems = [new("floor-1", [1], ["site-0-finish"])] };
        var finished = new Dictionary<string, double> { [Shelf + "/finish"] = 900, ["site-0-finish"] = 400 };
        Assert.Equal(new ForecastRoomService(1, "", 900), Assert.Single(Outcome(context, finished).ServedRooms));
        // Left owing, it serves at its owed work over the builder, never inside the horizon.
        Assert.Equal(new ForecastRoomService(1, "", Day), Assert.Single(Outcome(context, new Dictionary<string, double> { ["site-0-finish"] = 400 }).ServedRooms));
        Assert.Equal(2 * Day, Assert.Single(Outcome(context with { OwedWork = new Dictionary<string, double> { [Shelf] = 2 * Day, ["floor-1"] = 0 } }).ServedRooms).ServedAt);
    }

    private const string BedWall = "planned/room1/wall/WoodLog", CorridorWall = "planned/room2/wall/WoodLog";

    /// <summary>The test-only bedroom credit: one capability-day a served day.</summary>
    private static readonly IReadOnlyDictionary<PlannedRole, RoomCredit> BedroomCredit = new Dictionary<PlannedRole, RoomCredit>
    {
        [PlannedRole.Bedroom] = (_, _, _, _) => [new("rest", "", 1)],
    };

    /// <summary>
    /// #3815: room 1 a started bedroom whose wood ring is two builder-days, and its connector, room 2 of
    /// <paramref name="role"/>: a day of corridor wall (none for an airlock, whose walls are its parent's ring) and half a
    /// day of floor on its cells. One builder.
    /// </summary>
    private static ForecastCoupledContext Connected(PlannedRole role, IReadOnlyDictionary<PlannedRole, RoomCredit> credits)
    {
        var corridor = role == PlannedRole.Circulation;
        return ForecastValueTests.Context(ForecastValueTests.Pawn()) with
        {
            PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.Bedroom, [2] = role },
            RoomCredits = credits,
            PlannedSites = corridor ? [new(BedWall, 1, "WoodLog", [], [BedWall + "/finish"]), new(CorridorWall, 2, "WoodLog", [], [CorridorWall + "/finish"])]
                : [new(BedWall, 1, "WoodLog", [], [BedWall + "/finish"])],
            Closures = [new(1, "WoodLog", [new(1, 1)], [BedWall], [], PlannedRoomPredictionTests.Closed("1", "WoodLog", "Bedroom", 10d, 1, 4, 0, false))],
            RoomItems = [new("connector-floor", [2], ["site-9-finish"])],
            OwedWork = new Dictionary<string, double> { [BedWall] = 2 * Day, [CorridorWall] = Day, ["connector-floor"] = Day / 2 },
            Connectors = new Dictionary<int, EquatableArray<int>> { [2] = [1] },
            OpenRooms = new HashSet<int> { 1 },
            PlannedBuilders = 1,
            InvestmentHorizonDays = Horizon,
        };
    }

    private static double ConnectedDevelopment(ForecastCoupledContext context)
    {
        var outcome = new ForecastCoupledOutcome(new([], 0, 0), Known(true), new Dictionary<string, ForecastNeedsResult>(), [])
        { RequestedDevelopment = [BedWall, CorridorWall] };
        ForecastCommitment[] relief = [new("flooring/connector-floor", [])
        {
            Outcome = new("flooring/connector-floor", "construction-method", "flooring/connector-floor", Known(Day), default)
            { Requirements = new([new("flooring/connector-floor", ProductionNeedPriority.Maintenance, 1.0)], default, default) },
        }];
        return ForecastCoupledSelection.CoupledConsequences(outcome, relief, context, Day, outcome).Development;
    }

    [Fact]
    public void ABedroomReachedThroughACorridorServesAfterTheCorridorsWorkAndTakesItsUnitsOutOfDevelopment()
    {
        var context = Connected(PlannedRole.Circulation, BedroomCredit);
        // The ring's two days, then the corridor's wall and floor: a day and a half later.
        var served = PlannedRoomCredits.ServiceDays(context, new Dictionary<string, double>(), Day);
        Assert.Equal(new ForecastRoomService(1, "WoodLog", 3.5 * Day), Assert.Single(served, s => s.Room == 1));
        Assert.Equal(2 * Day, Assert.Single(PlannedRoomCredits.ServiceDays(context with { Connectors = new Dictionary<int, EquatableArray<int>>() },
            new Dictionary<string, double>(), Day)).ServedAt);
        // Both rooms' sites and the corridor's floor leave the count with the priced bedroom; the corridor earns no credit.
        Assert.True(context.Priced(2));
        Assert.Equal(0, ConnectedDevelopment(context));
        Assert.Contains(CorridorWall, PlannedRoomCredits.PlannedUnits(context));
        Assert.Contains("flooring/connector-floor", PlannedRoomCredits.PricedRelief(context));
        // A corridor serving no priced room keeps its floor's relief as development; no room's walls count (#3828).
        var unpriced = Connected(PlannedRole.Circulation, new Dictionary<PlannedRole, RoomCredit>());
        Assert.False(unpriced.Priced(2));
        Assert.Equal(1, ConnectedDevelopment(unpriced));
    }

    [Fact]
    public void AnAirlockSealingABedroomServesItAfterTheAirlocksWorkAndLeavesDevelopmentWithIt()
    {
        var context = Connected(PlannedRole.Airlock, BedroomCredit);
        var served = PlannedRoomCredits.ServiceDays(context, new Dictionary<string, double>(), Day);
        Assert.Equal(2.5 * Day, Assert.Single(served, s => s.Room == 1).ServedAt);
        // Inside the schedule the bedroom serves when the later of its ring and the airlock's floor finishes.
        var finished = new Dictionary<string, double> { [BedWall + "/finish"] = 300, ["site-9-finish"] = 700 };
        Assert.Equal(700, Assert.Single(PlannedRoomCredits.ServiceDays(context, finished, Day), s => s.Room == 1).ServedAt);
        Assert.True(context.Priced(2));
        Assert.Contains("flooring/connector-floor", PlannedRoomCredits.PricedRelief(context));
        Assert.Equal(1, ConnectedDevelopment(context)); // the corridor wall of no room left
    }

    [Fact]
    public void ConnectorsAreReadFromTheLayoutPlansRoomsAndDoors()
    {
        // Bedroom 1 and workshop 4 open on corridor 2, which also meets corridor 5; airlock 3 seals bedroom 1.
        PlannedRoom[] rooms =
        [
            new() { Id = 1, Role = PlannedRole.Bedroom }, new() { Id = 2, Role = PlannedRole.Circulation },
            PlannedRoom.AttachAirlock(new(5, 5), Rotation.South) with { Id = 3, Parent = 1 },
            new() { Id = 4, Role = PlannedRole.Workshop }, new() { Id = 5, Role = PlannedRole.Circulation },
        ];
        RoomLink[] links = [new(1, 2, new(1, 1)), new(2, 4, new(2, 2)), new(2, 5, new(3, 3))];
        var connectors = PlannedRoomCredits.ConnectorsOf(rooms, links);
        Assert.Equal([2, 3], connectors.Keys.Order());
        Assert.Equal([1, 4], connectors[2]);
        Assert.Equal([1], connectors[3]);
        Assert.Empty(PlannedRoomCredits.ConnectorsOf([], []));
    }

    private const string BarracksWall = "planned/room1/wall/WoodLog", Beds = "planned/room1/build/Bed/43/48", Stove = "planned/room1/build/FueledStove/44/46";

    /// <summary>
    /// #3827: room 1 a started barracks, one builder: a wood ring of two builder-days, a builder-day of beds and a stove with
    /// no finish option, so no owed-work row.
    /// </summary>
    private static ForecastCoupledContext Barracks() => ForecastValueTests.Context(ForecastValueTests.Pawn()) with
    {
        PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.Barracks },
        RoomCredits = new Dictionary<PlannedRole, RoomCredit> { [PlannedRole.Barracks] = (_, _, _, _) => [new("rest", "", 1)] },
        PlannedSites =
        [
            new(BarracksWall, 1, "WoodLog", [], [BarracksWall + "/finish"]),
            new(Beds, 1, "", [], [Beds + "/finish"]),
            new(Stove, 1, "", [], [Stove + "/finish"]),
        ],
        Closures = [new(1, "WoodLog", [new(1, 1)], [BarracksWall], [], PlannedRoomPredictionTests.Closed("1", "WoodLog", "Barracks", 10d, 1, 4, 0, false))],
        OwedWork = new Dictionary<string, double> { [BarracksWall] = 2 * Day, [Beds] = Day },
        OpenRooms = new HashSet<int> { 1 },
        PlannedBuilders = 1,
        InvestmentHorizonDays = Horizon,
    };

    [Fact]
    public void ABarracksOwingABlockedStoveIsCreditedFromTheDayItsOtherItemsFinish()
    {
        var blocked = Barracks() with { BlockedItems = new HashSet<string>(StringComparer.Ordinal) { Stove } };
        // Projected: the ring's two days, then the beds' one.
        var served = Assert.Single(PlannedRoomCredits.ServiceDays(blocked, new Dictionary<string, double>(), Day));
        Assert.Equal(new ForecastRoomService(1, "WoodLog", 3 * Day), served);
        // Inside the schedule: the last of the ring and the beds, the stove never finishing.
        var finished = new Dictionary<string, double> { [BarracksWall + "/finish"] = 300, [Beds + "/finish"] = 900 };
        Assert.Equal(900, Assert.Single(PlannedRoomCredits.ServiceDays(blocked, finished, Day)).ServedAt);
        var row = Assert.Single(ForecastValue.Of(Outcome(blocked), blocked, Day).InvestmentContributions, r => r.Key == "barracks/1/rest");
        Assert.Equal(Horizon - 3, row.Units, 9);
    }

    [Fact]
    public void ABarracksOwingAStoveNoOptionCanFinishIsNeverServedAndEarnsNothing()
    {
        // Not blocked (its research done, its steel unread or obtainable): the stove still owes work no row prices, so no
        // builder can finish the room (#3835: a named never-served state, no unassessed finding).
        var context = Barracks();
        Assert.Empty(PlannedRoomCredits.ServiceDays(context, new Dictionary<string, double> { [BarracksWall + "/finish"] = 300, [Beds + "/finish"] = 900 }, Day));
        var ledger = ForecastValue.Of(Outcome(context), context, Day);
        Assert.DoesNotContain(ledger.InvestmentContributions, r => r.Key.StartsWith("barracks/", StringComparison.Ordinal));
        Assert.DoesNotContain(ledger.Findings, f => f.Contains("_unassessed:1", StringComparison.Ordinal));
    }

    [Fact]
    public void APricedRoomWithoutAFreeBuilderIsNeverServedAndEarnsNothing()
    {
        var unbuilt = Context(StorageCredit) with { PlannedBuilders = 0 };
        Assert.Empty(PlannedRoomCredits.ServiceDays(unbuilt, new Dictionary<string, double>(), Day));
        var ledger = ForecastValue.Of(Outcome(unbuilt), unbuilt, Day);
        Assert.DoesNotContain(ledger.InvestmentContributions, r => r.Key.StartsWith("storage/", StringComparison.Ordinal));
        Assert.DoesNotContain(ledger.Findings, f => f.Contains("_unassessed:1", StringComparison.Ordinal));
    }

    [Fact]
    public void ACreditWhoseFactIsUnreadThrowsARequiredFact()
    {
        var unread = Context(new Dictionary<PlannedRole, RoomCredit> { [PlannedRole.Storage] = (_, _, _, _) => throw new RequiredFactException("stock") });
        Assert.Throws<RequiredFactException>(() => ForecastValue.Of(Outcome(unread), unread, Day));
    }

    /// <summary>
    /// #3835 acceptance: a fresh colony's review, the production way (GenerateCoupledActivities over ConstructionReview's facts,
    /// one planned room per shipped role, each predicted as native predicts every priced room), runs every shipped credit on
    /// its served room without a throw: each fresh-colony state is a named state with its true credit, not an unread fact.
    /// </summary>
    [Fact]
    public void AFreshColonyRunsEveryShippedCreditWithoutAThrow()
    {
        var facts = WithEveryShippedRoom(ForecastCoupledTests.ConstructionReview());
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts)!.Context;
        var pawns = generated.Needs.Pawns.Select(p => p.Pawn).Distinct(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(pawns);
        var outcome = new ForecastCoupledOutcome(new([], 0, 0), Known(true), pawns.ToDictionary(p => p, p => new ForecastNeedsResult([], default, default, new(new(p, "", default, default), []), [])
        {
            Mood = new([], default, default, default, default, default, [])
            {
                PressureTicks = Known(0d), BreakRisk = Known(0d), BreakTierTicks = [0, 0, 0], ShiftedBreakTierTicks = [new(0, [Day, 0, 0])],
            },
        }), []);
        RunsEveryShippedCredit(generated, outcome, Day);
    }

    /// <summary>
    /// #3858 acceptance: a colony cooking meals, the production way (GenerateCoupledActivities, then the comparison selecting
    /// the cook-and-eat portfolio). The meal carries the thoughts native reads over the colony's ingredient mix (here human meat
    /// as an ingredient), so the eater's mood forecast stays whole, and every shipped credit runs on that outcome without a throw.
    /// </summary>
    [Fact]
    public void AColonyCookingMealsKeepsItsMoodForecastAndRunsEveryShippedCredit()
    {
        var cannibal = NeedsFixtures.Memory("AteHumanMeatAsIngredient", -15, Day, false) with { StackLimit = 1 };
        var cooking = ForecastPortfolioChoiceTests.Observed([ForecastPortfolioChoiceTests.Conversion("rice", "Rice", "CookMeal", 10)],
            [new("rice", "Rice", new(1, 1), 4, Known(1000d))]);
        var coupled = cooking.ForecastObserved.Coupled!;
        cooking = cooking with { ForecastObserved = cooking.ForecastObserved with
            { Coupled = coupled with { FoodUses = EquatableArray.From(coupled.FoodUses.Select(u => u with { Thoughts = [cannibal] })) } } };
        var (generated, outcome) = Selected(WithEveryShippedRoom(cooking), 150);
        Assert.Contains(generated.NeedTasks, n => n.Kind == "food" && n.Resource.Value == "Meal" && outcome.Plan.Allocations.Any(a => a.Activity == n.Task));
        var mood = Assert.IsType<ForecastMoodResult>(outcome.Needs["pawn"].Mood);
        Assert.True(mood.BreakRisk.IsKnown);
        // The ingredient memory is priced: the same meal without it leaves a higher thought offset at the horizon.
        var plain = cooking with { ForecastObserved = cooking.ForecastObserved with { Coupled = coupled } };
        var (_, unflavoured) = Selected(WithEveryShippedRoom(plain), 150);
        Assert.True(outcome.Needs["pawn"].ThoughtOffsetAtHorizon.Value < unflavoured.Needs["pawn"].ThoughtOffsetAtHorizon.Value);
        RunsEveryShippedCredit(generated, outcome, 150);
    }

    /// <summary>
    /// #3858 acceptance: a pawn on a spar, the production way (PracticeMethodCandidates' bout, priced by tier 2's skill
    /// marginal). The spar is recreation with the TrainedWith memory a completed bout gives, so the mood forecast stays whole and
    /// every shipped credit runs on the outcome without a throw.
    /// </summary>
    [Fact]
    public void APawnSparringKeepsItsMoodForecastAndRunsEveryShippedCredit()
    {
        var trainedWith = NeedsFixtures.Memory("RimGovernor_TrainedWith", 3, Day, false) with { StackLimit = 1 };
        var facts = ForecastPortfolioChoiceTests.Observed([], [], helper: true);
        var learning = CapabilityMaintenanceTests.Learning() with { Xp = Known(0d) };
        var roster = facts.ForecastObserved.WorkPawns.Value.Select(p => p with { Skills = Known<EquatableArray<WorkSkill>>([new() { Name = "Melee", Level = 1, Learning = learning }]) }).ToArray();
        var workers = roster.Select((p, i) => new PracticeWorker(p.Id.Value, "Melee", "target/" + i, new(i + 1, 1), "RimGovernor_Spar", "RimGovernor_Spar",
            Known(0d), Known(100d), learning) { CallbackIntervalTicks = Known(10d), RawXpPerCallback = Known(10d), JoyGainPerTick = .0001, Thoughts = [trainedWith] }).ToArray();
        facts = facts with { Hostiles = Known(0L), CriticalPatients = Known(0L), ForecastObserved = facts.ForecastObserved with
        {
            WorkPawns = Known(EquatableArray.From(roster)),
            Needs = facts.ForecastObserved.Needs! with { Pawns = EquatableArray.From(facts.ForecastObserved.Needs!.Pawns.Select(p => p with
            { Needs = EquatableArray.From(p.Needs.Select(n => n with { Level = 1d, FallPerTick = 0d })) })) },
            Coupled = facts.ForecastObserved.Coupled! with { Practice = new() { Known = true, Workers = EquatableArray.From(workers) } }
        } };
        var investment = roster.ToDictionary(p => ProjectGraph.SkillKey(p.Id.Value, "Melee"), _ => 1d, StringComparer.Ordinal);
        var (generated, outcome) = Selected(WithEveryShippedRoom(facts), 300, investment);
        var spars = generated.NeedTasks.Where(n => n.Kind == "joy" && outcome.Plan.Allocations.Any(a => a.Activity == n.Task)).ToArray();
        Assert.NotEmpty(spars);
        Assert.All(spars, s => Assert.Equal([trainedWith], s.Thoughts.ToArray()));
        Assert.All(spars, s => Assert.True(Assert.IsType<ForecastMoodResult>(outcome.Needs[s.Pawn].Mood).BreakRisk.IsKnown));
        RunsEveryShippedCredit(generated, outcome, 300);
    }

    /// <summary>One planned room per shipped role, each predicted as native predicts every priced room.</summary>
    private static (int Room, PlannedRole Role)[] ShippedRooms() => PlannedRoomCredits.Shipped.Keys.Select((role, i) => (Room: i + 1, Role: role)).ToArray();

    /// <summary><paramref name="review"/> with <see cref="ShippedRooms"/> planned, the way RoundsRead and RoundsWork read them.</summary>
    private static RoundsFacts WithEveryShippedRoom(RoundsFacts review)
    {
        var roles = ShippedRooms();
        var facts = review with
        {
            // RoundsWork reads each work pawn's skills (a fresh colonist with none it could practise) and health.
            ForecastObserved = review.ForecastObserved with { WorkPawns = Known(EquatableArray.From(review.ForecastObserved.WorkPawns.Value
                .Select(p => p with { Skills = p.Skills.IsKnown ? p.Skills : Known<EquatableArray<WorkSkill>>([]), Health = Known(1d) }))) },
            PlannedShelters = [.. roles.Select(r => new PlannedRoom { Id = r.Room, Role = r.Role,
                Region = RegionRules.RectRegion(new RimGovernor.Brain.Policy.Rectangle { X = 5 + (10 * r.Room), Z = 5, Width = 3, Height = 3 }) })],
            PlannedRoomPredictions = EquatableArray.From(roles.Select(r => PlannedRoomPredictionTests.Closed(
                r.Room.ToString(System.Globalization.CultureInfo.InvariantCulture), "", Wire.Name(r.Role), 30d, 0, 9, 0, false))),
            // RoundsRead furnishes the planned hospital with the catalog's hospital bed.
            HospitalBed = new RoomFurniture { Beds = [new() { Def = "Bed", Slots = 1, ImmunityGainSpeedFactor = 1.07 }] }.HospitalBed(),
        };
        return facts;
    }

    /// <summary>The production comparison's selected outcome over <paramref name="facts"/> and the context it was evaluated on.</summary>
    private static (ForecastCoupledContext Context, ForecastCoupledOutcome Outcome) Selected(RoundsFacts facts, double horizon,
        IReadOnlyDictionary<string, double>? investment = null)
    {
        var generated = Assert.IsType<ForecastCoupledCandidates>(ForecastCoupledCandidates.GenerateCoupledActivities(facts, horizon)?.Priced());
        if (investment is not null) generated = generated with { Context = generated.Context with { Investment = investment } };
        var request = ForecastCoupled.CoupledForecastRequestOf(new("world/map/load", 100), horizon / GameTime.TicksPerDay,
            facts with { CoupledAlternatives = generated }, new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, [], new());
        var selected = ForecastCoupledSelection.CompareActivityOutcomes(request with { Coupled = generated.Context }, generated);
        Assert.NotEmpty(selected.Selection.Selected);
        return (generated.Context, selected.Selected);
    }

    /// <summary>
    /// Every shipped credit on its served room in <paramref name="outcome"/>, and the ledger over them all, without a throw. Tier 2
    /// sets the investment horizon and the tending a wound gets now (ColonyValue.TendingNow); the round reads the storyteller's
    /// threat schedule (here a quiet one) and its disease schedule (no disease incident yet); the catalog carries the capacity
    /// penalty tables.
    /// </summary>
    private static void RunsEveryShippedCredit(ForecastCoupledContext generated, ForecastCoupledOutcome outcome, double horizon)
    {
        var roles = ShippedRooms();
        var context = generated with
        {
            // The round reads no creepjoiner held apart (RoundsCreepJoiner.CreepJoinerForecastOf, #3844).
            CreepJoiners = new() { Unread = "", Held = [] },
            InvestmentHorizonDays = Horizon, Tending = new ForecastTending(true, 0.6), CapacityPenalties = ForecastValueTests.Tables(),
            Harm = ForecastValueTests.RaidDay(raidsPerDay: 0) with { Illness = new IllnessSchedule { Assessed = true }, Containment = generated.Harm.Containment },
        };
        Assert.All(roles, r => Assert.Equal(r.Role, context.PlannedRoomRoles[r.Room]));
        var served = outcome with { ServedRooms = [.. roles.Select(r => new ForecastRoomService(r.Room, "", Day))] };
        foreach (var (room, role) in roles) Assert.NotNull(PlannedRoomCredits.Shipped[role](new(room, "", Day), served, context, horizon));
        Assert.NotNull(ForecastValue.Of(served, context, horizon));
    }
}
