using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ForecastNeedsTests
{
    private static ForecastCapacityInput Input(double horizon = 1000) => new()
    {
        Pawn = new() { Id = new("pawn"), Available = Known(true), Applies = Known(true), Job = Known(new PawnJob()),
            Schedule = Known(EquatableArray.From(Enumerable.Repeat(PawnRules.ScheduleSleep, 24))) },
        Location = "start", HorizonTicks = horizon, LocalHour = Known(12.0), OpportunityCostPerTick = Known(1.0),
        CurrentJobRemainingTicks = Known(0.0), Occupancy = Known(default(EquatableArray<ForecastOccupancy>)),
        LocationAfterProtectedTime = Known("bed"), ProtectTimetable = false,
    };
    private static NeedsForecastPawn Pawn() => new()
    {
        Pawn = "pawn", State = "eligible", Awake = Known(true), SleepThreshold = Known(.75), WakeThreshold = Known(1d), SleepBlockedUntilTicks = Known(0d), Mood = Known(.5), MoodTarget = Known(.5), BreakThreshold = Known(.3),
        Needs = [new("rest", Known(.2), Known(.0005), Known(.28)),
            new("food", Known(.5), Known(.0001), Known(.3)),
            new("joy", Known(.4), Known(.0001), Known(.3))],
        Memories = Known<EquatableArray<NeedMemory>>([]),
        Targets = [new("bed", "rest", "", 1, Known(20.0), default, default, Known(.002)),
            new("rice", "food", "Rice", 10, Known(10.0), Known(20.0), Known(.2), default),
            new("pin", "joy", "", 1, Known(15.0), default, default, Known(.002))],
    };
    private static ForecastNeedChoice Sleep(double start = 0, double end = 400) => new("sleep", "pawn", "rest", "bed", "", 0,
        start, start + 20, end, (end - start - 20) * .002, []);

    [Theory]
    [InlineData(97.325204571088, 1088.325204571088)]
    [InlineData(30016.1164428265, 36127.1164428265)]
    [InlineData(56907.1164428265, 60000)]
    public void UnrelatedJoyTimingCannotChangeFoodOrRestConsequencesEvenByOneBit(double start, double end)
    {
        // The native portfolio selected materially worse rest trajectories because
        // irrelevant joy breakpoints changed earlier food metrics below log precision.
        var pawn = Pawn() with { Needs =
            [new("food", Known(.257004916667938), Known(.0000166666676249588), Known(.3)),
             new("rest", Known(.875153124332428), Known(.0000169642857142857), Known(.3)),
             new("joy", Known(.1), Known(.00000833333376795053), Known(.3))] };
        var input = Input(60000);
        var baseline = ForecastNeeds.ForecastNeedsTimeline(pawn, input, []);
        var joy = new ForecastNeedChoice("joy", "pawn", "joy", "pin", "", 0, start, start, end, .5, []);
        var serviced = ForecastNeeds.ForecastNeedsTimeline(pawn, input, [joy]);
        Assert.Equal(Assert.Single(baseline.Needs, n => n.Kind == "food"), Assert.Single(serviced.Needs, n => n.Kind == "food"));
        Assert.Equal(Assert.Single(baseline.Needs, n => n.Kind == "rest"), Assert.Single(serviced.Needs, n => n.Kind == "rest"));
        Assert.NotEqual(Assert.Single(baseline.Needs, n => n.Kind == "joy"), Assert.Single(serviced.Needs, n => n.Kind == "joy"));
    }

    [Fact]
    public void SleepAndItsNativeWakeBreakpointCannotPerturbUnchangedFoodTrajectory()
    {
        var pawn = Pawn() with { SleepWhileStarving = Known(false), Needs =
            [new("food", Known(.528273165225983), Known(.0000166666676249588), Known(.3)),
             new("rest", Known(.25), Known(.0000169642857142857), Known(.3)),
             new("joy", Known(.1), Known(.00000833333376795053), Known(.3))] };
        var input = Input(60000);
        var baseline = ForecastNeeds.ForecastNeedsTimeline(pawn, input, []);
        var sleep = Sleep(97.325204571088, 1280.2626);
        var rested = ForecastNeeds.ForecastNeedsTimeline(pawn, input, [sleep]);
        Assert.Equal(Assert.Single(baseline.Needs, n => n.Kind == "food"), Assert.Single(rested.Needs, n => n.Kind == "food"));
        Assert.NotEqual(Assert.Single(baseline.Needs, n => n.Kind == "rest"), Assert.Single(rested.Needs, n => n.Kind == "rest"));
        var joy = new ForecastNeedChoice("joy", "pawn", "joy", "pin", "", 0, 7096, 7096, 8184.32520457109, .5, []);
        var combined = ForecastNeeds.ForecastNeedsTimeline(pawn, input, [sleep, joy]);
        Assert.Equal(Assert.Single(rested.Needs, n => n.Kind == "food"), Assert.Single(combined.Needs, n => n.Kind == "food"));
        Assert.Equal(Assert.Single(rested.Needs, n => n.Kind == "rest"), Assert.Single(combined.Needs, n => n.Kind == "rest"));
    }

    [Fact]
    public void ATimelineWithoutMoodHasTheSameNeedsAndCapacity()
    {
        // #3467: the search's physiology timeline reads only capacity, so it skips the mood timeline.
        var pawn = Pawn() with { ConditionalThoughts = Known<EquatableArray<NeedConditionalThought>>([]), MoodUnclampedTarget = Known(.5), MoodIntervalTicks = Known(150d), LocalHour = Known(12.0) };
        var choices = new[] { Sleep(), new ForecastNeedChoice("meal", "pawn", "food", "rice", "Rice", 1, 500, 520, 600, .2, []) };
        var full = ForecastNeeds.ForecastNeedsTimeline(pawn, Input(), choices);
        var physical = ForecastNeeds.ForecastNeedsTimeline(pawn, Input(), choices, mood: false);
        Assert.NotNull(full.Mood);
        Assert.NotEmpty(full.Mood.Trajectory);
        Assert.Null(physical.Mood);
        Assert.Equal(full.Needs, physical.Needs);
        Assert.Equal(full.Capacity.Capacity, physical.Capacity.Capacity);
        Assert.Equal(full.UsefulFoodGain, physical.UsefulFoodGain);
        Assert.Empty(ForecastNeeds.ForecastNeedsTimeline(pawn, Input(), choices, moodTrajectory: false).Mood!.Trajectory);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(.5, 65, 14.75)]
    [InlineData(1, 140, 66)]
    public void SaturationCutsRemainDistinctAcrossRecoveryDecayAndRepeatedCalls(double threshold, double exposure, double depth)
    {
        var pawn = Pawn() with { Needs = [new("joy", Known(.2), Known(.01), Known(threshold))] };
        var recovery = new ForecastNeedChoice("joy", "pawn", "joy", "pin", "", 0, 0, 0, 100, 2, []);
        var first = ForecastNeeds.ForecastNeedsTimeline(pawn, Input(200), [recovery]);
        var metric = Assert.Single(first.Needs);
        Assert.Equal(0, metric.FinalLevel.Value, 8);
        Assert.Equal(66, metric.DeficitTicks.Value, 8);
        Assert.Equal(exposure, metric.ThresholdExposureTicks.Value, 8);
        Assert.Equal(depth, metric.ThresholdDepthTicks.Value, 8);
        Assert.Equal(1.2, metric.WastedGain.Value, 8);
        // A different branch's cuts and interrupted activity must not survive the call.
        ForecastNeeds.ForecastNeedsTimeline(pawn, Input(200), [recovery], new Dictionary<string, double> { [recovery.Id] = 20 });
        Assert.Equal(first.Needs, ForecastNeeds.ForecastNeedsTimeline(pawn, Input(200), [recovery]).Needs);
    }

    [Fact]
    public void OrdinaryReviewGeneratesFoodQuantityRestTimingAndRecreationBeforeSelection()
    {
        var facts = WorkResourceTestFacts.Create("pawn");
        facts = facts with { ForecastObserved = facts.ForecastObserved with
        { Needs = new() { Tick = 100, Pawns = [Pawn()] } } };
        var reviewed = WorkRules.ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([]));
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
        var tired = ForecastNeeds.ForecastNeedsTimeline(Pawn(), input, []);
        var rested = ForecastNeeds.ForecastNeedsTimeline(Pawn(), input, [Sleep()]);
        Assert.True(rested.Capacity.Capacity.Windows.Value.Sum(w => w.End - w.Start) > tired.Capacity.Capacity.Windows.Value.Sum(w => w.End - w.Start));
        Assert.Contains(rested.Capacity.Capacity.Windows.Value, w => w.Start == 400 && w.End == 1000);
        Assert.DoesNotContain(rested.Capacity.Capacity.Windows.Value, w => w.Start < 400);
        Assert.True(rested.Needs.Single(n => n.Kind == "rest").ThresholdExposureTicks.Value < tired.Needs.Single(n => n.Kind == "rest").ThresholdExposureTicks.Value);
        var problem = new ForecastActivityProblem
        {
            Activities = [new("production", "bed", "bed", [])], Pawns = [rested.Capacity.Capacity],
            Options = [new("production", "pawn", Known(500.0), Known(0.0))],
            Deadline = Known(1000.0), AlternativeLaborCost = Known(1000.0),
        };
        Assert.NotNull(ForecastActivities.CompareActivityPlans(problem).Selected);
        Assert.Null(ForecastActivities.CompareActivityPlans(problem with { Pawns = [tired.Capacity.Capacity] }).Selected);
        Assert.Throws<DomainException>(() => ForecastNeeds.ForecastNeedsTimeline(Pawn(), input with { Occupancy = Known<EquatableArray<ForecastOccupancy>>(
            [new("long_trip", "pawn", Known(0.0), Known(700.0), ForecastOccupancyState.Active)]) }, [Sleep()]));
    }

    [Fact]
    public void InterruptionCreditsOnlyElapsedRestAndCompletedFood()
    {
        var input = Input(); var sleep = Sleep();
        var full = ForecastNeeds.ForecastNeedsTimeline(Pawn(), input, [sleep]);
        var interrupted = ForecastNeeds.ForecastNeedsTimeline(Pawn(), input, [sleep], new Dictionary<string, double> { [sleep.Id] = 100 });
        Assert.True(full.Needs[0].FinalLevel.Value > interrupted.Needs[0].FinalLevel.Value);
        var eat = new ForecastNeedChoice("eat", "pawn", "food", "rice", "Rice", 3, 0, 10, 100, .6, []);
        var ate = ForecastNeeds.ForecastNeedsTimeline(Pawn(), input, [eat]);
        var stopped = ForecastNeeds.ForecastNeedsTimeline(Pawn(), input, [eat], new Dictionary<string, double> { [eat.Id] = 50 });
        Assert.True(ate.Needs.Single(n => n.Kind == "food").WastedGain.Value > 0);
        Assert.Equal(.4, stopped.Needs.Single(n => n.Kind == "food").FinalLevel.Value, 8);
    }

    [Fact]
    public void EqualCurrentMoodHasDifferentExpiringMemoryConsequencesAndMealEffects()
    {
        var a = Pawn() with { Memories = Known<EquatableArray<NeedMemory>>([new("happy", 10, Known(100.0), false)]) };
        var b = a with { Memories = Known<EquatableArray<NeedMemory>>([new("happy", 10, Known(2000.0), false)]) };
        Assert.Equal(.4, ForecastNeeds.ForecastNeedsTimeline(a, Input(), []).MemoryAdjustedMoodTarget.Value, 8);
        Assert.Equal(.5, ForecastNeeds.ForecastNeedsTimeline(b, Input(), []).MemoryAdjustedMoodTarget.Value, 8);
        var meal = new ForecastNeedChoice("eat", "pawn", "food", "fine", "FineMeal", 1, 0, 10, 100, .6,
            [new("fine_meal", 5, Known(2000.0), false)]);
        Assert.Equal(.55, ForecastNeeds.ForecastNeedsTimeline(b, Input(), [meal]).MemoryAdjustedMoodTarget.Value, 8);
    }

    [Fact]
    public void UnknownMechanicsNeverGiveFreeWorkAndMissingRoutesDoNotMakeInstantMeals()
    {
        var pawn = Pawn() with { Needs = [new("rest", Known(.2), default, Known(.28))] };
        Assert.False(ForecastNeeds.ForecastNeedsTimeline(pawn, Input(), []).Capacity.Capacity.Windows.IsKnown);
        var targets = Pawn() with { Targets = [Pawn().Targets[1] with { TravelTicks = default }] };
        var generated = ForecastNeeds.GenerateNeedsAlternatives(targets, ForecastCapacity.ForecastPawnCapacityOf(Input()), 1000);
        Assert.Empty(generated.Choices);
        Assert.Contains("unknown_need_target:rice", generated.Findings);
        Assert.False(ForecastNeeds.ForecastNeedsTimeline(Pawn() with { Memories = default }, Input(), []).MemoryAdjustedMoodTarget.IsKnown);
    }

    [Fact]
    public void LegacyScheduleDoesNotPreselectNeedTimingButExplicitObligationsRemainProtected()
    {
        Assert.Equal(0, ForecastCapacity.ForecastPawnCapacityOf(Input() with { ProtectTimetable = true }).Capacity.Windows.Value.Sum(w => w.End - w.Start));
        var generated = ForecastNeeds.GenerateNeedsAlternatives(Pawn(), ForecastCapacity.ForecastPawnCapacityOf(Input()), 1000);
        Assert.Contains(generated.Choices, c => c.Depart == 0);
        var protectedInput = Input() with { Occupancy = Known<EquatableArray<ForecastOccupancy>>(
            [new("player", "pawn", Known(0.0), Known(300.0), ForecastOccupancyState.Active)]) };
        var protectedChoices = ForecastNeeds.GenerateNeedsAlternatives(Pawn(), ForecastCapacity.ForecastPawnCapacityOf(protectedInput), 1000);
        Assert.All(protectedChoices.Choices, c => Assert.True(c.Depart >= 300));
        Assert.Empty(ForecastNeeds.GenerateNeedsAlternatives(Pawn() with { State = "player_work" }, ForecastCapacity.ForecastPawnCapacityOf(Input()), 1000).Choices);
    }

    [Fact]
    public void DistanceRecoveryAndEligibilityChangeFeasibleChoicesAndBudgetIsExplicit()
    {
        var input = Input();
        var fast = ForecastNeeds.GenerateNeedsAlternatives(Pawn(), ForecastCapacity.ForecastPawnCapacityOf(input), 1000);
        var slow = ForecastNeeds.GenerateNeedsAlternatives(Pawn() with { Targets = [Pawn().Targets[0] with { GainPerTick = Known(.00001) }] }, ForecastCapacity.ForecastPawnCapacityOf(input), 1000);
        Assert.Contains(fast.Choices, c => c.Kind == "rest" && c.Gain > .5);
        Assert.DoesNotContain(slow.Choices, c => c.Gain > .5);
        var far = ForecastNeeds.GenerateNeedsAlternatives(Pawn() with { Targets = [Pawn().Targets[1] with { TravelTicks = Known(999.0) }] }, ForecastCapacity.ForecastPawnCapacityOf(input), 1000);
        Assert.Empty(far.Choices);
        var bounded = ForecastNeeds.GenerateNeedsAlternatives(Pawn(), ForecastCapacity.ForecastPawnCapacityOf(input), 1000, budget: 2);
        Assert.Equal(2, bounded.Choices.Count); Assert.True(bounded.Truncated);
        Assert.Equal(bounded, ForecastNeeds.GenerateNeedsAlternatives(Pawn(), ForecastCapacity.ForecastPawnCapacityOf(input), 1000, budget: 2));
    }
    [Fact]
    public void ManyFoodTargetsAndCountsCannotStarveTheBedOfTheGenerationBudget()
    {
        // #3167: food sorts before rest, and meals plus a 16-count rice stack spent all 32 units before the bed was reached,
        // so a tired cook had no rest alternative (no_feasible_need_choice:rest) and kept cooking.
        var pawn = Pawn() with { Needs = [new("rest", Known(.06), Known(.0000169), Known(.3)), new("food", Known(.2), Known(.0000166), Known(.3))],
            Targets = [.. Enumerable.Range(0, 4).Select(i => new NeedTarget("meal" + i, "food", "MealSimple", 3, Known(40.0 + i), Known(300.0), Known(.9), default)),
                new("rice", "food", "RawRice", 75, Known(60.0), Known(300.0), Known(.05), default),
                new("bed", "rest", "", 1, Known(55.0), default, default, Known(.0000381))] };
        var generated = ForecastNeeds.GenerateNeedsAlternatives(pawn, ForecastCapacity.ForecastPawnCapacityOf(Input(60000)), 60000, budget: 32);
        Assert.Contains(generated.Choices, c => c.Kind == "rest" && c.Target == "bed");
        Assert.Contains(generated.Choices, c => c.Kind == "food");
        Assert.DoesNotContain("no_feasible_need_choice:rest", generated.Findings);
        Assert.True(generated.Truncated);
        Assert.Contains("needs_generation_truncated", generated.Findings);
        Assert.Contains("needs_generation_truncated:food", generated.Findings);
    }

    [Fact]
    public void UnknownOrRepeatedMealMemoriesDoNotBecomeKnownMoodAndPermanentIsExplicit()
    {
        var first = new ForecastNeedChoice("eat", "pawn", "food", "fine", "FineMeal", 1, 0, 10, 100, .2,
            [new("fine_meal", 5, Known(2000.0), false)]);
        var second = first with { Id = "eat2", Depart = 200, Start = 210, End = 300 };
        Assert.False(ForecastNeeds.ForecastNeedsTimeline(Pawn(), Input(), [first, second]).MemoryAdjustedMoodTarget.IsKnown);
        Assert.False(ForecastNeeds.ForecastNeedsTimeline(Pawn(), Input(), [first with { Thoughts = [new("unknown", 5, default, false)] }]).MemoryAdjustedMoodTarget.IsKnown);
        Assert.Equal(.5, ForecastNeeds.ForecastNeedsTimeline(Pawn() with { Memories = Known<EquatableArray<NeedMemory>>(
            [new("permanent", 10, default, false) { Permanent = true }]) }, Input(), []).MemoryAdjustedMoodTarget.Value, 8);
        Assert.Throws<DomainException>(() => ForecastNeeds.ForecastNeedsTimeline(Pawn(), Input() with
        { CurrentWork = new(new("pawn"), Known(true), default, Known(500.0)) }, [Sleep()]));
    }
    [Fact]
    public void ABlockedPawnKeepsItsNeedChoicesAndCapacityWhileAnUncontrolledOneHasNone()
    {
        // #3557: a voluntary-control blocker is a finding, not a capacity exclusion; the pawn is fed rather than held on standby.
        var blocked = Pawn() with { State = "unsupported_voluntary_state", VoluntaryControlBlockers = ["chemical_need:Chemical_Smokeleaf"] };
        var capacity = ForecastCapacity.ForecastPawnCapacityOf(Input());
        var generated = ForecastNeeds.GenerateNeedsAlternatives(blocked, capacity, 1000);
        Assert.Contains(generated.Choices, c => c.Kind == "food");
        Assert.DoesNotContain(generated.Findings, f => f.StartsWith("needs_state:", StringComparison.Ordinal));
        Assert.Equal(ForecastNeeds.ForecastNeedsTimeline(Pawn(), Input(), []).Capacity, ForecastNeeds.ForecastNeedsTimeline(blocked, Input(), []).Capacity);
        var downed = ForecastNeeds.GenerateNeedsAlternatives(Pawn() with { State = "downed" }, capacity, 1000);
        Assert.Empty(downed.Choices);
        Assert.Contains("needs_state:downed", downed.Findings);
    }

    [Fact]
    public void NativeNeedsWireRoundTripsKnownUnknownAndPermanentFacts()
    {
        var source = new NeedsForecastRead { Tick = 100, Pawns = [Pawn() with
        { VoluntaryControlBlockers = ["unsupported_psyfocus"], Memories = Known<EquatableArray<NeedMemory>>([new("permanent", 3, default, false) { Permanent = true }]) }] };
        var text = RimGovernor.Contracts.Wire.WireJson.Serialize(source);
        var copy = RimGovernor.Contracts.Wire.WireJson.Deserialize<NeedsForecastRead>(text);
        Assert.Equal(source, copy);
        var unknown = source with { Pawns = [Pawn() with { Memories = default, Mood = default }] };
        Assert.Equal(unknown, RimGovernor.Contracts.Wire.WireJson.Deserialize<NeedsForecastRead>(RimGovernor.Contracts.Wire.WireJson.Serialize(unknown)));
    }
}
