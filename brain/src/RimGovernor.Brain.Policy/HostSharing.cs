namespace RimGovernor.Brain.Policy;

/// <summary>
/// A kind whose orders of <paramref name="Recipe"/> a standing bench also takes (RG-HABITAT-005): the plan hosts the
/// recipe at both definitions, a chain's bridge and its replacement or a host and its retained spare.
/// </summary>
public sealed record BenchShare(string Recipe, string Kind);

/// <summary>
/// The fate of a standing spare once its replacement serves (RG-HABITAT-005): the adopted plan hosts the load at another
/// definition and no longer at the spare's, so it is removed (<see cref="Retire"/>), or a pawn is working one of its bills
/// now and the removal waits (<see cref="Held"/>, naming the fact) rather than interrupting it.
/// </summary>
public abstract record HostRetirement(string Bench)
{
    /// <summary>The transition choice's removal of the spare: uninstalled to storage (<paramref name="Uninstall"/>) or deconstructed.</summary>
    public sealed record Retire(string Bench, string Def, bool Uninstall) : HostRetirement(Bench);

    public sealed record Held(string Bench, string Fact) : HostRetirement(Bench);
}

public static partial class WorkRules
{
    /// <summary>
    /// Whether <paramref name="bench"/> takes <paramref name="order"/>: an order of its own kind, or a dispatcher-sized one
    /// (<see cref="Stocked"/>) of a kind it shares that recipe with (<see cref="GearBench.Shares"/>).
    /// </summary>
    public static bool Takes(this GearBench bench, OrderSpec order) =>
        bench.Def == order.BenchKind || order.Stocked() && bench.Shares.Any(s => s.Recipe == order.Recipe && s.Kind == order.BenchKind);

    /// <summary>
    /// The kinds the plan hosts each recipe at when it hosts it at more than one definition through a chain or a pair
    /// (<see cref="HostStep.Bridge"/>, <see cref="HostStep.Share"/>), by recipe. None without a plan.
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> SharedKinds(LayoutPlan? plan) =>
        (plan?.Rooms ?? []).SelectMany(r => r.Production).GroupBy(p => p.Recipe, StringComparer.Ordinal)
            .Where(g => g.Any(p => p.Step != HostStep.Host) && g.Select(p => p.Bench).Distinct().Count() > 1)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Bench).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);

    /// <summary>
    /// The benches with the kinds the plan shares between them (<see cref="SharedKinds"/>): each bench of a shared kind
    /// takes the recipe's dispatcher-sized orders naming the others, and a bill of the recipe it carries for an order that
    /// names another kind (none of the <paramref name="orders"/> naming its own) reads back as that order's, so the ledger
    /// keeps the copy rather than removing it as an orphan.
    /// </summary>
    public static List<GearBench> ShareBenches(IReadOnlyList<GearBench> benches, LayoutPlan? plan, IReadOnlyList<OrderSpec> orders)
    {
        var shared = SharedKinds(plan);
        if (shared.Count == 0) return [.. benches];
        return [.. benches.Select(b =>
        {
            var shares = shared.Where(s => s.Value.Contains(b.Def)).SelectMany(s => s.Value.Where(k => k != b.Def).Select(k => new BenchShare(s.Key, k))).ToArray();
            if (shares.Length == 0) return b;
            var bills = b.Bills.Select(bill =>
            {
                if (bill.Spec.BenchKind != b.Def || !shares.Any(s => s.Recipe == bill.Recipe)) return bill;
                var own = bill.Spec.Key();
                if (orders.Any(o => o.Key() == own)) return bill;
                var named = orders.FirstOrDefault(o => o.Stocked() && shares.Any(s => s.Recipe == o.Recipe && s.Kind == o.BenchKind) && (bill.Spec with { BenchKind = o.BenchKind }).Key() == o.Key());
                return named is null ? bill : bill with { Spec = bill.Spec with { BenchKind = named.BenchKind } };
            });
            return b with { Shares = EquatableArray.From(shares), Bills = EquatableArray.From(bills) };
        })];
    }

    /// <summary>
    /// The standing benches as the host comparison reads them (<see cref="HostBench"/>): serving when usable and reachable,
    /// the recipes each offers now (<see cref="BenchOffers"/>), and the bills a pawn's current job is working.
    /// </summary>
    public static EquatableArray<HostBench> HostBenchesOf(IReadOnlyList<GearBench> benches) =>
        EquatableArray.From(benches.OrderBy(b => b.Id, StringComparer.Ordinal).Select(b => new HostBench(b.Id, b.Def, b.Usable && b.Observed.Reachable,
            EquatableArray.From(b.Recipes.Select(r => r.Definition).Where(r => BenchOffers(b, r)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
            EquatableArray.From(b.Bills.Where(bill => bill.Observed.Workers.Count != 0).Select(bill => bill.Id).Order(StringComparer.Ordinal)))));
}

public static partial class PlannedRules
{
    /// <summary>
    /// The standing spares an owner's loads retire (RG-HABITAT-005), replacing the automatic campfire retirement. For each of
    /// <paramref name="recipes"/> the plan hosts at a definition (its Host production, no chain bridge pending), every other
    /// standing bench offering the recipe is proposed when the plan hosts nothing at its definition (a kept spare is a Share
    /// production), no declared order of a recipe the plan does not move off it names its kind, and no other owner claims it
    /// (<paramref name="claimed"/>, <see cref="ClaimedBy"/>: a heating campfire). The transition choice
    /// (<see cref="ServicingChoiceRules.Transition"/>) decides each: while the replacement does not serve
    /// (<see cref="LayoutHostRules.Serving"/>) the spare's service is unassessed ("replacement not serving:{recipe}") and Retain
    /// wins; once it serves the host comparison has replaced the spare (Retain excluded, its service nothing) and Store or
    /// Remove is compared. A chosen removal still passes <see cref="HostRemovalHold"/>: a spare a pawn is working a bill at is
    /// Held naming the bills in progress, so the replacement's service is never bought by interrupting work. A choice that could
    /// price nothing is Held naming its hold.
    /// </summary>
    public static IReadOnlyList<HostRetirement> HostRetirementsOf(LayoutPlan plan, LedgerView view, IReadOnlyCollection<string> recipes, Func<string, bool> claimed, TransitionFacts? transitions) =>
        HostTransitionsOf(plan, view, recipes, claimed, transitions).Retirements;

    /// <summary>
    /// <see cref="HostRetirementsOf"/> with the transition decision of each spare it proposed (bound or compared,
    /// ServicingChoiceRules.Choose), the first removing recipe's in ordinal order: a spare's removal depends on its replacement serving
    /// (ReplacementDependency, named only while it serves), so a lost replacement re-selects it and drops the binding.
    /// </summary>
    public static (IReadOnlyList<HostRetirement> Retirements, IReadOnlyList<TransitionDecision> Decisions) HostTransitionsOf(LayoutPlan plan, LedgerView view, IReadOnlyCollection<string> recipes, Func<string, bool> claimed, TransitionFacts? transitions)
    {
        var made = plan.Rooms.SelectMany(r => r.Production).ToArray();
        var hosted = made.Select(p => p.Bench).ToHashSet(StringComparer.Ordinal);
        var output = new List<HostRetirement>();
        var decisions = new Dictionary<string, TransitionDecision>(StringComparer.Ordinal);
        foreach (var recipe in recipes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (made.Where(p => p.Recipe == recipe && p.Step == HostStep.Host).Select(p => p.Bench).Order(StringComparer.Ordinal).FirstOrDefault() is not { } host
                || made.Any(p => p.Recipe == recipe && p.Step == HostStep.Bridge && p.Bench != host))
            {
                continue;
            }
            var unserved = LayoutHostRules.Serving(view, host, recipe) ? null : recipe;
            foreach (var spare in view.Benches.Where(b => b.Def != host && b.Recipes.Contains(recipe) && !hosted.Contains(b.Def) && !claimed(b.Id)))
            {
                // An order the plan does not move to another host still runs on this kind: the spare serves it.
                if (view.Orders.Any(o => o.Target > 0 && o.BenchKind == spare.Def && !made.Any(p => p.Recipe == o.Recipe && p.Step == HostStep.Host && p.Bench != spare.Def))) continue;
                // A spare two recipes propose is removed once, by the first recipe whose choice removes it.
                if (decisions.GetValueOrDefault(spare.Id) is { Admitted: true }) continue;
                var decision = ServicingChoiceRules.Choose(new(spare.Id, spare.Def, TransitionCause.HostRetirement)
                {
                    RetainExcluded = unserved is null ? ReplacedHost : null,
                    Service = ServicingChoiceRules.HostSpareService(unserved),
                    Dependency = unserved is null ? new ReplacementDependency(recipe, host) : null,
                }, transitions);
                decisions[spare.Id] = decision;
                if (decision.Hold is { } held) output.Add(new HostRetirement.Held(spare.Id, held));
                else if (decision.Admitted)
                    output.Add(HostRemovalHold(plan, view, spare.Id) is { } hold
                        ? new HostRetirement.Held(spare.Id, hold)
                        : new HostRetirement.Retire(spare.Id, spare.Def, decision.Uninstalls));
            }
        }
        return ([.. output.Distinct()], [.. decisions.Values]);
    }

    /// <summary>The retain exclusion of a spare whose serving replacement the host comparison chose.</summary>
    public const string ReplacedHost = "replaced_host";

    /// <summary>
    /// The one rule every removal of a standing host the plan replaces passes (RG-HABITAT-005): a chain's bridge or a spare,
    /// a standing bench offering a recipe with a declared order that the plan hosts (its Host production) at another
    /// definition. Its removal waits, named, until a replacement serves each such recipe (<see cref="LayoutHostRules.Serving"/>:
    /// "replacement not serving:{recipe}"), then while a pawn works one of its bills ("bill in progress:{bills}"). Null
    /// admits it: a bench the plan replaces nothing at, or one no read names.
    /// </summary>
    public static string? HostRemovalHold(LayoutPlan plan, LedgerView view, string bench)
    {
        if (view.Benches.FirstOrDefault(b => b.Id == bench) is not { } standing) return null;
        var made = plan.Rooms.SelectMany(r => r.Production).Where(p => p.Step == HostStep.Host && p.Bench != standing.Def).ToArray();
        var replaced = standing.Recipes.Where(r => view.Orders.Any(o => o.Recipe == r && o.Target > 0) && made.Any(p => p.Recipe == r)).ToArray();
        if (replaced.Length == 0) return null;
        if (replaced.FirstOrDefault(r => !made.Any(p => p.Recipe == r && LayoutHostRules.Serving(view, p.Bench, r))) is { } unserved)
        {
            return "replacement not serving:" + unserved;
        }
        return standing.Working.Count != 0 ? "bill in progress:" + string.Join(",", standing.Working) : null;
    }

    /// <summary>
    /// A room reconcile's ops with every removal target <see cref="HostRemovalHold"/> holds taken out (a removal left with
    /// no target is dropped), and the first hold's fact, null when none holds. The room's other work goes on.
    /// </summary>
    public static (IReadOnlyList<Operation> Ops, string? Held) AdmittedRemovals(IReadOnlyList<Operation> ops, LayoutPlan plan, LedgerView view)
    {
        string? held = null;
        var output = new List<Operation>();
        foreach (var op in ops)
        {
            if (op.Kind is not (OpKind.FurnitureOut or OpKind.Pack or OpKind.PackInUse or OpKind.Reinstall) || op.Targets.Count == 0)
            {
                output.Add(op);
                continue;
            }
            var admitted = new List<ClearanceTarget>();
            var pieces = new List<WantedPiece>();
            for (var i = 0; i < op.Targets.Count; i++)
            {
                var target = op.Targets[i];
                if (HostRemovalHold(plan, view, target.EntityId) is { } hold) held ??= target.EntityId + ":" + hold;
                else
                {
                    admitted.Add(target);
                    // A reinstall moves Targets[i] onto Pieces[i]: the pair stays together.
                    if (op.Kind == OpKind.Reinstall) pieces.Add(op.Pieces[i]);
                }
            }
            if (admitted.Count == op.Targets.Count) output.Add(op);
            else if (admitted.Count > 0) output.Add(op with { Targets = admitted, Pieces = op.Kind == OpKind.Reinstall ? pieces : op.Pieces });
        }
        return (output, held);
    }

    /// <summary>Whether another owner than <paramref name="owner"/> claims the building <c>id</c> (its first cell in a construction claim): a heating campfire is the temperature Need's.</summary>
    public static Func<string, bool> ClaimedBy(ReviewFacts facts, FilingKey owner) => id =>
        facts.CurrentConstruction.Buildings.FirstOrDefault(b => b.Id == id) is { Cells.Count: > 0 } building
        && facts.ConstructionClaims.Any(c => c.Concern != owner && c.Cells.Contains(building.Cells[0]));

    /// <summary>
    /// Whether <paramref name="owner"/> owes a spare's removal or awaits one (<see cref="HostRetirementsOf"/>); false with
    /// no plan or no ledger view. It keeps the owner's Need unmet so the owner's step removes the spare.
    /// </summary>
    public static bool HostRetireOwed(ReviewFacts facts, LedgerView? view, FilingKey owner) =>
        facts.LayoutPlan is { } plan && view is not null
        && HostRetirementsOf(plan, view, OwnedHostedRecipes(view, owner, plan), ClaimedBy(facts, owner), ServicingChoiceRules.TransitionFactsOf(facts)).Count != 0;

    /// <summary>
    /// The recipes of <paramref name="owner"/>'s ledger loads the plan hosts (RG-HABITAT-005): its declared orders with a
    /// target (<see cref="LedgerView.Owners"/>) naming no bench kind, or naming a kind the plan hosts the recipe away from
    /// (a chain's bridge or a spare its replacement retires), by name. Empty before a review reconciled the ledger.
    /// </summary>
    public static IReadOnlyList<string> OwnedHostedRecipes(LedgerView view, FilingKey owner, LayoutPlan plan)
    {
        var made = plan.Rooms.SelectMany(r => r.Production).ToArray();
        return
        [
            .. view.Orders.Where(o => o.Target > 0 && view.Owners.TryGetValue(o.Key(), out var owners) && owners.Contains(owner.Value)
                    && (o.BenchKind.Length == 0 || made.Any(p => p.Recipe == o.Recipe && p.Step == HostStep.Host && p.Bench != o.BenchKind)))
                .Select(o => o.Recipe).Distinct().Order(StringComparer.Ordinal),
        ];
    }
}
