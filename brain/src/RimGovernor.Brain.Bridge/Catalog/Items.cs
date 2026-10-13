using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Go catalog_items.go, catalog_fuel.go and the plant and light rules of plant_rules.go: item facts the defs state. A
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
    public P.FoodIngredientClass RawFoodClass(string name)
    {
        var within = CategoriesWithin(ThingRow(name));
        if (within.Contains(CategoryMeatRaw))
        {
            return P.FoodIngredientClass.Meat;
        }

        if (within.Contains(CategoryPlantFoodRaw))
        {
            return P.FoodIngredientClass.Vegetable;
        }

        return within.Contains(CategoryAnimalProductRaw) ? P.FoodIngredientClass.AnimalProduct : P.FoodIngredientClass.None;
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
        if (rawClass == P.FoodIngredientClass.None && row.ingestible is { } meal && meal.foodType.HasFlag(FoodTypeFlags.Meal))
        {
            rawClass = P.FoodIngredientClass.Any;
        }

        if (rawClass == P.FoodIngredientClass.None)
        {
            return null;
        }

        if (rawClass == P.FoodIngredientClass.Meat && row.ingestible?.sourceDef is { } source && ThingRow(source.defName).race?.intelligence >= Intelligence.Humanlike)
        {
            return null;
        }

        var perishable = RotDays(name) is not null;
        var (nutrition, shown) = ShownStatValue(name, string.Empty, StatNames.Nutrition);
        if (!shown || nutrition <= 0)
        {
            return null;
        }

        return new P.TradeFoodGood
        {
            Class = rawClass,
            Prepared = rawClass == P.FoodIngredientClass.Any,
            NonPerishable = !perishable,
            Crop = rawClass == P.FoodIngredientClass.Vegetable,
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

    /// <summary>Whether a corpse def is of a humanlike race: the race def its ingestible names as its source.</summary>
    public bool HumanlikeCorpse(string name)
    {
        var source = ThingRow(name).ingestible?.sourceDef ?? throw new CatalogException($"corpse def {name} names no source race");
        return ThingRow(source.defName).race?.intelligence >= Intelligence.Humanlike;
    }

    /// <summary>Whether a power building shorts out in rain, from its power or battery comp.</summary>
    public bool RainVulnerable(string name) =>
        ThingRow(name).GetCompProperties<CompProperties_Power>() is { } power
            ? power.shortCircuitInRain
            : throw new CatalogException($"def {name} has no power comp");

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

    /// <summary>The burn the named def's CompProperties_Refuelable states: unknown for a def without the comp, so a building that cannot be refueled is not a fuel consumer.</summary>
    public Fact<P.FuelBurn> RefuelBurn(string name)
    {
        var row = ThingRow(name);
        if (row.GetCompProperties<CompProperties_Refuelable>() is not { } fuel)
        {
            return Unknown<P.FuelBurn>();
        }

        return Known(new P.FuelBurn
        {
            PerDay = fuel.fuelConsumptionRate,
            UnitsPerItem = GameMembers.FuelMultiplier(fuel),
            UseDriven = fuel.consumeFuelOnlyWhenUsed || fuel.externalTicking,
            InRain = fuel.fuelConsumptionPerTickInRain > 0,
            WhenPowered = fuel.consumeFuelOnlyWhenPowered,
            Flickable = HasComp<CompProperties_Flickable>(row),
            Difficulty = fuel.factorByDifficulty,
        });
    }

    /// <summary><c>PlantProperties.IsTree</c> of def's row: a wood-harvest tag, or forceIsTree. False for a def without plant properties.</summary>
    public bool PlantIsTree(string def) =>
        FindThing(def)?.plant is { } props && (props.harvestTag == WoodHarvestTag || props.IsTree);

    /// <summary><c>PlantProperties.diesToLight</c> of def's row (cave fungus).</summary>
    public bool PlantDiesToLight(string def) => FindThing(def)?.plant is { diesToLight: true };

    /// <summary>The glowRadius of def's CompProperties_Glower, null when the def has no glower comp.</summary>
    public float? GlowRadius(string def) =>
        FindThing(def)?.GetCompProperties<CompProperties_Glower>() is { } glower ? glower.glowRadius : null;
}
