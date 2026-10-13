using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Go food_rules.go (the part the catalog reads): the food rules the game's code states over the defs: which defs a
// food policy can allow, the kind each is for diets and a meal's ingredients.

public sealed partial class DefinitionCatalog
{
    /// <summary>
    /// The defs a policy can allow: a nutrition-giving ingestible that is no drug and no corpse
    /// (<c>ThingDef.IsNutritionGivingIngestible</c>, <c>IsDrug</c>, <c>IsCorpse</c>; the nutrition is
    /// <c>IngestibleProperties.CachedNutrition</c>, the Nutrition stat of the def).
    /// </summary>
    public bool IsFood(string name) => IsFood(ThingRow(name));

    private bool IsFood(ThingDef row)
    {
        if (row.ingestible is not { } ingestible || ingestible.drugCategory != DrugCategory.None)
        {
            return false;
        }

        if (typeof(Corpse).IsAssignableFrom(row.thingClass))
        {
            return false;
        }

        var (nutrition, shown) = ShownStatValue(row.defName, string.Empty, StatNames.Nutrition);
        return shown && nutrition > 0;
    }

    /// <summary>
    /// The kind the def has for diets: meals by their preferability tier, then kibble, hay, meat by its source (human,
    /// insect, other), fungus, animal product, a vegetable or fruit food type, else other. Null for a def that is no food.
    /// </summary>
    public P.FoodKind? FoodKindOf(string name)
    {
        var row = ThingRow(name);
        return IsFood(row) ? FoodKindFor(row) : null;
    }

    private P.FoodKind FoodKindFor(ThingDef row)
    {
        var ingestible = row.ingestible;
        switch (ingestible.preferability)
        {
            case FoodPreferability.MealAwful:
                return P.FoodKind.MealAwful;
            case FoodPreferability.MealSimple:
                return P.FoodKind.MealSimple;
            case FoodPreferability.MealFine:
                return P.FoodKind.MealFine;
            case FoodPreferability.MealLavish:
                return P.FoodKind.MealLavish;
        }

        if (HasFoodFlag(row, FoodTypeFlags.Kibble))
        {
            return P.FoodKind.Kibble;
        }

        if (row.defName == HayResource.Value)
        {
            return P.FoodKind.Hay;
        }

        if (RawMeat(row.defName))
        {
            return MeatSourceKind(row);
        }

        if (HasAllFoodFlags(row, FoodTypeFlags.Fungus))
        {
            return P.FoodKind.Fungus;
        }

        if (HasAllFoodFlags(row, FoodTypeFlags.AnimalProduct))
        {
            return P.FoodKind.AnimalProduct;
        }

        return HasFoodFlag(row, FoodTypeFlags.VegetableOrFruit) ? P.FoodKind.Vegetable : P.FoodKind.Other;
    }

    // HasFoodFlag reports whether the ingestible's food type has any bit of flag.
    private static bool HasFoodFlag(ThingDef row, FoodTypeFlags flag) => row.ingestible is { } i && (i.foodType & flag) != 0;

    // HasAllFoodFlags is Enum.HasFlag: every bit of flag.
    private static bool HasAllFoodFlags(ThingDef row, FoodTypeFlags flag) => row.ingestible is { } i && (i.foodType & flag) == flag;

    // MeatSourceKind is FoodUtility.GetMeatSourceCategory of a raw meat: human when its source race is humanlike,
    // insect when the source's flesh is insectoid, else ordinary raw meat.
    private P.FoodKind MeatSourceKind(ThingDef row)
    {
        if (!HasAllFoodFlags(row, FoodTypeFlags.Meat))
        {
            return P.FoodKind.RawMeat;
        }

        var source = row.ingestible.sourceDef;
        if (source is null)
        {
            return P.FoodKind.RawMeat;
        }

        if (FindThing(source.defName)?.race is { } race && race.intelligence >= Intelligence.Humanlike)
        {
            return P.FoodKind.HumanMeat;
        }

        return RaceFlags(source.defName).Insect ? P.FoodKind.InsectMeat : P.FoodKind.RawMeat;
    }

    // MealIngredientsOf is FoodUtility.GetFoodKind of a meal def: meat for a raw meat or a meat or corpse food type, any
    // for an animal product, else the ingredients comp's noIngredientsFoodKind (non-meat without the comp).
    private P.MealIngredients MealIngredientsOf(ThingDef row)
    {
        if (RawMeat(row.defName) || HasFoodFlag(row, FoodTypeFlags.Meat) || HasFoodFlag(row, FoodTypeFlags.Corpse))
        {
            return P.MealIngredients.MeatOnly;
        }

        if (HasAllFoodFlags(row, FoodTypeFlags.AnimalProduct))
        {
            return P.MealIngredients.Any;
        }

        return row.GetCompProperties<CompProperties_Ingredients>()?.noIngredientsFoodKind switch
        {
            FoodKind.Meat => P.MealIngredients.MeatOnly,
            FoodKind.Any => P.MealIngredients.Any,
            _ => P.MealIngredients.NonMeat,
        };
    }

    /// <summary>Every food a policy can allow with its kind and, for a meal, its ingredients, sorted by name.</summary>
    public IReadOnlyList<P.Food> Foods() => _foods.Value;

    private List<P.Food> BuildFoods()
    {
        var foods = new List<P.Food>();
        foreach (var row in _thingDefs.Values.OrderBy(d => d.defName, StringComparer.Ordinal))
        {
            if (!IsFood(row))
            {
                continue;
            }

            var kind = FoodKindFor(row);
            var isMeal = kind is P.FoodKind.MealAwful or P.FoodKind.MealSimple or P.FoodKind.MealFine or P.FoodKind.MealLavish;
            foods.Add(new P.Food(row.defName, kind, isMeal ? MealIngredientsOf(row) : P.MealIngredients.None));
        }

        return foods;
    }
}
