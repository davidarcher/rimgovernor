using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Host;

/// <summary>
/// The recipe facts <see cref="FacadeNativeClient.ReadGearBenchesAsync"/> asks the definition catalog for (the
/// <c>DefinitionCatalog.Recipe*</c> methods of Go's <c>bridge.DefinitionCatalog</c>). Each throws for a recipe the
/// catalog has no def for, as the Go methods returned an error.
/// </summary>
internal interface IGearRecipeCatalog
{
    /// <summary>What the recipe does by its def.</summary>
    RecipeRole RecipeRoleOf(string name);

    /// <summary>The resources the recipe makes.</summary>
    IReadOnlyList<P.Resource> RecipeProducts(string name);

    /// <summary>The ingredient alternatives the recipe consumes.</summary>
    Fact<EquatableArray<EquatableArray<P.Amount>>> RecipeIngredients(string name);

    /// <summary>The skill gates of the recipe at the bench definition.</summary>
    Fact<EquatableArray<P.WorkRequirement>> RecipeWork(string name, string bench);

    /// <summary>The work one unit takes.</summary>
    Fact<double> RecipeWorkAmount(string name);

    /// <summary>The PawnKindDef of the mech a gestation recipe makes, empty for every other recipe.</summary>
    string RecipeMechKind(string name);

    /// <summary>The declare-only class of the recipe by what it makes.</summary>
    P.LedgerBillKind RecipeLedgerKind(string name, string mechKind);

    /// <summary>The armory ladder rung of the recipe at the bench definition.</summary>
    P.ArmoryTier RecipeArmoryTier(string name, string bench);
}

// Go internal/bridge gear_bench_census.go (ReadGearBenches, ReadSupplyStock).
internal sealed partial class FacadeNativeClient
{
    /// <summary>Go <c>maxStockFilterDefNames</c>: native ListSupplies' StockFilter.DefNames cap.</summary>
    private const int GearMaxStockFilterDefNames = 256;

    /// <summary>Go <c>maxBillsPerBench</c> bound of <c>gearBillsFromStack</c> (15 bills).</summary>
    private const int GearMaxBills = 15;

    /// <summary>
    /// Go <c>Client.ReadGearBenches</c>: a fresh, uncached census of every workbench's bill stack, its CAS token and
    /// its recipe catalog. <paramref name="catalog"/> is Go's <c>client.DefinitionCatalog(ctx, identity)</c>; it is
    /// awaited after the bills reply validates, the order the Go read made the calls in.
    /// </summary>
    internal async Task<(IReadOnlyList<GearBenchRead> Value, BridgeResult Receipt)> ReadGearBenchesAsync(
        PbCommon.Identity identity,
        Func<CancellationToken, Task<IGearRecipeCatalog>> catalog,
        CancellationToken cancellationToken
    )
    {
        BridgeFunctions.ValidateIdentity(identity);
        var request = new PbObservations.BillsRequest { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity } };
        var reply = await Facade.ReadBillsAsync(request, cancellationToken).ConfigureAwait(false);
        BridgeFunctions.BuildingUnknown(reply);
        PbObservations.BillsSnapshot? snapshot;
        switch (reply.Outcome)
        {
            case PbObservations.BillsReplyOutcome.ObservedCase observed:
                snapshot = observed.Value;
                break;
            case PbObservations.BillsReplyOutcome.UnavailableCase unavailable:
                throw Unavailable(unavailable.Value);
            case PbObservations.BillsReplyOutcome.FailureCase failure:
                throw Failure(failure.Value);
            default:
                throw BridgeFunctions.Contract("missing bills outcome");
        }
        if (snapshot is null || !GearContextMatches(snapshot.Context, identity))
        {
            throw BridgeFunctions.Contract("invalid bills context");
        }
        var recipeCatalog = await catalog(cancellationToken).ConfigureAwait(false);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var reads = new List<GearBenchRead>(snapshot.Benches.Count);
        foreach (var stack in snapshot.Benches)
        {
            if (stack is null || stack.Bench is null || !BridgeFunctions.IsValidId(stack.Bench.Id ?? "") || seen.Contains(stack.Bench.Id ?? ""))
            {
                throw BridgeFunctions.Contract("invalid gear bench identity");
            }
            var benchId = stack.Bench.Id ?? "";
            if (stack.Snapshot is null || !BridgeFunctions.IsValidId(stack.Snapshot.Token ?? "") || (stack.Snapshot.EntityId ?? "") != benchId)
            {
                throw BridgeFunctions.Contract("invalid gear bench token");
            }
            seen.Add(benchId);
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
            reads.Add(new GearBenchRead { Token = stack.Snapshot.Token ?? "", Bench = bench });
        }
        reads.Sort((a, b) => string.CompareOrdinal(a.Bench.Id, b.Bench.Id));
        return (reads, NoReceipt);
    }

    /// <summary>
    /// Go <c>Client.ReadSupplyStock</c>: reads the currently available, unforbidden owned quantity for a bounded set
    /// of resource definitions. A requested definition the census has no row for is a known zero.
    /// </summary>
    internal async Task<(IReadOnlyList<P.Stock> Value, BridgeResult Receipt)> ReadSupplyStockAsync(PbCommon.Identity identity, IReadOnlyList<string> defNames, CancellationToken cancellationToken)
    {
        BridgeFunctions.ValidateIdentity(identity);
        if (defNames.Count == 0)
        {
            return ([], NoReceipt);
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
        return (stocks, NoReceipt);
    }

    /// <summary>Go <c>readSupplyStockChunk</c>.</summary>
    private async Task GearReadSupplyStockChunkAsync(PbCommon.Identity identity, List<string> defNames, HashSet<string> seenOut, List<P.Stock> output, CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(defNames, StringComparer.Ordinal);
        var request = new PbObservations.ListSuppliesRequest
        {
            Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
            Filter = new PbObservations.StockFilter { DefNames = EquatableArray.From(defNames), Ownership = PbObservations.StockOwnership.Ours },
        };
        var reply = await Facade.ListSuppliesAsync(request, cancellationToken).ConfigureAwait(false);
        BridgeFunctions.BuildingUnknown(reply);
        PbObservations.SuppliesSnapshot? snapshot;
        switch (reply.Outcome)
        {
            case PbObservations.ListSuppliesReplyOutcome.ObservedCase observed:
                snapshot = observed.Value;
                break;
            case PbObservations.ListSuppliesReplyOutcome.UnavailableCase unavailable:
                throw Unavailable(unavailable.Value);
            case PbObservations.ListSuppliesReplyOutcome.FailureCase failure:
                throw Failure(failure.Value);
            default:
                throw BridgeFunctions.Contract("missing supplies outcome");
        }
        if (snapshot is null || !GearContextMatches(snapshot.Context, identity))
        {
            throw BridgeFunctions.Contract("invalid supplies context");
        }
        if (snapshot.Stocks.Count > defNames.Count)
        {
            throw BridgeFunctions.Contract("incomplete supplies census");
        }
        foreach (var row in snapshot.Stocks)
        {
            var name = row?.Definition?.DefName ?? "";
            if (row is null || row.Definition is null || !BridgeFunctions.IsValidId(name) || !names.Contains(name) || seenOut.Contains(name))
            {
                throw BridgeFunctions.Contract("invalid resource stock row");
            }
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
    private async Task<(List<P.GearRecipe> Recipes, string Def)> GearReadRecipesAsync(PbCommon.Identity identity, string bench, IGearRecipeCatalog catalog, CancellationToken cancellationToken)
    {
        var request = new PbObservations.RecipesRequest { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity }, BenchId = bench };
        var reply = await Facade.ReadRecipesAsync(request, cancellationToken).ConfigureAwait(false);
        BridgeFunctions.BuildingUnknown(reply);
        PbObservations.RecipesSnapshot? snapshot;
        switch (reply.Outcome)
        {
            case PbObservations.RecipesReplyOutcome.ObservedCase observed:
                snapshot = observed.Value;
                break;
            case PbObservations.RecipesReplyOutcome.UnavailableCase unavailable:
                throw Unavailable(unavailable.Value);
            case PbObservations.RecipesReplyOutcome.FailureCase failure:
                throw Failure(failure.Value);
            default:
                throw BridgeFunctions.Contract("missing recipes outcome");
        }
        if (snapshot is null || !GearContextMatches(snapshot.Context, identity))
        {
            throw BridgeFunctions.Contract("invalid recipes context");
        }
        if (snapshot.Snapshot is null || (snapshot.Snapshot.EntityId ?? "") != bench)
        {
            throw BridgeFunctions.Contract("recipe snapshot bench mismatch");
        }
        var benchDef = snapshot.BenchDef ?? "";
        if (!BridgeFunctions.IsValidId(benchDef))
        {
            throw BridgeFunctions.Contract("recipe snapshot has no bench definition");
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        var recipes = new List<P.GearRecipe>(snapshot.Recipes.Count);
        foreach (var row in snapshot.Recipes)
        {
            var definition = row?.Recipe?.DefName ?? "";
            if (row is null || row.Recipe is null || !BridgeFunctions.IsValidId(definition) || names.Contains(definition))
            {
                throw BridgeFunctions.Contract("invalid gear recipe identity");
            }
            names.Add(definition);
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
    private static List<P.GearBill> GearBillsFromStack(PbObservations.BillStackRow stack, string benchDef, List<P.GearRecipe> recipes, IGearRecipeCatalog catalog)
    {
        if (stack.Bills.Count > GearMaxBills)
        {
            throw BridgeFunctions.Contract("bill stack exceeds bound");
        }
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
            var recipeName = bill?.Recipe?.DefName ?? "";
            if (bill is null || bill.Recipe is null || !BridgeFunctions.IsValidId(recipeName))
            {
                throw BridgeFunctions.Contract("invalid gear bill identity");
            }
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
                    Worker = BridgeFunctions.OptionalRef(bill.Worker) ? Fact.Known(bill.Worker?.Id ?? "") : Fact.Unknown<string>(),
                    Products = billProducts,
                    Spent = bill.Finished.GetValueOrDefault(),
                    Kind = billKind,
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
            Worker = BridgeFunctions.OptionalRef(bill.Worker) ? bill.Worker?.Id ?? "" : "",
            Ingredients = bill.DefaultIngredients.GetValueOrDefault() ? default : EquatableArray.From(bill.IngredientFilter?.AllowedDefNames ?? default),
        };
        return Fact.Known(spec);
    }

    /// <summary>Go <c>ValidateContext(ctx) == nil &amp;&amp; sameIdentity(ctx.Identity, identity)</c>.</summary>
    private static bool GearContextMatches(PbCommon.ObservationContext? context, PbCommon.Identity identity)
    {
        try
        {
            BridgeFunctions.ValidateContext(context);
        }
        catch (BridgeContractException)
        {
            return false;
        }
        return BridgeFunctions.SameIdentity(context!.Identity, identity);
    }
}
