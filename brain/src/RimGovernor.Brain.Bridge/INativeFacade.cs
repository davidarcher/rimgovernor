using System.Threading.Channels;
using Authority = RimGovernor.Contracts.Wire.Authority;
using Clock = RimGovernor.Contracts.Wire.Clock;
using Lifecycle = RimGovernor.Contracts.Wire.Lifecycle;
using Mirror = RimGovernor.Contracts.Wire.Mirror;
using Obs = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Placement = RimGovernor.Contracts.Wire.Placement;
using Presentation = RimGovernor.Contracts.Wire.Presentation;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

namespace RimGovernor.Brain.Bridge;

/// <summary>
/// The brain's direct, in-memory call path to the native mod: the native capabilities as typed calls
/// (<c>contracts/native-protobuf-cutover.md</c>). A call carries the same request and
/// reply records as the tool's JSON bodies (<c>RimGovernor.Contracts.Wire</c>); no call is serialized, and nothing
/// is parsed from text.
/// </summary>
/// <remarks>
/// <para>
/// Threading: every method may be called from any thread except the game thread, and never blocks the caller's
/// thread. A call that reads or writes game state is queued to the game thread under the main-thread admission
/// rules and runs there (the planner worker never calls a game API); the returned task completes with the reply
/// on a thread-pool thread. The replies are fully built when the task completes, so they are immutable by
/// convention: nobody mutates a reply after it is returned.
/// </para>
/// <para>
/// Failure: a converted read returns its payload record. A refusal (invalid request, stale identity) faults the task
/// with <c>BridgeRefusedException</c> (<c>NativeFailureException</c>, carrying the failure code and detail) and an
/// unavailable game with <c>BridgeUnavailableException</c> (<c>NotLoadedException</c> when no colony map is loaded);
/// <c>NativeFaults</c> builds both. A method whose reply still declares <c>Failure</c> or <c>Unavailable</c> arms
/// (the family reads not yet converted, and <see cref="ApplyAsync"/>, whose per-action refusals are results) answers
/// them in the reply. A fault inside the hop (a bug) faults the task. Cancelling the token before the call runs
/// cancels the task; once it ran, its effects stand.
/// </para>
/// </remarks>
public interface INativeFacade
{
    // Lifecycle.

    Task<Lifecycle.LoadedIdentity> ReadIdentityAsync(Lifecycle.IdentityRequest request, CancellationToken cancellationToken);

    Task<Lifecycle.LoadedTick> ReadTickAsync(Lifecycle.TickRequest request, CancellationToken cancellationToken);

    Task<Lifecycle.GovernorStateBlobs> ReadGovernorStateAsync(Lifecycle.GovernorStateRequest request, CancellationToken cancellationToken);

    /// <summary>Replaces the whole governor-state blob set; staging is lock-guarded and needs the game thread only to attach the component to an older save.</summary>
    Task<Lifecycle.GovernorStateReply> PutGovernorStateBatchAsync(Lifecycle.PutGovernorStateBatchRequest request, CancellationToken cancellationToken);

    /// <summary>Long-poll for a vanilla pre-save signal; completes after the timeout or the signal. Runs off the game thread.</summary>
    Task<Lifecycle.WaitSaveSignalReply> WaitSaveSignalAsync(Lifecycle.WaitSaveSignalRequest request, CancellationToken cancellationToken);

    Task<Lifecycle.FlushDoneReply> FlushDoneAsync(Lifecycle.FlushDoneRequest request, CancellationToken cancellationToken);

    // Authority.

    Task<Authority.Status> ReadAuthorityStatusAsync(Authority.StatusRequest request, CancellationToken cancellationToken);

    Task<Authority.ControlReply> ControlAuthorityAsync(Authority.ControlRequest request, CancellationToken cancellationToken);

    // Placement and zones.

    Task<Placement.PlacementReply> PreviewPlacementAsync(Placement.PlacementRequest request, CancellationToken cancellationToken);

    Task<Operations.ZonePreviewReply> PreviewZoneAsync(Operations.ZonePreviewRequest request, CancellationToken cancellationToken);

    // Observations.

    Task<Obs.StatusReply> ReadStatusAsync(Obs.StatusRequest request, CancellationToken cancellationToken);

    Task<Obs.ColonyFactsSnapshot> ReadColonyFactsAsync(Obs.ColonyFactsRequest request, CancellationToken cancellationToken);

    Task<Obs.GetCellsReply> GetCellsAsync(Obs.GetCellsRequest request, CancellationToken cancellationToken);

    Task<Obs.ListBuildingsReply> ListBuildingsAsync(Obs.ListBuildingsRequest request, CancellationToken cancellationToken);

    Task<Obs.ListPawnsReply> ListPawnsAsync(Obs.ListPawnsRequest request, CancellationToken cancellationToken);

    Task<Obs.ActivityTaskRead> ReadActivityTasksAsync(Obs.ActivityTaskRequest request, CancellationToken cancellationToken);
    Task<Obs.ConstructionActivityRead> ReadConstructionActivityAsync(Obs.ConstructionActivityRequest request, CancellationToken cancellationToken);

    Task<Obs.ListSuppliesReply> ListSuppliesAsync(Obs.ListSuppliesRequest request, CancellationToken cancellationToken);

    Task<Obs.ListRoomsReply> ListRoomsAsync(Obs.ListRoomsRequest request, CancellationToken cancellationToken);

    Task<Obs.ResearchReply> ReadResearchAsync(Obs.ResearchRequest request, CancellationToken cancellationToken);

    Task<Obs.DefinitionCatalogAck> ReadDefinitionCatalogAsync(Obs.DefinitionCatalogRequest request, CancellationToken cancellationToken);

    Task<Obs.BillsReply> ReadBillsAsync(Obs.BillsRequest request, CancellationToken cancellationToken);

    Task<Obs.RecipesReply> ReadRecipesAsync(Obs.RecipesRequest request, CancellationToken cancellationToken);

    // Operations. Every write is an Apply intent; Hands owns the game-order writes on the game thread.

    Task<Operations.ApplyReply> ApplyAsync(Operations.ApplyRequest request, CancellationToken cancellationToken);

    // Clock.

    Task<Clock.Status> RunClockAsync(Clock.RunRequest request, CancellationToken cancellationToken);

    Task<Clock.Status> HoldClockAsync(Clock.HoldRequest request, CancellationToken cancellationToken);

    Task<Clock.HeartbeatAck> HeartbeatClockAsync(Clock.HeartbeatRequest request, CancellationToken cancellationToken);

    Task<Clock.Status> ReadClockStatusAsync(Clock.StatusRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the colony events native pushes (<see cref="ColonyEventChannel"/>): swaps in a fresh queue, completes the
    /// previous reader, clears <see cref="ColonyEventsLost"/> and returns the new reading side, so a started or reloaded
    /// brain begins empty and native keeps no reference to an earlier brain's read. In
    /// process only, so no op routes it; it does not need the game thread.
    /// </summary>
    Task<ChannelReader<ColonyEvent>> OpenColonyEventsAsync(CancellationToken cancellationToken);

    /// <summary>The colony events dropped since the last <see cref="OpenColonyEventsAsync"/>; any change forces a Full step.</summary>
    long ColonyEventsLost { get; }

    // Presentation reads.

    Task<Presentation.CameraState> ReadCameraAsync(Presentation.ReadRequest request, CancellationToken cancellationToken);

    Task<Presentation.SelectionSnapshot> ReadSelectionAsync(Presentation.ReadRequest request, CancellationToken cancellationToken);

    Task<Presentation.ColonistRosterReply> ReadColonistsAsync(Presentation.ColonistRosterRequest request, CancellationToken cancellationToken);

    // The remaining native tools (#3100): each runs the tool's own typed core, validation included.

    Task<Obs.ClearanceTargetsReply> ReadClearanceTargetsAsync(Obs.ClearanceTargetsRequest request, CancellationToken cancellationToken);

    Task<Mirror.CombatGeometryReply> ReadCombatGeometryAsync(Mirror.CombatGeometryRequest request, CancellationToken cancellationToken);

    Task<Obs.ConsumptionReply> ReadConsumptionAsync(Obs.ConsumptionRequest request, CancellationToken cancellationToken);

    Task<Obs.StorageOccupancyReply> ReadStorageOccupancyAsync(Obs.StorageOccupancyRequest request, CancellationToken cancellationToken);

    Task<Obs.DefenseSiteReply> ReadDefenseSiteAsync(Obs.DefenseSiteRequest request, CancellationToken cancellationToken);

    Task<Obs.LinesOfFireReply> ReadLinesOfFireAsync(Obs.LinesOfFireRequest request, CancellationToken cancellationToken);

    Task<Presentation.NotificationsSnapshot> ReadNotificationsAsync(Presentation.NotificationsRequest request, CancellationToken cancellationToken);

    Task<Obs.PopulationReply> ReadPopulationAsync(Obs.PopulationRequest request, CancellationToken cancellationToken);

    Task<Obs.ResourceSourcesReply> ListResourceSourcesAsync(Obs.ResourceSourcesRequest request, CancellationToken cancellationToken);

    Task<Obs.SpatialAccessReply> ReadSpatialAccessAsync(Obs.SpatialAccessRequest request, CancellationToken cancellationToken);

    Task<Obs.TradeAcquisitionReply> ReadTradeAcquisitionAsync(Obs.TradeAcquisitionRequest request, CancellationToken cancellationToken);

    Task<Obs.TradersReply> ListTradersAsync(Obs.TradersRequest request, CancellationToken cancellationToken);

    Task<Obs.TradeSessionReply> ReadTradeSessionAsync(Obs.TradeSessionRequest request, CancellationToken cancellationToken);

    Task<Obs.TradeSheetReply> ReadTradeSheetAsync(Obs.TradeSheetRequest request, CancellationToken cancellationToken);

    Task<Obs.WallUpgradeSitesReply> ListWallUpgradeSitesAsync(Obs.WallUpgradeSitesRequest request, CancellationToken cancellationToken);

    Task<Obs.WorldReply> ReadWorldAsync(Obs.WorldRequest request, CancellationToken cancellationToken);

    Task<Obs.WorldProgressionReply> ReadWorldProgressionAsync(Obs.WorldProgressionRequest request, CancellationToken cancellationToken);

    Task<Obs.ListZonesReply> ListZonesAsync(Obs.ListZonesRequest request, CancellationToken cancellationToken);

    Task<Obs.ExcavationSiteReply> ReadExcavationSiteAsync(Obs.ExcavationSiteRequest request, CancellationToken cancellationToken);

    Task<Lifecycle.LoadReply> StartLoadAsync(Lifecycle.LoadRequest request, CancellationToken cancellationToken);

    Task<Lifecycle.LoadReply> PollLoadAsync(Lifecycle.RequestStatus request, CancellationToken cancellationToken);

    Task<Lifecycle.NewColonyReply> StartNewColonyAsync(Lifecycle.NewColonyRequest request, CancellationToken cancellationToken);

    Task<Lifecycle.NewColonyReply> PollNewColonyAsync(Lifecycle.RequestStatus request, CancellationToken cancellationToken);

    Task<Lifecycle.MenuReply> StartMenuAsync(Lifecycle.MenuRequest request, CancellationToken cancellationToken);

    Task<Lifecycle.MenuReply> PollMenuAsync(Lifecycle.RequestStatus request, CancellationToken cancellationToken);

    Task<Lifecycle.SaveReply> SaveGameAsync(Lifecycle.SaveRequest request, CancellationToken cancellationToken);

    Task<Lifecycle.SaveReply> PollSaveAsync(Lifecycle.RequestStatus request, CancellationToken cancellationToken);

    Task<Presentation.OverlayReply> ApplyOverlayAsync(Presentation.OverlayRequest request, CancellationToken cancellationToken);

    Task<Presentation.RenderStatus> ReadRenderStateAsync(Presentation.ReadRequest request, CancellationToken cancellationToken);

    Task<Presentation.RenderReply> DemandRenderingAsync(Presentation.RenderDemand request, CancellationToken cancellationToken);

    // Frame handoff.

    /// <summary>
    /// Subscribes to frames (the shape <paramref name="request"/> names; a keyframe request keeps the current
    /// subscription) and makes the next frame carry every section. The reply is the stream's own Unavailable
    /// when frames cannot be captured. No shared-memory ring is created for an in-process subscriber.
    /// </summary>
    Task<Obs.SnapshotStreamReply> OpenFramesAsync(Obs.SnapshotStreamRequest request, CancellationToken cancellationToken);

    /// <summary>Makes the next frame capture every applied write; reports whether a write was uncaptured. Does not wait for the capture.</summary>
    Task<Obs.FlushSnapshotReply> FlushFramesAsync(Obs.FlushSnapshotRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// The frames the native mod captures after <see cref="OpenFramesAsync"/>. Latest wins: a reader that falls
    /// behind sees only the newest frame, and every frame is whole (no unchanged section is elided), so a dropped
    /// frame never loses a section. A frame is immutable once published.
    /// </summary>
    ChannelReader<NativeFrame> Frames { get; }
}
