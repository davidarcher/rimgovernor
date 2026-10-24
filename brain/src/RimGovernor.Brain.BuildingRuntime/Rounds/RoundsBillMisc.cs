namespace RimGovernor.Brain.BuildingRuntime;


public sealed partial class Rounder
{
    /// <summary>
    /// The bills the ledger's declared batches open, with the recipe ingredient slots the catalog reads. A batch whose
    /// recipe the catalog has no row for, or has a slot no def fills, never starts and so opens no bill.
    /// </summary>
    internal async Task<IReadOnlyList<OpenBill>> OpenBillsAsync(GenerationSnapshot snapshot, CancellationToken ct)
    {
        if (Native is not IDefinitionSource source)
        {
            return [];
        }
        var orders = Ledger.DeclaredBatches().ToList();
        if (orders.Count == 0)
        {
            return [];
        }
        var catalog = await source.DefinitionCatalogAsync(Boundary.Identity(snapshot), ct).ConfigureAwait(false);
        var bills = new List<OpenBill>(orders.Count);
        foreach (var order in orders)
        {
            if (catalog.RecipeFillable(order.Recipe))
            {
                bills.Add(new OpenBill { Count = order.Target, Filter = order.Ingredients, Slots = catalog.RecipeIngredients(order.Recipe) });
            }
        }
        return bills;
    }
}

partial class Rounder
{
    /// <summary>
    /// The mech gestation facts of the review: the mechanitors and their controlled mechs, gestators, wastepacks
    /// and chargers. Null while the biotech census or the work pawns are unread.
    /// </summary>
    internal static async Task<MechGestation?> MechGestationAsync(object native, C.Identity identity, ColonyProjection projection, CancellationToken call)
    {
        if (projection.BiotechSection is not PbObservations.BiotechSection.Active { Facts: var colony } || !projection.WorkPawns.TryGetValue(out var pawns))
        {
            return null;
        }
        var g = new MechGestation { Catalog = projection.MechCatalog };
        var ids = new List<string>();
        var mechanitors = new List<MechanitorInput>();
        foreach (var p in pawns)
        {
            if (p.Biotech is PawnBiotechStatus.Active { Facts.Mechanitor: { } m })
            {
                mechanitors.Add(new MechanitorInput { Id = p.Id, Mechanitor = m });
                ids.AddRange(m.ControlledMechs);
            }
        }
        if (ids.Count > 0)
        {
            if (native is not FacadeNativeClient source)
            {
                throw new ControlException("mechGestation: native cannot read mechs");
            }
            g = g with { Mechs = await source.ReadMechsAsync(identity, ids, call).ConfigureAwait(false) };
        }
        if (projection.Facts.WorkRoster.TryGetValue(out var roster))
        {
            g = g with { Coverage = roster };
        }
        return g with { Mechanitors = mechanitors, Gestators = BiotechColonyReads.GestatorFacts(colony), Wastepacks = BiotechColonyReads.WastepackFacts(colony), Chargers = MechRules.MechChargersOf(colony) };
    }

    /// <summary>The part benches with each recipe's mech kind carried over from the gear census.</summary>
    internal static List<ProductionBench> MechBenches(IReadOnlyList<GearBench> gear)
    {
        var partBenches = PartBenches(gear);
        var kinds = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var bench in gear)
        {
            kinds[bench.Id] = bench.Recipes.ToDictionary(r => r.Definition, r => r.MechKind, StringComparer.Ordinal);
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
        var parts = SurgeryRules.SurgeryParts(SurgeryRules.SelectSurgery(pawns, new HashSet<PawnId>(), new SurgeryContext()).Wants);
        var chosen = SurgeryRules.ChosenElectives(pawns, ctx);
        if (parts.Count == 0 && chosen.Count == 0)
        {
            return (parts, []);
        }
        if (native is not FacadeNativeClient source)
        {
            return (parts, []);
        }
        var reads = (await source.ReadGearBenchesAsync(identity, call).ConfigureAwait(false));
        var benches = reads.Select(read => read.Bench).ToList();
        return SurgeryPartsOn(pawns, ctx, benches);
    }

    internal static (IReadOnlyList<SurgeryPart> Parts, IReadOnlyList<ProductionBench> Benches) SurgeryPartsOn(Fact<EquatableArray<CarePawn>> pawns, SurgeryContext ctx, IReadOnlyList<GearBench> benches)
    {
        var parts = SurgeryRules.SurgeryParts(SurgeryRules.SelectSurgery(pawns, new HashSet<PawnId>(), new SurgeryContext()).Wants).ToList();
        var chosen = SurgeryRules.ChosenElectives(pawns, ctx);
        if (parts.Count == 0 && chosen.Count == 0)
        {
            return (parts, []);
        }
        var productions = PartBenches(benches);
        parts.AddRange(SurgeryRules.ElectiveParts(chosen, SurgeryRules.FabricableParts(productions)));
        return (parts, productions);
    }

    /// <summary>The production benches the gear benches host, one row per bench.</summary>
    internal static List<ProductionBench> PartBenches(IReadOnlyList<GearBench> benches)
    {
        var output = new List<ProductionBench>();
        foreach (var gear in benches)
        {
            var benchRecipes = new List<ProductionRecipe>();
            var benchBills = new List<ExistingProductionBill>();
            foreach (var recipe in gear.Recipes)
            {
                // These rows feed part fabrication only, which never reads a recipe or a product as food.
                benchRecipes.Add(new ProductionRecipe
                {
                    Name = recipe.Definition,
                    Food = new RecipeFood.NotFood(),
                    Available = recipe.Available && recipe.AvailableOn,
                    Products = [.. recipe.Products.Select(product => new ProductionProduct { Name = product.Value, Food = new ProductFood.NotFood() })],
                });
            }
            foreach (var bill in gear.Bills)
            {
                benchBills.Add(bill.Existing);
            }
            output.Add(new ProductionBench { Id = gear.Id, Token = gear.Id, Usable = true, Recipes = benchRecipes, Bills = benchBills });
        }
        return output;
    }
}
