using RecipeDef = global::Verse.RecipeDef;
using ThingDef = global::Verse.ThingDef;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsPopulationJoinerPlanner
{
    public async Task<Declared> DeclareOrdersAsync(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct) =>
        (await DeclareOrdersInnerAsync(snapshot, projection, benches, ct).ConfigureAwait(false)).For(PolicyConstants.MaintainPopulation);

    /// <summary>
    /// Declares the production batches of the open produce-item decrees (OrderDeclarer) and notes whether one is
    /// declared, for the clock's game time (decreeLending). Harvest and hunt decrees stay admitStandardMethod's.
    /// Go reads the definition catalog lazily from inside the policy's recipes callback; the callback is
    /// synchronous here, so a first pass finds out whether the policy asks for recipes at all and the catalog is
    /// read (once) only then, before the declaration runs again with it.
    /// </summary>
    private async Task<Declared> DeclareOrdersInnerAsync(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct)
    {
        var asked = false;
        var request = new DecreeOrderRequest
        {
            Offers = projection.Facts.QuestOffers,
            Now = projection.Identity.Tick.Value,
            Stock = projection.Facts.Resources,
            Recipes = _ =>
            {
                asked = true;
                return [];
            },
        };
        var declared = PolicyApi.DeclareDecreeOrders(request);
        Exception? catalogErr = null;
        if (asked)
        {
            DefinitionCatalog? catalog = null;
            if (reviewer.Native is IDefinitionSource source)
            {
                try
                {
                    catalog = await source.DefinitionCatalogAsync(Boundary.Identity(snapshot), ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    catalogErr = ex;
                }
            }
            else
            {
                catalogErr = new InvalidOperationException("decree recipes: the native source serves no definitions");
            }
            if (catalog is null)
            {
                catalogErr ??= new InvalidOperationException("decree recipes: the native source serves no definitions");
            }
            else if (catalogErr is null)
            {
                declared = PolicyApi.DeclareDecreeOrders(request with { Recipes = objective => DecreeRecipes(benches, catalog, objective) });
            }
        }
        if (catalogErr is not null)
        {
            ct.ThrowIfCancellationRequested();
            declared.Unread(Cause.UnreadDefinitions);
        }
        lock (mu)
        {
            decreeWork = declared.Orders.Count > 0;
        }
        return declared;
    }

    internal bool DecreeLending()
    {
        lock (mu)
        {
            return decreeWork;
        }
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>The recipes on the benches that make the quest objective's item, with their standing batch bills.</summary>
    internal static List<DecreeRecipe> DecreeRecipes(IReadOnlyList<GearBench> benches, DefinitionCatalog catalog, QuestObjective objective)
    {
        var output = new List<DecreeRecipe>();
        foreach (var bench in benches)
        {
            if (!bench.Recipes.TryGetValue(out var recipes))
            {
                continue;
            }
            bench.Bills.TryGetValue(out var bills);
            foreach (var recipe in recipes)
            {
                var row = catalog.DefRow<RecipeDef>(recipe.Definition);
                if (row is null)
                {
                    continue;
                }
                long units = 0;
                foreach (var product in row.products ?? [])
                {
                    if (product.thingDef?.defName == objective.Def)
                    {
                        units += product.count;
                    }
                }
                if (units <= 0)
                {
                    continue;
                }
                var ak = recipe.Available.TryGetValue(out var available);
                var ok = recipe.AvailableOn.TryGetValue(out var on);
                var candidate = new DecreeRecipe
                {
                    Bench = bench.Id,
                    BenchKind = bench.Def,
                    Recipe = recipe.Definition,
                    Product = new Resource(objective.Def),
                    Units = units,
                    Ingredients = recipe.Ingredients,
                };
                var stuff = new Dictionary<Resource, bool>();
                var standing = new List<OrderSpec>();
                if (ak && ok)
                {
                    candidate = candidate with { Available = Known(available && on) };
                }
                recipe.Ingredients.TryGetValue(out var groups);
                foreach (var group in groups)
                {
                    foreach (var alternative in group)
                    {
                        var def = catalog.DefRow<ThingDef>(alternative.Resource.Value);
                        if (def is not null && def.stuffProps is not null && row.productHasIngredientStuff)
                        {
                            stuff[alternative.Resource] = true;
                        }
                    }
                }
                foreach (var bill in bills)
                {
                    if (bill.Recipe != recipe.Definition)
                    {
                        continue;
                    }
                    if (!bill.Active.TryGetValue(out var active))
                    {
                        candidate = candidate with { Available = Unknown<bool>() };
                        continue;
                    }
                    if (!active)
                    {
                        continue;
                    }
                    if (bill.Spec.TryGetValue(out var spec) && spec.Mode == BillMode.GearBatch && (objective.Stuff == "" || spec.Ingredients.Contains(objective.Stuff)))
                    {
                        candidate = candidate with { Existing = true };
                        standing.Add(spec);
                    }
                    else
                    {
                        candidate = candidate with { Available = Known(false) };
                    }
                }
                output.Add(candidate with { Stuff = stuff, Standing = standing });
            }
        }
        return output;
    }
}
