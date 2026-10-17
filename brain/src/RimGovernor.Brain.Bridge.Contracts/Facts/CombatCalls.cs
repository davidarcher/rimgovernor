using RimGovernor.Contracts.Wire.Mirror;
using RimGovernor.Contracts.Wire.Operations;
using RimGovernor.Contracts.Wire.Receipts;
using Receipt = RimGovernor.Contracts.Wire.Receipts.Receipt;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go give_job.go (UndraftAction), frames.go (PodsStrategy, DropPodArrival), combat_orders.go (CombatOrderResults),
// combat_geometry.go (CombatGeometryAsk, CombatGeometryProposeAsk) and client.go (MaxConcurrentCalls). The
// RecordCombatOrder telemetry row is the caller's: the redesigned telemetry facade takes its emitter by instance.
public static class CombatCalls
{
    /// <summary>
    /// Go's <c>MaxConcurrentCalls</c>: how many native calls one client may have in flight at once. Calls need not be single-flight; this cap is
    /// backpressure against a caller bug flooding the native bridge at once.
    /// </summary>
    public const int MaxConcurrentCalls = 8;

    /// <summary>Go's <c>PodsStrategy</c>: the raid strategy of a drop-pod arrival row.</summary>
    public const string PodsStrategy = "pods";

    /// <summary>Go's <c>UndraftAction</c>: the wire intent that undrafts one pawn no live plan needs.</summary>
    /// <exception cref="BridgeContractException">The key or the pawn is not a valid identifier.</exception>
    public static RimGovernor.Contracts.Wire.Operations.Action UndraftAction(string key, PawnId pawn)
    {
        if (!WireValidation.IsValidId(key) || !WireValidation.IsValidId(pawn.Value))
        {
            throw BridgeContractException.Contract("undraft requires a key and a pawn");
        }
        return new RimGovernor.Contracts.Wire.Operations.Action { Key = key, Intent = new DraftIntent { PawnId = pawn.Value, Drafted = false } };
    }

    /// <summary>
    /// Go's <c>DropPodArrival</c>: whether <paramref name="row"/> is a drop-pod raid's arrival, a hostile arrived
    /// row with strategy pods.
    /// </summary>
    public static bool DropPodArrival(CombatEventRow row) =>
        row.Kind is not null && row.Kind.GetValueOrDefault() == CombatLogKind.HostileArrived && row.RaidStrategy is not null && (row.RaidStrategy ?? "") == PodsStrategy;

    /// <summary>
    /// Go's <c>CombatOrderResults</c>: decodes an applied combat_orders action receipt, one result per order, in
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

    /// <summary>Go's <c>CombatGeometryAsk</c>: a request for <paramref name="identity"/>'s cells and hostiles.</summary>
    public static CombatGeometryRequest CombatGeometryAsk(Identity identity, IEnumerable<Cell> cells, IEnumerable<string> hostiles, string pawnId)
    {
        var request = new CombatGeometryRequest { Identity = identity };
        request = request with { Cells = [.. request.Cells, .. cells] };
        request = request with { HostileIds = [.. request.HostileIds, .. hostiles] };
        if (pawnId.Length != 0)
        {
            request = request with { PawnId = pawnId };
        }
        return request;
    }

    /// <summary>
    /// Go's <c>CombatGeometryProposeAsk</c>: a request whose cells are all proposed by the native for
    /// <paramref name="propose"/>'s role, scored against <paramref name="hostiles"/>.
    /// </summary>
    public static CombatGeometryRequest CombatGeometryProposeAsk(Identity identity, CombatGeometryPropose propose, IEnumerable<string> hostiles, string pawnId)
    {
        var request = CombatGeometryAsk(identity, [], hostiles, pawnId);
        request = request with { Propose = propose };
        return request;
    }
}
