using RimGovernor.Brain.Bridge.Contracts;

namespace RimGovernor.Brain.BuildingRuntime;

//
// Identity, Tick, Status and ReadColonyFacts hand back the whole reply and leave a failure or unavailable arm in it:
// the consumers (the clock coordinator's cleanup, the Decode* helpers) read the arm themselves.
public sealed partial class FacadeNativeClient
{
    private const string ReadColonyFactsMethod = "rimgovernor/observations_read_colony_facts";

    private const string ReadStatusMethod = "rimgovernor/observations_read_status";

    private const string CombatFrameMethod = "rimgovernor/snapshot_frame_combat";

    /// <summary>
    /// The hunt census parameters of every colony facts read: a direct read carries them, and the snapshot stream
    /// (<see cref="OpenFramesAsync"/>) hands them to each frame's colony facts read.
    /// </summary>
    private static readonly PbObservations.ColonyFactsRequest ColonyFactsParameters = new()
    {
        HerdRadius = HerdRadius,
    };

    /// <summary>The loaded identity.</summary>
    internal Task<PbLifecycle.IdentityReply> IdentityAsync(CancellationToken cancellationToken) =>
        Facade.ReadIdentityAsync(new PbLifecycle.IdentityRequest(), cancellationToken);

    /// <summary>
    /// The bare observation scope, the identity's context and pause state.
    /// </summary>
    internal Task<PbLifecycle.TickReply> TickAsync(CancellationToken cancellationToken) =>
        Facade.ReadTickAsync(new PbLifecycle.TickRequest(), cancellationToken);

    /// <summary>
    /// A status read that requests the context only (no colonists, no threats). With frames
    /// the read first waits for a frame past this client's last write, then reads live: the frame's status section is
    /// the emergency census, a different shape.
    /// </summary>
    internal async Task<PbObservations.StatusReply> StatusAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
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
                view => new FrameLookup<bool>(false, false, view.Snapshot.Emergency is not null),
                cancellationToken).ConfigureAwait(false);
        }
        return await Facade.ReadStatusAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The colony census, from the newest frame when it carries the planning form
    /// (the form the stream seeds the section under), else read live.
    /// </summary>
    internal async Task<PbObservations.ColonyFactsReply> ReadColonyFactsAsync(PbCommon.Identity identity, bool planning, CancellationToken cancellationToken)
    {
        var request = ColonyFactsRequest(identity, planning);
        PbObservations.ColonyFactsReply? reply = null;
        if (UseFrames)
        {
            var served = await FrameReadAsync(
                ReadColonyFactsMethod,
                identity,
                fallback: true,
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
        return reply;
    }

    /// <summary>
    /// The exact request <see cref="ReadColonyFactsAsync"/> issues; the stream seeds
    /// its colony_facts section under the planning form of it.
    /// </summary>
    internal static PbObservations.ColonyFactsRequest ColonyFactsRequest(PbCommon.Identity identity, bool planning) =>
        ColonyFactsParameters with
        {
            Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
            Planning = planning,
        };

    /// <summary>
    /// Composes the step's snapshot, the live tick (scope and pause state), then the clock
    /// status the controller owns. A clock status that is unavailable leaves the snapshot's <c>ClockStatus</c> unset.
    /// </summary>
    internal async Task<PbObservations.BundleSnapshot?> ReadStepAsync(StepRequest request, CancellationToken cancellationToken)
    {
        var tick = await TickAsync(cancellationToken).ConfigureAwait(false);
        var outcome = tick.Outcome;
        if (outcome.Unavailable is { } tickUnavailable)
        {
            throw Unavailable(tickUnavailable);
        }
        var loaded = outcome.Loaded!;
        var identity = loaded.Context!.Identity!;
        var step = new PbObservations.BundleSnapshot { Context = loaded.Context };
        if (request.ClockStatus)
        {
            try
            {
                step = step with { ClockStatus = await ReadOwnedClockStatusAsync(identity, cancellationToken).ConfigureAwait(false) };
            }
            catch (BridgeUnavailableException)
            {
                // The owned clock status is unavailable: the step goes on without it.
            }
        }
        return step;
    }
}
