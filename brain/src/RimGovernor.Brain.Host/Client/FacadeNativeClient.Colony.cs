using RimGovernor.Brain.Bridge.Contracts;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Host;

// Go protobuf.go (Identity, Tick, Status), colony.go (ReadColonyFacts, ColonyFactsRequest) and step.go (ReadStep).
//
// Identity, Tick, Status and ReadColonyFacts hand back the whole reply and leave a well-formed failure or unavailable
// arm in it: the consumers (the clock coordinator's cleanup, the Decode* helpers) read the arm themselves, as they did
// from Go's (reply, error) pair where the error only carried the arm. A reply that is malformed, and an arm whose
// payload is malformed, still throw the contract error.
internal sealed partial class FacadeNativeClient
{
    private const string ReadColonyFactsMethod = "rimgovernor/observations_read_colony_facts";

    private const string ReadStatusMethod = "rimgovernor/observations_read_status";

    private const string CombatFrameMethod = "rimgovernor/snapshot_frame_combat";

    /// <summary>Go <c>Client.Identity</c>: the loaded identity with its validated capability list.</summary>
    internal async Task<(PbLifecycle.IdentityReply Reply, BridgeResult Receipt)> IdentityAsync(CancellationToken cancellationToken)
    {
        var reply = await Facade.ReadIdentityAsync(new PbLifecycle.IdentityRequest(), cancellationToken).ConfigureAwait(false);
        var outcome = reply.Outcome;
        if (outcome?.Loaded is { } loaded)
        {
            BridgeFunctions.ValidateContext(loaded.Context);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var capability in loaded.Capabilities)
            {
                var support = capability.Support is { } value ? (int)value : 0;
                var name = capability.FullMethodName ?? "";
                if (support < 1 || support > 3 || !BridgeFunctions.IsValidId(name) || !BridgeFunctions.Diagnostic(capability.Detail is not null, capability.Detail ?? "") || !seen.Add(name))
                {
                    throw BridgeFunctions.Contract("invalid capability");
                }
            }
        }
        else if (outcome?.Unavailable is { } unavailable)
        {
            BridgeFunctions.ValidateUnavailable(unavailable);
        }
        else if (outcome?.Failure is { } failure)
        {
            BridgeFunctions.ValidateFailure(failure);
        }
        else
        {
            throw BridgeFunctions.Contract(outcome is null ? "identity outcome missing" : "missing loaded identity");
        }
        return (reply, NoReceipt);
    }

    /// <summary>
    /// Go <c>Client.Tick</c>: the bare observation scope, the identity's context and pause state without the
    /// capability list.
    /// </summary>
    internal async Task<(PbLifecycle.TickReply Reply, BridgeResult Receipt)> TickAsync(CancellationToken cancellationToken)
    {
        var reply = await Facade.ReadTickAsync(new PbLifecycle.TickRequest(), cancellationToken).ConfigureAwait(false);
        var outcome = reply.Outcome;
        if (outcome?.Loaded is { } loaded)
        {
            BridgeFunctions.ValidateContext(loaded.Context);
        }
        else if (outcome?.Unavailable is { } unavailable)
        {
            BridgeFunctions.ValidateUnavailable(unavailable);
        }
        else if (outcome?.Failure is { } failure)
        {
            BridgeFunctions.ValidateFailure(failure);
        }
        else
        {
            throw BridgeFunctions.Contract("tick outcome missing");
        }
        return (reply, NoReceipt);
    }

    /// <summary>
    /// Go <c>Client.Status</c>: a status read that requests the context only (no colonists, no threats). With frames
    /// the read first waits for a frame past this client's last write, then reads live: the frame's status section is
    /// the emergency census, a different shape.
    /// </summary>
    internal async Task<(PbObservations.StatusReply Reply, BridgeResult Receipt)> StatusAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        BridgeFunctions.ValidateIdentity(identity);
        var request = new PbObservations.StatusRequest
        {
            Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
            Colonists = false,
            Threats = false,
        };
        if (UseFrames)
        {
            await FrameReadAsync(
                ReadStatusMethod,
                identity,
                fallback: true,
                anyFrame: false,
                view => new FrameLookup<bool>(false, false, view.Snapshot.Emergency is not null),
                cancellationToken).ConfigureAwait(false);
        }
        var reply = await Facade.ReadStatusAsync(request, cancellationToken).ConfigureAwait(false);
        var outcome = reply.Outcome;
        if (outcome?.Observed is { } status)
        {
            Exception? problem = null;
            try
            {
                BridgeFunctions.ValidateContext(status.Context);
                if (!BridgeFunctions.SameIdentity(status.Context!.Identity, identity))
                {
                    problem = BridgeFunctions.Contract("status identity mismatch");
                }
            }
            catch (BridgeContractException invalid)
            {
                problem = invalid;
            }
            if (status.Colonists.Count != 0 || status.Threats is not null)
            {
                problem = BridgeFunctions.Contract("unrequested status section");
            }
            foreach (var issue in status.Issues)
            {
                if (!BridgeFunctions.IsValidId(issue.Field ?? ""))
                {
                    problem = BridgeFunctions.Contract("invalid read issue");
                    break;
                }
                try
                {
                    BridgeFunctions.ValidateUnavailable(issue.Unavailable);
                }
                catch (BridgeContractException invalid)
                {
                    problem = invalid;
                    break;
                }
            }
            if (problem is not null)
            {
                throw problem;
            }
        }
        else if (outcome?.Unavailable is { } unavailable)
        {
            BridgeFunctions.ValidateUnavailable(unavailable);
        }
        else if (outcome?.Failure is { } failure)
        {
            BridgeFunctions.ValidateFailure(failure);
        }
        else
        {
            throw BridgeFunctions.Contract("status outcome missing");
        }
        return (reply, NoReceipt);
    }

    /// <summary>
    /// Go <c>Client.ReadColonyFacts</c>: the colony census, from the newest frame when it carries the planning form
    /// (the form the stream seeds the section under), else read live. Validated against the expected identity.
    /// </summary>
    internal async Task<(PbObservations.ColonyFactsReply Reply, BridgeResult Receipt)> ReadColonyFactsAsync(PbCommon.Identity identity, bool planning, CancellationToken cancellationToken)
    {
        BridgeFunctions.ValidateIdentity(identity);
        var request = ColonyFactsRequest(identity, planning);
        PbObservations.ColonyFactsReply? reply = null;
        if (UseFrames)
        {
            var served = await FrameReadAsync(
                ReadColonyFactsMethod,
                identity,
                fallback: true,
                anyFrame: false,
                view =>
                {
                    var facts = view.Snapshot.ColonyFacts;
                    return new FrameLookup<PbObservations.ColonyFactsSnapshot>(planning && facts is not null, facts, facts is not null);
                },
                cancellationToken).ConfigureAwait(false);
            if (served.Served)
            {
                reply = new PbObservations.ColonyFactsReply { Outcome = served.Value! };
            }
        }
        reply ??= await Facade.ReadColonyFactsAsync(request, cancellationToken).ConfigureAwait(false);
        var outcome = reply.Outcome;
        if (outcome?.Failure is { } failure)
        {
            BridgeFunctions.ValidateFailure(failure);
        }
        else if (outcome?.Unavailable is { } unavailable)
        {
            BridgeFunctions.ValidateUnavailable(unavailable);
        }
        else if (outcome?.Observed is { } observed)
        {
            BridgeFunctions.ValidateColonyFacts(observed, request.Scope!.ExpectedIdentity);
            if (observed.Planning?.Outcome?.Observed is not null && !planning)
            {
                throw BridgeFunctions.Contract("unrequested planning facts");
            }
        }
        else
        {
            throw BridgeFunctions.Contract("missing colony outcome");
        }
        return (reply, NoReceipt);
    }

    /// <summary>
    /// Go <c>ColonyFactsRequest</c>: the exact request <see cref="ReadColonyFactsAsync"/> issues; the stream seeds
    /// its colony_facts section under the planning form of it.
    /// </summary>
    internal static PbObservations.ColonyFactsRequest ColonyFactsRequest(PbCommon.Identity identity, bool planning) =>
        new()
        {
            Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
            Planning = planning,
            HerdRadius = HerdRadius,
            HuntRouteBudgetMs = HuntRouteBudgetMs,
            HuntPredatorMarginCells = P.PolicyFunctions.HuntPredatorMarginCells,
        };

    /// <summary>
    /// Go <c>Client.ReadStep</c>: composes the step's snapshot, the live tick (scope and pause state), then the clock
    /// status the controller owns, then the emergency census, served from the snapshot stream. A clock status that is
    /// unavailable leaves the snapshot's <c>ClockStatus</c> unset (Go returned the snapshot with the unavailable error).
    /// </summary>
    internal async Task<PbObservations.BundleSnapshot?> ReadStepAsync(BridgeFunctions.StepRequest request, CancellationToken cancellationToken)
    {
        if (request.Identity is not null)
        {
            BridgeFunctions.ValidateIdentity(request.Identity);
        }
        var (tick, _) = await TickAsync(cancellationToken).ConfigureAwait(false);
        var outcome = tick.Outcome;
        if (outcome?.Failure is { } tickFailure)
        {
            throw Failure(tickFailure);
        }
        if (outcome?.Unavailable is { } tickUnavailable)
        {
            throw Unavailable(tickUnavailable);
        }
        if (outcome?.Loaded is not { Paused: not null } loaded)
        {
            throw BridgeFunctions.Contract("step tick missing");
        }
        var identity = loaded.Context!.Identity!;
        if (request.Identity is not null && !BridgeFunctions.SameIdentity(identity, request.Identity))
        {
            throw new BridgeRefusedException("step identity changed");
        }
        var step = new PbObservations.BundleSnapshot { Context = loaded.Context, Paused = loaded.Paused };
        var sections = RequireFrameSections();
        if (request.ClockStatus)
        {
            try
            {
                step = step with { ClockStatus = await sections.ReadClockStatusAsync(identity, cancellationToken).ConfigureAwait(false) };
            }
            catch (BridgeUnavailableException)
            {
                // The owned clock status is unavailable: the step goes on without it.
            }
        }
        if (request.Emergency)
        {
            var observed = await ReadEmergencyStatusAsync(sections, identity, cancellationToken).ConfigureAwait(false);
            // The census references resolve against the pawn table.
            var table = await FramePawnSnapshotAsync(identity, cancellationToken).ConfigureAwait(false);
            var pawns = sections.PawnTable(table, identity);
            sections.DecodeEmergencyStatus(observed, pawns, identity);
            step = step with { Emergency = observed, Pawns = table };
            // A drop-pod raid's raiders are in no census row until the pods open; the stream's arrival rows carry it.
            if (UseFrames)
            {
                var combat = await FrameSectionAsync(CombatFrameMethod, identity, anyFrame: false, view => view.Snapshot, cancellationToken).ConfigureAwait(false);
                step = step with { CombatEvents = PodArrivals(combat.CombatEvents) };
            }
        }
        return step;
    }

    /// <summary>The emergency census read of a step: the frame's emergency section, else the live status read.</summary>
    private async Task<PbObservations.StatusSnapshot> ReadEmergencyStatusAsync(IFrameSections sections, PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        var request = sections.EmergencyRequest(identity);
        if (UseFrames)
        {
            var served = await FrameReadAsync(
                ReadStatusMethod,
                identity,
                fallback: true,
                anyFrame: false,
                view =>
                {
                    var emergency = view.Snapshot.Emergency;
                    return new FrameLookup<PbObservations.StatusSnapshot>(emergency is not null, emergency, emergency is not null);
                },
                cancellationToken).ConfigureAwait(false);
            if (served.Served)
            {
                return served.Value!;
            }
        }
        var reply = await Facade.ReadStatusAsync(request, cancellationToken).ConfigureAwait(false);
        var outcome = reply.Outcome;
        if (outcome?.Observed is { } observed)
        {
            return observed;
        }
        if (outcome?.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }
        if (outcome?.Failure is { } failure)
        {
            throw Failure(failure);
        }
        throw BridgeFunctions.Contract("emergency status outcome missing");
    }
}
