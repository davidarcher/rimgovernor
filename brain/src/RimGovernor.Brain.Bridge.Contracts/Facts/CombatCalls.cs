using RimGovernor.Contracts.Wire.Mirror;
using RimGovernor.Contracts.Wire.Operations;
using RimGovernor.Contracts.Wire.Receipts;
using Receipt = RimGovernor.Contracts.Wire.Receipts.Receipt;

namespace RimGovernor.Brain.Bridge.Contracts;

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
    public static RimGovernor.Contracts.Wire.Operations.Action UndraftAction(string key, PawnId pawn)
    {
        return new RimGovernor.Contracts.Wire.Operations.Action { Key = key, Intent = new DraftIntent { PawnId = pawn.Value, Drafted = false } };
    }

    /// <summary>
    /// Whether <paramref name="row"/> is a drop-pod raid's arrival, a hostile arrived
    /// row with strategy pods.
    /// </summary>
    public static bool DropPodArrival(CombatEventRow row) =>
        row.Kind is not null && row.Kind.GetValueOrDefault() == CombatLogKind.HostileArrived && row.RaidStrategy is not null && (row.RaidStrategy ?? "") == PodsStrategy;

    /// <summary>
    /// Decodes an applied combat_orders action receipt, one result per order, in
    /// order. A receipt that is not applied, or carries no combat orders evidence, yields none.
    /// </summary>
    public static IReadOnlyList<CombatOrderResult> CombatOrderResults(Receipt receipt)
    {
        if (receipt.Outcome.Applied?.Observed?.Effect?.CombatOrders is not { } effect)
        {
            return [];
        }
        var output = new List<CombatOrderResult>(effect.Results.Count);
        for (var i = 0; i < effect.Results.Count; i++)
        {
            var v = effect.Results[i];
            output.Add(new CombatOrderResult { Index = i, PawnId = v.PawnId ?? "", Applied = v.Applied.GetValueOrDefault(), Refusal = v.Refusal ?? "", JobDef = v.JobDef ?? "" });
        }
        return output;
    }
}
