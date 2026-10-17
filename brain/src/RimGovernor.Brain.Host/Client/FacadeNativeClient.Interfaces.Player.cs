using RimGovernor.Brain.PlayerApi;

namespace RimGovernor.Brain.Host;

// The player routes' native seams (PlayerApi): the presentation, notification, render-lease and lifecycle capabilities
// Go's httpapi.Config took from the bridge client. Each is bound to the core method of the same Go name.
internal sealed partial class FacadeNativeClient : IPresentationReader, IPresentationMediaWriter, INotificationReader, ILifecycleWriter
{
    Task<PbPresentation.CameraReply> IPresentationReader.ReadCameraAsync(PbPresentation.ReadRequest request, CancellationToken cancellationToken) =>
        ReadCameraAsync(request, cancellationToken);

    Task<PbPresentation.SelectionReply> IPresentationReader.ReadSelectionAsync(PbPresentation.ReadRequest request, CancellationToken cancellationToken) =>
        ReadSelectionAsync(request, cancellationToken);

    Task<PbPresentation.ColonistRosterReply> IPresentationReader.ReadColonistRosterAsync(PbPresentation.ColonistRosterRequest request, CancellationToken cancellationToken) =>
        ReadColonistRosterAsync(request, cancellationToken);

    Task<PbPresentation.RenderReply> IPresentationReader.ReadRenderStateAsync(PbPresentation.ReadRequest request, CancellationToken cancellationToken) =>
        ReadRenderStateAsync(request, cancellationToken);

    Task<PbPresentation.RenderReply> IPresentationMediaWriter.DemandRenderingAsync(PbPresentation.RenderDemand request, CancellationToken cancellationToken) =>
        DemandRenderingAsync(request, cancellationToken);

    Task<PbPresentation.NotificationsReply> INotificationReader.ReadNotificationsAsync(PbPresentation.NotificationsRequest request, CancellationToken cancellationToken) =>
        ReadNotificationsAsync(request, cancellationToken);

    Task<PbLifecycle.SaveReply> ILifecycleWriter.SaveAsync(PbLifecycle.SaveRequest request, CancellationToken cancellationToken) =>
        SaveAsync(request, cancellationToken);

    Task<PbLifecycle.SaveReply> ILifecycleWriter.ReadSaveAsync(string requestId, CancellationToken cancellationToken) =>
        ReadSaveAsync(requestId, cancellationToken);

    Task<PbLifecycle.LoadReply> ILifecycleWriter.LoadAsync(PbLifecycle.LoadRequest request, CancellationToken cancellationToken) =>
        LoadAsync(request, cancellationToken);

    Task<PbLifecycle.LoadReply> ILifecycleWriter.ReadLoadAsync(string requestId, CancellationToken cancellationToken) =>
        ReadLoadAsync(requestId, cancellationToken);

    Task<PbLifecycle.NewColonyReply> ILifecycleWriter.NewColonyAsync(PbLifecycle.NewColonyRequest request, CancellationToken cancellationToken) =>
        NewColonyAsync(request, cancellationToken);

    Task<PbLifecycle.NewColonyReply> ILifecycleWriter.ReadNewColonyAsync(string requestId, CancellationToken cancellationToken) =>
        ReadNewColonyAsync(requestId, cancellationToken);

    Task<PbLifecycle.MenuReply> ILifecycleWriter.MenuAsync(PbLifecycle.MenuRequest request, CancellationToken cancellationToken) =>
        MenuAsync(request, cancellationToken);

    Task<PbLifecycle.MenuReply> ILifecycleWriter.ReadMenuAsync(string requestId, CancellationToken cancellationToken) =>
        ReadMenuAsync(requestId, cancellationToken);
}
