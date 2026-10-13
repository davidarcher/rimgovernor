using System.Threading.Channels;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.NativeFacade;
using Authority = RimGovernor.Contracts.Wire.Authority;
using Clock = RimGovernor.Contracts.Wire.Clock;
using Lifecycle = RimGovernor.Contracts.Wire.Lifecycle;
using Mirror = RimGovernor.Contracts.Wire.Mirror;
using Obs = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Placement = RimGovernor.Contracts.Wire.Placement;
using Presentation = RimGovernor.Contracts.Wire.Presentation;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

namespace RimGovernor.Brain.Host;

/// <summary>
/// Counts every native call into the read tally a scheduler step or a dispatch attached
/// (<see cref="BridgeFunctions.CountNativeRead"/>; Go <c>readTallyFrom(ctx).add</c> in <c>Client.call</c>), under the
/// native tool name Go's <c>rimgovernor/*</c> methods carry, errors and cancellations included. It wraps the facade
/// the client calls, so every core method is counted once per round trip with no per-method bookkeeping.
/// </summary>
internal sealed class CountingNativeFacade(INativeFacade inner) : INativeFacade
{
    public IStatSource Stats => inner.Stats;

    public ChannelReader<NativeFrame> Frames => inner.Frames;

    public Task<Lifecycle.IdentityReply> ReadIdentityAsync(Lifecycle.IdentityRequest request, CancellationToken cancellationToken) => Count("lifecycle_read_identity", () => inner.ReadIdentityAsync(request, cancellationToken));

    public Task<Lifecycle.TickReply> ReadTickAsync(Lifecycle.TickRequest request, CancellationToken cancellationToken) => Count("lifecycle_read_tick", () => inner.ReadTickAsync(request, cancellationToken));

    public Task<Lifecycle.GovernorStateReply> ReadGovernorStateAsync(Lifecycle.GovernorStateRequest request, CancellationToken cancellationToken) => Count("lifecycle_read_governor_state", () => inner.ReadGovernorStateAsync(request, cancellationToken));

    public Task<Lifecycle.GovernorStateReply> PutGovernorStateBatchAsync(Lifecycle.PutGovernorStateBatchRequest request, CancellationToken cancellationToken) => Count("lifecycle_put_governor_state_batch", () => inner.PutGovernorStateBatchAsync(request, cancellationToken));

    public Task<Lifecycle.WaitSaveSignalReply> WaitSaveSignalAsync(Lifecycle.WaitSaveSignalRequest request, CancellationToken cancellationToken) => Count("lifecycle_wait_save_signal", () => inner.WaitSaveSignalAsync(request, cancellationToken));

    public Task<Lifecycle.FlushDoneReply> FlushDoneAsync(Lifecycle.FlushDoneRequest request, CancellationToken cancellationToken) => Count("lifecycle_flush_done", () => inner.FlushDoneAsync(request, cancellationToken));

    public Task<Authority.StatusReply> ReadAuthorityStatusAsync(Authority.StatusRequest request, CancellationToken cancellationToken) => Count("authority_read_status", () => inner.ReadAuthorityStatusAsync(request, cancellationToken));

    public Task<Authority.ControlReply> ControlAuthorityAsync(Authority.ControlRequest request, CancellationToken cancellationToken) => Count("authority_control", () => inner.ControlAuthorityAsync(request, cancellationToken));

    public Task<Placement.PlacementReply> PreviewPlacementAsync(Placement.PlacementRequest request, CancellationToken cancellationToken) => Count("placement_preview", () => inner.PreviewPlacementAsync(request, cancellationToken));

    public Task<Operations.ZonePreviewReply> PreviewZoneAsync(Operations.ZonePreviewRequest request, CancellationToken cancellationToken) => Count("zones_preview", () => inner.PreviewZoneAsync(request, cancellationToken));

    public Task<Obs.StatusReply> ReadStatusAsync(Obs.StatusRequest request, CancellationToken cancellationToken) => Count("observations_read_status", () => inner.ReadStatusAsync(request, cancellationToken));

    public Task<Obs.ColonyFactsReply> ReadColonyFactsAsync(Obs.ColonyFactsRequest request, CancellationToken cancellationToken) => Count("observations_read_colony_facts", () => inner.ReadColonyFactsAsync(request, cancellationToken));

    public Task<Obs.GetCellsReply> GetCellsAsync(Obs.GetCellsRequest request, CancellationToken cancellationToken) => Count("observations_get_cells", () => inner.GetCellsAsync(request, cancellationToken));

    public Task<Obs.ListBuildingsReply> ListBuildingsAsync(Obs.ListBuildingsRequest request, CancellationToken cancellationToken) => Count("observations_list_buildings", () => inner.ListBuildingsAsync(request, cancellationToken));

    public Task<Obs.ListPawnsReply> ListPawnsAsync(Obs.ListPawnsRequest request, CancellationToken cancellationToken) => Count("observations_list_pawns", () => inner.ListPawnsAsync(request, cancellationToken));

    public Task<Obs.ListSuppliesReply> ListSuppliesAsync(Obs.ListSuppliesRequest request, CancellationToken cancellationToken) => Count("observations_list_supplies", () => inner.ListSuppliesAsync(request, cancellationToken));

    public Task<Obs.ListRoomsReply> ListRoomsAsync(Obs.ListRoomsRequest request, CancellationToken cancellationToken) => Count("observations_list_rooms", () => inner.ListRoomsAsync(request, cancellationToken));

    public Task<Obs.ResearchReply> ReadResearchAsync(Obs.ResearchRequest request, CancellationToken cancellationToken) => Count("observations_read_research", () => inner.ReadResearchAsync(request, cancellationToken));

    public Task<Obs.DefinitionCatalogReply> ReadDefinitionCatalogAsync(Obs.DefinitionCatalogRequest request, CancellationToken cancellationToken) => Count("observations_read_definition_catalog", () => inner.ReadDefinitionCatalogAsync(request, cancellationToken));

    public Task<Obs.BillsReply> ReadBillsAsync(Obs.BillsRequest request, CancellationToken cancellationToken) => Count("observations_read_bills", () => inner.ReadBillsAsync(request, cancellationToken));

    public Task<Obs.RecipesReply> ReadRecipesAsync(Obs.RecipesRequest request, CancellationToken cancellationToken) => Count("observations_read_recipes", () => inner.ReadRecipesAsync(request, cancellationToken));

    public Task<Operations.ApplyReply> ApplyAsync(Operations.ApplyRequest request, CancellationToken cancellationToken) => Count("operations_apply", () => inner.ApplyAsync(request, cancellationToken));

    public Task<Operations.RulesAttachReply> AttachRulesAsync(Operations.RulesAttachRequest request, CancellationToken cancellationToken) => Count("rules_attach", () => inner.AttachRulesAsync(request, cancellationToken));

    public Task<Operations.RulesClearReply> ClearRulesAsync(Operations.RulesClearRequest request, CancellationToken cancellationToken) => Count("rules_clear", () => inner.ClearRulesAsync(request, cancellationToken));

    public Task<Operations.RulesStatusReply> ReadRulesStatusAsync(Operations.RulesStatusRequest request, CancellationToken cancellationToken) => Count("rules_read_status", () => inner.ReadRulesStatusAsync(request, cancellationToken));

    public Task<Receipts.LookupReply> LookupReceiptAsync(Receipts.LookupRequest request, CancellationToken cancellationToken) => Count("receipts_lookup", () => inner.LookupReceiptAsync(request, cancellationToken));

    public Task<Clock.ControlReply> StartClockAsync(Clock.StartRequest request, CancellationToken cancellationToken) => Count("clock_start", () => inner.StartClockAsync(request, cancellationToken));

    public Task<Clock.ControlReply> RenewClockAsync(Clock.RenewRequest request, CancellationToken cancellationToken) => Count("clock_renew", () => inner.RenewClockAsync(request, cancellationToken));

    public Task<Clock.ControlReply> ChangeClockSpeedAsync(Clock.SpeedRequest request, CancellationToken cancellationToken) => Count("clock_change_speed", () => inner.ChangeClockSpeedAsync(request, cancellationToken));

    public Task<Clock.StatusReply> PauseClockAsync(Clock.OwnedRequest request, CancellationToken cancellationToken) => Count("clock_pause", () => inner.PauseClockAsync(request, cancellationToken));

    public Task<Clock.StatusReply> ReadClockStatusAsync(Clock.StatusRequest request, CancellationToken cancellationToken) => Count("clock_read_status", () => inner.ReadClockStatusAsync(request, cancellationToken));

    public Task<Clock.EventsReply> ReadClockEventsAsync(Clock.EventsRequest request, CancellationToken cancellationToken) => Count("clock_read_events", () => inner.ReadClockEventsAsync(request, cancellationToken));

    public Task<Clock.AttemptReply> ReadClockAttemptAsync(Clock.AttemptRequest request, CancellationToken cancellationToken) => Count("clock_read_attempt", () => inner.ReadClockAttemptAsync(request, cancellationToken));

    public Task<Presentation.CameraReply> ReadCameraAsync(Presentation.ReadRequest request, CancellationToken cancellationToken) => Count("presentation_camera", () => inner.ReadCameraAsync(request, cancellationToken));

    public Task<Presentation.SelectionReply> ReadSelectionAsync(Presentation.ReadRequest request, CancellationToken cancellationToken) => Count("presentation_selection", () => inner.ReadSelectionAsync(request, cancellationToken));

    public Task<Presentation.ColonistRosterReply> ReadColonistsAsync(Presentation.ColonistRosterRequest request, CancellationToken cancellationToken) => Count("presentation_colonists", () => inner.ReadColonistsAsync(request, cancellationToken));

    public Task<Obs.ClearanceTargetsReply> ReadClearanceTargetsAsync(Obs.ClearanceTargetsRequest request, CancellationToken cancellationToken) => Count("observations_get_clearance_targets", () => inner.ReadClearanceTargetsAsync(request, cancellationToken));

    public Task<Mirror.CombatGeometryReply> ReadCombatGeometryAsync(Mirror.CombatGeometryRequest request, CancellationToken cancellationToken) => Count("combat_geometry", () => inner.ReadCombatGeometryAsync(request, cancellationToken));

    public Task<Obs.ConsumptionReply> ReadConsumptionAsync(Obs.ConsumptionRequest request, CancellationToken cancellationToken) => Count("observations_read_consumption", () => inner.ReadConsumptionAsync(request, cancellationToken));

    public Task<Obs.DefenseSiteReply> ReadDefenseSiteAsync(Obs.DefenseSiteRequest request, CancellationToken cancellationToken) => Count("observations_read_defense_site", () => inner.ReadDefenseSiteAsync(request, cancellationToken));

    public Task<Obs.LinesOfFireReply> ReadLinesOfFireAsync(Obs.LinesOfFireRequest request, CancellationToken cancellationToken) => Count("observations_read_lines_of_fire", () => inner.ReadLinesOfFireAsync(request, cancellationToken));

    public Task<Obs.HusbandryReply> ReadHusbandryAsync(Obs.HusbandryRequest request, CancellationToken cancellationToken) => Count("observations_read_husbandry", () => inner.ReadHusbandryAsync(request, cancellationToken));

    public Task<Presentation.NotificationsReply> ReadNotificationsAsync(Presentation.NotificationsRequest request, CancellationToken cancellationToken) => Count("presentation_notifications", () => inner.ReadNotificationsAsync(request, cancellationToken));

    public Task<Obs.PopulationReply> ReadPopulationAsync(Obs.PopulationRequest request, CancellationToken cancellationToken) => Count("observations_read_population", () => inner.ReadPopulationAsync(request, cancellationToken));

    public Task<Obs.ResourceSourcesReply> ListResourceSourcesAsync(Obs.ResourceSourcesRequest request, CancellationToken cancellationToken) => Count("observations_list_resource_sources", () => inner.ListResourceSourcesAsync(request, cancellationToken));

    public Task<Obs.AncientShrinesReply> ReadAncientShrinesAsync(Obs.AncientShrinesRequest request, CancellationToken cancellationToken) => Count("observations_get_ancient_shrines", () => inner.ReadAncientShrinesAsync(request, cancellationToken));

    public Task<Obs.SpatialAccessReply> ReadSpatialAccessAsync(Obs.SpatialAccessRequest request, CancellationToken cancellationToken) => Count("observations_read_spatial_access", () => inner.ReadSpatialAccessAsync(request, cancellationToken));

    public Task<Obs.EvaluateStatReply> EvaluateStatAsync(Obs.EvaluateStatRequest request, CancellationToken cancellationToken) => Count("observations_evaluate_stat", () => inner.EvaluateStatAsync(request, cancellationToken));

    public Task<Obs.TradeAcquisitionReply> ReadTradeAcquisitionAsync(Obs.TradeAcquisitionRequest request, CancellationToken cancellationToken) => Count("observations_read_trade_acquisition", () => inner.ReadTradeAcquisitionAsync(request, cancellationToken));

    public Task<Obs.TradersReply> ListTradersAsync(Obs.TradersRequest request, CancellationToken cancellationToken) => Count("observations_list_traders", () => inner.ListTradersAsync(request, cancellationToken));

    public Task<Obs.TradeSessionReply> ReadTradeSessionAsync(Obs.TradeSessionRequest request, CancellationToken cancellationToken) => Count("observations_read_trade_session", () => inner.ReadTradeSessionAsync(request, cancellationToken));

    public Task<Obs.TradeSheetReply> ReadTradeSheetAsync(Obs.TradeSheetRequest request, CancellationToken cancellationToken) => Count("observations_read_trade_sheet", () => inner.ReadTradeSheetAsync(request, cancellationToken));

    public Task<Obs.WallUpgradeSitesReply> ListWallUpgradeSitesAsync(Obs.WallUpgradeSitesRequest request, CancellationToken cancellationToken) => Count("observations_list_wall_upgrade_sites", () => inner.ListWallUpgradeSitesAsync(request, cancellationToken));

    public Task<Obs.WorldReply> ReadWorldAsync(Obs.WorldRequest request, CancellationToken cancellationToken) => Count("observations_read_world", () => inner.ReadWorldAsync(request, cancellationToken));

    public Task<Obs.WorldProgressionReply> ReadWorldProgressionAsync(Obs.WorldProgressionRequest request, CancellationToken cancellationToken) => Count("observations_read_world_progression", () => inner.ReadWorldProgressionAsync(request, cancellationToken));

    public Task<Obs.ListZonesReply> ListZonesAsync(Obs.ListZonesRequest request, CancellationToken cancellationToken) => Count("observations_list_zones", () => inner.ListZonesAsync(request, cancellationToken));

    public Task<Obs.ExcavationSiteReply> ReadExcavationSiteAsync(Obs.ExcavationSiteRequest request, CancellationToken cancellationToken) => Count("observations_read_excavation_site", () => inner.ReadExcavationSiteAsync(request, cancellationToken));

    public Task<Lifecycle.LoadReply> StartLoadAsync(Lifecycle.LoadRequest request, CancellationToken cancellationToken) => Count("lifecycle_load", () => inner.StartLoadAsync(request, cancellationToken));

    public Task<Lifecycle.LoadReply> PollLoadAsync(Lifecycle.RequestStatus request, CancellationToken cancellationToken) => Count("lifecycle_read_load", () => inner.PollLoadAsync(request, cancellationToken));

    public Task<Lifecycle.NewColonyReply> StartNewColonyAsync(Lifecycle.NewColonyRequest request, CancellationToken cancellationToken) => Count("lifecycle_new_colony", () => inner.StartNewColonyAsync(request, cancellationToken));

    public Task<Lifecycle.NewColonyReply> PollNewColonyAsync(Lifecycle.RequestStatus request, CancellationToken cancellationToken) => Count("lifecycle_read_new_colony", () => inner.PollNewColonyAsync(request, cancellationToken));

    public Task<Lifecycle.SaveReply> SaveGameAsync(Lifecycle.SaveRequest request, CancellationToken cancellationToken) => Count("lifecycle_save", () => inner.SaveGameAsync(request, cancellationToken));

    public Task<Lifecycle.SaveReply> PollSaveAsync(Lifecycle.RequestStatus request, CancellationToken cancellationToken) => Count("lifecycle_read_save", () => inner.PollSaveAsync(request, cancellationToken));

    public Task<Presentation.OverlayReply> ApplyOverlayAsync(Presentation.OverlayRequest request, CancellationToken cancellationToken) => Count("presentation_overlay", () => inner.ApplyOverlayAsync(request, cancellationToken));

    public Task<Presentation.RenderReply> ReadRenderStateAsync(Presentation.ReadRequest request, CancellationToken cancellationToken) => Count("presentation_render_state", () => inner.ReadRenderStateAsync(request, cancellationToken));

    public Task<Presentation.RenderReply> DemandRenderingAsync(Presentation.RenderDemand request, CancellationToken cancellationToken) => Count("presentation_render_demand", () => inner.DemandRenderingAsync(request, cancellationToken));

    public Task<Obs.SnapshotStreamReply> OpenFramesAsync(Obs.SnapshotStreamRequest request, CancellationToken cancellationToken) => Count("observations_open_snapshot_stream", () => inner.OpenFramesAsync(request, cancellationToken));

    public Task<Obs.FlushSnapshotReply> FlushFramesAsync(Obs.FlushSnapshotRequest request, CancellationToken cancellationToken) => Count("observations_flush_snapshot", () => inner.FlushFramesAsync(request, cancellationToken));

    private static Task<T> Count<T>(string tool, Func<Task<T>> call)
    {
        BridgeFunctions.CountNativeRead("rimgovernor/" + tool);
        return call();
    }
}
