using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ProductiveLearningTests
{
    private static readonly double[] ExpectedDurations = [100, 80, 80];
    internal static ProductiveSkillWork Work(double speed = 1, double learning = .1) => new(new()
    {
        Disabled = false, TrackerAllowsLearning = true,
        StoredLevel = 1, Aptitude = 0, Xp = 95d, XpSinceMidnight = 0d,
        DirectFactor = 1d, UnsaturatedFactor = 1d, SaturationThreshold = 1000d,
        SaturatedFactor = .2, LossThreshold = -100d, DecayInterval = 200,
        NextDecay = 200, ValidForTicks = 1000, NextMidnightReset = 1000,
        TrackerIntervalTicks = 1, TrackerPhase = 0, TrackerElapsedTicks = 0,
        Levels = [new(0, 100, 0), new(1, 100, 0), new(2, 1000, 0)], Practice = new SkillPractice.None(),
    }, new("Cooking", learning, false), 100d,
        [new(0, speed), new(1, speed), new(2, 1.25)], "native-test");

    internal static ForecastActivityProblem Meals(int count, double speed = 1, double learning = .1, double deadline = 950)
    {
        var tasks = Enumerable.Range(0, count).Select(i => new ForecastActivity("meal/" + i, "stove", "stove",
            i == 0 ? [] : ["meal/" + (i - 1)])).ToArray();
        return new()
        {
            Activities = tasks,
            Pawns = [new("experienced", "stove", Known<EquatableArray<ForecastTimeWindow>>([new(0, deadline)]), Known(1d)),
                new("developing", "stove", Known<EquatableArray<ForecastTimeWindow>>([new(0, deadline)]), Known(1d))],
            Options = tasks.SelectMany(t => new[] {
                new ForecastActivityOption(t.Id, "experienced", Known(91d), Known(0d)) { Equipment = ["bench"] },
                new ForecastActivityOption(t.Id, "developing", Known(100 / speed), Known(0d)) { Equipment = ["bench"], SkillWork = Work(speed, learning) }
            }).ToArray(),
            Equipment = [new("bench", "stove", Known<EquatableArray<ForecastTimeWindow>>([new(0, deadline)]))],
            Deadline = Known(deadline), AlternativeLaborCost = Known(10000d), ExpansionBudget = 4096
        };
    }

    [Fact]
    public void NoUnlockLearningChangesAllocationOnlyWhenLaterUsefulWorkRepaysImmediateCost()
    {
        var result = ForecastActivities.CompareActivityPlans(Meals(3)).Selected!;
        Assert.All(result.Allocations, a => Assert.Equal("developing", a.Pawn));
        Assert.Equal(ExpectedDurations, result.Allocations.Select(a => a.WorkTicks));
        Assert.Equal(260, result.LaborCost);
        // Identical native rate table, no learning: capability remains unchanged, expert wins.
        Assert.All(ForecastActivities.CompareActivityPlans(Meals(3, learning: 0)).Selected!.Allocations, a => Assert.Equal("experienced", a.Pawn));
        Assert.All(ForecastActivities.CompareActivityPlans(Meals(3, speed: .5)).Selected!.Allocations, a => Assert.Equal("experienced", a.Pawn));
        // A high terminal skill/XP receives no value without later demanded work.
        Assert.Equal("experienced", Assert.Single(ForecastActivities.CompareActivityPlans(Meals(1)).Selected!.Allocations).Pawn);
    }

    [Fact]
    public void ImprovedDurationsEnterEquipmentAvailabilityAndDeadlineBeforeOutput()
    {
        var problem = Meals(3, deadline: 265);
        var result = ForecastActivities.CompareActivityPlans(problem);
        Assert.NotNull(result.Selected);
        Assert.Equal(260, result.Selected!.CompletedAt);
        Assert.Equal(260, ForecastActivities.EquipmentClaimsOf(problem, result.Selected).Max(c => c.End.Value));
        Assert.Empty(ForecastActivities.CompareActivityPlans(Meals(3, learning: 0, deadline: 265)).Alternatives);
        Assert.Equal("experienced", ForecastActivities.CompareActivityPlans(Meals(1, deadline: 95)).Selected!.Allocations[0].Pawn);
    }

    [Fact]
    public void CompletionLearningCountsPhysicalWorkOnceAndInterruptionSuppliesNoCallback()
    {
        var plan = ForecastActivities.CompareActivityPlans(Meals(3)).Selected!;
        Assert.Equal(26, plan.Allocations.Last().Learning!.After.AppliedGain);
        var option = Meals(1).Options.Single(o => o.Pawn == "developing");
        var interrupted = plan.Allocations[0] with { Learning = null };
        Assert.Equal(100, ForecastProductiveWorkPolicy.ForecastProductiveWork(option with { Activity = "later" }, [interrupted], 100).WorkTicks.Value);
        Assert.Equal(80, ForecastProductiveWorkPolicy.ForecastProductiveWork(option with { Activity = "later" }, [plan.Allocations[0]], 100).WorkTicks.Value);
    }

    [Fact]
    public void DecayChangesRateWithinWorkAndExpiredOrMissingEvidenceCannotPromiseImprovement()
    {
        var option = Meals(1).Options.Single(o => o.Pawn == "developing");
        var work = Work() with { Learning = Work().Learning with { StoredLevel = 2, Xp = -95d,
            NextDecay = 10, Levels = [new(0, 100, 0), new(1, 100, 0), new(2, 1000, 10)] } };
        var duration = ForecastProductiveWorkPolicy.ForecastProductiveWork(option with { SkillWork = work }, [], 0);
        Assert.Equal(98, duration.WorkTicks.Value); // 10 * 1.25 then 88 * 1, native whole work ticks.
        Assert.False(ForecastProductiveWorkPolicy.ForecastProductiveWork(option, [], GameTime.TicksPerDay - 50).WorkTicks.IsKnown);
        Assert.False(ForecastProductiveWorkPolicy.ForecastProductiveWork(option with { SkillWork = Work() with { Rates = [] } }, [], 0).WorkTicks.IsKnown);
        Assert.False(ForecastProductiveWorkPolicy.ForecastProductiveWork(option with { SkillWork = Work() with { Learning = Work().Learning with { Xp = double.NaN } } }, [], 0).WorkTicks.IsKnown);
    }

    [Theory]
    [InlineData(.1, 1, "pawn")]
    [InlineData(0, 1, "helper")]
    [InlineData(.1, .5, "helper")]
    public void NormalFoodCompositionAndSharedSelectionValueLearningThroughLaterDemandedMeals(double learning, double speed, string chosen)
    {
        var facts = NormalFoodFacts(learning, speed);
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts, 700)!;
        var request = ForecastCoupled.CoupledForecastRequestOf(new("world/map/load", 100), 700d / GameTime.TicksPerDay,
            facts with { CoupledAlternatives = generated },  new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, [], new());
        var selection = ForecastCoupledSelection.CompareActivityOutcomes(request, generated);
        var batch = Assert.IsType<PawnActivities>(ForecastCoupledSelection.StartCoupledActivity(selection, generated, 1, "cooking", 100));
        var recipes = batch.Tasks.Where(t => t.Step.Kind == PawnActivityKind.Recipe).ToArray();
        Assert.True(recipes.Length == 2, $"recipes={recipes.Length}; selected={string.Join(';', selection.Selection.Selected)}; expansions={selection.Selection.Expansions}; findings={string.Join(';', selection.Selection.Findings)}; alternatives={generated.Alternatives.Count}");
        Assert.All(recipes, t => Assert.Equal(chosen, t.Pawn));
        Assert.All(recipes, t => Assert.True(generated.Context.Activities.Activities.Single(a => a.Id == t.Id).RequiresFoodUse));
        if (learning > 0 && speed == 1) Assert.Equal(2, selection.Selected.Plan.Allocations.Count(a => a.Learning is not null));
    }
    /// <summary>A doctor's Medicine mechanics with a surgery at the native 0.1 × 16 raw XP per recipe work tick (#3632).</summary>
    internal static ProductiveSkillWork Surgery(double workAmount = 150, double speed = .75) => new(Work().Learning,
        new("Medicine", 1.6, false), workAmount, [new(1, speed)], "native-test");
    internal static TendLearning Tend(double rawXp = 250) => new(Work().Learning, "Medicine", rawXp, "native-test");

    [Fact]
    public void SurgeryCallbackPaysForTheTicksSpentOnRecipeWork()
    {
        var option = new ForecastActivityOption("surgery", "doctor", Known(200d), Known(0d)) { SkillWork = Surgery() };
        var duration = ForecastProductiveWorkPolicy.ForecastProductiveWork(option, [], 0);
        // 150 work at 0.75 a tick is 200 ticks; native pays per tick spent (ticksSpentDoingRecipeWork), not per unit of work.
        Assert.Equal(200, duration.WorkTicks.Value);
        Assert.Equal(320, duration.Evidence!.Callbacks.Single().RawXp);
        Assert.Equal(SkillXpPath.NormalRecord, duration.Evidence.Callbacks.Single().Path);
        Assert.Equal("Medicine", duration.Evidence.Skill);
        // Only the current level's rate is read (#3633 adds the others): a level reached before the work has no rate.
        var promoted = option with { SkillWork = Surgery() with { Learning = Work().Learning with { Xp = 99.5d } } };
        Assert.False(ForecastProductiveWorkPolicy.ForecastProductiveWork(promoted, [new("tend", "doctor", 0, 0, 0, 10, 0, 0, 10, 10, 0)
            { Learning = new("Medicine", [new("tend", 10, 5d, SkillXpPath.NormalTracker)], Projection(), Projection(), 10, 10, "native-test") }], 10).WorkTicks.IsKnown);
    }

    [Fact]
    public void EachCompletedTendPaysOnceAtCompletionAndKeepsTheObservedDuration()
    {
        var option = new ForecastActivityOption("tend/1", "doctor", Known(400d), Known(0d)) { TendLearning = Tend() };
        var first = ForecastProductiveWorkPolicy.ForecastProductiveWork(option, [], 0);
        Assert.Equal(400, first.WorkTicks.Value);
        Assert.Equal(new SkillXpEvent("tend/1", 400, 250d, SkillXpPath.NormalTracker, BeforeDecay: true), first.Evidence!.Callbacks.Single());
        Assert.Equal(250, first.Evidence.After.AppliedGain);
        var allocation = new ForecastActivityAllocation("tend/1", "doctor", 0, 0, 0, 400, 0, 0, 400, 400, 0) { Learning = first.Evidence };
        var second = ForecastProductiveWorkPolicy.ForecastProductiveWork(option with { Activity = "tend/2" }, [allocation], 400);
        Assert.Equal(250, second.Evidence!.Before.AppliedGain);
        Assert.Equal(500, second.Evidence.After.AppliedGain);
        // Learning that cannot be projected never removes the treatment: the observed duration stays, without evidence.
        var expired = ForecastProductiveWorkPolicy.ForecastProductiveWork(option with { TendLearning = Tend() with
            { Learning = Work().Learning with { Xp = double.NaN } } }, [], 0);
        Assert.Equal(400, expired.WorkTicks.Value);
        Assert.Null(expired.Evidence);
    }

    [Fact]
    public void TheGameSurgeryWorkersAreQualifiedAndUnknownWorkersStayUnknown()
    {
        var medicine = new RimWorld.SkillDef { defName = "Medicine" };
        RecipeLearning? Observe(Type worker) => RimGovernor.Brain.Bridge.DefinitionCatalog.ObserveRecipeLearning(
            new Verse.RecipeDef { defName = worker.Name, workerClass = worker, workSkill = medicine, workSkillLearnFactor = 16 });
        foreach (var worker in new[] { typeof(Verse.RecipeWorker), typeof(RimWorld.Recipe_InstallArtificialBodyPart),
            typeof(RimWorld.Recipe_RemoveBodyPart), typeof(RimWorld.Recipe_Surgery) })
            Assert.Equal(1.6, Observe(worker)!.RawXpPerWorkTick);
        Assert.Null(Observe(typeof(ModSurgery)));
        Assert.Null(Observe(typeof(RimWorld.Recipe_ExecuteByCut)));
    }

    private sealed class ModSurgery : RimWorld.Recipe_Surgery;

    private static SkillProjection Projection() => new(1, 1, 0d, 0d, 0d, Work().Learning);

    internal static RoundsFacts NormalFoodFacts(double learning = .1, double speed = 1)
    {
        var novice = ForecastPortfolioChoiceTests.Conversion("rice", "Rice", "SimpleMeal", 100 / speed) with
            { AvailableInput = 8, Cooking = new FoodCooking.Cook(100 / speed, 0, Work(speed, learning)), CanSupply = false, Delivery = new FoodDelivery.CannotCarry() };
        var facts = ForecastPortfolioChoiceTests.Observed([novice, novice with { Pawn = "helper", Cooking = new FoodCooking.Cook(91d, 0, null), CanSupply = true, Delivery = new FoodDelivery.Carried(1d) }],
            [new("rice", "Rice", new(1, 1), 8, new FoodFreshness.Rots(2000d))], helper: true);
        var read = facts.ForecastObserved.Coupled!;
        var needs = facts.ForecastObserved.Needs!;
        facts = facts with { ForecastObserved = facts.ForecastObserved with
        {
            Needs = needs with { Pawns = EquatableArray.From(needs.Pawns.Select(p => p.Pawn == "pawn" ? p with
                { Needs = EquatableArray.From(p.Needs.Select(n => n.Kind == "food" ? n with { Level = .75, FallPerTick = .002, Threshold = .25 } : n)) } : p)) },
            Coupled = read with { FoodUses = EquatableArray.From(read.FoodUses.Select(u => u with { Nutrition = .5, FreshTicks = new FoodFreshness.Rots(2000d) })) }
        } };
        facts = WorkRules.ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([]));
        return facts;
    }
}
