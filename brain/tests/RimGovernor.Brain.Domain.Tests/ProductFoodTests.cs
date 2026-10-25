using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// What a recipe product is worth as food (ProductFood, #3875): a product with no food kind is NotFood and adds no
/// nutrition, a food a colonist eats carries its per-unit nutrition into the bill rules, and one only an animal
/// eats (kibble) counts no eaten nutrition.
/// </summary>
public sealed class ProductFoodTests
{
    private static ProductionProduct Product(ProductFood food) => new() { Food = food };

    private static ProductionBench Bench(ProductionProduct product, int target) => ProductionBenchFixtures.Cooking("stove", "ElectricStove", "stove-token", true) with
    {
        Recipes = [new ProductionRecipe { Access = ProductionBenchFixtures.Recipe("Cook"), Food = new RecipeFood.NotFood(), Products = [product] }],
        Bills = [new ExistingProductionBill { Observed = BillWire.Bill("bill") with { Recipe = "Cook", RepeatMode = RimGovernor.Brain.GameFacts.Operations.RepeatMode.Target, TargetCount = target } }],
    };

    [Fact]
    public void NotFoodIsNoMealAndNoNutrition()
    {
        var product = Product(new ProductFood.NotFood(new() { DefName = "Product", Edible = false, Storable = 0 }));
        Assert.False(product.EatenNutrition(out var nutrition));
        Assert.Equal(0, nutrition);
        Assert.False(product.Meal());
        Assert.False(product.SurvivalMeal());
        Assert.False(product.SimpleMeal());
    }

    [Fact]
    public void EatenFoodCarriesItsNutrition()
    {
        var product = Product(new ProductFood.Food(new() { DefName = "Product", Edible = true, Storable = 10 }, FoodKind.MealSimple, 0.9, true, false));
        Assert.True(product.EatenNutrition(out var nutrition));
        Assert.Equal(0.9, nutrition);
        Assert.True(product.Meal());
        Assert.True(product.SimpleMeal());
        Assert.False(product.SurvivalMeal());
    }

    [Fact]
    public void ANeverRottingMealIsTheSurvivalMeal()
    {
        var product = Product(new ProductFood.Food(new() { DefName = "Product", Edible = true, Storable = 10 }, FoodKind.MealFine, 0.9, false, false));
        Assert.True(product.SurvivalMeal());
        Assert.False(product.SimpleMeal());
    }

    [Fact]
    public void AnimalFoodCountsNoEatenNutrition()
    {
        var product = Product(new ProductFood.Food(new() { DefName = "Product", Edible = false, Storable = 75 }, FoodKind.Kibble, 0.05, false, false));
        Assert.False(product.EatenNutrition(out _));
        Assert.False(product.Meal());
    }

    [Fact]
    public void OnlyEatenProductsReserveBillNutrition()
    {
        var meal = Bench(Product(new ProductFood.Food(new() { DefName = "Product", Edible = true, Storable = 10 }, FoodKind.MealSimple, 0.9, true, false)), 10);
        var none = Bench(Product(new ProductFood.NotFood(new() { DefName = "Product", Edible = false, Storable = 0 })), 10) with { Observed = ProductionBenchFixtures.Observed("smithy", "Smithy", "smithy-token", true) };
        var (total, ok) = ProductionRules.ReservedFoodNutrition([meal, none]);
        Assert.True(ok);
        Assert.Equal(9, total, 6);
    }

    [Fact]
    public void ABenchInNoRoomIsNeverSeparated()
    {
        var butcher = ProductionBenchFixtures.Butchering("spot", "ButcherSpot", "spot-token", true);
        Assert.Empty(ProductionRules.SeparatedButcherBenches([butcher]));
        Assert.False(ProductionRules.AllButchersColocated([butcher]));
        Assert.Equal(["spot"], ProductionRules.SeparatedButcherBenches([ProductionBenchFixtures.InRoom(butcher, "r1")]).Keys);
    }
}
