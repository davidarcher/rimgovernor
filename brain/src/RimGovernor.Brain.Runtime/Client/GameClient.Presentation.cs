using RimGovernor.Brain.GameAccess;

namespace RimGovernor.Brain.Runtime;

// (PresentationMedia.DemandRendering) and ReadNotifications. The camera, selection, roster, render-state and notification
// reads answer their payload (the facade throws a refusal); a failure arm of the others throws what Failure builds.
public sealed partial class GameClient
{
    /// <summary>The camera, observation only.</summary>
    internal Task<RimGovernor.Brain.GameFacts.Presentation.CameraState> ReadCameraAsync(RimGovernor.Brain.GameOrders.Presentation.ReadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Facade.ReadCameraAsync(request, cancellationToken);
    }

    /// <summary>The selection, observation only.</summary>
    internal Task<RimGovernor.Brain.GameFacts.Presentation.SelectionSnapshot> ReadSelectionAsync(RimGovernor.Brain.GameOrders.Presentation.ReadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Facade.ReadSelectionAsync(request, cancellationToken);
    }

    /// <summary>The colonist roster.</summary>
    internal Task<RimGovernor.Brain.GameFacts.Presentation.ColonistRoster> ReadColonistRosterAsync(RimGovernor.Brain.GameOrders.Presentation.ColonistRosterRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Facade.ReadColonistsAsync(request, cancellationToken);
    }

    /// <summary>
    /// A zero-side-effect observation that never extends or shortens the native rendering
    /// lease. <see cref="DemandRenderingAsync"/> is the only call that changes lease state.
    /// </summary>
    internal Task<RimGovernor.Brain.GameFacts.Presentation.RenderStatus> ReadRenderStateAsync(RimGovernor.Brain.GameOrders.Presentation.ReadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Facade.ReadRenderStateAsync(request, cancellationToken);
    }

    /// <summary>
    /// Asks the native mod to keep rendering for the lease seconds of real
    /// time; zero only reads status. The viewer carries no authority of its own.
    /// </summary>
    internal async Task<RimGovernor.Brain.GameFacts.Presentation.RenderReply> DemandRenderingAsync(RimGovernor.Brain.GameOrders.Presentation.RenderDemand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.LeaseSeconds > 30)
        {
            throw BridgeContractException.Contract("render demand lease exceeds 30 seconds");
        }

        var reply = await Facade.DemandRenderingAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply.Outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }

        return reply;
    }

    /// <summary>
    /// Only observes. Partial listings stay partial (the listing says so); this read never acknowledges or dismisses anything.
    /// </summary>
    internal Task<RimGovernor.Brain.GameFacts.Presentation.NotificationsSnapshot> ReadNotificationsAsync(RimGovernor.Brain.GameOrders.Presentation.NotificationsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.LetterLimit < 1 || request.MessageLimit < 1 || request.AlertLimit < 1)
        {
            throw BridgeContractException.Contract("notification limit below 1");
        }

        return Facade.ReadNotificationsAsync(request, cancellationToken);
    }
}
