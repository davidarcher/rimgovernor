namespace RimGovernor.Brain.Policy;

// The ledger view is the read-only picture of one Round's work ledger that the station planner reads: whether the
// Round declared and reconciled, and the orders it wants. It is a projection of memory the Round already holds, and
// nothing is fed back to a planner.

/// <summary>The ledger as the station planner reads it.</summary>
public sealed record LedgerView
{
    public string Status { get; init; } = "";

    /// <summary>
    /// The wanted orders by key, each the spec as the station planner keys it: recipe, ingredients, worker, mode,
    /// target, product, class and bench kind; not the owner (a spec two needs declare is one order) or requirements.
    /// </summary>
    public IReadOnlyList<OrderSpec> Orders { get; init; } = [];

    /// <summary>
    /// The owners that declared each wanted order, by its spec Key (<see cref="WorkRules.OrderOwners"/>): the filing keys
    /// of the Needs a coalesced order serves, by name. In memory only, like the view.
    /// </summary>
    public IReadOnlyDictionary<string, EquatableArray<string>> Owners { get; init; } = new Dictionary<string, EquatableArray<string>>();
}

public static partial class WorkRules
{
    // Ledger view statuses.

    /// <summary>No review has filed the ledger yet.</summary>
    public const string LedgerViewNone = "no_review";

    /// <summary>No planner has declared into the ledger.</summary>
    public const string LedgerViewInactive = "inactive";

    /// <summary>The bench readback was unread, so nothing was diffed.</summary>
    public const string LedgerViewUnread = "readback_unknown";

    /// <summary>The Round declared and diffed.</summary>
    public const string LedgerViewReconciled = "reconciled";

    /// <summary>An empty view with the given status.</summary>
    public static LedgerView NewLedgerView(string status) => new() { Status = status };

    /// <summary>The reconciled view of one Round's wanted orders (WantedOrders), in key order, with their declaring owners.</summary>
    public static LedgerView BuildLedgerView(IReadOnlyDictionary<string, OrderSpec> wanted, IReadOnlyList<Declared> declared) =>
        new()
        {
            Status = LedgerViewReconciled,
            Orders = [.. wanted.Keys.Order(StringComparer.Ordinal).Select(k => StationOrder(wanted[k]))],
            Owners = OrderOwners(declared),
        };

    /// <summary>
    /// The owners of every declared order by spec Key: the non-empty <see cref="OrderSpec.Owner"/> of each declaration of
    /// it, distinct and by name. Two owners declaring one spec coalesce into one order (WantedOrders) and both own it.
    /// </summary>
    public static IReadOnlyDictionary<string, EquatableArray<string>> OrderOwners(IReadOnlyList<Declared> declared) =>
        declared.SelectMany(d => d.Orders).Where(o => o.Owner.Length > 0).GroupBy(o => o.Key(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => EquatableArray.From(g.Select(o => o.Owner).Distinct().Order(StringComparer.Ordinal)), StringComparer.Ordinal);

    private static OrderSpec StationOrder(OrderSpec o) =>
        new() { Recipe = o.Recipe, Ingredients = o.Ingredients, Worker = o.Worker, Mode = o.Mode, Target = o.Target, Product = o.Product, Class = o.Class, BenchKind = o.BenchKind };
}
