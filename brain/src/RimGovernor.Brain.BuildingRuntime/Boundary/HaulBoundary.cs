using Receipt = RimGovernor.Brain.Executor.Receipt;

namespace RimGovernor.Brain.BuildingRuntime;

// Port of internal/buildingruntime/haul (boundary.go). Dropped: the LeaseSource dependency (no native epoch lease in-process).

/// <summary>Narrows the bridge client to the one read a haul dispatch makes: the hauler's row, for the tick the dispatch is journaled at.</summary>
public interface IHaulNative
{
    Task<PbObservations.ListPawnsReply> ReadPawnsAsync(PbCommon.Identity identity, IReadOnlyList<string> pawnIds, CancellationToken ct);
}

public sealed record HaulCapabilities(IHaulNative Native, IActionsWriter Writer);

/// <summary>
/// Sends a haul as an idempotent intent naming the pawn and the item. Native checks both against live state when
/// it applies and refuses with a reason; an applied haul is ordered, and where the item went is read by the next
/// planner review.
/// </summary>
public sealed class HaulBoundary : IHaulBoundary
{
    private readonly IHaulNative _native;
    private readonly IActionsWriter _writer;
    private readonly IClock _clock;

    public HaulBoundary(IHaulNative native, IActionsWriter writer, IClock clock)
    {
        _native = native;
        _writer = writer;
        _clock = clock;
    }

    /// <summary>Anchors the dispatch to a fresh native read. It does not preview: native judges the intent when it applies.</summary>
    public async Task<HaulInspection> InspectHaulAsync(Target target, CancellationToken ct)
    {
        var started = _clock.Now();
        try
        {
            _ = BridgeApi.IntentAction("inspect", target.Action);
        }
        catch (BridgeContractException e)
        {
            throw new EvidenceException(e);
        }
        var haul = target.Action.Haul!;
        var reply = await _native.ReadPawnsAsync(Boundary.Identity(target.Snapshot), [haul.Pawn.Value], ct).ConfigureAwait(false);
        var observed = reply.Outcome?.Observed ?? throw new HeldException();
        var current = Boundary.Context(observed.Context, target.Snapshot);
        var result = new HaulInspection { StartedAt = started, Snapshot = current, Tick = new Tick((observed.Context?.Tick).GetValueOrDefault()), ObservedAt = _clock.Now() };
        ct.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>Sends the intent through Actions/Apply.</summary>
    public Task<Receipt> WriteHaulAsync(Placement placement, CancellationToken ct) => Boundary.DispatchIntentAsync(placement, _writer, ct);
}
