using RimGovernor.Brain.GameFacts.Mirror;
using RimGovernor.Brain.GameOrders.Mirror;
using RimGovernor.Brain.GameFacts.Operations;
using RimGovernor.Brain.GameOrders.Operations;
using RimGovernor.Brain.GameFacts.Receipts;
using Receipt = RimGovernor.Brain.GameFacts.Receipts.Receipt;

namespace RimGovernor.Brain.GameAccess;

// MaxConcurrentCalls, the pods arrival and the combat order results. The
// RecordCombatOrder telemetry row is the caller's: the redesigned telemetry facade takes its emitter by instance.
public static class CombatCalls
{
    /// <summary>
    /// How many native calls one client may have in flight at once. Calls need not be single-flight; this cap is
    /// backpressure against a caller bug flooding the native bridge at once.
    /// </summary>
    public const int MaxConcurrentCalls = 8;

    /// <summary>The raid strategy of a drop-pod arrival row.</summary>
    public const string PodsStrategy = "pods";

    /// <summary>The wire intent that undrafts one pawn no live plan needs.</summary>
    public static RimGovernor.Brain.GameOrders.Operations.Action UndraftAction(string key, PawnId pawn)
    {
        return new RimGovernor.Brain.GameOrders.Operations.Action { Key = key, Intent = new DraftIntent { PawnId = pawn.Value, Drafted = false } };
    }

    /// <summary>
    /// Whether <paramref name="row"/> is a drop-pod raid's arrival, a hostile arrived
    /// row with strategy pods.
    /// </summary>
    public static bool DropPodArrival(CombatEventRow row) =>
        row.Kind == CombatLogKind.HostileArrived && row.RaidStrategy == PodsStrategy;

    /// <summary>
    /// Retains an applied combat_orders action's native results, in request order.
    /// A receipt that is not applied, or carries no combat orders evidence, yields none.
    /// </summary>
    public static IReadOnlyList<Domain.CombatResult> CombatOrderResults(Receipt receipt)
    {
        if (receipt.Outcome.Applied?.Observed?.Effect?.CombatOrders is not { } effect)
        {
            return [];
        }
        return effect.Results;
    }
}
