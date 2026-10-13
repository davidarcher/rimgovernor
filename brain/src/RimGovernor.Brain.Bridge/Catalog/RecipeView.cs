using System.Collections.Concurrent;
using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Go catalog_recipe_view.go: what a recipe is, costs and needs at a bench, read from its RecipeDef and the defs it
// joins; a frame carries only whether a bench offers the recipe now. The recipes' ingredient filters are the game's
// resolved ThingFilters, so a recipe generated from its product (RecipeDefGenerator) already has its allowed
// defs and the Go reconstruction of a generated slot (generatedSlot) has no counterpart.

/// <summary>One ingredient slot resolved against the defs: the defs its filter allows and the count each takes.</summary>
internal sealed record RecipeSlot(IReadOnlyList<string> Allowed, IReadOnlyList<long> Counts);

/// <summary>The facts a meal planner reads of a recipe at a bench: what the recipe is worth as food. A measure the defs do not settle is unknown.</summary>
public sealed record MealFacts
{
    public Fact<bool> NeedsPower { get; init; }

    public Fact<double> Mood { get; init; }

    public Fact<double> NutrientEfficiency { get; init; }

    public Fact<double> WorkPerNutrition { get; init; }

    public Fact<EquatableArray<P.FoodIngredientSlot>> IngredientClasses { get; init; }

    public Fact<int> CookSkillFloor { get; init; }
}

public sealed partial class DefinitionCatalog
{
    // smallVolumePerUnit is ThingDef.SmallVolumePerUnit.
    private const string StatWorkToMake = "WorkToMake";

    private readonly ConcurrentDictionary<string, RecipeSlot[]> _recipeSlots = new(StringComparer.Ordinal);
    private Lazy<IReadOnlyDictionary<string, IReadOnlyList<WorkGiverDef>>>? _billGivers;
    private Lazy<IReadOnlyDictionary<string, IReadOnlyList<string>>>? _recipeBenches;

    /// <summary>name's RecipeDef; it is an error when the catalog has none.</summary>
    public RecipeDef Recipe(string name) =>
        DefRow<RecipeDef>(name) ?? throw new CatalogException($"catalog has no recipe row for {name}");

    /// <summary>The distinct defs the recipe makes, in the recipe's order.</summary>
    public IReadOnlyList<P.Resource> RecipeProducts(string name) => RecipeProductDefs(Recipe(name));

    private static List<P.Resource> RecipeProductDefs(RecipeDef row)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<P.Resource>();
        foreach (var product in row.products.Seq())
        {
            if (product.thingDef is { } def && seen.Add(def.defName))
            {
                result.Add(new P.Resource(def.defName));
            }
        }

        return result;
    }

    /// <summary>
    /// The recipe's ingredient slots as alternatives, each allowed def with the count the slot takes of it (the game's
    /// <c>IngredientCount.CountRequiredOfFor</c>, which the recipe's value getter scales). Unknown when a slot allows no def
    /// or takes none of one.
    /// </summary>
    public Fact<EquatableArray<EquatableArray<P.Amount>>> RecipeIngredients(string name)
    {
        var slots = RecipeSlotsOf(name);
        var slotAmounts = new List<EquatableArray<P.Amount>>(slots.Length);
        foreach (var slot in slots)
        {
            if (slot.Allowed.Count == 0)
            {
                return Unknown<EquatableArray<EquatableArray<P.Amount>>>();
            }

            var amounts = new List<P.Amount>(slot.Allowed.Count);
            for (var i = 0; i < slot.Allowed.Count; i++)
            {
                if (slot.Counts[i] <= 0)
                {
                    return Unknown<EquatableArray<EquatableArray<P.Amount>>>();
                }

                amounts.Add(new P.Amount(new P.Resource(slot.Allowed[i]), slot.Counts[i]));
            }

            slotAmounts.Add(EquatableArray.From(amounts));
        }

        return Known(EquatableArray.From(slotAmounts));
    }

    // RecipeSlotsOf resolves every slot of the recipe, once per catalog.
    private RecipeSlot[] RecipeSlotsOf(string name) =>
        _recipeSlots.GetOrAdd(name, static (key, catalog) => catalog.ResolveRecipeSlots(catalog.Recipe(key)), this);

    private RecipeSlot[] ResolveRecipeSlots(RecipeDef row)
    {
        var getter = row.IngredientValueGetter;
        var byNutrition = getter is IngredientValueGetter_Nutrition;
        if (!byNutrition && getter is not IngredientValueGetter_Volume)
        {
            throw new CatalogException($"recipe {row.defName} values its ingredients with {getter.GetType().FullName}, which the recipe view does not model");
        }

        var slots = new List<RecipeSlot>();
        foreach (var ingredient in row.ingredients.Seq())
        {
            var allowed = new List<string>();
            var counts = new List<long>();
            foreach (var def in AllowedDefs(ingredient.filter))
            {
                var value = ValuePerUnit(def, byNutrition);
                if (value <= 0)
                {
                    throw new CatalogException($"recipe {row.defName} slot allows {def}, which has no value to the {getter.GetType().Name} ingredient getter");
                }

                allowed.Add(def);
                counts.Add((long)Math.Ceiling(ingredient.GetBaseCount() / value));
            }

            slots.Add(new RecipeSlot(allowed, counts));
        }

        return [.. slots];
    }

    // ValuePerUnit is IngredientValueGetter.ValuePerUnitOf: the nutrition of a nutrition-giving ingestible for the
    // nutrition getter, and for the volume getter the volume of a stuff (its unit is 0.1 when small) and 1 for any
    // other def.
    private float ValuePerUnit(string def, bool nutrition)
    {
        var row = ThingRow(def);
        if (nutrition)
        {
            if (row.ingestible is null)
            {
                return 0;
            }

            var (value, shown) = ShownStatValue(def, string.Empty, StatNames.Nutrition);
            return shown ? value : 0;
        }

        return row.stuffProps is not null && row.smallVolume ? ThingDef.SmallVolumePerUnit : 1;
    }

    /// <summary>The defs the filter allows (<c>ThingFilter.AllowedThingDefs</c>), sorted by name. A null filter allows none.</summary>
    internal static IReadOnlyList<string> AllowedDefs(ThingFilter? filter) =>
        filter is null ? [] : [.. filter.AllowedThingDefs.Select(d => d.defName).Order(StringComparer.Ordinal)];

    /// <summary>Whether a ThingFilter allows the def, by the game's own resolution (the listed defs and every def within a listed category, less the disallowed ones).</summary>
    public bool FilterAccepts(ThingFilter? filter, string def) => filter is not null && filter.Allows(ThingRow(def));

    /// <summary>
    /// The one work requirement a bill on this recipe at the bench definition needs covered: the work type of the DoBill
    /// giver serving the bench together with the recipe's own work skill and the highest minimum level over its skill
    /// requirements. Unknown when no giver serves the bench.
    /// </summary>
    public Fact<EquatableArray<P.WorkRequirement>> RecipeWork(string name, string bench)
    {
        var row = Recipe(name);
        if (BillWorkType(row, bench) is not { } work)
        {
            return Unknown<EquatableArray<P.WorkRequirement>>();
        }

        var minimum = 0;
        foreach (var skill in row.skillRequirements.Seq())
        {
            if (skill.minLevel < 0)
            {
                return Unknown<EquatableArray<P.WorkRequirement>>();
            }

            minimum = Math.Max(minimum, skill.minLevel);
        }

        return TryWorkType(work, out var type)
            ? Known<EquatableArray<P.WorkRequirement>>([new P.WorkRequirement { Work = type, Skill = row.workSkill?.defName ?? string.Empty, Minimum = minimum }])
            : Unknown<EquatableArray<P.WorkRequirement>>();
    }

    /// <summary>
    /// The work one unit of the recipe takes, as <c>RecipeDef.WorkAmountTotal</c> reads it: the recipe's own workAmount,
    /// else (a negative amount) the first product's base WorkToMake stat. The stuff multiplier of a stuffed product is
    /// not applied, so the figure is the base cost. Unknown when the recipe has no amount and no shown WorkToMake on its
    /// product.
    /// </summary>
    public Fact<double> RecipeWorkAmount(string name)
    {
        var row = Recipe(name);
        var work = row.workAmount;
        if (work < 0)
        {
            if (row.products.Seq().FirstOrDefault() is not { thingDef: { } first })
            {
                return Unknown<double>();
            }

            var (stat, shown) = ShownStatValue(first.defName, string.Empty, StatWorkToMake);
            if (!shown)
            {
                return Unknown<double>();
            }

            work = stat;
        }

        return float.IsFinite(work) && work >= 0 ? Known((double)work) : Unknown<double>();
    }

    // BillWorkType is the work type whose DoBill giver serves the bench definition, so a worker must have it enabled to
    // take the bill: the first such giver by name whose work type the recipe's required giver work type allows. Null
    // when no giver serves the bench.
    private string? BillWorkType(RecipeDef recipe, string bench)
    {
        _billGivers ??= new(BuildBillGivers);
        if (!_billGivers.Value.TryGetValue(bench, out var givers))
        {
            return null;
        }

        foreach (var giver in givers)
        {
            if (recipe.requiredGiverWorkType is null || recipe.requiredGiverWorkType == giver.workType)
            {
                return giver.workType.defName;
            }
        }

        return null;
    }

    // BuildBillGivers are the DoBill work givers of each bench definition they serve, by giver name.
    private Dictionary<string, IReadOnlyList<WorkGiverDef>> BuildBillGivers()
    {
        var result = new Dictionary<string, List<WorkGiverDef>>(StringComparer.Ordinal);
        foreach (var giver in Rows<WorkGiverDef>().Values.OrderBy(g => g.defName, StringComparer.Ordinal))
        {
            if (giver.workType is null || giver.fixedBillGiverDefs.Seq().Any() is false || !typeof(WorkGiver_DoBill).IsAssignableFrom(giver.giverClass))
            {
                continue;
            }

            foreach (var bench in giver.fixedBillGiverDefs)
            {
                if (!result.TryGetValue(bench.defName, out var list))
                {
                    result[bench.defName] = list = [];
                }

                list.Add(giver);
            }
        }

        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<WorkGiverDef>)kv.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// The PawnKindDef of the mech a Biotech gestation recipe makes: the catalog's mech kind of the race it produces.
    /// Empty for every other recipe; a gestation recipe that produces no mech race is an error.
    /// </summary>
    public string RecipeMechKind(string name)
    {
        var row = Recipe(name);
        if (row.mechResurrection)
        {
            return string.Empty;
        }

        if (row.products.Seq().ToList() is [{ thingDef: { } race }] && !row.specialProducts.Seq().Any())
        {
            var kind = MechKindOfRace(race.defName);
            if (kind.Length > 0)
            {
                return kind;
            }
        }

        return row.gestationCycles > 0
            ? throw new CatalogException($"gestation recipe {name} does not produce a mechanoid race")
            : string.Empty;
    }

    /// <summary>
    /// The recipes that make the product, with the player-buildable bill giver buildings that host each, read before any
    /// such bench exists so a workshop project can choose which bench to stage. A recipe is available when every research
    /// project it requires is finished; one gated by ideology (a meme, a faction tag, a building precept) is left out, as
    /// nothing but the game can say it is available.
    /// </summary>
    public IReadOnlyList<P.RecipeHost> RecipeHosts(string product, IReadOnlySet<string>? finished)
    {
        var result = new List<P.RecipeHost>();
        foreach (var row in Rows<RecipeDef>().Values.OrderBy(r => r.defName, StringComparer.Ordinal))
        {
            var products = RecipeProductDefs(row);
            if (!products.Contains(new P.Resource(product)))
            {
                continue;
            }

            if (row.memePrerequisitesAny.Seq().Any() || row.factionPrerequisiteTags.Seq().Any() || row.fromIdeoBuildingPreceptOnly)
            {
                continue;
            }

            var benches = RecipeBenchesOf(row);
            if (benches.Count == 0)
            {
                continue;
            }

            var available = true;
            var research = new List<string>();
            foreach (var project in RecipeResearch(row))
            {
                research.Add(project);
                available = available && finished?.Contains(project) == true;
            }

            research.Sort(StringComparer.Ordinal);
            var requiredWork = Unknown<EquatableArray<P.WorkRequirement>>();
            foreach (var bench in benches)
            {
                requiredWork = RecipeWork(row.defName, bench);
                if (requiredWork.IsKnown)
                {
                    break;
                }
            }

            result.Add(new P.RecipeHost
            {
                Definition = row.defName,
                Products = Domain.EquatableArray.From(products),
                Benches = Domain.EquatableArray.From(benches),
                Available = available,
                Ingredients = RecipeIngredients(row.defName),
                RequiredWork = requiredWork,
                Research = Domain.EquatableArray.From(research),
            });
        }

        return result;
    }

    // RecipeResearch is the distinct research projects a recipe requires (researchPrerequisite and researchPrerequisites).
    private static IEnumerable<string> RecipeResearch(RecipeDef row)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (row.researchPrerequisite is { } single && seen.Add(single.defName))
        {
            yield return single.defName;
        }

        foreach (var project in row.researchPrerequisites.Seq())
        {
            if (seen.Add(project.defName))
            {
                yield return project.defName;
            }
        }
    }

    // RecipeBenchesOf are the player-buildable bill giver buildings hosting the recipe, sorted: the defs it names as
    // users and the defs that list it.
    private IReadOnlyList<string> RecipeBenchesOf(RecipeDef recipe)
    {
        _recipeBenches ??= new(BuildRecipeBenches);
        return _recipeBenches.Value.GetValueOrDefault(recipe.defName) ?? [];
    }

    private Dictionary<string, IReadOnlyList<string>> BuildRecipeBenches()
    {
        var users = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Note(RecipeDef recipe, ThingDef user)
        {
            if (!users.TryGetValue(recipe.defName, out var set))
            {
                users[recipe.defName] = set = new HashSet<string>(StringComparer.Ordinal);
            }

            set.Add(user.defName);
        }

        foreach (var recipe in Rows<RecipeDef>().Values)
        {
            foreach (var user in recipe.recipeUsers.Seq())
            {
                Note(recipe, user);
            }
        }

        foreach (var thing in _thingDefs.Values)
        {
            foreach (var recipe in thing.recipes.Seq())
            {
                Note(recipe, thing);
            }
        }

        return users.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<string>)[.. kv.Value
                .Select(ThingRow)
                .Where(row => row.category == ThingCategory.Building && Buildable(row) && typeof(Building_WorkTable).IsAssignableFrom(row.thingClass))
                .Select(row => row.defName)
                .Order(StringComparer.Ordinal)],
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Reads the meal facts of a recipe at a bench definition. A recipe that makes one nutrition-giving ingestible has a
    /// mood (its taste thought's base effect), work per nutrition and, valued by nutrition from raw ingredient slots, the
    /// nutrient efficiency (product nutrition over ingredient nutrition), the raw class alternatives of each slot and
    /// the cooking skill floor; a recipe that fails any of these steps keeps the measures before it.
    /// </summary>
    public MealFacts RecipeMealFacts(string name, string bench)
    {
        var row = Recipe(name);
        var facts = new MealFacts { NeedsPower = Known(HasComp<CompProperties_Power>(ThingRow(bench))) };
        if (row.products.Seq().ToList() is not [{ thingDef: { } productDef } product])
        {
            return facts;
        }

        var productRow = ThingRow(productDef.defName);
        if (productRow.ingestible is null)
        {
            return facts;
        }

        var (value, shown) = ShownStatValue(productDef.defName, string.Empty, StatNames.Nutrition);
        var nutrition = value * product.count;
        if (!shown || !Positive(nutrition))
        {
            return facts;
        }

        if (productRow.ingestible.tasteThought is not { } thought)
        {
            facts = facts with { Mood = Known(0.0) };
        }
        else if (thought.stages is [{ } stage] && float.IsFinite(stage.baseMoodEffect))
        {
            facts = facts with { Mood = Known((double)stage.baseMoodEffect) };
        }

        var work = row.workAmount;
        if (work < 0)
        {
            var (stat, workShown) = ShownStatValue(productDef.defName, string.Empty, StatWorkToMake);
            if (workShown)
            {
                work = stat;
            }
        }

        if (float.IsFinite(work) && work >= 0)
        {
            facts = facts with { WorkPerNutrition = Known((double)(work / nutrition)) };
        }

        if (row.IngredientValueGetter is not IngredientValueGetter_Nutrition || row.ingredients.Seq().Any() is false)
        {
            return facts;
        }

        if (MealSlots(row) is not var (slots, input) || !Positive(input))
        {
            return facts;
        }

        facts = facts with { IngredientClasses = Known(EquatableArray.From(slots)), NutrientEfficiency = Known(nutrition / input) };
        var floor = 0;
        foreach (var skill in row.skillRequirements.Seq())
        {
            if (skill.skill != row.workSkill)
            {
                continue;
            }

            if (skill.minLevel < 0 || skill.minLevel > SkillRecord.MaxLevel)
            {
                return facts;
            }

            floor = Math.Max(floor, skill.minLevel);
        }

        return facts with { CookSkillFloor = Known(floor) };
    }

    // MealSlots reads each ingredient slot of a nutrition-valued recipe as the raw food classes of the defs the slot,
    // the recipe's fixed filter and its default filter all allow, with the summed base count of the slots. Null when a
    // slot has a zero count, no def, a def that is no ingestible or one outside every raw class.
    private (List<P.FoodIngredientSlot> Slots, double Input)? MealSlots(RecipeDef row)
    {
        var fixedFilter = row.fixedIngredientFilter ?? throw new CatalogException($"recipe {row.defName} has no fixed ingredient filter");
        var standard = row.defaultIngredientFilter ?? fixedFilter;
        var slots = new List<P.FoodIngredientSlot>();
        double input = 0;
        foreach (var ingredient in row.ingredients.Seq())
        {
            double count = ingredient.GetBaseCount();
            if (!Positive(count))
            {
                return null;
            }

            input += count;
            var classes = new List<P.FoodIngredientClass>();
            foreach (var def in AllowedDefs(ingredient.filter))
            {
                var thing = ThingRow(def);
                if (!fixedFilter.Allows(thing) || !standard.Allows(thing))
                {
                    continue;
                }

                if (thing.ingestible is null)
                {
                    return null;
                }

                var rawClass = RawFoodClass(def);
                if (rawClass == P.FoodIngredientClass.None)
                {
                    return null;
                }

                if (!classes.Contains(rawClass))
                {
                    classes.Add(rawClass);
                }
            }

            if (classes.Count == 0)
            {
                return null;
            }

            classes.Sort((a, b) => RawClassOrder(a) - RawClassOrder(b));
            if (classes.Count == 3)
            {
                classes = [P.FoodIngredientClass.Any];
            }

            slots.Add(new P.FoodIngredientSlot { Alternatives = [.. classes] });
        }

        return (slots, input);
    }

    // RawClassOrder is the order a slot lists its classes in: meat, vegetable, animal product.
    private static int RawClassOrder(P.FoodIngredientClass raw) => raw switch
    {
        P.FoodIngredientClass.Meat => 1,
        P.FoodIngredientClass.Vegetable => 2,
        _ => 3,
    };

    internal static bool Positive(double n) => double.IsFinite(n) && n > 0;
}
