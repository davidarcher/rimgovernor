namespace RimGovernor.Brain.Runtime;


public sealed partial class ColonyReview
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

partial class ColonyReview
{
    /// <summary>
    /// The mech gestation facts of the review: the mechanitors and their controlled mechs, gestators, wastepacks
    /// and chargers. Null while the biotech census or the work pawns are unread.
    /// </summary>
    internal static async Task<MechGestation?> MechGestationAsync(object native, RimGovernor.Brain.GameFacts.Common.Identity identity, ColonyReading projection, CancellationToken call)
    {
        var pawns = projection.Facts.ForecastObserved.WorkPawns.Pawns;
        if (projection.Snapshot.Biotech is not RimGovernor.Brain.GameFacts.Observations.BiotechSection.Active { Facts: var colony })
        {
            return null;
        }
        var g = new MechGestation { Catalog = projection.Facts.MechCatalog };
        var ids = new List<string>();
        var mechanitors = new List<MechanitorInput>();
        foreach (var p in pawns)
        {
            if (p.Biotech is PawnBiotechStatus.Active { Facts.Mechanitor: { } m })
            {
                mechanitors.Add(new MechanitorInput { Observed = p.Observed });
                ids.AddRange(m.ControlledMechs.Select(mech => mech.Id));
            }
        }
        if (ids.Count > 0)
        {
            if (native is not GameClient source)
            {
                throw new ControlException("mechGestation: native cannot read mechs");
            }
            g = g with { Mechs = await source.ReadMechsAsync(identity, ids, call).ConfigureAwait(false) };
        }
        if (projection.Facts.WorkRoster is WorkRosterRead.Read { Coverage: var roster })
        {
            g = g with { Coverage = roster };
        }
        return g with { Mechanitors = mechanitors, Gestators = colony.Gestators, Wastepacks = colony.Wastepacks, Chargers = colony.Chargers };
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
    internal static async Task<(IReadOnlyList<SurgeryPart> Parts, IReadOnlyList<ProductionBench> Benches)> SurgeryPartDemandAsync(object native, RimGovernor.Brain.GameFacts.Common.Identity identity, EquatableArray<CarePawn> pawns, SurgeryContext ctx, CancellationToken call)
    {
        var parts = SurgeryRules.SurgeryParts(SurgeryRules.SelectSurgery(pawns, new HashSet<PawnId>(), new SurgeryContext()).Wants);
        var chosen = SurgeryRules.ChosenElectives(pawns, ctx);
        if (parts.Count == 0 && chosen.Count == 0)
        {
            return (parts, []);
        }
        if (native is not GameClient source)
        {
            return (parts, []);
        }
        var reads = (await source.ReadGearBenchesAsync(identity, call).ConfigureAwait(false));
        var benches = reads.Select(read => read.Bench).ToList();
        return SurgeryPartsOn(pawns, ctx, benches);
    }

    internal static (IReadOnlyList<SurgeryPart> Parts, IReadOnlyList<ProductionBench> Benches) SurgeryPartsOn(EquatableArray<CarePawn> pawns, SurgeryContext ctx, IReadOnlyList<GearBench> benches)
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
                    Access = recipe.Access,
                    Food = new RecipeFood.NotFood(),
                    Products = [.. recipe.Products.Select(product => new ProductionProduct { Food = new ProductFood.CatalogProduct(product.Value) })],
                });
            }
            foreach (var bill in gear.Bills)
            {
                if (!bill.Spent) benchBills.Add(bill.Existing);
            }
            output.Add(new ProductionBench { Observed = gear.Observed, Use = new ProductionUse.Ordinary(), Recipes = benchRecipes, Bills = benchBills });
        }
        return output;
    }
}
