#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
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

        private InProcessNativeFacade()
        {
            ProtoBoundary.HopTimed = RimGovernor.Brain.Bridge.Contracts.ReadTallyRules.CountHop;
            ProtoBoundary.HopPartTimed = RimGovernor.Brain.Bridge.Contracts.ReadTallyRules.CountHopPart;
        }

        public ChannelReader<NativeFrame> Frames => SnapshotStream.Handoff.Reader;

        public Task<Obs.ActivityTaskRead> ReadActivityTasksAsync(Obs.ActivityTaskRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadActivityTasks, request, () => NativeActivityTasks.Read(request), cancellationToken);
        public Task<Obs.ConstructionActivityRead> ReadConstructionActivityAsync(Obs.ConstructionActivityRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadConstructionActivity, request, () => NativeConstructionActivity.Read(request), cancellationToken);

        // A hop for a read (observation class) or a write (control class, which runs ahead of queued reads). Each call
        // is counted once into the attached read tally under the facade method called, errors and cancellations included.
        private static Task<T> Read<TRequest, T>(NativeOp<TRequest, T> op, TRequest request, Func<T> core, CancellationToken cancellationToken,
            [CallerMemberName] string tool = "") where TRequest : class where T : class
        {
            Count(tool);
            ArgumentNullException.ThrowIfNull(request);
            return ProtoBoundary.OnGameThread("facade/" + op.Name, core, cancellationToken, Observation);
        }

        private static Task<T> Write<TRequest, T>(NativeOp<TRequest, T> op, TRequest request, Func<T> core, CancellationToken cancellationToken,
            [CallerMemberName] string tool = "") where TRequest : class where T : class
        {
            Count(tool);
            ArgumentNullException.ThrowIfNull(request);
            return ProtoBoundary.OnGameThread("facade/" + op.Name, core, cancellationToken, Control);
        }

        // The calls that never hop count here; opening the colony events reads nothing from the game and is not counted.
        private static void Count([CallerMemberName] string tool = "") => RimGovernor.Brain.Bridge.Contracts.ReadTallyRules.CountNativeRead(tool);

        // Lifecycle.

        public Task<Lifecycle.LoadedIdentity> ReadIdentityAsync(Lifecycle.IdentityRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadIdentity, request, () => ProtoIdentityTools.HandleReadIdentity(request), cancellationToken);

        public Task<Lifecycle.LoadedTick> ReadTickAsync(Lifecycle.TickRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadTick, request, () => ProtoIdentityTools.HandleReadTick(request), cancellationToken);

        public Task<Lifecycle.GovernorStateBlobs> ReadGovernorStateAsync(Lifecycle.GovernorStateRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadGovernorState, request, ProtoGovernorStateTools.HandleRead, cancellationToken);

        public Task<Lifecycle.GovernorStateReply> PutGovernorStateBatchAsync(Lifecycle.PutGovernorStateBatchRequest request, CancellationToken cancellationToken)
        {
            Count();
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            var reply = ProtoGovernorStateTools.TryPut(request, out var set);
            if (reply != null) return Task.FromResult(reply);
            // A save that predates the component needs the game thread to attach it; the call is already counted.
            return ProtoBoundary.OnGameThread("facade/" + NativeOps.PutGovernorStateBatch.Name, () => ProtoGovernorStateTools.PutOnGameThread(set), cancellationToken, Control);
        }

        public Task<Lifecycle.WaitSaveSignalReply> WaitSaveSignalAsync(Lifecycle.WaitSaveSignalRequest request, CancellationToken cancellationToken)
        {
            Count();
            ArgumentNullException.ThrowIfNull(request);
            return ProtoLifecycleSaveSignalTools.WaitCore(request, cancellationToken);
        }

        public Task<Lifecycle.FlushDoneReply> FlushDoneAsync(Lifecycle.FlushDoneRequest request, CancellationToken cancellationToken)
        {
            Count();
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ProtoLifecycleSaveSignalTools.FlushDoneCore(request));
        }

        // Authority.

        public Task<Authority.Status> ReadAuthorityStatusAsync(Authority.StatusRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadAuthorityStatus, request, () => NativeAuthorityTools.HandleReadStatus(request), cancellationToken);

        public Task<Authority.ControlReply> ControlAuthorityAsync(Authority.ControlRequest request, CancellationToken cancellationToken)
            => Write(NativeOps.ControlAuthority, request, () => NativeAuthorityControlTools.Apply(request), cancellationToken);

        // Placement and zones.

        public Task<Placement.PlacementReply> PreviewPlacementAsync(Placement.PlacementRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.PreviewPlacement, request, () => PlacementPreviewsTools.HandlePreview(request, cancellationToken), cancellationToken);

        public Task<Operations.ZonePreviewReply> PreviewZoneAsync(Operations.ZonePreviewRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.PreviewZone, request, () => NativeOperationTools.HandlePreviewZone(request), cancellationToken);

        // Observations.

        public Task<Obs.StatusSnapshot> ReadStatusAsync(Obs.StatusRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadStatus, request, () => NativeObservationTools.HandleReadStatus(request), cancellationToken);

        public Task<Obs.ColonyFactsSnapshot> ReadColonyFactsAsync(Obs.ColonyFactsRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadColonyFacts, request, () => NativeColonyObservationTools.HandleReadColonyFacts(request), cancellationToken);

        public Task<Obs.CellsSnapshot> GetCellsAsync(Obs.GetCellsRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.GetCells, request, () => NativeObservationTools.HandleGetCells(request), cancellationToken);

        public Task<Obs.BuildingsSnapshot> ListBuildingsAsync(Obs.ListBuildingsRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ListBuildings, request, () => NativeBuildingObservationTools.HandleListBuildings(request), cancellationToken);

        public Task<Obs.PawnSnapshot> ListPawnsAsync(Obs.ListPawnsRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ListPawns, request, () => NativePawnObservationTools.HandleListPawns(request), cancellationToken);

        public Task<Obs.TendDoctorSnapshot> ReadTendDoctorsAsync(Obs.TendDoctorsRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadTendDoctors, request, () => NativePawnObservationTools.HandleTendDoctors(request), cancellationToken);

        public Task<Obs.SuppliesSnapshot> ListSuppliesAsync(Obs.ListSuppliesRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ListSupplies, request, () => NativeSuppliesObservationTools.HandleListSupplies(request), cancellationToken);

        public Task<Obs.RoomsSnapshot> ListRoomsAsync(Obs.ListRoomsRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ListRooms, request, () => NativeRoomObservationTools.HandleListRooms(request), cancellationToken);

        public Task<Obs.ResearchSnapshot> ReadResearchAsync(Obs.ResearchRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadResearch, request, () => NativeResearchObservationTools.HandleReadResearch(request), cancellationToken);

        public Task<Obs.DefinitionCatalogAck> ReadDefinitionCatalogAsync(Obs.DefinitionCatalogRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadDefinitionCatalog, request, () => NativeDefinitionCatalogTool.HandleReadDefinitionCatalog(request), cancellationToken);

        public Task<Obs.BillsSnapshot> ReadBillsAsync(Obs.BillsRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadBills, request, () => NativeBillsObservationTools.HandleReadBills(request), cancellationToken);

        public Task<Obs.RecipesSnapshot> ReadRecipesAsync(Obs.RecipesRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadRecipes, request, () => NativeBillsObservationTools.HandleReadRecipes(request), cancellationToken);

        // Operations.

        public Task<Operations.ApplyReply> ApplyAsync(Operations.ApplyRequest request, CancellationToken cancellationToken)
            => Write(NativeOps.Apply, request, () => NativeActionTools.HandleApply(request), cancellationToken);

        // Clock.

        public Task<Clock.Status> RunClockAsync(Clock.RunRequest request, CancellationToken cancellationToken)
            => Write(NativeOps.RunClock, request, () => NativeClockTools.HandleRun(request), cancellationToken);

        public Task<Clock.Status> HoldClockAsync(Clock.HoldRequest request, CancellationToken cancellationToken)
            => Write(NativeOps.HoldClock, request, () => NativeClockTools.HandleHold(request), cancellationToken);

        public Task<Clock.HeartbeatAck> HeartbeatClockAsync(Clock.HeartbeatRequest request, CancellationToken cancellationToken)
            => Write(NativeOps.HeartbeatClock, request, () => NativeClockTools.HandleHeartbeat(request), cancellationToken);

        public Task<Clock.Status> ReadClockStatusAsync(Clock.StatusRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadClockStatus, request, () => NativeClockTools.HandleReadStatus(request), cancellationToken);

        // The queue is lock-free and native-owned: opening it needs no game-thread hop.
        public Task<ChannelReader<ColonyEvent>> OpenColonyEventsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Supervisor.ColonyEvents.Open());
        }

        public long ColonyEventsLost => Supervisor.ColonyEvents.Lost;

        // Presentation reads.

        public Task<Presentation.CameraState> ReadCameraAsync(Presentation.ReadRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadCamera, request, () => NativePresentationReadTools.HandleCamera(request), cancellationToken);

        public Task<Presentation.SelectionSnapshot> ReadSelectionAsync(Presentation.ReadRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadSelection, request, () => NativePresentationReadTools.HandleSelection(request), cancellationToken);

        public Task<Presentation.ColonistRoster> ReadColonistsAsync(Presentation.ColonistRosterRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadColonists, request, () => NativePresentationReadTools.HandleColonists(request), cancellationToken);

        // The remaining native tools (#3100).

        public Task<Obs.ClearanceTargetsSnapshot> ReadClearanceTargetsAsync(Obs.ClearanceTargetsRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadClearanceTargets, request, () => NativeClearanceObservationTools.HandleRead(request), cancellationToken);

        public Task<Mirror.CombatGeometryReply> ReadCombatGeometryAsync(Mirror.CombatGeometryRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadCombatGeometry, request, () => NativeCombatGeometryTools.HandleRead(request), cancellationToken);

        public Task<Obs.ConsumptionSnapshot> ReadConsumptionAsync(Obs.ConsumptionRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadConsumption, request, () => NativeConsumptionTool.HandleReadConsumption(request), cancellationToken);

        public Task<Obs.StorageOccupancySnapshot> ReadStorageOccupancyAsync(Obs.StorageOccupancyRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadStorageOccupancy, request, () => NativeStorageOccupancyTool.HandleRead(request), cancellationToken);

        public Task<Obs.DefenseSiteReply> ReadDefenseSiteAsync(Obs.DefenseSiteRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadDefenseSite, request, () => NativeDefenseObservationTools.HandleReadDefenseSite(request), cancellationToken);

        public Task<Obs.LinesOfFireReply> ReadLinesOfFireAsync(Obs.LinesOfFireRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadLinesOfFire, request, () => NativeDefenseObservationTools.HandleReadLinesOfFire(request), cancellationToken);

        public Task<Presentation.NotificationsSnapshot> ReadNotificationsAsync(Presentation.NotificationsRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadNotifications, request, () => NativeNotificationReadTools.HandleNotifications(request), cancellationToken);

        public Task<Obs.PopulationSnapshot> ReadPopulationAsync(Obs.PopulationRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadPopulation, request, () => NativePopulationObservation.HandleReadPopulation(request), cancellationToken);

        public Task<Obs.ResourceSourcesSnapshot> ListResourceSourcesAsync(Obs.ResourceSourcesRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ListResourceSources, request, () => NativeResourceSourcesTool.HandleListResourceSources(request), cancellationToken);

        public Task<Obs.SpatialAccessReply> ReadSpatialAccessAsync(Obs.SpatialAccessRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadSpatialAccess, request, () => NativeSpatialAccessTool.HandleReadSpatialAccess(request), cancellationToken);

        public Task<Obs.TradeAcquisition> ReadTradeAcquisitionAsync(Obs.TradeAcquisitionRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadTradeAcquisition, request, () => NativeTradeAcquisitionTools.HandleReadTradeAcquisition(request), cancellationToken);

        public Task<Obs.TradersSnapshot> ListTradersAsync(Obs.TradersRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ListTraders, request, () => NativeTradeObservationTools.HandleListTraders(request), cancellationToken);

        public Task<Obs.TradeSession> ReadTradeSessionAsync(Obs.TradeSessionRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadTradeSession, request, () => NativeTradeObservationTools.HandleReadTradeSession(request), cancellationToken);

        public Task<Obs.TradeSheet> ReadTradeSheetAsync(Obs.TradeSheetRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadTradeSheet, request, () => NativeTradeObservationTools.HandleReadTradeSheet(request), cancellationToken);

        public Task<Obs.WallUpgradeSnapshot> ListWallUpgradeSitesAsync(Obs.WallUpgradeSitesRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ListWallUpgradeSites, request, () => NativeWallUpgradeObservationTools.HandleListWallUpgradeSites(request), cancellationToken);

        public Task<Obs.WorldSnapshot> ReadWorldAsync(Obs.WorldRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadWorld, request, () => NativeWorldObservationTools.HandleReadWorld(request), cancellationToken);

        public Task<Obs.WorldProgressionSnapshot> ReadWorldProgressionAsync(Obs.WorldProgressionRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadWorldProgression, request, () => NativeWorldProgressionObservationTools.HandleReadWorldProgression(request), cancellationToken);

        public Task<Obs.ZonesSnapshot> ListZonesAsync(Obs.ListZonesRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ListZones, request, () => NativeZoneObservationTools.HandleListZones(request), cancellationToken);

        public Task<Obs.ExcavationSiteSnapshot> ReadExcavationSiteAsync(Obs.ExcavationSiteRequest request, CancellationToken cancellationToken)
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

        public Task<Presentation.RenderStatus> ReadRenderStateAsync(Presentation.ReadRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.ReadRenderState, request, () => ProtoPresentationMediaTools.HandleRenderState(request), cancellationToken);

        public Task<Presentation.RenderReply> DemandRenderingAsync(Presentation.RenderDemand request, CancellationToken cancellationToken)
            => Write(NativeOps.DemandRendering, request, () => ProtoPresentationMediaTools.HandleDemandRendering(request), cancellationToken);

        // Frame handoff.

        public Task<Obs.SnapshotStreamReply> OpenFramesAsync(Obs.SnapshotStreamRequest request, CancellationToken cancellationToken)
            => Read(NativeOps.OpenFrames, request, () => SnapshotStream.Open(request), cancellationToken);

        public Task<Obs.FlushSnapshotReply> FlushFramesAsync(Obs.FlushSnapshotRequest request, CancellationToken cancellationToken)
        {
            Count();
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new Obs.FlushSnapshotReply { Flushed = SnapshotStream.Flush() });
        }
    }

}
