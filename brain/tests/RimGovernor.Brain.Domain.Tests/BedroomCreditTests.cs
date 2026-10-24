using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

// #3655: a schedule that closes a planned bedroom is credited, in the Investment term, the mood its occupant gains a day
// from then to tier 2's horizon: the wellbeing of the bed-thought delta plus the break days the shifted mood saves.
public sealed class BedroomCreditTests
{
    private const double Day = GameTime.TicksPerDay;
    private const double Horizon = 10;
    private static readonly Cell Inside = new(1, 1);
    private static readonly NeedCell IndoorCell = new(Known(true), Known(false), Known(false), Known(false), Known(false));

    private static NeedThoughtStage Stage(string thought, string role, int stage, double offset)
        => new(thought, role, stage, offset, Known(Day), false);

    private static readonly NeedThoughtStage[] BedStages =
    [
        Stage("SleptOutside", "slept_outside", 0, -4),
        Stage("SleptInBedroom", "slept_in_bedroom", 0, -4), Stage("SleptInBedroom", "slept_in_bedroom", 2, 2),
        Stage("SleptInBarracks", "slept_in_barracks", 0, -7), Stage("SleptInBarracks", "slept_in_barracks", 2, -4),
        Stage("Ascetic", "ascetic", 0, 5), Stage("Ascetic", "ascetic", 2, 0), new("Ascetic", "ascetic", 4, 0, default, false),
    ];

    /// <summary>The pawn owns a bed in a barracks at <paramref name="barracksStage"/>, or no bed when null.</summary>
    private static NeedsForecastPawn Pawn(bool ascetic, int? barracksStage) => SleepFixtures.Sleeping(ForecastValueTests.Pawn(),
        barracksStage is null ? SleepFixtures.Ground() : SleepFixtures.InBed("bunk")) with
    {
        Ascetic = ascetic, OwnedBed = barracksStage is null ? "" : "bunk",
        ThoughtStages = EquatableArray.From(BedStages),
        Targets = barracksStage is not { } stage ? [] :
            [new("bunk", "rest", "", 1, Known(0d), default, default, Known(.00005)) { Bed = new("Barracks", Known(stage), false), Cell = IndoorCell, Location = new(9, 9) }],
    };

    private static ForecastCoupledContext Context(NeedsForecastPawn pawn, int bedroomStage, params MoodThought[] observed)
        => ForecastValueTests.Context(pawn) with
        {
            PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.Bedroom },
            Closures = [new(1, "WoodLog", [Inside], ["planned/room1/wall/WoodLog"], [],
                PlannedRoomPredictionTests.Closed("1", "WoodLog", "Bedroom", 30d, bedroomStage, 4, 0, false))],
            BedroomOccupants = new Dictionary<int, EquatableArray<string>> { [1] = ["pawn"] },
            InvestmentHorizonDays = Horizon,
            PlannedBeds = SleepFixtures.Beds(1, 1), Thermal = SleepFixtures.Outdoors(),
            ObservedThoughts = new Dictionary<string, Fact<EquatableArray<MoodThought>>> { ["pawn"] = Known(EquatableArray.From(observed)) },
        };

    /// <summary>A day in the mildest break tier unshifted, and none once shifted by <paramref name="shift"/>.</summary>
    private static ForecastCoupledOutcome Closing(double closedAt, double shift) =>
        new(new([], 0, 0), Known(true), new Dictionary<string, ForecastNeedsResult>
        {
            ["pawn"] = new([], default, default, new(new("pawn", "", default, default), []), [])
            {
                Mood = new([], default, default, default, default, default, [])
                {
                    PressureTicks = Known(0d), BreakRisk = Known(0d), BreakTierTicks = [Day, 0, 0],
                    ShiftedBreakTierTicks = [new(shift, [0, 0, 0])],
                },
            },
        }, [])
        { ServedRooms = [new(1, "WoodLog", closedAt)] };

    private static ForecastInvestmentContribution Credit(ForecastCoupledContext context, ForecastCoupledOutcome outcome)
        => Assert.Single(ForecastValue.Of(outcome, context, Day).InvestmentContributions, r => r.Key.StartsWith("bedroom/", StringComparison.Ordinal));

    [Fact]
    public void ANonAsceticInAMediocreBarracksIsCreditedTheBedroomsWellbeingAndBreaksToTheHorizon()
    {
        // Barracks stage 2 (-4) to bedroom stage 2 (+2): a delta of 6, closing half a day in.
        var context = Context(Pawn(ascetic: false, barracksStage: 2), bedroomStage: 2);
        var row = Credit(context, Closing(Day / 2, 0.06));
        var breaks = context.MoodBreaks.DaysLost([Day, 0, 0])!.Value;
        var perDay = context.Value.WellbeingWeight * 6 / 100 + breaks;
        Assert.Equal(("pawn", "bedroom/1/pawn", Horizon - 0.5), (row.Pawn, row.Key, row.Units));
        Assert.Equal(-perDay * (Horizon - 0.5), row.Value, 12);
    }

    [Fact]
    public void AnAsceticInASharedBarracksMovingToAnAwfulBedroomIsCreditedItsAsceticStage()
    {
        // A shared barracks gives an ascetic no Ascetic thought (observed none); an awful room's stage gives +5.
        var context = Context(Pawn(ascetic: true, barracksStage: 2), bedroomStage: 0);
        Assert.Equal(Known(5d), ForecastHousing.BedroomMoodDelta(context.Needs.Pawns[0], Known(0), context.ObservedThoughts["pawn"]));
        var row = Credit(context, Closing(0, 0.05));
        Assert.Equal(-(context.Value.WellbeingWeight * 5 / 100 + context.MoodBreaks.DaysLost([Day, 0, 0])!.Value) * Horizon, row.Value, 12);
        // Owning a room alone, its Ascetic thought is lost: an awful room (+5) to another awful one gains nothing.
        Assert.Equal(Known(0d), ForecastHousing.BedroomMoodDelta(context.Needs.Pawns[0], Known(0), Known(EquatableArray.From([new MoodThought("Ascetic", 5)]))));
    }

    /// <summary>
    /// #3744 acceptance (d): an open bedroom ring with <paramref name="ringDays"/> of ring work left and one builder is
    /// projected to serve then (#3796: its owed work done), and its credit runs from that day to tier 2's horizon: none at or past it.
    /// </summary>
    [Theory]
    [InlineData(3, 7)]
    [InlineData(10, 0)]
    [InlineData(12, 0)]
    public void TheCreditRunsFromTheProjectedClosureAndIsZeroAtOrPastTheHorizon(double ringDays, double units)
    {
        var context = Context(Pawn(ascetic: false, barracksStage: 2), bedroomStage: 2) with
        {
            PlannedSites = [new("planned/room1/wall/WoodLog", 1, "WoodLog", [], ["planned/room1/wall/WoodLog/finish"])],
            OwedWork = new Dictionary<string, double> { ["planned/room1/wall/WoodLog"] = ringDays * Day },
            OpenRooms = new HashSet<int> { 1 },
            PlannedBuilders = 1,
        };
        var closed = Assert.Single(PlannedRoomCredits.ServiceDays(context, new Dictionary<string, double>(), Day).Served);
        Assert.Equal(ringDays * Day, closed.ServedAt);
        var rows = ForecastValue.Of(Closing(closed.ServedAt, 0.06), context, Day).InvestmentContributions
            .Where(r => r.Key.StartsWith("bedroom/", StringComparison.Ordinal)).ToArray();
        if (units == 0)
        {
            Assert.Empty(rows);
            return;
        }
        Assert.Equal(units, Assert.Single(rows).Units, 9);
    }

    [Fact]
    public void AnOccupantWithUnreadStagesIsNamedAndNotCredited()
    {
        var context = Context(Pawn(ascetic: false, barracksStage: 1), bedroomStage: 2);
        var ledger = ForecastValue.Of(Closing(0, 0.06), context, Day);
        Assert.DoesNotContain(ledger.InvestmentContributions, r => r.Key.StartsWith("bedroom/", StringComparison.Ordinal));
        Assert.Contains("bedroom_credit_unassessed:1", ledger.Findings);
    }

    // ----- #3840: the layout/first-bedroom-closes case, every bed term ApplyBedThoughts, the rest tick and the comfort need apply.

    /// <summary>Need_Rest.BaseRestGainPerTick and the Rested category's RestFallPerTick (Need_Rest, ilspycmd).</summary>
    private const double RestGain = 3.809524E-05, RestFall = 1.5833333E-05;

    /// <summary>
    /// A colonist of the case sleeping on its own sleeping spot outdoors at 21 °C: the spot is costless (SleptOnGround), its
    /// room psychologically outdoors (SleptOutside), Comfort 0.4 (SleepingSpotBase) and without BedRestEffectiveness (the
    /// stat's valueIfMissing 0.8). The rows are the vanilla defs' (Thoughts_Memory_Misc, Thoughts_Memory_RoomStats,
    /// Thoughts_Situation_Needs), the sleep range the human's ComfyTemperatureMin/Max (16, 26).
    /// </summary>
    private static NeedsForecastPawn CaseColonist(string id) => ForecastValueTests.Pawn() with
    {
        Pawn = id, OwnedBed = "spot-" + id, SleepComfortableMin = 16, SleepComfortableMax = 26,
        Needs = [new("rest", Known(1d), Known(RestFall), Known(.28)) { UnservedGainPerTick = Known(RestGain * .8), GainPerTickPerEffectiveness = RestGain }],
        Sleep = new("spot-" + id, true, true, RestGain * .8, .4, 21),
        Targets = [new("spot-" + id, "rest", "", 1, Known(0d), default, default, Known(RestGain * .8)) { Bed = new("", Known(0), false), Location = new(9, 9) }],
        ThoughtStages =
        [
            Stage("SleptOutside", "slept_outside", 0, -4), Stage("SleptOnGround", "slept_on_ground", 0, -4),
            Stage("SleptInCold", "slept_in_cold", 0, -4), Stage("SleptInHeat", "slept_in_heat", 0, -4),
            Stage("SleptInBarracks", "slept_in_barracks", 0, -7), Stage("SleptInBarracks", "slept_in_barracks", 1, -5),
            Stage("SleptInBedroom", "slept_in_bedroom", 0, -4),
            Stage("NeedComfort", ForecastHousing.NeedComfortRole, 0, -3), Stage("NeedComfort", ForecastHousing.NeedComfortRole, 1, 4),
            Stage("NeedComfort", ForecastHousing.NeedComfortRole, 2, 6), Stage("NeedComfort", ForecastHousing.NeedComfortRole, 3, 8),
            Stage("NeedComfort", ForecastHousing.NeedComfortRole, 4, 10),
        ],
    };

    /// <summary>
    /// #3840 acceptance: the case (temperate 21 °C, three colonists on outdoor sleeping spots, a 5x5 WoodLog barracks with three
    /// wood beds, Comfort 0.75 and rest effectiveness 1) credited with every term, at the awful (stage 0, −7) and dull (stage 1,
    /// −5) barracks the vanilla def gives. Per colonist the move is worth slept_in_barracks + 4 (slept_outside lost) + 4
    /// (slept_on_ground lost) + the comfort need over the day in the bed less on the spot, no temperature memory at either
    /// (21 °C), plus the capability-days the faster rest frees. With every term the barracks credit is positive: it wins
    /// against the spots it replaces, by the values asserted below; against its charged wood it wins while a WoodLog is worth
    /// less than the break-even this test states, which only the live colony model prices.
    /// </summary>
    [Theory]
    [InlineData(0, -7, 1.5869)]
    [InlineData(1, -5, 1.6469)]
    public void TheCaseBarracksIsCreditedEveryBedTermAndBeatsTheSpots(int stage, double barracks, double expected)
    {
        string[] ids = ["c1", "c2", "c3"];
        var pawns = ids.Select(CaseColonist).ToArray();
        var curve = new NeedCurve(.5, .6, .04, false);
        var context = ForecastValueTests.Context(pawns[0]) with
        {
            Needs = new() { Pawns = EquatableArray.From(pawns) },
            PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.Barracks },
            Closures = [new(1, "WoodLog", [Inside], ["planned/room1/wall/WoodLog"], [], PlannedRoomPredictionTests.Closed("1", "WoodLog", "Barracks", 30d, stage, 25, 0, false))],
            BedroomOccupants = new Dictionary<int, EquatableArray<string>> { [1] = EquatableArray.From(ids) },
            PlannedBeds = new Dictionary<int, IReadOnlyList<SleepingBedFacts?>> { [1] = [.. ids.Select(_ => new SleepingBedFacts("Bed", "WoodLog", false, 1, .75))] },
            ComfortCurve = curve, Thermal = SleepFixtures.Outdoors(), InvestmentHorizonDays = Horizon,
            ObservedThoughts = ids.ToDictionary(id => id, _ => Known(EquatableArray.From(Array.Empty<MoodThought>())), StringComparer.Ordinal),
        };

        // Each term on its own, from the game's numbers.
        var comfort = ForecastHousing.ComfortMood(pawns[0], .75, curve) - ForecastHousing.ComfortMood(pawns[0], .4, curve);
        var rest = RestFall / (RestFall + RestGain * .8) - RestFall / (RestFall + RestGain);
        Assert.Equal(0d, ForecastHousing.SleepTemperatureThought(pawns[0], 21));
        Assert.Equal(3.5925, comfort, 4);
        Assert.Equal(.0483, rest, 4);
        var delta = barracks + 4 + 4 + comfort;

        // Calm colonists (no break days either way): the credit a colonist-day is the wellbeing of the delta plus the rest.
        var shifts = ForecastHousing.BreakShifts(context);
        var outcome = new ForecastCoupledOutcome(new([], 0, 0), Known(true), ids.ToDictionary(id => id, id => new ForecastNeedsResult([], default, default, new(new(id, "", default, default), []), [])
        {
            Mood = new([], default, default, default, default, default, [])
            {
                PressureTicks = Known(0d), BreakRisk = Known(0d), BreakTierTicks = [0, 0, 0],
                ShiftedBreakTierTicks = [.. shifts[id].Select(s => new ForecastShiftedBreaks(s, [0, 0, 0]))],
            },
        }, StringComparer.Ordinal), []) { ServedRooms = [new(1, "WoodLog", 0)] };
        var rows = ForecastValue.Of(outcome, context, Day).InvestmentContributions
            .Where(r => ids.Any(id => r.Key == PlannedRoomCredits.Key(PlannedRole.Barracks, 1, id))).ToArray();
        Assert.Equal(3, rows.Length);
        var perDay = context.Value.WellbeingWeight * delta / 100 + rest;
        Assert.All(rows, r => Assert.Equal(-perDay * Horizon, r.Value, 9));

        // It wins against the spots: per colonist-day the awful barracks gains +4.59 mood (0.0046 capability-days at the
        // default WellbeingWeight 0.1) and the dull one +6.59 (0.0066), the faster rest 0.0483 more: the rest dominates.
        Assert.True(delta > 0 && perDay > 0);
        var credit = -rows.Sum(r => r.Value);
        Assert.Equal(expected, credit, 4);
        // Against its wood: 23 walls x 5 + a door's 25 + three beds x 45 = 275 WoodLog (ThingDefs Wall, Door, Bed
        // costStuffCount). It wins while a WoodLog is worth less than 0.0058 (awful) or 0.0060 (dull) capability-days over
        // the 10 days: the live colony model's marginal wood value decides that, not this test.
        Assert.Equal(stage == 0 ? .00577 : .00599, credit / 275, 5);
    }

    [Fact]
    public void ABreakShiftCountsTheTiersAMoodChangeOfThatSizeWould()
    {
        var pawn = ForecastConditionalMoodTests.PawnProbe();
        var sleep = ForecastConditionalMoodTests.SleepProbe("sleep", 17 * GameTime.TicksPerHour, Day);
        var shifted = ForecastMood.ForecastMoodTimeline(pawn, Day, [sleep], breakShifts: [0.1]);
        var moved = ForecastMood.ForecastMoodTimeline(pawn, Day, [sleep], situational: [("Moved", 10, 0)]);
        var row = Assert.Single(shifted.ShiftedBreakTierTicks);
        Assert.Equal(0.1, row.Shift);
        Assert.Equal(moved.BreakTierTicks.ToArray(), row.TierTicks.ToArray());
        Assert.NotEqual(shifted.BreakTierTicks.ToArray(), row.TierTicks.ToArray());
    }
}
