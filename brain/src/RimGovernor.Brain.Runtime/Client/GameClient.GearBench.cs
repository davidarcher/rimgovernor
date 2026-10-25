using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Domain;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Runtime;

public sealed partial class GameClient
{
    /// <summary>Native ListSupplies' StockFilter.DefNames cap.</summary>
    private const int GearMaxStockFilterDefNames = 256;

    /// <summary>
    /// A fresh, uncached census of every workbench's bill stack, its CAS token and
    /// its recipe catalog. <paramref name="catalog"/> is the definition catalog read; it is
    /// awaited after the bills reply arrives. The benches' recipe reads read nothing of each other, so they are
    /// issued together and their replies read in bench order (#3566): the first failure in that order throws.
    /// </summary>
    internal async Task<IReadOnlyList<GearBenchRead>> ReadGearBenchesAsync(
        RimGovernor.Brain.GameFacts.Common.Identity identity,
        Func<CancellationToken, Task<GearRecipeCatalogAdapter>> catalog,
        CancellationToken cancellationToken
    )
    {
        var request = new RimGovernor.Brain.GameOrders.Observations.BillsRequest { Scope = new RimGovernor.Brain.GameFacts.Observations.ReadScope { ExpectedIdentity = identity } };
        var snapshot = await Facade.ReadBillsAsync(request, cancellationToken).ConfigureAwait(false);
        var recipeCatalog = await catalog(cancellationToken).ConfigureAwait(false);
        var recipeReads = new Task<((List<P.GearRecipe> Recipes, string Def) Value, Exception? Error)>[snapshot.Benches.Count];
        for (var i = 0; i < snapshot.Benches.Count; i++)
        {
            recipeReads[i] = SharedReads.Attempt(GearReadRecipesAsync(identity, snapshot.Benches[i].Bench, recipeCatalog, cancellationToken));
        }
        var reads = new List<GearBenchRead>(snapshot.Benches.Count);
        for (var i = 0; i < snapshot.Benches.Count; i++)
        {
            var stack = snapshot.Benches[i];
            var (recipes, def) = SharedReads.Reply(await recipeReads[i].ConfigureAwait(false));
            var bills = GearBillsFromStack(stack, def, recipes, recipeCatalog);
            var bench = new P.GearBench
            {
                Observed = stack,
                Bills = EquatableArray.From(bills),
                Recipes = EquatableArray.From(recipes),
            };
            reads.Add(new GearBenchRead { Bench = bench });
        }
        reads.Sort((a, b) => string.CompareOrdinal(a.Bench.Id, b.Bench.Id));
        return reads;
    }

    /// <summary>
    /// Reads the currently available, unforbidden owned quantity for a bounded set
    /// of resource definitions. A requested definition the census has no row for is a known zero.
    /// </summary>
    internal async Task<IReadOnlyList<P.Stock>> ReadSupplyStockAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, IReadOnlyList<string> defNames, CancellationToken cancellationToken)
    {
        if (defNames.Count == 0)
        {
            return [];
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in defNames)
        {
            if (!names.Add(name))
            {
                throw BridgeContractException.Contract("duplicate supply stock def name");
            }
        }
        var seenOut = new HashSet<string>(StringComparer.Ordinal);
        var stocks = new List<P.Stock>(defNames.Count);
        // Native refuses a filter of more than GearMaxStockFilterDefNames names, so a long recipe list reads in chunks.
        for (var start = 0; start < defNames.Count; start += GearMaxStockFilterDefNames)
        {
            var chunk = defNames.Skip(start).Take(GearMaxStockFilterDefNames).ToList();
            await GearReadSupplyStockChunkAsync(identity, chunk, seenOut, stocks, cancellationToken).ConfigureAwait(false);
        }
        // A complete page groups the things present: a requested definition with no row is a known zero, not an unobserved stock.
        foreach (var name in defNames)
        {
            if (!seenOut.Contains(name))
            {
                stocks.Add(new P.Stock(new P.Resource(name), 0));
            }
        }
        return stocks;
    }

    private async Task GearReadSupplyStockChunkAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, List<string> defNames, HashSet<string> seenOut, List<P.Stock> output, CancellationToken cancellationToken)
    {
        var request = new RimGovernor.Brain.GameOrders.Observations.ListSuppliesRequest
        {
            Scope = new RimGovernor.Brain.GameFacts.Observations.ReadScope { ExpectedIdentity = identity },
            Filter = new RimGovernor.Brain.GameFacts.Observations.StockFilter
            {
                DefNames = EquatableArray.From(defNames),
                Category = RimGovernor.Brain.GameFacts.Observations.StockCategory.Haulable,
                Ownership = RimGovernor.Brain.GameFacts.Observations.StockOwnership.Ours,
                IncludeHeld = true,
            },
        };
        var snapshot = await Facade.ListSuppliesAsync(request, cancellationToken).ConfigureAwait(false);
        foreach (var row in snapshot.Stocks)
        {
            seenOut.Add(row.DefName);
            output.Add(new P.Stock(new P.Resource(row.DefName), row.OursUnforbidden));
        }
    }

    /// <summary>
    /// The bench's recipes with what the catalog says each is, and the bench definition. A recipe with a slot no def
    /// fills, or that no work giver serves at this bench, is never worked there and is left out.
    /// </summary>
    private async Task<(List<P.GearRecipe> Recipes, string Def)> GearReadRecipesAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, string bench, GearRecipeCatalogAdapter catalog, CancellationToken cancellationToken)
    {
        var request = new RimGovernor.Brain.GameOrders.Observations.RecipesRequest { Scope = new RimGovernor.Brain.GameFacts.Observations.ReadScope { ExpectedIdentity = identity }, BenchId = bench };
        var snapshot = await Facade.ReadRecipesAsync(request, cancellationToken).ConfigureAwait(false);
        var benchDef = snapshot.BenchDef;
        var recipes = new List<P.GearRecipe>(snapshot.Recipes.Count);
        foreach (var row in snapshot.Recipes)
        {
            var definition = row.Recipe;
            if (!catalog.RecipeFillable(definition) || !catalog.RecipeWorkedAt(definition, benchDef))
            {
                continue;
            }
            var role = catalog.RecipeRoleOf(definition);
            // What the recipe is and costs is the catalog's; the frame says only whether this bench offers it now.
            var products = catalog.RecipeProducts(definition);
            var ingredients = catalog.RecipeIngredients(definition);
            var requiredWork = catalog.RecipeWork(definition, benchDef);
            var workAmount = catalog.RecipeWorkAmount(definition);
            var mechKind = catalog.RecipeMechKind(definition);
            var kind = catalog.RecipeLedgerKind(definition, mechKind);
            var armory = catalog.RecipeArmoryTier(definition, benchDef);
            recipes.Add(
                new P.GearRecipe
                {
                    Access = new P.RecipeAccess.OnBench(row),
                    Role = role,
                    Products = EquatableArray.From(products),
                    Ingredients = ingredients,
                    RequiredWork = requiredWork,
                    WorkAmount = workAmount,
                    WorkSpeedStat = catalog.RecipeWorkSpeedStat(definition),
                    MechKind = mechKind,
                    Kind = kind,
                    Armory = armory,
                }
            );
        }
        return (recipes, benchDef);
    }

    private static List<P.GearBill> GearBillsFromStack(RimGovernor.Brain.GameFacts.Observations.BillStackRow stack, string benchDef, List<P.GearRecipe> recipes, GearRecipeCatalogAdapter catalog)
    {
        var products = new Dictionary<string, EquatableArray<P.Resource>>(StringComparer.Ordinal);
        var kinds = new Dictionary<string, P.LedgerBillKind>(StringComparer.Ordinal);
        foreach (var recipe in recipes)
        {
            products[recipe.Definition] = recipe.Products;
            kinds[recipe.Definition] = recipe.Kind;
        }
        var bills = new List<P.GearBill>(stack.Bills.Count);
        foreach (var bill in stack.Bills)
        {
            var recipeName = bill.Recipe;
            var role = catalog.RecipeRoleOf(recipeName);
            // An unrestricted bill carries no worker reference.
            products.TryGetValue(recipeName, out var billProducts);
            kinds.TryGetValue(recipeName, out var billKind);
            bills.Add(
                new P.GearBill
                {
                    Observed = bill,
                    Role = role,
                    Products = billProducts,
                    Kind = bill.BillClass == "Bill_GovernorRecipe" ? P.LedgerBillKind.ActivityExecution : billKind,
                    Spec = GearBillOrderSpec(bill, recipeName, benchDef),
                }
            );
        }
        return bills;
    }

    /// <summary>
    /// A bill read back as the work ledger's order spec, in the terms native identifies a bill
    /// by (recipe, ingredient filter, worker pin, repeat mode and count).
    /// </summary>
    private static P.OrderSpec GearBillOrderSpec(RimGovernor.Brain.GameFacts.Observations.BillState bill, string recipeName, string benchDef)
    {
        var repeat = ProductionRules.BillRepeatOf(bill.RepeatMode);
        switch (repeat)
        {
            case P.BillRepeat.Forever:
                return Spec(BillMode.ButcherForever, 0);
            case P.BillRepeat.Count:
                return Spec(BillMode.GearBatch, bill.RepeatCount);
            case P.BillRepeat.TargetCount:
                return Spec(BillMode.StockTarget, bill.TargetCount);
        }
        throw new DomainException($"undefined bill repeat {(int)repeat}");

        P.OrderSpec Spec(BillMode mode, int target) => new P.OrderSpec
        {
            Recipe = recipeName,
            BenchKind = benchDef,
            Mode = mode,
            Target = target,
            Worker = bill.Worker ?? "",
            Ingredients = bill.DefaultIngredients ? default : bill.IngredientFilter.AllowedDefNames,
        };
    }
}
