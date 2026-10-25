using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class DiningRecCreditTests
{
    private const double Day = GameTime.TicksPerDay;
    private const double Horizon = 10;
    private static readonly Cell Inside = new(1, 1);
    internal static NeedThoughtStage[] NativeRows =>
    [
        .. Enumerable.Range(0, 10).Select(s => new NeedThoughtStage("AteInImpressiveDiningRoom", ForecastRoomThoughts.DiningRole, s, s < 3 ? 0 : s - 1, new ThoughtLasting.Memory(Day, false, 1, 1, false))),
        .. Enumerable.Range(0, 10).Select(s => new NeedThoughtStage("JoyActivityInImpressiveRecRoom", ForecastRoomThoughts.RecRole, s, s < 3 ? 0 : s - 1, new ThoughtLasting.Memory(Day, false, 1, 1, false))),
        new("AteWithoutTable", ForecastRoomThoughts.AteWithoutTableRole, 0, -3, new ThoughtLasting.Memory(Day, false, 1, 1, false)),
    ];

    private static NeedsForecastPawn Colonist(string id, double meals = 2, double joys = 0) => ForecastValueTests.Pawn() with
    {
        Pawn = id, RoomUses = new([], [new("ChessTable", 1, new(Day, false, true))]),
        Needs = [NeedsFixtures.Rate("food", 1, meals * .7 / Day, .3),
            NeedsFixtures.Rate("joy", 1, joys == 0 ? 0 : .7 * joys / (Day - .7 * joys), .3) with { OutdoorShareByTwelfth = [.. new double[12]] }],
        Targets = [NeedsFixtures.Food("meal", "MealSimple", 1, 0, 100, .7) with { Meal = new(32, false) }],
        ThoughtStages = [.. NativeRows],
    };

    private static ForecastPlannedClosure Closure(int room, string role, int stage, Cell[]? seats = null) =>
        new(room, "WoodLog", [Inside], [FormattableString.Invariant($"planned/room{room}/wall/WoodLog")], [],
            PlannedRoomPredictionTests.Closed(room.ToString(System.Globalization.CultureInfo.InvariantCulture), "WoodLog", role, 30, stage, 20, 0, false) with
            {
                Services = new(role == "DiningRoom" ? [.. (seats ?? [Inside, new(2, 1), new(3, 1)])] : [],
                    role == "RecRoom" ? [new("ChessTable", Inside, 1, true)] : []),
            });

    private static ForecastCoupledContext Context(NeedsForecastPawn[] pawns, params ForecastPlannedClosure[] closures) =>
        ForecastValueTests.Context(pawns[0]) with
        {
            Needs = NeedsFixtures.Empty with { Pawns = [.. pawns] },
            PlannedRoomRoles = closures.ToDictionary(c => c.Room, c => c.Prediction.Role == "RecRoom" ? PlannedRole.Rec : PlannedRole.Dining),
            Closures = [.. closures], InvestmentHorizonDays = Horizon,
            ColonistThoughts = pawns.ToDictionary(p => p.Pawn, _ => EquatableArray.From(Array.Empty<MoodThought>()), StringComparer.Ordinal),
        };

    private static ForecastCoupledOutcome Serving(string[] pawns, params (int Room, double At)[] served) =>
        new(new([], 0, 0), true, pawns.ToDictionary(p => p, p => new ForecastNeedsResult([], new ForecastTerm.Unassessed("not_projected"), new ForecastTerm.Unassessed("not_projected"), new(new(p, "", new ForecastCapacityWindows.Unread(), 0), []), [])
        {
            Mood = MoodResults.Flat(0d, [Day, 0, 0]) with
            { Response = new(.34, .12 / 2500, .08 / 2500, .35, [new(0, Day, .34, .34, false)]) },
        }), []) { ServedRooms = [.. served.Select(s => new ForecastRoomService(s.Room, "WoodLog", s.At))] };

    private static ForecastInvestmentContribution[] Rows(ForecastCoupledContext context, ForecastCoupledOutcome outcome, string role) =>
        [.. ForecastValue.Of(outcome, context, Day).InvestmentContributions.Where(r => r.Key.StartsWith(role + "/", StringComparison.Ordinal)).OrderBy(r => r.Key, StringComparer.Ordinal)];

    private static double Gain(ForecastCoupledContext context, ForecastCoupledOutcome outcome, string pawn, double delta) =>
        ForecastHousing.GainPerDay(delta, outcome.Needs[pawn].Mood, context.MoodBreaks, context.Value.WellbeingWeight, 1)!.Value;

    [Fact]
    public void SharedLedgerCreditsThreeDinersOnlyAfterTheFurnishedRoomServes()
    {
        string[] ids = ["a", "b", "c"];
        var context = Context([.. ids.Select(id => Colonist(id))], Closure(1, "DiningRoom", 3));
        var outcome = Serving(ids, (1, Day / 2));
        var rows = Rows(context, outcome, "dining");
        Assert.Equal(["dining/1/a", "dining/1/b", "dining/1/c"], rows.Select(r => r.Key));
        Assert.All(rows, r => Assert.Equal(-Gain(context, outcome, r.Pawn, 2) * (Horizon - .5), r.Value, 12));
    }

    [Fact]
    public void FoodPickupRadiusAndSimultaneousSeatClaimsChangeTheActualProductionCredit()
    {
        var pawn = Colonist("a");
        var near = Closure(1, "DiningRoom", 3, [Inside]);
        var far = Closure(1, "DiningRoom", 3, [new(100, 100)]);
        Assert.NotEmpty(Rows(Context([pawn], near), Serving(["a"], (1, 0)), "dining"));
        Assert.Empty(Rows(Context([pawn], far), Serving(["a"], (1, 0)), "dining"));
        var people = new[] { pawn, Colonist("b"), Colonist("c") };
        var context = Context(people, near, Closure(2, "DiningRoom", 3, [new(2, 1)]));
        var outcome = Serving(["a", "b", "c"], (1, 0), (2, Day));
        var rows = Rows(context, outcome, "dining");
        Assert.Equal(["dining/1/a", "dining/2/b"], rows.Select(r => r.Key));
        Assert.Equal(-Gain(context, outcome, "a", 2) * Horizon, rows[0].Value, 12);
        Assert.Equal(-Gain(context, outcome, "b", 2) * (Horizon - 1), rows[1].Value, 12);
    }

    [Fact]
    public void AFreeSecondSeatServesASecondDinerRatherThanPayingForTheFirstDinerAgain()
    {
        var context = Context([Colonist("a"), Colonist("b")], Closure(1, "DiningRoom", 3, [Inside]), Closure(2, "DiningRoom", 4, [new(2, 1)]));
        var outcome = Serving(["a", "b"], (1, 0), (2, Day));
        var rows = Rows(context, outcome, "dining");
        Assert.Equal(["dining/1/a", "dining/2/b"], rows.Select(r => r.Key));
        Assert.Equal(-Gain(context, outcome, "b", 3) * (Horizon - 1), rows[1].Value, 12);
    }

    [Fact]
    public void MealNutritionWholeCountsAndHungryThresholdAreSharedWithTheColonyCycle()
    {
        var pawn = Colonist("a");
        var need = pawn.Needs.Single(n => n.Kind == "food");
        var small = NeedsFixtures.Food("raw", "Rice", 2, 10, 100, .1);
        Assert.Equal(.2, NeedServiceRules.MealGain(small, need), 12);
        var full = small with { AvailableCount = 16 };
        Assert.Equal(.7, NeedServiceRules.MealGain(full, need), 12);
        pawn = pawn with { Targets = [small] };
        var service = Assert.IsType<ModelService.Observed>(NeedServiceRules.Steady(pawn, need).Service);
        Assert.Equal(.2, service.GainPerUse, 12);
        Assert.Equal((100d, 10d), (service.ServiceTicks, service.ApproachTicks));
        var limited = Context([pawn], Closure(1, "DiningRoom", 3));
        var ordinary = Context([Colonist("a")], Closure(1, "DiningRoom", 3));
        // Short-lived modded memory makes differing meal counts visible even when both diners are continuously fed.
        NeedsForecastPawn Short(NeedsForecastPawn p) => p with { ThoughtStages = [.. p.ThoughtStages.Select(s => s with { Lasting = new ThoughtLasting.Memory(Day / 100, false, 1, 1, false) })] };
        limited = limited with { Needs = limited.Needs with { Pawns = [Short(pawn)] } };
        ordinary = ordinary with { Needs = ordinary.Needs with { Pawns = [Short(Colonist("a"))] } };
        Assert.True(RoomUseRules.Of(limited, true, [limited.Closures[0]]).Pawns["a"] > RoomUseRules.Of(ordinary, true, [ordinary.Closures[0]]).Pawns["a"]);
    }

    [Fact]
    public void BaselineComesFromCurrentMealRoomsAndNotFromAMemoryHeldForever()
    {
        var pawn = Colonist("a");
        var closure = Closure(1, "DiningRoom", 3, [Inside]);
        var context = Context([pawn], closure) with { ColonistThoughts = new Dictionary<string, EquatableArray<MoodThought>> { ["a"] = [new("AteInImpressiveDiningRoom", 8)] } };
        var outcome = Serving(["a"], (1, 0));
        Assert.Equal(-Gain(context, outcome, "a", 2) * Horizon, Assert.Single(Rows(context, outcome, "dining")).Value, 12);
        pawn = pawn with { RoomUses = pawn.RoomUses! with { Seats = [new(Inside, new NeedRoomStage.Staged(6))] } };
        context = Context([pawn], closure);
        Assert.Equal(-Gain(context, outcome, "a", -3) * Horizon, Assert.Single(Rows(context, outcome, "dining")).Value, 12);
        pawn = Colonist("a") with { Targets = [pawn.Targets[0] with { Meal = new(32, true) }] };
        context = Context([pawn], Closure(1, "DiningRoom", 1, [Inside]));
        Assert.Equal(-Gain(context, outcome, "a", 3) * Horizon, Assert.Single(Rows(context, outcome, "dining")).Value, 12);
    }

    [Fact]
    public void RecreationUsesTheBestEligibleKindAtItsNativeToleranceAndOutdoorShare()
    {
        var pawn = Colonist("a", joys: .5);
        var context = Context([pawn], Closure(1, "RecRoom", 4));
        var outcome = Serving(["a"], (1, 0));
        Assert.Equal(-Gain(context, outcome, "a", 1.5) * Horizon, Assert.Single(Rows(context, outcome, "rec")).Value, 10);
        pawn = pawn with { Needs = [.. pawn.Needs.Select(n => n.Kind == "joy" ? n with { UnservedGainPerTick = 2, OutdoorShareByTwelfth = [.. Enumerable.Repeat(1d, 12)] } : n)] };
        Assert.Empty(Rows(Context([pawn], Closure(1, "RecRoom", 4)), outcome, "rec"));
        // A faster standing source elsewhere remains the steady service even after the room is built.
        pawn = Colonist("a", joys: .5) with { Targets = [NeedsFixtures.Service("outside-room", "joy", 0, 2) with { RoomStage = new NeedRoomStage.Staged(3) }] };
        Assert.Empty(Rows(Context([pawn], Closure(1, "RecRoom", 4)), outcome, "rec"));
        // Native eligibility refuses the proposed kind: it cannot acquire a rate from another pawn's tolerance.
        pawn = Colonist("a", joys: .5) with { RoomUses = new([], []) };
        Assert.Empty(Rows(Context([pawn], Closure(1, "RecRoom", 4)), outcome, "rec"));
    }

    [Fact]
    public void ShortNativeJoyJobsProduceMoreRoomMemoriesThanOneFullRefill()
    {
        var pawn = Colonist("a", joys: .1);
        var need = pawn.Needs.Single(n => n.Kind == "joy");
        pawn = pawn with
        {
            RoomUses = new([], [new("ChessTable", .001, new(100, false, true))]),
            ThoughtStages = [.. pawn.ThoughtStages.Select(s => s with { Lasting = new ThoughtLasting.Memory(Day / 100, false, 1, 1, false) })],
        };
        var context = Context([pawn], Closure(1, "RecRoom", 4));
        var expected = 3 * .01 * Day / (100 + .1 / need.FallPerTick);
        Assert.Equal(expected, RoomUseRules.Of(context, false, [context.Closures[0]]).Pawns["a"], 12);
        pawn = pawn with { RoomUses = new([], [new("ChessTable", .001, new(100, false, false))]) };
        Assert.Empty(Rows(Context([pawn], Closure(1, "RecRoom", 4)), Serving(["a"], (1, 0)), "rec"));
    }

    [Theory]
    [InlineData(false, 2, 3, .5, 3)]
    [InlineData(false, 4, 2, .5, 3)]
    [InlineData(false, 2.5, -1, .5, 2)]
    [InlineData(false, 2.5, 0, .5, 2)]
    [InlineData(true, 2, 3, .5, 1.5)]
    [InlineData(true, 4, 2, .5, 2.25)]
    [InlineData(true, .5, 3, .5, .5)]
    [InlineData(false, 0, 1, 1, 0)]
    public void RoomMemoriesUseNativeGroupMultiplierAndMeanFadingOffsets(bool fades, double uses, int limit, double multiplier, double expected) =>
        Assert.Equal(expected, ForecastRoomThoughts.Held(2, new ThoughtLasting.Memory(Day, fades, limit, multiplier, false), uses), 12);

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void ExistingDisturbedSleepPricingAndRoomPricingBothRenewTheFirstMemoryAtNonpositiveLimits(int limit)
    {
        var disturbed = new MemoryThought(-1, 1, limit, .5);
        Assert.Equal(-1, disturbed.Mood(20), 12);
        Assert.Equal(disturbed.Mood(20), ForecastRoomThoughts.Held(-1,
            new ThoughtLasting.Memory(Day, false, limit, .5, false), 20), 12);
    }

    [Theory]
    [InlineData(true, 5.25)]
    [InlineData(false, 7)]
    public void NativeStagesStackControlsWhetherDifferentRoomStagesShareOneMemoryGroup(bool stagesStack, double expected)
    {
        var pawn = Colonist("a") with
        {
            ThoughtStages = [.. NativeRows.Select(s => s with { Lasting = new ThoughtLasting.Memory(Day, false, 2, .5, stagesStack) })],
        };
        Assert.Equal(expected, RoomUseRules.Memory(pawn, ForecastRoomThoughts.DiningRole,
            new Dictionary<int, double> { [3] = 1, [6] = 1 }), 12);
    }

    [Fact]
    public void CombinedRoomPricingReusesTheExactSharedMoodResponseInsteadOfEnumeratingRoomSubsets()
    {
        var pawn = Colonist("a");
        var shifts = new[] { -.037, .023 };
        var mood = Assert.IsType<ForecastMoodResult.Projected>(ForecastMood.ForecastMoodTimeline(pawn, Day, [], breakShifts: shifts, response: true));
        var model = ForecastValueTests.Context(pawn).MoodBreaks;
        foreach (var shift in shifts)
            Assert.Equal(ForecastHousing.GainPerDay(shift * 100, mood with { Response = null }, model, 1, 1)!.Value,
                ForecastHousing.GainPerDay(shift * 100, mood, model, 1, 1)!.Value, 12);
        Assert.NotNull(ForecastHousing.GainPerDay(1.137, mood, model, 1, 1));
    }

    [Fact]
    public void UnobservedMechanicsFailRequiredFactsAndAbsentFurnitureEarnsNothing()
    {
        var pawn = Colonist("a");
        var context = Context([pawn], Closure(1, "DiningRoom", 3));
        var outcome = Serving(["a"], (1, 0));
        Assert.StartsWith("room service mechanics:a", Assert.Throws<RequiredFactException>(() => Rows(context with { Needs = context.Needs with { Pawns = [pawn with { RoomUses = null }] } }, outcome, "dining")).Fact, StringComparison.Ordinal);
        Assert.StartsWith("meal table mechanics:meal", Assert.Throws<RequiredFactException>(() => Rows(context with { Needs = context.Needs with { Pawns = [pawn with { Targets = [pawn.Targets[0] with { Meal = null }] }] } }, outcome, "dining")).Fact, StringComparison.Ordinal);
        Assert.Empty(Rows(Context([pawn], Closure(1, "Room", 3)), outcome, "dining"));
        Assert.Empty(Rows(Context([pawn with { Mood = null }], Closure(1, "DiningRoom", 3)), outcome, "dining"));
    }

    [Fact]
    public void OrdinaryProductionCarriesCanonicalRoomMechanicsIntoTheInvestmentLedger()
    {
        var review = ForecastCoupledTests.ConstructionReview();
        var pawn = Colonist("pawn");
        var dining = new PlannedRoom { Id = 2, Role = PlannedRole.Dining, Region = RegionRules.RectRegion(new() { X = 10, Z = 0, Width = 5, Height = 5 }) };
        var facts = review with
        {
            PlannedShelters = [dining],
            PlannedRoomPredictions = [Closure(2, "DiningRoom", 3, [new(11, 1)]).Prediction with { Stuff = "" }],
            ForecastObserved = review.ForecastObserved with { Needs = review.ForecastObserved.Needs! with { Pawns = [pawn] } },
            MoodPawns = [MoodFixtures.Pawn("pawn")], MoodBreak = ForecastValueTests.Context(pawn).MoodBreaks,
        };
        var context = ForecastCoupledCandidates.GenerateCoupledActivities(facts)!.Context with { InvestmentHorizonDays = Horizon };
        Assert.Same(pawn.RoomUses, context.Needs.Pawns[0].RoomUses);
        var outcome = Serving(["pawn"], (2, 0));
        Assert.Equal(-Gain(context, outcome, "pawn", 2) * Horizon, Assert.Single(Rows(context, outcome, "dining")).Value, 12);
        var unplanned = ForecastCoupledCandidates.GenerateCoupledActivities(review)!.Context with { InvestmentHorizonDays = Horizon };
        Assert.Empty(Rows(unplanned, Serving(["pawn"]), "dining"));
    }
}
