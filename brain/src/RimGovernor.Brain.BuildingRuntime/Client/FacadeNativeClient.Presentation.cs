using RimGovernor.Brain.Bridge.Contracts;

namespace RimGovernor.Brain.BuildingRuntime;

// (PresentationMedia.DemandRendering) and ReadNotifications. The camera, selection, roster, render-state and notification
// reads answer their payload (the facade throws a refusal); a failure arm of the others throws what Failure builds.
public sealed partial class FacadeNativeClient
{
    /// <summary>The camera, observation only.</summary>
    internal Task<PbPresentation.CameraState> ReadCameraAsync(PbPresentation.ReadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Facade.ReadCameraAsync(request, cancellationToken);
    }

    /// <summary>The selection, observation only.</summary>
    internal Task<PbPresentation.SelectionSnapshot> ReadSelectionAsync(PbPresentation.ReadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Facade.ReadSelectionAsync(request, cancellationToken);
    }

    /// <summary>The colonist roster.</summary>
    internal Task<PbPresentation.ColonistRoster> ReadColonistRosterAsync(PbPresentation.ColonistRosterRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Facade.ReadColonistsAsync(request, cancellationToken);
    }

    /// <summary>
    /// A zero-side-effect observation that never extends or shortens the native rendering
    /// lease. <see cref="DemandRenderingAsync"/> is the only call that changes lease state.
    /// </summary>
    internal Task<PbPresentation.RenderStatus> ReadRenderStateAsync(PbPresentation.ReadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Facade.ReadRenderStateAsync(request, cancellationToken);
    }

    /// <summary>
    /// Asks the native mod to keep rendering for the lease seconds of real
    /// time; zero only reads status. The viewer carries no authority of its own.
    /// </summary>
    internal async Task<PbPresentation.RenderReply> DemandRenderingAsync(PbPresentation.RenderDemand request, CancellationToken cancellationToken)
    {
        if (request is null || request.Viewer is null)
        {
            throw BridgeContractException.Contract("render demand viewer required");
        }

        if (request.LeaseSeconds.GetValueOrDefault() > 30)
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
    /// Only observes. Partial listings and unknown optional facts remain partial or
    /// unknown; this read never acknowledges or dismisses anything.
    /// </summary>
    internal Task<PbPresentation.NotificationsSnapshot> ReadNotificationsAsync(PbPresentation.NotificationsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        foreach (var limit in new[] { request.LetterLimit, request.MessageLimit, request.AlertLimit })
        {
            if (limit is < 1)
            {
                throw BridgeContractException.Contract("notification limit below 1");
            }
        }

        return Facade.ReadNotificationsAsync(request, cancellationToken);
    }
}
