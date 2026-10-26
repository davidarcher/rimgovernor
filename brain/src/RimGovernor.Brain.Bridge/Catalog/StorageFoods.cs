using RimWorld;
using Verse;

namespace RimGovernor.Brain.Bridge;

public sealed partial class DefinitionCatalog
{
    /// <summary>All meal food types, including installed content absent from an authored vanilla list.</summary>
    public static bool MealStorageFood(ThingDef row) => row.ingestible is { } food && food.foodType.HasFlag(FoodTypeFlags.Meal);

    /// <summary>Rot-capable foods and animal corpses, including insect corpses through their parent category.</summary>
    public static bool ColdStorageFood(ThingDef row)
    {
        if (!row.HasComp<CompRottable>()) return false;
        var categories = CategoriesWithin(row);
        return categories.Contains(nameof(ThingCategoryDefOf.Foods)) || categories.Contains(nameof(ThingCategoryDefOf.CorpsesAnimal));
    }
}
