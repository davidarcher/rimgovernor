using RimGovernor.Brain.GameAccess;

namespace RimGovernor.Brain.Runtime;

//
// Identity, Tick and ReadColonyFacts hand back the whole reply and leave a failure or unavailable arm in it:
// the consumers (the clock coordinator's cleanup, the Decode* helpers) read the arm themselves.
public sealed partial class GameClient
{
    private const string ReadColonyFactsMethod = "observations_read_colony_facts";

    private const string CombatFrameMethod = "snapshot_frame_combat";

    /// <summary>The loaded identity.</summary>
    internal Task<RimGovernor.Brain.GameFacts.Lifecycle.LoadedIdentity> IdentityAsync(CancellationToken cancellationToken) =>
        Facade.ReadIdentityAsync(new RimGovernor.Brain.GameOrders.Lifecycle.IdentityRequest(), cancellationToken);

    /// <summary>
    /// The bare observation scope, the identity's context and pause state.
    /// </summary>
    internal Task<RimGovernor.Brain.GameFacts.Lifecycle.LoadedTick> TickAsync(CancellationToken cancellationToken) =>
        Facade.ReadTickAsync(new RimGovernor.Brain.GameOrders.Lifecycle.TickRequest(), cancellationToken);

    /// <summary>The complete colony census from the newest frame, shared with its entity and zone tables.</summary>
    internal Task<RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot> ReadColonyFactsAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken cancellationToken) =>
        FrameReadAsync(ReadColonyFactsMethod, identity, view => view.Snapshot.ColonyFacts, cancellationToken);

    /// <summary>
    /// Composes the step's snapshot, the live tick (scope and pause state), then the clock
    /// status the controller owns. A clock status that is unavailable leaves the snapshot's <c>ClockStatus</c> unset.
    /// </summary>
    internal async Task<RimGovernor.Brain.GameFacts.Observations.BundleSnapshot?> ReadStepAsync(StepRequest request, CancellationToken cancellationToken)
    {
        var loaded = await TickAsync(cancellationToken).ConfigureAwait(false);
        var identity = loaded.Context!.Identity!;
        var step = new RimGovernor.Brain.GameFacts.Observations.BundleSnapshot { Context = loaded.Context };
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
