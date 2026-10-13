namespace RimGovernor.Brain.BuildingRuntime;

// Go: rounds_bill_demand.go, rounds_bill_mech.go and rounds_bill_surgery.go.

/// <summary>The native mech read of the mech gestation review.</summary>
public interface IMechSource
{
    Task<IReadOnlyList<MechInput>> ReadMechsAsync(C.Identity identity, IReadOnlyList<string> ids, CancellationToken ct);
}

public sealed partial class Rounder
{
    /// <summary>The bills the ledger's declared batches open, with the recipe ingredient slots the catalog reads.</summary>
    internal async Task<IReadOnlyList<OpenBill>> OpenBillsAsync(GenerationSnapshot snapshot, CancellationToken ct)
    {
        if (Native is not IDefinitionSource source)
        {
            return [];
        }
        var bills = new List<OpenBill>();
        foreach (var order in Ledger.DeclaredBatches())
        {
            bills.Add(new OpenBill { Recipe = order.Recipe, Count = order.Target, Filter = order.Ingredients });
        }
        if (bills.Count == 0)
        {
            return [];
        }
        var catalog = await source.DefinitionCatalogAsync(Boundary.Identity(snapshot), ct).ConfigureAwait(false) ?? throw new InvalidOperationException("native served no definition catalog");
        var withSlots = new List<OpenBill>(bills.Count);
        foreach (var bill in bills)
        {
            try
            {
                withSlots.Add(bill with { Slots = catalog.RecipeIngredients(bill.Recipe) });
            }
            catch (DomainException)
            {
                withSlots.Add(bill with { Slots = Unknown<EquatableArray<EquatableArray<Amount>>>() });
            }
        }
        return withSlots;
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>
    /// The mech gestation facts of the review: the mechanitors and their controlled mechs, gestators, wastepacks
    /// and chargers. Null while the biotech census or the work pawns are unread.
    /// </summary>
    internal static async Task<MechGestation?> MechGestationAsync(object native, C.Identity identity, ColonyProjection projection, CancellationToken call)
    {
        if (!projection.Biotech.TryGetValue(out var colony) || !projection.WorkPawns.TryGetValue(out var pawns))
        {
            return null;
        }
        var g = new MechGestation { Catalog = projection.MechCatalog };
        var ids = new List<string>();
        var mechanitors = new List<MechanitorInput>();
        foreach (var p in pawns)
        {
            if (!p.Biotech.TryGetValue(out var bt))
            {
                continue;
            }
            if (bt.Mechanitor.TryGetValue(out var m) && m is not null)
            {
                mechanitors.Add(new MechanitorInput { Id = p.Id, Mechanitor = m });
                ids.AddRange(m.ControlledMechs);
            }
        }
        if (ids.Count > 0)
        {
            if (native is not IMechSource source)
            {
                throw new ControlException("mechGestation: native cannot read mechs");
            }
            g = g with { Mechs = await source.ReadMechsAsync(identity, ids, call).ConfigureAwait(false) };
        }
        if (projection.Facts.WorkRoster.TryGetValue(out var roster))
        {
            g = g with { Coverage = roster };
        }
        var gestators = new List<GestatorFact>();
        foreach (var x in colony.Gestators)
        {
            gestators.Add(new GestatorFact { Id = x.ID, Active = Known(x.BillID.IsKnown), WasteCount = x.WasteCount });
        }
        var wastepacks = new List<WastepackFact>();
        foreach (var x in colony.Wastepacks)
        {
            wastepacks.Add(new WastepackFact { Count = x.Count, Frozen = x.Frozen, InAtomizer = x.InAtomizer });
        }
        return g with { Mechanitors = mechanitors, Gestators = gestators, Wastepacks = wastepacks, Chargers = colony.MechChargerRows() };
    }

    /// <summary>The part benches with each recipe's mech kind carried over from the gear census.</summary>
    internal static List<ProductionBench> MechBenches(IReadOnlyList<GearBench> gear)
    {
        var partBenches = PartBenches(gear);
        var kinds = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var bench in gear)
        {
            if (!bench.Recipes.TryGetValue(out var recipes))
            {
                continue;
            }
            kinds[bench.Id] = recipes.ToDictionary(r => r.Definition, r => r.MechKind, StringComparer.Ordinal);
        }
        var benches = new List<ProductionBench>(partBenches.Count);
        foreach (var bench in partBenches)
        {
            var recipes = new List<ProductionRecipe>(bench.Recipes.Count);
            foreach (var recipe in bench.Recipes)
            {
                recipes.Add(recipe with { MechKind = kinds.TryGetValue(bench.Id, out var byRecipe) && byRecipe.TryGetValue(recipe.Name, out var kind) ? kind : "" });
            }
            benches.Add(bench with { Recipes = recipes });
        }
        return benches;
    }

    /// <summary>The surgery parts the care pawns want and the production benches that could fabricate them.</summary>
    internal static async Task<(IReadOnlyList<SurgeryPart> Parts, IReadOnlyList<ProductionBench> Benches)> SurgeryPartDemandAsync(object native, C.Identity identity, Fact<EquatableArray<CarePawn>> pawns, SurgeryContext ctx, CancellationToken call)
    {
        var parts = PolicyApi.SurgeryParts(PolicyApi.SelectSurgery(pawns, new HashSet<PawnId>(), new SurgeryContext()).Wants);
        var chosen = PolicyApi.ChosenElective(pawns, ctx).Ok;
        if (parts.Count == 0 && !chosen)
        {
            return (parts, []);
        }
        if (native is not IArtBenchSource source)
        {
            return (parts, []);
        }
        var reads = (await source.ReadGearBenchesAsync(identity, call).ConfigureAwait(false)).Reads;
        var benches = reads.Select(read => read.Bench).ToList();
        return SurgeryPartsOn(pawns, ctx, benches);
    }

    internal static (IReadOnlyList<SurgeryPart> Parts, IReadOnlyList<ProductionBench> Benches) SurgeryPartsOn(Fact<EquatableArray<CarePawn>> pawns, SurgeryContext ctx, IReadOnlyList<GearBench> benches)
    {
        var parts = PolicyApi.SurgeryParts(PolicyApi.SelectSurgery(pawns, new HashSet<PawnId>(), new SurgeryContext()).Wants).ToList();
        var (elective, chosen) = PolicyApi.ChosenElective(pawns, ctx);
        if (parts.Count == 0 && !chosen)
        {
            return (parts, []);
        }
        var productions = PartBenches(benches);
        parts.AddRange(PolicyApi.ElectiveParts(elective, chosen, PolicyApi.FabricableParts(productions)));
        return (parts, productions);
    }

    /// <summary>The production benches the gear benches host, one row per bench whose recipes and bills are both read.</summary>
    internal static List<ProductionBench> PartBenches(IReadOnlyList<GearBench> benches)
    {
        var output = new List<ProductionBench>();
        foreach (var gear in benches)
        {
            if (!gear.Recipes.TryGetValue(out var recipes) || !gear.Bills.TryGetValue(out var bills))
            {
                continue;
            }
            var benchRecipes = new List<ProductionRecipe>();
            var benchBills = new List<ExistingProductionBill>();
            foreach (var recipe in recipes)
            {
                var row = new ProductionRecipe { Name = recipe.Definition };
                var ak = recipe.Available.TryGetValue(out var available);
                var ok = recipe.AvailableOn.TryGetValue(out var on);
                if (ak && ok)
                {
                    row = row with { Available = Known(available && on) };
                }
                row = row with { Products = [.. recipe.Products.Select(product => new ProductionProduct { Name = product.Value })] };
                benchRecipes.Add(row);
            }
            foreach (var bill in bills)
            {
                benchBills.Add(new ExistingProductionBill { Id = bill.Id, Recipe = bill.Recipe, Worker = bill.Worker, Active = bill.Active });
            }
            output.Add(new ProductionBench { Id = gear.Id, Token = Known(gear.Id), Usable = Known(true), Recipes = benchRecipes, Bills = benchBills });
        }
        return output;
    }
}
