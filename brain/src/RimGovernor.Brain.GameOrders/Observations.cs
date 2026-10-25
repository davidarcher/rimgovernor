using ReadScope = RimGovernor.Brain.GameFacts.Observations.ReadScope;
using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameFacts;

namespace RimGovernor.Brain.GameOrders.Observations;

/// <summary>
/// include_salvage asks for salvage evidence on the rows outside Home; a
/// caller that never reads salvage leaves it false and the native read skips
/// the salvage route and storage searches.
/// planned_ground widens the census on those cells only: player-faction
/// buildings touching them become targets (with encloses_room) and their
/// constructed floors are reported per cell. Empty keeps the non-player census.
/// </summary>
public sealed record ClearanceTargetsRequest
{
    public ReadScope? Scope { get; init; }

    public bool IncludeSalvage { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Rectangle>))]
    public EquatableArray<Rectangle> PlannedGround { get; init; }

    /// <summary>
    /// Salvage evidence tunables, brain-supplied: a cached row older than
    /// salvage_max_age_ticks is recomputed; the frame refresher spends at most
    /// salvage_frame_budget_ms per update; a read computes rows with no cache
    /// entry up to salvage_inline_budget_ms, or salvage_census_budget_ms when its
    /// cache starts empty. Rows past the budget carry the skipped salvage case.
    /// </summary>
    public int SalvageMaxAgeTicks { get; init; }

    public double SalvageFrameBudgetMs { get; init; }

    public double SalvageInlineBudgetMs { get; init; }

    public double SalvageCensusBudgetMs { get; init; }
}

/// <summary>Every row carries every section; the filter only picks the pawns.</summary>
public sealed record ListPawnsRequest
{
    public ReadScope? Scope { get; init; }

    public PawnFilter? Filter { get; init; }
}

/// <summary>
/// The opt-in tend read: the doctor-side native gates of each named pawn and its reachability to every other named
/// pawn (the candidate patients). Reachability is quadratic in the pawns (about 0.3 us a pair), so it runs under
/// <see cref="ReachBudgetMs"/> main-thread milliseconds: a doctor reached after the budget is spent reports
/// <see cref="TendReach.Skipped"/>, never a partial list.
/// </summary>
public sealed record TendDoctorsRequest
{
    public required ReadScope Scope { get; init; }

    /// <summary>1..256 distinct pawn ids, dead pawns included; an id with no pawn on the map has no row.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> PawnIds { get; init; }

    public required uint ReachBudgetMs { get; init; }
}

public sealed record ListSuppliesRequest
{
    public ReadScope? Scope { get; init; }

    public required StockFilter Filter { get; init; }
}

public sealed record ListBuildingsRequest
{
    public ReadScope? Scope { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Ids { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> DefNames { get; init; }

    /// <summary>
    /// The construction stages to list; empty lists every stage.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<BuildingStatus>))]
    public EquatableArray<BuildingStatus> Statuses { get; init; }

    /// <summary>
    /// Every faction's buildings; false lists only the player's.
    /// </summary>
    public bool AnyFaction { get; init; }
}

public sealed record ListRoomsRequest
{
    public ReadScope? Scope { get; init; }

    /// <summary>Only the rooms with a cell in this rectangle; every room when absent.</summary>
    public Rectangle? Region { get; init; }

    /// <summary>Whether a room's contents and beds count the buildings in its boundary; false when omitted.</summary>
    public bool IncludeBoundary { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> RoomIds { get; init; }
}

public sealed record ListZonesRequest
{
    public ReadScope? Scope { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Ids { get; init; }

    public string? NameContains { get; init; }

    public Rectangle? Region { get; init; }

    public bool IncludeFilter { get; init; }
}

/// <summary>
/// GetCellsRequest reads the inclusive rectangle, on the map, of at most
/// 1048576 cells.
/// </summary>
public sealed record GetCellsRequest
{
    public ReadScope? Scope { get; init; }

    public Rectangle? Rectangle { get; init; }
}

/// <summary>
/// progress_only lists the current projects, every unfinished knowledge
/// project and the started unfinished ones, each with its lock reasons; the
/// other filters are ignored. The flags are false when omitted.
/// </summary>
public sealed record ResearchRequest
{
    public ReadScope? Scope { get; init; }

    public bool IncludeLocked { get; init; }

    public bool IncludeFinished { get; init; }

    public string? NameContains { get; init; }

    public bool ProgressOnly { get; init; }
}

/// <summary>
/// budget_ms: main-thread milliseconds the audit may spend (about 70-80 ms per colonist,
/// independent of the blocked and target cell counts). Colonists past it come back
/// <see cref="PawnAccess.Skipped"/>. An empty <see cref="PawnIds"/> audits every mobile free colonist.
/// </summary>
public sealed record SpatialAccessRequest
{
    public required ReadScope Scope { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<RimGovernor.Brain.Domain.Cell>))]
    public required EquatableArray<RimGovernor.Brain.Domain.Cell> BlockedCells { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<RimGovernor.Brain.Domain.Cell>))]
    public required EquatableArray<RimGovernor.Brain.Domain.Cell> TargetCells { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> PawnIds { get; init; }

    public required uint BudgetMs { get; init; }
}

public sealed record DefenseSiteRequest
{
    public required ReadScope Scope { get; init; }

    public required Rectangle Region { get; init; }
}

public sealed record LinesOfFireRequest
{
    public required ReadScope Scope { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<RimGovernor.Brain.Domain.Cell>))]
    public required EquatableArray<RimGovernor.Brain.Domain.Cell> FiringCells { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<RimGovernor.Brain.Domain.Cell>))]
    public required EquatableArray<RimGovernor.Brain.Domain.Cell> ApproachCells { get; init; }

    public required uint BudgetMs { get; init; }
}

/// <summary>
/// One or more unique nonnegative cells, at least one of them on the map, and the access cell a miner works from.
/// </summary>
public sealed record ExcavationSiteRequest
{
    public ReadScope? Scope { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<RimGovernor.Brain.Domain.Cell>))]
    public EquatableArray<RimGovernor.Brain.Domain.Cell> Cells { get; init; }

    public required RimGovernor.Brain.Domain.Cell AccessCell { get; init; }
}

public sealed record WallUpgradeSitesRequest
{
    public ReadScope? Scope { get; init; }

    public string? TargetId { get; init; }
}

/// <summary>Resource is the def name of the resource whose sources are read; a def the game does not hold is refused.</summary>
public sealed record ResourceSourcesRequest
{
    public ReadScope? Scope { get; init; }

    public required string Resource { get; init; }
}

/// <summary>SinceHour is the last hour the reader holds; the reply lists the completed hours after it (-1 reads the whole ring).</summary>
public sealed record ConsumptionRequest
{
    public required int SinceHour { get; init; }
}

/// <summary>SinceHour is the last hour the reader does not need (-1 reads the whole ring).</summary>
public sealed record StorageOccupancyRequest
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> Groups { get; init; }

    public required int SinceHour { get; init; }
}

public sealed record PopulationRequest
{
    public ReadScope? Scope { get; init; }
}

/// <summary>Settlements names the settlement search to answer; a request without one lists none.</summary>
public sealed record WorldRequest
{
    public ReadScope? Scope { get; init; }

    public SettlementSearch? Settlements { get; init; }
}



public sealed record WorldProgressionRequest
{
    public required ReadScope Scope { get; init; }
}

/// <summary>bench_id narrows the census to one bench; absent reads every player bench.</summary>
public sealed record BillsRequest
{
    public ReadScope? Scope { get; init; }

    public string? BenchId { get; init; }
}

/// <summary>
/// bench_id names the bench whose recipe availability is read; required.
/// </summary>
public sealed record RecipesRequest
{
    public ReadScope? Scope { get; init; }

    public required string BenchId { get; init; }
}

public sealed record TradersRequest
{
    public ReadScope? Scope { get; init; }
}

public sealed record TradeSessionRequest
{
    public ReadScope? Scope { get; init; }
}

public sealed record TradeSheetRequest
{
    public ReadScope? Scope { get; init; }

    public string? SessionId { get; init; }

}

public sealed record TradeAcquisitionRequest
{
    public ReadScope? Scope { get; init; }

    public RimGovernor.Brain.GameOrders.Operations.FormCaravanIntent? Pack { get; init; }
}

/// <summary>
/// HerdRadius: the cells within which same-race animals count toward a hunt row's herd size.
/// </summary>
public sealed record ColonyFactsRequest
{
    /// <summary>The requested window for native storyteller cycle times. Zero requests no future window.</summary>
    public double IncidentHorizonDays { get; init; }

    public ReadScope? Scope { get; init; }

    public uint HerdRadius { get; init; } = 25;
}

public sealed record StatusRequest
{
    public ReadScope? Scope { get; init; }
}

/// <summary>
/// The snapshot stream: native captures whole BundleSnapshot frames and hands
/// the newest to the in-process brain; this request opens it (or replaces its
/// subscription). A frame carries every state family whole (no page caps, no
/// field masks) plus the subscription's resource sources, and the whole map as
/// a cell grid. Clock status stays a dedicated read.
/// </summary>
public sealed record SnapshotStreamRequest
{
    public double IncidentHorizonDays { get; init; }

    /// <summary>
    /// Main-thread time one frame may spend measuring route path costs
    /// (FindPathNow per reachable facility x colonist pair, ~0.4 ms each). Pairs
    /// past a limit report RouteTravel.Skipped; unlimited measures every pair.
    /// </summary>
    public required FrameBudget RoutePathBudget { get; init; }

    /// <summary>
    /// The LinesOfFireRequest budget_ms of each frame's combat lines of fire.
    /// </summary>
    public required FrameBudget LinesOfFireBudget { get; init; }
}

public sealed record DefinitionCatalogRequest
{
    public required ReadScope Scope { get; init; }
}

/// <summary>
/// Reads the newest captured snapshot frame over the tool channel (the acceptance
/// harness; the in-process brain takes frames through the facade's frame channel).
/// The first read subscribes the default shape and makes the next frame capture.
/// </summary>
public sealed record SnapshotReadRequest
{
    /// <summary>
    /// Only a frame numbered past this is returned; the head and write counters always are.
    /// </summary>
    public required ulong AfterNumber { get; init; }
}

/// <summary>
/// Makes the next frame capture if any applied write is uncaptured;
/// the reply does not wait for the capture.
/// </summary>
public sealed record FlushSnapshotRequest;
