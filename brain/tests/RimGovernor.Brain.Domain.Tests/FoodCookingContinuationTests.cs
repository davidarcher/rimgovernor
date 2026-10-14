using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using System.Text.Json;
using RimGovernor.Brain.Store.Codec;
using RimGovernor.Brain.Bridge.Contracts;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class FoodCookingContinuationTests
{
    private static FoodActivityOption Raw(string target = "raw") => new("pawn", target, "Rice", new(3, 4), new(8, 9), "HarvestGiver", 10, 20, 0)
        { NutritionPerUnit = Known(0.05) };
    private static FoodActivityOption Cook(string target = "raw", bool supplied = false, double work = 10) => Raw(target) with
    {
        Destination = new(5, 6), Cooking = new(new("bench", "bill", "recipe", "cook-giver", new(5, 5), "Meal", 1, new(8, 9)),
            10, work, 5, Known(0.9), supplied)
    };
    private static FoodActivityRead Read(params FoodActivityOption[] options) => new() { Known = true, Context = new() { Tick = 100 }, Options = EquatableArray.From(options) };
    private static PawnActivities Harvest() => StartFoodActivity(Read(Raw("crop")), 1, "method", 500, out _)!;
    private static FoodActivityRead Done(PawnActivities batch, string state, params FoodActivityOption[] options) => Read(options) with
        { Execution = new("method/pawn", "method", batch.Orders[0].Revision, 0, state, "", null, 100) };

    [Fact]
    public void RawAndCookingCompeteBeforeRecipeAndIngredientsAreBound()
    {
        var harvest = Harvest();
        var cooked = ContinueFoodActivity(harvest, Known(true), Done(harvest, "completed", Raw(), Cook()), out var comparison)!;
        Assert.NotNull(cooked.Orders[0].OutputDelivery!.Cooking);
        Assert.Equal(PawnActivityKind.Deliver, cooked.Orders[0].Steps[0].Kind);
        Assert.Equal(10, cooked.Orders[0].Steps[0].Count);
        Assert.Equal(3, comparison!.Plan!.Allocations.Length);
        var otherRecipe = Cook() with { Cooking = Cook().Cooking! with { ProductNutrition = Known(2.0),
            Continuation = Cook().Cooking!.Continuation with { Bill = "other-bill", Recipe = "other-recipe" } } };
        var recipeChoice = ContinueFoodActivity(harvest, Known(true), Done(harvest, "completed", Raw(), Cook(), otherRecipe), out _)!;
        Assert.Equal("other-recipe", recipeChoice.Orders[0].OutputDelivery!.Cooking!.Recipe);
        foreach (var expensive in new[] { Cook(work: 200), Cook() with { TravelTicks = 250 }, Cook() with { Count = 9 } })
        {
            var raw = ContinueFoodActivity(harvest, Known(true), Done(harvest, "completed", Raw(), expensive), out _)!;
            Assert.Null(raw.Orders[0].OutputDelivery!.Cooking);
        }
        var unknown = Cook() with { Cooking = Cook().Cooking! with { ProductNutrition = default } };
        Assert.Null(ContinueFoodActivity(harvest, Known(true), Done(harvest, "completed", Raw(), unknown), out _)!.Orders[0].OutputDelivery!.Cooking);
    }

    [Fact]
    public void SupplyRebindsChangedStackThenCookingRequiresObservedMealForDelivery()
    {
        var harvest = Harvest();
        var supply = ContinueFoodActivity(harvest, Known(true), Done(harvest, "completed", Cook()), out _)!;
        var cooking = ContinueFoodActivity(supply, Known(true), Done(supply, "completed", Cook("changed-stack", true)), out _)!;
        foreach (var batch in new[] { supply, cooking })
        {
            BridgeFunctions.RegisterIntentKinds();
            var action = DomainAction.NewPawnActivitiesAction(new("food-action"), batch);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream)) Codecs.Default.Write(writer, action);
            using var json = JsonDocument.Parse(stream.ToArray());
            var decoded = Codecs.Default.Read<DomainAction>(json.RootElement);
            var intent = BridgeFunctions.IntentAction("food-action", decoded).Intent!.PawnActivities!.Batch!;
            Assert.Equal(batch.Orders[0].OutputDelivery, intent.Orders[0].OutputDelivery);
            Assert.Equal(batch.Orders[0].Steps, intent.Orders[0].Steps);
        }
        var step = Assert.Single(cooking.Orders[0].Steps);
        Assert.Equal(PawnActivityKind.Cook, step.Kind);
        Assert.Equal("changed-stack", Assert.Single(step.Cooking!.Ingredients).Thing);
        Assert.Equal("recipe", step.Cooking.Recipe);
        Assert.Null(cooking.Orders[0].OutputDelivery!.Cooking); // No automatic second iteration.
        Assert.Equal("Meal", cooking.Orders[0].OutputDelivery!.Resource);
        var meal = Raw("observed-meal") with { Resource = "Meal", Count = 1 };
        var delivered = ContinueFoodActivity(cooking, Known(true), Done(cooking, "completed", meal), out _)!;
        Assert.Equal("observed-meal", delivered.Orders[0].Steps[0].Target);
        Assert.Equal(500, delivered.Orders[0].Steps[0].Deadline);
        Assert.True(ContinueFoodActivity(cooking, Known(true), Done(cooking, "completed"), out _)!.Orders[0].Release);
        Assert.True(ContinueFoodActivity(delivered, Known(true), Done(delivered, "completed", meal), out _)!.Orders[0].Release);
    }

    [Fact]
    public void InterruptedCookingLostBenchAndRecoveredConcernReleaseWithoutReplayingConsumption()
    {
        var harvest = Harvest();
        var supply = ContinueFoodActivity(harvest, Known(true), Done(harvest, "completed", Cook()), out _)!;
        Assert.True(ContinueFoodActivity(supply, Known(true), Done(supply, "completed"), out _)!.Orders[0].Release);
        var differentBill = Cook("wrong-bill", true) with { Cooking = Cook("wrong-bill", true).Cooking! with
            { Continuation = Cook().Cooking!.Continuation with { Bill = "other-bill" } } };
        Assert.True(ContinueFoodActivity(supply, Known(true), Done(supply, "completed", differentBill), out _)!.Orders[0].Release);
        Assert.True(ContinueFoodActivity(supply, Known(true), Done(supply, "failed", Cook("leftover", true)), out _)!.Orders[0].Release);
        var cooking = ContinueFoodActivity(supply, Known(true), Done(supply, "completed", Cook("rebound", true)), out _)!;
        Assert.True(ContinueFoodActivity(cooking, Known(true), Done(cooking, "failed", Raw("existing-meal") with { Resource = "Meal" }), out _)!.Orders[0].Release);
        Assert.True(ContinueFoodActivity(cooking, Known(false), Done(cooking, "completed"), out _)!.Orders[0].Release);
        Assert.Null(ContinueFoodActivity(cooking, Known(true), Done(cooking, "running"), out _));
    }
}
