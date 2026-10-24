using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

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
            ? [new(11, 18, true, -10, 0), new(23, 6, true, 16, 0)]
            : []),
        Memories = Known<EquatableArray<NeedMemory>>([]),
        Needs = [new("rest", Known(.3), Known(0d), Known(.28))],
        Targets = [new("bed", "rest", "", 1, Known(0d), default, default, Known(.7 / (7 * Hour)))],
    };
    internal static ForecastNeedChoice SleepProbe(string id, double from, double to) => Sleep(id, from, to);
    internal static NeedsForecastPawn PawnProbe() => Pawn();
    private static ForecastNeedChoice Sleep(string id, double from, double to)
        => new(id, "pawn", "rest", "bed", "", 0, from, from, to, (to - from) * .7 / (7 * Hour), []);
    private static ForecastCapacityInput Capacity() => new()
    {
        Pawn = new() { Id = new("pawn"), Available = Known(true), Applies = Known(true), Job = Known(new PawnJob()),
            Schedule = Known(EquatableArray.From(Enumerable.Repeat(PawnRules.ScheduleSleep, 24))) },
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
        var generated = ForecastNeeds.GenerateNeedsAlternatives(pawn, ForecastCapacity.ForecastPawnCapacityOf(Capacity()), Day);
        var day = Assert.Single(generated.Choices, c => Math.Abs(c.Start - 5 * Hour) < .001 && Math.Abs(c.End - 12 * Hour) < .001);
        var night = Assert.Single(generated.Choices, c => Math.Abs(c.Start - 17 * Hour) < .001 && Math.Abs(c.End - Day) < .001);
        var dayResult = ForecastMood.ForecastMoodTimeline(pawn, Day, [day]);
        var nightResult = ForecastMood.ForecastMoodTimeline(pawn, Day, [night]);
        Assert.True(dayResult.PressureTicks.Value < nightResult.PressureTicks.Value);
        Assert.Equal(.5, At(dayResult, 5 * Hour).Target.Value, 8);
        Assert.Equal(.4, At(nightResult, 5 * Hour).Target.Value, 8);
        Assert.Equal(.66, At(dayResult, 17 * Hour).Target.Value, 8);
        Assert.Equal(.5, At(nightResult, 17 * Hour).Target.Value, 8);
        var controlDay = ForecastMood.ForecastMoodTimeline(Pawn(false), Day, [day]);
        var controlNight = ForecastMood.ForecastMoodTimeline(Pawn(false), Day, [night]);
        Assert.Equal(controlDay.PressureTicks.Value, controlNight.PressureTicks.Value, 8);
        Assert.Equal(controlDay.TerminalMood.Value, controlNight.TerminalMood.Value, 8);
    }

    [Fact]
    public void ABandedThoughtVariesInsideItsSpansAndStaysObservedOutside()
    {
        // #3327: EnvironmentCold at -12 is observed; an assessed warm span from hour 2 to 4 removes it (+12).
        var pawn = Pawn(false);
        var plain = ForecastMood.ForecastMoodTimeline(pawn, Day, []);
        var banded = ForecastMood.ForecastMoodTimeline(pawn, Day, [], banded: [("EnvironmentCold", [(2 * Hour, 4 * Hour, 12.0)])]);
        Assert.Equal(.5, At(banded, 0).Target.Value, 9);
        Assert.Equal(.62, At(banded, 2 * Hour).Target.Value, 9);
        Assert.Equal(.5, At(banded, 4 * Hour).Target.Value, 9);
        Assert.True(banded.PressureTicks.Value < plain.PressureTicks.Value);
        Assert.Contains("situational_thought_constant_outside_exposure:EnvironmentCold", banded.Findings);
        Assert.Contains("unmodeled_situational_thoughts_constant", banded.Findings);
        // A span covering the horizon leaves nothing at the observed constant; no span changes nothing but names the thought.
        Assert.DoesNotContain("situational_thought_constant_outside_exposure:EnvironmentCold",
            ForecastMood.ForecastMoodTimeline(pawn, Day, [], banded: [("EnvironmentCold", [(0, Day, 0.0)])]).Findings);
        var none = ForecastMood.ForecastMoodTimeline(pawn, Day, [], banded: [("EnvironmentCold", [])]);
        Assert.Equal(plain.Trajectory, none.Trajectory);
        Assert.Equal(plain.BreakRisk, none.BreakRisk);
        Assert.Equal(plain.Findings.Append("situational_thought_constant_outside_exposure:EnvironmentCold").Order(StringComparer.Ordinal), none.Findings);
        // One thought changed both ways is stacking.
        Assert.Equal(["unsupported_situational_change_stacking"], ForecastMood.ForecastMoodTimeline(pawn, Day, [], situational: [("EnvironmentCold", 4, 0)],
            banded: [("EnvironmentCold", [])]).Findings);
    }

    [Fact]
    public void ATemperatureCardIsCutWhereItsInputCrossesABand()
    {
        // Comfortable from 10 C; a span cooling from 5 C to -25 C is a deficit of 5 rising to 35: stages 0..3 (-4..-16).
        var card = MechanicCards.ByDef["EnvironmentCold"] with { Status = MechanicCardStatus.Verified };
        var facts = new CardPawnFacts { Traits = Known<EquatableArray<string>>([]), Hediffs = Known<EquatableArray<string>>([]), IdeologyActive = Known(false) };
        var observed = Known<EquatableArray<MoodThought>>([new("EnvironmentCold", -4)]);
        var (thought, spans) = ForecastMood.TemperatureThought(card, facts, observed, [new(0, 30 * Hour, 5, -25, 10, 30)]);
        Assert.Equal("EnvironmentCold", thought);
        double Delta(double tick) => spans.Single(s => s.Start <= tick && tick < s.End).OffsetDelta;
        Assert.Equal(0, Delta(4 * Hour));
        Assert.Equal(-4, Delta(6 * Hour));
        Assert.Equal(-8, Delta(16 * Hour));
        Assert.Equal(-12, Delta(26 * Hour));
        Assert.Equal(30 * Hour, spans.Sum(s => s.End - s.Start), 6);
        // Unknown facts or observations give no spans: the observed constant stays.
        Assert.Empty(ForecastMood.TemperatureThought(card, facts with { IdeologyActive = Known(true) }, observed, [new(0, Hour, 5, 5, 10, 30)]).Spans);
        Assert.Empty(ForecastMood.TemperatureThought(card, facts, default, [new(0, Hour, 5, 5, 10, 30)]).Spans);
        Assert.Empty(ForecastMood.TemperatureThought(card, null, observed, [new(0, Hour, 5, 5, 10, 30)]).Spans);
        // Only a Live card prices: the shipped extracted card waits for its probe (#3328).
        Assert.True(ForecastMood.TemperatureCard(card));
        Assert.False(ForecastMood.TemperatureCard(card with { Status = MechanicCardStatus.Extracted }));
        Assert.False(ForecastMood.TemperatureCard(MechanicCards.ByDef["DeadMansApparel"]));
    }

    [Theory]
    [InlineData(.5, 0)]
    [InlineData(.4, 1 / 4.25)]
    [InlineData(.2, 1 / 1.05)]
    public void BreakRiskIntegratesEachTiersRenewalRateOverTheTargetTrajectory(double target, double perDay)
    {
        var pawn = Pawn(false) with { MoodUnclampedTarget = Known(target), MoodTarget = Known(target) };
        var result = ForecastMood.ForecastMoodTimeline(pawn, Day, []);
        Assert.Equal(perDay, result.BreakRisk.Value, 8);
        Assert.DoesNotContain("mental_break_probability_unassessed", result.Findings);
        // A fading -10 memory lifts the target across the minor threshold halfway: in that tier for half the horizon.
        var fading = ForecastMood.ForecastMoodTimeline(Pawn(false) with { MoodUnclampedTarget = Known(.4), MoodTarget = Known(.4),
            Memories = Known<EquatableArray<NeedMemory>>([new("sting", -10, Known(Day), true)]) }, Day, []);
        Assert.Equal(.5 / 4.25, fading.BreakRisk.Value, 4);
        var unknown = ForecastMood.ForecastMoodTimeline(pawn with { BreakThreshold = default }, Day, []);
        Assert.False(unknown.BreakRisk.IsKnown);
        Assert.Contains("mental_break_probability_unassessed", unknown.Findings);
    }

    [Fact]
    public void ActualMoodRespondsGraduallyAndSleepFreezesActualRatherThanOnlyRemovingThought()
    {
        var pawn = Pawn(false) with { Mood = Known(.2), MoodUnclampedTarget = Known(.8), MoodTarget = Known(.8) };
        var awake = ForecastMood.ForecastMoodTimeline(pawn, Hour, []);
        var asleep = ForecastMood.ForecastMoodTimeline(pawn, Hour, [Sleep("sleep", 0, Hour)]);
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
                [new(23, 6, true, 16, 16)]) };
        var result = ForecastMood.ForecastMoodTimeline(pawn, Day, []);
        Assert.Equal(.66, result.Trajectory[0].Target.Value, 8);
        Assert.Equal(.5, At(result, 5 * Hour).Target.Value, 8);
        Assert.Equal(.66, At(result, 22 * Hour).Target.Value, 8);
        Assert.Equal(.66, result.Trajectory[^1].Target.Value, 8);
    }

    [Fact]
    public void InterruptedSleepEndsSuppressionAndAllowsActualMoodToResumeChanging()
    {
        var sleep = Sleep("sleep", 5 * Hour, 12 * Hour);
        var full = ForecastMood.ForecastMoodTimeline(Pawn(), Day, [sleep]);
        var interrupted = ForecastMood.ForecastMoodTimeline(Pawn(), Day, [sleep], new Dictionary<string, double> { [sleep.Id] = 8 * Hour });
        Assert.True(At(full, 5 * Hour).Asleep);
        Assert.False(At(interrupted, 8 * Hour).Asleep);
        Assert.Equal(.4, At(interrupted, 8 * Hour).Target.Value, 8);
        Assert.True(interrupted.PressureTicks.Value > full.PressureTicks.Value);
        // Points fall only at events; the mood at 9h is the terminal mood of a 9h horizon.
        var fullAtNine = ForecastMood.ForecastMoodTimeline(Pawn(), 9 * Hour, [sleep]);
        var interruptedAtNine = ForecastMood.ForecastMoodTimeline(Pawn(), 9 * Hour, [sleep], new Dictionary<string, double> { [sleep.Id] = 8 * Hour });
        Assert.True(interruptedAtNine.TerminalMood.Value < fullAtNine.TerminalMood.Value);
    }

    [Fact]
    public void MemoryExpiryChangesTargetOnceWhileUnknownExpiryKeepsMoodUnassessed()
    {
        var pawn = Pawn(false) with { MoodUnclampedTarget = Known(.6), MoodTarget = Known(.6),
            Memories = Known<EquatableArray<NeedMemory>>([new("memory", 10, Known(1000d), false)]) };
        var result = ForecastMood.ForecastMoodTimeline(pawn, Hour, []);
        Assert.Equal(.6, result.Trajectory[0].Target.Value, 8);
        Assert.Equal(.5, At(result, 1000).Target.Value, 8);
        var unknown = ForecastMood.ForecastMoodTimeline(pawn with { Memories = Known<EquatableArray<NeedMemory>>([new("memory", 10, default, false)]) }, Hour, []);
        Assert.False(unknown.PressureTicks.IsKnown);
        Assert.False(unknown.TerminalMood.IsKnown);
        Assert.Contains("unknown_conditional_mood_mechanics", unknown.Findings);
    }

    [Fact]
    public void UnclampedObservedTargetPreservesOverflowWhenCurrentEffectEnds()
    {
        var pawn = Pawn(false) with { LocalHour = Known(1d), MoodTarget = Known(1d), MoodUnclampedTarget = Known(1.1),
            ConditionalThoughts = Known<EquatableArray<NeedConditionalThought>>([new(23, 6, true, 16, 16)]) };
        var result = ForecastMood.ForecastMoodTimeline(pawn, Day, []);
        Assert.Equal(1, result.Trajectory[0].Target.Value);
        Assert.Equal(.94, At(result, 5 * Hour).Target.Value, 8);
    }

    [Fact]
    public void UnknownResponseDoesNotEraseKnownTargetAndUnknownConditionalsCannotCreateBenefit()
    {
        var result = ForecastMood.ForecastMoodTimeline(Pawn(false) with { MoodRisePerTick = default }, Hour, []);
        Assert.True(result.PressureTicks.IsKnown);
        Assert.False(result.TerminalMood.IsKnown);
        var unknown = ForecastMood.ForecastMoodTimeline(Pawn() with { ConditionalThoughts = default }, Hour, []);
        Assert.False(unknown.PressureTicks.IsKnown);
        Assert.False(unknown.TerminalMood.IsKnown);
    }

    [Fact]
    public void GenerationProtectsActualObligationsAndReportsBoundsDeterministically()
    {
        var occupied = Capacity() with { Occupancy = Known<EquatableArray<ForecastOccupancy>>(
            [new("urgent", "pawn", Known(5 * Hour), Known(12 * Hour), ForecastOccupancyState.Active)]) };
        var generated = ForecastNeeds.GenerateNeedsAlternatives(Pawn(), ForecastCapacity.ForecastPawnCapacityOf(occupied), Day);
        Assert.DoesNotContain(generated.Choices, c => c.Depart < 12 * Hour && c.End > 5 * Hour);
        Assert.Contains(generated.Choices, c => Math.Abs(c.Start - 17 * Hour) < .001 && Math.Abs(c.End - Day) < .001);
        var bounded = ForecastNeeds.GenerateNeedsAlternatives(Pawn(), ForecastCapacity.ForecastPawnCapacityOf(Capacity()), Day, budget: 2);
        Assert.True(bounded.Truncated);
        Assert.Contains("needs_generation_truncated", bounded.Findings);
        Assert.Equal(bounded, ForecastNeeds.GenerateNeedsAlternatives(Pawn(), ForecastCapacity.ForecastPawnCapacityOf(Capacity()), Day, budget: 2));
    }
    [Fact]
    public void NativeWakeEndsRecoveryAndThoughtSuppressionAtSamePhysicalInstant()
    {
        var pawn = Pawn() with { LocalHour = Known(11d), MoodUnclampedTarget = Known(.4),
            ConditionalThoughts = Known<EquatableArray<NeedConditionalThought>>([new(11, 18, true, -10, -10)]),
            WakeThreshold = Known(.8), Needs = [new("rest", Known(.3), Known(0d), Known(.28))] };
        var sleep = new ForecastNeedChoice("long", "pawn", "rest", "bed", "", 0, 0, 0, 1000, 1, []);
        var result = ForecastNeeds.ForecastNeedsTimeline(pawn, Capacity() with { HorizonTicks = 1000 }, [sleep]);
        Assert.Equal(.8, result.Needs.Single(n => n.Kind == "rest").FinalLevel.Value, 8);
        Assert.False(At(result.Mood!, 500).Asleep);
        Assert.Equal(.4, At(result.Mood!, 500).Target.Value, 8);
        Assert.Contains("sleep_service_capped_at_native_wake:long", result.Findings);
        Assert.DoesNotContain(result.Capacity.Capacity.Windows.Value, w => w.Start < 1000);
    }

    [Fact]
    public void DelayedGenerationRecalculatesWakeTimeInsteadOfSlowingTheObservedRecoveryRate()
    {
        var pawn = Pawn() with { WakeThreshold = Known(.8), Needs = [new("rest", Known(.3), Known(.00001), Known(.28))] };
        var generated = ForecastNeeds.GenerateNeedsAlternatives(pawn, ForecastCapacity.ForecastPawnCapacityOf(Capacity()), Day);
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
        Assert.Empty(ForecastNeeds.GenerateNeedsAlternatives(pawn, ForecastCapacity.ForecastPawnCapacityOf(Capacity()), Day).Choices);
        var result = ForecastNeeds.ForecastNeedsTimeline(pawn, Capacity(), [Sleep("unknown", 0, Hour)]);
        Assert.False(result.Needs.Single(n => n.Kind == "rest").FinalLevel.IsKnown);
        Assert.False(result.Mood!.PressureTicks.IsKnown);
        Assert.False(ForecastMood.ForecastMoodTimeline(Pawn() with { Awake = Known(false) }, Day, []).PressureTicks.IsKnown);
        var continuation = ForecastMood.ForecastMoodTimeline(Pawn() with { Awake = Known(false) }, Day, [Sleep("continue", 0, Hour)]);
        Assert.True(continuation.PressureTicks.IsKnown);
    }

    [Fact]
    public void StarvingSleepRequiresCompletedMealAndInterruptedEatingCannotUnlockIt()
    {
        var pawn = Pawn() with { SleepWhileStarving = Known(false),
            Needs = [new("rest", Known(.3), Known(0d), Known(.28)),
                new("food", Known(0d), Known(0d), Known(.2))] };
        var sleep = Sleep("sleep", 200, 1000);
        var meal = new ForecastNeedChoice("meal", "pawn", "food", "food", "Rice", 1, 0, 0, 100, .5, []);
        Assert.False(ForecastNeeds.ForecastNeedsTimeline(pawn, Capacity(), [sleep]).Needs.Single(n => n.Kind == "rest").FinalLevel.IsKnown);
        Assert.True(ForecastNeeds.ForecastNeedsTimeline(pawn, Capacity(), [meal, sleep]).Needs.Single(n => n.Kind == "rest").FinalLevel.IsKnown);
        Assert.False(ForecastNeeds.ForecastNeedsTimeline(pawn, Capacity(), [meal, sleep], new Dictionary<string, double> { ["meal"] = 50 })
            .Needs.Single(n => n.Kind == "rest").FinalLevel.IsKnown);
        Assert.True(ForecastNeeds.ForecastNeedsTimeline(pawn with { SleepWhileStarving = Known(true) }, Capacity(), [sleep])
            .Needs.Single(n => n.Kind == "rest").FinalLevel.IsKnown);
    }
    private static (ForecastCoupledCandidates Candidates, ForecastCoupledSelection Result) SelectObserved(NeedsForecastPawn pawn, double horizon = Day)
    {
        var facts = WorkResourceTestFacts.Create("pawn");
        pawn = pawn with { Targets = EquatableArray.From(pawn.Targets.Select(t => t with { Location = new(1, 1) })) };
        facts = facts with { ForecastObserved = facts.ForecastObserved with
        {
            Needs = new() { Tick = 100, Pawns = [pawn] },
            Coupled = new() { Research = ActivityContinuationFixtures.NoResearch, Hunt = HuntFixtures.NoHunt, Tick = 100, Food = new(), Construction = ConstructionFixtures.NoConstruction(), Routes = [new("pawn", new(1, 1), new(1, 1), Known(0d)) { FromStart = true }] },
        } };
        facts = WorkRules.ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([]));
        var candidates = ForecastCoupledCandidates.GenerateCoupledActivities(facts, horizon)!.Priced(horizon);
        facts = facts with { CoupledAlternatives = candidates };
        var request = ForecastCoupled.CoupledForecastRequestOf(new("world/map/load", 100), horizon / GameTime.TicksPerDay,
            facts,  new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, [], new());
        return (candidates, ForecastCoupledSelection.CompareActivityOutcomes(request, candidates));
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
        // Rest that reaches zero within minutes breaks the zero floor unless a sleep starts before it does.
        var (_, urgent) = SelectObserved(Pawn() with { Needs = [new("rest", Known(.27), Known(.00054), Known(.28))] });
        Assert.InRange(Assert.Single(urgent.Selected.Plan.Allocations).Start, 0, .27 / .00054);
        Assert.Equal(0d, urgent.SelectedVector.Value.UrgentZero.Excess);
        Assert.True(urgent.BaselineVector.Value.UrgentZero.Excess > 0);
        var (_, shortHorizon) = SelectObserved(Pawn(), 2 * Hour);
        Assert.Empty(shortHorizon.Selection.Selected);
    }
    [Fact]
    public void NativeThresholdAndDisturbanceDelayActivationAndTinyResponseIntervalAddsNoSegments()
    {
        var pawn = Pawn() with { Needs = [new("rest", Known(.9), Known(.00001), Known(.28))], SleepBlockedUntilTicks = Known(20000d) };
        var generated = ForecastNeeds.GenerateNeedsAlternatives(pawn, ForecastCapacity.ForecastPawnCapacityOf(Capacity()), Day);
        Assert.NotEmpty(generated.Choices);
        Assert.All(generated.Choices, c => Assert.True(c.Start >= 20000 && Math.Max(0, .9 - .00001 * c.Start) < .75));
        // Mood moves in exact segments between events (#3468); the native response interval adds none.
        var tiny = ForecastMood.ForecastMoodTimeline(Pawn() with { MoodIntervalTicks = Known(.000001) }, Day, []);
        var native = ForecastMood.ForecastMoodTimeline(Pawn(), Day, []);
        Assert.Equal(native.Trajectory.Count, tiny.Trajectory.Count);
        Assert.Equal(native.TerminalMood, tiny.TerminalMood);
    }
}
