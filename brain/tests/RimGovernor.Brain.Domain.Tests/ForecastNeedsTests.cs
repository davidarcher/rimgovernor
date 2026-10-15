using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ForecastNeedsTests
{
    private static ForecastCapacityInput Input(double horizon = 1000) => new()
    {
        Pawn = new() { Id = new("pawn"), Available = Known(true), Applies = Known(true), Job = Known(new PawnJob()),
            Schedule = Known(EquatableArray.From(Enumerable.Repeat(PolicyConstants.ScheduleSleep, 24))) },
        Location = "start", HorizonTicks = horizon, LocalHour = Known(12.0), OpportunityCostPerTick = Known(1.0),
        CurrentJobRemainingTicks = Known(0.0), Occupancy = Known(default(EquatableArray<ForecastOccupancy>)),
        LocationAfterProtectedTime = Known("bed"), ProtectTimetable = false,
    };
    private static NeedsForecastPawn Pawn() => new()
    {
        Pawn = "pawn", State = "eligible", Awake = Known(true), SleepThreshold = Known(.75), WakeThreshold = Known(1d), SleepBlockedUntilTicks = Known(0d), Mood = Known(.5), MoodTarget = Known(.5), BreakThreshold = Known(.3),
        Needs = [new("rest", Known(.2), Known(.0005), Known(.28), Known(1.0)),
            new("food", Known(.5), Known(.0001), Known(.3), Known(1.0)),
            new("joy", Known(.4), Known(.0001), Known(.3), Known(1.0))],
        Memories = Known<EquatableArray<NeedMemory>>([]),
        Targets = [new("bed", "rest", "", 1, Known(20.0), default, default, Known(.002)),
            new("rice", "food", "Rice", 10, Known(10.0), Known(20.0), Known(.2), default),
            new("pin", "joy", "", 1, Known(15.0), default, default, Known(.002))],
    };
    private static ForecastNeedChoice Sleep(double start = 0, double end = 400) => new("sleep", "pawn", "rest", "bed", "", 0,
        start, start + 20, end, (end - start - 20) * .002, []);

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(.5, 65, 14.75)]
    [InlineData(1, 140, 66)]
    public void SaturationCutsRemainDistinctAcrossRecoveryDecayAndRepeatedCalls(double threshold, double exposure, double depth)
    {
        var pawn = Pawn() with { Needs = [new("joy", Known(.2), Known(.01), Known(threshold), Known(1d))] };
        var recovery = new ForecastNeedChoice("joy", "pawn", "joy", "pin", "", 0, 0, 0, 100, 2, []);
        var first = ForecastNeedsTimeline(pawn, Input(200), [recovery]);
        var metric = Assert.Single(first.Needs);
        Assert.Equal(0, metric.FinalLevel.Value, 8);
        Assert.Equal(66, metric.DeficitTicks.Value, 8);
        Assert.Equal(exposure, metric.ThresholdExposureTicks.Value, 8);
        Assert.Equal(depth, metric.ThresholdDepthTicks.Value, 8);
        Assert.Equal(1.2, metric.WastedGain.Value, 8);
        // A different branch's cuts and interrupted activity must not survive the call.
        ForecastNeedsTimeline(pawn, Input(200), [recovery], new Dictionary<string, double> { [recovery.Id] = 20 });
        Assert.Equal(first.Needs, ForecastNeedsTimeline(pawn, Input(200), [recovery]).Needs);
    }

    [Fact]
    public void OrdinaryReviewGeneratesFoodQuantityRestTimingAndRecreationBeforeSelection()
    {
        var facts = WorkResourceTestFacts.Create("pawn");
        facts = facts with { ForecastObserved = facts.ForecastObserved with
        { Needs = new() { Tick = 100, Pawns = [Pawn()] } } };
        var reviewed = ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([]));
        var choices = Assert.IsType<ForecastNeedsReview>(reviewed.NeedsAlternatives).Alternatives["pawn"].Choices;
        Assert.Contains(choices, c => c.Kind == "food" && c.Count == 1);
        Assert.Contains(choices, c => c.Kind == "food" && c.Count == 2);
        Assert.Contains(choices, c => c.Kind == "rest" && c.Depart == 0);
        Assert.Contains(choices, c => c.Kind == "rest" && c.Depart > 0);
        Assert.Contains(choices, c => c.Kind == "joy");
        Assert.True(choices.Where(c => c.Kind == "rest").Select(c => c.End - c.Start).Distinct().Count() > 1);
    }

    [Fact]
    public void RestBeforeWorkRestoresLaterFeasibilityAndDoesNotOverlapTravel()
    {
        var input = Input();
        var tired = ForecastNeedsTimeline(Pawn(), input, []);
        var rested = ForecastNeedsTimeline(Pawn(), input, [Sleep()]);
        Assert.True(rested.Capacity.FreeTicks.Value > tired.Capacity.FreeTicks.Value);
        Assert.Contains(rested.Capacity.Capacity.Windows.Value, w => w.Start == 400 && w.End == 1000);
        Assert.DoesNotContain(rested.Capacity.Capacity.Windows.Value, w => w.Start < 400);
        Assert.True(rested.Needs.Single(n => n.Kind == "rest").ThresholdExposureTicks.Value < tired.Needs.Single(n => n.Kind == "rest").ThresholdExposureTicks.Value);
        var problem = new ForecastActivityProblem
        {
            Activities = [new("production", "bed", "bed", [])], Pawns = [rested.Capacity.Capacity],
            Options = [new("production", "pawn", Known(500.0), Known(0.0))],
            Deadline = Known(1000.0), AlternativeLaborCost = Known(1000.0),
        };
        Assert.NotNull(CompareActivityPlans(problem).Selected);
        Assert.Null(CompareActivityPlans(problem with { Pawns = [tired.Capacity.Capacity] }).Selected);
        Assert.Throws<DomainException>(() => ForecastNeedsTimeline(Pawn(), input with { Occupancy = Known<EquatableArray<ForecastOccupancy>>(
            [new("long_trip", "pawn", Known(0.0), Known(700.0), ForecastOccupancyState.Active)]) }, [Sleep()]));
    }

    [Fact]
    public void InterruptionCreditsOnlyElapsedRestAndCompletedFood()
    {
        var input = Input(); var sleep = Sleep();
        var full = ForecastNeedsTimeline(Pawn(), input, [sleep]);
        var interrupted = ForecastNeedsTimeline(Pawn(), input, [sleep], new Dictionary<string, double> { [sleep.Id] = 100 });
        Assert.True(full.Needs[0].FinalLevel.Value > interrupted.Needs[0].FinalLevel.Value);
        var eat = new ForecastNeedChoice("eat", "pawn", "food", "rice", "Rice", 3, 0, 10, 100, .6, []);
        var ate = ForecastNeedsTimeline(Pawn(), input, [eat]);
        var stopped = ForecastNeedsTimeline(Pawn(), input, [eat], new Dictionary<string, double> { [eat.Id] = 50 });
        Assert.True(ate.Needs.Single(n => n.Kind == "food").WastedGain.Value > 0);
        Assert.Equal(.4, stopped.Needs.Single(n => n.Kind == "food").FinalLevel.Value, 8);
    }

    [Fact]
    public void EqualCurrentMoodHasDifferentExpiringMemoryConsequencesAndMealEffects()
    {
        var a = Pawn() with { Memories = Known<EquatableArray<NeedMemory>>([new("happy", 10, Known(100.0), false)]) };
        var b = a with { Memories = Known<EquatableArray<NeedMemory>>([new("happy", 10, Known(2000.0), false)]) };
        Assert.Equal(.4, ForecastNeedsTimeline(a, Input(), []).MemoryAdjustedMoodTarget.Value, 8);
        Assert.Equal(.5, ForecastNeedsTimeline(b, Input(), []).MemoryAdjustedMoodTarget.Value, 8);
        var meal = new ForecastNeedChoice("eat", "pawn", "food", "fine", "FineMeal", 1, 0, 10, 100, .6,
            [new("fine_meal", 5, Known(2000.0), false)]);
        Assert.Equal(.55, ForecastNeedsTimeline(b, Input(), [meal]).MemoryAdjustedMoodTarget.Value, 8);
    }

    [Fact]
    public void UnknownMechanicsNeverGiveFreeWorkAndMissingRoutesDoNotMakeInstantMeals()
    {
        var pawn = Pawn() with { Needs = [new("rest", Known(.2), default, Known(.28), Known(1.0))] };
        Assert.False(ForecastNeedsTimeline(pawn, Input(), []).Capacity.FreeTicks.IsKnown);
        var targets = Pawn() with { Targets = [Pawn().Targets[1] with { TravelTicks = default }] };
        var generated = GenerateNeedsAlternatives(targets, ForecastPawnCapacityOf(Input()), 1000);
        Assert.Empty(generated.Choices);
        Assert.Contains("unknown_need_target:rice", generated.Findings);
        Assert.False(ForecastNeedsTimeline(Pawn() with { Memories = default }, Input(), []).MemoryAdjustedMoodTarget.IsKnown);
    }

    [Fact]
    public void LegacyScheduleDoesNotPreselectNeedTimingButExplicitObligationsRemainProtected()
    {
        Assert.Equal(0, ForecastPawnCapacityOf(Input() with { ProtectTimetable = true }).FreeTicks.Value);
        var generated = GenerateNeedsAlternatives(Pawn(), ForecastPawnCapacityOf(Input()), 1000);
        Assert.Contains(generated.Choices, c => c.Depart == 0);
        var protectedInput = Input() with { Occupancy = Known<EquatableArray<ForecastOccupancy>>(
            [new("player", "pawn", Known(0.0), Known(300.0), ForecastOccupancyState.Active)]) };
        var protectedChoices = GenerateNeedsAlternatives(Pawn(), ForecastPawnCapacityOf(protectedInput), 1000);
        Assert.All(protectedChoices.Choices, c => Assert.True(c.Depart >= 300));
        Assert.Empty(GenerateNeedsAlternatives(Pawn() with { State = "player_work" }, ForecastPawnCapacityOf(Input()), 1000).Choices);
    }

    [Fact]
    public void DistanceRecoveryAndEligibilityChangeFeasibleChoicesAndBudgetIsExplicit()
    {
        var input = Input();
        var fast = GenerateNeedsAlternatives(Pawn(), ForecastPawnCapacityOf(input), 1000);
        var slow = GenerateNeedsAlternatives(Pawn() with { Targets = [Pawn().Targets[0] with { GainPerTick = Known(.00001) }] }, ForecastPawnCapacityOf(input), 1000);
        Assert.Contains(fast.Choices, c => c.Kind == "rest" && c.Gain > .5);
        Assert.DoesNotContain(slow.Choices, c => c.Gain > .5);
        var far = GenerateNeedsAlternatives(Pawn() with { Targets = [Pawn().Targets[1] with { TravelTicks = Known(999.0) }] }, ForecastPawnCapacityOf(input), 1000);
        Assert.Empty(far.Choices);
        var bounded = GenerateNeedsAlternatives(Pawn(), ForecastPawnCapacityOf(input), 1000, budget: 2);
        Assert.Equal(2, bounded.Choices.Count); Assert.True(bounded.Truncated);
        Assert.Equal(bounded, GenerateNeedsAlternatives(Pawn(), ForecastPawnCapacityOf(input), 1000, budget: 2));
    }
    [Fact]
    public void UnknownOrRepeatedMealMemoriesDoNotBecomeKnownMoodAndPermanentIsExplicit()
    {
        var first = new ForecastNeedChoice("eat", "pawn", "food", "fine", "FineMeal", 1, 0, 10, 100, .2,
            [new("fine_meal", 5, Known(2000.0), false)]);
        var second = first with { Id = "eat2", Depart = 200, Start = 210, End = 300 };
        Assert.False(ForecastNeedsTimeline(Pawn(), Input(), [first, second]).MemoryAdjustedMoodTarget.IsKnown);
        Assert.False(ForecastNeedsTimeline(Pawn(), Input(), [first with { Thoughts = [new("unknown", 5, default, false)] }]).MemoryAdjustedMoodTarget.IsKnown);
        Assert.Equal(.5, ForecastNeedsTimeline(Pawn() with { Memories = Known<EquatableArray<NeedMemory>>(
            [new("permanent", 10, default, false) { Permanent = true }]) }, Input(), []).MemoryAdjustedMoodTarget.Value, 8);
        Assert.Throws<DomainException>(() => ForecastNeedsTimeline(Pawn(), Input() with
        { CurrentWork = new(new("pawn"), Known(true), default, Known(500.0)) }, [Sleep()]));
    }
    [Fact]
    public void NativeNeedsWireRoundTripsKnownUnknownAndPermanentFacts()
    {
        var source = new NeedsForecastRead { Tick = 100, Pawns = [Pawn() with
        { Memories = Known<EquatableArray<NeedMemory>>([new("permanent", 3, default, false) { Permanent = true }]) }] };
        var text = RimGovernor.Contracts.Wire.WireJson.Serialize(source);
        var copy = RimGovernor.Contracts.Wire.WireJson.Deserialize<NeedsForecastRead>(text);
        Assert.Equal(source, copy);
        var unknown = source with { Pawns = [Pawn() with { Memories = default, Mood = default }] };
        Assert.Equal(unknown, RimGovernor.Contracts.Wire.WireJson.Deserialize<NeedsForecastRead>(RimGovernor.Contracts.Wire.WireJson.Serialize(unknown)));
    }
}
