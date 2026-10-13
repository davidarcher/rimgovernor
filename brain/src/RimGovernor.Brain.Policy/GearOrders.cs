namespace RimGovernor.Brain.Policy;

// The gear and armory planners declare the production orders they want standing to the work ledger
// (OrderDeclarer); the ledger places a missing order, keeps a matching bill and removes an undeclared one. A
// declaration is the whole wanted set of one Round, derived from the census and the bench readback alone: an
// order is judged again every Round and a bill that already makes a need is declared as it stands, which keeps it.

public static partial class PolicyFunctions
{
    /// <summary>
    /// MaintainEquipment's wanted apparel orders: one demand-sized batch per definition and stuff the loadout model
    /// still lacks after netting stored stock. Armor and helmets are the armory's (DeclareArmoryOrders). A wearable
    /// item already observed is worn before anything is crafted, so a pending candidate declares no new order,
    /// only the bills that already stand. Abstain while the census or the bench readback is unread.
    /// </summary>
    public static Declared DeclareGearOrders(GearPlanningRequest r)
    {
        var (review, v, unread) = GearOrderCensus(r);
        if (unread is { } cause)
        {
            return Abstaining(cause);
        }
        var needs = new List<GearNeed>();
        foreach (var p in v!.Pawns)
        {
            if (!p.Replacements.TryGetValue(out var replacements))
            {
                return Abstaining(Cause.UnreadGearNeeds);
            }
            foreach (var n in replacements)
            {
                if (!ArmoryArmor(n.Definition))
                {
                    needs.Add(new GearNeed(p, n));
                }
            }
        }
        var (orders, unknown) = GearOrders(needs, v, review!, r);
        return AbstainUnless(unknown, orders);
    }

    /// <summary>
    /// The armory's wanted orders at tier: the weapon batches (weapons is ArmoryWeaponDemand), the armor ladder the
    /// tier allows (each armor definition falls back down its family's rungs to the best one a bench recipe makes)
    /// and the stock-target mortar shells. Abstain while the tier, the census or the bench readback is unread.
    /// </summary>
    public static Declared DeclareArmoryOrders(GearPlanningRequest r, ArmoryTier tier, IReadOnlyList<Amount> weapons, IReadOnlyList<Amount> shells)
    {
        var (review, v, unread) = GearOrderCensus(r);
        if (unread is { } cause)
        {
            return Abstaining(cause);
        }
        r.Benches.TryGetValue(out var benches);

        var @out = new Declared();
        void Collect(IReadOnlyList<OrderSpec> orders, Cause? unreadCause)
        {
            @out.Orders.AddRange(orders);
            if (unreadCause is { } c)
            {
                @out.Unread(c);
            }
        }
        var needs = new List<GearNeed>();
        foreach (var d in weapons)
        {
            DomainException.Require(ValidResource(d.Resource) && d.Count > 0 && d.Count <= 256, $"invalid weapon demand \"{d.Resource.Value}\" x{d.Count}");
            for (long i = 0; i < d.Count; i++)
            {
                needs.Add(new GearNeed(new GearPawn { Pawn = new PawnId("weapon-batch"), Loadout = "colony" }, new GearReplacement { Definition = d.Resource, Reason = "unarmed" }));
            }
        }
        var (weaponOrders, weaponUnknown) = GearOrders(needs, v!, review!, r);
        Collect(weaponOrders, weaponUnknown);
        // Armor and shells follow the tier; weapons stand without it.
        if (tier == ArmoryTier.Unknown)
        {
            return @out.Unread(Cause.UnreadArmoryTier);
        }
        var armor = new Dictionary<Resource, int>();
        foreach (var p in v!.Pawns)
        {
            if (!p.Replacements.TryGetValue(out var replacements))
            {
                return Abstaining(Cause.UnreadGearNeeds);
            }
            foreach (var n in replacements)
            {
                if (ArmoryArmor(n.Definition))
                {
                    armor[n.Definition] = armor.GetValueOrDefault(n.Definition) + 1;
                }
            }
        }
        foreach (var d in armor.Keys.OrderBy(k => k.Value, StringComparer.Ordinal))
        {
            var (rungs, _) = ArmoryArmorRungs(d, tier);
            foreach (var rung in rungs)
            {
                var rungNeeds = new List<GearNeed>();
                for (var i = 0; i < armor[d]; i++)
                {
                    rungNeeds.Add(new GearNeed(new GearPawn { Pawn = new PawnId("armor-batch"), Loadout = "colony" }, new GearReplacement { Definition = rung, Reason = "armory" }));
                }
                var (orders, unknown) = GearOrders(rungNeeds, v, review!, r);
                Collect(orders, unknown);
                if (orders.Count > 0 || unknown is not null)
                {
                    break;
                }
            }
        }
        var (stocked, shellUnknown) = ShellOrders(benches, shells);
        Collect(stocked, shellUnknown);
        return @out;
    }

    /// <summary>
    /// The reviewed, loadout-modelled census a declaration reads; Unread names the fact the review cannot
    /// establish (recovery or the bench readback), null when both are read.
    /// </summary>
    private static (GearReview? Review, GearObservation? Observation, Cause? Unread) GearOrderCensus(GearPlanningRequest r)
    {
        var review = ReviewGear(r.Observation);
        if (!review.Recovered.IsKnown)
        {
            return (null, null, Cause.UnreadGearRecover);
        }
        if (!r.Benches.IsKnown)
        {
            return (null, null, Cause.UnreadBenches);
        }
        r.Observation.TryGetValue(out var observed);
        return (review, ModeledGearObservation(observed!, review.Loadouts), null);
    }

    /// <summary>Orders needs by pawn, definition, stuff and reason.</summary>
    private static List<GearNeed> SortGearNeeds(IEnumerable<GearNeed> needs) =>
        needs
            .OrderBy(n => n.Pawn.Pawn.Value, StringComparer.Ordinal)
            .ThenBy(n => n.Need.Definition.Value, StringComparer.Ordinal)
            .ThenBy(n => n.Need.Stuff.Value, StringComparer.Ordinal)
            .ThenBy(n => n.Need.Reason, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Turns needs into the orders that make them: per definition and stuff, the bill that already makes it
    /// (declared as it stands) or the first funded recipe's batch sized to the demand left after stored stock.
    /// Unknown names the fact left unread (a bench census, bills or a recipe's facts).
    /// </summary>
    private static (IReadOnlyList<OrderSpec> Orders, Cause? Unknown) GearOrders(List<GearNeed> needs, GearObservation v, GearReview review, GearPlanningRequest r)
    {
        var orders = new List<OrderSpec>();
        Cause? unknown = null;
        if (needs.Count == 0)
        {
            return (orders, null);
        }
        needs = SortGearNeeds(needs);
        var (benches, demand, known) = GearBenchDemand(needs, v, review, r);
        if (!known)
        {
            return (orders, Cause.UnreadBenches);
        }
        var pending = false;
        foreach (var p in v.Pawns)
        {
            p.Candidates.TryGetValue(out var candidates);
            pending = pending || candidates.Count > 0;
        }
        var done = new HashSet<GearStockKey>();
        foreach (var n in needs)
        {
            var key = new GearStockKey(n.Need.Definition, n.Need.Stuff);
            var count = demand.GetValueOrDefault(key);
            if (count == 0 || !done.Add(key))
            {
                continue;
            }
            var (method, resolved) = ProduceNeed(n, count, benches, r);
            if (!resolved)
            {
                continue;
            }
            switch (method.Kind)
            {
                case GearMethodKind.Unknown:
                    unknown = Cause.UnreadBills;
                    break;
                case GearMethodKind.WaitForExistingWork:
                    if (!method.Bill!.Spec.TryGetValue(out var spec))
                    {
                        unknown = Cause.UnreadBills;
                        continue;
                    }
                    orders.Add(spec);
                    break;
                case GearMethodKind.Produce:
                    if (pending)
                    {
                        continue;
                    }
                    orders.Add(
                        new OrderSpec
                        {
                            Recipe = method.Recipe,
                            Ingredients = EquatableArray.From(method.Filter.Select(f => f.Value)),
                            Mode = BillMode.GearBatch,
                            Target = method.Count,
                            BenchKind = GearBenchDef(benches, method.Bench),
                        }
                    );
                    break;
            }
        }
        return (orders, unknown);
    }

    private static string GearBenchDef(IReadOnlyList<GearBench> benches, string id)
    {
        foreach (var b in benches)
        {
            if (b.Id == id)
            {
                return b.Def;
            }
        }
        return "";
    }

    /// <summary>
    /// The stock-target orders of the mortar shell targets, in priority order: a shell some bench bill already
    /// makes is declared as that bill stands, else the first bench (by id) with an available recipe makes it. A
    /// target no recipe makes is skipped.
    /// </summary>
    private static (IReadOnlyList<OrderSpec> Orders, Cause? Unknown) ShellOrders(IReadOnlyList<GearBench> benches, IReadOnlyList<Amount> targets)
    {
        var orders = new List<OrderSpec>();
        Cause? unknown = null;
        var sorted = benches.OrderBy(b => b.Id, StringComparer.Ordinal).ToList();
        var standing = new Dictionary<Resource, GearBill>();
        foreach (var b in sorted)
        {
            b.Bills.TryGetValue(out var bills);
            foreach (var bill in bills)
            {
                if (bill.Spent)
                {
                    continue;
                }
                foreach (var product in bill.Products)
                {
                    standing.TryAdd(product, bill);
                }
            }
        }
        foreach (var t in targets)
        {
            if (t.Count <= 0)
            {
                continue;
            }
            if (standing.TryGetValue(t.Resource, out var bill))
            {
                if (!bill.Spec.TryGetValue(out var spec))
                {
                    unknown = Cause.UnreadBills;
                    continue;
                }
                orders.Add(spec);
                continue;
            }
            DomainException.Require(t.Count <= int.MaxValue, $"shell bill target {t.Count} exceeds the bill count");
            var placed = false;
            foreach (var b in sorted)
            {
                if (!b.Recipes.TryGetValue(out var recipes))
                {
                    continue;
                }
                foreach (var recipe in recipes)
                {
                    var ak = recipe.Available.TryGetValue(out var available);
                    var ok = recipe.AvailableOn.TryGetValue(out var on);
                    if (ak && available && ok && on && recipe.Products.Contains(t.Resource))
                    {
                        orders.Add(new OrderSpec { Recipe = recipe.Definition, Mode = BillMode.StockTarget, Target = (int)t.Count, BenchKind = b.Def });
                        placed = true;
                        break;
                    }
                }
                if (placed)
                {
                    break;
                }
            }
        }
        return (orders, unknown);
    }
}
