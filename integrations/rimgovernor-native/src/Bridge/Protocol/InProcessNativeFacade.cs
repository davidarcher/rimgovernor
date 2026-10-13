#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Google.Protobuf;
using RimGovernor.Brain.NativeFacade;
using Authority = RimGovernor.Protocol.Authority;
using Clock = RimGovernor.Protocol.Clock;
using Lifecycle = RimGovernor.Protocol.Lifecycle;
using Obs = RimGovernor.Protocol.Observations;
using Operations = RimGovernor.Protocol.Operations;
using Placement = RimGovernor.Protocol.Placement;
using Presentation = RimGovernor.Protocol.Presentation;
using Receipts = RimGovernor.Protocol.Receipts;

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
        private static Task<T> Read<T>(string operation, IMessage request, Func<T> core, CancellationToken cancellationToken) where T : class
        {
            ArgumentNullException.ThrowIfNull(request);
            return ProtoBoundary.OnGameThread("facade/" + operation, core, cancellationToken, Observation);
        }

        private static Task<T> Write<T>(string operation, IMessage request, Func<T> core, CancellationToken cancellationToken) where T : class
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

        // Frame handoff.

        public Task<Obs.SnapshotStreamReply> OpenFramesAsync(Obs.SnapshotStreamRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            foreach (var resource in request.ResourceSources)
            {
                if (!ProtoBoundary.IsIdentifier(resource))
                {
                    return Task.FromResult(new Obs.SnapshotStreamReply { Failure = ProtoBoundary.Fail(RimGovernor.Protocol.Common.FailureCode.InvalidRequest, "Resource sources must be identifiers.") });
                }
            }
            return Read("observations_open_snapshot_stream", request, () => SnapshotStream.OpenInProcess(request), cancellationToken);
        }

        public Task<Obs.FlushSnapshotReply> FlushFramesAsync(Obs.FlushSnapshotRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new Obs.FlushSnapshotReply { Flushed = SnapshotStream.Flush() });
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
