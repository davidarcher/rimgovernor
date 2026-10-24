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
        [PlannedRole.Storage] = (_, _, _, _, _) => [new("shelves", "", 1)],
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
        [PlannedRole.Bedroom] = (_, _, _, _, _) => [new("rest", "", 1)],
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
        RoomCredits = new Dictionary<PlannedRole, RoomCredit> { [PlannedRole.Barracks] = (_, _, _, _, _) => [new("rest", "", 1)] },
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
        var unread = Context(new Dictionary<PlannedRole, RoomCredit> { [PlannedRole.Storage] = (_, _, _, _, _) => throw new RequiredFactException("stock") });
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
    /// the cook-and-eat portfolio). The meal carries the thoughts native reads for its batch's ingredient (here human meat as an
    /// ingredient), so the eater's mood forecast stays whole, and every shipped credit runs on that outcome without a throw.
    /// </summary>
    [Fact]
    public void AColonyCookingMealsKeepsItsMoodForecastAndRunsEveryShippedCredit()
    {
        var cannibal = NeedsFixtures.Memory("AteHumanMeatAsIngredient", -15, Day, false) with { StackLimit = 1 };
        var cooking = ForecastPortfolioChoiceTests.Observed([ForecastPortfolioChoiceTests.Conversion("rice", "Rice", "CookMeal", 10)],
            [new("rice", "Rice", new(1, 1), 4, new FoodFreshness.Rots(1000d))]);
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
    /// #3872 acceptance: one bill allows rice and human meat and the colony stocks both, the production way. A governor batch is
    /// cooked from the one ingredient its recipe step is supplied, so native reads a meal use per ingredient: the rice meal has no
    /// ingredient thought, the human-meat meal the cannibal one. Each batch is priced with its own; the rice batch is the quicker
    /// cook, the comparison chooses it, and the eaten meal carries no human-meat thought (the union mix gave it one). With only
    /// human meat stocked, the meal carries it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ACookedBatchCarriesTheThoughtsOfItsOwnIngredientNotEveryIngredientItsBillAllows(bool riceStocked)
    {
        var cannibal = NeedsFixtures.Memory("AteHumanMeatAsIngredient", -15, Day, false) with { StackLimit = 1 };
        var meat = ForecastPortfolioChoiceTests.Conversion("meat", "Meat_Human", "CookMeal", 80);
        var rice = ForecastPortfolioChoiceTests.Conversion("rice", "Rice", "CookMeal", 10);
        Assert.Equal(meat.Bill, rice.Bill);
        var cooking = ForecastPortfolioChoiceTests.Observed(riceStocked ? [meat, rice] : [meat],
            riceStocked ? [new("meat", "Meat_Human", new(1, 1), 4, new FoodFreshness.Rots(1000d)), new("rice", "Rice", new(1, 1), 4, new FoodFreshness.Rots(1000d))]
                : [new("meat", "Meat_Human", new(1, 1), 4, new FoodFreshness.Rots(1000d))]);
        var coupled = cooking.ForecastObserved.Coupled!;
        cooking = cooking with { ForecastObserved = cooking.ForecastObserved with { Coupled = coupled with { FoodUses = EquatableArray.From(
            coupled.FoodUses.Select(u => u.Ingredient == "Meat_Human" ? u with { Thoughts = [cannibal] } : u)) } } };
        var (generated, outcome) = Selected(WithEveryShippedRoom(cooking), 150);
        var meals = generated.NeedTasks.Where(n => n.Kind == "food" && n.Resource.Value == "Meal").ToArray();
        var eaten = meals.Where(n => outcome.Plan.Allocations.Any(a => a.Activity == n.Task)).ToArray();
        Assert.NotEmpty(eaten);
        Assert.Contains(meals, n => n.Thoughts.Contains(cannibal));
        if (riceStocked)
        {
            Assert.Contains(meals, n => n.Thoughts.Count == 0);
            Assert.All(eaten, n => Assert.Empty(n.Thoughts));
        }
        else Assert.All(meals, n => Assert.Equal([cannibal], n.Thoughts.ToArray()));
        Assert.True(Assert.IsType<ForecastMoodResult>(outcome.Needs["pawn"].Mood).BreakRisk.IsKnown);
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
        var learning = CapabilityMaintenanceTests.Learning() with { Xp = 0d };
        var roster = facts.ForecastObserved.WorkPawns.Value.Select(p => p with { Skills = Known<EquatableArray<WorkSkill>>([new() { Name = "Melee", Level = 1, Learning = learning }]) }).ToArray();
        var workers = roster.Select((p, i) => new PracticeWorker(p.Id.Value, "Melee", "target/" + i, new(i + 1, 1), "RimGovernor_Spar", "RimGovernor_Spar",
            0d, 100d, learning) { Company = [], CallbackIntervalTicks = 10d, RawXpPerCallback = 10d, PreparationTicks = 0, JoyGainPerTick = .0001, Thoughts = [trainedWith], Equipment = [], Findings = [] }).ToArray();
        facts = facts with { Hostiles = Known(0L), CriticalPatients = Known(0L), ForecastObserved = facts.ForecastObserved with
        {
            WorkPawns = Known(EquatableArray.From(roster)),
            Needs = facts.ForecastObserved.Needs! with { Pawns = EquatableArray.From(facts.ForecastObserved.Needs!.Pawns.Select(p => p with
            { Needs = EquatableArray.From(p.Needs.Select(n => n with { Level = 1d, FallPerTick = 0d })) })) },
            Coupled = facts.ForecastObserved.Coupled! with { Practice = new() { Workers = EquatableArray.From(workers) } }
        } };
        var investment = roster.ToDictionary(p => ProjectGraph.SkillKey(p.Id.Value, "Melee"), _ => 1d, StringComparer.Ordinal);
        var (generated, outcome) = Selected(WithEveryShippedRoom(facts), 300, investment);
        var spars = generated.NeedTasks.Where(n => n.Kind == "joy" && outcome.Plan.Allocations.Any(a => a.Activity == n.Task)).ToArray();
        Assert.NotEmpty(spars);
        Assert.All(spars, s => Assert.Equal([trainedWith], s.Thoughts.ToArray()));
        Assert.All(spars, s => Assert.True(Assert.IsType<ForecastMoodResult>(outcome.Needs[s.Pawn].Mood).BreakRisk.IsKnown));
        RunsEveryShippedCredit(generated, outcome, 300);
    }

    /// <summary>
    /// #3871 and #3881 acceptance: a pending commitment, the production way (the fresh colony's construction, its bill reservations
    /// read). Every fact that settles the site's completion is read, so the commitment granting a memory has a known completion:
    /// a site done inside the day lifts the mood from its end, the mood forecast stays whole and every shipped credit runs.
    /// </summary>
    [Fact]
    public void APendingCommitmentKeepsItsMoodForecastAndRunsEveryShippedCredit()
    {
        var memory = NeedsFixtures.Memory("KnowBuriedInSarcophagus", 4, 8d * Day, false);
        // A fresh colony the production way: native reads the bench census every frame, so the bill reservations are known (here
        // none) and every fact that settles the site's completion is read (#3881).
        var facts = WorkRules.ReviewWorkResources(WithEveryShippedRoom(ForecastCoupledTests.ConstructionReview()),
            Known<EquatableArray<ProductionBatch>>([]), new([]));
        var generated = Assert.IsType<ForecastCoupledCandidates>(ForecastCoupledCandidates.GenerateCoupledActivities(facts));
        var sites = generated.Alternatives.Where(c => c.Id.StartsWith("site-", StringComparison.Ordinal) && !c.Id.Contains("/after/", StringComparison.Ordinal)).ToArray();
        var pending = sites.Select(c => c with { MoodMemories = [new("pawn", memory)] }).ToArray();
        Assert.NotEmpty(pending);
        var outcomes = Evaluated(facts with { CoupledAlternatives = generated with { Alternatives = EquatableArray.From(pending) } }, Day, pending);
        var plain = Evaluated(facts with { CoupledAlternatives = generated with { Alternatives = EquatableArray.From(sites) } }, Day, sites);
        Assert.Equal(plain.Length, outcomes.Length);
        var granted = 0;
        foreach (var (outcome, without) in outcomes.Zip(plain))
        {
            Assert.Equal(without.Plan.Allocations, outcome.Plan.Allocations);
            Assert.DoesNotContain(outcome.Findings, f => f.Reason == "commitment_completion_unassessed");
            Assert.True(outcome.Feasible.IsKnown);
            var mood = Assert.IsType<ForecastMoodResult>(outcome.Needs["pawn"].Mood);
            Assert.True(mood.BreakRisk.IsKnown);
            // The memory lifts the mood from the site's completion on; a site that does not complete inside the day grants nothing.
            var lifted = mood.TerminalMood.Value - Assert.IsType<ForecastMoodResult>(without.Needs["pawn"].Mood).TerminalMood.Value;
            Assert.True(lifted >= -1e-9, "memory lowered the mood by " + lifted);
            if (lifted > 1e-9) granted++;
            RunsEveryShippedCredit(generated.Context, outcome, Day);
        }
        Assert.True(granted > 0, "no outcome granted the site's memory");
    }

    /// <summary>A completion the evaluation cannot settle is an unread fact (#3881): the site's memories are not dropped quietly.</summary>
    [Fact]
    public void AnUnsettledCommitmentCompletionIsAnUnreadFact()
    {
        var facts = WorkRules.ReviewWorkResources(WithEveryShippedRoom(ForecastCoupledTests.ConstructionReview()),
            Known<EquatableArray<ProductionBatch>>([]), new([]));
        var generated = Assert.IsType<ForecastCoupledCandidates>(ForecastCoupledCandidates.GenerateCoupledActivities(facts));
        var site = generated.Alternatives.First(c => c.Id.StartsWith("site-", StringComparison.Ordinal) && !c.Id.Contains("/after/", StringComparison.Ordinal));
        // A lot whose count was never read leaves the site's delivery, and so its completion, unsettled.
        var unread = generated with { Context = generated.Context with { InitialLots = EquatableArray.From(generated.Context.InitialLots.Select(l => l with { Count = default })) } };
        var pending = new[] { site with { MoodMemories = [new("pawn", NeedsFixtures.Memory("KnowBuriedInSarcophagus", 4, 8d * Day, false))] } };
        var thrown = Assert.Throws<RequiredFactException>(() => Evaluated(facts with { CoupledAlternatives = unread with { Alternatives = EquatableArray.From(pending) } }, Day, pending));
        Assert.StartsWith("commitment_completion:", thrown.Fact, StringComparison.Ordinal);
    }

    /// <summary>
    /// #3871 acceptance: a planned roof, the production way (ForecastRoofConsequencesTests' builder whose owned bed is closed into a
    /// bedroom by a wall and a door). A fresh sleep inside the onset window (0.8 rest at 0.00016 a tick, 5000 ticks) renews its bed
    /// thoughts at the chance Rand.Range(2500, 10000) gives, and a pawn asleep now at the onset native read: either way the mood
    /// forecast stays whole and every shipped credit runs.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APlannedRoofKeepsItsMoodForecastAndRunsEveryShippedCredit(bool asleep)
    {
        var facts = ForecastRoofConsequencesTests.Review(ForecastRoofConsequencesTests.Closed("Bedroom", 2), pawn => asleep
            ? pawn with { Awake = false, Location = pawn.Targets[0].Location, SleepBlockedUntilTicks = 0, BedThoughts = new BedThoughtOnset.Due(1000),
                LyingIn = LyingInBed }
            : pawn with { Targets = [.. pawn.Targets.Select(t => t with { Recovery = new NeedRecovery.Rate(.00016) })] }, review =>
            {
                review = WithEveryShippedRoom(review);
                // A pawn asleep in its bed is at the bed (the fixture's inside cell, 1,1): native's route from its start there
                // costs nothing. The start routes to the wall and door cells keep the walk their site rows price.
                var sites = review.PlannedSites.Value;
                return !asleep ? review : review with { PlannedSites = Known(sites with
                    { Routes = EquatableArray.From(sites.Routes.Select(r => r.FromStart && r.To == new Cell(1, 1) ? r with { Ticks = new RouteTicks.Reachable(0d) } : r)) }) };
            });
        var generated = facts.CoupledAlternatives!;
        var full = generated.Context.NeedTasks.Where(n => n.Kind == "rest").OrderByDescending(n => n.Gain).First();
        var roots = generated.Alternatives.Where(c => c.ActivityCompletions.SequenceEqual([full.Task])
            || new[] { ForecastRoofConsequencesTests.Wall, ForecastRoofConsequencesTests.Door }.Any(site =>
                c.Id.StartsWith(site + "/composition/", StringComparison.Ordinal) && !c.Id.Contains("/after/", StringComparison.Ordinal))).ToArray();
        Assert.Equal(3, roots.Length);
        // The schedules that sleep: an observed sleep continues (Pawn_JobTracker keeps the lie-down until the wake), so an
        // asleep pawn's schedule sleeps from the start; one that walks to a site first (now priced, the walk being known)
        // is not a continuation the mood forecast assesses.
        var outcomes = Evaluated(facts, ForecastRoofConsequencesTests.Horizon, roots)
            .Where(o => o.Plan.Allocations.Any(a => a.Activity == full.Task && (!asleep || a.Depart == 0))).ToArray();
        Assert.Contains(outcomes, o => o.Plan.Allocations.Any(a => a.Activity.StartsWith(ForecastRoofConsequencesTests.Wall, StringComparison.Ordinal)));
        foreach (var outcome in outcomes)
        {
            Assert.True(Assert.IsType<ForecastMoodResult>(outcome.Needs["pawn"].Mood).BreakRisk.IsKnown);
            RunsEveryShippedCredit(generated.Context, outcome, ForecastRoofConsequencesTests.Horizon);
        }
    }

    /// <summary>The builder's lie-down in its owned bed in the open (ForecastRoofConsequencesTests.Review), as native reads it.</summary>
    private static readonly NeedLyingIn LyingInBed = new(new("bed", true, false, .00005, 0, 20), false, false);

    /// <summary>
    /// #3882 acceptance: a pawn asleep now on an outcome that does not continue its sleep, the production way
    /// (ForecastRoofConsequencesTests' builder asleep in its owned bed, its bed thoughts given, on the schedules that build the
    /// planned room without the rest). The outcome wakes it at its first order or leaves the lie-down to native until then: the
    /// mood forecast stays whole and every shipped credit runs.
    /// </summary>
    [Fact]
    public void APawnWokenFromItsSleepKeepsItsMoodForecastAndRunsEveryShippedCredit()
    {
        var facts = ForecastRoofConsequencesTests.Review(ForecastRoofConsequencesTests.Closed("Bedroom", 2), pawn => pawn with
        {
            Awake = false, Location = pawn.Targets[0].Location, SleepBlockedUntilTicks = 0, BedThoughts = new BedThoughtOnset.Given(), LyingIn = LyingInBed,
        }, WithEveryShippedRoom);
        var generated = facts.CoupledAlternatives!;
        var roots = generated.Alternatives.Where(c => new[] { ForecastRoofConsequencesTests.Wall, ForecastRoofConsequencesTests.Door }.Any(site =>
            c.Id.StartsWith(site + "/composition/", StringComparison.Ordinal) && !c.Id.Contains("/after/", StringComparison.Ordinal))).ToArray();
        Assert.Equal(2, roots.Length);
        var rests = generated.Context.NeedTasks.Where(n => n.Kind == "rest").Select(n => n.Task).ToHashSet(StringComparer.Ordinal);
        var outcomes = Evaluated(facts, ForecastRoofConsequencesTests.Horizon, roots).Where(o => !o.Plan.Allocations.Any(a => rests.Contains(a.Activity))).ToArray();
        Assert.Contains(outcomes, o => o.Plan.Allocations.Any(a => a.Activity.StartsWith(ForecastRoofConsequencesTests.Wall, StringComparison.Ordinal)));
        foreach (var outcome in outcomes)
        {
            Assert.True(Assert.IsType<ForecastMoodResult>(outcome.Needs["pawn"].Mood).BreakRisk.IsKnown);
            RunsEveryShippedCredit(generated.Context, outcome, ForecastRoofConsequencesTests.Horizon);
        }
        // Sent no order, the pawn sleeps on in its lie-down until its rest reaches the wake threshold (0.8 at 0.00005 a tick).
        var baseline = ColonyForecastRules.ForecastColony(ForecastCoupled.CoupledForecastRequestOf(new("world", 100), ForecastRoofConsequencesTests.Horizon / GameTime.TicksPerDay,
            facts, new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, [], new())).Baseline.Coupled!.Alternatives.Single(o => o.Plan.Allocations.Length == 0);
        Assert.Equal(1, baseline.Needs["pawn"].Needs.Single(n => n.Kind == "rest").FinalLevel.Value, 9);
        Assert.True(Assert.IsType<ForecastMoodResult>(baseline.Needs["pawn"].Mood).BreakRisk.IsKnown);
    }

    /// <summary>
    /// #3882: whether the outcome wakes a pawn asleep now is the binding's (CoordinatedActivityBinding.OrderedAtStart, #3884). A
    /// sleep it departs for at once is an order now, which wakes the pawn at tick 0 (its mood, which freezes while asleep, moves
    /// from there); one departing more than DistantServiceTicks ahead is left to vanilla, so the observed lie-down goes on (0.2
    /// rest gaining 0.00002 a tick, below the wake threshold) and the mood holds until it leaves.
    /// </summary>
    [Fact]
    public void AnOrderWakesASleeperAndADistantSleepLeavesItAsleep()
    {
        var (facts, outcomes) = SleeperRests();
        var context = facts.CoupledAlternatives!.Context;
        bool Ordered(ForecastCoupledOutcome o) => RimGovernor.Brain.Policy.Methods.Binding.CoordinatedActivityBinding.OrderedAtStart(
            ["selected"], o.Plan.Allocations, context.Bindings, context.PlannedSites.SelectMany(s => s.Activities).ToHashSet(StringComparer.Ordinal)).Contains("pawn");
        var woken = outcomes.First(Ordered).Needs["pawn"].Mood!;
        Assert.NotEqual(woken.Trajectory[0].Actual, woken.Trajectory.First(p => p.Tick > 0).Actual);
        var distant = outcomes.First(o => !Ordered(o));
        var asleep = distant.Needs["pawn"].Mood!;
        Assert.True(asleep.BreakRisk.IsKnown);
        Assert.Equal(asleep.Trajectory[0].Actual, asleep.Trajectory.First(p => p.Tick >= distant.Plan.Allocations[0].Depart).Actual);
    }

    /// <summary>The builder asleep in its bed gaining 0.00002 rest a tick, its bed thoughts given, and the forecast outcomes of each
    /// rest alone, as its own exclusive alternative.</summary>
    private static (RoundsFacts Facts, ForecastCoupledOutcome[] Outcomes) SleeperRests()
    {
        var facts = ForecastRoofConsequencesTests.Review(ForecastRoofConsequencesTests.Closed("Bedroom", 2), pawn => pawn with
        {
            Awake = false, Location = pawn.Targets[0].Location, SleepBlockedUntilTicks = 0, BedThoughts = new BedThoughtOnset.Given(),
            LyingIn = LyingInBed with { Place = LyingInBed.Place with { GainPerTick = .00002 } },
        }, WithEveryShippedRoom);
        facts = facts with { CoupledAlternatives = facts.CoupledAlternatives! with { Context = facts.CoupledAlternatives.Context with { MoodTrajectory = true } } };
        var generated = facts.CoupledAlternatives!;
        var rests = generated.Context.NeedTasks.Where(n => n.Kind == "rest").Select(n => n.Task).ToHashSet(StringComparer.Ordinal);
        var outcomes = generated.Alternatives.Where(c => c.ActivityCompletions.Count == 1 && rests.Contains(c.ActivityCompletions[0]))
            .SelectMany(r => ColonyForecastRules.ForecastColony(SleeperRequest(facts, r)).Proposed.Coupled!.Alternatives)
            .Where(o => o.Plan.Allocations.Length == 1).ToArray();
        return (facts, outcomes);
    }

    private static ColonyForecastRequest SleeperRequest(RoundsFacts facts, ForecastCommitment rest) =>
        ForecastCoupled.CoupledForecastRequestOf(new("world", 100), ForecastRoofConsequencesTests.Horizon / GameTime.TicksPerDay,
            facts, new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, [], new() { Add = [rest] });

    /// <summary>
    /// #3884: the forecast wakes a pawn asleep now exactly when the binding orders it at tick 0, in the two cases the copy of the
    /// rule got wrong. A helper's cooking stands beside the sleeper's rest; the sleeper's mood, which freezes while asleep, moves
    /// from tick 0 only when StartSelected sends it an order.
    /// - The schedule departs the sleeper at once to wait for a rest that waits on the cooking and starts far ahead: the binding
    ///   leaves it out, so its lie-down goes on until it must leave.
    /// - The cooking has no binding: Start orders no one, so a rest due soon (which, the cooking bound, is an order now) leaves the
    ///   sleeper asleep.
    /// </summary>
    [Fact]
    public void TheForecastWakesASleeperExactlyWhenTheBindingOrdersIt()
    {
        var (facts, outcomes) = SleeperRests();
        var outcome = outcomes.First(o => o.Plan.Allocations[0].Start > 0);
        var rest = outcome.Plan.Allocations[0];
        var commitment = facts.CoupledAlternatives!.Alternatives.Single(c => c.ActivityCompletions.SequenceEqual([rest.Activity]));
        var request = SleeperRequest(facts, commitment);
        var context = request.Coupled!;
        var cook = new RimGovernor.Brain.Policy.Methods.ActivityTaskCandidate("cook", "helper", [], PawnActivityKind.Work, "stove", "", "FinishFrame", 0, 90000, new(0, 0));
        var sleep = context.Bindings[(rest.Activity, "pawn")];
        context = context with { Activities = context.Activities with { Activities = [.. context.Activities.Activities, new("cook", "start", "start", [])] } };
        ForecastActivityAllocation Rest(double depart, double start) => rest with
        {
            Depart = depart, Arrive = depart + rest.TravelTicks, Start = start, End = start + (rest.End - rest.Start), WaitTicks = start - depart - rest.TravelTicks,
        };
        var cooking = new ForecastActivityAllocation("cook", "helper", 0, 0, 0, 100, 0, 0, 100, 0, 0);
        void Agree(bool ordered, ForecastCoupledContext bound, ForecastActivityAllocation sleeping)
        {
            var plan = new ForecastActivityPlan([sleeping, cooking], sleeping.End, 0);
            var started = RimGovernor.Brain.Policy.Methods.Binding.CoordinatedActivityBinding.StartSelected(new(["selected"], plan, Known(0d), [], 0, false),
                bound.Bindings, 1, "m", 100, bound.PlannedSites);
            Assert.Equal(ordered, started?.Orders.Any(o => o.Pawn == "pawn") == true);
            var mood = new ForecastCoupled.CoupledSearchCache().Evaluate(request with { Coupled = bound },
                new Dictionary<string, ForecastCommitment> { [commitment.Id] = commitment }, bound.Activities, plan).Needs["pawn"].Mood!;
            Assert.True(mood.BreakRisk.IsKnown);
            Assert.Equal(ordered, mood.Trajectory[0].Actual != mood.Trajectory.First(p => p.Tick > 0).Actual);
        }
        Dictionary<(string Task, string Pawn), RimGovernor.Brain.Policy.Methods.ActivityTaskCandidate> Bindings(bool cookBound, bool waits)
        {
            var bindings = context.Bindings.ToDictionary(b => b.Key, b => b.Value);
            if (cookBound) bindings[("cook", "helper")] = cook;
            if (waits) bindings[(rest.Activity, "pawn")] = sleep with { Prerequisites = ["cook"] };
            return bindings;
        }
        // Departed at once to wait 5000 ticks for its rest, which waits on the cooking: left to vanilla, asleep.
        Agree(false, context with { Bindings = Bindings(cookBound: true, waits: true) }, Rest(0, 5000));
        // A rest due in 300 ticks is an order now, with the cooking bound; unbound, no one is ordered.
        Agree(true, context with { Bindings = Bindings(cookBound: true, waits: false) }, Rest(300, 300));
        Agree(false, context with { Bindings = Bindings(cookBound: false, waits: false) }, Rest(300, 300));
    }

    /// <summary>
    /// #3889: the forecast wakes a pawn asleep now exactly when the review orders it at tick 0, the review's own steps before the
    /// binding included. The review runs as Rounder does (ReviewCoupledExecution, then StartSelected on its dispatch); the
    /// forecast (CoupledSearchCache.Evaluate) reads the same dispatch from the context's incumbent hold, the outcome's
    /// commitments its selection. Each case failed while the forecast read the schedule as given.
    /// - The sleeper is a retained incumbent's pawn: its rest due in 300 ticks is dropped, so only the helper is ordered and the
    ///   sleeper sleeps on.
    /// - The helper is the incumbent, its retained service unbound in the schedule: dropped, the rest due soon is ordered.
    /// - Nothing is selected: the review orders no one, so the rest due soon leaves the sleeper asleep.
    /// </summary>
    [Fact]
    public void TheForecastWakesASleeperExactlyWhenTheReviewOrdersIt()
    {
        var (facts, outcomes) = SleeperRests();
        var source = outcomes.First(o => o.Plan.Allocations[0].Start > 0).Plan.Allocations[0];
        // A rest due in 300 ticks: an order now, when the review dispatches it (TheForecastWakesASleeperExactlyWhenTheBindingOrdersIt).
        var rest = source with { Depart = 300, Arrive = 300 + source.TravelTicks, Start = 300, End = 300 + (source.End - source.Start), WaitTicks = -source.TravelTicks };
        var commitment = facts.CoupledAlternatives!.Alternatives.Single(c => c.ActivityCompletions.SequenceEqual([rest.Activity]));
        var request = SleeperRequest(facts, commitment);
        var context = request.Coupled!;
        var cook = new RimGovernor.Brain.Policy.Methods.ActivityTaskCandidate("cook", "helper", [], PawnActivityKind.Work, "stove", "", "FinishFrame", 0, 90000, new(0, 0));
        var bindings = context.Bindings.ToDictionary(b => b.Key, b => b.Value);
        bindings[("cook", "helper")] = cook;
        context = context with
        {
            Bindings = bindings,
            Activities = context.Activities with { Activities = [.. context.Activities.Activities, new("cook", "start", "start", []),
                new("incumbent/service", "start", "start", [])] },
        };
        var cooking = new ForecastActivityAllocation("cook", "helper", 0, 0, 0, 100, 0, 0, 100, 0, 0);
        var service = new ForecastActivityAllocation("incumbent/service", "helper", 0, 0, 0, 100, 0, 0, 100, 0, 0);
        ForecastObservedIncumbent Incumbent(string pawn) => new(new("old-plan"), new(1,
            [new(pawn, "assignment", "method", 0, 1, false, [new(PawnActivityKind.Recreation, "pin", "", "", 0, 1000)])]), "incumbent/service",
            new(pawn, "assignment", "method", 1, "started")) { Activities = ["incumbent/service"] };
        void Agree(bool ordered, string[] selected, ForecastObservedIncumbent[] incumbents, ForecastCoupledContext bound, params ForecastActivityAllocation[] rows)
        {
            var plan = new ForecastActivityPlan(rows, rows.Max(a => a.End), 0);
            var outcome = new ForecastCoupledOutcome(plan, Known(true), new Dictionary<string, ForecastNeedsResult>(), []);
            var vector = ForecastCoupledSelection.CoupledConsequences(outcome, []);
            var selection = new ForecastCoupledSelection(new(selected, plan, Known(0d), [], 0, false), outcome, outcome, vector, vector, [], 100, false);
            var execution = ForecastCoupledExecution.ReviewCoupledExecution(selection, EquatableArray.From(incumbents), 100, bound.Activities.Activities);
            Assert.Empty(execution.Releases);
            var started = execution.Dispatch is { } dispatch
                ? RimGovernor.Brain.Policy.Methods.Binding.CoordinatedActivityBinding.StartSelected(dispatch.Selection, bound.Bindings, 1, "m", 100, bound.PlannedSites) : null;
            Assert.Equal(ordered, started?.Orders.Any(o => o.Pawn == "pawn") == true);
            var held = bound with { IncumbentHold = ForecastIncumbentHold.Of(incumbents) };
            var commitments = selected.ToDictionary(id => id, id => id == commitment.Id ? commitment : new ForecastCommitment(id, []) { ActivityCompletions = [id] });
            var mood = new ForecastCoupled.CoupledSearchCache().Evaluate(request with { Coupled = held }, commitments, held.Activities, plan).Needs["pawn"].Mood!;
            Assert.True(mood.BreakRisk.IsKnown);
            Assert.Equal(ordered, mood.Trajectory[0].Actual != mood.Trajectory.First(p => p.Tick > 0).Actual);
        }
        Agree(false, [commitment.Id, "incumbent/service"], [Incumbent("pawn")], context, rest, cooking);
        Agree(true, [commitment.Id, "incumbent/service"], [Incumbent("helper")], context, rest, service);
        Agree(false, [], [], context, rest, cooking);
    }

    /// <summary>
    /// #3871 acceptance: two colonists on range drills at stands within chat range, the production way (PracticeMethodCandidates'
    /// drills priced by tier 2's skill marginal). Each finishes while the other is on its session, so each gains the TrainedWith
    /// memory (TrainingCompany.NearTrainer): the mood forecast stays whole, the memory is priced (stands out of range leave a lower
    /// thought offset), and every shipped credit runs on the outcome without a throw.
    /// </summary>
    [Fact]
    public void TwoColonistsDrillingTogetherKeepTheirMoodForecastAndRunEveryShippedCredit()
    {
        var trainedWith = NeedsFixtures.Memory("RimGovernor_TrainedWith", 3, Day, false) with { StackLimit = 1 };
        (ForecastCoupledContext, ForecastCoupledOutcome) Drill(bool company)
        {
            var facts = ForecastPortfolioChoiceTests.Observed([], [], helper: true);
            var learning = CapabilityMaintenanceTests.Learning() with { Xp = 0d };
            var roster = facts.ForecastObserved.WorkPawns.Value.Select(p => p with { Skills = Known<EquatableArray<WorkSkill>>([new() { Name = "Shooting", Level = 1, Learning = learning }]) }).ToArray();
            var workers = roster.Select((p, i) => new PracticeWorker(p.Id.Value, "Shooting", "stand/" + i, new(i + 1, 1), "RimGovernor_TrainRange", "RimGovernor_TrainShooting",
                0d, 100d, learning) { CallbackIntervalTicks = 10d, RawXpPerCallback = 10d, PreparationTicks = 0, JoyGainPerTick = 0, Thoughts = [trainedWith], Equipment = [], Findings = [],
                    Company = company ? ["stand/" + (1 - i)] : [] }).ToArray();
            facts = facts with { Hostiles = Known(0L), CriticalPatients = Known(0L), ForecastObserved = facts.ForecastObserved with
            {
                WorkPawns = Known(EquatableArray.From(roster)),
                Needs = facts.ForecastObserved.Needs! with { Pawns = EquatableArray.From(facts.ForecastObserved.Needs!.Pawns.Select(p => p with
                { Needs = EquatableArray.From(p.Needs.Select(n => n with { Level = 1d, FallPerTick = 0d })) })) },
                Coupled = facts.ForecastObserved.Coupled! with { Practice = new() { Workers = EquatableArray.From(workers) } }
            } };
            var investment = roster.ToDictionary(p => ProjectGraph.SkillKey(p.Id.Value, "Shooting"), _ => 1d, StringComparer.Ordinal);
            return Selected(WithEveryShippedRoom(facts), 300, investment);
        }
        var (generated, outcome) = Drill(company: true);
        var drills = generated.PracticeTasks.Where(p => outcome.Plan.Allocations.Any(a => a.Activity == p.Task)).ToArray();
        Assert.Equal(2, drills.Select(d => d.Worker.Pawn).Distinct().Count());
        var (_, alone) = Drill(company: false);
        foreach (var pawn in drills.Select(d => d.Worker.Pawn).Distinct())
        {
            Assert.True(Assert.IsType<ForecastMoodResult>(outcome.Needs[pawn].Mood).BreakRisk.IsKnown);
            Assert.True(outcome.Needs[pawn].ThoughtOffsetAtHorizon.Value > alone.Needs[pawn].ThoughtOffsetAtHorizon.Value);
        }
        RunsEveryShippedCredit(generated, outcome, 300);
    }

    /// <summary>Every outcome the production forecast evaluates over <paramref name="facts"/>, with <paramref name="add"/> proposed.</summary>
    private static ForecastCoupledOutcome[] Evaluated(RoundsFacts facts, double horizon, ForecastCommitment[] add)
    {
        var forecast = ColonyForecastRules.ForecastColony(ForecastCoupled.CoupledForecastRequestOf(new("world", 100), horizon / GameTime.TicksPerDay, facts,
            new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, [], new() { Add = EquatableArray.From(add) }));
        Assert.True(forecast.Proposed.Coupled is { Alternatives.Count: > 0 }, "proposed: " + string.Join(",", forecast.Proposed.Coupled?.Findings ?? []) + " baseline: " + string.Join(",", forecast.Baseline.Coupled?.Findings ?? []));
        return [.. forecast.Proposed.Coupled!.Alternatives];
    }

    /// <summary>One planned room per shipped role numbered from <paramref name="first"/>, each predicted as native predicts every priced room.</summary>
    private static (int Room, PlannedRole Role)[] ShippedRooms(int first = 1) => PlannedRoomCredits.Shipped.Keys.Select((role, i) => (Room: i + first, Role: role)).ToArray();

    /// <summary><paramref name="review"/> with <see cref="ShippedRooms"/> planned after its own planned rooms, the way RoundsRead and
    /// RoundsWork read them.</summary>
    internal static RoundsFacts WithEveryShippedRoom(RoundsFacts review)
    {
        var roles = ShippedRooms(review.PlannedShelters.Count == 0 ? 1 : review.PlannedShelters.Max(r => r.Id) + 1);
        var facts = review with
        {
            // RoundsWork reads each work pawn's skills (a fresh colonist with none it could practise) and health.
            ForecastObserved = review.ForecastObserved with { WorkPawns = Known(EquatableArray.From(review.ForecastObserved.WorkPawns.Value
                .Select(p => p with { Skills = p.Skills.IsKnown ? p.Skills : Known<EquatableArray<WorkSkill>>([]), Health = Known(1d) }))) },
            PlannedShelters = [.. review.PlannedShelters, .. roles.Select(r => new PlannedRoom { Id = r.Room, Role = r.Role,
                Region = RegionRules.RectRegion(new RimGovernor.Brain.Policy.Rectangle { X = 5 + (10 * r.Room), Z = 5, Width = 3, Height = 3 }) })],
            PlannedRoomPredictions = EquatableArray.From(review.PlannedRoomPredictions.Concat(roles.Select(r => PlannedRoomPredictionTests.Closed(
                r.Room.ToString(System.Globalization.CultureInfo.InvariantCulture), "", Wire.Name(r.Role), 30d, 0, 9, 0, false)))),
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
    /// sets the investment horizon and the tending a wound gets over the horizon (ColonyValue.TendingOverHorizon); the round reads the storyteller's
    /// threat schedule (here a quiet one) and its disease schedule (no disease incident yet); the catalog carries the capacity
    /// penalty tables.
    /// </summary>
    private static void RunsEveryShippedCredit(ForecastCoupledContext generated, ForecastCoupledOutcome outcome, double horizon)
    {
        var roles = ShippedRooms(generated.PlannedRoomRoles.Keys.Max() - PlannedRoomCredits.Shipped.Count + 1);
        var context = generated with
        {
            // The round reads no creepjoiner held apart (RoundsCreepJoiner.CreepJoinerForecastOf, #3844).
            CreepJoiners = new() { Unread = "", Held = [] },
            InvestmentHorizonDays = Horizon, Tending = new ForecastTending(true, 0.6), CapacityPenalties = ForecastValueTests.Tables(),
            Harm = ForecastValueTests.RaidDay(raidsPerDay: 0) with { Illness = new IllnessSchedule { Assessed = true }, Containment = generated.Harm.Containment },
        };
        Assert.All(roles, r => Assert.Equal(r.Role, context.PlannedRoomRoles[r.Room]));
        var served = outcome with { ServedRooms = [.. roles.Select(r => new ForecastRoomService(r.Room, "", Day))] };
        foreach (var (room, role) in roles) Assert.NotNull(PlannedRoomCredits.Shipped[role](new(room, "", Day), served, [], context, horizon));
        Assert.NotNull(ForecastValue.Of(served, context, horizon));
    }
}
