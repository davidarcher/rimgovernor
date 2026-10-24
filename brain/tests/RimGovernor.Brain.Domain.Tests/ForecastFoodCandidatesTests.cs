using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ForecastFoodCandidatesTests
{
    private static readonly RimGovernor.Brain.Domain.PawnId Pawn = new("worker");
    private static readonly Resource Raw = new("TestRaw");
    private static readonly Resource Meal = new("TestMeal");
    private static ColonyForecastRequest Frame(int skill = 5) => new()
    {
        Origin = new("test", 42), HorizonDays = 4000.0 / GameTime.TicksPerDay,
        Demand = new DerivedDemand { Work = new ForecastWorkInputs { Tick = new(42),
            IngredientReservations = Known(default(EquatableArray<IngredientReservation>)),
            Pawns = Known(EquatableArray.From(new[] { new WorkPawn { Id = Pawn, Available = Known(true),
                Skills = Known(EquatableArray.From(new[] { new WorkSkill { Name = "Cooking", Level = skill } })) } })) } },
        Facts = new ColonyForecastFacts { Stock = Known(default(EquatableArray<Amount>)),
            Items = new ItemFacts { Nutrition = new Dictionary<Resource, double> { [Raw] = 0.01, [Meal] = 0.1 } },
            Food = new FoodSupply { Complete = Known(true), Consumers = [new() { Id = Pawn, NutritionPerDay = Known(1.0) }],
                Stocks = [new() { Id = "existing", Holder = Known(default(RimGovernor.Brain.Domain.PawnId)), Nutrition = Known(1000.0 / GameTime.TicksPerDay),
                    Rot = new FoodRot.Keeps(), Eaters = [Pawn] }] } },
    };
    private static ForecastActivityProblem Capacity() => new()
    {
        Pawns = [new("worker", "home", Known(EquatableArray.From(new[] { new ForecastTimeWindow(0, 4000) })), Known(1.0))],
        Routes = [new("worker", "home", "plant", Known(10.0)), new("worker", "bench", "plant", Known(10.0))],
        Equipment = [new("bench", "bench", Known(EquatableArray.From(new[] { new ForecastTimeWindow(0, 4000) })))],
        Deadline = Known(4000.0), AlternativeLaborCost = Known(3000.0), ExpansionBudget = 4096,
    };
    private static ForecastFoodCandidateInputs Inputs() => new()
    {
        Tick = new(42), Destination = "home",
        Sources = Known(EquatableArray.From(new[] { new AcquisitionSource { Id = "plant", Definition = "TestPlant", Resource = Raw.Value, Food = true, Yield = 4, Growth = 1 } })),
        Crops = [new() { Name = "TestPlant", Harvests = Known(Raw), HarvestMinGrowth = Known(0.8), HarvestWork = Known(100.0) }],
        Benches = Known(EquatableArray.From(new[] { new ProductionBench { Id = "bench", Token = "bench-token", Usable = true,
            Recipes = [new() { Name = "TestRecipe", Role = RecipeRole.OrdinaryMeal, Available = Known(true),
                Food = new RecipeFood.Food(0, 0, new RecipeCooking.FromRaw(1, [], 2)) }] } })),
        Recipes = Known(EquatableArray.From(new[] { new ProductionRecipeInput("TestRecipe", EquatableArray.From(new[] {
            EquatableArray.From(new[] { new Amount(Raw, 2) }) }), false)
            { Products = EquatableArray.From(new[] { new Amount(Meal, 1) }), WorkAmount = 40.0 } })),
        Workers = [new("worker", Known(true), Known(true), Known(10.0), Known(10.0), Known(0.0))],
        Transfers = [new("plant", "home", "worker", Raw, Known(4.0), Known(10.0)),
            new("plant", "bench", "worker", Raw, Known(4.0), Known(10.0)), new("bench", "home", "worker", Meal, Known(2.0), Known(10.0))],
        EdibleAtDestination = new Dictionary<Resource, FoodStock> { [Raw] = new() { Holder = Known(default(RimGovernor.Brain.Domain.PawnId)), Rot = new FoodRot.Keeps(), Eaters = [Pawn] },
            [Meal] = new() { Holder = Known(default(RimGovernor.Brain.Domain.PawnId)), Rot = new FoodRot.Keeps(), Eaters = [Pawn] } },
    };
    private static ForecastActivityOutcomeComparison Compare(ColonyForecastRequest frame, ForecastFoodCandidateInputs input, ForecastActivityProblem? capacity = null)
    {
        var generated = ForecastFoodCandidates.GenerateFoodActivities(frame, capacity ?? Capacity(), input);
        Assert.NotNull(generated.Problem);
        return ForecastSurvivalNeeds.CompareSurvivalActivities(frame, generated.Problem, generated.Relief);
    }

    [Fact]
    public void ObservedHarvestAndRecipeGenerateUsefulCookingInsteadOfLocallyPinningRawFood()
    {
        var generated = ForecastFoodCandidates.GenerateFoodActivities(Frame(), Capacity(), Inputs());
        Assert.Equal(3, generated.Relief.Length);
        var result = ForecastSurvivalNeeds.CompareSurvivalActivities(Frame(), generated.Problem!, generated.Relief);
        Assert.Single(result.Selected);
        var binding = Assert.Single(generated.Bindings, b => b.Recipe == "TestRecipe" && b.Iterations == 1);
        Assert.Contains(result.Plan!.Allocations, a => a.Activity == binding.Activity);
        Assert.Equal(4, result.Plan.Allocations.Length); // harvest, supply, cook, final delivery
        var cook = generated.Problem!.Activities.Single(a => a.Id == binding.Activity);
        Assert.Equal(2, Assert.Single(cook.Inputs).Units.Value);
        Assert.Equal(1, Assert.Single(cook.Outputs).Units.Value);
    }

    [Fact]
    public void RawDeliveryWinsWhenCarryingCapacityAvoidsExtraTripsAndNoCookSkillPreventsCooking()
    {
        var input = Inputs();
        input = input with { Sources = Known(EquatableArray.From(input.Sources.Value.Select(s => s with { Yield = 10 }))) };
        // A small cooked batch is cheaper than three raw-food trips; carrying all raw food reverses that.
        Assert.Equal(4, Compare(Frame(), input).Plan!.Allocations.Length);
        input = input with { Transfers = input.Transfers.Select(t => t.Resource == Raw
            ? t with { UnitsPerTrip = Known(10.0) } : t).ToArray() };
        var raw = Compare(Frame(), input);
        Assert.Equal(2, raw.Plan!.Allocations.Length);
        Assert.Empty(Compare(Frame(0), Inputs()).Selected);
    }

    [Fact]
    public void RecipeProductCountsAndIngredientQuantitiesAreNotAssumedToBeOne()
    {
        var input = Inputs();
        input = input with { Recipes = Known(EquatableArray.From(input.Recipes.Value.Select(r => r with
            { Products = EquatableArray.From(new[] { new Amount(Meal, 3) }) }))) };
        var generated = ForecastFoodCandidates.GenerateFoodActivities(Frame(), Capacity(), input);
        var cook = generated.Problem!.Activities.Single(a => generated.Bindings.Any(b => b.Activity == a.Id && b.Iterations == 2));
        Assert.Equal(6, Assert.Single(cook.Outputs).Units.Value);
    }

    [Fact]
    public void SmallBatchMeetsDeadlineWhenCookingTheWholeHarvestWouldBeTooLate()
    {
        var input = Inputs();
        input = input with { Recipes = Known(EquatableArray.From(input.Recipes.Value.Select(r => r with { WorkAmount = 6000.0 }))) };
        var generated = ForecastFoodCandidates.GenerateFoodActivities(Frame(), Capacity(), input);
        var result = ForecastSurvivalNeeds.CompareSurvivalActivities(Frame(), generated.Problem!, generated.Relief);
        var selected = Assert.Single(generated.Bindings, b => b.Recipe != "" && result.Plan!.Allocations.Any(a => a.Activity == b.Activity));
        Assert.Equal(1, selected.Iterations);
        Assert.True(result.Plan!.CompletedAt < 1000);
        // Reproduce the old maximum-only candidate set: no alternative covers the need in time.
        var large = generated.Bindings.Single(b => b.Iterations == 2);
        var maximumOnly = generated.Relief.Where(r => r.Completion == large.Activity.Replace("-cook", "-delivery") || r.Completion.EndsWith("-raw")).ToArray();
        Assert.Empty(ForecastSurvivalNeeds.CompareSurvivalActivities(Frame(), generated.Problem!, maximumOnly).Selected);
    }

    [Fact]
    public void LongerFoodHorizonRequiresMoreThanTheCheapestBatch()
    {
        var frame = Frame() with { HorizonDays = 9000.0 / GameTime.TicksPerDay };
        var generated = ForecastFoodCandidates.GenerateFoodActivities(frame, Capacity() with { Deadline = Known(9000.0) }, Inputs());
        var result = ForecastSurvivalNeeds.CompareSurvivalActivities(frame, generated.Problem!, generated.Relief);
        var selected = Assert.Single(generated.Bindings, b => b.Recipe != "" && result.Plan!.Allocations.Any(a => a.Activity == b.Activity));
        Assert.Equal(2, selected.Iterations);
        var small = generated.Bindings.Single(b => b.Iterations == 1);
        Assert.Empty(ForecastSurvivalNeeds.CompareSurvivalActivities(frame, generated.Problem!, generated.Relief.Where(r =>
            r.Completion == small.Activity.Replace("-cook", "-delivery")).ToArray()).Selected);
    }

    [Fact]
    public void BatchEnumerationSpendsBudgetAndDoesNotPretendOmittedSizesWereCompared()
    {
        var input = Inputs() with { GenerationBudget = 3 };
        var generated = ForecastFoodCandidates.GenerateFoodActivities(Frame(), Capacity(), input);
        Assert.True(generated.GenerationTruncated);
        Assert.Equal(3, generated.Generated); // source, recipe inspection, one batch alternative
        Assert.Single(generated.Bindings, b => b.Iterations == 1);
        Assert.DoesNotContain(generated.Bindings, b => b.Iterations > 1);
        Assert.Contains("food_generation_truncated", generated.Findings);
        var again = ForecastFoodCandidates.GenerateFoodActivities(Frame(), Capacity(), input);
        Assert.Equal(generated.Bindings, again.Bindings);
        Assert.Equal(generated.Relief, again.Relief);
        Assert.Equal(generated.Generated, again.Generated);
    }

    [Fact]
    public void OccupiedBenchAndMissingTransferEstimateRefuseRatherThanGuess()
    {
        Assert.Empty(Compare(Frame(), Inputs(), Capacity() with { Equipment = [] }).Selected);
        Assert.Empty(Compare(Frame(), Inputs() with { Transfers = [] }).Selected);
    }

    [Fact]
    public void ImmatureTakenAndUnknownSourcesDoNotCreateHarvestTasks()
    {
        foreach (var source in new[] { Inputs().Sources.Value[0] with { Growth = 0.2 }, Inputs().Sources.Value[0] with { Taken = true } })
            Assert.Null(ForecastFoodCandidates.GenerateFoodActivities(Frame(), Capacity(), Inputs() with { Sources = Known(EquatableArray.From(new[] { source })) }).Problem);
        var unknown = ForecastFoodCandidates.GenerateFoodActivities(Frame(), Capacity(), Inputs() with { Sources = default });
        Assert.Null(unknown.Problem);
        Assert.Contains("unknown_food_sources_or_roster", unknown.Findings);
    }

    [Fact]
    public void GenerationBudgetIsExplicitAndDeterministic()
    {
        var input = Inputs() with { GenerationBudget = 1 };
        var generated = ForecastFoodCandidates.GenerateFoodActivities(Frame(), Capacity(), input);
        Assert.True(generated.GenerationTruncated);
        Assert.Equal(1, generated.Generated);
        Assert.Single(generated.Relief);
        Assert.Contains("food_generation_truncated", generated.Findings);
        Assert.Throws<DomainException>(() => ForecastFoodCandidates.GenerateFoodActivities(Frame(), Capacity(), input with { Tick = new(43) }));
    }

    [Fact]
    public void CarryingCapacityChangesGeneratedWorkAndCanMissTheFoodDeadline()
    {
        var input = Inputs();
        var slow = input with { Transfers = input.Transfers.Select(t => t with { UnitsPerTrip = Known(0.01) }).ToArray() };
        Assert.Empty(Compare(Frame(), slow).Selected);
        var generated = ForecastFoodCandidates.GenerateFoodActivities(Frame(), Capacity(), slow);
        Assert.Contains(generated.Problem!.Options, o => o.WorkTicks.TryGetValue(out var ticks) && ticks >= 2000);
    }

    [Fact]
    public void MissingRecipeCountsDoNotInventMealsAndUnknownAvailabilityCannotHarvest()
    {
        var input = Inputs();
        var missing = input with { Recipes = Known(EquatableArray.From(input.Recipes.Value.Select(r => r with { Products = default }))) };
        var generated = ForecastFoodCandidates.GenerateFoodActivities(Frame(), Capacity(), missing);
        Assert.Single(generated.Relief);
        Assert.Contains("unsupported_or_unknown_food_recipe:TestRecipe", generated.Findings);
        var unusableBench = ForecastFoodCandidates.GenerateFoodActivities(Frame(), Capacity(), input with
            { Benches = Known(EquatableArray.From(input.Benches.Value.Select(b => b with { Usable = false }))) });
        Assert.DoesNotContain(unusableBench.Bindings, b => b.Recipe != "");
        var frame = Frame();
        frame = frame with { Demand = frame.Demand with { Work = frame.Demand.Work with
            { Pawns = Known(EquatableArray.From(frame.Demand.Work.Pawns.Value.Select(p => p with { Available = default }))) } } };
        var unavailable = ForecastFoodCandidates.GenerateFoodActivities(frame, Capacity(), input);
        Assert.Empty(unavailable.Problem!.Options);
        Assert.Contains("unknown_food_worker_availability:worker", unavailable.Findings);
        Assert.Empty(ForecastSurvivalNeeds.CompareSurvivalActivities(frame, unavailable.Problem, unavailable.Relief).Selected);
    }

    [Fact]
    public void RecipesRequiringAnotherIngredientAreNotSilentlyTreatedAsSingleCropMeals()
    {
        var input = Inputs();
        input = input with { Recipes = Known(EquatableArray.From(input.Recipes.Value.Select(r => r with
            { Slots = EquatableArray.From(new[] { r.Slots[0], EquatableArray.From(new[] { new Amount(new("Other"), 1) }) }) }))) };
        var generated = ForecastFoodCandidates.GenerateFoodActivities(Frame(), Capacity(), input);
        Assert.Single(generated.Relief);
        Assert.DoesNotContain(generated.Bindings, b => b.Recipe != "");
    }
}
