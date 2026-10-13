namespace RimGovernor.Brain.BuildingRuntime;

// Go: rounds_bill_demand.go, rounds_bill_mech.go and rounds_bill_surgery.go.

/// <summary>The native mech read of the mech gestation review.</summary>
public interface IMechSource
{
    IReadOnlyList<MechInput> ReadMechs(C.Identity identity, IReadOnlyList<string> ids, CancellationToken ct);
}

public sealed partial class Rounder
{
    /// <summary>The bills the ledger's declared batches open, with the recipe ingredient slots the catalog reads.</summary>
    internal IReadOnlyList<OpenBill> OpenBills(GenerationSnapshot snapshot, CancellationToken ct)
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
        var catalog = source.DefinitionCatalog(Boundary.Identity(snapshot), ct);
        foreach (var bill in bills)
        {
            try
            {
                bill.Slots = catalog.RecipeIngredients(bill.Recipe);
            }
            catch (DomainException)
            {
                bill.Slots = Unknown<IReadOnlyList<IReadOnlyList<Amount>>>();
            }
        }
        return bills;
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>
    /// The mech gestation facts of the review: the mechanitors and their controlled mechs, gestators, wastepacks
    /// and chargers. Null while the biotech census or the work pawns are unread.
    /// </summary>
    internal static MechGestation? MechGestation(object native, C.Identity identity, ColonyProjection projection, CancellationToken call)
    {
        if (!projection.Biotech.TryGetValue(out var colony) || !projection.WorkPawns.TryGetValue(out var pawns))
        {
            return null;
        }
        var g = new MechGestation { Catalog = projection.MechCatalog };
        var ids = new List<string>();
        foreach (var p in pawns)
        {
            if (!p.Biotech.TryGetValue(out var bt))
            {
                continue;
            }
            if (bt.Mechanitor.TryGetValue(out var m) && m is not null)
            {
                g.Mechanitors.Add(new MechanitorInput { Id = p.Id, PawnMechanitor = m });
                ids.AddRange(m.ControlledMechs);
            }
        }
        if (ids.Count > 0)
        {
            if (native is not IMechSource source)
            {
                throw new ControlException("mechGestation: native cannot read mechs");
            }
            g.Mechs = source.ReadMechs(identity, ids, call);
        }
        if (projection.Facts.WorkRoster.TryGetValue(out var roster))
        {
            g.Coverage = roster;
        }
        foreach (var x in colony.Gestators)
        {
            g.Gestators.Add(new GestatorFact { Id = x.Id, Active = Known(x.BillId.IsKnown), WasteCount = x.WasteCount });
        }
        foreach (var x in colony.Wastepacks)
        {
            g.Wastepacks.Add(new WastepackFact { Count = x.Count, Frozen = x.Frozen, InAtomizer = x.InAtomizer });
        }
        g.Chargers = colony.MechChargerRows();
        return g;
    }

    /// <summary>The part benches with each recipe's mech kind carried over from the gear census.</summary>
    internal static List<ProductionBench> MechBenches(IReadOnlyList<GearBench> gear)
    {
        var benches = PartBenches(gear);
        var kinds = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var bench in gear)
        {
            if (!bench.Recipes.TryGetValue(out var recipes))
            {
                continue;
            }
            kinds[bench.Id] = recipes.ToDictionary(r => r.Definition, r => r.MechKind, StringComparer.Ordinal);
        }
        foreach (var bench in benches)
        {
            foreach (var recipe in bench.Recipes)
            {
                recipe.MechKind = kinds.TryGetValue(bench.Id, out var byRecipe) && byRecipe.TryGetValue(recipe.Name, out var kind) ? kind : "";
            }
        }
        return benches;
    }

    /// <summary>The surgery parts the care pawns want and the production benches that could fabricate them.</summary>
    internal static (IReadOnlyList<SurgeryPart> Parts, IReadOnlyList<ProductionBench> Benches) SurgeryPartDemand(object native, C.Identity identity, Fact<IReadOnlyList<CarePawn>> pawns, SurgeryContext ctx, CancellationToken call)
    {
        var parts = PolicyApi.SurgeryParts(PolicyApi.SelectSurgery(pawns, [], new SurgeryContext()).Wants);
        var chosen = PolicyApi.ChosenElective(pawns, ctx).Chosen;
        if ((parts.Count == 0 && !chosen) || native is not IArtBenchSource source)
        {
            return (parts, []);
        }
        var reads = source.ReadGearBenches(identity, call).Reads;
        var benches = reads.Select(read => read.Bench).ToList();
        return SurgeryPartsOn(pawns, ctx, benches);
    }

    internal static (IReadOnlyList<SurgeryPart> Parts, IReadOnlyList<ProductionBench> Benches) SurgeryPartsOn(Fact<IReadOnlyList<CarePawn>> pawns, SurgeryContext ctx, IReadOnlyList<GearBench> benches)
    {
        var parts = PolicyApi.SurgeryParts(PolicyApi.SelectSurgery(pawns, [], new SurgeryContext()).Wants).ToList();
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
            var bench = new ProductionBench { Id = gear.Id, Token = Known(gear.Id), Usable = Known(true) };
            foreach (var recipe in recipes)
            {
                var row = new ProductionRecipe { Name = recipe.Definition };
                var ak = recipe.Available.TryGetValue(out var available);
                var ok = recipe.AvailableOn.TryGetValue(out var on);
                if (ak && ok)
                {
                    row.Available = Known(available && on);
                }
                foreach (var product in recipe.Products)
                {
                    row.Products.Add(new ProductionProduct { Name = product.Value });
                }
                bench.Recipes.Add(row);
            }
            foreach (var bill in bills)
            {
                bench.Bills.Add(new ExistingProductionBill { Id = bill.Id, Recipe = bill.Recipe, Worker = bill.Worker, Active = bill.Active });
            }
            output.Add(bench);
        }
        return output;
    }
}
