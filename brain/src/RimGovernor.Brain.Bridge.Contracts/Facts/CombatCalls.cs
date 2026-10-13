using RimGovernor.Protocol.Mirror;
using RimGovernor.Protocol.Operations;
using RimGovernor.Protocol.Receipts;
using Receipt = RimGovernor.Protocol.Receipts.Receipt;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go give_job.go (UndraftAction), frames.go (PodsStrategy, DropPodArrival), combat_orders.go (CombatOrderResults),
// combat_geometry.go (CombatGeometryAsk, CombatGeometryProposeAsk) and client.go (MaxConcurrentCalls). The
// RecordCombatOrder telemetry row is the caller's: the redesigned telemetry facade takes its emitter by instance.
public static partial class BridgeFunctions
{
    /// <summary>
    /// Go's <c>MaxConcurrentCalls</c>: how many native calls one client may have in flight at once. The GABP
    /// connection correlates concurrent requests by id, so calls need not be single-flight; this cap is
    /// backpressure against a caller bug flooding the native bridge at once.
    /// </summary>
    public const int MaxConcurrentCalls = 8;

    /// <summary>Go's <c>PodsStrategy</c>: the raid strategy of a drop-pod arrival row.</summary>
    public const string PodsStrategy = "pods";

    private static readonly HashSet<string> CombatRefusals = new(StringComparer.Ordinal)
    {
        CombatRefusalNotDrafted,
        CombatRefusalStaleSnapshot,
        CombatRefusalNotFound,
        CombatRefusalUnreachable,
        CombatRefusalCannotHit,
        CombatRefusalNoGroundVerb,
        CombatRefusalNotADoor,
        CombatRefusalNativeRefused,
        CombatRefusalCannotRescue,
        CombatRefusalNoBed,
        CombatRefusalCannotRepair,
        CombatRefusalCannotDraft,
        CombatRefusalNotAMortar,
        CombatRefusalUnknownShell,
        CombatRefusalNoShell,
        CombatRefusalNotOurs,
        CombatRefusalUntrained,
        CombatRefusalNotADrug,
        CombatRefusalNoDrug,
    };

    /// <summary>Go's <c>UndraftAction</c>: the wire intent that undrafts one pawn no live plan needs.</summary>
    /// <exception cref="BridgeContractException">The key or the pawn is not a valid identifier.</exception>
    public static RimGovernor.Protocol.Operations.Action UndraftAction(string key, PawnId pawn)
    {
        if (!IsValidId(key) || !IsValidId(pawn.Value))
        {
            throw Contract("undraft requires a key and a pawn");
        }
        return new RimGovernor.Protocol.Operations.Action { Key = key, Draft = new DraftIntent { PawnId = pawn.Value, Drafted = false } };
    }

    /// <summary>
    /// Go's <c>DropPodArrival</c>: whether <paramref name="row"/> is a drop-pod raid's arrival, a hostile arrived
    /// row with strategy pods.
    /// </summary>
    public static bool DropPodArrival(CombatEventRow row) =>
        row.HasKind && row.Kind == CombatLogKind.HostileArrived && row.HasRaidStrategy && row.RaidStrategy == PodsStrategy;

    /// <summary>
    /// Go's <c>CombatOrderResults</c>: decodes an applied combat_orders action receipt against its request, one
    /// result per order, in order, each applied or refused with a known reason. An uncertain receipt carries no
    /// result set and yields none.
    /// </summary>
    /// <exception cref="BridgeContractException">The receipt does not answer the request.</exception>
    public static IReadOnlyList<CombatOrderResult> CombatOrderResults(Receipt receipt, CombatOrders command)
    {
        if (receipt.OutcomeCase == Receipt.OutcomeOneofCase.Uncertain)
        {
            return [];
        }
        if (receipt.OutcomeCase != Receipt.OutcomeOneofCase.Applied)
        {
            throw Contract("combat orders receipt not applied");
        }
        var effect = receipt.Applied.Observed?.CombatOrders ?? throw Contract("combat orders evidence missing");
        if (effect.Results.Count != command.Orders.Count)
        {
            throw Contract($"combat orders: {effect.Results.Count} results for {command.Orders.Count} orders");
        }
        var output = new List<CombatOrderResult>(effect.Results.Count);
        for (var i = 0; i < effect.Results.Count; i++)
        {
            var v = effect.Results[i];
            if (v is null || !v.HasIndex || v.Index != i || !v.HasApplied)
            {
                throw Contract($"combat order result {i} index or outcome missing");
            }
            var want = command.Orders[i].Pawn?.EntityId ?? "";
            if (v.PawnId != want)
            {
                throw Contract($"combat order result {i} names pawn \"{v.PawnId}\", want \"{want}\"");
            }
            var result = new CombatOrderResult { Index = i, PawnId = v.PawnId, Applied = v.Applied, Refusal = v.Refusal, JobDef = v.JobDef };
            if (result.Applied)
            {
                if (v.HasRefusal)
                {
                    throw Contract($"combat order result {i} applied with a refusal");
                }
                if (result.JobDef.Length != 0 && !IsValidId(result.JobDef))
                {
                    throw Contract($"combat order result {i} job invalid");
                }
            }
            else if (!CombatRefusals.Contains(result.Refusal))
            {
                throw Contract($"combat order result {i} refused without a known reason: \"{result.Refusal}\"");
            }
            else if (v.HasJobDef)
            {
                throw Contract($"combat order result {i} refused with a job");
            }
            output.Add(result);
        }
        return output;
    }

    /// <summary>Go's <c>CombatGeometryAsk</c>: a request for <paramref name="identity"/>'s cells and hostiles.</summary>
    public static CombatGeometryRequest CombatGeometryAsk(Identity identity, IEnumerable<Cell> cells, IEnumerable<string> hostiles, string pawnId)
    {
        var request = new CombatGeometryRequest { Identity = identity.Clone() };
        request.Cells.AddRange(cells);
        request.HostileIds.AddRange(hostiles);
        if (pawnId.Length != 0)
        {
            request.PawnId = pawnId;
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
        request.Propose = propose;
        return request;
    }
}
