using RimGovernor.Brain.Bridge.Contracts;
using static RimGovernor.Brain.Bridge.Contracts.BridgeFunctions;

namespace RimGovernor.Brain.BuildingRuntime;

// Go presentation_reads.go (ReadCamera, ReadSelection, ReadColonistRoster, ReadRenderState), presentation_media.go
// (PresentationMedia.DemandRendering) and notifications.go (ReadNotifications). A failure arm throws what Failure
// builds, as Go returned it as an error alongside the reply.
public sealed partial class FacadeNativeClient
{
    /// <summary>Go <c>Client.ReadCamera</c>: the camera, observation only.</summary>
    internal async Task<PbPresentation.CameraReply> ReadCameraAsync(PbPresentation.ReadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateIdentity(request.Identity);
        var reply = await Facade.ReadCameraAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply.Outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }

        return reply;
    }

    /// <summary>Go <c>Client.ReadSelection</c>: the selection, observation only.</summary>
    internal async Task<PbPresentation.SelectionReply> ReadSelectionAsync(PbPresentation.ReadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateIdentity(request.Identity);
        var reply = await Facade.ReadSelectionAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply.Outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }

        return reply;
    }

    /// <summary>Go <c>Client.ReadColonistRoster</c>: the colonist roster.</summary>
    internal async Task<PbPresentation.ColonistRosterReply> ReadColonistRosterAsync(PbPresentation.ColonistRosterRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateIdentity(request.Identity);
        if (request.CurrentMapOnly is null)
        {
            throw Contract("currentMapOnly presence required");
        }

        var reply = await Facade.ReadColonistsAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply.Outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }

        return reply;
    }

    /// <summary>
    /// Go <c>Client.ReadRenderState</c>: a zero-side-effect observation that never extends or shortens the native rendering
    /// lease. <see cref="DemandRenderingAsync"/> is the only call that changes lease state.
    /// </summary>
    internal async Task<PbPresentation.RenderReply> ReadRenderStateAsync(PbPresentation.ReadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateIdentity(request.Identity);
        var reply = await Facade.ReadRenderStateAsync(request, cancellationToken).ConfigureAwait(false);
        return PresentationRenderOutcome(reply);
    }

    /// <summary>
    /// Go <c>PresentationMedia.DemandRendering</c>: asks the native mod to keep rendering for the lease seconds of real
    /// time; zero only reads status. The viewer carries no authority of its own.
    /// </summary>
    internal async Task<PbPresentation.RenderReply> DemandRenderingAsync(PbPresentation.RenderDemand request, CancellationToken cancellationToken)
    {
        if (request is null || request.Viewer is null)
        {
            throw Contract("render demand viewer required");
        }

        if (request.Viewer.ViewerId is not null && !IsValidId(request.Viewer.ViewerId))
        {
            throw Contract("invalid viewer id");
        }

        if (request.LeaseSeconds.GetValueOrDefault() > 30)
        {
            throw Contract("render demand lease exceeds 30 seconds");
        }

        var reply = await Facade.DemandRenderingAsync(request, cancellationToken).ConfigureAwait(false);
        return PresentationRenderOutcome(reply);
    }

    private static PbPresentation.RenderReply PresentationRenderOutcome(PbPresentation.RenderReply reply)
    {
        if (reply.Outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }

        return reply;
    }

    /// <summary>
    /// Go <c>Client.ReadNotifications</c>: only observes. Partial listings and unknown optional facts remain partial or
    /// unknown; this read never acknowledges or dismisses anything.
    /// </summary>
    internal async Task<PbPresentation.NotificationsReply> ReadNotificationsAsync(PbPresentation.NotificationsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        foreach (var limit in new[] { request.LetterLimit, request.MessageLimit, request.AlertLimit })
        {
            if (limit is < 1)
            {
                throw Contract("notification limit below 1");
            }
        }

        var reply = await Facade.ReadNotificationsAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply.Outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }

        return reply;
    }
}
