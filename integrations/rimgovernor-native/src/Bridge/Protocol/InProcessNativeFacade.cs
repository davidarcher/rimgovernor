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

        public ChannelReader<NativeFrame> Frames => SnapshotStream.Handoff.Reader;

        public Task<Obs.ActivityTaskRead> ReadActivityTasksAsync(Obs.ActivityTaskRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadActivityTasks, request, () => NativeActivityTasks.Read(request), cancellationToken);
        public Task<Obs.ConstructionActivityRead> ReadConstructionActivityAsync(Obs.ConstructionActivityRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadConstructionActivity, request, () => NativeConstructionActivity.Read(request), cancellationToken);
        public Task<Obs.RecipeActivityRead> ReadRecipeActivityAsync(Obs.RecipeActivityRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadRecipeActivity, request, () => NativeRecipeActivity.Read(request), cancellationToken);

        // A hop for a read (observation class) or a write (control class, which runs ahead of queued reads).
        private static Task<T> Read<TRequest, T>(NativeOp<TRequest, T> op, TRequest request, Func<T> core, CancellationToken cancellationToken) where TRequest : class where T : class
        {
            ArgumentNullException.ThrowIfNull(request);
            return ProtoBoundary.OnGameThread("facade/" + op.Name, core, cancellationToken, Observation);
        }

        private static Task<T> Write<TRequest, T>(NativeOp<TRequest, T> op, TRequest request, Func<T> core, CancellationToken cancellationToken) where TRequest : class where T : class
        {
            ArgumentNullException.ThrowIfNull(request);
            return ProtoBoundary.OnGameThread("facade/" + op.Name, core, cancellationToken, Control);
        }

        // Lifecycle.

        public Task<Lifecycle.IdentityReply> ReadIdentityAsync(Lifecycle.IdentityRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadIdentity, request, () => ProtoIdentityTools.HandleReadIdentity(request), cancellationToken);

        public Task<Lifecycle.TickReply> ReadTickAsync(Lifecycle.TickRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadTick, request, () => ProtoIdentityTools.HandleReadTick(request), cancellationToken);

        public Task<Lifecycle.GovernorStateReply> ReadGovernorStateAsync(Lifecycle.GovernorStateRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadGovernorState, request, ProtoGovernorStateTools.HandleRead, cancellationToken);

        public Task<Lifecycle.GovernorStateReply> PutGovernorStateBatchAsync(Lifecycle.PutGovernorStateBatchRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            var reply = ProtoGovernorStateTools.TryPut(request, out var set);
            if (reply != null) return Task.FromResult(reply);
            // A save that predates the component needs the game thread to attach it.
            return Write(NativeOps.PutGovernorStateBatch, request, () => ProtoGovernorStateTools.PutOnGameThread(set), cancellationToken);
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
            => Read(NativeOps.ReadAuthorityStatus, request, () => NativeAuthorityTools.HandleReadStatus(request), cancellationToken);

        public Task<Authority.ControlReply> ControlAuthorityAsync(Authority.ControlRequest request, CancellationToken cancellationToken)
            => Write(NativeOps.ControlAuthority, request, () => NativeAuthorityControlTools.Apply(request), cancellationToken);

        // Placement and zones.

        public Task<Placement.PlacementReply> PreviewPlacementAsync(Placement.PlacementRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.PreviewPlacement, request, () => PlacementPreviewsTools.HandlePreview(request, cancellationToken), cancellationToken);

        public Task<Operations.ZonePreviewReply> PreviewZoneAsync(Operations.ZonePreviewRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.PreviewZone, request, () => NativeOperationTools.HandlePreviewZone(request), cancellationToken);

        // Observations.

        public Task<Obs.StatusReply> ReadStatusAsync(Obs.StatusRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadStatus, request, () => NativeObservationTools.HandleReadStatus(request), cancellationToken);

        public Task<Obs.ColonyFactsReply> ReadColonyFactsAsync(Obs.ColonyFactsRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadColonyFacts, request, () => NativeColonyObservationTools.HandleReadColonyFacts(request), cancellationToken);

        public Task<Obs.GetCellsReply> GetCellsAsync(Obs.GetCellsRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.GetCells, request, () => NativeObservationTools.HandleGetCells(request), cancellationToken);

        public Task<Obs.ListBuildingsReply> ListBuildingsAsync(Obs.ListBuildingsRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ListBuildings, request, () => NativeBuildingObservationTools.HandleListBuildings(request), cancellationToken);

        public Task<Obs.ListPawnsReply> ListPawnsAsync(Obs.ListPawnsRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ListPawns, request, () => NativePawnObservationTools.HandleListPawns(request), cancellationToken);

        public Task<Obs.ListSuppliesReply> ListSuppliesAsync(Obs.ListSuppliesRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ListSupplies, request, () => NativeSuppliesObservationTools.HandleListSupplies(request), cancellationToken);

        public Task<Obs.ListRoomsReply> ListRoomsAsync(Obs.ListRoomsRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ListRooms, request, () => NativeRoomObservationTools.HandleListRooms(request), cancellationToken);

        public Task<Obs.ResearchReply> ReadResearchAsync(Obs.ResearchRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadResearch, request, () => NativeResearchObservationTools.HandleReadResearch(request), cancellationToken);

        public Task<Obs.DefinitionCatalogReply> ReadDefinitionCatalogAsync(Obs.DefinitionCatalogRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadDefinitionCatalog, request, () => NativeDefinitionCatalogTool.HandleReadDefinitionCatalog(request), cancellationToken);

        public Task<Obs.BillsReply> ReadBillsAsync(Obs.BillsRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadBills, request, () => NativeBillsObservationTools.HandleReadBills(request), cancellationToken);

        public Task<Obs.RecipesReply> ReadRecipesAsync(Obs.RecipesRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadRecipes, request, () => NativeBillsObservationTools.HandleReadRecipes(request), cancellationToken);

        // Operations.

        public Task<Operations.ApplyReply> ApplyAsync(Operations.ApplyRequest request, CancellationToken cancellationToken)
            => Write(NativeOps.Apply, request, () => NativeActionTools.HandleApply(request), cancellationToken);

        public Task<Receipts.LookupReply> LookupReceiptAsync(Receipts.LookupRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.LookupReceipt, request, () => NativeOperationTools.HandleLookup(request), cancellationToken);

        // Clock.

        public Task<Clock.StatusReply> RunClockAsync(Clock.RunRequest request, CancellationToken cancellationToken)
            => Write(NativeOps.RunClock, request, () => NativeClockTools.HandleRun(request), cancellationToken);

        public Task<Clock.StatusReply> HoldClockAsync(Clock.HoldRequest request, CancellationToken cancellationToken)
            => Write(NativeOps.HoldClock, request, () => NativeClockTools.HandleHold(request), cancellationToken);

        public Task<Clock.HeartbeatReply> HeartbeatClockAsync(Clock.HeartbeatRequest request, CancellationToken cancellationToken)
            => Write(NativeOps.HeartbeatClock, request, () => NativeClockTools.HandleHeartbeat(request), cancellationToken);

        public Task<Clock.StatusReply> ReadClockStatusAsync(Clock.StatusRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadClockStatus, request, () => NativeClockTools.HandleReadStatus(request), cancellationToken);

        // The queue is lock-free and native-owned: opening it needs no game-thread hop.
        public Task<ChannelReader<Clock.ClockEvent>> OpenColonyEventsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Supervisor.ColonyEvents.Open());
        }

        public long ColonyEventsLost => Supervisor.ColonyEvents.Lost;

        // Presentation reads.

        public Task<Presentation.CameraReply> ReadCameraAsync(Presentation.ReadRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadCamera, request, () => NativePresentationReadTools.HandleCamera(request), cancellationToken);

        public Task<Presentation.SelectionReply> ReadSelectionAsync(Presentation.ReadRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadSelection, request, () => NativePresentationReadTools.HandleSelection(request), cancellationToken);

        public Task<Presentation.ColonistRosterReply> ReadColonistsAsync(Presentation.ColonistRosterRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadColonists, request, () => NativePresentationReadTools.HandleColonists(request), cancellationToken);

        // The remaining native tools (#3100).

        public Task<Obs.ClearanceTargetsReply> ReadClearanceTargetsAsync(Obs.ClearanceTargetsRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadClearanceTargets, request, () => NativeClearanceObservationTools.HandleRead(request), cancellationToken);

        public Task<Mirror.CombatGeometryReply> ReadCombatGeometryAsync(Mirror.CombatGeometryRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadCombatGeometry, request, () => NativeCombatGeometryTools.HandleRead(request), cancellationToken);

        public Task<Obs.ConsumptionReply> ReadConsumptionAsync(Obs.ConsumptionRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadConsumption, request, () => NativeConsumptionTool.HandleReadConsumption(request), cancellationToken);

        public Task<Obs.StorageOccupancyReply> ReadStorageOccupancyAsync(Obs.StorageOccupancyRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadStorageOccupancy, request, () => NativeStorageOccupancyTool.HandleRead(request), cancellationToken);

        public Task<Obs.DefenseSiteReply> ReadDefenseSiteAsync(Obs.DefenseSiteRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadDefenseSite, request, () => NativeDefenseObservationTools.HandleReadDefenseSite(request), cancellationToken);

        public Task<Obs.LinesOfFireReply> ReadLinesOfFireAsync(Obs.LinesOfFireRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadLinesOfFire, request, () => NativeDefenseObservationTools.HandleReadLinesOfFire(request), cancellationToken);

        public Task<Obs.HusbandryReply> ReadHusbandryAsync(Obs.HusbandryRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadHusbandry, request, () => NativeHusbandryObservation.HandleReadHusbandry(request), cancellationToken);

        public Task<Presentation.NotificationsReply> ReadNotificationsAsync(Presentation.NotificationsRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadNotifications, request, () => NativeNotificationReadTools.HandleNotifications(request), cancellationToken);

        public Task<Obs.PopulationReply> ReadPopulationAsync(Obs.PopulationRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadPopulation, request, () => NativePopulationObservation.HandleReadPopulation(request), cancellationToken);

        public Task<Obs.ResourceSourcesReply> ListResourceSourcesAsync(Obs.ResourceSourcesRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ListResourceSources, request, () => NativeResourceSourcesTool.HandleListResourceSources(request), cancellationToken);

        public Task<Obs.SpatialAccessReply> ReadSpatialAccessAsync(Obs.SpatialAccessRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadSpatialAccess, request, () => NativeSpatialAccessTool.HandleReadSpatialAccess(request), cancellationToken);

        public Task<Obs.EvaluateStatReply> EvaluateStatAsync(Obs.EvaluateStatRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.EvaluateStat, request, () => NativeStatTool.HandleEvaluateStat(request), cancellationToken);

        public Task<Obs.TradeAcquisitionReply> ReadTradeAcquisitionAsync(Obs.TradeAcquisitionRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadTradeAcquisition, request, () => NativeTradeAcquisitionTools.HandleReadTradeAcquisition(request), cancellationToken);

        public Task<Obs.TradersReply> ListTradersAsync(Obs.TradersRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ListTraders, request, () => NativeTradeObservationTools.HandleListTraders(request), cancellationToken);

        public Task<Obs.TradeSessionReply> ReadTradeSessionAsync(Obs.TradeSessionRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadTradeSession, request, () => NativeTradeObservationTools.HandleReadTradeSession(request), cancellationToken);

        public Task<Obs.TradeSheetReply> ReadTradeSheetAsync(Obs.TradeSheetRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadTradeSheet, request, () => NativeTradeObservationTools.HandleReadTradeSheet(request), cancellationToken);

        public Task<Obs.WallUpgradeSitesReply> ListWallUpgradeSitesAsync(Obs.WallUpgradeSitesRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ListWallUpgradeSites, request, () => NativeWallUpgradeObservationTools.HandleListWallUpgradeSites(request), cancellationToken);

        public Task<Obs.WorldReply> ReadWorldAsync(Obs.WorldRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadWorld, request, () => NativeWorldObservationTools.HandleReadWorld(request), cancellationToken);

        public Task<Obs.WorldProgressionReply> ReadWorldProgressionAsync(Obs.WorldProgressionRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadWorldProgression, request, () => NativeWorldProgressionObservationTools.HandleReadWorldProgression(request), cancellationToken);

        public Task<Obs.ListZonesReply> ListZonesAsync(Obs.ListZonesRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ListZones, request, () => NativeZoneObservationTools.HandleListZones(request), cancellationToken);

        public Task<Obs.ExcavationSiteReply> ReadExcavationSiteAsync(Obs.ExcavationSiteRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadExcavationSite, request, () => NativeObservationTools.HandleReadExcavationSite(request), cancellationToken);

        public Task<Lifecycle.LoadReply> StartLoadAsync(Lifecycle.LoadRequest request, CancellationToken cancellationToken)
            => Write(NativeOps.StartLoad, request, () => ProtoLifecycleLoadTools.StartLoad(request), cancellationToken);

        public Task<Lifecycle.LoadReply> PollLoadAsync(Lifecycle.RequestStatus request, CancellationToken cancellationToken)
            => Write(NativeOps.PollLoad, request, () => ProtoLifecycleLoadTools.PollLoad(request), cancellationToken);

        public Task<Lifecycle.NewColonyReply> StartNewColonyAsync(Lifecycle.NewColonyRequest request, CancellationToken cancellationToken)
            => Write(NativeOps.StartNewColony, request, () => ProtoLifecycleNewColonyTools.StartNewColony(request), cancellationToken);

        public Task<Lifecycle.NewColonyReply> PollNewColonyAsync(Lifecycle.RequestStatus request, CancellationToken cancellationToken)
            => Write(NativeOps.PollNewColony, request, () => ProtoLifecycleNewColonyTools.PollNewColony(request), cancellationToken);

        public Task<Lifecycle.MenuReply> StartMenuAsync(Lifecycle.MenuRequest request, CancellationToken cancellationToken)
            => Write(NativeOps.StartMenu, request, () => ProtoLifecycleMenuTools.StartMenu(request), cancellationToken);

        public Task<Lifecycle.MenuReply> PollMenuAsync(Lifecycle.RequestStatus request, CancellationToken cancellationToken)
            => Write(NativeOps.PollMenu, request, () => ProtoLifecycleMenuTools.PollMenu(request), cancellationToken);

        public Task<Lifecycle.SaveReply> SaveGameAsync(Lifecycle.SaveRequest request, CancellationToken cancellationToken)
            => Write(NativeOps.SaveGame, request, () => ProtoLifecycleSaveTools.Apply(request), cancellationToken);

        public Task<Lifecycle.SaveReply> PollSaveAsync(Lifecycle.RequestStatus request, CancellationToken cancellationToken)
            => Read(NativeOps.PollSave, request, () => ProtoLifecycleSaveTools.PollSave(request), cancellationToken);

        public Task<Presentation.OverlayReply> ApplyOverlayAsync(Presentation.OverlayRequest request, CancellationToken cancellationToken)
            => Write(NativeOps.ApplyOverlay, request, () => ProtoOverlayTools.Apply(request), cancellationToken);

        public Task<Presentation.RenderReply> ReadRenderStateAsync(Presentation.ReadRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadRenderState, request, () => ProtoPresentationMediaTools.HandleRenderState(request), cancellationToken);

        public Task<Presentation.RenderReply> DemandRenderingAsync(Presentation.RenderDemand request, CancellationToken cancellationToken)
            => Write(NativeOps.DemandRendering, request, () => ProtoPresentationMediaTools.HandleDemandRendering(request), cancellationToken);

        // Frame handoff.

        public Task<Obs.SnapshotStreamReply> OpenFramesAsync(Obs.SnapshotStreamRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            return Read(NativeOps.OpenFrames, request, () => SnapshotStream.Open(request), cancellationToken);
        }

        public Task<Obs.FlushSnapshotReply> FlushFramesAsync(Obs.FlushSnapshotRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new Obs.FlushSnapshotReply { Outcome = new Obs.FlushSnapshotReplyOutcome.FlushedCase(SnapshotStream.Flush()) });
        }
    }

}
