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
        LocalHour = Known(12d), Mood = Known(moodTarget), MoodTarget = Known(moodTarget), MoodUnclampedTarget = Known(moodTarget),
        BreakThreshold = Known(.35), MoodRisePerTick = Known(.12 / Hour), MoodFallPerTick = Known(.08 / Hour),
        MoodIntervalTicks = Known(150d), MoodFreezesWhileAsleep = Known(true),
        ConditionalThoughts = Known<EquatableArray<NeedConditionalThought>>([]),
        Needs = [new("rest", Known(1d), Known(0d), Known(.28)) { UnservedGainPerTick = Known(1 / 1500d) }, new("food", Known(.25), Known(Fall), Known(.3))],
    };

    private static ForecastCapacityInput Input(double horizon = H) => new()
    {
        Pawn = new() { Id = new("pawn"), Available = Known(true), Applies = Known(true), Job = Known(new PawnJob()),
            Schedule = Known(EquatableArray.From(Enumerable.Repeat(PawnRules.ScheduleSleep, 24))) },
        Location = "start", HorizonTicks = horizon, LocalHour = Known(12.0), OpportunityCostPerTick = Known(1.0),
        CurrentJobRemainingTicks = Known(0.0), Occupancy = Known(default(EquatableArray<ForecastOccupancy>)),
        LocationAfterProtectedTime = Known("bed"), ProtectTimetable = false,
    };

    private static ForecastNeedChoice Meal(double start) => new("eat@" + start, "pawn", "food", "meal", "MealSimple", 1, start, start, start + 1000, .9, []);

    private static ForecastNeedsResult Eats(NeedsForecastPawn pawn, double start) => ForecastNeeds.ForecastNeedsTimeline(pawn, Input(), [Meal(start)]);

    private static ForecastNeedMetric Joy(double exposure) => new("joy", Known(.5), Known(0d), Known(exposure), Known(0d)) { ZeroTicks = Known(0d), ThresholdDepthTicks = Known(0d) };

    private static ForecastCoupledOutcome Outcome(ForecastNeedsResult needs) =>
        new(new([], 0, 0), Known(true), new Dictionary<string, ForecastNeedsResult> { ["pawn"] = needs }, [])
        { Support = new() { FoodPoisonExposure = Known(0d) }, ThermalExposure = [new("pawn", H, 0, 0, 0)] };

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
        var pawn = Pawn(.5) with { Needs = [new("rest", Known(.3), Known(.01 / 1000), Known(.28))] };

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

        var eat = ForecastCoupledSelection.CoupledConsequences(Outcome(eatFirst), [], context, H, Outcome(eatFirst), null);
        var play = ForecastCoupledSelection.CoupledConsequences(Outcome(playFirst), [], context, H, Outcome(eatFirst), null);

        // Neither plan starves or exhausts the pawn: the floors and the physiological terms tie.
        Assert.Equal(0, eat.Value.UrgentZero.Excess);
        Assert.Equal(0, play.Value.UrgentZero.Excess);
        Assert.Equal(play.Value.Ledger.Hunger.Value, eat.Value.Ledger.Hunger.Value);
        Assert.Equal(play.Value.Ledger.Rest.Value, eat.Value.Ledger.Rest.Value);
        Assert.Equal(0, eat.Value.Ledger.MoodBreaks.Value);
        Assert.True(play.Value.Ledger.MoodBreaks.Value > new ForecastValuePolicy().TieResolution);
        Assert.True(play.Value.Ledger.Wellbeing.Value > 0);
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

        Assert.Equal(0, late.MoodBreaks.Value);
        Assert.True(late.Wellbeing.Value > 0);
        Assert.True(late.LossAgainst(ForecastValue.Of(early, context, H, early)).Value > 0);
    }

    // A pawn that ate without a table carries AteWithoutTable; its next meal renews it (stackLimit 1) rather than adding a second.
    private static NeedMemory WithoutTable(double remaining, int stackLimit = 1) => new("AteWithoutTable", -3, Known(remaining), false) { StackLimit = stackLimit };

    private static ForecastNeedChoice MealWith(double start, NeedMemory thought) => Meal(start) with { Id = "eat@" + start, Thoughts = [thought] };

    [Fact]
    public void AMealRenewsTheStackLimitOneMemoryItRepeats()
    {
        // Observed -3 with 30000 ticks left; the meal ending at 9000 renews it, so the horizon still holds one -3, not two.
        var pawn = Pawn(.5) with { Memories = Known<EquatableArray<NeedMemory>>([WithoutTable(30000)]) };

        var result = ForecastNeeds.ForecastNeedsTimeline(pawn, Input(), [MealWith(8000, WithoutTable(Day))]);

        Assert.Equal(0, result.ThoughtOffsetAtHorizon.Value);
        Assert.True(result.Mood!.BreakRisk.IsKnown);
        Assert.DoesNotContain("unsupported_memory_renewal_or_stacking", result.Mood.Findings);
    }

    [Fact]
    public void ASecondMealRenewsTheFirstMealsMemory()
    {
        var result = ForecastNeeds.ForecastNeedsTimeline(Pawn(.5), Input(), [MealWith(2000, WithoutTable(Day)), MealWith(12000, WithoutTable(Day))]);

        Assert.Equal(-3, result.ThoughtOffsetAtHorizon.Value);
        Assert.True(result.Mood!.BreakRisk.IsKnown);
    }

    [Fact]
    public void AMealSharingAStackingDefStaysUnassessed()
    {
        var pawn = Pawn(.5) with { Memories = Known<EquatableArray<NeedMemory>>([WithoutTable(30000, stackLimit: 3)]) };

        var result = ForecastNeeds.ForecastNeedsTimeline(pawn, Input(), [MealWith(8000, WithoutTable(Day, stackLimit: 3))]);

        Assert.False(result.ThoughtOffsetAtHorizon.IsKnown);
        Assert.Equal(["unsupported_memory_renewal_or_stacking"], result.Mood!.Findings);
    }

    [Fact]
    public void StackedObservedMemoriesExpireOneByOneAndNameTheApproximation()
    {
        // Two -5 memories of one def, one expiring inside the horizon: mood stays assessed, each expiry counted on its own.
        var pawn = Pawn(.5) with { Memories = Known<EquatableArray<NeedMemory>>([
            new("Insulted", -5, Known(5000d), false), new("Insulted", -5, Known(Day), false), new("Chitchat", 0, Known(100d), false), new("Chitchat", 0, Known(200d), false)]) };

        var result = ForecastNeeds.ForecastNeedsTimeline(pawn, Input(), [Meal(8000)]);

        Assert.Equal(5, result.ThoughtOffsetAtHorizon.Value);
        Assert.True(result.Mood!.BreakRisk.IsKnown);
        Assert.Contains("stacked_memory_expiry_additive", result.Mood.Findings);
        Assert.Single(result.Findings, "stacked_memory_expiry_additive");
    }
}
