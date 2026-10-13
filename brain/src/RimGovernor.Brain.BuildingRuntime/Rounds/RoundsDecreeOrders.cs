using RecipeDef = global::RimGovernor.Protocol.Defs.RecipeDef;
using ThingDef = global::RimGovernor.Protocol.Defs.ThingDef;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsPopulationJoinerPlanner
{
    public Declared DeclareOrders(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct) =>
        DeclareOrdersInner(snapshot, projection, benches, ct).For(PolicyApi.MaintainPopulation);

    private Declared DeclareOrdersInner(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct)
    {
        DefinitionCatalog? catalog = null;
        Exception? catalogErr = null;

        IReadOnlyList<DecreeRecipe> Recipes(QuestObjective objective)
        {
            if (catalog is null && catalogErr is null)
            {
                if (_reviewer.Native is IDefinitionSource source)
                {
                    try
                    {
                        catalog = source.DefinitionCatalog(BoundaryApi.Identity(snapshot), ct);
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
            }
            if (catalogErr is not null)
            {
                return [];
            }
            return DecreeRecipes(benches, catalog!, objective);
        }

        var declared = PolicyApi.DeclareDecreeOrders(new DecreeOrderRequest
        {
            Offers = projection.Facts.QuestOffers,
            Now = (long)projection.Identity.Tick,
            Stock = projection.Facts.Resources,
            Recipes = Recipes,
        });
        if (catalogErr is not null)
        {
            ct.ThrowIfCancellationRequested();
            declared.Unread(PolicyApi.CauseUnreadDefinitions);
        }
        lock (_mu)
        {
            _decreeWork = declared.Orders.Count > 0;
        }
        return declared;
    }

    internal bool DecreeLending()
    {
        lock (_mu)
        {
            return _decreeWork;
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
            bills ??= [];
            foreach (var recipe in recipes)
            {
                var row = catalog.DefRow<RecipeDef>(recipe.Definition);
                if (row is null)
                {
                    continue;
                }
                long units = 0;
                foreach (var product in row.Products)
                {
                    if (product.Value.ThingDef == objective.Def)
                    {
                        units += product.Value.Count;
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
                    Stuff = new Dictionary<Resource, bool>(),
                };
                if (ak && ok)
                {
                    candidate.Available = Known(available && on);
                }
                recipe.Ingredients.TryGetValue(out var groups);
                foreach (var group in groups ?? [])
                {
                    foreach (var alternative in group)
                    {
                        var def = catalog.DefRow<ThingDef>(alternative.Resource.Value);
                        if (def is not null && def.StuffProps is not null && row.ProductHasIngredientStuff)
                        {
                            candidate.Stuff[alternative.Resource] = true;
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
                        candidate.Available = Unknown<bool>();
                        continue;
                    }
                    if (!active)
                    {
                        continue;
                    }
                    if (bill.Spec.TryGetValue(out var spec) && spec.Mode == GearMode.Batch && (objective.Stuff == "" || spec.Ingredients.Contains(objective.Stuff)))
                    {
                        candidate.Existing = true;
                        candidate.Standing.Add(spec);
                    }
                    else
                    {
                        candidate.Available = Known(false);
                    }
                }
                output.Add(candidate);
            }
        }
        return output;
    }
}
