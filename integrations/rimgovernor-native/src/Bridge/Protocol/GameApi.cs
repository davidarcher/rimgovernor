using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using RimGovernor.Brain.Bridge;

namespace RimGovernor.Native
{
    /// <summary>
    /// The native side of <see cref="IGameApi"/>: each capability the tools expose is called here as a typed
    /// call into the same core the tool runs, on the game thread through <see cref="GameThread.OnGameThread{T}"/>
    /// (the tool's admission class, per-frame allowance, watchdog and observation account), with nothing parsed
    /// from or encoded to text. The few capabilities that never touch the game thread (the save handshake, and the
    /// governor-state write on a game that already carries its component) run on the caller's thread, as their tools
    /// do on the bridge worker.
    /// </summary>
    internal sealed class GameApi : IGameApi
    {
        internal static readonly GameApi Instance = new GameApi();

        private const string Control = GameThreadQueue.Control, Observation = GameThreadQueue.Observation;

        private GameApi()
        {
            GameThread.HopTimed = RimGovernor.Brain.GameAccess.ReadTallyRules.CountHop;
            GameThread.HopPartTimed = RimGovernor.Brain.GameAccess.ReadTallyRules.CountHopPart;
        }

        public ChannelReader<NativeFrame> Frames => SnapshotStream.Handoff.Reader;

        public Task<RimGovernor.Brain.GameFacts.Observations.ActivityTaskRead> ReadActivityTasksAsync(RimGovernor.Brain.GameOrders.Observations.ActivityTaskRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadActivityTasks, request, () => NativeActivityTasks.Read(request), cancellationToken);
        public Task<RimGovernor.Brain.GameFacts.Observations.ConstructionActivityRead> ReadConstructionActivityAsync(RimGovernor.Brain.GameOrders.Observations.ConstructionActivityRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadConstructionActivity, request, () => NativeConstructionActivity.Read(request), cancellationToken);

        // A hop for a read (observation class) or a write (control class, which runs ahead of queued reads). Each call
        // is counted once into the attached read tally under the facade method called, errors and cancellations included.
        private static Task<T> Read<TRequest, T>(NativeOp<TRequest, T> op, TRequest request, Func<T> core, CancellationToken cancellationToken,
            [CallerMemberName] string tool = "") where TRequest : class where T : class
        {
            Count(tool);
            ArgumentNullException.ThrowIfNull(request);
            return GameThread.OnGameThread("facade/" + op.Name, core, cancellationToken, Observation);
        }

        private static Task<T> Write<TRequest, T>(NativeOp<TRequest, T> op, TRequest request, Func<T> core, CancellationToken cancellationToken,
            [CallerMemberName] string tool = "") where TRequest : class where T : class
        {
            Count(tool);
            ArgumentNullException.ThrowIfNull(request);
            return GameThread.OnGameThread("facade/" + op.Name, core, cancellationToken, Control);
        }

        // The calls that never hop count here; opening the colony events reads nothing from the game and is not counted.
        private static void Count([CallerMemberName] string tool = "") => RimGovernor.Brain.GameAccess.ReadTallyRules.CountNativeRead(tool);

        // RimGovernor.Brain.GameFacts.Lifecycle.

        public Task<RimGovernor.Brain.GameFacts.Lifecycle.LoadedIdentity> ReadIdentityAsync(RimGovernor.Brain.GameOrders.Lifecycle.IdentityRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadIdentity, request, () => IdentityTools.HandleReadIdentity(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Lifecycle.LoadedTick> ReadTickAsync(RimGovernor.Brain.GameOrders.Lifecycle.TickRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadTick, request, () => IdentityTools.HandleReadTick(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Lifecycle.GovernorStateBlobs> ReadGovernorStateAsync(RimGovernor.Brain.GameOrders.Lifecycle.GovernorStateRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadGovernorState, request, GovernorStateTools.HandleRead, cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Lifecycle.GovernorStateReply> PutGovernorStateBatchAsync(RimGovernor.Brain.GameOrders.Lifecycle.PutGovernorStateBatchRequest request, CancellationToken cancellationToken)
        {
            Count();
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            var reply = GovernorStateTools.TryPut(request, out var set);
            if (reply != null) return Task.FromResult(reply);
            // A save that predates the component needs the game thread to attach it; the call is already counted.
            return GameThread.OnGameThread("facade/" + NativeOps.PutGovernorStateBatch.Name, () => GovernorStateTools.PutOnGameThread(set), cancellationToken, Control);
        }

        public Task<RimGovernor.Brain.GameFacts.Lifecycle.WaitSaveSignalReply> WaitSaveSignalAsync(RimGovernor.Brain.GameOrders.Lifecycle.WaitSaveSignalRequest request, CancellationToken cancellationToken)
        {
            Count();
            ArgumentNullException.ThrowIfNull(request);
            return LifecycleSaveSignalTools.WaitCore(request, cancellationToken);
        }

        public Task<RimGovernor.Brain.GameFacts.Lifecycle.FlushDoneReply> FlushDoneAsync(RimGovernor.Brain.GameOrders.Lifecycle.FlushDoneRequest request, CancellationToken cancellationToken)
        {
            Count();
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(LifecycleSaveSignalTools.FlushDoneCore(request));
        }

        // RimGovernor.Brain.GameFacts.Authority.

        public Task<RimGovernor.Brain.GameFacts.Authority.Status> ReadAuthorityStatusAsync(RimGovernor.Brain.GameOrders.Authority.StatusRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadAuthorityStatus, request, () => NativeAuthorityTools.HandleReadStatus(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Authority.ControlReply> ControlAuthorityAsync(RimGovernor.Brain.GameOrders.Authority.ControlRequest request, CancellationToken cancellationToken)
            => Write(NativeOps.ControlAuthority, request, () => NativeAuthorityControlTools.Apply(request), cancellationToken);

        // RimGovernor.Brain.GameFacts.Placement and zones.

        public Task<RimGovernor.Brain.GameFacts.Placement.PlacementReply> PreviewPlacementAsync(RimGovernor.Brain.GameOrders.Placement.PlacementRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.PreviewPlacement, request, () => PlacementPreviewsTools.HandlePreview(request, cancellationToken), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Operations.ZonePreviewReply> PreviewZoneAsync(RimGovernor.Brain.GameOrders.Operations.ZonePreviewRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.PreviewZone, request, () => NativeOperationTools.HandlePreviewZone(request), cancellationToken);

        // Observations.

        public Task<RimGovernor.Brain.GameFacts.Observations.StatusSnapshot> ReadStatusAsync(RimGovernor.Brain.GameOrders.Observations.StatusRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadStatus, request, () => NativeObservationTools.HandleReadStatus(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot> ReadColonyFactsAsync(RimGovernor.Brain.GameOrders.Observations.ColonyFactsRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadColonyFacts, request, () => NativeColonyObservationTools.HandleReadColonyFacts(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Observations.CellsSnapshot> GetCellsAsync(RimGovernor.Brain.GameOrders.Observations.GetCellsRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.GetCells, request, () => NativeObservationTools.HandleGetCells(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Observations.BuildingsSnapshot> ListBuildingsAsync(RimGovernor.Brain.GameOrders.Observations.ListBuildingsRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ListBuildings, request, () => NativeBuildingObservationTools.HandleListBuildings(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Observations.PawnSnapshot> ListPawnsAsync(RimGovernor.Brain.GameOrders.Observations.ListPawnsRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ListPawns, request, () => NativePawnObservationTools.HandleListPawns(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Observations.TendDoctorSnapshot> ReadTendDoctorsAsync(RimGovernor.Brain.GameOrders.Observations.TendDoctorsRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadTendDoctors, request, () => NativePawnObservationTools.HandleTendDoctors(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Observations.SuppliesSnapshot> ListSuppliesAsync(RimGovernor.Brain.GameOrders.Observations.ListSuppliesRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ListSupplies, request, () => NativeSuppliesObservationTools.HandleListSupplies(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Observations.RoomsSnapshot> ListRoomsAsync(RimGovernor.Brain.GameOrders.Observations.ListRoomsRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ListRooms, request, () => NativeRoomObservationTools.HandleListRooms(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Observations.ResearchSnapshot> ReadResearchAsync(RimGovernor.Brain.GameOrders.Observations.ResearchRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadResearch, request, () => NativeResearchObservationTools.HandleReadResearch(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Observations.DefinitionCatalogAck> ReadDefinitionCatalogAsync(RimGovernor.Brain.GameOrders.Observations.DefinitionCatalogRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadDefinitionCatalog, request, () => NativeDefinitionCatalogTool.HandleReadDefinitionCatalog(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Observations.BillsSnapshot> ReadBillsAsync(RimGovernor.Brain.GameOrders.Observations.BillsRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadBills, request, () => NativeBillsObservationTools.HandleReadBills(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Observations.RecipesSnapshot> ReadRecipesAsync(RimGovernor.Brain.GameOrders.Observations.RecipesRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadRecipes, request, () => NativeBillsObservationTools.HandleReadRecipes(request), cancellationToken);

        // RimGovernor.Brain.GameFacts.Operations.

        public Task<RimGovernor.Brain.GameFacts.Operations.ApplyReply> ApplyAsync(RimGovernor.Brain.GameOrders.Operations.ApplyRequest request, CancellationToken cancellationToken)
            => Write(NativeOps.Apply, request, () => NativeActionTools.HandleApply(request), cancellationToken);

        // RimGovernor.Brain.GameFacts.Clock.

        public Task<RimGovernor.Brain.GameFacts.Clock.Status> RunClockAsync(RimGovernor.Brain.GameOrders.Clock.RunRequest request, CancellationToken cancellationToken)
            => Write(NativeOps.RunClock, request, () => NativeClockTools.HandleRun(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Clock.Status> HoldClockAsync(RimGovernor.Brain.GameOrders.Clock.HoldRequest request, CancellationToken cancellationToken)
            => Write(NativeOps.HoldClock, request, () => NativeClockTools.HandleHold(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Clock.HeartbeatAck> HeartbeatClockAsync(RimGovernor.Brain.GameOrders.Clock.HeartbeatRequest request, CancellationToken cancellationToken)
            => Write(NativeOps.HeartbeatClock, request, () => NativeClockTools.HandleHeartbeat(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Clock.Status> ReadClockStatusAsync(RimGovernor.Brain.GameOrders.Clock.StatusRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadClockStatus, request, () => NativeClockTools.HandleReadStatus(request), cancellationToken);

        // The queue is lock-free and native-owned: opening it needs no game-thread hop.
        public Task<ChannelReader<ColonyEvent>> OpenColonyEventsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Supervisor.ColonyEvents.Open());
        }

        public long ColonyEventsLost => Supervisor.ColonyEvents.Lost;

        // RimGovernor.Brain.GameFacts.Presentation reads.

        public Task<RimGovernor.Brain.GameFacts.Presentation.CameraState> ReadCameraAsync(RimGovernor.Brain.GameOrders.Presentation.ReadRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadCamera, request, () => NativePresentationReadTools.HandleCamera(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Presentation.SelectionSnapshot> ReadSelectionAsync(RimGovernor.Brain.GameOrders.Presentation.ReadRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadSelection, request, () => NativePresentationReadTools.HandleSelection(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Presentation.ColonistRoster> ReadColonistsAsync(RimGovernor.Brain.GameOrders.Presentation.ColonistRosterRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadColonists, request, () => NativePresentationReadTools.HandleColonists(request), cancellationToken);

        // The remaining native tools (#3100).

        public Task<RimGovernor.Brain.GameFacts.Observations.ClearanceTargetsSnapshot> ReadClearanceTargetsAsync(RimGovernor.Brain.GameOrders.Observations.ClearanceTargetsRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadClearanceTargets, request, () => NativeClearanceObservationTools.HandleRead(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Mirror.CombatGeometry> ReadCombatGeometryAsync(RimGovernor.Brain.GameOrders.Mirror.CombatGeometryRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadCombatGeometry, request, () => NativeCombatGeometryTools.HandleRead(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Observations.ConsumptionSnapshot> ReadConsumptionAsync(RimGovernor.Brain.GameOrders.Observations.ConsumptionRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadConsumption, request, () => NativeConsumptionTool.HandleReadConsumption(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Observations.StorageOccupancySnapshot> ReadStorageOccupancyAsync(RimGovernor.Brain.GameOrders.Observations.StorageOccupancyRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadStorageOccupancy, request, () => NativeStorageOccupancyTool.HandleRead(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Observations.DefenseSiteSnapshot> ReadDefenseSiteAsync(RimGovernor.Brain.GameOrders.Observations.DefenseSiteRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadDefenseSite, request, () => NativeDefenseObservationTools.HandleReadDefenseSite(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Observations.LinesOfFireSnapshot> ReadLinesOfFireAsync(RimGovernor.Brain.GameOrders.Observations.LinesOfFireRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadLinesOfFire, request, () => NativeDefenseObservationTools.HandleReadLinesOfFire(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Presentation.NotificationsSnapshot> ReadNotificationsAsync(RimGovernor.Brain.GameOrders.Presentation.NotificationsRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadNotifications, request, () => NativeNotificationReadTools.HandleNotifications(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Observations.PopulationSnapshot> ReadPopulationAsync(RimGovernor.Brain.GameOrders.Observations.PopulationRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadPopulation, request, () => NativePopulationObservation.HandleReadPopulation(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Observations.ResourceSourcesSnapshot> ListResourceSourcesAsync(RimGovernor.Brain.GameOrders.Observations.ResourceSourcesRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ListResourceSources, request, () => NativeResourceSourcesTool.HandleListResourceSources(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Observations.SpatialAccessSnapshot> ReadSpatialAccessAsync(RimGovernor.Brain.GameOrders.Observations.SpatialAccessRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadSpatialAccess, request, () => NativeSpatialAccessTool.HandleReadSpatialAccess(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Observations.TradeAcquisition> ReadTradeAcquisitionAsync(RimGovernor.Brain.GameOrders.Observations.TradeAcquisitionRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadTradeAcquisition, request, () => NativeTradeAcquisitionTools.HandleReadTradeAcquisition(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Observations.TradersSnapshot> ListTradersAsync(RimGovernor.Brain.GameOrders.Observations.TradersRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ListTraders, request, () => NativeTradeObservationTools.HandleListTraders(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Observations.TradeSession> ReadTradeSessionAsync(RimGovernor.Brain.GameOrders.Observations.TradeSessionRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadTradeSession, request, () => NativeTradeObservationTools.HandleReadTradeSession(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Observations.TradeSheet> ReadTradeSheetAsync(RimGovernor.Brain.GameOrders.Observations.TradeSheetRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadTradeSheet, request, () => NativeTradeObservationTools.HandleReadTradeSheet(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Observations.WallUpgradeSnapshot> ListWallUpgradeSitesAsync(RimGovernor.Brain.GameOrders.Observations.WallUpgradeSitesRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ListWallUpgradeSites, request, () => NativeWallUpgradeObservationTools.HandleListWallUpgradeSites(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Observations.WorldSnapshot> ReadWorldAsync(RimGovernor.Brain.GameOrders.Observations.WorldRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadWorld, request, () => NativeWorldObservationTools.HandleReadWorld(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Observations.WorldProgressionSnapshot> ReadWorldProgressionAsync(RimGovernor.Brain.GameOrders.Observations.WorldProgressionRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadWorldProgression, request, () => NativeWorldProgressionObservationTools.HandleReadWorldProgression(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Observations.ZonesSnapshot> ListZonesAsync(RimGovernor.Brain.GameOrders.Observations.ListZonesRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ListZones, request, () => NativeZoneObservationTools.HandleListZones(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Observations.ExcavationSiteSnapshot> ReadExcavationSiteAsync(RimGovernor.Brain.GameOrders.Observations.ExcavationSiteRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadExcavationSite, request, () => NativeObservationTools.HandleReadExcavationSite(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Lifecycle.LoadReply> StartLoadAsync(RimGovernor.Brain.GameOrders.Lifecycle.LoadRequest request, CancellationToken cancellationToken)
            => Write(NativeOps.StartLoad, request, () => LifecycleLoadTools.StartLoad(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Lifecycle.LoadReply> PollLoadAsync(RimGovernor.Brain.GameFacts.Lifecycle.RequestStatus request, CancellationToken cancellationToken)
            => Write(NativeOps.PollLoad, request, () => LifecycleLoadTools.PollLoad(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Lifecycle.NewColonyReply> StartNewColonyAsync(RimGovernor.Brain.GameOrders.Lifecycle.NewColonyRequest request, CancellationToken cancellationToken)
            => Write(NativeOps.StartNewColony, request, () => LifecycleNewColonyTools.StartNewColony(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Lifecycle.NewColonyReply> PollNewColonyAsync(RimGovernor.Brain.GameFacts.Lifecycle.RequestStatus request, CancellationToken cancellationToken)
            => Write(NativeOps.PollNewColony, request, () => LifecycleNewColonyTools.PollNewColony(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Lifecycle.MenuReply> StartMenuAsync(RimGovernor.Brain.GameOrders.Lifecycle.MenuRequest request, CancellationToken cancellationToken)
            => Write(NativeOps.StartMenu, request, () => LifecycleMenuTools.StartMenu(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Lifecycle.MenuReply> PollMenuAsync(RimGovernor.Brain.GameFacts.Lifecycle.RequestStatus request, CancellationToken cancellationToken)
            => Write(NativeOps.PollMenu, request, () => LifecycleMenuTools.PollMenu(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Lifecycle.SaveReply> SaveGameAsync(RimGovernor.Brain.GameOrders.Lifecycle.SaveRequest request, CancellationToken cancellationToken)
            => Write(NativeOps.SaveGame, request, () => LifecycleSaveTools.Apply(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Lifecycle.SaveReply> PollSaveAsync(RimGovernor.Brain.GameFacts.Lifecycle.RequestStatus request, CancellationToken cancellationToken)
            => Read(NativeOps.PollSave, request, () => LifecycleSaveTools.PollSave(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Presentation.OverlayReply> ApplyOverlayAsync(RimGovernor.Brain.GameOrders.Presentation.OverlayRequest request, CancellationToken cancellationToken)
            => Write(NativeOps.ApplyOverlay, request, () => OverlayTools.Apply(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Presentation.RenderStatus> ReadRenderStateAsync(RimGovernor.Brain.GameOrders.Presentation.ReadRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadRenderState, request, () => PresentationMediaTools.HandleRenderState(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Presentation.RenderReply> DemandRenderingAsync(RimGovernor.Brain.GameOrders.Presentation.RenderDemand request, CancellationToken cancellationToken)
            => Write(NativeOps.DemandRendering, request, () => PresentationMediaTools.HandleDemandRendering(request), cancellationToken);

        // Frame handoff.

        public Task<RimGovernor.Brain.GameFacts.Observations.SnapshotStreamReply> OpenFramesAsync(RimGovernor.Brain.GameOrders.Observations.SnapshotStreamRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.OpenFrames, request, () => SnapshotStream.Open(request), cancellationToken);

        public Task<RimGovernor.Brain.GameFacts.Observations.FlushSnapshotReply> FlushFramesAsync(RimGovernor.Brain.GameOrders.Observations.FlushSnapshotRequest request, CancellationToken cancellationToken)
        {
            Count();
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new RimGovernor.Brain.GameFacts.Observations.FlushSnapshotReply { Flushed = SnapshotStream.Flush() });
        }
    }

}
