using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ForecastConditionalMoodTests
{
    private const double Hour = GameTime.TicksPerHour;
    private const double Day = GameTime.TicksPerDay;
    private static NeedsForecastPawn Pawn(bool conditional = true) => NeedsFixtures.Pawn() with
    {
        Pawn = "pawn", State = "eligible", LocalHour = 6d, Awake = true,
        SleepThreshold = .75, WakeThreshold = 1d, SleepBlockedUntilTicks = 0d,
        Mood = NeedsFixtures.Mood with { Level = .5, Target = .5, UnclampedTarget = .5, BreakThreshold = .45, RisePerTick = .12 / Hour, FallPerTick = .08 / Hour, FreezesWhileAsleep = true, Conditional = new ConditionalMood.Modeled(conditional
            ? [new(11, 18, true, -10, 0), new(23, 6, true, 16, 0)]
            : []), Memories = [] },
        Needs = [NeedsFixtures.Rate("rest", .3, 0d, .28)],
        Targets = [NeedsFixtures.Service("bed", "rest", 0d, .7 / (7 * Hour))],
    };
    internal static ForecastNeedChoice SleepProbe(string id, double from, double to) => Sleep(id, from, to);
    internal static NeedsForecastPawn PawnProbe() => Pawn();
    private static ForecastNeedChoice Sleep(string id, double from, double to)
        => new(id, "pawn", "rest", "bed", "", 0, from, from, to, (to - from) * .7 / (7 * Hour), []);
    private static ForecastCapacityInput Capacity() => new()
    {
        Pawn = new() { Id = new("pawn"), State = ForecastPawnCapacityState.Available,
            Schedule = EquatableArray.From(Enumerable.Repeat(PawnRules.ScheduleSleep, 24)) },
        Location = "bed", HorizonTicks = Day, LocalHour = 6d, OpportunityCostPerTick = 1d,
        CurrentWork = new(new("pawn"), false, WorkType.None, new ForecastWorkRemaining.Timed(0d)), Occupancy = new ForecastProtection.Read([]),
        LocationAfterProtectedTime = "bed", ProtectTimetable = false,
    };
    private static ForecastMoodPoint At(ForecastMoodResult result, double tick)
        => Assert.Single(result.Projected().Trajectory, p => Math.Abs(p.Tick - tick) < .000001);

    [Fact]
    public void GeneratedDayAndNightSleepCompareConditionalConsequencesAndControlRemovesThatPreference()
    {
        var pawn = Pawn();
        var generated = ForecastNeeds.GenerateNeedsAlternatives(pawn, ForecastCapacity.ForecastPawnCapacityOf(Capacity()), Day);
        var day = Assert.Single(generated.Choices, c => Math.Abs(c.Start - 5 * Hour) < .001 && Math.Abs(c.End - 12 * Hour) < .001);
        var night = Assert.Single(generated.Choices, c => Math.Abs(c.Start - 17 * Hour) < .001 && Math.Abs(c.End - Day) < .001);
        var dayResult = ForecastMood.ForecastMoodTimeline(pawn, Day, [day]);
        var nightResult = ForecastMood.ForecastMoodTimeline(pawn, Day, [night]);
        Assert.True(dayResult.Projected().PressureTicks < nightResult.Projected().PressureTicks);
        Assert.Equal(.5, At(dayResult, 5 * Hour).Target, 8);
        Assert.Equal(.4, At(nightResult, 5 * Hour).Target, 8);
        Assert.Equal(.66, At(dayResult, 17 * Hour).Target, 8);
        Assert.Equal(.5, At(nightResult, 17 * Hour).Target, 8);
        var controlDay = ForecastMood.ForecastMoodTimeline(Pawn(false), Day, [day]);
        var controlNight = ForecastMood.ForecastMoodTimeline(Pawn(false), Day, [night]);
        Assert.Equal(controlDay.Projected().PressureTicks, controlNight.Projected().PressureTicks, 8);
        Assert.Equal(controlDay.Projected().TerminalMood, controlNight.Projected().TerminalMood, 8);
    }

    [Fact]
    public void ABandedThoughtVariesInsideItsSpansAndStaysObservedOutside()
    {
        // #3327: EnvironmentCold at -12 is observed; an assessed warm span from hour 2 to 4 removes it (+12).
        var pawn = Pawn(false);
        var plain = ForecastMood.ForecastMoodTimeline(pawn, Day, []);
        var banded = ForecastMood.ForecastMoodTimeline(pawn, Day, [], banded: [("EnvironmentCold", [(2 * Hour, 4 * Hour, 12.0)])]);
        Assert.Equal(.5, At(banded, 0).Target, 9);
        Assert.Equal(.62, At(banded, 2 * Hour).Target, 9);
        Assert.Equal(.5, At(banded, 4 * Hour).Target, 9);
        Assert.True(banded.Projected().PressureTicks < plain.Projected().PressureTicks);
        Assert.Contains("situational_thought_constant_outside_exposure:EnvironmentCold", banded.Findings);
        Assert.Contains("unmodeled_situational_thoughts_constant", banded.Findings);
        // A span covering the horizon leaves nothing at the observed constant; no span changes nothing but names the thought.
        Assert.DoesNotContain("situational_thought_constant_outside_exposure:EnvironmentCold",
            ForecastMood.ForecastMoodTimeline(pawn, Day, [], banded: [("EnvironmentCold", [(0, Day, 0.0)])]).Findings);
        var none = ForecastMood.ForecastMoodTimeline(pawn, Day, [], banded: [("EnvironmentCold", [])]);
        Assert.Equal(plain.Projected().Trajectory, none.Projected().Trajectory);
        Assert.Equal(plain.Projected().Breaks, none.Projected().Breaks);
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
        var facts = new CardPawnFacts { Traits = [], Hediffs = [], IdeologyActive = false, Precepts = [] };
        EquatableArray<MoodThought> observed = [new("EnvironmentCold", -4)];
        var (thought, spans) = ForecastMood.TemperatureThought(card, facts, observed, [new(0, 30 * Hour, 5, -25, 10, 30)]);
        Assert.Equal("EnvironmentCold", thought);
        double Delta(double tick) => spans.Single(s => s.Start <= tick && tick < s.End).OffsetDelta;
        Assert.Equal(0, Delta(4 * Hour));
        Assert.Equal(-4, Delta(6 * Hour));
        Assert.Equal(-8, Delta(16 * Hour));
        Assert.Equal(-12, Delta(26 * Hour));
        Assert.Equal(30 * Hour, spans.Sum(s => s.End - s.Start), 6);
        // No pawn facts give no spans: the observed constant stays.
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
        var pawn = Pawn(false) with { Mood = Pawn(false).Mood! with { UnclampedTarget = target, Target = target }};
        var result = ForecastMood.ForecastMoodTimeline(pawn, Day, []);
        Assert.Equal(perDay, result.Assessed().Risk, 8);
        Assert.DoesNotContain("mental_break_probability_unassessed", result.Findings);
        // A fading -10 memory lifts the target across the minor threshold halfway: in that tier for half the horizon.
        var fading = ForecastMood.ForecastMoodTimeline(Pawn(false) with { Mood = Pawn(false).Mood! with { UnclampedTarget = .4, Target = .4, Memories = [NeedsFixtures.Memory("sting", -10, Day, true)] }}, Day, []);
        Assert.Equal(.5 / 4.25, fading.Assessed().Risk, 4);
    }

    [Fact]
    public void ActualMoodRespondsGraduallyAndSleepFreezesActualRatherThanOnlyRemovingThought()
    {
        var pawn = Pawn(false) with { Mood = Pawn(false).Mood! with { Level = .2, UnclampedTarget = .8, Target = .8 }};
        var awake = ForecastMood.ForecastMoodTimeline(pawn, Hour, []);
        var asleep = ForecastMood.ForecastMoodTimeline(pawn, Hour, [Sleep("sleep", 0, Hour)]);
        Assert.Equal(.8, awake.Projected().Trajectory[0].Target, 8);
        Assert.Equal(.2, awake.Projected().Trajectory[0].Actual, 8);
        Assert.Equal(.32, awake.Projected().TerminalMood, 8);
        Assert.Equal(.2, asleep.Projected().TerminalMood, 8);
        Assert.True(awake.Projected().ActualExposureTicks > 0);
        Assert.Equal(0, awake.Projected().TargetExposureTicks);
        Assert.Contains("mood_response_interval_phase_unassessed", awake.Findings);
    }

    [Fact]
    public void WrappedWindowReappearsAfterMorningBoundaryWithoutDuplicateCurrentCredit()
    {
        var pawn = Pawn() with { LocalHour = 1d, Mood = Pawn(false).Mood! with { Target = .66, UnclampedTarget = .66, Conditional = new ConditionalMood.Modeled([new(23, 6, true, 16, 16)]) }};
        var result = ForecastMood.ForecastMoodTimeline(pawn, Day, []);
        Assert.Equal(.66, result.Projected().Trajectory[0].Target, 8);
        Assert.Equal(.5, At(result, 5 * Hour).Target, 8);
        Assert.Equal(.66, At(result, 22 * Hour).Target, 8);
        Assert.Equal(.66, result.Projected().Trajectory[^1].Target, 8);
    }

    [Fact]
    public void InterruptedSleepEndsSuppressionAndAllowsActualMoodToResumeChanging()
    {
        var sleep = Sleep("sleep", 5 * Hour, 12 * Hour);
        var full = ForecastMood.ForecastMoodTimeline(Pawn(), Day, [sleep]);
        var interrupted = ForecastMood.ForecastMoodTimeline(Pawn(), Day, [sleep], new Dictionary<string, double> { [sleep.Id] = 8 * Hour });
        Assert.True(At(full, 5 * Hour).Asleep);
        Assert.False(At(interrupted, 8 * Hour).Asleep);
        Assert.Equal(.4, At(interrupted, 8 * Hour).Target, 8);
        Assert.True(interrupted.Projected().PressureTicks > full.Projected().PressureTicks);
        // Points fall only at events; the mood at 9h is the terminal mood of a 9h horizon.
        var fullAtNine = ForecastMood.ForecastMoodTimeline(Pawn(), 9 * Hour, [sleep]);
        var interruptedAtNine = ForecastMood.ForecastMoodTimeline(Pawn(), 9 * Hour, [sleep], new Dictionary<string, double> { [sleep.Id] = 8 * Hour });
        Assert.True(interruptedAtNine.Projected().TerminalMood < fullAtNine.Projected().TerminalMood);
    }

    [Fact]
    public void MemoryExpiryChangesTargetOnce()
    {
        var pawn = Pawn(false) with { Mood = Pawn(false).Mood! with { UnclampedTarget = .6, Target = .6, Memories = [NeedsFixtures.Memory("memory", 10, 1000d, false)] }};
        var result = ForecastMood.ForecastMoodTimeline(pawn, Hour, []);
        Assert.Equal(.6, result.Projected().Trajectory[0].Target, 8);
        Assert.Equal(.5, At(result, 1000).Target, 8);
    }

    [Fact]
    public void UnclampedObservedTargetPreservesOverflowWhenCurrentEffectEnds()
    {
        var pawn = Pawn(false) with { LocalHour = 1d, Mood = Pawn(false).Mood! with { Target = 1d, UnclampedTarget = 1.1, Conditional = new ConditionalMood.Modeled([new(23, 6, true, 16, 16)]) }};
        var result = ForecastMood.ForecastMoodTimeline(pawn, Day, []);
        Assert.Equal(1, result.Projected().Trajectory[0].Target);
        Assert.Equal(.94, At(result, 5 * Hour).Target, 8);
    }

    [Fact]
    public void UnmodeledConditionalsCannotCreateBenefit()
    {
        var unknown = ForecastMood.ForecastMoodTimeline(Pawn() with { Mood = Pawn().Mood! with
            { Conditional = new ConditionalMood.Unmodeled(["NightOwl"]) } }, Hour, []);
        Assert.IsType<ForecastMoodResult.Unprojected>(unknown);
        Assert.Equal(["unknown_conditional_mood_mechanics:NightOwl"], unknown.Findings);
    }

    [Fact]
    public void GenerationProtectsActualObligationsAndReportsBoundsDeterministically()
    {
        var occupied = Capacity() with { Occupancy = new ForecastProtection.Read(
            [new("urgent", "pawn", 5 * Hour, 12 * Hour, ForecastOccupancyState.Active)]) };
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
        var pawn = Pawn() with { LocalHour = 11d,
            Mood = Pawn(false).Mood! with { UnclampedTarget = .4, Conditional = new ConditionalMood.Modeled([new(11, 18, true, -10, -10)]) },
            WakeThreshold = .8, Needs = [NeedsFixtures.Rate("rest", .3, 0d, .28)] };
        var sleep = new ForecastNeedChoice("long", "pawn", "rest", "bed", "", 0, 0, 0, 1000, 1, []);
        var result = ForecastNeeds.ForecastNeedsTimeline(pawn, Capacity() with { HorizonTicks = 1000 }, [sleep]);
        Assert.Equal(.8, result.Needs.Single(n => n.Kind == "rest").FinalLevel.Number(), 8);
        Assert.False(At(result.Mood!, 500).Asleep);
        Assert.Equal(.4, At(result.Mood!, 500).Target, 8);
        Assert.Contains("sleep_service_capped_at_native_wake:long", result.Findings);
        Assert.DoesNotContain(result.Capacity.Capacity.Windows.ReadWindows(), w => w.Start < 1000);
    }

    [Fact]
    public void DelayedGenerationRecalculatesWakeTimeInsteadOfSlowingTheObservedRecoveryRate()
    {
        var pawn = Pawn() with { WakeThreshold = .8, Needs = [NeedsFixtures.Rate("rest", .3, .00001, .28)] };
        var generated = ForecastNeeds.GenerateNeedsAlternatives(pawn, ForecastCapacity.ForecastPawnCapacityOf(Capacity()), Day);
        Assert.All(generated.Choices, choice =>
        {
            var before = Math.Max(0, .3 - .00001 * choice.Start);
            Assert.True(before + choice.Gain <= .8 + .00000001);
            Assert.Equal(Assert.IsType<NeedRecovery.Rate>(pawn.Targets[0].Recovery).GainPerTick, choice.Gain / (choice.End - choice.Start), 10);
        });
        Assert.Contains(generated.Choices, c => c.Start > 0 && Math.Abs(Math.Max(0, .3 - .00001 * c.Start) + c.Gain - .8) < .000001);
    }

    [Fact]
    public void UnsupportedSleepActivationCannotCreateKnownRestOrMoodBenefits()
    {
        var pawn = Pawn() with { SleepBlocked = true };
        Assert.Empty(ForecastNeeds.GenerateNeedsAlternatives(pawn, ForecastCapacity.ForecastPawnCapacityOf(Capacity()), Day).Choices);
        var result = ForecastNeeds.ForecastNeedsTimeline(pawn, Capacity(), [Sleep("unknown", 0, Hour)]);
        Assert.False(result.Needs.Single(n => n.Kind == "rest").FinalLevel is ForecastTerm.Assessed);
        Assert.IsType<ForecastMoodResult.Unprojected>(result.Mood);
        // Asleep now without a rest from tick 0, the outcome wakes the pawn there (#3882): its mood runs awake from it.
        var woken = ForecastMood.ForecastMoodTimeline(Pawn() with { Awake = false }, Day, []);
        woken.Projected();
        Assert.Equal(ForecastMood.ForecastMoodTimeline(Pawn(), Day, []).Projected().PressureTicks, woken.Projected().PressureTicks);
        var continuation = ForecastMood.ForecastMoodTimeline(Pawn() with { Awake = false }, Day, [Sleep("continue", 0, Hour)]);
        continuation.Projected();
    }

    [Fact]
    public void StarvingSleepRequiresCompletedMealAndInterruptedEatingCannotUnlockIt()
    {
        var pawn = Pawn() with { SleepWhileStarving = false,
            Needs = [NeedsFixtures.Rate("rest", .3, 0d, .28),
                NeedsFixtures.Rate("food", 0d, 0d, .2)] };
        var sleep = Sleep("sleep", 200, 1000);
        var meal = new ForecastNeedChoice("meal", "pawn", "food", "food", "Rice", 1, 0, 0, 100, .5, []);
        Assert.False(ForecastNeeds.ForecastNeedsTimeline(pawn, Capacity(), [sleep]).Needs.Single(n => n.Kind == "rest").FinalLevel is ForecastTerm.Assessed);
        Assert.True(ForecastNeeds.ForecastNeedsTimeline(pawn, Capacity(), [meal, sleep]).Needs.Single(n => n.Kind == "rest").FinalLevel is ForecastTerm.Assessed);
        Assert.False(ForecastNeeds.ForecastNeedsTimeline(pawn, Capacity(), [meal, sleep], new Dictionary<string, double> { ["meal"] = 50 })
            .Needs.Single(n => n.Kind == "rest").FinalLevel is ForecastTerm.Assessed);
        Assert.True(ForecastNeeds.ForecastNeedsTimeline(pawn with { SleepWhileStarving = true }, Capacity(), [sleep])
            .Needs.Single(n => n.Kind == "rest").FinalLevel is ForecastTerm.Assessed);
    }
    private static (ForecastCoupledCandidates Candidates, ForecastCoupledSelection Result) SelectObserved(NeedsForecastPawn pawn, double horizon = Day)
    {
        var facts = WorkResourceTestFacts.Create("pawn");
        pawn = pawn with { Targets = EquatableArray.From(pawn.Targets.Select(t => t with { Location = new(1, 1) })) };
        facts = facts with { ForecastObserved = facts.ForecastObserved with
        {
            Needs = NeedsFixtures.Empty with { Tick = new(100), Pawns = [pawn] },
            Coupled = CoupledFixtures.Empty with { Research = ActivityContinuationFixtures.NoResearch, Hunt = HuntFixtures.NoHunt, Clothing = ClothingFixtures.NoClothing, Tick = 100, Food = CoupledFixtures.NoFood, Construction = ConstructionFixtures.NoConstruction(), Routes = [new("pawn", new(1, 1), new(1, 1), new RouteTicks.Reachable(0d)) { FromStart = true }] },
        } };
        facts = WorkRules.ReviewWorkResources(facts, new ProductionBatchCensus.Read([]), new([]));
        var candidates = ForecastCoupledCandidates.GenerateCoupledActivities(facts, horizon)!.Priced(horizon);
        facts = facts with { CoupledAlternatives = candidates };
        var request = ForecastCoupled.CoupledForecastRequestOf(new(100), horizon / GameTime.TicksPerDay,
            facts,  new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, []);
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
        Assert.True(selected.SelectedVector.BreakRisk.Number() < selected.BaselineVector.BreakRisk.Number());
        var (_, control) = SelectObserved(Pawn(false));
        Assert.Empty(control.Selection.Selected);
        // Rest that reaches zero within minutes breaks the zero floor unless a sleep starts before it does.
        var (_, urgent) = SelectObserved(Pawn() with { Needs = [NeedsFixtures.Rate("rest", .27, .00054, .28)] });
        Assert.InRange(Assert.Single(urgent.Selected.Plan.Allocations).Start, 0, .27 / .00054);
        Assert.Equal(0d, urgent.SelectedVector.Value.UrgentZero.Excess);
        Assert.True(urgent.BaselineVector.Value.UrgentZero.Excess > 0);
        var (_, shortHorizon) = SelectObserved(Pawn(), 2 * Hour);
        Assert.Empty(shortHorizon.Selection.Selected);
    }
    [Fact]
    public void NativeThresholdAndDisturbanceDelayActivation()
    {
        var pawn = Pawn() with { Needs = [NeedsFixtures.Rate("rest", .9, .00001, .28)], SleepBlockedUntilTicks = 20000d };
        var generated = ForecastNeeds.GenerateNeedsAlternatives(pawn, ForecastCapacity.ForecastPawnCapacityOf(Capacity()), Day);
        Assert.NotEmpty(generated.Choices);
        Assert.All(generated.Choices, c => Assert.True(c.Start >= 20000 && Math.Max(0, .9 - .00001 * c.Start) < .75));
    }
}
