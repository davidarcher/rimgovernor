using System.Threading.Channels;
using RimGovernor.Brain.Domain;
using Authority = RimGovernor.Contracts.Wire.Authority;
using Clock = RimGovernor.Contracts.Wire.Clock;
using Lifecycle = RimGovernor.Contracts.Wire.Lifecycle;
using Mirror = RimGovernor.Contracts.Wire.Mirror;
using Obs = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Placement = RimGovernor.Contracts.Wire.Placement;
using Presentation = RimGovernor.Contracts.Wire.Presentation;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

namespace RimGovernor.Brain.NativeFacade;

/// <summary>
/// The brain's direct, in-memory call path to the native mod: the capabilities Go reached over GABP and
/// ProtoJSON (<c>contracts/native-protobuf-cutover.md</c>) as typed calls. A call carries the same request and
/// reply messages as the capability, so the protobuf shapes stay at this boundary until #3100 replaces them with
/// plain records; no call is serialized, and nothing is parsed from text.
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
/// Failure: a refusal the capability would have sent (invalid request, stale identity, unavailable fact) is the
/// reply's <c>Failure</c> or <c>Unavailable</c> arm, exactly as before. A fault inside the hop (a bug) faults the
/// task. Cancelling the token before the call runs cancels the task; once it ran, its effects stand.
/// </para>
/// </remarks>
public interface INativeFacade
{
    /// <summary>
    /// Def-level stat reads answered by the game on its own thread (<c>GameThreadStatService</c>). Its calls are synchronous
    /// and block the calling thread until the game thread drains them, so they must not be made on the game thread
    /// (they throw there). Memoized per load; per-frame game-thread budget documented in <c>docs/developers/architecture/native-facade.md</c>.
    /// </summary>
    IStatSource Stats { get; }

    // Lifecycle.

    Task<Lifecycle.IdentityReply> ReadIdentityAsync(Lifecycle.IdentityRequest request, CancellationToken cancellationToken);

    Task<Lifecycle.TickReply> ReadTickAsync(Lifecycle.TickRequest request, CancellationToken cancellationToken);

    Task<Lifecycle.GovernorStateReply> ReadGovernorStateAsync(Lifecycle.GovernorStateRequest request, CancellationToken cancellationToken);

    /// <summary>Replaces the whole governor-state blob set; staging is lock-guarded and needs the game thread only to attach the component to an older save.</summary>
    Task<Lifecycle.GovernorStateReply> PutGovernorStateBatchAsync(Lifecycle.PutGovernorStateBatchRequest request, CancellationToken cancellationToken);

    /// <summary>Long-poll for a vanilla pre-save signal; completes after the timeout or the signal. Runs off the game thread.</summary>
    Task<Lifecycle.WaitSaveSignalReply> WaitSaveSignalAsync(Lifecycle.WaitSaveSignalRequest request, CancellationToken cancellationToken);

    Task<Lifecycle.FlushDoneReply> FlushDoneAsync(Lifecycle.FlushDoneRequest request, CancellationToken cancellationToken);

    // Authority.

    Task<Authority.StatusReply> ReadAuthorityStatusAsync(Authority.StatusRequest request, CancellationToken cancellationToken);

    Task<Authority.ControlReply> ControlAuthorityAsync(Authority.ControlRequest request, CancellationToken cancellationToken);

    // Placement and zones.

    Task<Placement.PlacementReply> PreviewPlacementAsync(Placement.PlacementRequest request, CancellationToken cancellationToken);

    Task<Operations.ZonePreviewReply> PreviewZoneAsync(Operations.ZonePreviewRequest request, CancellationToken cancellationToken);

    // Observations.

    Task<Obs.StatusReply> ReadStatusAsync(Obs.StatusRequest request, CancellationToken cancellationToken);

    Task<Obs.ColonyFactsReply> ReadColonyFactsAsync(Obs.ColonyFactsRequest request, CancellationToken cancellationToken);

    Task<Obs.GetCellsReply> GetCellsAsync(Obs.GetCellsRequest request, CancellationToken cancellationToken);

    Task<Obs.ListBuildingsReply> ListBuildingsAsync(Obs.ListBuildingsRequest request, CancellationToken cancellationToken);

    Task<Obs.ListPawnsReply> ListPawnsAsync(Obs.ListPawnsRequest request, CancellationToken cancellationToken);

    Task<Obs.ListSuppliesReply> ListSuppliesAsync(Obs.ListSuppliesRequest request, CancellationToken cancellationToken);

    Task<Obs.ListRoomsReply> ListRoomsAsync(Obs.ListRoomsRequest request, CancellationToken cancellationToken);

    Task<Obs.ResearchReply> ReadResearchAsync(Obs.ResearchRequest request, CancellationToken cancellationToken);

    Task<Obs.DefinitionCatalogReply> ReadDefinitionCatalogAsync(Obs.DefinitionCatalogRequest request, CancellationToken cancellationToken);

    Task<Obs.BillsReply> ReadBillsAsync(Obs.BillsRequest request, CancellationToken cancellationToken);

    Task<Obs.RecipesReply> ReadRecipesAsync(Obs.RecipesRequest request, CancellationToken cancellationToken);

    // Operations. Every write is an Apply intent; Hands owns the game-order writes on the game thread.

    Task<Operations.ApplyReply> ApplyAsync(Operations.ApplyRequest request, CancellationToken cancellationToken);

    Task<Operations.RulesAttachReply> AttachRulesAsync(Operations.RulesAttachRequest request, CancellationToken cancellationToken);

    Task<Operations.RulesClearReply> ClearRulesAsync(Operations.RulesClearRequest request, CancellationToken cancellationToken);

    Task<Operations.RulesStatusReply> ReadRulesStatusAsync(Operations.RulesStatusRequest request, CancellationToken cancellationToken);

    Task<Receipts.LookupReply> LookupReceiptAsync(Receipts.LookupRequest request, CancellationToken cancellationToken);

    // Clock.

    Task<Clock.ControlReply> StartClockAsync(Clock.StartRequest request, CancellationToken cancellationToken);

    Task<Clock.ControlReply> RenewClockAsync(Clock.RenewRequest request, CancellationToken cancellationToken);

    Task<Clock.ControlReply> ChangeClockSpeedAsync(Clock.SpeedRequest request, CancellationToken cancellationToken);

    Task<Clock.StatusReply> PauseClockAsync(Clock.OwnedRequest request, CancellationToken cancellationToken);

    Task<Clock.StatusReply> ReadClockStatusAsync(Clock.StatusRequest request, CancellationToken cancellationToken);

    Task<Clock.EventsReply> ReadClockEventsAsync(Clock.EventsRequest request, CancellationToken cancellationToken);

    Task<Clock.AttemptReply> ReadClockAttemptAsync(Clock.AttemptRequest request, CancellationToken cancellationToken);

    // Presentation reads.

    Task<Presentation.CameraReply> ReadCameraAsync(Presentation.ReadRequest request, CancellationToken cancellationToken);

    Task<Presentation.SelectionReply> ReadSelectionAsync(Presentation.ReadRequest request, CancellationToken cancellationToken);

    Task<Presentation.ColonistRosterReply> ReadColonistsAsync(Presentation.ColonistRosterRequest request, CancellationToken cancellationToken);

    // The remaining native tools (#3100): each runs the tool's own typed core, validation included.

    Task<Obs.ClearanceTargetsReply> ReadClearanceTargetsAsync(Obs.ClearanceTargetsRequest request, CancellationToken cancellationToken);

    Task<Mirror.CombatGeometryReply> ReadCombatGeometryAsync(Mirror.CombatGeometryRequest request, CancellationToken cancellationToken);

    Task<Obs.ConsumptionReply> ReadConsumptionAsync(Obs.ConsumptionRequest request, CancellationToken cancellationToken);

    Task<Obs.DefenseSiteReply> ReadDefenseSiteAsync(Obs.DefenseSiteRequest request, CancellationToken cancellationToken);

    Task<Obs.LinesOfFireReply> ReadLinesOfFireAsync(Obs.LinesOfFireRequest request, CancellationToken cancellationToken);

    Task<Obs.HusbandryReply> ReadHusbandryAsync(Obs.HusbandryRequest request, CancellationToken cancellationToken);

    Task<Presentation.NotificationsReply> ReadNotificationsAsync(Presentation.NotificationsRequest request, CancellationToken cancellationToken);

    Task<Obs.PopulationReply> ReadPopulationAsync(Obs.PopulationRequest request, CancellationToken cancellationToken);

    Task<Obs.ResourceSourcesReply> ListResourceSourcesAsync(Obs.ResourceSourcesRequest request, CancellationToken cancellationToken);

    Task<Obs.AncientShrinesReply> ReadAncientShrinesAsync(Obs.AncientShrinesRequest request, CancellationToken cancellationToken);

    Task<Obs.SpatialAccessReply> ReadSpatialAccessAsync(Obs.SpatialAccessRequest request, CancellationToken cancellationToken);

    Task<Obs.EvaluateStatReply> EvaluateStatAsync(Obs.EvaluateStatRequest request, CancellationToken cancellationToken);

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

    Task<Lifecycle.SaveReply> SaveGameAsync(Lifecycle.SaveRequest request, CancellationToken cancellationToken);

    Task<Lifecycle.SaveReply> PollSaveAsync(Lifecycle.RequestStatus request, CancellationToken cancellationToken);

    Task<Presentation.OverlayReply> ApplyOverlayAsync(Presentation.OverlayRequest request, CancellationToken cancellationToken);

    Task<Presentation.RenderReply> ReadRenderStateAsync(Presentation.ReadRequest request, CancellationToken cancellationToken);

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
