using System.Collections.Concurrent;
using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Go catalog_recipes.go, catalog_recipe_facts.go, catalog_recipe_bulk.go, catalog_install.go and catalog_gear_bill.go.
// What a recipe does is read from its RecipeDef, never from its defName: the butcher recipe is the one whose worker
// counter is the game's butcher counter, a sculpture makes an art building, an ordinary meal makes a perishable
// meal. A recipe the catalog has no row for is a named error. The ThingFilter is the game's resolved filter, so
// the Go refusal of filters with fields the evaluator does not model (unmodelledFilterField) has no counterpart.

public sealed partial class DefinitionCatalog
{
    // The thing category root of every body part item, the natural organs and the artificial parts.
    private const string CategoryBodyParts = "BodyParts";

    private readonly ConcurrentDictionary<string, RecipeRole> _recipeRoles = new(StringComparer.Ordinal);
    private Lazy<IReadOnlySet<string>>? _bulkRecipes;
    private Lazy<P.RecipeFacts>? _recipeFacts;

    /// <summary>What the recipe does by its def, <see cref="RecipeRole.None"/> for a recipe the planners do not single out; a recipe with no def is an error.</summary>
    public RecipeRole RecipeRoleOf(string name) =>
        _recipeRoles.GetOrAdd(name, static (key, catalog) => catalog.DeriveRecipeRole(catalog.Recipe(key)), this);

    private RecipeRole DeriveRecipeRole(RecipeDef row)
    {
        if (row.workerCounterClass is { } counter && typeof(RecipeWorkerCounter_ButcherAnimals).IsAssignableFrom(counter))
        {
            return RecipeRole.ButcherFlesh;
        }

        if (row.products.Seq().ToList() is [{ thingDef: { } productDef }] && !row.specialProducts.Seq().Any())
        {
            var def = ThingRow(productDef.defName);

            // A sculpture is a minifiable building with an art comp made of a stuff; a weapon or apparel can carry
            // CompArt too (Make_Gun_BeamRepeater) and a ritual sculpture costs fixed items (VoidSculpture), neither is one.
            if (def.category == ThingCategory.Building && def.minifiedDef is not null && def.costStuffCount > 0 && HasComp<CompProperties_Art>(def))
            {
                return RecipeRole.Sculpture;
            }
        }

        return MakesOrdinaryMeal(row) ? RecipeRole.OrdinaryMeal : RecipeRole.None;
    }

    // MakesOrdinaryMeal is whether every product is a perishable meal of the simple, fine or lavish kind; a meal that
    // never rots is a reserve, not part of the ordinary tier family.
    private bool MakesOrdinaryMeal(RecipeDef row)
    {
        if (!row.products.Seq().Any())
        {
            return false;
        }

        foreach (var product in row.products)
        {
            var thing = ThingRow(product.thingDef.defName);
            if (thing.ingestible is null)
            {
                return false;
            }

            if (FoodKindFor(thing) is not (P.FoodKind.MealSimple or P.FoodKind.MealFine or P.FoodKind.MealLavish))
            {
                return false;
            }

            if (RotDays(thing.defName) is null)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The recipe facts the planners read, derived from the RecipeDefs once per catalog.</summary>
    public P.RecipeFacts RecipeFacts()
    {
        _recipeFacts ??= new(BuildRecipeFacts);
        return _recipeFacts.Value;
    }

    private P.RecipeFacts BuildRecipeFacts()
    {
        var installs = new List<P.MaterialInstall>();
        var billWork = new Dictionary<string, P.WorkType>(StringComparer.Ordinal);
        foreach (var recipe in Rows<RecipeDef>().Values.OrderBy(r => r.defName, StringComparer.Ordinal))
        {
            if (MaterialInstallOf(recipe) is { } install)
            {
                installs.Add(install);
            }

            foreach (var bench in RecipeBenchesOf(recipe))
            {
                if (BillWorkType(recipe, bench) is { } work && TryWorkType(work, out var type))
                {
                    billWork[recipe.defName] = type;
                    break;
                }
            }
        }

        var (organs, vital) = BodyPartFacts();
        return new P.RecipeFacts { MaterialInstalls = [.. installs], BillWork = billWork, HarvestOrgans = [.. organs], VitalParts = vital.ToDictionary(part => part, _ => true, StringComparer.Ordinal) };
    }

    // MaterialInstallOf reads a recipe as an install of a part made straight from a stuff: it adds a hediff, consumes
    // one def that no body part category holds, and the hediff is what removing the part gives that same def back (a peg
    // leg takes a log and a removed peg leg is a log). A body part item install, a denture that takes only medicine and
    // every other recipe are not.
    private P.MaterialInstall? MaterialInstallOf(RecipeDef recipe)
    {
        if (recipe.addsHediff is not { } hediff || hediff.spawnThingOnRemoved is not { } spawned)
        {
            return null;
        }

        if (InstallItem(recipe.defName) != default(P.Resource))
        {
            return null;
        }

        foreach (var slot in recipe.ingredients.Seq())
        {
            if (slot.filter.AllowedThingDefs.ToList() is not [var only] || only != spawned)
            {
                continue;
            }

            var price = StatValue(spawned.defName, string.Empty, StatNames.MarketValue);
            var bodies = recipe.appliedOnFixedBodyParts.Seq().Select(b => b.defName).ToList();
            if (bodies.Count == 0)
            {
                throw new CatalogException($"recipe {recipe.defName} installs {hediff.defName} on no fixed body part");
            }

            return new P.MaterialInstall
            {
                Recipe = recipe.defName,
                Bodies = [.. bodies],
                Part = hediff.defName,
                Work = recipe.workAmount,
                Material = spawned.defName,
                Value = (double)price * slot.GetBaseCount(),
            };
        }

        return null;
    }

    /// <summary>
    /// The one body part item the recipe consumes: the part an install recipe installs (InstallBionicArm consumes
    /// BionicArm, the natural install of a kidney consumes Kidney). It is the recipe's ingredient slot that names a single
    /// def sitting within the body part categories; a recipe with no such slot (a peg leg is made of logs) installs no
    /// item.
    /// </summary>
    public P.Resource InstallItem(string recipe)
    {
        var row = Recipe(recipe);
        var item = default(P.Resource);
        foreach (var slot in row.ingredients.Seq())
        {
            if (slot.filter.AllowedThingDefs.ToList() is not [var thing])
            {
                continue;
            }

            if (!CategoriesWithin(ThingRow(thing.defName)).Contains(CategoryBodyParts))
            {
                continue;
            }

            if (item != default(P.Resource))
            {
                throw new CatalogException($"recipe {recipe}: more than one body part ingredient ({item.Value}, {thing.defName})");
            }

            item = new P.Resource(thing.defName);
        }

        return item;
    }

    /// <summary>
    /// Whether the recipe is a bulk recipe: one of a group of recipes that take the same ingredient filters and make
    /// the same products, the one that makes more of them per trip to the bench (CookMealSimpleBulk against
    /// CookMealSimple, Make_PemmicanBulk against Make_Pemmican). The group is read from the defs, never from a name
    /// suffix; a recipe with no def is an error.
    /// </summary>
    public bool RecipeBulk(string name)
    {
        _ = Recipe(name);
        _bulkRecipes ??= new(BuildBulkRecipes);
        return _bulkRecipes.Value.Contains(name);
    }

    // BuildBulkRecipes groups every recipe that makes products by its product defs and its ingredient slot filters
    // (the defs each allows); a recipe that makes more in total than the smallest of its group is bulk.
    private HashSet<string> BuildBulkRecipes()
    {
        var groups = new Dictionary<string, List<(string Name, int Total)>>(StringComparer.Ordinal);
        foreach (var recipe in Rows<RecipeDef>().Values.OrderBy(r => r.defName, StringComparer.Ordinal))
        {
            if (!recipe.products.Seq().Any() || !recipe.ingredients.Seq().Any())
            {
                continue;
            }

            var products = recipe.products.Select(p => p.thingDef.defName).Order(StringComparer.Ordinal);
            var key = string.Join(',', products) + string.Concat(recipe.ingredients.Select(slot => "|" + string.Join(',', AllowedDefs(slot.filter))));
            if (!groups.TryGetValue(key, out var members))
            {
                groups[key] = members = [];
            }

            members.Add((recipe.defName, recipe.products.Sum(p => p.count)));
        }

        var bulk = new HashSet<string>(StringComparer.Ordinal);
        foreach (var members in groups.Values)
        {
            var least = members.Min(m => m.Total);
            bulk.UnionWith(members.Where(m => m.Total > least).Select(m => m.Name));
        }

        return bulk;
    }

    /// <summary>
    /// What a bill option of an apparel def asks of the colony: the research projects its recipe requires and the adjusted
    /// cost of the def made from the stuff (empty for a def not made from stuff). The recipe is the first by defName whose
    /// only product is the def (<c>RecipeDef.ProducedThingDef</c>); null when no recipe makes it.
    /// </summary>
    public (IReadOnlyList<string> Research, IReadOnlyList<StatCost> Ingredients)? ApparelBill(string def, string stuff)
    {
        foreach (var row in Rows<RecipeDef>().Values.OrderBy(r => r.defName, StringComparer.Ordinal))
        {
            if (row.specialProducts.Seq().Any() || row.products.Seq().ToList() is not [{ thingDef: { } product }] || product.defName != def)
            {
                continue;
            }

            var research = RecipeResearch(row).Order(StringComparer.Ordinal).ToList();
            return (research, Stats.CostListAdjusted(def, stuff));
        }

        return null;
    }

    /// <summary>Whether the def has a smoke-pop verb (a <c>Verb_SmokePop</c> in its verbs).</summary>
    public bool SmokePop(string def) =>
        FindThing(def)?.Verbs.Seq().Any(v => v.verbClass == typeof(Verb_SmokePop)) == true;
}
