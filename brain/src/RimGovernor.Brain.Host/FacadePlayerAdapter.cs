using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.PlayerApi;

namespace RimGovernor.Brain.Host;

/// <summary>
/// The player routes' native seams (PlayerApi): the presentation, notification, render-lease and lifecycle capabilities,
/// each bound to the client's core method of the same name. PlayerApi sits above BuildingRuntime, so the client cannot
/// implement these itself.
/// </summary>
internal sealed class FacadePlayerAdapter(FacadeNativeClient client) : IPresentationReader, IPresentationMediaWriter, INotificationReader, ILifecycleWriter
{
    public Task<PbPresentation.CameraReply> ReadCameraAsync(PbPresentation.ReadRequest request, CancellationToken cancellationToken) =>
        client.ReadCameraAsync(request, cancellationToken);

    public Task<PbPresentation.SelectionReply> ReadSelectionAsync(PbPresentation.ReadRequest request, CancellationToken cancellationToken) =>
        client.ReadSelectionAsync(request, cancellationToken);

    public Task<PbPresentation.ColonistRosterReply> ReadColonistRosterAsync(PbPresentation.ColonistRosterRequest request, CancellationToken cancellationToken) =>
        client.ReadColonistRosterAsync(request, cancellationToken);

    public Task<PbPresentation.RenderReply> ReadRenderStateAsync(PbPresentation.ReadRequest request, CancellationToken cancellationToken) =>
        client.ReadRenderStateAsync(request, cancellationToken);

    public Task<PbPresentation.RenderReply> DemandRenderingAsync(PbPresentation.RenderDemand request, CancellationToken cancellationToken) =>
        client.DemandRenderingAsync(request, cancellationToken);

    public Task<PbPresentation.NotificationsReply> ReadNotificationsAsync(PbPresentation.NotificationsRequest request, CancellationToken cancellationToken) =>
        client.ReadNotificationsAsync(request, cancellationToken);

    public Task<PbLifecycle.SaveReply> SaveAsync(PbLifecycle.SaveRequest request, CancellationToken cancellationToken) =>
        client.SaveAsync(request, cancellationToken);

    public Task<PbLifecycle.SaveReply> ReadSaveAsync(string requestId, CancellationToken cancellationToken) =>
        client.ReadSaveAsync(requestId, cancellationToken);

    public Task<PbLifecycle.LoadReply> LoadAsync(PbLifecycle.LoadRequest request, CancellationToken cancellationToken) =>
        client.LoadAsync(request, cancellationToken);

    public Task<PbLifecycle.LoadReply> ReadLoadAsync(string requestId, CancellationToken cancellationToken) =>
        client.ReadLoadAsync(requestId, cancellationToken);

    public Task<PbLifecycle.NewColonyReply> NewColonyAsync(PbLifecycle.NewColonyRequest request, CancellationToken cancellationToken) =>
        client.NewColonyAsync(request, cancellationToken);

    public Task<PbLifecycle.NewColonyReply> ReadNewColonyAsync(string requestId, CancellationToken cancellationToken) =>
        client.ReadNewColonyAsync(requestId, cancellationToken);

    public Task<PbLifecycle.MenuReply> MenuAsync(PbLifecycle.MenuRequest request, CancellationToken cancellationToken) =>
        client.MenuAsync(request, cancellationToken);

    public Task<PbLifecycle.MenuReply> ReadMenuAsync(string requestId, CancellationToken cancellationToken) =>
        client.ReadMenuAsync(requestId, cancellationToken);
}
