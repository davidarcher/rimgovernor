using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.PlayerApi;

namespace RimGovernor.Brain.Host;

/// <summary>
/// The player routes' native seams (PlayerApi): the presentation, notification, render-lease and lifecycle capabilities,
/// each bound to the client's core method of the same name. PlayerApi sits above Runtime, so the client cannot
/// implement these itself.
/// </summary>
internal sealed class FacadePlayerAdapter(GameClient client) : IPresentationReader, IPresentationMediaWriter, INotificationReader, ILifecycleWriter
{
    public Task<RimGovernor.Brain.GameFacts.Presentation.CameraState> ReadCameraAsync(RimGovernor.Brain.GameOrders.Presentation.ReadRequest request, CancellationToken cancellationToken) =>
        client.ReadCameraAsync(request, cancellationToken);

    public Task<RimGovernor.Brain.GameFacts.Presentation.SelectionSnapshot> ReadSelectionAsync(RimGovernor.Brain.GameOrders.Presentation.ReadRequest request, CancellationToken cancellationToken) =>
        client.ReadSelectionAsync(request, cancellationToken);

    public Task<RimGovernor.Brain.GameFacts.Presentation.ColonistRoster> ReadColonistRosterAsync(RimGovernor.Brain.GameOrders.Presentation.ColonistRosterRequest request, CancellationToken cancellationToken) =>
        client.ReadColonistRosterAsync(request, cancellationToken);

    public Task<RimGovernor.Brain.GameFacts.Presentation.RenderStatus> ReadRenderStateAsync(RimGovernor.Brain.GameOrders.Presentation.ReadRequest request, CancellationToken cancellationToken) =>
        client.ReadRenderStateAsync(request, cancellationToken);

    public Task<RimGovernor.Brain.GameFacts.Presentation.RenderReply> DemandRenderingAsync(RimGovernor.Brain.GameOrders.Presentation.RenderDemand request, CancellationToken cancellationToken) =>
        client.DemandRenderingAsync(request, cancellationToken);

    public Task<RimGovernor.Brain.GameFacts.Presentation.NotificationsSnapshot> ReadNotificationsAsync(RimGovernor.Brain.GameOrders.Presentation.NotificationsRequest request, CancellationToken cancellationToken) =>
        client.ReadNotificationsAsync(request, cancellationToken);

    public Task<RimGovernor.Brain.GameFacts.Lifecycle.SaveReply> SaveAsync(RimGovernor.Brain.GameOrders.Lifecycle.SaveRequest request, CancellationToken cancellationToken) =>
        client.SaveAsync(request, cancellationToken);

    public Task<RimGovernor.Brain.GameFacts.Lifecycle.SaveReply> ReadSaveAsync(string requestId, CancellationToken cancellationToken) =>
        client.ReadSaveAsync(requestId, cancellationToken);

    public Task<RimGovernor.Brain.GameFacts.Lifecycle.LoadReply> LoadAsync(RimGovernor.Brain.GameOrders.Lifecycle.LoadRequest request, CancellationToken cancellationToken) =>
        client.LoadAsync(request, cancellationToken);

    public Task<RimGovernor.Brain.GameFacts.Lifecycle.LoadReply> ReadLoadAsync(string requestId, CancellationToken cancellationToken) =>
        client.ReadLoadAsync(requestId, cancellationToken);

    public Task<RimGovernor.Brain.GameFacts.Lifecycle.NewColonyReply> NewColonyAsync(RimGovernor.Brain.GameOrders.Lifecycle.NewColonyRequest request, CancellationToken cancellationToken) =>
        client.NewColonyAsync(request, cancellationToken);

    public Task<RimGovernor.Brain.GameFacts.Lifecycle.NewColonyReply> ReadNewColonyAsync(string requestId, CancellationToken cancellationToken) =>
        client.ReadNewColonyAsync(requestId, cancellationToken);

    public Task<RimGovernor.Brain.GameFacts.Lifecycle.MenuReply> MenuAsync(RimGovernor.Brain.GameOrders.Lifecycle.MenuRequest request, CancellationToken cancellationToken) =>
        client.MenuAsync(request, cancellationToken);

    public Task<RimGovernor.Brain.GameFacts.Lifecycle.MenuReply> ReadMenuAsync(string requestId, CancellationToken cancellationToken) =>
        client.ReadMenuAsync(requestId, cancellationToken);
}
