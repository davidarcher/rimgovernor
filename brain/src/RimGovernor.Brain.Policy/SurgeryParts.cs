namespace RimGovernor.Brain.Policy;

/// <summary>One missing part a restore wants: the items that would serve it, best first, and how much the want is worth.</summary>
public sealed record SurgeryPart
{
    public PawnId Pawn { get; init; }

    public int Part { get; init; }

    public IReadOnlyList<Resource> Items { get; init; } = [];

    public double Value { get; init; }
}

public static partial class PolicyFunctions
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
            var items = want.Items.Where(ValidResource).ToList();
            if (items.Count > 0)
            {
                output.Add(
                    new SurgeryPart
                    {
                        Pawn = want.Pawn,
                        Part = want.Part,
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
            if (!bench.Usable.TryGetValue(out var usable) || !usable)
            {
                continue;
            }
            foreach (var recipe in bench.Recipes)
            {
                if (recipe.Available.TryGetValue(out var available) && available)
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
        Fact<EquatableArray<CarePawn>> pawns,
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
    public static Fact<TradeNeed> SurgeryTradeNeed(Fact<TradeNeed> need, IReadOnlyList<SurgeryPart> parts)
    {
        if (!need.TryGetValue(out var n) || parts.Count == 0)
        {
            return need;
        }
        return Known(n with { SurgeryParts = [.. n.SurgeryParts, .. parts] });
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
                if (!bill.Active.TryGetValue(out var active) || active)
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
                    var uk = bench.Usable.TryGetValue(out var usable);
                    var tk = bench.Token.TryGetValue(out var token);
                    foreach (var recipe in bench.Recipes)
                    {
                        if (!ProducesItem(recipe, item))
                        {
                            continue;
                        }
                        inProduction = inProduction || billed.Contains(recipe.Name);
                        var ak = recipe.Available.TryGetValue(out var available);
                        var serves = uk && usable && tk && FoodId(token!) && ak && available;
                        fabricable = fabricable || serves;
                        if (pick is null && serves && bench.Bills.Count < 15)
                        {
                            pick = new BillSelection
                            {
                                Bench = bench.Id,
                                Recipe = recipe.Name,
                                Token = token!,
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
    /// ties by pawn id then part) but over every elective, stocked or not. Each is a PartShort want whose Options
    /// and Items are that part's recipes, best first. A patient with a served operation, a queued bill or a
    /// stocked option of their best part wants nothing (SelectSurgery queues the stocked one), and a part item
    /// already wanted by a higher-ranked patient is not wanted twice. The purchase path and ElectiveParts both
    /// read this.
    /// </summary>
    public static IReadOnlyList<SurgeryWant> ChosenElectives(Fact<EquatableArray<CarePawn>> pawns, SurgeryContext ctx)
    {
        pawns.TryGetValue(out var rows);
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
            if (!pawn.Dead.TryGetValue(out var dead) || dead)
            {
                continue;
            }
            if (!pawn.QueuedSurgeries.TryGetValue(out var queued) || queued > 0)
            {
                continue;
            }
            if (!pawn.Operations.TryGetValue(out var ops))
            {
                continue;
            }
            var served = false;
            foreach (var op in ops)
            {
                var (weight, known) = ServedSurgery(pawn, op);
                served = served || !known || weight > 0;
            }
            if (served)
            {
                continue;
            }
            var parts = new Dictionary<int, List<SurgeryOperation>>();
            var order = new List<int>();
            foreach (var op in ops)
            {
                var pk = op.PartIndex.TryGetValue(out var part);
                if (!op.Recipe.IsKnown || !pk || !ElectiveUpgrade(op) || !SurgeryAcceptable(op, ElectiveFailureCap))
                {
                    continue;
                }
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
                group[0].PartDefName.TryGetValue(out var name);
                var weight = PartWeight(name ?? "");
                if (profiles.TryGetValue(pawn.Id, out var profile))
                {
                    weight *= UpgradeRoleWeight(profile, name ?? "");
                }
                group[0].Recipe.TryGetValue(out var top);
                var value = (group[0].Tier - 1) * weight;
                if (best is not null && value <= best.Value)
                {
                    continue;
                }
                var options = new List<string>();
                var items = new List<Resource>();
                bestStocked = false;
                foreach (var op in group)
                {
                    op.Recipe.TryGetValue(out var recipe);
                    options.Add(recipe ?? "");
                    if (!op.Item.IsEmpty)
                    {
                        items.Add(op.Item);
                    }
                    if (op.IngredientsOnMap.TryGetValue(out var stocked) && stocked)
                    {
                        bestStocked = true;
                    }
                }
                best = new SurgeryWant
                {
                    Pawn = pawn.Id,
                    Part = part,
                    Recipe = top ?? "",
                    Reason = SurgeryWantReason.PartShort,
                    Value = value,
                    Options = options,
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
            if ((fabricable?.Contains(item) ?? false) && ValidResource(item))
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
