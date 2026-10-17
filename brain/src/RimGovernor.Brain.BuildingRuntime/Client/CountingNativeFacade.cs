using System.Runtime.CompilerServices;
using System.Threading.Channels;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Bridge;
using Authority = RimGovernor.Contracts.Wire.Authority;
using Clock = RimGovernor.Contracts.Wire.Clock;
using Lifecycle = RimGovernor.Contracts.Wire.Lifecycle;
using Mirror = RimGovernor.Contracts.Wire.Mirror;
using Obs = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Placement = RimGovernor.Contracts.Wire.Placement;
using Presentation = RimGovernor.Contracts.Wire.Presentation;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// Counts every native call into the read tally a scheduler step or a dispatch attached
/// (<see cref="BridgeFunctions.CountNativeRead"/>; Go <c>readTallyFrom(ctx).add</c> in <c>Client.call</c>), under the
/// <see cref="INativeFacade"/> method called, errors and cancellations included. It wraps the facade
/// the client calls, so every core method is counted once per round trip with no per-method bookkeeping.
/// </summary>
internal sealed class CountingNativeFacade(INativeFacade inner) : INativeFacade
{
    public ChannelReader<NativeFrame> Frames => inner.Frames;

    public Task<Lifecycle.IdentityReply> ReadIdentityAsync(Lifecycle.IdentityRequest request, CancellationToken cancellationToken) => Count(() => inner.ReadIdentityAsync(request, cancellationToken));

    public Task<Lifecycle.TickReply> ReadTickAsync(Lifecycle.TickRequest request, CancellationToken cancellationToken) => Count(() => inner.ReadTickAsync(request, cancellationToken));

    public Task<Lifecycle.GovernorStateReply> ReadGovernorStateAsync(Lifecycle.GovernorStateRequest request, CancellationToken cancellationToken) => Count(() => inner.ReadGovernorStateAsync(request, cancellationToken));

    public Task<Lifecycle.GovernorStateReply> PutGovernorStateBatchAsync(Lifecycle.PutGovernorStateBatchRequest request, CancellationToken cancellationToken) => Count(() => inner.PutGovernorStateBatchAsync(request, cancellationToken));

    public Task<Lifecycle.WaitSaveSignalReply> WaitSaveSignalAsync(Lifecycle.WaitSaveSignalRequest request, CancellationToken cancellationToken) => Count(() => inner.WaitSaveSignalAsync(request, cancellationToken));

    public Task<Lifecycle.FlushDoneReply> FlushDoneAsync(Lifecycle.FlushDoneRequest request, CancellationToken cancellationToken) => Count(() => inner.FlushDoneAsync(request, cancellationToken));

    public Task<Authority.StatusReply> ReadAuthorityStatusAsync(Authority.StatusRequest request, CancellationToken cancellationToken) => Count(() => inner.ReadAuthorityStatusAsync(request, cancellationToken));

    public Task<Authority.ControlReply> ControlAuthorityAsync(Authority.ControlRequest request, CancellationToken cancellationToken) => Count(() => inner.ControlAuthorityAsync(request, cancellationToken));

    public Task<Placement.PlacementReply> PreviewPlacementAsync(Placement.PlacementRequest request, CancellationToken cancellationToken) => Count(() => inner.PreviewPlacementAsync(request, cancellationToken));

    public Task<Operations.ZonePreviewReply> PreviewZoneAsync(Operations.ZonePreviewRequest request, CancellationToken cancellationToken) => Count(() => inner.PreviewZoneAsync(request, cancellationToken));

    public Task<Obs.StatusReply> ReadStatusAsync(Obs.StatusRequest request, CancellationToken cancellationToken) => Count(() => inner.ReadStatusAsync(request, cancellationToken));

    public Task<Obs.ColonyFactsReply> ReadColonyFactsAsync(Obs.ColonyFactsRequest request, CancellationToken cancellationToken) => Count(() => inner.ReadColonyFactsAsync(request, cancellationToken));

    public Task<Obs.GetCellsReply> GetCellsAsync(Obs.GetCellsRequest request, CancellationToken cancellationToken) => Count(() => inner.GetCellsAsync(request, cancellationToken));

    public Task<Obs.ListBuildingsReply> ListBuildingsAsync(Obs.ListBuildingsRequest request, CancellationToken cancellationToken) => Count(() => inner.ListBuildingsAsync(request, cancellationToken));

    public Task<Obs.ListPawnsReply> ListPawnsAsync(Obs.ListPawnsRequest request, CancellationToken cancellationToken) => Count(() => inner.ListPawnsAsync(request, cancellationToken));
    public Task<Obs.ActivityTaskRead> ReadActivityTasksAsync(Obs.ActivityTaskRequest request, CancellationToken cancellationToken) => Count(() => inner.ReadActivityTasksAsync(request, cancellationToken));
    public Task<Obs.ConstructionActivityRead> ReadConstructionActivityAsync(Obs.ConstructionActivityRequest request, CancellationToken cancellationToken) => Count(() => inner.ReadConstructionActivityAsync(request, cancellationToken));
    public Task<Obs.RecipeActivityRead> ReadRecipeActivityAsync(Obs.RecipeActivityRequest request, CancellationToken cancellationToken) => Count(() => inner.ReadRecipeActivityAsync(request, cancellationToken));

    public Task<Obs.ListSuppliesReply> ListSuppliesAsync(Obs.ListSuppliesRequest request, CancellationToken cancellationToken) => Count(() => inner.ListSuppliesAsync(request, cancellationToken));

    public Task<Obs.ListRoomsReply> ListRoomsAsync(Obs.ListRoomsRequest request, CancellationToken cancellationToken) => Count(() => inner.ListRoomsAsync(request, cancellationToken));

    public Task<Obs.ResearchReply> ReadResearchAsync(Obs.ResearchRequest request, CancellationToken cancellationToken) => Count(() => inner.ReadResearchAsync(request, cancellationToken));

    public Task<Obs.DefinitionCatalogReply> ReadDefinitionCatalogAsync(Obs.DefinitionCatalogRequest request, CancellationToken cancellationToken) => Count(() => inner.ReadDefinitionCatalogAsync(request, cancellationToken));

    public Task<Obs.BillsReply> ReadBillsAsync(Obs.BillsRequest request, CancellationToken cancellationToken) => Count(() => inner.ReadBillsAsync(request, cancellationToken));

    public Task<Obs.RecipesReply> ReadRecipesAsync(Obs.RecipesRequest request, CancellationToken cancellationToken) => Count(() => inner.ReadRecipesAsync(request, cancellationToken));

    public Task<Operations.ApplyReply> ApplyAsync(Operations.ApplyRequest request, CancellationToken cancellationToken) => Count(() => inner.ApplyAsync(request, cancellationToken));

    public Task<Receipts.LookupReply> LookupReceiptAsync(Receipts.LookupRequest request, CancellationToken cancellationToken) => Count(() => inner.LookupReceiptAsync(request, cancellationToken));

    public Task<Clock.StatusReply> RunClockAsync(Clock.RunRequest request, CancellationToken cancellationToken) => Count(() => inner.RunClockAsync(request, cancellationToken));

    public Task<Clock.StatusReply> HoldClockAsync(Clock.HoldRequest request, CancellationToken cancellationToken) => Count(() => inner.HoldClockAsync(request, cancellationToken));

    public Task<Clock.HeartbeatReply> HeartbeatClockAsync(Clock.HeartbeatRequest request, CancellationToken cancellationToken) => Count(() => inner.HeartbeatClockAsync(request, cancellationToken));

    public Task<Clock.StatusReply> ReadClockStatusAsync(Clock.StatusRequest request, CancellationToken cancellationToken) => Count(() => inner.ReadClockStatusAsync(request, cancellationToken));

    // Opening the colony events reads nothing from the game: it is passed through uncounted.
    public Task<ChannelReader<ColonyEvent>> OpenColonyEventsAsync(CancellationToken cancellationToken) => inner.OpenColonyEventsAsync(cancellationToken);

    public long ColonyEventsLost => inner.ColonyEventsLost;

    public Task<Presentation.CameraReply> ReadCameraAsync(Presentation.ReadRequest request, CancellationToken cancellationToken) => Count(() => inner.ReadCameraAsync(request, cancellationToken));

    public Task<Presentation.SelectionReply> ReadSelectionAsync(Presentation.ReadRequest request, CancellationToken cancellationToken) => Count(() => inner.ReadSelectionAsync(request, cancellationToken));

    public Task<Presentation.ColonistRosterReply> ReadColonistsAsync(Presentation.ColonistRosterRequest request, CancellationToken cancellationToken) => Count(() => inner.ReadColonistsAsync(request, cancellationToken));

    public Task<Obs.ClearanceTargetsReply> ReadClearanceTargetsAsync(Obs.ClearanceTargetsRequest request, CancellationToken cancellationToken) => Count(() => inner.ReadClearanceTargetsAsync(request, cancellationToken));

    public Task<Mirror.CombatGeometryReply> ReadCombatGeometryAsync(Mirror.CombatGeometryRequest request, CancellationToken cancellationToken) => Count(() => inner.ReadCombatGeometryAsync(request, cancellationToken));

    public Task<Obs.ConsumptionReply> ReadConsumptionAsync(Obs.ConsumptionRequest request, CancellationToken cancellationToken) => Count(() => inner.ReadConsumptionAsync(request, cancellationToken));
    public Task<Obs.StorageOccupancyReply> ReadStorageOccupancyAsync(Obs.StorageOccupancyRequest request, CancellationToken cancellationToken) => Count(() => inner.ReadStorageOccupancyAsync(request, cancellationToken));

    public Task<Obs.DefenseSiteReply> ReadDefenseSiteAsync(Obs.DefenseSiteRequest request, CancellationToken cancellationToken) => Count(() => inner.ReadDefenseSiteAsync(request, cancellationToken));

    public Task<Obs.LinesOfFireReply> ReadLinesOfFireAsync(Obs.LinesOfFireRequest request, CancellationToken cancellationToken) => Count(() => inner.ReadLinesOfFireAsync(request, cancellationToken));

    public Task<Obs.HusbandryReply> ReadHusbandryAsync(Obs.HusbandryRequest request, CancellationToken cancellationToken) => Count(() => inner.ReadHusbandryAsync(request, cancellationToken));

    public Task<Presentation.NotificationsReply> ReadNotificationsAsync(Presentation.NotificationsRequest request, CancellationToken cancellationToken) => Count(() => inner.ReadNotificationsAsync(request, cancellationToken));

    public Task<Obs.PopulationReply> ReadPopulationAsync(Obs.PopulationRequest request, CancellationToken cancellationToken) => Count(() => inner.ReadPopulationAsync(request, cancellationToken));

    public Task<Obs.ResourceSourcesReply> ListResourceSourcesAsync(Obs.ResourceSourcesRequest request, CancellationToken cancellationToken) => Count(() => inner.ListResourceSourcesAsync(request, cancellationToken));

    public Task<Obs.SpatialAccessReply> ReadSpatialAccessAsync(Obs.SpatialAccessRequest request, CancellationToken cancellationToken) => Count(() => inner.ReadSpatialAccessAsync(request, cancellationToken));

    public Task<Obs.EvaluateStatReply> EvaluateStatAsync(Obs.EvaluateStatRequest request, CancellationToken cancellationToken) => Count(() => inner.EvaluateStatAsync(request, cancellationToken));

    public Task<Obs.TradeAcquisitionReply> ReadTradeAcquisitionAsync(Obs.TradeAcquisitionRequest request, CancellationToken cancellationToken) => Count(() => inner.ReadTradeAcquisitionAsync(request, cancellationToken));

    public Task<Obs.TradersReply> ListTradersAsync(Obs.TradersRequest request, CancellationToken cancellationToken) => Count(() => inner.ListTradersAsync(request, cancellationToken));

    public Task<Obs.TradeSessionReply> ReadTradeSessionAsync(Obs.TradeSessionRequest request, CancellationToken cancellationToken) => Count(() => inner.ReadTradeSessionAsync(request, cancellationToken));

    public Task<Obs.TradeSheetReply> ReadTradeSheetAsync(Obs.TradeSheetRequest request, CancellationToken cancellationToken) => Count(() => inner.ReadTradeSheetAsync(request, cancellationToken));

    public Task<Obs.WallUpgradeSitesReply> ListWallUpgradeSitesAsync(Obs.WallUpgradeSitesRequest request, CancellationToken cancellationToken) => Count(() => inner.ListWallUpgradeSitesAsync(request, cancellationToken));

    public Task<Obs.WorldReply> ReadWorldAsync(Obs.WorldRequest request, CancellationToken cancellationToken) => Count(() => inner.ReadWorldAsync(request, cancellationToken));

    public Task<Obs.WorldProgressionReply> ReadWorldProgressionAsync(Obs.WorldProgressionRequest request, CancellationToken cancellationToken) => Count(() => inner.ReadWorldProgressionAsync(request, cancellationToken));

    public Task<Obs.ListZonesReply> ListZonesAsync(Obs.ListZonesRequest request, CancellationToken cancellationToken) => Count(() => inner.ListZonesAsync(request, cancellationToken));

    public Task<Obs.ExcavationSiteReply> ReadExcavationSiteAsync(Obs.ExcavationSiteRequest request, CancellationToken cancellationToken) => Count(() => inner.ReadExcavationSiteAsync(request, cancellationToken));

    public Task<Lifecycle.LoadReply> StartLoadAsync(Lifecycle.LoadRequest request, CancellationToken cancellationToken) => Count(() => inner.StartLoadAsync(request, cancellationToken));

    public Task<Lifecycle.LoadReply> PollLoadAsync(Lifecycle.RequestStatus request, CancellationToken cancellationToken) => Count(() => inner.PollLoadAsync(request, cancellationToken));

    public Task<Lifecycle.NewColonyReply> StartNewColonyAsync(Lifecycle.NewColonyRequest request, CancellationToken cancellationToken) => Count(() => inner.StartNewColonyAsync(request, cancellationToken));

    public Task<Lifecycle.NewColonyReply> PollNewColonyAsync(Lifecycle.RequestStatus request, CancellationToken cancellationToken) => Count(() => inner.PollNewColonyAsync(request, cancellationToken));

    public Task<Lifecycle.MenuReply> StartMenuAsync(Lifecycle.MenuRequest request, CancellationToken cancellationToken) => Count(() => inner.StartMenuAsync(request, cancellationToken));

    public Task<Lifecycle.MenuReply> PollMenuAsync(Lifecycle.RequestStatus request, CancellationToken cancellationToken) => Count(() => inner.PollMenuAsync(request, cancellationToken));

    public Task<Lifecycle.SaveReply> SaveGameAsync(Lifecycle.SaveRequest request, CancellationToken cancellationToken) => Count(() => inner.SaveGameAsync(request, cancellationToken));

    public Task<Lifecycle.SaveReply> PollSaveAsync(Lifecycle.RequestStatus request, CancellationToken cancellationToken) => Count(() => inner.PollSaveAsync(request, cancellationToken));

    public Task<Presentation.OverlayReply> ApplyOverlayAsync(Presentation.OverlayRequest request, CancellationToken cancellationToken) => Count(() => inner.ApplyOverlayAsync(request, cancellationToken));

    public Task<Presentation.RenderReply> ReadRenderStateAsync(Presentation.ReadRequest request, CancellationToken cancellationToken) => Count(() => inner.ReadRenderStateAsync(request, cancellationToken));

    public Task<Presentation.RenderReply> DemandRenderingAsync(Presentation.RenderDemand request, CancellationToken cancellationToken) => Count(() => inner.DemandRenderingAsync(request, cancellationToken));

    public Task<Obs.SnapshotStreamReply> OpenFramesAsync(Obs.SnapshotStreamRequest request, CancellationToken cancellationToken) => Count(() => inner.OpenFramesAsync(request, cancellationToken));

    public Task<Obs.FlushSnapshotReply> FlushFramesAsync(Obs.FlushSnapshotRequest request, CancellationToken cancellationToken) => Count(() => inner.FlushFramesAsync(request, cancellationToken));

    private static Task<T> Count<T>(Func<Task<T>> call, [CallerMemberName] string tool = "")
    {
        BridgeFunctions.CountNativeRead(tool);
        return call();
    }
}
