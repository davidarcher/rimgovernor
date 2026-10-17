using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.BuildingRuntime;

// Go internal/bridge gear_bench_census.go (ReadGearBenches, ReadSupplyStock).
public sealed partial class FacadeNativeClient
{
    /// <summary>Go <c>maxStockFilterDefNames</c>: native ListSupplies' StockFilter.DefNames cap.</summary>
    private const int GearMaxStockFilterDefNames = 256;

    /// <summary>
    /// Go <c>Client.ReadGearBenches</c>: a fresh, uncached census of every workbench's bill stack, its CAS token and
    /// its recipe catalog. <paramref name="catalog"/> is Go's <c>client.DefinitionCatalog(ctx, identity)</c>; it is
    /// awaited after the bills reply arrives, the order the Go read made the calls in.
    /// </summary>
    internal async Task<IReadOnlyList<GearBenchRead>> ReadGearBenchesAsync(
        PbCommon.Identity identity,
        Func<CancellationToken, Task<GearRecipeCatalogAdapter>> catalog,
        CancellationToken cancellationToken
    )
    {
        var request = new PbObservations.BillsRequest { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity } };
        var reply = await Facade.ReadBillsAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply.Outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }
        if (reply.Outcome.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }
        var snapshot = reply.Outcome.Observed!;
        var recipeCatalog = await catalog(cancellationToken).ConfigureAwait(false);
        var reads = new List<GearBenchRead>(snapshot.Benches.Count);
        foreach (var stack in snapshot.Benches)
        {
            var benchId = stack.Bench!.Id ?? "";
            var (recipes, def) = await GearReadRecipesAsync(identity, benchId, recipeCatalog, cancellationToken).ConfigureAwait(false);
            var bills = GearBillsFromStack(stack, def, recipes, recipeCatalog);
            var bench = new P.GearBench
            {
                Id = benchId,
                Def = def,
                WorkSpeed = GearBenchWorkSpeed(stack),
                Bills = Fact.Known(EquatableArray.From(bills)),
                Recipes = Fact.Known(EquatableArray.From(recipes)),
                Usable = stack.Usable is null ? Fact.Unknown<bool>() : Fact.Known(stack.Usable.GetValueOrDefault()),
            };
            reads.Add(new GearBenchRead { Token = stack.Snapshot?.Token ?? "", Bench = bench });
        }
        reads.Sort((a, b) => string.CompareOrdinal(a.Bench.Id, b.Bench.Id));
        return reads;
    }

    /// <summary>
    /// Go <c>Client.ReadSupplyStock</c>: reads the currently available, unforbidden owned quantity for a bounded set
    /// of resource definitions. A requested definition the census has no row for is a known zero.
    /// </summary>
    internal async Task<IReadOnlyList<P.Stock>> ReadSupplyStockAsync(PbCommon.Identity identity, IReadOnlyList<string> defNames, CancellationToken cancellationToken)
    {
        if (defNames.Count == 0)
        {
            return [];
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in defNames)
        {
            if (!BridgeFunctions.IsValidId(name) || !names.Add(name))
            {
                throw BridgeFunctions.Contract("invalid supply stock def name");
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
                stocks.Add(new P.Stock(new P.Resource(name), Fact.Known(0L)));
            }
        }
        return stocks;
    }

    /// <summary>Go <c>readSupplyStockChunk</c>.</summary>
    private async Task GearReadSupplyStockChunkAsync(PbCommon.Identity identity, List<string> defNames, HashSet<string> seenOut, List<P.Stock> output, CancellationToken cancellationToken)
    {
        var request = new PbObservations.ListSuppliesRequest
        {
            Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
            Filter = new PbObservations.StockFilter { DefNames = EquatableArray.From(defNames), Ownership = PbObservations.StockOwnership.Ours },
        };
        var reply = await Facade.ListSuppliesAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply.Outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }
        if (reply.Outcome.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }
        foreach (var row in reply.Outcome.Observed!.Stocks)
        {
            var name = row.Definition.DefName ?? "";
            seenOut.Add(name);
            output.Add(
                new P.Stock(
                    new P.Resource(name),
                    row.OursUnforbidden is { } available && available >= 0 ? Fact.Known(available) : Fact.Unknown<long>()
                )
            );
        }
    }

    /// <summary>Go <c>Client.readGearRecipes</c>: the bench's recipes with what the catalog says each is, and the bench definition.</summary>
    private async Task<(List<P.GearRecipe> Recipes, string Def)> GearReadRecipesAsync(PbCommon.Identity identity, string bench, GearRecipeCatalogAdapter catalog, CancellationToken cancellationToken)
    {
        var request = new PbObservations.RecipesRequest { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity }, BenchId = bench };
        var reply = await Facade.ReadRecipesAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply.Outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }
        if (reply.Outcome.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }
        var snapshot = reply.Outcome.Observed!;
        var benchDef = snapshot.BenchDef ?? "";
        var recipes = new List<P.GearRecipe>(snapshot.Recipes.Count);
        foreach (var row in snapshot.Recipes)
        {
            var definition = row.Recipe?.DefName ?? "";
            var role = catalog.RecipeRoleOf(definition);
            var available = row.AvailableNow is { } now ? Fact.Known(now) : Fact.Unknown<bool>();
            var availableOn = row.AvailableOnBench is { } onBench ? Fact.Known(onBench) : Fact.Unknown<bool>();
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
                    Definition = definition,
                    Role = role,
                    Available = available,
                    AvailableOn = availableOn,
                    Products = EquatableArray.From(products),
                    Ingredients = ingredients,
                    RequiredWork = requiredWork,
                    WorkAmount = workAmount,
                    MechKind = mechKind,
                    Kind = kind,
                    Armory = armory,
                }
            );
        }
        return (recipes, benchDef);
    }

    /// <summary>Go <c>benchWorkSpeed</c>: the stack's work-table speed factor; unknown when native sent none or a negative one.</summary>
    private static Fact<double> GearBenchWorkSpeed(PbObservations.BillStackRow stack) =>
        stack.WorkSpeed is { } speed && speed >= 0 ? Fact.Known(speed) : Fact.Unknown<double>();

    /// <summary>Go <c>gearBillsFromStack</c>.</summary>
    private static List<P.GearBill> GearBillsFromStack(PbObservations.BillStackRow stack, string benchDef, List<P.GearRecipe> recipes, GearRecipeCatalogAdapter catalog)
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
            var recipeName = bill.Recipe?.DefName ?? "";
            var role = catalog.RecipeRoleOf(recipeName);
            // An unrestricted bill carries no worker reference.
            products.TryGetValue(recipeName, out var billProducts);
            kinds.TryGetValue(recipeName, out var billKind);
            bills.Add(
                new P.GearBill
                {
                    Id = bill.Id ?? "",
                    Recipe = recipeName,
                    Role = role,
                    Active = bill.Suspended is not null && bill.Finished is not null ? Fact.Known(!bill.Suspended.GetValueOrDefault() && !bill.Finished.GetValueOrDefault()) : Fact.Unknown<bool>(),
                    Worker = Fact.Known(bill.Worker?.Id ?? ""),
                    Products = billProducts,
                    Spent = bill.Finished.GetValueOrDefault(),
                    Kind = bill.BillClass == "Bill_GovernorRecipe" ? P.LedgerBillKind.ActivityExecution : billKind,
                    Spec = GearBillOrderSpec(bill, recipeName, benchDef),
                }
            );
        }
        return bills;
    }

    /// <summary>
    /// Go <c>billOrderSpec</c>: a bill read back as the work ledger's order spec, in the terms native identifies a bill
    /// by (recipe, ingredient filter, worker pin, repeat mode and count). Unknown when the readback lacks the repeat
    /// mode, its count or whether the ingredient filter is the recipe's default.
    /// </summary>
    private static Fact<P.OrderSpec> GearBillOrderSpec(PbObservations.BillState bill, string recipeName, string benchDef)
    {
        if (bill.RepeatMode is null || bill.DefaultIngredients is null)
        {
            return Fact.Unknown<P.OrderSpec>();
        }
        BillMode mode;
        var target = 0;
        switch (BridgeFunctions.RepeatModeName(bill.RepeatMode.GetValueOrDefault()))
        {
            case "Forever":
                mode = BillMode.ButcherForever;
                break;
            case "RepeatCount":
                if (bill.RepeatCount is null)
                {
                    return Fact.Unknown<P.OrderSpec>();
                }
                mode = BillMode.GearBatch;
                target = bill.RepeatCount.GetValueOrDefault();
                break;
            case "TargetCount":
                if (bill.TargetCount is null)
                {
                    return Fact.Unknown<P.OrderSpec>();
                }
                mode = BillMode.StockTarget;
                target = bill.TargetCount.GetValueOrDefault();
                break;
            default:
                return Fact.Unknown<P.OrderSpec>();
        }
        var spec = new P.OrderSpec
        {
            Recipe = recipeName,
            BenchKind = benchDef,
            Mode = mode,
            Target = target,
            Worker = bill.Worker?.Id ?? "",
            Ingredients = bill.DefaultIngredients.GetValueOrDefault() ? default : EquatableArray.From(bill.IngredientFilter?.AllowedDefNames ?? default),
        };
        return Fact.Known(spec);
    }
}
