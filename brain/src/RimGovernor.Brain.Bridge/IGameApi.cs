using System.Threading.Channels;

namespace RimGovernor.Brain.Bridge;

/// <summary>
/// The brain's direct, in-memory call path to the native mod: the native capabilities as typed calls.
/// A call carries the same request and
/// reply records as the tool's JSON bodies (<c>RimGovernor.Brain.GameFacts</c>); no call is serialized, and nothing
/// is parsed from text.
/// </summary>
/// <remarks>
/// <para>
/// Threading: every method may be called from any thread except the game thread, and never blocks the caller's
/// thread. A call that reads or writes game state is queued to the game thread under the game-thread queue
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
public interface IGameApi
{
    // RimGovernor.Brain.GameFacts.Lifecycle.

    Task<RimGovernor.Brain.GameFacts.Lifecycle.LoadedIdentity> ReadIdentityAsync(RimGovernor.Brain.GameOrders.Lifecycle.IdentityRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Lifecycle.LoadedTick> ReadTickAsync(RimGovernor.Brain.GameOrders.Lifecycle.TickRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Lifecycle.GovernorStateBlobs> ReadGovernorStateAsync(RimGovernor.Brain.GameOrders.Lifecycle.GovernorStateRequest request, CancellationToken cancellationToken);

    /// <summary>Replaces the whole governor-state blob set; staging is lock-guarded and needs the game thread only to attach the component to an older save.</summary>
    Task<RimGovernor.Brain.GameFacts.Lifecycle.GovernorStateReply> PutGovernorStateBatchAsync(RimGovernor.Brain.GameOrders.Lifecycle.PutGovernorStateBatchRequest request, CancellationToken cancellationToken);

    /// <summary>Long-poll for a vanilla pre-save signal; completes after the timeout or the signal. Runs off the game thread.</summary>
    Task<RimGovernor.Brain.GameFacts.Lifecycle.WaitSaveSignalReply> WaitSaveSignalAsync(RimGovernor.Brain.GameOrders.Lifecycle.WaitSaveSignalRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Lifecycle.FlushDoneReply> FlushDoneAsync(RimGovernor.Brain.GameOrders.Lifecycle.FlushDoneRequest request, CancellationToken cancellationToken);

    // RimGovernor.Brain.GameFacts.Authority.

    Task<RimGovernor.Brain.GameFacts.Authority.Status> ReadAuthorityStatusAsync(RimGovernor.Brain.GameOrders.Authority.StatusRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Authority.ControlReply> ControlAuthorityAsync(RimGovernor.Brain.GameOrders.Authority.ControlRequest request, CancellationToken cancellationToken);

    // RimGovernor.Brain.GameFacts.Placement and zones.

    Task<RimGovernor.Brain.GameFacts.Placement.PlacementReply> PreviewPlacementAsync(RimGovernor.Brain.GameOrders.Placement.PlacementRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Operations.ZonePreviewReply> PreviewZoneAsync(RimGovernor.Brain.GameOrders.Operations.ZonePreviewRequest request, CancellationToken cancellationToken);

    // Observations.

    Task<RimGovernor.Brain.GameFacts.Observations.StatusSnapshot> ReadStatusAsync(RimGovernor.Brain.GameOrders.Observations.StatusRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot> ReadColonyFactsAsync(RimGovernor.Brain.GameOrders.Observations.ColonyFactsRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Observations.CellsSnapshot> GetCellsAsync(RimGovernor.Brain.GameOrders.Observations.GetCellsRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Observations.BuildingsSnapshot> ListBuildingsAsync(RimGovernor.Brain.GameOrders.Observations.ListBuildingsRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Observations.PawnSnapshot> ListPawnsAsync(RimGovernor.Brain.GameOrders.Observations.ListPawnsRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Observations.TendDoctorSnapshot> ReadTendDoctorsAsync(RimGovernor.Brain.GameOrders.Observations.TendDoctorsRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Observations.ActivityTaskRead> ReadActivityTasksAsync(RimGovernor.Brain.GameOrders.Observations.ActivityTaskRequest request, CancellationToken cancellationToken);
    Task<RimGovernor.Brain.GameFacts.Observations.ConstructionActivityRead> ReadConstructionActivityAsync(RimGovernor.Brain.GameOrders.Observations.ConstructionActivityRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Observations.SuppliesSnapshot> ListSuppliesAsync(RimGovernor.Brain.GameOrders.Observations.ListSuppliesRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Observations.RoomsSnapshot> ListRoomsAsync(RimGovernor.Brain.GameOrders.Observations.ListRoomsRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Observations.ResearchSnapshot> ReadResearchAsync(RimGovernor.Brain.GameOrders.Observations.ResearchRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Observations.DefinitionCatalogAck> ReadDefinitionCatalogAsync(RimGovernor.Brain.GameOrders.Observations.DefinitionCatalogRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Observations.BillsSnapshot> ReadBillsAsync(RimGovernor.Brain.GameOrders.Observations.BillsRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Observations.RecipesSnapshot> ReadRecipesAsync(RimGovernor.Brain.GameOrders.Observations.RecipesRequest request, CancellationToken cancellationToken);

    // RimGovernor.Brain.GameFacts.Operations. Every write is an Apply intent; Hands owns the game-order writes on the game thread.

    Task<RimGovernor.Brain.GameFacts.Operations.ApplyReply> ApplyAsync(RimGovernor.Brain.GameOrders.Operations.ApplyRequest request, CancellationToken cancellationToken);

    // RimGovernor.Brain.GameFacts.Clock.

    Task<RimGovernor.Brain.GameFacts.Clock.Status> RunClockAsync(RimGovernor.Brain.GameOrders.Clock.RunRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Clock.Status> HoldClockAsync(RimGovernor.Brain.GameOrders.Clock.HoldRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Clock.HeartbeatAck> HeartbeatClockAsync(RimGovernor.Brain.GameOrders.Clock.HeartbeatRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Clock.Status> ReadClockStatusAsync(RimGovernor.Brain.GameOrders.Clock.StatusRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the colony events native pushes (<see cref="ColonyEventChannel"/>): swaps in a fresh queue, completes the
    /// previous reader, clears <see cref="ColonyEventsLost"/> and returns the new reading side, so a started or reloaded
    /// brain begins empty and native keeps no reference to an earlier brain's read. In
    /// process only, so no op routes it; it does not need the game thread.
    /// </summary>
    Task<ChannelReader<ColonyEvent>> OpenColonyEventsAsync(CancellationToken cancellationToken);

    /// <summary>The colony events dropped since the last <see cref="OpenColonyEventsAsync"/>; any change forces a Full step.</summary>
    long ColonyEventsLost { get; }

    // RimGovernor.Brain.GameFacts.Presentation reads.

    Task<RimGovernor.Brain.GameFacts.Presentation.CameraState> ReadCameraAsync(RimGovernor.Brain.GameOrders.Presentation.ReadRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Presentation.SelectionSnapshot> ReadSelectionAsync(RimGovernor.Brain.GameOrders.Presentation.ReadRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Presentation.ColonistRoster> ReadColonistsAsync(RimGovernor.Brain.GameOrders.Presentation.ColonistRosterRequest request, CancellationToken cancellationToken);

    // The remaining native tools (#3100): each runs the tool's own typed core, validation included.

    Task<RimGovernor.Brain.GameFacts.Observations.ClearanceTargetsSnapshot> ReadClearanceTargetsAsync(RimGovernor.Brain.GameOrders.Observations.ClearanceTargetsRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Mirror.CombatGeometry> ReadCombatGeometryAsync(RimGovernor.Brain.GameOrders.Mirror.CombatGeometryRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Observations.ConsumptionSnapshot> ReadConsumptionAsync(RimGovernor.Brain.GameOrders.Observations.ConsumptionRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Observations.StorageOccupancySnapshot> ReadStorageOccupancyAsync(RimGovernor.Brain.GameOrders.Observations.StorageOccupancyRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Observations.DefenseSiteSnapshot> ReadDefenseSiteAsync(RimGovernor.Brain.GameOrders.Observations.DefenseSiteRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Observations.LinesOfFireSnapshot> ReadLinesOfFireAsync(RimGovernor.Brain.GameOrders.Observations.LinesOfFireRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Presentation.NotificationsSnapshot> ReadNotificationsAsync(RimGovernor.Brain.GameOrders.Presentation.NotificationsRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Observations.PopulationSnapshot> ReadPopulationAsync(RimGovernor.Brain.GameOrders.Observations.PopulationRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Observations.ResourceSourcesSnapshot> ListResourceSourcesAsync(RimGovernor.Brain.GameOrders.Observations.ResourceSourcesRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Observations.SpatialAccessSnapshot> ReadSpatialAccessAsync(RimGovernor.Brain.GameOrders.Observations.SpatialAccessRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Observations.TradeAcquisition> ReadTradeAcquisitionAsync(RimGovernor.Brain.GameOrders.Observations.TradeAcquisitionRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Observations.TradersSnapshot> ListTradersAsync(RimGovernor.Brain.GameOrders.Observations.TradersRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Observations.TradeSession> ReadTradeSessionAsync(RimGovernor.Brain.GameOrders.Observations.TradeSessionRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Observations.TradeSheet> ReadTradeSheetAsync(RimGovernor.Brain.GameOrders.Observations.TradeSheetRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Observations.WallUpgradeSnapshot> ListWallUpgradeSitesAsync(RimGovernor.Brain.GameOrders.Observations.WallUpgradeSitesRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Observations.WorldSnapshot> ReadWorldAsync(RimGovernor.Brain.GameOrders.Observations.WorldRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Observations.WorldProgressionSnapshot> ReadWorldProgressionAsync(RimGovernor.Brain.GameOrders.Observations.WorldProgressionRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Observations.ZonesSnapshot> ListZonesAsync(RimGovernor.Brain.GameOrders.Observations.ListZonesRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Observations.ExcavationSiteSnapshot> ReadExcavationSiteAsync(RimGovernor.Brain.GameOrders.Observations.ExcavationSiteRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Lifecycle.LoadReply> StartLoadAsync(RimGovernor.Brain.GameOrders.Lifecycle.LoadRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Lifecycle.LoadReply> PollLoadAsync(RimGovernor.Brain.GameFacts.Lifecycle.RequestStatus request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Lifecycle.NewColonyReply> StartNewColonyAsync(RimGovernor.Brain.GameOrders.Lifecycle.NewColonyRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Lifecycle.NewColonyReply> PollNewColonyAsync(RimGovernor.Brain.GameFacts.Lifecycle.RequestStatus request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Lifecycle.MenuReply> StartMenuAsync(RimGovernor.Brain.GameOrders.Lifecycle.MenuRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Lifecycle.MenuReply> PollMenuAsync(RimGovernor.Brain.GameFacts.Lifecycle.RequestStatus request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Lifecycle.SaveReply> SaveGameAsync(RimGovernor.Brain.GameOrders.Lifecycle.SaveRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Lifecycle.SaveReply> PollSaveAsync(RimGovernor.Brain.GameFacts.Lifecycle.RequestStatus request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Presentation.OverlayReply> ApplyOverlayAsync(RimGovernor.Brain.GameOrders.Presentation.OverlayRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Presentation.RenderStatus> ReadRenderStateAsync(RimGovernor.Brain.GameOrders.Presentation.ReadRequest request, CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Presentation.RenderReply> DemandRenderingAsync(RimGovernor.Brain.GameOrders.Presentation.RenderDemand request, CancellationToken cancellationToken);

    // Frame handoff.

    /// <summary>
    /// Subscribes to frames (the shape <paramref name="request"/> names; a keyframe request keeps the current
    /// subscription) and makes the next frame carry every section. The reply is the stream's own Unavailable
    /// when frames cannot be captured. No shared-memory ring is created for an in-process subscriber.
    /// </summary>
    Task<RimGovernor.Brain.GameFacts.Observations.SnapshotStreamReply> OpenFramesAsync(RimGovernor.Brain.GameOrders.Observations.SnapshotStreamRequest request, CancellationToken cancellationToken);

    /// <summary>Makes the next frame capture every applied write; reports whether a write was uncaptured. Does not wait for the capture.</summary>
    Task<RimGovernor.Brain.GameFacts.Observations.FlushSnapshotReply> FlushFramesAsync(RimGovernor.Brain.GameOrders.Observations.FlushSnapshotRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// The frames the native mod captures after <see cref="OpenFramesAsync"/>. Latest wins: a reader that falls
    /// behind sees only the newest frame, and every frame is whole (no unchanged section is elided), so a dropped
    /// frame never loses a section. A frame is immutable once published.
    /// </summary>
    ChannelReader<NativeFrame> Frames { get; }
}
