using System.Collections.Concurrent;
using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// What a recipe is, costs and needs at a bench, read from its RecipeDef and the defs it
// joins; a frame carries only whether a bench offers the recipe now. The recipes' ingredient filters are the game's
// resolved ThingFilters, so a recipe generated from its product (RecipeDefGenerator) already has its allowed
// defs.

/// <summary>One ingredient slot resolved against the defs: the defs its filter allows and the count each takes.</summary>
internal sealed record RecipeSlot(IReadOnlyList<string> Allowed, IReadOnlyList<long> Counts);

public sealed partial class DefinitionCatalog
{
    // smallVolumePerUnit is ThingDef.SmallVolumePerUnit.
    private const string StatWorkToMake = "WorkToMake";

    // The order a raw meal slot lists its classes in.
    private static readonly P.FoodIngredientClass[] RawClassOrder = [P.FoodIngredientClass.Meat, P.FoodIngredientClass.Vegetable, P.FoodIngredientClass.AnimalProduct];

    private readonly ConcurrentDictionary<string, RecipeSlot[]> _recipeSlots = new(StringComparer.Ordinal);
    private Lazy<IReadOnlyDictionary<string, IReadOnlyList<WorkGiverDef>>>? _billGivers;
    private Lazy<IReadOnlyDictionary<string, IReadOnlyList<string>>>? _recipeBenches;

    /// <summary>name's RecipeDef; it is an error when the catalog has none.</summary>
    public RecipeDef Recipe(string name) =>
        DefRow<RecipeDef>(name) ?? throw new CatalogException($"catalog has no recipe row for {name}");

    /// <summary>The distinct defs the recipe makes, in the recipe's order.</summary>
    public IReadOnlyList<P.Resource> RecipeProducts(string name) => RecipeProductDefs(Recipe(name));

    /// <summary>
    /// Definition-only inputs for observed building types, including recipes not yet claimed by a Method. A recipe with
    /// an ingredient slot no def fills (<see cref="RecipeFillable"/>) can never start, so it is not offered.
    /// </summary>
    public EquatableArray<P.ProductionRecipeInput> ProductionRecipeInputs(IEnumerable<string> buildingDefs)
    {
        var buildings = buildingDefs.ToHashSet(StringComparer.Ordinal);
        var result = new List<P.ProductionRecipeInput>();
        foreach (var recipe in Rows<RecipeDef>().Values.Where(r => r.products.Count > 0 && RecipeBenchesOf(r).Any(buildings.Contains) && RecipeFillable(r.defName)).OrderBy(r => r.defName, StringComparer.Ordinal))
        {
            result.Add(new(recipe.defName, RecipeIngredients(recipe.defName), recipe.unfinishedThingDef is not null)
            {
                Products = EquatableArray.From(recipe.products.Select(p => new P.Amount(new P.Resource(p.thingDef.defName), p.count))),
                WorkAmount = RecipeWorkAmount(recipe.defName),
            });
        }
        return EquatableArray.From(result);
    }

    private IReadOnlyList<P.Resource> RecipeProductDefs(RecipeDef row) => RecipeProductIndex().Products[row.defName];

    /// <summary>The recipes, by defName, that make def; none when nothing does.</summary>
    private IReadOnlyList<RecipeDef> RecipesMaking(string def) =>
        RecipeProductIndex().Makers.TryGetValue(def, out var rows) ? rows : [];

    private sealed record RecipeProductRows(Dictionary<string, IReadOnlyList<P.Resource>> Products, Dictionary<string, IReadOnlyList<RecipeDef>> Makers);

    private RecipeProductRows? _recipeProducts;

    // The catalog's recipes do not change, and the host and tier queries asked for every recipe's products, sorted,
    // per product per pawn option: the products and the recipes making each def are read once per catalog.
    private RecipeProductRows RecipeProductIndex() => LazyInitializer.EnsureInitialized(ref _recipeProducts, () =>
    {
        var products = new Dictionary<string, IReadOnlyList<P.Resource>>(StringComparer.Ordinal);
        var makers = new Dictionary<string, List<RecipeDef>>(StringComparer.Ordinal);
        foreach (var row in Rows<RecipeDef>().Values.OrderBy(r => r.defName, StringComparer.Ordinal))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<P.Resource>();
            foreach (var product in row.products.Seq())
            {
                if (product.thingDef is { } def && seen.Add(def.defName))
                {
                    result.Add(new P.Resource(def.defName));
                    if (!makers.TryGetValue(def.defName, out var rows))
                    {
                        makers[def.defName] = rows = [];
                    }
                    rows.Add(row);
                }
            }
            products[row.defName] = result;
        }
        return new(products, makers.ToDictionary(m => m.Key, IReadOnlyList<RecipeDef> (m) => m.Value, StringComparer.Ordinal));
    });

    /// <summary>
    /// The recipe's ingredient slots as alternatives, each allowed def with the count the slot takes of it (the game's
    /// <c>IngredientCount.CountRequiredOfFor</c>, which the recipe's value getter scales). A recipe with a slot no def
    /// fills has no ingredients to read: callers offer only <see cref="RecipeFillable"/> recipes, and asking for another
    /// is an error.
    /// </summary>
    public EquatableArray<EquatableArray<P.Amount>> RecipeIngredients(string name)
    {
        var slots = RecipeSlotsOf(name);
        var slotAmounts = new List<EquatableArray<P.Amount>>(slots.Length);
        foreach (var slot in slots)
        {
            if (slot.Allowed.Count == 0)
            {
                throw new CatalogException($"recipe {name} has an ingredient slot no def fills");
            }

            slotAmounts.Add(EquatableArray.From(slot.Allowed.Select((def, i) => new P.Amount(new P.Resource(def), slot.Counts[i]))));
        }

        return EquatableArray.From(slotAmounts);
    }

    /// <summary>
    /// Whether the catalog has the recipe and every ingredient slot of it allows some def. A slot whose filter allows none can never be filled,
    /// so a bill of the recipe never starts: planners do not offer it, nor one the catalog has no row for.
    /// </summary>
    public bool RecipeFillable(string name) => DefRow<RecipeDef>(name) is not null && RecipeSlotsOf(name).All(slot => slot.Allowed.Count > 0);

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

                var count = (long)Math.Ceiling(ingredient.GetBaseCount() / value);
                if (count <= 0)
                {
                    throw new CatalogException($"recipe {row.defName} has a slot that takes none of {def}");
                }

                allowed.Add(def);
                counts.Add(count);
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

            var (value, shown) = ShownStatValue(def, string.Empty, CatalogStats.Nutrition);
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
    /// requirements (a requirement below zero is met by every pawn, as the game's <c>SkillRequirement.PawnSatisfies</c>
    /// reads it, so the minimum is never below zero). Only a recipe <see cref="RecipeWorkedAt"/> the bench has one; asking
    /// for another is an error.
    /// </summary>
    public EquatableArray<P.WorkRequirement> RecipeWork(string name, string bench)
    {
        var row = Recipe(name);
        var work = BillWorkType(row, bench) ?? throw new CatalogException($"no DoBill work giver serves recipe {name} at bench {bench}");
        if (!TryWorkType(work, out var type))
        {
            throw new CatalogException($"recipe {name} at bench {bench} is worked under {work}, which WorkType does not model");
        }

        var minimum = 0;
        foreach (var skill in row.skillRequirements.Seq())
        {
            minimum = Math.Max(minimum, skill.minLevel);
        }

        return [new P.WorkRequirement { Work = type, Skill = row.workSkill?.defName ?? string.Empty, Minimum = minimum }];
    }

    /// <summary>
    /// Whether a DoBill work giver serves the bench definition for the recipe, so a worker can take its bill there. A
    /// recipe no giver serves at a bench is never worked there, and planners do not offer it at that bench.
    /// </summary>
    public bool RecipeWorkedAt(string name, string bench) => BillWorkType(Recipe(name), bench) is not null;

    /// <summary>
    /// The work one unit of the recipe takes, as <c>RecipeDef.WorkAmountTotal</c> reads it: the recipe's own workAmount,
    /// else (a negative amount) the first product's base WorkToMake stat, which the game reads whether or not it shows
    /// it. The stuff multiplier of a stuffed product is not applied, so the figure is the base cost. A recipe with no
    /// amount and no product is an error, as the game's own read fails on it.
    /// </summary>
    public double RecipeWorkAmount(string name)
    {
        var row = Recipe(name);
        if (row.workAmount >= 0)
        {
            return row.workAmount;
        }

        var first = row.products.Seq().FirstOrDefault()?.thingDef
            ?? throw new CatalogException($"recipe {name} sets no work amount and makes no product to read WorkToMake from");
        return PlannerStatValue(first.defName, string.Empty, CatalogStats.WorkToMake);
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
    /// nothing but the game can say it is available, and so is one with a slot no def fills (RecipeFillable).
    /// </summary>
    public IReadOnlyList<P.RecipeHost> RecipeHosts(string product, IReadOnlySet<string>? finished)
    {
        var result = new List<P.RecipeHost>();
        foreach (var row in RecipesMaking(product))
        {
            if (row.memePrerequisitesAny.Seq().Any() || row.factionPrerequisiteTags.Seq().Any() || row.fromIdeoBuildingPreceptOnly || !RecipeFillable(row.defName))
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
            result.Add(new P.RecipeHost
            {
                Definition = row.defName,
                Products = Domain.EquatableArray.From(RecipeProductDefs(row)),
                Benches = Domain.EquatableArray.From(benches),
                Available = available,
                Ingredients = RecipeIngredients(row.defName),
                Research = Domain.EquatableArray.From(research),
            });
        }

        return result;
    }

    /// <summary>
    /// Every recipe a player-buildable bench hosts, as a bench recipe row with no bench (#3659): a declarer that finds
    /// no standing bench listing a recipe for what it needs orders one of these, and the station graph hosts it. A row
    /// is available when the recipe's research and some host's are finished, its skill gate and armory rung those of
    /// the first such host by name; AvailableOn is known true, as no bench stands to refuse it. A recipe gated by
    /// ideology, making nothing, or whose facts the catalog cannot read is left out.
    /// </summary>
    public EquatableArray<P.GearRecipe> CraftRecipes(IReadOnlySet<string> finished)
    {
        var output = new List<P.GearRecipe>();
        foreach (var row in CraftRows())
        {
            if (!row.Research.All(finished.Contains) || row.Hosts.FirstOrDefault(h => h.Research.All(finished.Contains)) is not { } host)
            {
                output.Add(row.Recipe);
                continue;
            }
            output.Add(row.Recipe with { Available = Known(true), RequiredWork = host.Work, Armory = host.Armory });
        }
        return Domain.EquatableArray.From(output);
    }

    private sealed record CraftHost(IReadOnlyList<string> Research, EquatableArray<P.WorkRequirement> Work, P.ArmoryTier Armory);

    private sealed record CraftRow(P.GearRecipe Recipe, IReadOnlyList<string> Research, IReadOnlyList<CraftHost> Hosts);

    private IReadOnlyList<CraftRow>? _craftRows;

    // The catalog's recipes do not change: the craft rows are read once per catalog, research judged per call.
    private IReadOnlyList<CraftRow> CraftRows() => LazyInitializer.EnsureInitialized(ref _craftRows, () =>
    {
        var rows = new List<CraftRow>();
        foreach (var row in Rows<RecipeDef>().Values.OrderBy(r => r.defName, StringComparer.Ordinal))
        {
            if (row.memePrerequisitesAny.Seq().Any() || row.factionPrerequisiteTags.Seq().Any() || row.fromIdeoBuildingPreceptOnly)
            {
                continue;
            }
            var benches = RecipeBenchesOf(row).Where(b => RecipeWorkedAt(row.defName, b)).ToList();
            var products = RecipeProductDefs(row);
            if (benches.Count == 0 || products.Count == 0 || !RecipeFillable(row.defName))
            {
                continue;
            }
            if (CraftRowOf(row, benches, products) is { } craft)
            {
                rows.Add(craft);
            }
        }
        return rows;
    });

    // A craft row of the recipe at the benches whose work giver serves it; null when the catalog cannot read its facts.
    private CraftRow? CraftRowOf(RecipeDef row, IReadOnlyList<string> benches, IReadOnlyList<P.Resource> products)
    {
        try
        {
            var hosts = benches.Select(b => new CraftHost([.. ThingRow(b).researchPrerequisites.Seq().Select(r => r.defName)], RecipeWork(row.defName, b), RecipeArmoryTier(row.defName, b))).ToList();
            var mechKind = RecipeMechKind(row.defName);
            var recipe = new P.GearRecipe
            {
                Definition = row.defName,
                Role = RecipeRoleOf(row.defName),
                Products = Domain.EquatableArray.From(products),
                Available = Known(false),
                AvailableOn = Known(true),
                Ingredients = RecipeIngredients(row.defName),
                RequiredWork = hosts[0].Work,
                WorkAmount = RecipeWorkAmount(row.defName),
                MechKind = mechKind,
                Kind = RecipeLedgerKind(row.defName, mechKind),
                Armory = hosts[0].Armory,
            };
            return new CraftRow(recipe, [.. RecipeResearch(row)], hosts);
        }
        catch (Exception e) when (e is DomainException or CatalogException)
        {
            return null;
        }
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
    /// Reads what a recipe is worth as food. A recipe that makes one nutrition-giving ingestible is food: its mood is the
    /// product's taste thought's base effect at the stage a new memory takes (0 with no taste thought) and its work per
    /// nutrition the recipe's work amount over the nutrition one iteration makes. A food recipe valued by nutrition from
    /// raw ingredient slots is cooked from raw: the nutrient efficiency (product nutrition over ingredient nutrition),
    /// the raw class alternatives of each slot and the cooking skill floor.
    /// </summary>
    public P.RecipeFood RecipeFood(string name)
    {
        var row = Recipe(name);
        if (row.products.Seq().ToList() is not [{ thingDef: { } productDef } product] || ThingRow(productDef.defName).ingestible is not { } ingestible)
        {
            return new P.RecipeFood.NotFood();
        }

        var (value, shown) = ShownStatValue(productDef.defName, string.Empty, CatalogStats.Nutrition);
        var nutrition = value * product.count;
        if (!shown || !Positive(nutrition))
        {
            return new P.RecipeFood.NotFood();
        }

        // A memory thought starts at its first stage (Thought_Memory's stage index 0).
        var mood = ingestible.tasteThought is not { } thought ? 0.0
            : thought.stages is [{ } stage, ..] ? stage.baseMoodEffect
            : throw new CatalogException($"taste thought {thought.defName} of {productDef.defName} has no first stage");
        return new P.RecipeFood.Food(mood, RecipeWorkAmount(name) / nutrition, RecipeCookingOf(row, nutrition));
    }

    // RecipeCookingOf reads a food recipe's raw cooking: valued by nutrition from slots that are all raw food classes.
    private P.RecipeCooking RecipeCookingOf(RecipeDef row, double nutrition)
    {
        if (row.IngredientValueGetter is not IngredientValueGetter_Nutrition || row.ingredients.Seq().Any() is false
            || MealSlots(row) is not var (slots, input) || !Positive(input))
        {
            return new P.RecipeCooking.NotFromRaw();
        }

        // A requirement below zero is met by every pawn (SkillRequirement.PawnSatisfies), as a floor of zero is.
        var floor = 0;
        foreach (var skill in row.skillRequirements.Seq())
        {
            if (skill.skill == row.workSkill)
            {
                floor = Math.Max(floor, skill.minLevel);
            }
        }

        return new P.RecipeCooking.FromRaw(nutrition / input, EquatableArray.From(slots), floor);
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

            classes.Sort((a, b) => Array.IndexOf(RawClassOrder, a) - Array.IndexOf(RawClassOrder, b));
            if (classes.Count == 3)
            {
                classes = [P.FoodIngredientClass.Any];
            }

            slots.Add(new P.FoodIngredientSlot { Alternatives = [.. classes] });
        }

        return (slots, input);
    }


    internal static bool Positive(double n) => double.IsFinite(n) && n > 0;
}
