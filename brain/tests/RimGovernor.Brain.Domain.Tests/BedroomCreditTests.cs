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
    private static NeedsForecastPawn Pawn(bool ascetic, int? barracksStage) => ForecastValueTests.Pawn() with
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
                new("1", "WoodLog", "Bedroom", Known(30d), Known(bedroomStage), 4, 0, false))],
            BedroomOccupants = new Dictionary<int, EquatableArray<string>> { [1] = ["pawn"] },
            InvestmentHorizonDays = Known(Horizon),
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
        { ClosedRooms = [new(1, "WoodLog", closedAt)] };

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
    /// #3744 acceptance (d): a started bedroom ring with <paramref name="ringDays"/> of ring work left and one builder is
    /// projected to close then, and its credit runs from that day to tier 2's horizon: none at or past it.
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
            RingWork = new Dictionary<string, double> { ["planned/room1/wall/WoodLog"] = ringDays * Day },
            StartedRooms = new HashSet<int> { 1 },
            PlannedBuilders = 1,
        };
        var closed = Assert.Single(ForecastRoofConsequences.PlannedClosedRooms(context, new Dictionary<string, double>(), Day));
        Assert.Equal(ringDays * Day, closed.ClosedAt);
        var rows = ForecastValue.Of(Closing(closed.ClosedAt, 0.06), context, Day).InvestmentContributions
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
        Assert.Contains("bedroom_credit_unassessed:pawn", ledger.Findings);
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
