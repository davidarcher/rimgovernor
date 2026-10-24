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
    private static ProductionProduct Product(ProductFood food) => new() { Name = "Product", Food = food };

    private static ProductionBench Bench(ProductionProduct product, int target) => new()
    {
        Id = "stove",
        Token = "stove-token",
        Usable = true,
        Recipes = [new ProductionRecipe { Name = "Cook", Food = new RecipeFood.NotFood(), Available = true, Products = [product] }],
        Bills = [new ExistingProductionBill { Id = "bill", Recipe = "Cook", Repeat = BillRepeat.TargetCount, TargetCount = target, Active = true, UnrestrictedWorker = true, Worker = "", DefaultIngredients = true, Ingredients = [] }],
    };

    [Fact]
    public void NotFoodIsNoMealAndNoNutrition()
    {
        var product = Product(new ProductFood.NotFood());
        Assert.False(product.EatenNutrition(out var nutrition));
        Assert.Equal(0, nutrition);
        Assert.False(product.Meal());
        Assert.False(product.SurvivalMeal());
        Assert.False(product.SimpleMeal());
    }

    [Fact]
    public void EatenFoodCarriesItsNutrition()
    {
        var product = Product(new ProductFood.Food(FoodKind.MealSimple, 0.9, true, true, false, 10));
        Assert.True(product.EatenNutrition(out var nutrition));
        Assert.Equal(0.9, nutrition);
        Assert.True(product.Meal());
        Assert.True(product.SimpleMeal());
        Assert.False(product.SurvivalMeal());
    }

    [Fact]
    public void ANeverRottingMealIsTheSurvivalMeal()
    {
        var product = Product(new ProductFood.Food(FoodKind.MealFine, 0.9, true, false, false, 10));
        Assert.True(product.SurvivalMeal());
        Assert.False(product.SimpleMeal());
    }

    [Fact]
    public void AnimalFoodCountsNoEatenNutrition()
    {
        var product = Product(new ProductFood.Food(FoodKind.Kibble, 0.05, false, false, false, 75));
        Assert.False(product.EatenNutrition(out _));
        Assert.False(product.Meal());
    }

    [Fact]
    public void OnlyEatenProductsReserveBillNutrition()
    {
        var meal = Bench(Product(new ProductFood.Food(FoodKind.MealSimple, 0.9, true, true, false, 10)), 10);
        var none = Bench(Product(new ProductFood.NotFood()), 10) with { Id = "smithy" };
        var (total, ok) = ProductionRules.ReservedFoodNutrition([meal, none]);
        Assert.True(ok);
        Assert.Equal(9, total, 6);
    }

    [Fact]
    public void ABenchInNoRoomIsNeverSeparated()
    {
        var butcher = new ProductionBench { Id = "spot", Token = "spot-token", Usable = true, Butcher = true, Room = null };
        Assert.Empty(ProductionRules.SeparatedButcherBenches([butcher]));
        Assert.False(ProductionRules.AllButchersColocated([butcher]));
        Assert.Equal(["spot"], ProductionRules.SeparatedButcherBenches([butcher with { Room = "r1" }]).Keys);
    }
}
