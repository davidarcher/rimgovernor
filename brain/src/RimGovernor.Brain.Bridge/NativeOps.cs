using RimGovernor.Contracts.Wire;
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
/// One named native operation: its request and reply records and, for an <see cref="INativeFacade"/> method, the
/// typed call. The name is the op's label everywhere: the facade's game-thread hop, the brain's read tally, and the
/// test-only HTTP route <c>POST /native/&lt;name&gt;</c> the out-of-process acceptance harness calls.
/// </summary>
public abstract class NativeOp
{
    /// <summary>The prefix the acceptance cases name an op with (<c>rimgovernor/operations_apply</c>).</summary>
    public const string ToolPrefix = "rimgovernor/";

    private protected NativeOp(string name) => Name = name;

    /// <summary>The op's name, without <see cref="ToolPrefix"/>: <c>operations_apply</c>.</summary>
    public string Name { get; }

    /// <summary>The name a case calls and requires the op by: <c>rimgovernor/operations_apply</c>.</summary>
    public string Tool => ToolPrefix + Name;

    public abstract Type Request { get; }

    public abstract Type Reply { get; }

    /// <summary>Whether the op is an <see cref="INativeFacade"/> method (the rest are bound by the mod alone).</summary>
    public abstract bool OnFacade { get; }

    /// <summary>
    /// The HTTP edge: deserializes <paramref name="body"/> as the request record (a body that does not fit throws
    /// <see cref="System.Text.Json.JsonException"/>), calls the facade, and serializes the reply record. A refusal or
    /// unavailability the call throws propagates for the route to map to its HTTP status.
    /// </summary>
    public abstract Task<string> InvokeJsonAsync(INativeFacade facade, string body, CancellationToken cancellationToken);
}

/// <summary>A native operation from <typeparamref name="TRequest"/> to <typeparamref name="TReply"/>.</summary>
public sealed class NativeOp<TRequest, TReply> : NativeOp
    where TRequest : class
    where TReply : class
{
    private readonly Func<INativeFacade, TRequest, CancellationToken, Task<TReply>>? _call;

    internal NativeOp(string name, Func<INativeFacade, TRequest, CancellationToken, Task<TReply>>? call)
        : base(name) => _call = call;

    public override Type Request => typeof(TRequest);

    public override Type Reply => typeof(TReply);

    public override bool OnFacade => _call is not null;

    /// <summary>Calls the op's facade method.</summary>
    public Task<TReply> CallAsync(INativeFacade facade, TRequest request, CancellationToken cancellationToken) =>
        (_call ?? throw new InvalidOperationException(Name + " is not a facade method; the mod binds it."))(facade, request, cancellationToken);

    public override Task<string> InvokeJsonAsync(INativeFacade facade, string body, CancellationToken cancellationToken) =>
        InvokeJsonAsync(body, (request, ct) => CallAsync(facade, request, ct), cancellationToken);

    /// <summary>The HTTP edge over a handler the mod binds (an op that is not on the facade).</summary>
    public async Task<string> InvokeJsonAsync(string body, Func<TRequest, CancellationToken, Task<TReply>> handler, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var request = WireJson.Deserialize<TRequest>(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        return WireJson.Serialize(await handler(request, cancellationToken).ConfigureAwait(false));
    }
}

/// <summary>
/// Every native operation, compile-checked: one entry per <see cref="INativeFacade"/> method plus the pull read the
/// acceptance harness alone uses (<see cref="ReadSnapshot"/>). The mod's HTTP route dispatches by these entries and the
/// harness's codec translates by their record types, so no side names an operation in a string it could misspell.
/// </summary>
public static class NativeOps
{
    private static readonly List<NativeOp> Ops = [];

    // Lifecycle.
    public static readonly NativeOp<Lifecycle.IdentityRequest, Lifecycle.LoadedIdentity> ReadIdentity = Op<Lifecycle.IdentityRequest, Lifecycle.LoadedIdentity>("lifecycle_read_identity", (f, r, c) => f.ReadIdentityAsync(r, c));
    public static readonly NativeOp<Lifecycle.TickRequest, Lifecycle.LoadedTick> ReadTick = Op<Lifecycle.TickRequest, Lifecycle.LoadedTick>("lifecycle_read_tick", (f, r, c) => f.ReadTickAsync(r, c));
    public static readonly NativeOp<Lifecycle.GovernorStateRequest, Lifecycle.GovernorStateBlobs> ReadGovernorState = Op<Lifecycle.GovernorStateRequest, Lifecycle.GovernorStateBlobs>("lifecycle_read_governor_state", (f, r, c) => f.ReadGovernorStateAsync(r, c));
    public static readonly NativeOp<Lifecycle.PutGovernorStateBatchRequest, Lifecycle.GovernorStateReply> PutGovernorStateBatch = Op<Lifecycle.PutGovernorStateBatchRequest, Lifecycle.GovernorStateReply>("lifecycle_put_governor_state_batch", (f, r, c) => f.PutGovernorStateBatchAsync(r, c));
    public static readonly NativeOp<Lifecycle.WaitSaveSignalRequest, Lifecycle.WaitSaveSignalReply> WaitSaveSignal = Op<Lifecycle.WaitSaveSignalRequest, Lifecycle.WaitSaveSignalReply>("lifecycle_wait_save_signal", (f, r, c) => f.WaitSaveSignalAsync(r, c));
    public static readonly NativeOp<Lifecycle.FlushDoneRequest, Lifecycle.FlushDoneReply> FlushDone = Op<Lifecycle.FlushDoneRequest, Lifecycle.FlushDoneReply>("lifecycle_flush_done", (f, r, c) => f.FlushDoneAsync(r, c));
    public static readonly NativeOp<Lifecycle.LoadRequest, Lifecycle.LoadReply> StartLoad = Op<Lifecycle.LoadRequest, Lifecycle.LoadReply>("lifecycle_load", (f, r, c) => f.StartLoadAsync(r, c));
    public static readonly NativeOp<Lifecycle.RequestStatus, Lifecycle.LoadReply> PollLoad = Op<Lifecycle.RequestStatus, Lifecycle.LoadReply>("lifecycle_read_load", (f, r, c) => f.PollLoadAsync(r, c));
    public static readonly NativeOp<Lifecycle.NewColonyRequest, Lifecycle.NewColonyReply> StartNewColony = Op<Lifecycle.NewColonyRequest, Lifecycle.NewColonyReply>("lifecycle_new_colony", (f, r, c) => f.StartNewColonyAsync(r, c));
    public static readonly NativeOp<Lifecycle.RequestStatus, Lifecycle.NewColonyReply> PollNewColony = Op<Lifecycle.RequestStatus, Lifecycle.NewColonyReply>("lifecycle_read_new_colony", (f, r, c) => f.PollNewColonyAsync(r, c));
    public static readonly NativeOp<Lifecycle.MenuRequest, Lifecycle.MenuReply> StartMenu = Op<Lifecycle.MenuRequest, Lifecycle.MenuReply>("lifecycle_menu", (f, r, c) => f.StartMenuAsync(r, c));
    public static readonly NativeOp<Lifecycle.RequestStatus, Lifecycle.MenuReply> PollMenu = Op<Lifecycle.RequestStatus, Lifecycle.MenuReply>("lifecycle_read_menu", (f, r, c) => f.PollMenuAsync(r, c));
    public static readonly NativeOp<Lifecycle.SaveRequest, Lifecycle.SaveReply> SaveGame = Op<Lifecycle.SaveRequest, Lifecycle.SaveReply>("lifecycle_save", (f, r, c) => f.SaveGameAsync(r, c));
    public static readonly NativeOp<Lifecycle.RequestStatus, Lifecycle.SaveReply> PollSave = Op<Lifecycle.RequestStatus, Lifecycle.SaveReply>("lifecycle_read_save", (f, r, c) => f.PollSaveAsync(r, c));

    // Authority.
    public static readonly NativeOp<Authority.StatusRequest, Authority.Status> ReadAuthorityStatus = Op<Authority.StatusRequest, Authority.Status>("authority_read_status", (f, r, c) => f.ReadAuthorityStatusAsync(r, c));
    public static readonly NativeOp<Authority.ControlRequest, Authority.ControlReply> ControlAuthority = Op<Authority.ControlRequest, Authority.ControlReply>("authority_control", (f, r, c) => f.ControlAuthorityAsync(r, c));

    // Placement and zones.
    public static readonly NativeOp<Placement.PlacementRequest, Placement.PlacementReply> PreviewPlacement = Op<Placement.PlacementRequest, Placement.PlacementReply>("placement_preview", (f, r, c) => f.PreviewPlacementAsync(r, c));
    public static readonly NativeOp<Operations.ZonePreviewRequest, Operations.ZonePreviewReply> PreviewZone = Op<Operations.ZonePreviewRequest, Operations.ZonePreviewReply>("zones_preview", (f, r, c) => f.PreviewZoneAsync(r, c));

    // Observations.
    public static readonly NativeOp<Obs.StatusRequest, Obs.StatusReply> ReadStatus = Op<Obs.StatusRequest, Obs.StatusReply>("observations_read_status", (f, r, c) => f.ReadStatusAsync(r, c));
    public static readonly NativeOp<Obs.ColonyFactsRequest, Obs.ColonyFactsSnapshot> ReadColonyFacts = Op<Obs.ColonyFactsRequest, Obs.ColonyFactsSnapshot>("observations_read_colony_facts", (f, r, c) => f.ReadColonyFactsAsync(r, c));
    public static readonly NativeOp<Obs.GetCellsRequest, Obs.CellsSnapshot> GetCells = Op<Obs.GetCellsRequest, Obs.CellsSnapshot>("observations_get_cells", (f, r, c) => f.GetCellsAsync(r, c));
    public static readonly NativeOp<Obs.ListBuildingsRequest, Obs.ListBuildingsReply> ListBuildings = Op<Obs.ListBuildingsRequest, Obs.ListBuildingsReply>("observations_list_buildings", (f, r, c) => f.ListBuildingsAsync(r, c));
    public static readonly NativeOp<Obs.ListPawnsRequest, Obs.PawnSnapshot> ListPawns = Op<Obs.ListPawnsRequest, Obs.PawnSnapshot>("observations_list_pawns", (f, r, c) => f.ListPawnsAsync(r, c));
    public static readonly NativeOp<Obs.ActivityTaskRequest, Obs.ActivityTaskRead> ReadActivityTasks = Op<Obs.ActivityTaskRequest, Obs.ActivityTaskRead>("activity_tasks", (f, r, c) => f.ReadActivityTasksAsync(r, c));
    public static readonly NativeOp<Obs.ConstructionActivityRequest, Obs.ConstructionActivityRead> ReadConstructionActivity = Op<Obs.ConstructionActivityRequest, Obs.ConstructionActivityRead>("construction_activity", (f, r, c) => f.ReadConstructionActivityAsync(r, c));
    public static readonly NativeOp<Obs.ListSuppliesRequest, Obs.ListSuppliesReply> ListSupplies = Op<Obs.ListSuppliesRequest, Obs.ListSuppliesReply>("observations_list_supplies", (f, r, c) => f.ListSuppliesAsync(r, c));
    public static readonly NativeOp<Obs.ListRoomsRequest, Obs.RoomsSnapshot> ListRooms = Op<Obs.ListRoomsRequest, Obs.RoomsSnapshot>("observations_list_rooms", (f, r, c) => f.ListRoomsAsync(r, c));
    public static readonly NativeOp<Obs.ResearchRequest, Obs.ResearchSnapshot> ReadResearch = Op<Obs.ResearchRequest, Obs.ResearchSnapshot>("observations_read_research", (f, r, c) => f.ReadResearchAsync(r, c));
    public static readonly NativeOp<Obs.DefinitionCatalogRequest, Obs.DefinitionCatalogAck> ReadDefinitionCatalog = Op<Obs.DefinitionCatalogRequest, Obs.DefinitionCatalogAck>("observations_read_definition_catalog", (f, r, c) => f.ReadDefinitionCatalogAsync(r, c));
    public static readonly NativeOp<Obs.BillsRequest, Obs.BillsReply> ReadBills = Op<Obs.BillsRequest, Obs.BillsReply>("observations_read_bills", (f, r, c) => f.ReadBillsAsync(r, c));
    public static readonly NativeOp<Obs.RecipesRequest, Obs.RecipesReply> ReadRecipes = Op<Obs.RecipesRequest, Obs.RecipesReply>("observations_read_recipes", (f, r, c) => f.ReadRecipesAsync(r, c));
    public static readonly NativeOp<Obs.ClearanceTargetsRequest, Obs.ClearanceTargetsSnapshot> ReadClearanceTargets = Op<Obs.ClearanceTargetsRequest, Obs.ClearanceTargetsSnapshot>("observations_get_clearance_targets", (f, r, c) => f.ReadClearanceTargetsAsync(r, c));
    public static readonly NativeOp<Mirror.CombatGeometryRequest, Mirror.CombatGeometryReply> ReadCombatGeometry = Op<Mirror.CombatGeometryRequest, Mirror.CombatGeometryReply>("combat_geometry", (f, r, c) => f.ReadCombatGeometryAsync(r, c));
    public static readonly NativeOp<Obs.ConsumptionRequest, Obs.ConsumptionReply> ReadConsumption = Op<Obs.ConsumptionRequest, Obs.ConsumptionReply>("observations_read_consumption", (f, r, c) => f.ReadConsumptionAsync(r, c));
    public static readonly NativeOp<Obs.StorageOccupancyRequest, Obs.StorageOccupancyReply> ReadStorageOccupancy = Op<Obs.StorageOccupancyRequest, Obs.StorageOccupancyReply>("observations_read_storage_occupancy", (f, r, c) => f.ReadStorageOccupancyAsync(r, c));
    public static readonly NativeOp<Obs.DefenseSiteRequest, Obs.DefenseSiteReply> ReadDefenseSite = Op<Obs.DefenseSiteRequest, Obs.DefenseSiteReply>("observations_read_defense_site", (f, r, c) => f.ReadDefenseSiteAsync(r, c));
    public static readonly NativeOp<Obs.LinesOfFireRequest, Obs.LinesOfFireReply> ReadLinesOfFire = Op<Obs.LinesOfFireRequest, Obs.LinesOfFireReply>("observations_read_lines_of_fire", (f, r, c) => f.ReadLinesOfFireAsync(r, c));
    public static readonly NativeOp<Obs.PopulationRequest, Obs.PopulationReply> ReadPopulation = Op<Obs.PopulationRequest, Obs.PopulationReply>("observations_read_population", (f, r, c) => f.ReadPopulationAsync(r, c));
    public static readonly NativeOp<Obs.ResourceSourcesRequest, Obs.ResourceSourcesReply> ListResourceSources = Op<Obs.ResourceSourcesRequest, Obs.ResourceSourcesReply>("observations_list_resource_sources", (f, r, c) => f.ListResourceSourcesAsync(r, c));
    public static readonly NativeOp<Obs.SpatialAccessRequest, Obs.SpatialAccessReply> ReadSpatialAccess = Op<Obs.SpatialAccessRequest, Obs.SpatialAccessReply>("observations_read_spatial_access", (f, r, c) => f.ReadSpatialAccessAsync(r, c));
    public static readonly NativeOp<Obs.TradeAcquisitionRequest, Obs.TradeAcquisitionReply> ReadTradeAcquisition = Op<Obs.TradeAcquisitionRequest, Obs.TradeAcquisitionReply>("observations_read_trade_acquisition", (f, r, c) => f.ReadTradeAcquisitionAsync(r, c));
    public static readonly NativeOp<Obs.TradersRequest, Obs.TradersReply> ListTraders = Op<Obs.TradersRequest, Obs.TradersReply>("observations_list_traders", (f, r, c) => f.ListTradersAsync(r, c));
    public static readonly NativeOp<Obs.TradeSessionRequest, Obs.TradeSessionReply> ReadTradeSession = Op<Obs.TradeSessionRequest, Obs.TradeSessionReply>("observations_read_trade_session", (f, r, c) => f.ReadTradeSessionAsync(r, c));
    public static readonly NativeOp<Obs.TradeSheetRequest, Obs.TradeSheetReply> ReadTradeSheet = Op<Obs.TradeSheetRequest, Obs.TradeSheetReply>("observations_read_trade_sheet", (f, r, c) => f.ReadTradeSheetAsync(r, c));
    public static readonly NativeOp<Obs.WallUpgradeSitesRequest, Obs.WallUpgradeSnapshot> ListWallUpgradeSites = Op<Obs.WallUpgradeSitesRequest, Obs.WallUpgradeSnapshot>("observations_list_wall_upgrade_sites", (f, r, c) => f.ListWallUpgradeSitesAsync(r, c));
    public static readonly NativeOp<Obs.WorldRequest, Obs.WorldReply> ReadWorld = Op<Obs.WorldRequest, Obs.WorldReply>("observations_read_world", (f, r, c) => f.ReadWorldAsync(r, c));
    public static readonly NativeOp<Obs.WorldProgressionRequest, Obs.WorldProgressionReply> ReadWorldProgression = Op<Obs.WorldProgressionRequest, Obs.WorldProgressionReply>("observations_read_world_progression", (f, r, c) => f.ReadWorldProgressionAsync(r, c));
    public static readonly NativeOp<Obs.ListZonesRequest, Obs.ZonesSnapshot> ListZones = Op<Obs.ListZonesRequest, Obs.ZonesSnapshot>("observations_list_zones", (f, r, c) => f.ListZonesAsync(r, c));
    public static readonly NativeOp<Obs.ExcavationSiteRequest, Obs.ExcavationSiteSnapshot> ReadExcavationSite = Op<Obs.ExcavationSiteRequest, Obs.ExcavationSiteSnapshot>("observations_read_excavation_site", (f, r, c) => f.ReadExcavationSiteAsync(r, c));

    // Operations and receipts.
    public static readonly NativeOp<Operations.ApplyRequest, Operations.ApplyReply> Apply = Op<Operations.ApplyRequest, Operations.ApplyReply>("operations_apply", (f, r, c) => f.ApplyAsync(r, c));

    // Clock.
    public static readonly NativeOp<Clock.RunRequest, Clock.Status> RunClock = Op<Clock.RunRequest, Clock.Status>("clock_run", (f, r, c) => f.RunClockAsync(r, c));
    public static readonly NativeOp<Clock.HoldRequest, Clock.Status> HoldClock = Op<Clock.HoldRequest, Clock.Status>("clock_hold", (f, r, c) => f.HoldClockAsync(r, c));
    public static readonly NativeOp<Clock.HeartbeatRequest, Clock.HeartbeatAck> HeartbeatClock = Op<Clock.HeartbeatRequest, Clock.HeartbeatAck>("clock_heartbeat", (f, r, c) => f.HeartbeatClockAsync(r, c));
    public static readonly NativeOp<Clock.StatusRequest, Clock.Status> ReadClockStatus = Op<Clock.StatusRequest, Clock.Status>("clock_read_status", (f, r, c) => f.ReadClockStatusAsync(r, c));

    // Presentation.
    public static readonly NativeOp<Presentation.ReadRequest, Presentation.CameraState> ReadCamera = Op<Presentation.ReadRequest, Presentation.CameraState>("presentation_camera", (f, r, c) => f.ReadCameraAsync(r, c));
    public static readonly NativeOp<Presentation.ReadRequest, Presentation.SelectionSnapshot> ReadSelection = Op<Presentation.ReadRequest, Presentation.SelectionSnapshot>("presentation_selection", (f, r, c) => f.ReadSelectionAsync(r, c));
    public static readonly NativeOp<Presentation.ColonistRosterRequest, Presentation.ColonistRosterReply> ReadColonists = Op<Presentation.ColonistRosterRequest, Presentation.ColonistRosterReply>("presentation_colonists", (f, r, c) => f.ReadColonistsAsync(r, c));
    public static readonly NativeOp<Presentation.NotificationsRequest, Presentation.NotificationsSnapshot> ReadNotifications = Op<Presentation.NotificationsRequest, Presentation.NotificationsSnapshot>("presentation_notifications", (f, r, c) => f.ReadNotificationsAsync(r, c));
    public static readonly NativeOp<Presentation.OverlayRequest, Presentation.OverlayReply> ApplyOverlay = Op<Presentation.OverlayRequest, Presentation.OverlayReply>("presentation_overlay", (f, r, c) => f.ApplyOverlayAsync(r, c));
    public static readonly NativeOp<Presentation.ReadRequest, Presentation.RenderStatus> ReadRenderState = Op<Presentation.ReadRequest, Presentation.RenderStatus>("presentation_render_state", (f, r, c) => f.ReadRenderStateAsync(r, c));
    public static readonly NativeOp<Presentation.RenderDemand, Presentation.RenderReply> DemandRendering = Op<Presentation.RenderDemand, Presentation.RenderReply>("presentation_render_demand", (f, r, c) => f.DemandRenderingAsync(r, c));

    // Frames.
    public static readonly NativeOp<Obs.SnapshotStreamRequest, Obs.SnapshotStreamReply> OpenFrames = Op<Obs.SnapshotStreamRequest, Obs.SnapshotStreamReply>("observations_open_frames", (f, r, c) => f.OpenFramesAsync(r, c));
    public static readonly NativeOp<Obs.FlushSnapshotRequest, Obs.FlushSnapshotReply> FlushFrames = Op<Obs.FlushSnapshotRequest, Obs.FlushSnapshotReply>("observations_flush_snapshot", (f, r, c) => f.FlushFramesAsync(r, c));

    /// <summary>The acceptance harness's pull read of the newest frame (no facade method: the brain reads <see cref="INativeFacade.Frames"/>).</summary>
    public static readonly NativeOp<Obs.SnapshotReadRequest, Obs.SnapshotReadReply> ReadSnapshot = Op<Obs.SnapshotReadRequest, Obs.SnapshotReadReply>("observations_read_snapshot", null);

    private static readonly Dictionary<string, NativeOp> ByName = Ops.ToDictionary(op => op.Name, StringComparer.Ordinal);

    /// <summary>Every op, in declaration order.</summary>
    public static IReadOnlyList<NativeOp> All => Ops;

    /// <summary>The op <paramref name="name"/> names, with or without <see cref="NativeOp.ToolPrefix"/>.</summary>
    public static bool TryGet(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out NativeOp? op)
    {
        ArgumentNullException.ThrowIfNull(name);
        return ByName.TryGetValue(name.StartsWith(NativeOp.ToolPrefix, StringComparison.Ordinal) ? name[NativeOp.ToolPrefix.Length..] : name, out op);
    }

    private static NativeOp<TRequest, TReply> Op<TRequest, TReply>(string name, Func<INativeFacade, TRequest, CancellationToken, Task<TReply>>? call)
        where TRequest : class
        where TReply : class
    {
        var op = new NativeOp<TRequest, TReply>(name, call);
        Ops.Add(op);
        return op;
    }
}
