namespace RimGovernor.Brain.Policy;

/// <summary>
/// Why at-risk food sits unstored although a covered site has room. <see cref="None"/> means no reason was
/// named. No haul is planned for any of them: vanilla hauling moves loose food once the blocker clears.
/// (Go <c>FoodStuckReason</c>.)
/// </summary>
public enum FoodStuckReason
{
    [WireName("")]
    None = 0,

    /// <summary>An unstored stack is forbidden, so no hauler touches it.</summary>
    [WireName("stack_forbidden")]
    Forbidden,

    /// <summary>No colonist has Hauling enabled and prioritised.</summary>
    [WireName("no_colonist_can_haul")]
    NoHauler,

    /// <summary>
    /// Neither cause can be shown from the facts (a forbidden flag or the work census is unread, or both are
    /// ruled out). It is reported as its own named reason, never folded into another.
    /// </summary>
    [WireName("cause_unestablished")]
    Unestablished,
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Go <c>FoodStorageStuckReason</c>: names why the unstored perishable stacks of the census are not stored.
    /// A known forbidden stack wins; otherwise a known empty hauler roster; anything else is
    /// <see cref="FoodStuckReason.Unestablished"/>.
    /// </summary>
    public static FoodStuckReason FoodStorageStuckReason(FoodStorageObservation observed, FoodStoragePolicy p, Fact<IReadOnlyList<PawnId>> haulers)
    {
        _ = observed.Stocks.TryGetValue(out var stocks);
        foreach (var entry in stocks ?? [])
        {
            if (!FoodStorageUnstored(entry, observed.ChilledMaxC, p))
            {
                continue;
            }
            if (entry.Stock.Forbidden.TryGetValue(out var forbidden) && forbidden)
            {
                return FoodStuckReason.Forbidden;
            }
        }
        if (haulers.TryGetValue(out var rows) && rows.Count == 0)
        {
            return FoodStuckReason.NoHauler;
        }
        return FoodStuckReason.Unestablished;
    }
}
