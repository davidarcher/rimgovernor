using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ForecastConditionalMoodTests
{
    private const double Hour = GameTime.TicksPerHour;
    private const double Day = GameTime.TicksPerDay;
    private static NeedsForecastPawn Pawn(bool conditional = true) => new()
    {
        Pawn = "pawn", State = "eligible", LocalHour = Known(6d), Awake = Known(true),
        SleepThreshold = Known(.75), WakeThreshold = Known(1d), SleepBlockedUntilTicks = Known(0d),
        Mood = Known(.5), MoodTarget = Known(.5), MoodUnclampedTarget = Known(.5), BreakThreshold = Known(.45),
        MoodRisePerTick = Known(.12 / Hour), MoodFallPerTick = Known(.08 / Hour),
        MoodIntervalTicks = Known(150d), MoodFreezesWhileAsleep = Known(true),
        ConditionalThoughts = Known<EquatableArray<NeedConditionalThought>>(conditional
            ? [new("day-effect", 11, 18, true, -10, 0, "observed-worker"), new("night-effect", 23, 6, true, 16, 0, "observed-worker")]
            : []),
        Memories = Known<EquatableArray<NeedMemory>>([]),
        Needs = [new("rest", Known(.3), Known(0d), Known(.28), Known(1d))],
        Targets = [new("bed", "rest", "", 1, Known(0d), default, default, Known(.7 / (7 * Hour)))],
    };
    private static ForecastNeedChoice Sleep(string id, double from, double to)
        => new(id, "pawn", "rest", "bed", "", 0, from, from, to, (to - from) * .7 / (7 * Hour), []);
    private static ForecastCapacityInput Capacity() => new()
    {
        Pawn = new() { Id = new("pawn"), Available = Known(true), Applies = Known(true), Job = Known(new PawnJob()),
            Schedule = Known(EquatableArray.From(Enumerable.Repeat(PolicyConstants.ScheduleSleep, 24))) },
        Location = "bed", HorizonTicks = Day, LocalHour = Known(6d), OpportunityCostPerTick = Known(1d),
        CurrentJobRemainingTicks = Known(0d), Occupancy = Known<EquatableArray<ForecastOccupancy>>([]),
        LocationAfterProtectedTime = Known("bed"), ProtectTimetable = false,
    };
    private static ForecastMoodPoint At(ForecastMoodResult result, double tick)
        => Assert.Single(result.Trajectory, p => Math.Abs(p.Tick - tick) < .000001);

    [Fact]
    public void GeneratedDayAndNightSleepCompareConditionalConsequencesAndControlRemovesThatPreference()
    {
        var pawn = Pawn();
        var generated = GenerateNeedsAlternatives(pawn, ForecastPawnCapacityOf(Capacity()), Day);
        var day = Assert.Single(generated.Choices, c => Math.Abs(c.Start - 5 * Hour) < .001 && Math.Abs(c.End - 12 * Hour) < .001);
        var night = Assert.Single(generated.Choices, c => Math.Abs(c.Start - 17 * Hour) < .001 && Math.Abs(c.End - Day) < .001);
        var dayResult = ForecastMoodTimeline(pawn, Day, [day]);
        var nightResult = ForecastMoodTimeline(pawn, Day, [night]);
        Assert.True(dayResult.PressureTicks.Value < nightResult.PressureTicks.Value);
        Assert.Equal(.5, At(dayResult, 5 * Hour).Target.Value, 8);
        Assert.Equal(.4, At(nightResult, 5 * Hour).Target.Value, 8);
        Assert.Equal(.66, At(dayResult, 17 * Hour).Target.Value, 8);
        Assert.Equal(.5, At(nightResult, 17 * Hour).Target.Value, 8);
        var controlDay = ForecastMoodTimeline(Pawn(false), Day, [day]);
        var controlNight = ForecastMoodTimeline(Pawn(false), Day, [night]);
        Assert.Equal(controlDay.PressureTicks.Value, controlNight.PressureTicks.Value, 8);
        Assert.Equal(controlDay.TerminalMood.Value, controlNight.TerminalMood.Value, 8);
    }

    [Theory]
    [InlineData(.5, 0)]
    [InlineData(.4, 1 / 4.25)]
    [InlineData(.2, 1 / 1.05)]
    public void BreakRiskIntegratesEachTiersRenewalRateOverTheTargetTrajectory(double target, double perDay)
    {
        var pawn = Pawn(false) with { MoodUnclampedTarget = Known(target), MoodTarget = Known(target) };
        var result = ForecastMoodTimeline(pawn, Day, []);
        Assert.Equal(perDay, result.BreakRisk.Value, 8);
        Assert.DoesNotContain("mental_break_probability_unassessed", result.Findings);
        // A fading -10 memory lifts the target across the minor threshold halfway: in that tier for half the horizon.
        var fading = ForecastMoodTimeline(Pawn(false) with { MoodUnclampedTarget = Known(.4), MoodTarget = Known(.4),
            Memories = Known<EquatableArray<NeedMemory>>([new("sting", -10, Known(Day), true)]) }, Day, []);
        Assert.Equal(.5 / 4.25, fading.BreakRisk.Value, 4);
        var unknown = ForecastMoodTimeline(pawn with { BreakThreshold = default }, Day, []);
        Assert.False(unknown.BreakRisk.IsKnown);
        Assert.Contains("mental_break_probability_unassessed", unknown.Findings);
    }

    [Fact]
    public void ActualMoodRespondsGraduallyAndSleepFreezesActualRatherThanOnlyRemovingThought()
    {
        var pawn = Pawn(false) with { Mood = Known(.2), MoodUnclampedTarget = Known(.8), MoodTarget = Known(.8) };
        var awake = ForecastMoodTimeline(pawn, Hour, []);
        var asleep = ForecastMoodTimeline(pawn, Hour, [Sleep("sleep", 0, Hour)]);
        Assert.Equal(.8, awake.Trajectory[0].Target.Value, 8);
        Assert.Equal(.2, awake.Trajectory[0].Actual.Value, 8);
        Assert.Equal(.32, awake.TerminalMood.Value, 8);
        Assert.Equal(.2, asleep.TerminalMood.Value, 8);
        Assert.True(awake.ActualExposureTicks.Value > 0);
        Assert.Equal(0, awake.TargetExposureTicks.Value);
        Assert.Contains("mood_response_interval_phase_unassessed", awake.Findings);
    }

    [Fact]
    public void WrappedWindowReappearsAfterMorningBoundaryWithoutDuplicateCurrentCredit()
    {
        var pawn = Pawn() with { LocalHour = Known(1d), MoodTarget = Known(.66), MoodUnclampedTarget = Known(.66),
            ConditionalThoughts = Known<EquatableArray<NeedConditionalThought>>(
                [new("night-effect", 23, 6, true, 16, 16, "observed-worker")]) };
        var result = ForecastMoodTimeline(pawn, Day, []);
        Assert.Equal(.66, result.Trajectory[0].Target.Value, 8);
        Assert.Equal(.5, At(result, 5 * Hour).Target.Value, 8);
        Assert.Equal(.66, At(result, 22 * Hour).Target.Value, 8);
        Assert.Equal(.66, result.Trajectory[^1].Target.Value, 8);
    }

    [Fact]
    public void InterruptedSleepEndsSuppressionAndAllowsActualMoodToResumeChanging()
    {
        var sleep = Sleep("sleep", 5 * Hour, 12 * Hour);
        var full = ForecastMoodTimeline(Pawn(), Day, [sleep]);
        var interrupted = ForecastMoodTimeline(Pawn(), Day, [sleep], new Dictionary<string, double> { [sleep.Id] = 8 * Hour });
        Assert.True(At(full, 9 * Hour).Asleep);
        Assert.False(At(interrupted, 8 * Hour).Asleep);
        Assert.Equal(.4, At(interrupted, 8 * Hour).Target.Value, 8);
        Assert.True(interrupted.PressureTicks.Value > full.PressureTicks.Value);
        Assert.True(At(interrupted, 9 * Hour).Actual.Value < At(full, 9 * Hour).Actual.Value);
    }

    [Fact]
    public void MemoryExpiryChangesTargetOnceWhileUnknownExpiryKeepsMoodUnassessed()
    {
        var pawn = Pawn(false) with { MoodUnclampedTarget = Known(.6), MoodTarget = Known(.6),
            Memories = Known<EquatableArray<NeedMemory>>([new("memory", 10, Known(1000d), false)]) };
        var result = ForecastMoodTimeline(pawn, Hour, []);
        Assert.Equal(.6, result.Trajectory[0].Target.Value, 8);
        Assert.Equal(.5, At(result, 1000).Target.Value, 8);
        var unknown = ForecastMoodTimeline(pawn with { Memories = Known<EquatableArray<NeedMemory>>([new("memory", 10, default, false)]) }, Hour, []);
        Assert.False(unknown.PressureTicks.IsKnown);
        Assert.False(unknown.TerminalMood.IsKnown);
        Assert.Contains("unknown_conditional_mood_mechanics", unknown.Findings);
    }

    [Fact]
    public void UnclampedObservedTargetPreservesOverflowWhenCurrentEffectEnds()
    {
        var pawn = Pawn(false) with { LocalHour = Known(1d), MoodTarget = Known(1d), MoodUnclampedTarget = Known(1.1),
            ConditionalThoughts = Known<EquatableArray<NeedConditionalThought>>([new("night-effect", 23, 6, true, 16, 16, "observed-worker")]) };
        var result = ForecastMoodTimeline(pawn, Day, []);
        Assert.Equal(1, result.Trajectory[0].Target.Value);
        Assert.Equal(.94, At(result, 5 * Hour).Target.Value, 8);
    }

    [Fact]
    public void UnknownResponseDoesNotEraseKnownTargetAndUnknownConditionalsCannotCreateBenefit()
    {
        var result = ForecastMoodTimeline(Pawn(false) with { MoodRisePerTick = default }, Hour, []);
        Assert.True(result.PressureTicks.IsKnown);
        Assert.False(result.TerminalMood.IsKnown);
        var unknown = ForecastMoodTimeline(Pawn() with { ConditionalThoughts = default }, Hour, []);
        Assert.False(unknown.PressureTicks.IsKnown);
        Assert.False(unknown.TerminalMood.IsKnown);
    }

    [Fact]
    public void GenerationProtectsActualObligationsAndReportsBoundsDeterministically()
    {
        var occupied = Capacity() with { Occupancy = Known<EquatableArray<ForecastOccupancy>>(
            [new("urgent", "pawn", Known(5 * Hour), Known(12 * Hour), ForecastOccupancyState.Active)]) };
        var generated = GenerateNeedsAlternatives(Pawn(), ForecastPawnCapacityOf(occupied), Day);
        Assert.DoesNotContain(generated.Choices, c => c.Depart < 12 * Hour && c.End > 5 * Hour);
        Assert.Contains(generated.Choices, c => Math.Abs(c.Start - 17 * Hour) < .001 && Math.Abs(c.End - Day) < .001);
        var bounded = GenerateNeedsAlternatives(Pawn(), ForecastPawnCapacityOf(Capacity()), Day, budget: 2);
        Assert.True(bounded.Truncated);
        Assert.Contains("needs_generation_truncated", bounded.Findings);
        Assert.Equal(bounded, GenerateNeedsAlternatives(Pawn(), ForecastPawnCapacityOf(Capacity()), Day, budget: 2));
    }
    [Fact]
    public void NativeWakeEndsRecoveryAndThoughtSuppressionAtSamePhysicalInstant()
    {
        var pawn = Pawn() with { LocalHour = Known(11d), MoodUnclampedTarget = Known(.4),
            ConditionalThoughts = Known<EquatableArray<NeedConditionalThought>>([new("day", 11, 18, true, -10, -10, "observed")]),
            WakeThreshold = Known(.8), Needs = [new("rest", Known(.3), Known(0d), Known(.28), Known(1d))] };
        var sleep = new ForecastNeedChoice("long", "pawn", "rest", "bed", "", 0, 0, 0, 1000, 1, []);
        var result = ForecastNeedsTimeline(pawn, Capacity() with { HorizonTicks = 1000 }, [sleep]);
        Assert.Equal(.8, result.Needs.Single(n => n.Kind == "rest").FinalLevel.Value, 8);
        Assert.False(At(result.Mood!, 500).Asleep);
        Assert.Equal(.4, At(result.Mood!, 500).Target.Value, 8);
        Assert.Contains("sleep_service_capped_at_native_wake:long", result.Findings);
        Assert.DoesNotContain(result.Capacity.Capacity.Windows.Value, w => w.Start < 1000);
    }

    [Fact]
    public void DelayedGenerationRecalculatesWakeTimeInsteadOfSlowingTheObservedRecoveryRate()
    {
        var pawn = Pawn() with { WakeThreshold = Known(.8), Needs = [new("rest", Known(.3), Known(.00001), Known(.28), Known(1d))] };
        var generated = GenerateNeedsAlternatives(pawn, ForecastPawnCapacityOf(Capacity()), Day);
        Assert.All(generated.Choices, choice =>
        {
            var before = Math.Max(0, .3 - .00001 * choice.Start);
            Assert.True(before + choice.Gain <= .8 + .00000001);
            Assert.Equal(pawn.Targets[0].GainPerTick.Value, choice.Gain / (choice.End - choice.Start), 10);
        });
        Assert.Contains(generated.Choices, c => c.Start > 0 && Math.Abs(Math.Max(0, .3 - .00001 * c.Start) + c.Gain - .8) < .000001);
    }

    [Fact]
    public void UnsupportedSleepActivationCannotCreateKnownRestOrMoodBenefits()
    {
        var pawn = Pawn() with { SleepBlockedUntilTicks = default };
        Assert.Empty(GenerateNeedsAlternatives(pawn, ForecastPawnCapacityOf(Capacity()), Day).Choices);
        var result = ForecastNeedsTimeline(pawn, Capacity(), [Sleep("unknown", 0, Hour)]);
        Assert.False(result.Needs.Single(n => n.Kind == "rest").FinalLevel.IsKnown);
        Assert.False(result.Mood!.PressureTicks.IsKnown);
        Assert.False(ForecastMoodTimeline(Pawn() with { Awake = Known(false) }, Day, []).PressureTicks.IsKnown);
        var continuation = ForecastMoodTimeline(Pawn() with { Awake = Known(false) }, Day, [Sleep("continue", 0, Hour)]);
        Assert.True(continuation.PressureTicks.IsKnown);
    }

    [Fact]
    public void StarvingSleepRequiresCompletedMealAndInterruptedEatingCannotUnlockIt()
    {
        var pawn = Pawn() with { SleepWhileStarving = Known(false),
            Needs = [new("rest", Known(.3), Known(0d), Known(.28), Known(1d)),
                new("food", Known(0d), Known(0d), Known(.2), Known(1d))] };
        var sleep = Sleep("sleep", 200, 1000);
        var meal = new ForecastNeedChoice("meal", "pawn", "food", "food", "Rice", 1, 0, 0, 100, .5, []);
        Assert.False(ForecastNeedsTimeline(pawn, Capacity(), [sleep]).Needs.Single(n => n.Kind == "rest").FinalLevel.IsKnown);
        Assert.True(ForecastNeedsTimeline(pawn, Capacity(), [meal, sleep]).Needs.Single(n => n.Kind == "rest").FinalLevel.IsKnown);
        Assert.False(ForecastNeedsTimeline(pawn, Capacity(), [meal, sleep], new Dictionary<string, double> { ["meal"] = 50 })
            .Needs.Single(n => n.Kind == "rest").FinalLevel.IsKnown);
        Assert.True(ForecastNeedsTimeline(pawn with { SleepWhileStarving = Known(true) }, Capacity(), [sleep])
            .Needs.Single(n => n.Kind == "rest").FinalLevel.IsKnown);
    }
    private static (ForecastCoupledCandidates Candidates, ForecastCoupledSelection Result) SelectObserved(NeedsForecastPawn pawn, double horizon = Day)
    {
        var facts = WorkResourceTestFacts.Create("pawn");
        pawn = pawn with { Targets = EquatableArray.From(pawn.Targets.Select(t => t with { Location = new(1, 1) })) };
        facts = facts with { ForecastObserved = facts.ForecastObserved with
        {
            Needs = new() { Tick = 100, Pawns = [pawn] },
            Coupled = new() { Tick = 100, Food = new() { Known = true }, Routes = [new("pawn", new(1, 1), new(1, 1), Known(0d)) { FromStart = true }] },
        } };
        facts = ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([]));
        var candidates = GenerateCoupledActivities(facts, horizon)!;
        facts = facts with { CoupledAlternatives = candidates };
        var request = CoupledForecastRequestOf(new("world/map/load", 100), horizon / GameTime.TicksPerDay,
            facts, new(), new() { Work = ForecastWorkInputsOf(facts) }, [], new());
        return (candidates, CompareActivityOutcomes(request, candidates));
    }

    [Fact]
    public void OrdinarySharedSelectorChoosesGeneratedDaySleepFromConditionalEffectsAndUrgencyOverridesIt()
    {
        var (generated, selected) = SelectObserved(Pawn());
        Assert.Contains(generated.Context.Activities.Activities, t => t.NotBefore == 5 * Hour);
        Assert.Contains(generated.Context.Activities.Activities, t => t.NotBefore == 17 * Hour);
        var sleep = Assert.Single(selected.Selected.Plan.Allocations);
        Assert.Equal(5 * Hour, sleep.Start, 6);
        Assert.Equal(12 * Hour, sleep.End, 6);
        Assert.True(selected.SelectedVector.BreakRisk.Value < selected.BaselineVector.BreakRisk.Value);
        var (_, control) = SelectObserved(Pawn(false));
        Assert.Empty(control.Selection.Selected);
        var (_, urgent) = SelectObserved(Pawn() with { Needs = [new("rest", Known(.27), Known(0d), Known(.28), Known(1d))] });
        Assert.Equal(0, Assert.Single(urgent.Selected.Plan.Allocations).Start);
        var (_, shortHorizon) = SelectObserved(Pawn(), 2 * Hour);
        Assert.Empty(shortHorizon.Selection.Selected);
    }
    [Fact]
    public void NativeThresholdAndDisturbanceDelayActivationAndTinyResponseIntervalIsExplicitlyBounded()
    {
        var pawn = Pawn() with { Needs = [new("rest", Known(.9), Known(.00001), Known(.28), Known(1d))], SleepBlockedUntilTicks = Known(20000d) };
        var generated = GenerateNeedsAlternatives(pawn, ForecastPawnCapacityOf(Capacity()), Day);
        Assert.NotEmpty(generated.Choices);
        Assert.All(generated.Choices, c => Assert.True(c.Start >= 20000 && Math.Max(0, .9 - .00001 * c.Start) < .75));
        var bounded = ForecastMoodTimeline(Pawn() with { MoodIntervalTicks = Known(.000001) }, Day, []);
        Assert.Contains("mood_event_budget_truncated", bounded.Findings);
        Assert.False(bounded.TerminalMood.IsKnown);
    }
}
