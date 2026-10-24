using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3806: a planned morgue or tomb is credited with the observed-corpse moods it removes: per colonist, the memories its ideo
/// gives on sighting the corpses the room takes, active from each of its own recorded sightings for the memory's duration,
/// stacked as the game's group stacks them, priced through GainPerDay from the room's service day.
/// </summary>
public sealed class CorpseCreditTests
{
    private const double Day = GameTime.TicksPerDay;
    private const int Ticks = GameTime.TicksPerDay;
    private const double Horizon = 10;

    /// <summary>Core's ObservedLayingCorpse under Corpses_Ugly: -4, half a day, stackLimit 3, stackedEffectMultiplier 0.5.</summary>
    private static readonly CorpseThoughtRead Fresh = new(false, "ObservedLayingCorpse", -4, Day / 2, 3, 0.5);

    /// <summary>Core's ObservedLayingRottingCorpse: -6, half a day, stackLimit 5, stackedEffectMultiplier 0.5.</summary>
    private static readonly CorpseThoughtRead Rotten = new(true, "ObservedLayingRottingCorpse", -6, Day / 2, 5, 0.5);

    /// <summary>A colonist with two days of sighting record.</summary>
    private static NeedsForecastPawn Pawn(int record = 2 * Ticks) => ForecastValueTests.Pawn() with { CorpseThoughts = [Fresh, Rotten], SightingTicks = record };

    /// <summary>A corpse by the kitchen door, which the colonist passes at the same hour each day: seen 6 hours and a day and 6 hours ago.</summary>
    private static CorpseSightingRead ByTheKitchenDoor(bool colonist = false, bool rotting = false) =>
        new(rotting, colonist, [new("pawn", [Ticks + Ticks / 4, Ticks / 4])]);

    private static ForecastCoupledContext Context(NeedsForecastPawn pawn, PlannedRole role = PlannedRole.Morgue, params CorpseSightingRead[] corpses) =>
        ForecastValueTests.Context(pawn) with
        {
            Needs = NeedsFixtures.Empty with { Pawns = [pawn], Corpses = EquatableArray.From(corpses) },
            PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = role },
            InvestmentHorizonDays = Horizon,
        };

    /// <summary>The room serves from <paramref name="servedAt"/>; no break unshifted, a day in the mildest tier once shifted by <paramref name="shift"/>.</summary>
    private static ForecastCoupledOutcome Outcome(double shift, double servedAt = 2 * Day, params ForecastRoomService[] rooms) =>
        new(new([], 0, 0), Known(true), new Dictionary<string, ForecastNeedsResult>
        {
            ["pawn"] = new([], default, default, new(new("pawn", "", new ForecastCapacityWindows.Unread(), 0), []), [])
            {
                Mood = MoodResults.Flat(0d, [0, 0, 0], [new(shift, [Day, 0, 0])]),
            },
        }, [])
        { ServedRooms = rooms.Length != 0 ? [.. rooms] : [new(1, "", servedAt)] };

    private static ForecastValueResult Ledger(ForecastCoupledContext context, ForecastCoupledOutcome outcome) => ForecastValue.Of(outcome, context, Day);

    private static ForecastInvestmentContribution[] Credits(ForecastValueResult ledger, string role) =>
        [.. ledger.InvestmentContributions.Where(r => r.Key.StartsWith(role + "/", StringComparison.Ordinal))];

    [Fact]
    public void TwoCorpsesByTheKitchenDoorCreditTheMorgueTheThoughtStageTimesTheExposure()
    {
        var context = Context(Pawn(), PlannedRole.Morgue, ByTheKitchenDoor(), ByTheKitchenDoor());
        // Each daily sighting holds both memories for half a day: two stacked at -4 x (1 + 0.5) half the time.
        var mood = -4 * (1 + 0.5) * 0.5;
        Assert.Equal(mood, ForecastCorpses.CorpseMood(context.Needs.Pawns[0], context.Needs.Corpses), 12);
        Assert.Equal(new[] { mood / 100 }, PlannedRoomCredits.BreakShifts(context)["pawn"]);
        var row = Assert.Single(Credits(Ledger(context, Outcome(mood / 100)), "morgue"));
        // The room removes the corpses' wellbeing and the break day their mood would cost, from day 2 to the horizon.
        var perDay = context.Value.WellbeingWeight * -mood / 100 + context.MoodBreaks.DaysLost([Day, 0, 0])!.Value;
        Assert.Equal(("pawn", "morgue/1/pawn", Horizon - 2), (row.Pawn, row.Key, row.Units));
        Assert.Equal(-perDay * (Horizon - 2), row.Value, 12);
    }

    [Fact]
    public void NoCorpsesCreditNothing()
    {
        var ledger = Ledger(Context(Pawn()), Outcome(0));
        Assert.Empty(Credits(ledger, "morgue"));
        Assert.DoesNotContain(ledger.Findings, f => f.Contains("unassessed:", StringComparison.Ordinal));
        Assert.False(PlannedRoomCredits.BreakShifts(Context(Pawn())).ContainsKey("pawn"));
    }

    [Fact]
    public void ASightingRenewsTheMemoryAndTheStackLimitCapsTheGroup()
    {
        // Seen every two hours for eight hours of the day: the memory holds from the first sighting to half a day after the last.
        var pawn = Pawn(record: Ticks);
        int[] passes = [.. Enumerable.Range(0, 5).Select(h => h * 2 * GameTime.TicksPerHour + 1)];
        CorpseSightingRead Seen() => new(true, false, [new("pawn", EquatableArray.From(passes))]);
        var active = (8 * GameTime.TicksPerHour + Day / 2) / Day;
        Assert.Equal(-6 * active, ForecastCorpses.CorpseMood(pawn, [Seen()]), 12);
        // Six rotting corpses seen together stack to the thought's limit of five.
        var group = Enumerable.Range(0, 5).Sum(i => Math.Pow(0.5, i));
        Assert.Equal(-6 * group * active, ForecastCorpses.CorpseMood(pawn, [.. Enumerable.Range(0, 6).Select(i => Seen())]), 12);
        // A colonist whose ideo gives no corpse memory loses nothing.
        Assert.Equal(0, ForecastCorpses.CorpseMood(pawn with { CorpseThoughts = [] }, [Seen()]));
    }

    [Fact]
    public void AColonistWithLessThanADayOfRecordIsCreditedFromWhatItHolds()
    {
        // #3835: half a day of record is its own cycle; the pass a quarter day ago holds the fresh memory the whole cycle.
        var young = Context(Pawn(record: Ticks / 2), PlannedRole.Morgue, ByTheKitchenDoor());
        Assert.Equal(-4, ForecastCorpses.CorpseMood(young.Needs.Pawns[0], young.Needs.Corpses), 12);
        var ledger = Ledger(young, Outcome(-0.04));
        Assert.Equal(-(young.Value.WellbeingWeight * 4 / 100 + young.MoodBreaks.DaysLost([Day, 0, 0])!.Value) * (Horizon - 2),
            Assert.Single(Credits(ledger, "morgue")).Value, 12);
        Assert.DoesNotContain(ledger.Findings, f => f.Contains("unassessed:", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEmptySightingRecordCreditsNothing()
    {
        // #3835: a colonist with no pass yet, or a save from before the sighting log was saved, covers nothing.
        var empty = Context(Pawn(record: 0), PlannedRole.Morgue, ByTheKitchenDoor());
        Assert.Equal(0, ForecastCorpses.CorpseMood(empty.Needs.Pawns[0], empty.Needs.Corpses));
        Assert.Empty(Credits(Ledger(empty, Outcome(-0.02)), "morgue"));
    }

    [Fact]
    public void ATombTakesTheColonistCorpsesAndTheMorgueTheStrangers()
    {
        var context = Context(Pawn(), PlannedRole.Morgue, ByTheKitchenDoor(colonist: true), ByTheKitchenDoor()) with
        {
            PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.Morgue, [2] = PlannedRole.Tomb },
        };
        // One fresh corpse each: -4 half the time.
        var ledger = Ledger(context, Outcome(-0.02, rooms: [new(1, "", 2 * Day), new(2, "", 2 * Day)]));
        var morgue = Assert.Single(Credits(ledger, "morgue"));
        var tomb = Assert.Single(Credits(ledger, "tomb"));
        Assert.Equal(morgue.Value, tomb.Value, 12);
        Assert.Equal(-(context.Value.WellbeingWeight * 2 / 100 + context.MoodBreaks.DaysLost([Day, 0, 0])!.Value) * (Horizon - 2), tomb.Value, 12);
        // With no tomb planned, the morgue takes the colonist corpse too.
        var alone = Context(Pawn(), PlannedRole.Morgue, ByTheKitchenDoor(colonist: true), ByTheKitchenDoor());
        Assert.Equal(-4 * 1.5 * 0.5, ForecastCorpses.CorpseMood(alone.Needs.Pawns[0], alone.Needs.Corpses), 12);
        Assert.Equal(new[] { -0.03 }, PlannedRoomCredits.BreakShifts(alone)["pawn"]);
    }

    [Fact]
    public void AMorgueAndATombArePriced()
    {
        Assert.True(Context(Pawn()).Priced(1));
        Assert.True(Context(Pawn(), PlannedRole.Tomb).Priced(1));
    }

    [Fact]
    public void TheCoupledContextCarriesTheCorpseFactsTheRoundsRead()
    {
        // The production path (#3822): GenerateCoupledActivities builds the context from the review's facts; the corpses and
        // each colonist's corpse memories and sighting record ride on the needs read (NeedsForecastRead), which RoundsRead fills.
        var facts = ForecastCoupledTests.ConstructionReview() with
        {
            PlannedShelters = [new() { Id = 1, Role = PlannedRole.Morgue, Region = RegionRules.RectRegion(new(1, 1, 1, 1)) }],
        };
        var context = ForecastCoupledCandidates.GenerateCoupledActivities(facts)!.Context;
        Assert.Same(facts.ForecastObserved.Needs, context.Needs);
        Assert.Equal(PlannedRole.Morgue, context.PlannedRoomRoles[1]);
        // A fresh colony's review (no corpse in sight, no sighting record) credits the morgue nothing and throws nothing.
        Assert.Empty(ForecastCorpses.MorgueCredit(new(1, "", Day), Outcome(0), [], context, Day)!);
        Assert.Empty(ForecastCorpses.BreakShifts(context));
    }
}
