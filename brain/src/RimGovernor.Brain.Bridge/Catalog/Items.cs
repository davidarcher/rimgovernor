using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Item facts the defs state. A
// frame carries what varies; what a def says about itself is read here from its ThingDef. A def without a row is a
// contract error, never a default.

public sealed partial class DefinitionCatalog
{
    // The ThingCategoryDefs a raw food ingredient sits within, in the order the ingredient class takes them.
    private const string CategoryMeatRaw = "MeatRaw";
    private const string CategoryPlantFoodRaw = "PlantFoodRaw";
    private const string CategoryAnimalProductRaw = "AnimalProductRaw";

    // PlantProperties.harvestTag of a tree (PlantProperties.IsTree compares it).
    private const string WoodHarvestTag = "Wood";

    /// <summary>Every book def (a def carrying CompProperties_Book) with what reading it does, by name: skill experience is a textbook, research a schematic, Anomaly research or a mental break a tome, otherwise a novel.</summary>
    public IReadOnlyList<P.Book> Books()
    {
        var books = new List<P.Book>();
        foreach (var row in _thingDefs.Values)
        {
            if (row.GetCompProperties<CompProperties_Book>() is not { } book)
            {
                continue;
            }

            bool tome = false, textbook = false, schematic = false;
            foreach (var doer in book.doers.Seq())
            {
                switch (doer)
                {
                    case BookOutcomeProperties_GainAnomalyResearch or BookOutcomeProperties_MentalBreak:
                        tome = true;
                        break;
                    case BookOutcomeProperties_GainSkillExp:
                        textbook = true;
                        break;
                    case BookOutcomeProperties_GainResearch:
                        schematic = true;
                        break;
                }
            }

            var kind = tome ? P.BookKind.Tome : textbook ? P.BookKind.Textbook : schematic ? P.BookKind.Schematic : P.BookKind.Novel;
            books.Add(new P.Book(row.defName, kind));
        }

        return [.. books.OrderBy(b => b.Def, StringComparer.Ordinal)];
    }

    /// <summary>The days a def's items last before they rot (its CompProperties_Rottable); null for a def that never rots.</summary>
    public double? RotDays(string name) =>
        ThingRow(name).GetCompProperties<CompProperties_Rottable>() is { } rot ? rot.daysToRotStart : null;

    /// <summary><c>IngestibleProperties.babiesCanIngest</c> of the def.</summary>
    public bool BabyEdible(string name) => ThingRow(name).ingestible is { babiesCanIngest: true };

    /// <summary>Whether the def's food type includes vegetable or fruit.</summary>
    public bool Vegetable(string name) => ThingRow(name).ingestible is { } i && i.foodType.HasFlag(FoodTypeFlags.VegetableOrFruit);

    /// <summary>
    /// The raw-ingredient class of a def: meat, vegetable or animal product by the first of MeatRaw, PlantFoodRaw and
    /// AnimalProductRaw that one of its thing categories sits within (the category itself or any parent of it); none for
    /// a def in none. Milk has the fluid food type and is still an animal product by its category.
    /// </summary>
    public RimGovernor.Brain.GameFacts.Observations.FoodIngredientClass RawFoodClass(string name) => RawFoodClass(ThingRow(name));

    /// <summary>The native ingredient class of an actual game definition, shared with the stock producer.</summary>
    public static RimGovernor.Brain.GameFacts.Observations.FoodIngredientClass RawFoodClass(ThingDef row)
    {
        var within = CategoriesWithin(row);
        if (within.Contains(CategoryMeatRaw))
        {
            return RimGovernor.Brain.GameFacts.Observations.FoodIngredientClass.Meat;
        }

        if (within.Contains(CategoryPlantFoodRaw))
        {
            return RimGovernor.Brain.GameFacts.Observations.FoodIngredientClass.Vegetable;
        }

        return within.Contains(CategoryAnimalProductRaw) ? RimGovernor.Brain.GameFacts.Observations.FoodIngredientClass.AnimalProduct : RimGovernor.Brain.GameFacts.Observations.FoodIngredientClass.None;
    }

    /// <summary>
    /// What a food def's defs say about buying it as a routine ingredient: the raw class by thing category, a meal food
    /// type as the prepared "any" class, whether it never rots, and whether it is a raw crop (the vegetable class). Null for
    /// a def that is neither raw ingredient nor meal, and for human meat. Nutrition is the caller's (a stat).
    /// </summary>
    public P.TradeFoodGood? TradeFood(string name)
    {
        var row = ThingRow(name);
        var rawClass = RawFoodClass(name);
        if (rawClass == RimGovernor.Brain.GameFacts.Observations.FoodIngredientClass.None && row.ingestible is { } meal && meal.foodType.HasFlag(FoodTypeFlags.Meal))
        {
            rawClass = RimGovernor.Brain.GameFacts.Observations.FoodIngredientClass.Any;
        }

        if (rawClass == RimGovernor.Brain.GameFacts.Observations.FoodIngredientClass.None)
        {
            return null;
        }

        if (rawClass == RimGovernor.Brain.GameFacts.Observations.FoodIngredientClass.Meat && row.ingestible?.sourceDef is { } source && ThingRow(source.defName).race?.intelligence >= Intelligence.Humanlike)
        {
            return null;
        }

        var perishable = RotDays(name) is not null;
        var (nutrition, shown) = ShownStatValue(name, string.Empty, CatalogStats.Nutrition);
        if (!shown || nutrition <= 0)
        {
            return null;
        }

        return new P.TradeFoodGood
        {
            Class = rawClass,
            Prepared = rawClass == RimGovernor.Brain.GameFacts.Observations.FoodIngredientClass.Any,
            NonPerishable = !perishable,
            Crop = rawClass == RimGovernor.Brain.GameFacts.Observations.FoodIngredientClass.Vegetable,
            Nutrition = nutrition,
        };
    }

    // CategoriesWithin is every ThingCategoryDef the def sits within: its own thing categories and all their parents.
    private static HashSet<string> CategoriesWithin(ThingDef row)
    {
        var within = new HashSet<string>(StringComparer.Ordinal);
        foreach (var category in row.thingCategories.Seq())
        {
            // A category already walked has its parents in within, which also ends a cycle.
            for (var walk = category; walk is not null && within.Add(walk.defName); walk = walk.parent)
            {
            }
        }

        return within;
    }

    /// <summary>Whether a corpse def is of a humanlike race (<see cref="CorpseRace"/>).</summary>
    public bool HumanlikeCorpse(string name) => ThingRow(CorpseRace(name)).race?.intelligence >= Intelligence.Humanlike;

    /// <summary>The race def of a corpse def: the def its ingestible names as its source.</summary>
    public string CorpseRace(string name) =>
        ThingRow(name).ingestible?.sourceDef?.defName ?? throw new CatalogException($"corpse def {name} names no source race");

    /// <summary>
    /// The work one butchering of a creature takes: the work amount (<see cref="RecipeWorkAmount"/>) of the butcher recipe
    /// (<see cref="RecipeRole.ButcherFlesh"/>), the first by defName. The core game always defines one; a catalog with
    /// none is an error.
    /// </summary>
    public double ButcherWork() =>
        Rows<RecipeDef>().Values.OrderBy(r => r.defName, StringComparer.Ordinal).FirstOrDefault(r => RecipeRoleOf(r.defName) == RecipeRole.ButcherFlesh) is { } butcher
            ? RecipeWorkAmount(butcher.defName)
            : throw new CatalogException("catalog has no flesh butchering recipe");

    /// <summary><c>ThingDef.IsMeat</c> of the def: an item in the MeatRaw category.</summary>
    public bool RawMeat(string name)
    {
        var row = ThingRow(name);
        return row.category == ThingCategory.Item && row.thingCategories.Seq().Any(c => c.defName == CategoryMeatRaw);
    }

    /// <summary><c>ThingDef.IsMedicine</c> of the def: a def with a MedicalPotency stat base.</summary>
    public bool Medicine(string name) => ThingRow(name).statBases.Seq().Any(s => s.stat.defName == StatNames.MedicalPotency);

    /// <summary>The item a mineable def yields when mined (its building.mineableThing), empty when the def has no row or yields nothing. A long-range scanner aimed at a mineable is aimed at that yield.</summary>
    public string MineableYield(string mineable) => FindThing(mineable)?.building?.mineableThing?.defName ?? string.Empty;

    /// <summary><c>PlantProperties.IsTree</c> of def's row: a wood-harvest tag, or forceIsTree. False for a def without plant properties.</summary>
    public bool PlantIsTree(string def) =>
        FindThing(def)?.plant is { } props && (props.harvestTag == WoodHarvestTag || props.IsTree);

    /// <summary><c>PlantProperties.diesToLight</c> of def's row (cave fungus).</summary>
    public bool PlantDiesToLight(string def) => FindThing(def)?.plant is { diesToLight: true };

    /// <summary>The glowRadius of def's CompProperties_Glower, null when the def has no glower comp.</summary>
    public float? GlowRadius(string def) =>
        FindThing(def)?.GetCompProperties<CompProperties_Glower>() is { } glower ? glower.glowRadius : null;
}
