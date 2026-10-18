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
    /// target and product; not the owner (a spec two concerns declare is one order), bench kind, class or requirements.
    /// </summary>
    public IReadOnlyList<OrderSpec> Orders { get; init; } = [];
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

    /// <summary>The reconciled view of one Round's wanted orders (WantedOrders), in key order.</summary>
    public static LedgerView BuildLedgerView(IReadOnlyDictionary<string, OrderSpec> wanted) =>
        new()
        {
            Status = LedgerViewReconciled,
            Orders = [.. wanted.Keys.Order(StringComparer.Ordinal).Select(k => StationOrder(wanted[k]))],
        };

    private static OrderSpec StationOrder(OrderSpec o) =>
        new() { Recipe = o.Recipe, Ingredients = o.Ingredients, Worker = o.Worker, Mode = o.Mode, Target = o.Target, Product = o.Product };
}
