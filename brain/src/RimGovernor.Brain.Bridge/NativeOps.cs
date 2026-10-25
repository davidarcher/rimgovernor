using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;

namespace RimGovernor.Brain.Bridge;

/// <summary>
/// One named native operation: its request and reply records and, for an <see cref="IGameApi"/> method, the
/// typed call. The name is the op's label everywhere: the facade's game-thread hop, the brain's read tally, and the
/// test-only HTTP route <c>POST /native/&lt;name&gt;</c> the out-of-process acceptance harness calls.
/// </summary>
public abstract class NativeOp
{
    private protected NativeOp(string name) => Name = name;

    /// <summary>The op's name everywhere it is called or required: <c>operations_apply</c>.</summary>
    public string Name { get; }

    public abstract Type Request { get; }

    public abstract Type Reply { get; }

    /// <summary>Whether the op is an <see cref="IGameApi"/> method (the rest are bound by the mod alone).</summary>
    public abstract bool OnFacade { get; }

    /// <summary>
    /// The HTTP edge: deserializes <paramref name="body"/> as the request record (a body that does not fit throws
    /// <see cref="System.Text.Json.JsonException"/>), calls the facade, and serializes the reply record. A refusal or
    /// unavailability the call throws propagates for the route to map to its HTTP status.
    /// </summary>
    public abstract Task<string> InvokeJsonAsync(IGameApi facade, string body, CancellationToken cancellationToken);
}

/// <summary>A native operation from <typeparamref name="TRequest"/> to <typeparamref name="TReply"/>.</summary>
public sealed class NativeOp<TRequest, TReply> : NativeOp
    where TRequest : class
    where TReply : class
{
    private readonly Func<IGameApi, TRequest, CancellationToken, Task<TReply>>? _call;

    internal NativeOp(string name, Func<IGameApi, TRequest, CancellationToken, Task<TReply>>? call)
        : base(name) => _call = call;

    public override Type Request => typeof(TRequest);

    public override Type Reply => typeof(TReply);

    public override bool OnFacade => _call is not null;

    /// <summary>Calls the op's facade method.</summary>
    public Task<TReply> CallAsync(IGameApi facade, TRequest request, CancellationToken cancellationToken) =>
        (_call ?? throw new InvalidOperationException(Name + " is not a facade method; the mod binds it."))(facade, request, cancellationToken);

    public override Task<string> InvokeJsonAsync(IGameApi facade, string body, CancellationToken cancellationToken) =>
        InvokeJsonAsync(body, (request, ct) => CallAsync(facade, request, ct), cancellationToken);

    /// <summary>The HTTP edge over a handler the mod binds (an op that is not on the facade).</summary>
    public async Task<string> InvokeJsonAsync(string body, Func<TRequest, CancellationToken, Task<TReply>> handler, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var request = OrdersJson.Deserialize<TRequest>(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        return OrdersJson.Serialize(await handler(request, cancellationToken).ConfigureAwait(false));
    }
}

/// <summary>
/// Every native operation, compile-checked: one entry per <see cref="IGameApi"/> method plus the pull read the
/// acceptance harness alone uses (<see cref="ReadSnapshot"/>). The mod's HTTP route dispatches by these entries and the
/// harness's codec translates by their record types, so no side names an operation in a string it could misspell.
/// </summary>
public static class NativeOps
{
    private static readonly List<NativeOp> Ops = [];

    // RimGovernor.Brain.GameFacts.Lifecycle.
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Lifecycle.IdentityRequest, RimGovernor.Brain.GameFacts.Lifecycle.LoadedIdentity> ReadIdentity = Op<RimGovernor.Brain.GameOrders.Lifecycle.IdentityRequest, RimGovernor.Brain.GameFacts.Lifecycle.LoadedIdentity>("lifecycle_read_identity", (f, r, c) => f.ReadIdentityAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Lifecycle.TickRequest, RimGovernor.Brain.GameFacts.Lifecycle.LoadedTick> ReadTick = Op<RimGovernor.Brain.GameOrders.Lifecycle.TickRequest, RimGovernor.Brain.GameFacts.Lifecycle.LoadedTick>("lifecycle_read_tick", (f, r, c) => f.ReadTickAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Lifecycle.GovernorStateRequest, RimGovernor.Brain.GameFacts.Lifecycle.GovernorStateBlobs> ReadGovernorState = Op<RimGovernor.Brain.GameOrders.Lifecycle.GovernorStateRequest, RimGovernor.Brain.GameFacts.Lifecycle.GovernorStateBlobs>("lifecycle_read_governor_state", (f, r, c) => f.ReadGovernorStateAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Lifecycle.PutGovernorStateBatchRequest, RimGovernor.Brain.GameFacts.Lifecycle.GovernorStateReply> PutGovernorStateBatch = Op<RimGovernor.Brain.GameOrders.Lifecycle.PutGovernorStateBatchRequest, RimGovernor.Brain.GameFacts.Lifecycle.GovernorStateReply>("lifecycle_put_governor_state_batch", (f, r, c) => f.PutGovernorStateBatchAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Lifecycle.WaitSaveSignalRequest, RimGovernor.Brain.GameFacts.Lifecycle.WaitSaveSignalReply> WaitSaveSignal = Op<RimGovernor.Brain.GameOrders.Lifecycle.WaitSaveSignalRequest, RimGovernor.Brain.GameFacts.Lifecycle.WaitSaveSignalReply>("lifecycle_wait_save_signal", (f, r, c) => f.WaitSaveSignalAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Lifecycle.FlushDoneRequest, RimGovernor.Brain.GameFacts.Lifecycle.FlushDoneReply> FlushDone = Op<RimGovernor.Brain.GameOrders.Lifecycle.FlushDoneRequest, RimGovernor.Brain.GameFacts.Lifecycle.FlushDoneReply>("lifecycle_flush_done", (f, r, c) => f.FlushDoneAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Lifecycle.LoadRequest, RimGovernor.Brain.GameFacts.Lifecycle.LoadReply> StartLoad = Op<RimGovernor.Brain.GameOrders.Lifecycle.LoadRequest, RimGovernor.Brain.GameFacts.Lifecycle.LoadReply>("lifecycle_load", (f, r, c) => f.StartLoadAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameFacts.Lifecycle.RequestStatus, RimGovernor.Brain.GameFacts.Lifecycle.LoadReply> PollLoad = Op<RimGovernor.Brain.GameFacts.Lifecycle.RequestStatus, RimGovernor.Brain.GameFacts.Lifecycle.LoadReply>("lifecycle_read_load", (f, r, c) => f.PollLoadAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Lifecycle.NewColonyRequest, RimGovernor.Brain.GameFacts.Lifecycle.NewColonyReply> StartNewColony = Op<RimGovernor.Brain.GameOrders.Lifecycle.NewColonyRequest, RimGovernor.Brain.GameFacts.Lifecycle.NewColonyReply>("lifecycle_new_colony", (f, r, c) => f.StartNewColonyAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameFacts.Lifecycle.RequestStatus, RimGovernor.Brain.GameFacts.Lifecycle.NewColonyReply> PollNewColony = Op<RimGovernor.Brain.GameFacts.Lifecycle.RequestStatus, RimGovernor.Brain.GameFacts.Lifecycle.NewColonyReply>("lifecycle_read_new_colony", (f, r, c) => f.PollNewColonyAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Lifecycle.MenuRequest, RimGovernor.Brain.GameFacts.Lifecycle.MenuReply> StartMenu = Op<RimGovernor.Brain.GameOrders.Lifecycle.MenuRequest, RimGovernor.Brain.GameFacts.Lifecycle.MenuReply>("lifecycle_menu", (f, r, c) => f.StartMenuAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameFacts.Lifecycle.RequestStatus, RimGovernor.Brain.GameFacts.Lifecycle.MenuReply> PollMenu = Op<RimGovernor.Brain.GameFacts.Lifecycle.RequestStatus, RimGovernor.Brain.GameFacts.Lifecycle.MenuReply>("lifecycle_read_menu", (f, r, c) => f.PollMenuAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Lifecycle.SaveRequest, RimGovernor.Brain.GameFacts.Lifecycle.SaveReply> SaveGame = Op<RimGovernor.Brain.GameOrders.Lifecycle.SaveRequest, RimGovernor.Brain.GameFacts.Lifecycle.SaveReply>("lifecycle_save", (f, r, c) => f.SaveGameAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameFacts.Lifecycle.RequestStatus, RimGovernor.Brain.GameFacts.Lifecycle.SaveReply> PollSave = Op<RimGovernor.Brain.GameFacts.Lifecycle.RequestStatus, RimGovernor.Brain.GameFacts.Lifecycle.SaveReply>("lifecycle_read_save", (f, r, c) => f.PollSaveAsync(r, c));

    // RimGovernor.Brain.GameFacts.Authority.
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Authority.StatusRequest, RimGovernor.Brain.GameFacts.Authority.Status> ReadAuthorityStatus = Op<RimGovernor.Brain.GameOrders.Authority.StatusRequest, RimGovernor.Brain.GameFacts.Authority.Status>("authority_read_status", (f, r, c) => f.ReadAuthorityStatusAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Authority.ControlRequest, RimGovernor.Brain.GameFacts.Authority.ControlReply> ControlAuthority = Op<RimGovernor.Brain.GameOrders.Authority.ControlRequest, RimGovernor.Brain.GameFacts.Authority.ControlReply>("authority_control", (f, r, c) => f.ControlAuthorityAsync(r, c));

    // RimGovernor.Brain.GameFacts.Placement and zones.
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Placement.PlacementRequest, RimGovernor.Brain.GameFacts.Placement.PlacementReply> PreviewPlacement = Op<RimGovernor.Brain.GameOrders.Placement.PlacementRequest, RimGovernor.Brain.GameFacts.Placement.PlacementReply>("placement_preview", (f, r, c) => f.PreviewPlacementAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Operations.ZonePreviewRequest, RimGovernor.Brain.GameFacts.Operations.ZonePreviewReply> PreviewZone = Op<RimGovernor.Brain.GameOrders.Operations.ZonePreviewRequest, RimGovernor.Brain.GameFacts.Operations.ZonePreviewReply>("zones_preview", (f, r, c) => f.PreviewZoneAsync(r, c));

    // Observations.
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Observations.StatusRequest, RimGovernor.Brain.GameFacts.Observations.StatusSnapshot> ReadStatus = Op<RimGovernor.Brain.GameOrders.Observations.StatusRequest, RimGovernor.Brain.GameFacts.Observations.StatusSnapshot>("observations_read_status", (f, r, c) => f.ReadStatusAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Observations.ColonyFactsRequest, RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot> ReadColonyFacts = Op<RimGovernor.Brain.GameOrders.Observations.ColonyFactsRequest, RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot>("observations_read_colony_facts", (f, r, c) => f.ReadColonyFactsAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Observations.GetCellsRequest, RimGovernor.Brain.GameFacts.Observations.CellsSnapshot> GetCells = Op<RimGovernor.Brain.GameOrders.Observations.GetCellsRequest, RimGovernor.Brain.GameFacts.Observations.CellsSnapshot>("observations_get_cells", (f, r, c) => f.GetCellsAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Observations.ListBuildingsRequest, RimGovernor.Brain.GameFacts.Observations.BuildingsSnapshot> ListBuildings = Op<RimGovernor.Brain.GameOrders.Observations.ListBuildingsRequest, RimGovernor.Brain.GameFacts.Observations.BuildingsSnapshot>("observations_list_buildings", (f, r, c) => f.ListBuildingsAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Observations.ListPawnsRequest, RimGovernor.Brain.GameFacts.Observations.PawnSnapshot> ListPawns = Op<RimGovernor.Brain.GameOrders.Observations.ListPawnsRequest, RimGovernor.Brain.GameFacts.Observations.PawnSnapshot>("observations_list_pawns", (f, r, c) => f.ListPawnsAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Observations.TendDoctorsRequest, RimGovernor.Brain.GameFacts.Observations.TendDoctorSnapshot> ReadTendDoctors = Op<RimGovernor.Brain.GameOrders.Observations.TendDoctorsRequest, RimGovernor.Brain.GameFacts.Observations.TendDoctorSnapshot>("observations_read_tend_doctors", (f, r, c) => f.ReadTendDoctorsAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Observations.ActivityTaskRequest, RimGovernor.Brain.GameFacts.Observations.ActivityTaskRead> ReadActivityTasks = Op<RimGovernor.Brain.GameOrders.Observations.ActivityTaskRequest, RimGovernor.Brain.GameFacts.Observations.ActivityTaskRead>("activity_tasks", (f, r, c) => f.ReadActivityTasksAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Observations.ConstructionActivityRequest, RimGovernor.Brain.GameFacts.Observations.ConstructionActivityRead> ReadConstructionActivity = Op<RimGovernor.Brain.GameOrders.Observations.ConstructionActivityRequest, RimGovernor.Brain.GameFacts.Observations.ConstructionActivityRead>("construction_activity", (f, r, c) => f.ReadConstructionActivityAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Observations.ListSuppliesRequest, RimGovernor.Brain.GameFacts.Observations.SuppliesSnapshot> ListSupplies = Op<RimGovernor.Brain.GameOrders.Observations.ListSuppliesRequest, RimGovernor.Brain.GameFacts.Observations.SuppliesSnapshot>("observations_list_supplies", (f, r, c) => f.ListSuppliesAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Observations.ListRoomsRequest, RimGovernor.Brain.GameFacts.Observations.RoomsSnapshot> ListRooms = Op<RimGovernor.Brain.GameOrders.Observations.ListRoomsRequest, RimGovernor.Brain.GameFacts.Observations.RoomsSnapshot>("observations_list_rooms", (f, r, c) => f.ListRoomsAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Observations.ResearchRequest, RimGovernor.Brain.GameFacts.Observations.ResearchSnapshot> ReadResearch = Op<RimGovernor.Brain.GameOrders.Observations.ResearchRequest, RimGovernor.Brain.GameFacts.Observations.ResearchSnapshot>("observations_read_research", (f, r, c) => f.ReadResearchAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Observations.DefinitionCatalogRequest, RimGovernor.Brain.GameFacts.Observations.DefinitionCatalogAck> ReadDefinitionCatalog = Op<RimGovernor.Brain.GameOrders.Observations.DefinitionCatalogRequest, RimGovernor.Brain.GameFacts.Observations.DefinitionCatalogAck>("observations_read_definition_catalog", (f, r, c) => f.ReadDefinitionCatalogAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Observations.BillsRequest, RimGovernor.Brain.GameFacts.Observations.BillsSnapshot> ReadBills = Op<RimGovernor.Brain.GameOrders.Observations.BillsRequest, RimGovernor.Brain.GameFacts.Observations.BillsSnapshot>("observations_read_bills", (f, r, c) => f.ReadBillsAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Observations.RecipesRequest, RimGovernor.Brain.GameFacts.Observations.RecipesSnapshot> ReadRecipes = Op<RimGovernor.Brain.GameOrders.Observations.RecipesRequest, RimGovernor.Brain.GameFacts.Observations.RecipesSnapshot>("observations_read_recipes", (f, r, c) => f.ReadRecipesAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Observations.ClearanceTargetsRequest, RimGovernor.Brain.GameFacts.Observations.ClearanceTargetsSnapshot> ReadClearanceTargets = Op<RimGovernor.Brain.GameOrders.Observations.ClearanceTargetsRequest, RimGovernor.Brain.GameFacts.Observations.ClearanceTargetsSnapshot>("observations_get_clearance_targets", (f, r, c) => f.ReadClearanceTargetsAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Mirror.CombatGeometryRequest, RimGovernor.Brain.GameFacts.Mirror.CombatGeometry> ReadCombatGeometry = Op<RimGovernor.Brain.GameOrders.Mirror.CombatGeometryRequest, RimGovernor.Brain.GameFacts.Mirror.CombatGeometry>("combat_geometry", (f, r, c) => f.ReadCombatGeometryAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Observations.ConsumptionRequest, RimGovernor.Brain.GameFacts.Observations.ConsumptionSnapshot> ReadConsumption = Op<RimGovernor.Brain.GameOrders.Observations.ConsumptionRequest, RimGovernor.Brain.GameFacts.Observations.ConsumptionSnapshot>("observations_read_consumption", (f, r, c) => f.ReadConsumptionAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Observations.StorageOccupancyRequest, RimGovernor.Brain.GameFacts.Observations.StorageOccupancySnapshot> ReadStorageOccupancy = Op<RimGovernor.Brain.GameOrders.Observations.StorageOccupancyRequest, RimGovernor.Brain.GameFacts.Observations.StorageOccupancySnapshot>("observations_read_storage_occupancy", (f, r, c) => f.ReadStorageOccupancyAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Observations.DefenseSiteRequest, RimGovernor.Brain.GameFacts.Observations.DefenseSiteSnapshot> ReadDefenseSite = Op<RimGovernor.Brain.GameOrders.Observations.DefenseSiteRequest, RimGovernor.Brain.GameFacts.Observations.DefenseSiteSnapshot>("observations_read_defense_site", (f, r, c) => f.ReadDefenseSiteAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Observations.LinesOfFireRequest, RimGovernor.Brain.GameFacts.Observations.LinesOfFireSnapshot> ReadLinesOfFire = Op<RimGovernor.Brain.GameOrders.Observations.LinesOfFireRequest, RimGovernor.Brain.GameFacts.Observations.LinesOfFireSnapshot>("observations_read_lines_of_fire", (f, r, c) => f.ReadLinesOfFireAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Observations.PopulationRequest, RimGovernor.Brain.GameFacts.Observations.PopulationSnapshot> ReadPopulation = Op<RimGovernor.Brain.GameOrders.Observations.PopulationRequest, RimGovernor.Brain.GameFacts.Observations.PopulationSnapshot>("observations_read_population", (f, r, c) => f.ReadPopulationAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Observations.ResourceSourcesRequest, RimGovernor.Brain.GameFacts.Observations.ResourceSourcesSnapshot> ListResourceSources = Op<RimGovernor.Brain.GameOrders.Observations.ResourceSourcesRequest, RimGovernor.Brain.GameFacts.Observations.ResourceSourcesSnapshot>("observations_list_resource_sources", (f, r, c) => f.ListResourceSourcesAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Observations.SpatialAccessRequest, RimGovernor.Brain.GameFacts.Observations.SpatialAccessSnapshot> ReadSpatialAccess = Op<RimGovernor.Brain.GameOrders.Observations.SpatialAccessRequest, RimGovernor.Brain.GameFacts.Observations.SpatialAccessSnapshot>("observations_read_spatial_access", (f, r, c) => f.ReadSpatialAccessAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Observations.TradeAcquisitionRequest, RimGovernor.Brain.GameFacts.Observations.TradeAcquisition> ReadTradeAcquisition = Op<RimGovernor.Brain.GameOrders.Observations.TradeAcquisitionRequest, RimGovernor.Brain.GameFacts.Observations.TradeAcquisition>("observations_read_trade_acquisition", (f, r, c) => f.ReadTradeAcquisitionAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Observations.TradersRequest, RimGovernor.Brain.GameFacts.Observations.TradersSnapshot> ListTraders = Op<RimGovernor.Brain.GameOrders.Observations.TradersRequest, RimGovernor.Brain.GameFacts.Observations.TradersSnapshot>("observations_list_traders", (f, r, c) => f.ListTradersAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Observations.TradeSessionRequest, RimGovernor.Brain.GameFacts.Observations.TradeSession> ReadTradeSession = Op<RimGovernor.Brain.GameOrders.Observations.TradeSessionRequest, RimGovernor.Brain.GameFacts.Observations.TradeSession>("observations_read_trade_session", (f, r, c) => f.ReadTradeSessionAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Observations.TradeSheetRequest, RimGovernor.Brain.GameFacts.Observations.TradeSheet> ReadTradeSheet = Op<RimGovernor.Brain.GameOrders.Observations.TradeSheetRequest, RimGovernor.Brain.GameFacts.Observations.TradeSheet>("observations_read_trade_sheet", (f, r, c) => f.ReadTradeSheetAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Observations.WallUpgradeSitesRequest, RimGovernor.Brain.GameFacts.Observations.WallUpgradeSnapshot> ListWallUpgradeSites = Op<RimGovernor.Brain.GameOrders.Observations.WallUpgradeSitesRequest, RimGovernor.Brain.GameFacts.Observations.WallUpgradeSnapshot>("observations_list_wall_upgrade_sites", (f, r, c) => f.ListWallUpgradeSitesAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Observations.WorldRequest, RimGovernor.Brain.GameFacts.Observations.WorldSnapshot> ReadWorld = Op<RimGovernor.Brain.GameOrders.Observations.WorldRequest, RimGovernor.Brain.GameFacts.Observations.WorldSnapshot>("observations_read_world", (f, r, c) => f.ReadWorldAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Observations.WorldProgressionRequest, RimGovernor.Brain.GameFacts.Observations.WorldProgressionSnapshot> ReadWorldProgression = Op<RimGovernor.Brain.GameOrders.Observations.WorldProgressionRequest, RimGovernor.Brain.GameFacts.Observations.WorldProgressionSnapshot>("observations_read_world_progression", (f, r, c) => f.ReadWorldProgressionAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Observations.ListZonesRequest, RimGovernor.Brain.GameFacts.Observations.ZonesSnapshot> ListZones = Op<RimGovernor.Brain.GameOrders.Observations.ListZonesRequest, RimGovernor.Brain.GameFacts.Observations.ZonesSnapshot>("observations_list_zones", (f, r, c) => f.ListZonesAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Observations.ExcavationSiteRequest, RimGovernor.Brain.GameFacts.Observations.ExcavationSiteSnapshot> ReadExcavationSite = Op<RimGovernor.Brain.GameOrders.Observations.ExcavationSiteRequest, RimGovernor.Brain.GameFacts.Observations.ExcavationSiteSnapshot>("observations_read_excavation_site", (f, r, c) => f.ReadExcavationSiteAsync(r, c));

    // RimGovernor.Brain.GameFacts.Operations and receipts.
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Operations.ApplyRequest, RimGovernor.Brain.GameFacts.Operations.ApplyReply> Apply = Op<RimGovernor.Brain.GameOrders.Operations.ApplyRequest, RimGovernor.Brain.GameFacts.Operations.ApplyReply>("operations_apply", (f, r, c) => f.ApplyAsync(r, c));

    // RimGovernor.Brain.GameFacts.Clock.
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Clock.RunRequest, RimGovernor.Brain.GameFacts.Clock.Status> RunClock = Op<RimGovernor.Brain.GameOrders.Clock.RunRequest, RimGovernor.Brain.GameFacts.Clock.Status>("clock_run", (f, r, c) => f.RunClockAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Clock.HoldRequest, RimGovernor.Brain.GameFacts.Clock.Status> HoldClock = Op<RimGovernor.Brain.GameOrders.Clock.HoldRequest, RimGovernor.Brain.GameFacts.Clock.Status>("clock_hold", (f, r, c) => f.HoldClockAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Clock.HeartbeatRequest, RimGovernor.Brain.GameFacts.Clock.HeartbeatAck> HeartbeatClock = Op<RimGovernor.Brain.GameOrders.Clock.HeartbeatRequest, RimGovernor.Brain.GameFacts.Clock.HeartbeatAck>("clock_heartbeat", (f, r, c) => f.HeartbeatClockAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Clock.StatusRequest, RimGovernor.Brain.GameFacts.Clock.Status> ReadClockStatus = Op<RimGovernor.Brain.GameOrders.Clock.StatusRequest, RimGovernor.Brain.GameFacts.Clock.Status>("clock_read_status", (f, r, c) => f.ReadClockStatusAsync(r, c));

    // RimGovernor.Brain.GameFacts.Presentation.
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Presentation.ReadRequest, RimGovernor.Brain.GameFacts.Presentation.CameraState> ReadCamera = Op<RimGovernor.Brain.GameOrders.Presentation.ReadRequest, RimGovernor.Brain.GameFacts.Presentation.CameraState>("presentation_camera", (f, r, c) => f.ReadCameraAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Presentation.ReadRequest, RimGovernor.Brain.GameFacts.Presentation.SelectionSnapshot> ReadSelection = Op<RimGovernor.Brain.GameOrders.Presentation.ReadRequest, RimGovernor.Brain.GameFacts.Presentation.SelectionSnapshot>("presentation_selection", (f, r, c) => f.ReadSelectionAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Presentation.ColonistRosterRequest, RimGovernor.Brain.GameFacts.Presentation.ColonistRoster> ReadColonists = Op<RimGovernor.Brain.GameOrders.Presentation.ColonistRosterRequest, RimGovernor.Brain.GameFacts.Presentation.ColonistRoster>("presentation_colonists", (f, r, c) => f.ReadColonistsAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Presentation.NotificationsRequest, RimGovernor.Brain.GameFacts.Presentation.NotificationsSnapshot> ReadNotifications = Op<RimGovernor.Brain.GameOrders.Presentation.NotificationsRequest, RimGovernor.Brain.GameFacts.Presentation.NotificationsSnapshot>("presentation_notifications", (f, r, c) => f.ReadNotificationsAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Presentation.OverlayRequest, RimGovernor.Brain.GameFacts.Presentation.OverlayReply> ApplyOverlay = Op<RimGovernor.Brain.GameOrders.Presentation.OverlayRequest, RimGovernor.Brain.GameFacts.Presentation.OverlayReply>("presentation_overlay", (f, r, c) => f.ApplyOverlayAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Presentation.ReadRequest, RimGovernor.Brain.GameFacts.Presentation.RenderStatus> ReadRenderState = Op<RimGovernor.Brain.GameOrders.Presentation.ReadRequest, RimGovernor.Brain.GameFacts.Presentation.RenderStatus>("presentation_render_state", (f, r, c) => f.ReadRenderStateAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Presentation.RenderDemand, RimGovernor.Brain.GameFacts.Presentation.RenderReply> DemandRendering = Op<RimGovernor.Brain.GameOrders.Presentation.RenderDemand, RimGovernor.Brain.GameFacts.Presentation.RenderReply>("presentation_render_demand", (f, r, c) => f.DemandRenderingAsync(r, c));

    // Frames.
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Observations.SnapshotStreamRequest, RimGovernor.Brain.GameFacts.Observations.SnapshotStreamReply> OpenFrames = Op<RimGovernor.Brain.GameOrders.Observations.SnapshotStreamRequest, RimGovernor.Brain.GameFacts.Observations.SnapshotStreamReply>("observations_open_frames", (f, r, c) => f.OpenFramesAsync(r, c));
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Observations.FlushSnapshotRequest, RimGovernor.Brain.GameFacts.Observations.FlushSnapshotReply> FlushFrames = Op<RimGovernor.Brain.GameOrders.Observations.FlushSnapshotRequest, RimGovernor.Brain.GameFacts.Observations.FlushSnapshotReply>("observations_flush_snapshot", (f, r, c) => f.FlushFramesAsync(r, c));

    /// <summary>The acceptance harness's pull read of the newest frame (no facade method: the brain reads <see cref="IGameApi.Frames"/>).</summary>
    public static readonly NativeOp<RimGovernor.Brain.GameOrders.Observations.SnapshotReadRequest, RimGovernor.Brain.GameFacts.Observations.SnapshotReadReply> ReadSnapshot = Op<RimGovernor.Brain.GameOrders.Observations.SnapshotReadRequest, RimGovernor.Brain.GameFacts.Observations.SnapshotReadReply>("observations_read_snapshot", null);

    private static readonly Dictionary<string, NativeOp> ByName = Ops.ToDictionary(op => op.Name, StringComparer.Ordinal);

    /// <summary>Every op, in declaration order.</summary>
    public static IReadOnlyList<NativeOp> All => Ops;

    /// <summary>The op with exactly <paramref name="name"/>.</summary>
    public static bool TryGet(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out NativeOp? op)
    {
        ArgumentNullException.ThrowIfNull(name);
        return ByName.TryGetValue(name, out op);
    }

    private static NativeOp<TRequest, TReply> Op<TRequest, TReply>(string name, Func<IGameApi, TRequest, CancellationToken, Task<TReply>>? call)
        where TRequest : class
        where TReply : class
    {
        var op = new NativeOp<TRequest, TReply>(name, call);
        Ops.Add(op);
        return op;
    }
}
