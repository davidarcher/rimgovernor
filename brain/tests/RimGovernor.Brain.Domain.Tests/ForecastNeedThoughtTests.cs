using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

// #3598: the food and rest thoughts follow the needs timeline, so time below the hungry or tired threshold costs its mood
// debuff and the break risk it raises in the capability-days ledger.
public sealed class ForecastNeedThoughtTests
{
    private const double Day = GameTime.TicksPerDay;
    private const double Hour = GameTime.TicksPerHour;

    // The horizon the meal plans are compared over: both are fed again by its end, so neither reaches zero food.
    private const double H = 20000;

    // Food falls from .25 by .1 every 4000 ticks: hungry below .3 from the start, urgently hungry below .15 from tick 4000.
    private const double Fall = .1 / 4000;

    private static NeedsForecastPawn Pawn(double moodTarget) => ForecastValueTests.Pawn() with
    {
        LocalHour = 12d,
        Mood = NeedsFixtures.Mood with { Level = moodTarget, Target = moodTarget, UnclampedTarget = moodTarget, BreakThreshold = .35, RisePerTick = .12 / Hour, FallPerTick = .08 / Hour, FreezesWhileAsleep = true, Conditional = new ConditionalMood.Modeled([]) },
        Needs = [NeedsFixtures.Rate("rest", 1d, 0d, .28) with { UnservedGainPerTick = 1 / 1500d }, NeedsFixtures.Rate("food", .25, Fall, .3)],
    };

    private static ForecastCapacityInput Input(double horizon = H) => new()
    {
        Pawn = new() { Id = new("pawn"), State = ForecastPawnCapacityState.Available,
            Schedule = EquatableArray.From(Enumerable.Repeat(PawnRules.ScheduleSleep, 24)) },
        Location = "start", HorizonTicks = horizon, LocalHour = 12.0, OpportunityCostPerTick = 1.0,
        CurrentWork = new(new("pawn"), false, WorkType.None, new ForecastWorkRemaining.Timed(0.0)), Occupancy = new ForecastProtection.Read([]),
        LocationAfterProtectedTime = "bed", ProtectTimetable = false,
    };

    private static ForecastNeedChoice Meal(double start) => new("eat@" + start, "pawn", "food", "meal", "MealSimple", 1, start, start, start + 1000, .9, []);

    private static ForecastNeedsResult Eats(NeedsForecastPawn pawn, double start) => ForecastNeeds.ForecastNeedsTimeline(pawn, Input(), [Meal(start)]);

    private static ForecastNeedMetric Joy(double exposure) => new("joy", new ForecastTerm.Assessed(.5), new ForecastTerm.Assessed(0d), new ForecastTerm.Assessed(exposure), new ForecastTerm.Assessed(0d)) { ZeroTicks = new ForecastTerm.Assessed(0d), ThresholdDepthTicks = new ForecastTerm.Assessed(0d) };

    private static ForecastCoupledOutcome Outcome(ForecastNeedsResult needs) =>
        new(new([], 0, 0), true, new Dictionary<string, ForecastNeedsResult> { ["pawn"] = needs }, [])
        { Support = new() { FoodPoisonExposure = 0 }, ThermalExposure = [new("pawn", H, 0, 0, 0)] };

    [Fact]
    public void TheFoodThoughtFollowsTheNeedsTimelineStageByStage()
    {
        // Hungry at the review, urgently hungry at 4000, fed by the meal ending at 9000: -6 observed, -12 from 4000, none after.
        var food = Eats(Pawn(.5), 8000).Needs.Single(n => n.Kind == "food");

        Assert.Equal([new(0, 4000, 0), new(4000, 9000, 1), new(9000, H, -1)], food.StageSpans.ToArray());
        var (thought, spans) = Assert.Single(ForecastMood.NeedThoughts([food]));
        Assert.Equal("NeedFood", thought);
        Assert.Equal([(0d, 4000d, 0d), (4000d, 9000d, -6d), (9000d, H, 6d)], spans);
    }

    [Fact]
    public void TheRestThoughtStagesAtTheTiredVeryTiredAndExhaustedLevels()
    {
        // Rest falls from .3 by .01 every 1000 ticks: tired below .28 at 2000, very tired below .14 at 16000, exhausted below .01 at 29000.
        var pawn = Pawn(.5) with { Needs = [NeedsFixtures.Rate("rest", .3, .01 / 1000, .28)] };

        var rest = ForecastNeeds.ForecastNeedsTimeline(pawn, Input(Day), [], mood: false).Needs.Single(n => n.Kind == "rest");

        Assert.Equal([-1, 0, 1, 2], rest.StageSpans.Select(s => s.Stage));
        Assert.Equal([2000, 16000, 29000], rest.StageSpans.Skip(1).Select(s => Math.Round(s.Start, 6)));
        Assert.Equal([0d, -6d, -12d, -18d], Assert.Single(ForecastMood.NeedThoughts([rest])).Spans.Select(s => s.OffsetDelta));
    }

    [Fact]
    public void EatingAHungryPawnFirstBeatsPlayingFirstWhenTheHungerDebuffCrossesTheBreakThreshold()
    {
        // Review 18240 (#3598): the plan that plays first leaves the worker hungry until it eats just before zero. At mood
        // .36 against a .35 threshold, urgent hunger's extra -6 is a minor-break tier the early meal never enters.
        var pawn = Pawn(.36);
        var context = ForecastValueTests.Context(pawn);
        var early = Eats(pawn, 100);
        var late = Eats(pawn, 8000);
        var eatFirst = early with { Needs = [.. early.Needs, Joy(5000)] };
        var playFirst = late with { Needs = [.. late.Needs, Joy(0)] };

        var eat = ForecastCoupledSelection.CoupledConsequences(Outcome(eatFirst), [], context, H, Outcome(eatFirst));
        var play = ForecastCoupledSelection.CoupledConsequences(Outcome(playFirst), [], context, H, Outcome(eatFirst));

        // Neither plan starves or exhausts the pawn: the floors and the physiological terms tie.
        Assert.Equal(0, eat.Value.UrgentZero.Excess);
        Assert.Equal(0, play.Value.UrgentZero.Excess);
        Assert.Equal(play.Value.Ledger.Hunger.Loss(), eat.Value.Ledger.Hunger.Loss());
        Assert.Equal(play.Value.Ledger.Rest.Loss(), eat.Value.Ledger.Rest.Loss());
        Assert.Equal(0, eat.Value.Ledger.MoodBreaks.Loss());
        Assert.True(play.Value.Ledger.MoodBreaks.Loss() > new ForecastValuePolicy().TieResolution);
        Assert.True(play.Value.Ledger.Wellbeing.Loss() > 0);
        Assert.True(ForecastCoupledSelection.CompareConsequences(eat, play) < 0);
        Assert.True(ForecastCoupledSelection.CompareConsequences(play, eat) > 0);
    }

    [Fact]
    public void ACarefreePawnStillPaysTheHungerDebuffAsWellbeing()
    {
        // Far above every threshold the debuff raises no break risk; the later meal costs the extra mood-days at the wellbeing weight.
        var pawn = Pawn(.8);
        var context = ForecastValueTests.Context(pawn);
        var early = Outcome(Eats(pawn, 100));

        var late = ForecastValue.Of(Outcome(Eats(pawn, 8000)), context, H, early);

        Assert.Equal(0, late.MoodBreaks.Loss());
        Assert.True(late.Wellbeing.Loss() > 0);
        Assert.True(late.LossAgainst(ForecastValue.Of(early, context, H, early)) > 0);
    }

    // A pawn that ate without a table carries AteWithoutTable; its next meal renews it (stackLimit 1) rather than adding a second.
    private static NeedMemory WithoutTable(double remaining, int stackLimit = 1) => NeedsFixtures.Memory("AteWithoutTable", -3, remaining, false) with { StackLimit = stackLimit };

    private static ForecastNeedChoice MealWith(double start, NeedMemory thought) => Meal(start) with { Id = "eat@" + start, Thoughts = [thought] };

    [Fact]
    public void AMealRenewsTheStackLimitOneMemoryItRepeats()
    {
        // Observed -3 with 30000 ticks left; the meal ending at 9000 renews it, so the horizon still holds one -3, not two.
        var pawn = Pawn(.5) with { Mood = Pawn(.5).Mood! with { Memories = [WithoutTable(30000)] }};

        var result = ForecastNeeds.ForecastNeedsTimeline(pawn, Input(), [MealWith(8000, WithoutTable(Day))]);

        Assert.Equal(0, result.ThoughtOffsetAtHorizon.Number());
        result.Mood!.Assessed();
        Assert.DoesNotContain("unsupported_memory_renewal_or_stacking", result.Mood!.Findings);
    }

    [Fact]
    public void ASecondMealRenewsTheFirstMealsMemory()
    {
        var result = ForecastNeeds.ForecastNeedsTimeline(Pawn(.5), Input(), [MealWith(2000, WithoutTable(Day)), MealWith(12000, WithoutTable(Day))]);

        Assert.Equal(-3, result.ThoughtOffsetAtHorizon.Number());
        result.Mood!.Assessed();
    }

    [Fact]
    public void AMealSharingAStackingDefStaysUnassessed()
    {
        var pawn = Pawn(.5) with { Mood = Pawn(.5).Mood! with { Memories = [WithoutTable(30000, stackLimit: 3)] }};

        var result = ForecastNeeds.ForecastNeedsTimeline(pawn, Input(), [MealWith(8000, WithoutTable(Day, stackLimit: 3))]);

        Assert.False(result.ThoughtOffsetAtHorizon is ForecastTerm.Assessed);
        Assert.Equal(["unsupported_memory_renewal_or_stacking"], result.Mood!.Findings);
    }

    [Fact]
    public void StackedObservedMemoriesExpireOneByOneAndNameTheApproximation()
    {
        // Two -5 memories of one def, one expiring inside the horizon: mood stays assessed, each expiry counted on its own.
        var pawn = Pawn(.5) with { Mood = Pawn(.5).Mood! with { Memories = [
            NeedsFixtures.Memory("Insulted", -5, 5000d, false), NeedsFixtures.Memory("Insulted", -5, Day, false), NeedsFixtures.Memory("Chitchat", 0, 100d, false), NeedsFixtures.Memory("Chitchat", 0, 200d, false)] }};

        var result = ForecastNeeds.ForecastNeedsTimeline(pawn, Input(), [Meal(8000)]);

        Assert.Equal(5, result.ThoughtOffsetAtHorizon.Number());
        result.Mood!.Assessed();
        Assert.Contains("stacked_memory_expiry_additive", result.Mood!.Findings);
        Assert.Single(result.Findings, "stacked_memory_expiry_additive");
    }
}
