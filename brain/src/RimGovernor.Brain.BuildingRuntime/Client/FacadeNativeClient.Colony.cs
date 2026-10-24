using RimGovernor.Brain.Bridge.Contracts;

namespace RimGovernor.Brain.BuildingRuntime;

//
// Identity, Tick and ReadColonyFacts hand back the whole reply and leave a failure or unavailable arm in it:
// the consumers (the clock coordinator's cleanup, the Decode* helpers) read the arm themselves.
public sealed partial class FacadeNativeClient
{
    private const string ReadColonyFactsMethod = "rimgovernor/observations_read_colony_facts";

    private const string CombatFrameMethod = "rimgovernor/snapshot_frame_combat";

    /// <summary>The loaded identity.</summary>
    internal Task<PbLifecycle.LoadedIdentity> IdentityAsync(CancellationToken cancellationToken) =>
        Facade.ReadIdentityAsync(new PbLifecycle.IdentityRequest(), cancellationToken);

    /// <summary>
    /// The bare observation scope, the identity's context and pause state.
    /// </summary>
    internal Task<PbLifecycle.LoadedTick> TickAsync(CancellationToken cancellationToken) =>
        Facade.ReadTickAsync(new PbLifecycle.TickRequest(), cancellationToken);

    /// <summary>The colony census: the planning form from the newest frame, the other form read live.</summary>
    internal async Task<PbObservations.ColonyFactsSnapshot> ReadColonyFactsAsync(PbCommon.Identity identity, bool planning, CancellationToken cancellationToken)
    {
        if (planning)
        {
            return await FrameReadAsync(ReadColonyFactsMethod, identity, view => view.Snapshot.ColonyFacts, cancellationToken).ConfigureAwait(false);
        }
        // The stream seeds the colony_facts section under the planning form only, so no frame carries this form.
        var request = new PbObservations.ColonyFactsRequest
        {
            Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
            Planning = false,
        };
        return await Facade.ReadColonyFactsAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Composes the step's snapshot, the live tick (scope and pause state), then the clock
    /// status the controller owns. A clock status that is unavailable leaves the snapshot's <c>ClockStatus</c> unset.
    /// </summary>
    internal async Task<PbObservations.BundleSnapshot?> ReadStepAsync(StepRequest request, CancellationToken cancellationToken)
    {
        var loaded = await TickAsync(cancellationToken).ConfigureAwait(false);
        var identity = loaded.Context!.Identity!;
        var step = new PbObservations.BundleSnapshot { Context = loaded.Context };
        if (request.ClockStatus)
        {
            try
            {
                step = step with { ClockStatus = await ReadClockStatusAsync(identity, cancellationToken).ConfigureAwait(false) };
            }
            catch (BridgeUnavailableException)
            {
                // The owned clock status is unavailable: the step goes on without it.
            }
        }
        return step;
    }
}
