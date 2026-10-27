using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class NativeTimetableTests
{
    internal static NeedsForecastPawn Needs(string pawn = "a") => NeedsFixtures.Pawn(pawn) with
    {
        Needs = [NeedsFixtures.Rate("rest", .8, .0001, .3) with { Unsupported = ["rest_category_rate_changes"] },
            NeedsFixtures.Rate("joy", .8, .00001, .3) with { OutdoorShareByTwelfth = EquatableArray.From(Enumerable.Repeat(0d, 12)) }],
        Targets = [NeedsFixtures.Service("bed", "rest", 0, .0002), NeedsFixtures.Service("game", "joy", 0, .00011)],
    };

    private static EquatableArray<string> Slots(NeedsForecastPawn pawn) => Assert.IsType<NativeTimetable.Planned>(PawnRules.RoutineTimetable(pawn)).Slots;

    [Fact]
    public void RegularBlocksUseCurrentMechanicsAndDoNotPromiseLabor()
    {
        var needs = Needs();
        var slots = Slots(needs);
        Assert.Equal(24, slots.Count);
        Assert.Equal(8, slots.Count(s => s == PawnRules.ScheduleSleep));
        Assert.Equal(2, slots.Count(s => s == PawnRules.ScheduleJoy));
        Assert.Equal(2, slots.Count(s => s == PawnRules.ScheduleAnything));
        Assert.Equal(PawnRules.ScheduleSleep, slots[22]);
        Assert.Equal(PawnRules.ScheduleJoy, slots[21]);
        Assert.Equal(14, PawnRules.WorkHours(slots));
        // A schedule alone provides no needs/care/current-work evidence for production.
        Assert.IsType<ForecastTerm.Unassessed>(NativeLaborCapacity.HoursPerDay(PawnFixtures.Row("a"), null, [WorkType.Cooking]));
        Assert.Equal(slots, Slots(needs with { LocalHour = 4, Needs = [needs.Needs[0] with { Level = .1 }, needs.Needs[1]] }));
    }

    [Fact]
    public void BetterRestAndRecreationFacilitiesResizePreferencesWithoutAnExtraTraitTemplate()
    {
        var needs = Needs();
        var faster = needs with { Targets = [NeedsFixtures.Service("bed", "rest", 0, .0005), NeedsFixtures.Service("game", "joy", 0, .00023)] };
        var slots = Slots(faster);
        Assert.Equal(4, slots.Count(s => s == PawnRules.ScheduleSleep));
        Assert.Equal(1, slots.Count(s => s == PawnRules.ScheduleJoy));
        Assert.True(PawnRules.WorkHours(slots) > PawnRules.WorkHours(Slots(needs)));
        var distant = faster with { Targets = [faster.Targets[0] with { TravelTicks = 3000 }, faster.Targets[1]] };
        Assert.Equal(6, Slots(distant).Count(s => s == PawnRules.ScheduleSleep));
    }

    [Fact]
    public void KnownAwakeMoodWindowsShiftTheNightWithoutClaimingUnmodeledBenefits()
    {
        var needs = Needs() with { Mood = NeedsFixtures.Mood with
            { Conditional = new ConditionalMood.Modeled([new(11, 18, true, -.1, 0), new(23, 6, true, .1, 0)]) } };
        var slots = Slots(needs);
        for (var hour = 11; hour < 18; hour++) Assert.Equal(PawnRules.ScheduleSleep, slots[hour]);
        for (var hour = 23; hour < 30; hour++) Assert.NotEqual(PawnRules.ScheduleSleep, slots[hour % 24]);
        Assert.Equal(Slots(Needs()), Slots(needs with { Mood = needs.Mood! with { Conditional = new ConditionalMood.Unmodeled(["other"]) } }));
    }

    [Fact]
    public void MissingServiceDoesNotBecomeAnAnythingScheduleOrBlockIndependentPriorities()
    {
        var pawn = PawnFixtures.Worker("a");
        var missing = Needs() with { Targets = [] };
        Assert.IsType<NativeTimetable.Unassessed>(PawnRules.RoutineTimetable(missing));
        Assert.Empty(PawnRules.TimetableChanges([pawn], null));
        Assert.Empty(PawnRules.TimetableChanges([pawn], NeedsFixtures.Read(0, missing)));
        Assert.IsType<NativeTimetable.Unassessed>(PawnRules.RoutineTimetable(Needs() with { SleepBlocked = true }));
        Assert.Equal(24, Slots(Needs() with { Needs = [], Targets = [] }).Count(s => s == PawnRules.ScheduleWork || s == PawnRules.ScheduleAnything));
        var worker = StockArrivalTestFacts.Crafter("a", 10, 1);
        pawn = pawn with { Observed = worker, Work = PawnFixtures.WorkTable(true, [new()
            { Observed = new() { DefName = "Crafting", Priority = 3, Disabled = false }, Skill = "Crafting", Order = 1 }]) };
        var change = Assert.Single(WorkRules.NativeSettingsChanges([pawn], [new(new("a"), [WorkPriority.Planned(WorkType.Crafting, 1)])], NeedsFixtures.Read(0, missing)));
        Assert.False(change.HasSchedule);
        Assert.Equal(1, Assert.Single(change.Settings).Priority);
    }

    [Fact]
    public void TimetableOnlyDifferenceWakesTheNeedAndSettlesAfterExactReadback()
    {
        var pawn = PawnFixtures.Worker("a");
        var read = NeedsFixtures.Read(0, Needs());
        var assignment = new PawnWorkAssignment(new("a"), []);
        var change = Assert.Single(WorkRules.NativeSettingsChanges([pawn], [assignment], read));
        Assert.Empty(change.Settings);
        var facts = WorkResourceTestFacts.Create("a") with { WorkCoverage = WorkCoverageState.Matched, CleanupPawns = false };
        facts = facts with { ForecastObserved = facts.ForecastObserved with { WorkPawns = new([pawn]), Needs = read } };
        Assert.Equal(WorkAssignmentsReview.Owed, Rules.WorkAssignmentsOwed(facts, null));
        pawn = pawn with { Observed = pawn.Observed with { Settings = pawn.Observed.Settings with
            { Schedule = EquatableArray.From(change.Schedule.Select((s, h) => new TimetableSlot { Hour = (uint)h, AssignmentDefName = s })) } } };
        Assert.Empty(WorkRules.NativeSettingsChanges([pawn], [assignment], read));
        Assert.Equal(WorkAssignmentsReview.Settled, Rules.WorkAssignmentsOwed(facts with { ForecastObserved = facts.ForecastObserved with { WorkPawns = new([pawn]) } }, null));
        var checkbox = pawn with { Work = PawnFixtures.WorkTable(false, []) };
        Assert.Empty(PawnRules.TimetableChanges([checkbox], read));
        Assert.Equal(WorkDecisionState.CheckboxMode, WorkRules.PlanWork([checkbox], [], new()).State);
    }
}
