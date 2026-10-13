#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
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

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// The native side of <see cref="INativeFacade"/>: each capability the tools expose is called here as a typed
    /// call into the same core the tool runs, on the game thread through <see cref="ProtoBoundary.OnGameThread{T}"/>
    /// (the tool's admission class, per-frame allowance, watchdog and observation account), with nothing parsed
    /// from or encoded to text. The few capabilities that never touch the game thread (the save handshake, and the
    /// governor-state write on a game that already carries its component) run on the caller's thread, as their tools
    /// do on the bridge worker.
    /// </summary>
    internal sealed class InProcessNativeFacade : INativeFacade
    {
        internal static readonly InProcessNativeFacade Instance = new InProcessNativeFacade();

        private const string Control = MainThreadAdmission.Control, Observation = MainThreadAdmission.Observation;

        private InProcessNativeFacade() { }

        public RimGovernor.Brain.Domain.IStatSource Stats => GameThreadStatService.Instance;

        public ChannelReader<NativeFrame> Frames => SnapshotStream.Handoff.Reader;

        // A hop for a read (observation class) or a write (control class, which runs ahead of queued reads).
        private static Task<T> Read<T>(string operation, object request, Func<T> core, CancellationToken cancellationToken) where T : class
        {
            ArgumentNullException.ThrowIfNull(request);
            return ProtoBoundary.OnGameThread("facade/" + operation, core, cancellationToken, Observation);
        }

        private static Task<T> Write<T>(string operation, object request, Func<T> core, CancellationToken cancellationToken) where T : class
        {
            ArgumentNullException.ThrowIfNull(request);
            return ProtoBoundary.OnGameThread("facade/" + operation, core, cancellationToken, Control);
        }

        // Lifecycle.

        public Task<Lifecycle.IdentityReply> ReadIdentityAsync(Lifecycle.IdentityRequest request, CancellationToken cancellationToken)
            => Read("lifecycle_read_identity", request, () => ProtoIdentityTools.HandleReadIdentity(request), cancellationToken);

        public Task<Lifecycle.TickReply> ReadTickAsync(Lifecycle.TickRequest request, CancellationToken cancellationToken)
            => Read("lifecycle_read_tick", request, () => ProtoIdentityTools.HandleReadTick(request), cancellationToken);

        public Task<Lifecycle.GovernorStateReply> ReadGovernorStateAsync(Lifecycle.GovernorStateRequest request, CancellationToken cancellationToken)
            => Read("lifecycle_read_governor_state", request, ProtoGovernorStateTools.HandleRead, cancellationToken);

        public Task<Lifecycle.GovernorStateReply> PutGovernorStateBatchAsync(Lifecycle.PutGovernorStateBatchRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            var reply = ProtoGovernorStateTools.TryPut(request, out var set);
            if (reply != null) return Task.FromResult(reply);
            // A save that predates the component needs the game thread to attach it.
            return Write("lifecycle_put_governor_state_batch", request, () => ProtoGovernorStateTools.PutOnGameThread(set), cancellationToken);
        }

        public Task<Lifecycle.WaitSaveSignalReply> WaitSaveSignalAsync(Lifecycle.WaitSaveSignalRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            return ProtoLifecycleSaveSignalTools.WaitCore(request, cancellationToken);
        }

        public Task<Lifecycle.FlushDoneReply> FlushDoneAsync(Lifecycle.FlushDoneRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ProtoLifecycleSaveSignalTools.FlushDoneCore(request));
        }

        // Authority.

        public Task<Authority.StatusReply> ReadAuthorityStatusAsync(Authority.StatusRequest request, CancellationToken cancellationToken)
            => Read("authority_status", request, () => NativeAuthorityTools.HandleReadStatus(request), cancellationToken);

        public Task<Authority.ControlReply> ControlAuthorityAsync(Authority.ControlRequest request, CancellationToken cancellationToken)
            => Write("authority_control", request, () => NativeAuthorityControlTools.Apply(request), cancellationToken);

        // Placement and zones.

        public Task<Placement.PlacementReply> PreviewPlacementAsync(Placement.PlacementRequest request, CancellationToken cancellationToken)
            => Read("placement_preview", request, () => PlacementPreviewsTools.HandlePreview(request, cancellationToken), cancellationToken);

        public Task<Operations.ZonePreviewReply> PreviewZoneAsync(Operations.ZonePreviewRequest request, CancellationToken cancellationToken)
            => Read("zone_preview", request, () => NativeOperationTools.HandlePreviewZone(request), cancellationToken);

        // Observations.

        public Task<Obs.StatusReply> ReadStatusAsync(Obs.StatusRequest request, CancellationToken cancellationToken)
            => Read("observations_read_status", request, () => NativeObservationTools.HandleReadStatus(request), cancellationToken);

        public Task<Obs.ColonyFactsReply> ReadColonyFactsAsync(Obs.ColonyFactsRequest request, CancellationToken cancellationToken)
            => Read("observations_read_colony_facts", request, () => NativeColonyObservationTools.HandleReadColonyFacts(request), cancellationToken);

        public Task<Obs.GetCellsReply> GetCellsAsync(Obs.GetCellsRequest request, CancellationToken cancellationToken)
            => Read("observations_get_cells", request, () => NativeObservationTools.HandleGetCells(request), cancellationToken);

        public Task<Obs.ListBuildingsReply> ListBuildingsAsync(Obs.ListBuildingsRequest request, CancellationToken cancellationToken)
            => Read("observations_list_buildings", request, () => NativeBuildingObservationTools.HandleListBuildings(request), cancellationToken);

        public Task<Obs.ListPawnsReply> ListPawnsAsync(Obs.ListPawnsRequest request, CancellationToken cancellationToken)
            => Read("observations_list_pawns", request, () => NativePawnObservationTools.HandleListPawns(request), cancellationToken);

        public Task<Obs.ListSuppliesReply> ListSuppliesAsync(Obs.ListSuppliesRequest request, CancellationToken cancellationToken)
            => Read("observations_list_supplies", request, () => NativeSuppliesObservationTools.HandleListSupplies(request), cancellationToken);

        public Task<Obs.ListRoomsReply> ListRoomsAsync(Obs.ListRoomsRequest request, CancellationToken cancellationToken)
            => Read("observations_list_rooms", request, () => NativeRoomObservationTools.HandleListRooms(request), cancellationToken);

        public Task<Obs.ResearchReply> ReadResearchAsync(Obs.ResearchRequest request, CancellationToken cancellationToken)
            => Read("observations_read_research", request, () => NativeResearchObservationTools.HandleReadResearch(request), cancellationToken);

        public Task<Obs.DefinitionCatalogReply> ReadDefinitionCatalogAsync(Obs.DefinitionCatalogRequest request, CancellationToken cancellationToken)
            => Read("observations_read_definition_catalog", request, () => NativeDefinitionCatalogTool.HandleReadDefinitionCatalog(request), cancellationToken);

        public Task<Obs.BillsReply> ReadBillsAsync(Obs.BillsRequest request, CancellationToken cancellationToken)
            => Read("observations_read_bills", request, () => NativeBillsObservationTools.HandleReadBills(request), cancellationToken);

        public Task<Obs.RecipesReply> ReadRecipesAsync(Obs.RecipesRequest request, CancellationToken cancellationToken)
            => Read("observations_read_recipes", request, () => NativeBillsObservationTools.HandleReadRecipes(request), cancellationToken);

        // Operations.

        public Task<Operations.ApplyReply> ApplyAsync(Operations.ApplyRequest request, CancellationToken cancellationToken)
            => Write("operations_apply", request, () => NativeActionTools.HandleApply(request), cancellationToken);

        public Task<Operations.RulesAttachReply> AttachRulesAsync(Operations.RulesAttachRequest request, CancellationToken cancellationToken)
            => Write("rules_attach", request, () => NativeRuleTools.HandleAttach(request), cancellationToken);

        public Task<Operations.RulesClearReply> ClearRulesAsync(Operations.RulesClearRequest request, CancellationToken cancellationToken)
            => Write("rules_clear", request, () => NativeRuleTools.HandleClear(request), cancellationToken);

        public Task<Operations.RulesStatusReply> ReadRulesStatusAsync(Operations.RulesStatusRequest request, CancellationToken cancellationToken)
            => Read("rules_status", request, () => NativeRuleTools.HandleReadStatus(request), cancellationToken);

        public Task<Receipts.LookupReply> LookupReceiptAsync(Receipts.LookupRequest request, CancellationToken cancellationToken)
            => Read("receipts_lookup", request, () => NativeOperationTools.HandleLookup(request), cancellationToken);

        // Clock.

        public Task<Clock.ControlReply> StartClockAsync(Clock.StartRequest request, CancellationToken cancellationToken)
            => Write("clock_start", request, () => NativeClockTools.HandleStart(request), cancellationToken);

        public Task<Clock.ControlReply> RenewClockAsync(Clock.RenewRequest request, CancellationToken cancellationToken)
            => Write("clock_renew", request, () => NativeClockTools.HandleRenew(request), cancellationToken);

        public Task<Clock.ControlReply> ChangeClockSpeedAsync(Clock.SpeedRequest request, CancellationToken cancellationToken)
            => Write("clock_change_speed", request, () => NativeClockTools.HandleChangeSpeed(request), cancellationToken);

        public Task<Clock.StatusReply> PauseClockAsync(Clock.OwnedRequest request, CancellationToken cancellationToken)
            => Write("clock_pause", request, () => NativeClockTools.HandlePause(request), cancellationToken);

        public Task<Clock.StatusReply> ReadClockStatusAsync(Clock.StatusRequest request, CancellationToken cancellationToken)
            => Read("clock_status", request, () => NativeClockTools.HandleReadStatus(request), cancellationToken);

        public Task<Clock.EventsReply> ReadClockEventsAsync(Clock.EventsRequest request, CancellationToken cancellationToken)
            => Read("clock_events", request, () => NativeClockTools.HandleReadEvents(request), cancellationToken);

        public Task<Clock.AttemptReply> ReadClockAttemptAsync(Clock.AttemptRequest request, CancellationToken cancellationToken)
            => Read("clock_attempt", request, () => NativeClockTools.HandleReadAttempt(request), cancellationToken);

        // Presentation reads.

        public Task<Presentation.CameraReply> ReadCameraAsync(Presentation.ReadRequest request, CancellationToken cancellationToken)
            => Read("presentation_camera", request, () => NativePresentationReadTools.HandleCamera(request), cancellationToken);

        public Task<Presentation.SelectionReply> ReadSelectionAsync(Presentation.ReadRequest request, CancellationToken cancellationToken)
            => Read("presentation_selection", request, () => NativePresentationReadTools.HandleSelection(request), cancellationToken);

        public Task<Presentation.ColonistRosterReply> ReadColonistsAsync(Presentation.ColonistRosterRequest request, CancellationToken cancellationToken)
            => Read("presentation_colonists", request, () => NativePresentationReadTools.HandleColonists(request), cancellationToken);

        // The remaining native tools (#3100).

        public Task<Obs.ClearanceTargetsReply> ReadClearanceTargetsAsync(Obs.ClearanceTargetsRequest request, CancellationToken cancellationToken)
            => Read("observations_get_clearance_targets", request, () => NativeClearanceObservationTools.HandleRead(request), cancellationToken);

        public Task<Mirror.CombatGeometryReply> ReadCombatGeometryAsync(Mirror.CombatGeometryRequest request, CancellationToken cancellationToken)
            => Read("combat_geometry", request, () => NativeCombatGeometryTools.HandleRead(request), cancellationToken);

        public Task<Obs.ConsumptionReply> ReadConsumptionAsync(Obs.ConsumptionRequest request, CancellationToken cancellationToken)
            => Read("observations_read_consumption", request, () => NativeConsumptionTool.HandleReadConsumption(request), cancellationToken);

        public Task<Obs.DefenseSiteReply> ReadDefenseSiteAsync(Obs.DefenseSiteRequest request, CancellationToken cancellationToken)
            => Read("observations_read_defense_site", request, () => NativeDefenseObservationTools.HandleReadDefenseSite(request), cancellationToken);

        public Task<Obs.LinesOfFireReply> ReadLinesOfFireAsync(Obs.LinesOfFireRequest request, CancellationToken cancellationToken)
            => Read("observations_read_lines_of_fire", request, () => NativeDefenseObservationTools.HandleReadLinesOfFire(request), cancellationToken);

        public Task<Obs.HusbandryReply> ReadHusbandryAsync(Obs.HusbandryRequest request, CancellationToken cancellationToken)
            => Read("observations_read_husbandry", request, () => NativeHusbandryObservation.HandleReadHusbandry(request), cancellationToken);

        public Task<Presentation.NotificationsReply> ReadNotificationsAsync(Presentation.NotificationsRequest request, CancellationToken cancellationToken)
            => Read("presentation_notifications", request, () => NativeNotificationReadTools.HandleNotifications(request), cancellationToken);

        public Task<Obs.PopulationReply> ReadPopulationAsync(Obs.PopulationRequest request, CancellationToken cancellationToken)
            => Read("observations_read_population", request, () => NativePopulationObservation.HandleReadPopulation(request), cancellationToken);

        public Task<Obs.ResourceSourcesReply> ListResourceSourcesAsync(Obs.ResourceSourcesRequest request, CancellationToken cancellationToken)
            => Read("observations_list_resource_sources", request, () => NativeResourceSourcesTool.HandleListResourceSources(request), cancellationToken);

        public Task<Obs.AncientShrinesReply> ReadAncientShrinesAsync(Obs.AncientShrinesRequest request, CancellationToken cancellationToken)
            => Read("observations_get_ancient_shrines", request, () => NativeShrineObservationTools.HandleAncientShrines(request), cancellationToken);

        public Task<Obs.SpatialAccessReply> ReadSpatialAccessAsync(Obs.SpatialAccessRequest request, CancellationToken cancellationToken)
            => Read("observations_read_spatial_access", request, () => NativeSpatialAccessTool.HandleReadSpatialAccess(request), cancellationToken);

        public Task<Obs.EvaluateStatReply> EvaluateStatAsync(Obs.EvaluateStatRequest request, CancellationToken cancellationToken)
            => Read("observations_evaluate_stat", request, () => NativeStatTool.HandleEvaluateStat(request), cancellationToken);

        public Task<Obs.TradeAcquisitionReply> ReadTradeAcquisitionAsync(Obs.TradeAcquisitionRequest request, CancellationToken cancellationToken)
            => Read("observations_read_trade_acquisition", request, () => NativeTradeAcquisitionTools.HandleReadTradeAcquisition(request), cancellationToken);

        public Task<Obs.TradersReply> ListTradersAsync(Obs.TradersRequest request, CancellationToken cancellationToken)
            => Read("observations_list_traders", request, () => NativeTradeObservationTools.HandleListTraders(request), cancellationToken);

        public Task<Obs.TradeSessionReply> ReadTradeSessionAsync(Obs.TradeSessionRequest request, CancellationToken cancellationToken)
            => Read("observations_read_trade_session", request, () => NativeTradeObservationTools.HandleReadTradeSession(request), cancellationToken);

        public Task<Obs.TradeSheetReply> ReadTradeSheetAsync(Obs.TradeSheetRequest request, CancellationToken cancellationToken)
            => Read("observations_read_trade_sheet", request, () => NativeTradeObservationTools.HandleReadTradeSheet(request), cancellationToken);

        public Task<Obs.WallUpgradeSitesReply> ListWallUpgradeSitesAsync(Obs.WallUpgradeSitesRequest request, CancellationToken cancellationToken)
            => Read("observations_list_wall_upgrade_sites", request, () => NativeWallUpgradeObservationTools.HandleListWallUpgradeSites(request), cancellationToken);

        public Task<Obs.WorldReply> ReadWorldAsync(Obs.WorldRequest request, CancellationToken cancellationToken)
            => Read("observations_read_world", request, () => NativeWorldObservationTools.HandleReadWorld(request), cancellationToken);

        public Task<Obs.WorldProgressionReply> ReadWorldProgressionAsync(Obs.WorldProgressionRequest request, CancellationToken cancellationToken)
            => Read("observations_read_world_progression", request, () => NativeWorldProgressionObservationTools.HandleReadWorldProgression(request), cancellationToken);

        public Task<Obs.ListZonesReply> ListZonesAsync(Obs.ListZonesRequest request, CancellationToken cancellationToken)
            => Read("observations_list_zones", request, () => NativeZoneObservationTools.HandleListZones(request), cancellationToken);

        public Task<Obs.ExcavationSiteReply> ReadExcavationSiteAsync(Obs.ExcavationSiteRequest request, CancellationToken cancellationToken)
            => Read("observations_read_excavation_site", request, () => NativeObservationTools.HandleReadExcavationSite(request), cancellationToken);

        public Task<Lifecycle.LoadReply> StartLoadAsync(Lifecycle.LoadRequest request, CancellationToken cancellationToken)
            => Write("lifecycle_load", request, () => ProtoLifecycleLoadTools.StartLoad(request), cancellationToken);

        public Task<Lifecycle.LoadReply> PollLoadAsync(Lifecycle.RequestStatus request, CancellationToken cancellationToken)
            => Write("lifecycle_read_load", request, () => ProtoLifecycleLoadTools.PollLoad(request), cancellationToken);

        public Task<Lifecycle.NewColonyReply> StartNewColonyAsync(Lifecycle.NewColonyRequest request, CancellationToken cancellationToken)
            => Write("lifecycle_new_colony", request, () => ProtoLifecycleNewColonyTools.StartNewColony(request), cancellationToken);

        public Task<Lifecycle.NewColonyReply> PollNewColonyAsync(Lifecycle.RequestStatus request, CancellationToken cancellationToken)
            => Write("lifecycle_read_new_colony", request, () => ProtoLifecycleNewColonyTools.PollNewColony(request), cancellationToken);

        public Task<Lifecycle.SaveReply> SaveGameAsync(Lifecycle.SaveRequest request, CancellationToken cancellationToken)
            => Write("lifecycle_save", request, () => ProtoLifecycleSaveTools.Apply(request), cancellationToken);

        public Task<Lifecycle.SaveReply> PollSaveAsync(Lifecycle.RequestStatus request, CancellationToken cancellationToken)
            => Read("lifecycle_read_save", request, () => ProtoLifecycleSaveTools.PollSave(request), cancellationToken);

        public Task<Presentation.OverlayReply> ApplyOverlayAsync(Presentation.OverlayRequest request, CancellationToken cancellationToken)
            => Write("presentation_overlay", request, () => ProtoOverlayTools.Apply(request), cancellationToken);

        public Task<Presentation.RenderReply> ReadRenderStateAsync(Presentation.ReadRequest request, CancellationToken cancellationToken)
            => Read("presentation_render_state", request, () => ProtoPresentationMediaTools.HandleRenderState(request), cancellationToken);

        public Task<Presentation.RenderReply> DemandRenderingAsync(Presentation.RenderDemand request, CancellationToken cancellationToken)
            => Write("presentation_render_demand", request, () => ProtoPresentationMediaTools.HandleDemandRendering(request), cancellationToken);

        // Frame handoff.

        public Task<Obs.SnapshotStreamReply> OpenFramesAsync(Obs.SnapshotStreamRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            foreach (var resource in request.ResourceSources)
            {
                if (!ProtoBoundary.IsIdentifier(resource))
                {
                    return Task.FromResult(new Obs.SnapshotStreamReply { Outcome = ProtoBoundary.Fail(RimGovernor.Contracts.Wire.Common.FailureCode.InvalidRequest, "Resource sources must be identifiers.") });
                }
            }
            return Read("observations_open_snapshot_stream", request, () => SnapshotStream.OpenInProcess(request), cancellationToken);
        }

        public Task<Obs.FlushSnapshotReply> FlushFramesAsync(Obs.FlushSnapshotRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new Obs.FlushSnapshotReply { Outcome = new Obs.FlushSnapshotReplyOutcome.FlushedCase(SnapshotStream.Flush()) });
        }
    }

    /// <summary>Publishes the facade to the brain in the same process when the host loads this assembly.</summary>
    internal static class NativeFacadeBootstrap
    {
        // CA2255 warns against module initializers in libraries; this assembly is a plugin the host loads by file
        // with no startup hook of its own (see ObservationFrameHook), and the facade must exist before the first
        // tool call. The initializer does no game work: it hands out an object whose calls hop later.
#pragma warning disable CA2255
        [ModuleInitializer]
        internal static void Publish()
        {
            if (NativeFacadeRegistry.Current == null) NativeFacadeRegistry.Publish(InProcessNativeFacade.Instance);
        }
#pragma warning restore CA2255
    }
}
