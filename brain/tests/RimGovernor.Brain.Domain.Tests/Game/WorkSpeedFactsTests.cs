using RimGovernor.Brain.Bridge;
using RimWorld;
using Verse;
using Verse.AI;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests.Game;

// #3825: the defs map a coupled task to its work type and speed stat, and price a unit of nutrition in work to grow and cook it.
public sealed class WorkSpeedFactsTests
{
    private static readonly ThingDef Rice = new() { defName = "RawRice", ingestible = new IngestibleProperties { foodType = FoodTypeFlags.VegetableOrFruit } };

    private static readonly ThingDef Meal = new() { defName = "MealSimple", ingestible = new IngestibleProperties { foodType = FoodTypeFlags.Meal, preferability = FoodPreferability.MealSimple } };

    internal static DefinitionCatalog Catalog()
    {
        var construction = new WorkTypeDef { defName = "Construction" };
        var plant = new ThingDef
        {
            defName = "Plant_Rice",
            plant = new PlantProperties { sowTags = ["Ground"], sowWork = 170, harvestWork = 200, harvestYield = 11, harvestedThingDef = Rice },
        };
        var ingredient = new IngredientCount();
        ingredient.SetBaseCount(0.5f);
        var cook = new RecipeDef
        {
            defName = "CookMealSimple", workAmount = 300, workSpeedStat = new StatDef { defName = "CookSpeed" },
            products = [new ThingDefCountClass(Meal, 1)], ingredients = [ingredient],
        };
        // A cooking recipe counts its ingredients by nutrition (RecipeDef.ingredientValueGetterClass, private in the def).
        typeof(RecipeDef).GetField("ingredientValueGetterClass", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(cook, typeof(IngredientValueGetter_Nutrition));
        var silver = new ThingDef { defName = "Silver" };
        var values = new Dictionary<(BuildableDef, ThingDef?, CatalogStat), DefStatTable.Value>();
        var subjects = new Dictionary<(BuildableDef, ThingDef?), DefStatTable.Cost>();
        foreach (var (thing, nutrition) in new[] { (silver, 0f), (Rice, 0.05f), (Meal, 0.9f), (plant, 0f) })
        {
            subjects[(thing, null)] = new([], null);
            values[(thing, null, CatalogStats.MarketValue)] = new(1f, true, null);
            values[(thing, null, CatalogStats.Nutrition)] = new(nutrition, nutrition > 0, null);
        }
        var stats = new DefStatTable(subjects, values,
            new Dictionary<BuildableDef, IReadOnlyList<string>>(),
            new HashSet<CatalogStat>(CatalogStats.All));
        return new DefinitionCatalog(
            [silver, new TerrainDef { defName = "Soil" }, Rice, Meal, plant, cook, construction,
                new WorkGiverDef { defName = "ConstructFinishFrames", workType = construction },
                new JobDef { defName = "FinishFrame", driverClass = typeof(JobDriver_ConstructFinishFrame) },
                new JobDef { defName = "Wait", driverClass = typeof(JobDriver_Wait) }],
            stats,
            new CatalogDerived("Silver", "Wort", 10f));
    }

    [Fact]
    public void TheDefsMapAGiverToItsWorkTypeAJobToItsDriversStatAndARecipeToItsOwn()
    {
        var work = Catalog().WorkSpeedFacts();
        Assert.Equal("Construction", work.GiverWork["ConstructFinishFrames"]);
        Assert.Equal("ConstructionSpeed", work.JobStat["FinishFrame"]);
        // A job whose driver applies no speed stat is absent, so the shortfall names it rather than guessing.
        Assert.False(work.JobStat.ContainsKey("Wait"));
        Assert.Equal("CookSpeed", work.RecipeStat["CookMealSimple"]);
        Assert.Equal("Hauling", work.Hauling);
        // The native read reads each pawn at every mapped driver's stat and every recipe's.
        var stats = DefinitionCatalog.WorkSpeedStatNames(Catalog().Rows<RecipeDef>().Values);
        Assert.Contains("CookSpeed", stats);
        Assert.Contains("ConstructionSpeed", stats);
    }

    [Fact]
    public void ANutritionCostsTheCheapestMealsCookingAndItsIngredientsGrowing()
    {
        var food = Catalog().FoodWork(new HashSet<string>(StringComparer.Ordinal));
        Assert.Equal("Plant_Rice", food.Crop);
        Assert.Equal((170 + 200) / (11 * 0.05), food.GrowTicksPerNutrition, 3);
        Assert.Equal("CookMealSimple", food.Meal);
        Assert.Equal(300 / 0.9, food.CookTicksPerNutrition, 3);
        Assert.Equal(0.5 / 0.9, food.IngredientPerNutrition, 6);
    }
}
