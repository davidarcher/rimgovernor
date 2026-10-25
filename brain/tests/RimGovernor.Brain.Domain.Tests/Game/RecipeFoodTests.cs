using RimGovernor.Brain.Bridge;
using RimWorld;
using Verse;
using Xunit;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Domain.Tests.Game;

// #3873: the catalog reads a recipe's food worth, ingredients and work as plain values. A recipe is food or not, cooked
// from raw classes or not; one no def can fill, or that no work giver serves at a bench, is not offered there.
public sealed class RecipeFoodTests
{
    private static readonly ThingCategoryDef PlantFoodRaw = new() { defName = "PlantFoodRaw" };
    private static readonly ThingDef Rice = new()
    {
        defName = "RawRice", ingestible = new IngestibleProperties { foodType = FoodTypeFlags.VegetableOrFruit }, thingCategories = [PlantFoodRaw],
    };
    private static readonly ThingDef Meal = new()
    {
        defName = "MealFine",
        ingestible = new IngestibleProperties
        {
            foodType = FoodTypeFlags.Meal, tasteThought = new ThoughtDef { defName = "AteFineMeal", stages = [new ThoughtStage { baseMoodEffect = 5 }] },
        },
    };
    private static readonly ThingDef Silver = new() { defName = "Silver" };
    private static readonly SkillDef Cooking = new() { defName = "Cooking" };
    private static readonly SkillDef Crafting = new() { defName = "Crafting" };
    private const string Stove = "Stove";

    private static DefinitionCatalog Catalog()
    {
        var rice = new ThingFilter();
        rice.SetAllow(Rice, true);
        var ingredient = new IngredientCount { filter = rice };
        ingredient.SetBaseCount(0.5f);
        var cook = new RecipeDef
        {
            defName = "CookMealFine", workAmount = 300, products = [new ThingDefCountClass(Meal, 1)], ingredients = [ingredient], fixedIngredientFilter = rice,
            workSkill = Cooking,
            skillRequirements = [new SkillRequirement { skill = Cooking, minLevel = 4 }, new SkillRequirement { skill = Crafting, minLevel = 9 }],
        };
        ByNutrition(cook);
        var plain = new RecipeDef { defName = "CookFromNothing", workAmount = 90, products = [new ThingDefCountClass(Meal, 1)] };
        var empty = new IngredientCount { filter = new ThingFilter() };
        empty.SetBaseCount(1);
        var unfillable = new RecipeDef { defName = "MakeFromNothing", workAmount = 10, products = [new ThingDefCountClass(Silver, 1)], ingredients = [empty] };
        var hidden = new RecipeDef { defName = "MakeSilver", workAmount = -1, products = [new ThingDefCountClass(Silver, 1)] };
        var stove = new ThingDef { defName = Stove, recipes = [cook, plain, unfillable, hidden] };
        var cooking = new WorkTypeDef { defName = "Cooking" };
        var values = new Dictionary<(BuildableDef, ThingDef?, CatalogStat), DefStatTable.Value>();
        var subjects = new Dictionary<(BuildableDef, ThingDef?), DefStatTable.Cost>();
        foreach (var (thing, nutrition) in new[] { (Silver, 0f), (Rice, 0.05f), (Meal, 0.9f), (stove, 0f) })
        {
            subjects[(thing, null)] = new([], null);
            values[(thing, null, CatalogStats.MarketValue)] = new(1f, true, null);
            values[(thing, null, CatalogStats.Nutrition)] = new(nutrition, nutrition > 0, null);
        }
        // The game reads a product's WorkToMake for a recipe with no amount of its own, though the stat is not shown.
        values[(Silver, null, CatalogStats.WorkToMake)] = new(1200f, false, null);
        var stats = new DefStatTable(subjects, values, new Dictionary<BuildableDef, IReadOnlyList<string>>(), new HashSet<CatalogStat>(CatalogStats.All));
        return new DefinitionCatalog(
            [Silver, new TerrainDef { defName = "Soil" }, Rice, Meal, PlantFoodRaw, stove, cook, plain, unfillable, hidden, cooking, Cooking, Crafting,
                new WorkGiverDef { defName = "DoBillsCook", workType = cooking, giverClass = typeof(WorkGiver_DoBill), fixedBillGiverDefs = [stove] }],
            stats,
            new CatalogDerived("Silver", "Wort", 10f));
    }

    // A cooking recipe counts its ingredients by nutrition (RecipeDef.ingredientValueGetterClass, private in the def).
    private static void ByNutrition(RecipeDef recipe) =>
        typeof(RecipeDef).GetField("ingredientValueGetterClass", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(recipe, typeof(IngredientValueGetter_Nutrition));

    [Fact]
    public void ARecipeCookedFromRawSlotsIsFoodWithItsMoodWorkEfficiencyClassesAndFloor()
    {
        var food = Assert.IsType<P.RecipeFood.Food>(Catalog().RecipeFood("CookMealFine"));
        Assert.Equal(5, food.Mood);
        Assert.Equal(300 / 0.9, food.WorkPerNutrition, 3);
        var raw = Assert.IsType<P.RecipeCooking.FromRaw>(food.Cooking);
        Assert.Equal(0.9 / 0.5, raw.NutrientEfficiency, 3);
        Assert.Equal([RimGovernor.Contracts.Wire.Observations.FoodIngredientClass.Vegetable], Assert.Single(raw.IngredientClasses).Alternatives);
        // Only the requirements on the recipe's own work skill set the cooking floor.
        Assert.Equal(4, raw.CookSkillFloor);
    }

    [Fact]
    public void AMealWithNoRawSlotsIsFoodNotCookedFromRawAndANonIngestibleIsNoFood()
    {
        var catalog = Catalog();
        var food = Assert.IsType<P.RecipeFood.Food>(catalog.RecipeFood("CookFromNothing"));
        Assert.IsType<P.RecipeCooking.NotFromRaw>(food.Cooking);
        Assert.Equal(90 / 0.9, food.WorkPerNutrition, 3);
        Assert.IsType<P.RecipeFood.NotFood>(catalog.RecipeFood("MakeSilver"));
    }

    [Fact]
    public void ARecipeNoDefFillsOrTheCatalogLacksIsNotFillableAndHasNoIngredientsToRead()
    {
        var catalog = Catalog();
        Assert.True(catalog.RecipeFillable("CookMealFine"));
        Assert.Equal(10, Assert.Single(Assert.Single(catalog.RecipeIngredients("CookMealFine"))).Count);
        Assert.False(catalog.RecipeFillable("MakeFromNothing"));
        Assert.False(catalog.RecipeFillable("NoSuchRecipe"));
        Assert.Throws<CatalogException>(() => catalog.RecipeIngredients("MakeFromNothing"));
    }

    [Fact]
    public void ARecipeIsWorkedOnlyWhereAGiverServesItsBenchAndItsWorkReadsTheHiddenWorkToMake()
    {
        var catalog = Catalog();
        Assert.True(catalog.RecipeWorkedAt("CookMealFine", Stove));
        Assert.False(catalog.RecipeWorkedAt("CookMealFine", "Campfire"));
        var work = Assert.Single(catalog.RecipeWork("CookMealFine", Stove));
        Assert.Equal((P.WorkType.Cooking, "Cooking", 9), (work.Work, work.Skill, work.Minimum));
        Assert.Throws<CatalogException>(() => catalog.RecipeWork("CookMealFine", "Campfire"));
        Assert.Equal(300, catalog.RecipeWorkAmount("CookMealFine"));
        Assert.Equal(1200, catalog.RecipeWorkAmount("MakeSilver"));
    }
}
