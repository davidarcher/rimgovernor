namespace RimGovernor.Brain.Policy;

/// <summary>One missing part a restore wants: the items that would serve it, best first, and how much the want is worth.</summary>
public sealed record SurgeryPart
{
    public IReadOnlyList<Resource> Items { get; init; } = [];

    public double Value { get; init; }
}

public static partial class SurgeryRules
{
    /// <summary>
    /// The medical/surgery Need's bill purpose: one batch bill fabricating a part a restore operation lacks, at a bench
    /// whose recipe is researched and usable. ProductionBillContext.Parts carries the wants.
    /// </summary>
    public static readonly BillPurpose SurgeryPartBill = new("surgery_part");

    /// <summary>
    /// Turns the part-short wants into part demand, most valuable want first (the best part's tier times the
    /// body part's weight).
    /// </summary>
    public static IReadOnlyList<SurgeryPart> SurgeryParts(IReadOnlyList<SurgeryWant> wants)
    {
        var output = new List<SurgeryPart>();
        foreach (var want in wants)
        {
            if (want.Reason != SurgeryWantReason.PartShort)
            {
                continue;
            }
            var items = want.Items.Where(AdmissionRules.ValidResource).ToList();
            if (items.Count > 0)
            {
                output.Add(
                    new SurgeryPart
                    {
                        Value = want.Value,
                        Items = items,
                    }
                );
            }
        }
        return [.. output.OrderByDescending(p => p.Value)];
    }

    /// <summary>The part demand as resource targets: one unit per part of its best item, merged by item.</summary>
    public static IReadOnlyList<ResourceDemand> SurgeryPartDemand(IReadOnlyList<SurgeryPart> parts)
    {
        var output = new List<ResourceDemand>();
        var index = new Dictionary<Resource, int>();
        foreach (var part in parts)
        {
            var item = part.Items[0];
            if (index.TryGetValue(item, out var i))
            {
                output[i] = output[i] with { Count = output[i].Count + 1 };
                continue;
            }
            index[item] = output.Count;
            output.Add(new ResourceDemand { Key = new ResourceKey { Def = item }, Count = 1 });
        }
        return output;
    }

    /// <summary>The items some usable bench has a researched recipe for, by the recipe producing each.</summary>
    public static HashSet<Resource> FabricableParts(IReadOnlyList<ProductionBench> benches)
    {
        var output = new HashSet<Resource>();
        foreach (var bench in benches)
        {
            if (!bench.Usable)
            {
                continue;
            }
            foreach (var recipe in bench.Recipes)
            {
                if (recipe.Available)
                {
                    foreach (var product in recipe.Products)
                    {
                        output.Add(new Resource(product.Name));
                    }
                }
            }
        }
        return output;
    }

    /// <summary>The parts no item of which can be fabricated: the ones a trader must supply.</summary>
    public static IReadOnlyList<SurgeryPart> TradeSurgeryParts(IReadOnlyList<SurgeryPart> parts, IReadOnlySet<Resource> fabricable)
    {
        var output = new List<SurgeryPart>();
        foreach (var part in parts)
        {
            var made = false;
            foreach (var item in part.Items)
            {
                made = made || fabricable.Contains(item);
            }
            if (!made)
            {
                output.Add(part);
            }
        }
        return output;
    }

    /// <summary>
    /// The parts a trader must supply: the served parts no bench can fabricate (TradeSurgeryParts) and, when
    /// none of those is pending, each chosen elective's part provided no item of it can be fabricated (the bill
    /// path, ElectiveParts, takes it otherwise). Silver bounds the purchase through the trade's reserve.
    /// </summary>
    public static IReadOnlyList<SurgeryPart> SurgeryPurchaseParts(
        EquatableArray<CarePawn> pawns,
        SurgeryContext ctx,
        IReadOnlyList<SurgeryPart> served,
        IReadOnlySet<Resource> fabricable
    )
    {
        var output = TradeSurgeryParts(served, fabricable);
        var unmade = ChosenElectives(pawns, ctx).Where(w => FabricableItems(w.Items, fabricable).Count == 0).ToList();
        return [.. output, .. SurgeryParts(unmade)];
    }

    /// <summary>Adds the parts only a trader can supply to the trade need; an unknown need stays unknown.</summary>
    public static TradeNeed SurgeryTradeNeed(TradeNeed need, IReadOnlyList<SurgeryPart> parts)
    {
        var n = need;
        if (parts.Count == 0)
        {
            return need;
        }
        return n with { SurgeryParts = [.. n.SurgeryParts, .. parts] };
    }

    /// <summary>
    /// Picks the highest-priority part's best fabricable item. A part whose items already have a bill on some
    /// bench is in production and gets no second one. When it picks nothing the gap names why for the
    /// highest-priority part it could not serve: nothing wanted, already in production, no usable researched
    /// recipe, or every bench's bill list full.
    /// </summary>
    public static (BillSelection Selection, BillGap Gap) SelectSurgeryPartBill(IReadOnlyList<ProductionBench> benches, IReadOnlyList<SurgeryPart> parts)
    {
        if (parts.Count == 0)
        {
            return (new BillSelection(), BillGap.NothingWanted);
        }
        var billed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bench in benches)
        {
            foreach (var bill in bench.Bills)
            {
                if (bill.Active)
                {
                    billed.Add(bill.Recipe);
                }
            }
        }
        var gap = BillGap.None;
        foreach (var part in parts)
        {
            BillSelection? pick = null;
            var inProduction = false;
            var fabricable = false;
            foreach (var item in part.Items)
            {
                foreach (var bench in benches)
                {
                    foreach (var recipe in bench.Recipes)
                    {
                        if (!ProducesItem(recipe, item))
                        {
                            continue;
                        }
                        inProduction = inProduction || billed.Contains(recipe.Name);
                        var serves = bench.Usable && FoodForecastRules.FoodId(bench.Token) && recipe.Available;
                        fabricable = fabricable || serves;
                        if (pick is null && serves && bench.Bills.Count < 15)
                        {
                            pick = new BillSelection
                            {
                                Bench = bench.Id,
                                Recipe = recipe.Name,
                                Mode = BillMode.GearBatch,
                                Target = 1,
                            };
                        }
                    }
                }
            }
            if (!inProduction && pick is not null)
            {
                return (pick, BillGap.None);
            }
            if (gap == BillGap.None)
            {
                gap = inProduction ? BillGap.InProduction : !fabricable ? BillGap.NoRecipe : BillGap.BenchFull;
            }
        }
        return (new BillSelection(), gap);
    }

    /// <summary>
    /// Each patient's elective whose part is missing: their best elective upgrade a doctor can perform within
    /// ElectiveFailureCap, ranked as SelectSurgery ranks (gain over the natural part x part weight x role weight,
    /// ties by pawn id then part) but over every elective, stocked or not. Each is a PartShort want whose Items
    /// are that part's recipes' items, best first. A patient with a served operation, a queued bill or a
    /// stocked option of their best part wants nothing (SelectSurgery queues the stocked one), and a part item
    /// already wanted by a higher-ranked patient is not wanted twice. A health goal's operation (an implant included)
    /// takes the place of the patient's best elective and outranks every other. The purchase path and ElectiveParts both
    /// read this.
    /// </summary>
    public static IReadOnlyList<SurgeryWant> ChosenElectives(EquatableArray<CarePawn> pawns, SurgeryContext ctx)
    {
        var rows = pawns;
        if (!ctx.HospitalBed)
        {
            return [];
        }
        var profiles = new Dictionary<PawnId, PawnProfile>();
        foreach (var p in ctx.Profiles)
        {
            profiles[p.Id] = p;
        }
        var wants = new List<SurgeryWant>();
        foreach (var pawn in rows)
        {
            if (pawn.Dead || pawn.QueuedSurgeries > 0)
            {
                continue;
            }
            var ops = pawn.Operations;
            if (ops.Any(op => ServedSurgery(pawn, op) > 0))
            {
                continue;
            }
            // A health goal's operation replaces the patient's own elective (#3508): it wants its part while none is stocked.
            var goalOps = GoalOperations(ops, GoalRecipes(ctx, pawn.Id));
            if (goalOps.Count > 0)
            {
                if (!goalOps.Any(op => op.Operation.IngredientsOnMap))
                {
                    wants.Add(new SurgeryWant
                    {
                        Pawn = pawn.Id,
                        Part = PartOf(goalOps[0].Operation),
                        Reason = SurgeryWantReason.PartShort,
                        Value = GoalSurgeryValue,
                        Items = [.. goalOps.Where(op => !op.Item.IsEmpty).Select(op => op.Item).Distinct()],
                    });
                }
                continue;
            }
            var parts = new Dictionary<int, List<CareOperation>>();
            var order = new List<int>();
            foreach (var op in ops)
            {
                // An elective upgrade always names its part.
                if (!ElectiveUpgrade(op) || !SurgeryAcceptable(op.Operation, ElectiveFailureCap))
                {
                    continue;
                }
                var part = PartOf(op.Operation);
                if (!parts.TryGetValue(part, out var group))
                {
                    order.Add(part);
                    group = [];
                    parts[part] = group;
                }
                group.Add(op);
            }
            order.Sort();
            SurgeryWant? best = null;
            var bestStocked = false;
            foreach (var part in order)
            {
                var group = parts[part].OrderByDescending(OpTier).ToList();
                var name = group[0].Operation.Part?.DefName ?? "";
                var weight = PartWeight(name);
                if (profiles.TryGetValue(pawn.Id, out var profile))
                {
                    weight *= PawnRules.UpgradeRoleWeight(profile, name);
                }
                var value = (group[0].Tier - 1) * weight;
                if (best is not null && value <= best.Value)
                {
                    continue;
                }
                var items = new List<Resource>();
                bestStocked = false;
                foreach (var op in group)
                {
                    if (!op.Item.IsEmpty)
                    {
                        items.Add(op.Item);
                    }
                    if (op.Operation.IngredientsOnMap)
                    {
                        bestStocked = true;
                    }
                }
                best = new SurgeryWant
                {
                    Pawn = pawn.Id,
                    Part = part,
                    Reason = SurgeryWantReason.PartShort,
                    Value = value,
                    Items = items,
                };
            }
            if (best is not null && !bestStocked)
            {
                wants.Add(best);
            }
        }
        var claimed = new HashSet<Resource>();
        var output = new List<SurgeryWant>();
        foreach (var want in wants.OrderByDescending(w => w.Value).ThenBy(w => w.Pawn).ThenBy(w => w.Part))
        {
            if (want.Items.Any(claimed.Contains))
            {
                continue;
            }
            foreach (var item in want.Items)
            {
                claimed.Add(item);
            }
            output.Add(want);
        }
        return output;
    }

    /// <summary>The valid items some usable bench can fabricate, in order.</summary>
    private static List<Resource> FabricableItems(IReadOnlyList<Resource> items, IReadOnlySet<Resource>? fabricable)
    {
        var output = new List<Resource>();
        foreach (var item in items)
        {
            if ((fabricable?.Contains(item) ?? false) && AdmissionRules.ValidResource(item))
            {
                output.Add(item);
            }
        }
        return output;
    }

    /// <summary>
    /// Each chosen elective's part demand for the bill path: one SurgeryPart of its fabricable items. A
    /// part nothing fabricates yields none here (the purchase path supplies it). Callers append it after the
    /// served parts, which win any conflict.
    /// </summary>
    public static IReadOnlyList<SurgeryPart> ElectiveParts(IReadOnlyList<SurgeryWant> wants, IReadOnlySet<Resource> fabricable) =>
        SurgeryParts([.. wants.Select(w => w with { Items = FabricableItems(w.Items, fabricable) })]);

    private static bool ProducesItem(ProductionRecipe recipe, Resource item)
    {
        foreach (var product in recipe.Products)
        {
            if (new Resource(product.Name) == item)
            {
                return true;
            }
        }
        return false;
    }
}
