using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

// #3803 (epic #3650 P1): a planned dining room is credited the AteInImpressiveDiningRoom memory each colonist's meals gain at
// its predicted Impressiveness stage (and the AteWithoutTable its tables lift), a rec room the JoyActivityInImpressiveRecRoom
// memory its joy activities gain, each through GainPerDay from the room's service day.
public sealed class DiningRecCreditTests
{
    private const double Day = GameTime.TicksPerDay;
    private const double Horizon = 10;
    private static readonly Cell Inside = new(1, 1);

    /// <summary>The core room memories' rows (Thoughts_Memory_RoomStats.xml): awful, dull and mediocre null (rows at 0), then decent +2 to wondrous +8, a day, no fade, stackLimit 1.</summary>
    private static IEnumerable<NeedThoughtStage> RoomStages(string def, string role, bool fades = false, int stackLimit = 1) =>
        Enumerable.Range(0, 10).Select(stage => new NeedThoughtStage(def, role, stage, stage < 3 ? 0 : stage - 1, NeedsFixtures.Lasting(Day, fades, stackLimit)));

    private static readonly NeedThoughtStage WithoutTable = new("AteWithoutTable", ForecastRoomThoughts.AteWithoutTableRole, 0, -3, NeedsFixtures.Lasting(Day, false, 1));

    private static NeedThoughtStage[] Stages(bool fades = false, int stackLimit = 1) =>
        [.. RoomStages("AteInImpressiveDiningRoom", ForecastRoomThoughts.DiningRole, fades, stackLimit), WithoutTable,
            .. RoomStages("JoyActivityInImpressiveRecRoom", ForecastRoomThoughts.RecRole, fades, stackLimit)];

    /// <summary>The room memory rows native reads for every pawn (NativeNeedsForecast.ThoughtStagesOf), with the core defs.</summary>
    internal static NeedThoughtStage[] NativeRows => Stages();

    /// <summary>A colonist eating <paramref name="meals"/> meals a day (its food falls 0.7 a meal, hungry at 0.3) and taking <paramref name="joys"/> joy activities a day.</summary>
    private static NeedsForecastPawn Colonist(string id, double meals = 2, double joys = 0, NeedThoughtStage[]? stages = null) => ForecastValueTests.Pawn() with
    {
        Pawn = id,
        Needs = [NeedsFixtures.Rate("food", 1, meals * 0.7 / Day, 0.3), NeedsFixtures.Rate("joy", 1, joys * 0.7 / Day, 0.3)],
        ThoughtStages = EquatableArray.From(stages ?? Stages()),
    };

    private static ForecastPlannedClosure Closure(int room, string role, int stage) =>
        new(room, "WoodLog", [Inside], [FormattableString.Invariant($"planned/room{room}/wall/WoodLog")], [],
            PlannedRoomPredictionTests.Closed(room.ToString(System.Globalization.CultureInfo.InvariantCulture), "WoodLog", role, 30, stage, 20, 0, false));

    private static ForecastCoupledContext Context(NeedsForecastPawn[] pawns, (int Room, PlannedRole Role)[] rooms, ForecastPlannedClosure[] closures, params MoodThought[] observed) =>
        ForecastValueTests.Context(pawns[0]) with
        {
            Needs = NeedsFixtures.Empty with { Pawns = [.. pawns] },
            PlannedRoomRoles = rooms.ToDictionary(r => r.Room, r => r.Role),
            Closures = [.. closures],
            InvestmentHorizonDays = Horizon,
            ColonistThoughts = pawns.ToDictionary(p => p.Pawn, _ => EquatableArray.From(observed), StringComparer.Ordinal),
        };

    private static ForecastCoupledContext Dining(int stage, NeedsForecastPawn[] pawns, params MoodThought[] observed) =>
        Context(pawns, [(1, PlannedRole.Dining)], [Closure(1, "DiningRoom", stage)], observed);

    /// <summary>Each pawn a day in the mildest break tier unshifted, none once its mood shifts by any of <paramref name="shifts"/>; the rooms serve at their ticks.</summary>
    private static ForecastCoupledOutcome Serving(string[] pawns, double[] shifts, params (int Room, double At)[] served) =>
        new(new([], 0, 0), Known(true), pawns.ToDictionary(p => p, p => new ForecastNeedsResult([], default, default, new(new(p, "", new ForecastCapacityWindows.Unread(), 0), []), [])
        {
            Mood = new([], default, default, default, default, default, [])
            {
                PressureTicks = Known(0d), BreakRisk = Known(0d), BreakTierTicks = [Day, 0, 0],
                ShiftedBreakTierTicks = [.. shifts.Select(s => new ForecastShiftedBreaks(s, [0, 0, 0]))],
            },
        }), [])
        { ServedRooms = [.. served.Select(s => new ForecastRoomService(s.Room, "WoodLog", s.At))] };

    private static ForecastInvestmentContribution[] Rows(ForecastCoupledContext context, ForecastCoupledOutcome outcome, string role) =>
        [.. ForecastValue.Of(outcome, context, Day).InvestmentContributions.Where(r => r.Key.StartsWith(role + "/", StringComparison.Ordinal)).OrderBy(r => r.Key, StringComparer.Ordinal)];

    private static double Gain(ForecastCoupledContext context, ForecastCoupledOutcome outcome, string pawn, double delta) =>
        ForecastHousing.GainPerDay(delta, outcome.Needs[pawn].Mood, context.MoodBreaks, context.Value.WellbeingWeight, 1)!.Value;

    private static string[] Findings(ForecastCoupledContext context, ForecastCoupledOutcome outcome, PlannedRole role, int room = 1) =>
        [.. PlannedRoomCredits.Unmodelled[role](outcome.ServedRooms.Single(s => s.Room == room), outcome, [], context, Day)];

    [Fact]
    public void ThreeColonistsEatingTwoMealsADayAreCreditedTheDecentStageMoodThroughGainPerDay()
    {
        string[] ids = ["a", "b", "c"];
        var context = Dining(3, [.. ids.Select(id => Colonist(id))]);
        Assert.True(context.Priced(1));
        var outcome = Serving(ids, [0.02], (1, Day / 2));
        var rows = Rows(context, outcome, "dining");
        Assert.Equal(["dining/1/a", "dining/1/b", "dining/1/c"], rows.Select(r => r.Key));
        foreach (var row in rows)
        {
            // Two meals a day renew the day-long +2 memory: it is held the whole day.
            Assert.Equal(Horizon - 0.5, row.Units);
            Assert.Equal(-Gain(context, outcome, row.Pawn, 2) * (Horizon - 0.5), row.Value, 12);
        }

        Assert.Equal([0.02], PlannedRoomCredits.BreakShifts(context)["a"]);
        Assert.Contains("dining_table_reach", Findings(context, outcome, PlannedRole.Dining));
    }

    [Fact]
    public void ARecRoomIsCreditedTheMemoryItsJoyActivitiesHold()
    {
        // Half a joy activity a day holds the day-long +3 (slightly impressive) memory half the time.
        var pawn = Colonist("a", joys: 0.5);
        var context = Context([pawn], [(1, PlannedRole.Rec)], [Closure(1, "RecRoom", 4)]);
        var outcome = Serving(["a"], [0.015], (1, 0));
        var row = Assert.Single(Rows(context, outcome, "rec"));
        Assert.Equal(("rec/1/a", Horizon), (row.Key, row.Units));
        Assert.Equal(-Gain(context, outcome, "a", 1.5) * Horizon, row.Value, 12);
        Assert.Contains("rec_joy_kind", Findings(context, outcome, PlannedRole.Rec));
    }

    [Fact]
    public void ADullDiningRoomLiftsTheAteWithoutTableHeldNowAndTheBetterRoomThoughtIsLost()
    {
        // Dull gives no memory; the tables lift AteWithoutTable's -3.
        var lifted = Dining(1, [Colonist("a")], new MoodThought("AteWithoutTable", -3));
        var outcome = Serving(["a"], [0.03], (1, 0));
        Assert.Equal(-Gain(lifted, outcome, "a", 3) * Horizon, Assert.Single(Rows(lifted, outcome, "dining")).Value, 12);
        // A colonist eating in an impressive room now (+5) loses 3 of it at a decent planned dining room.
        var worse = Dining(3, [Colonist("a")], new MoodThought("AteInImpressiveDiningRoom", 5));
        var lost = Serving(["a"], [-0.03], (1, 0));
        Assert.Equal(-Gain(worse, lost, "a", -3) * Horizon, Assert.Single(Rows(worse, lost, "dining")).Value, 12);
    }

    [Fact]
    public void ALaterDiningRoomIsCreditedOnlyItsGainOverTheEarlierOne()
    {
        var context = Context([Colonist("a")], [(1, PlannedRole.Dining), (2, PlannedRole.Dining)], [Closure(1, "DiningRoom", 3), Closure(2, "DiningRoom", 5)]);
        Assert.Equal([-0.02, 0.02, 0.04], PlannedRoomCredits.BreakShifts(context)["a"]);
        var outcome = Serving(["a"], [0.02], (1, 0), (2, 2 * Day));
        var rows = Rows(context, outcome, "dining");
        Assert.Equal(["dining/1/a", "dining/2/a"], rows.Select(r => r.Key));
        Assert.Equal(-Gain(context, outcome, "a", 2) * Horizon, rows[0].Value, 12);
        Assert.Equal(-Gain(context, outcome, "a", 2) * (Horizon - 2), rows[1].Value, 12);
    }

    [Fact]
    public void AHeldMemoryIsRenewedAtEachUseAndAFadingOneAveragesBetweenUses()
    {
        Assert.Equal(4, ForecastRoomThoughts.Held(4, 1, false, 3), 12);
        Assert.Equal(1, ForecastRoomThoughts.Held(4, 1, false, 0.25), 12);
        Assert.Equal(4 * (1 - 1 / 4d), ForecastRoomThoughts.Held(4, 1, true, 2), 12);
        Assert.Equal(4 * 0.25, ForecastRoomThoughts.Held(4, 1, true, 0.5), 12);
        Assert.Equal(0, ForecastRoomThoughts.Held(4, 1, false, 0));
        Assert.Equal(2, ForecastRoomThoughts.UsesPerDay(NeedsFixtures.Rate("food", 1, 1.4 / Day, 0.3)), 12);
    }

    [Fact]
    public void ARoomWithoutTablesOrRecreationAColonyWithoutUsersAndAStackedMemoryAreNamedAndEarnNothing()
    {
        var outcome = Serving(["a"], [0.02], (1, 0));
        var tableless = Context([Colonist("a")], [(1, PlannedRole.Dining)], [Closure(1, "Room", 3)]);
        Assert.Empty(Rows(tableless, outcome, "dining"));
        Assert.Contains(ForecastRoomThoughts.NoTable, Findings(tableless, outcome, PlannedRole.Dining));
        var bare = Context([Colonist("a", joys: 1)], [(1, PlannedRole.Rec)], [Closure(1, "Room", 3)]);
        Assert.Empty(Rows(bare, outcome, "rec"));
        Assert.Contains(ForecastRoomThoughts.NoRecreation, Findings(bare, outcome, PlannedRole.Rec));
        var moodless = Dining(3, [Colonist("a") with { Mood = null }]);
        Assert.Empty(Rows(moodless, outcome, "dining"));
        Assert.Contains(ForecastRoomThoughts.NoDiner, Findings(moodless, outcome, PlannedRole.Dining));
        var stacked = Dining(3, [Colonist("a", stages: Stages(stackLimit: 3))]);
        Assert.Empty(Rows(stacked, outcome, "dining"));
        Assert.Contains(ForecastRoomThoughts.Stacked, Findings(stacked, outcome, PlannedRole.Dining));
        var open = Context([Colonist("a")], [(1, PlannedRole.Dining)], []);
        Assert.Empty(Rows(open, outcome, "dining"));
        Assert.Contains(ForecastRoomThoughts.NotClosed, Findings(open, outcome, PlannedRole.Dining));
    }

    [Fact]
    public void AnUnreadStageRowThoughtDefOrObservedThoughtsIsARequiredFact()
    {
        var outcome = Serving(["a"], [0.02], (1, 0));
        var unstaged = Dining(3, [Colonist("a", stages: [.. Stages().Where(s => !(s.Role == ForecastRoomThoughts.DiningRole && s.Stage == 3))])]);
        Assert.StartsWith(ForecastRoomThoughts.DiningRole + " thought stage 3:a", Assert.Throws<RequiredFactException>(() => ForecastValue.Of(outcome, unstaged, Day)).Fact, StringComparison.Ordinal);
        var tableUnread = Dining(3, [Colonist("a", stages: [.. Stages().Where(s => s.Role != ForecastRoomThoughts.AteWithoutTableRole)])]);
        Assert.StartsWith(ForecastRoomThoughts.AteWithoutTableRole + " thought:a", Assert.Throws<RequiredFactException>(() => ForecastValue.Of(outcome, tableUnread, Day)).Fact, StringComparison.Ordinal);
        var unobserved = Dining(3, [Colonist("a")]) with { ColonistThoughts = new Dictionary<string, EquatableArray<MoodThought>>() };
        Assert.StartsWith("observed thoughts:a", Assert.Throws<RequiredFactException>(() => ForecastValue.Of(outcome, unobserved, Day)).Fact, StringComparison.Ordinal);
    }

    [Fact]
    public void AFreshColonysProductionContextPricesItsDiningAndRecRoomsAndNothingBeforeTheFirstLayoutPlan()
    {
        // The production path (#3822): GenerateCoupledActivities builds the context from the review's facts, the planned rooms
        // predicted as native predicts every priced room furnished, each colonist's room memory rows as native reads them and its
        // thoughts as RoundsMood reads them (none held yet).
        var dining = new PlannedRoom { Id = 2, Role = PlannedRole.Dining, Region = RegionRules.RectRegion(new() { X = 10, Z = 0, Width = 5, Height = 5 }) };
        var rec = new PlannedRoom { Id = 3, Role = PlannedRole.Rec, Region = RegionRules.RectRegion(new() { X = 20, Z = 0, Width = 5, Height = 5 }) };
        var review = ForecastCoupledTests.ConstructionReview();
        var needs = review.ForecastObserved.Needs!;
        var facts = review with
        {
            PlannedShelters = [dining, rec],
            PlannedRoomPredictions = [PlannedRoomPredictionTests.Closed("2", "", "DiningRoom", 30, 3, 25, 0, false), PlannedRoomPredictionTests.Closed("3", "", "RecRoom", 30, 4, 25, 0, false)],
            ForecastObserved = review.ForecastObserved with { Needs = needs with { Pawns = [.. needs.Pawns.Select(p => p with { ThoughtStages = EquatableArray.From(NativeRows) })] } },
            MoodPawns = Known(EquatableArray.From(needs.Pawns.Select(p => new MoodPawn { Id = new(p.Pawn), Thoughts = [] }))),
            // The round reads the mood break curve (MoodBreak).
            MoodBreak = ForecastValueTests.Context(ForecastValueTests.Pawn()).MoodBreaks,
        };
        var context = ForecastCoupledCandidates.GenerateCoupledActivities(facts)!.Context with { InvestmentHorizonDays = Horizon };
        Assert.Equal(PlannedRole.Dining, context.PlannedRoomRoles[2]);
        Assert.Equal(PlannedRole.Rec, context.PlannedRoomRoles[3]);
        Assert.True(context.Priced(2) && context.Priced(3));
        // The colonist eats many meals a day: the decent dining room's +2 is held all day. It has no joy need: the rec room serves nobody.
        var outcome = Serving(["pawn"], [0.02], (2, 0), (3, 0));
        var row = Assert.Single(ForecastRoomThoughts.DiningCredit(outcome.ServedRooms[0], outcome, [], context, Day));
        Assert.Equal(("pawn", Gain(context, outcome, "pawn", 2)), (row.Pawn, row.PerDay));
        Assert.Equal([0.02], PlannedRoomCredits.BreakShifts(context)["pawn"]);
        Assert.Empty(ForecastRoomThoughts.RecCredit(outcome.ServedRooms[1], outcome, [], context, Day));
        Assert.Contains(ForecastRoomThoughts.NoRecUser, ForecastRoomThoughts.RecFindings(outcome.ServedRooms[1], outcome, [], context, Day));

        // Before the first layout plan no room is planned: nothing is served and no fact is read.
        var unplanned = ForecastCoupledCandidates.GenerateCoupledActivities(review)!.Context with { InvestmentHorizonDays = Horizon };
        Assert.DoesNotContain(unplanned.PlannedRoomRoles.Values, r => r is PlannedRole.Dining or PlannedRole.Rec);
        Assert.Empty(ForecastRoomThoughts.Shifts(unplanned));
        Assert.DoesNotContain(ForecastValue.Of(Serving(["pawn"], []), unplanned, Day).InvestmentContributions, r => r.Key.StartsWith("dining/", StringComparison.Ordinal));
    }
}