using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Contracts.Wire.Observations;

/// <summary>
/// Complete census of visible, deconstructible non-player buildings touching Home.
/// Absence of roof_blocker means safe removal.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<ClearanceClass>))]
public enum ClearanceClass
{
    [WireName("CLEARANCE_CLASS_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("CLEARANCE_CLASS_ANCIENT_WALL_DOOR")]
    AncientWallDoor = 1,
    [WireName("CLEARANCE_CLASS_SHIP_CHUNK")]
    ShipChunk = 2,
    [WireName("CLEARANCE_CLASS_ANCIENT_CASKET")]
    AncientCasket = 3,
    [WireName("CLEARANCE_CLASS_OTHER")]
    Other = 4,
}

/// <summary>
/// Ancient shrines: each sealed or opened ancient-danger room as one
/// unit. Caskets report native hit points and HasAnyContents; the occupant
/// stays unknown until opened. Guards are the hostile pawns and hives inside
/// the room rectangle and are complete only once the interior is unfogged.
/// Breach walls are the perimeter walls the player may deconstruct without
/// a roof-support blocker, each with the adjacent cell outside the room.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<ShrineGuardKind>))]
public enum ShrineGuardKind
{
    [WireName("SHRINE_GUARD_KIND_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("SHRINE_GUARD_KIND_MECHANOID")]
    Mechanoid = 1,
    [WireName("SHRINE_GUARD_KIND_INSECTOID")]
    Insectoid = 2,
    [WireName("SHRINE_GUARD_KIND_FLESHBEAST")]
    Fleshbeast = 3,
    [WireName("SHRINE_GUARD_KIND_HUMAN")]
    Human = 4,
    [WireName("SHRINE_GUARD_KIND_HIVE")]
    Hive = 5,
    [WireName("SHRINE_GUARD_KIND_OTHER")]
    Other = 6,
}

[JsonConverter(typeof(WireEnumConverter<SurgeryKind>))]
public enum SurgeryKind
{
    [WireName("SURGERY_KIND_UNSPECIFIED")]
    Unspecified = 0,
    /// <summary>
    /// Installs a part where the body part is missing.
    /// </summary>
    [WireName("SURGERY_KIND_RESTORE")]
    Restore = 1,
    /// <summary>
    /// Removes a condition.
    /// </summary>
    [WireName("SURGERY_KIND_CURE")]
    Cure = 2,
    /// <summary>
    /// Removes a damaged or artificial part.
    /// </summary>
    [WireName("SURGERY_KIND_AMPUTATE")]
    Amputate = 3,
    /// <summary>
    /// Installs over a present part, or an implant.
    /// </summary>
    [WireName("SURGERY_KIND_INSTALL")]
    Install = 4,
    /// <summary>
    /// Removes a clean natural part.
    /// </summary>
    [WireName("SURGERY_KIND_HARVEST")]
    Harvest = 5,
    /// <summary>
    /// Any other medical recipe (administer, execute).
    /// </summary>
    [WireName("SURGERY_KIND_OTHER")]
    Other = 6,
}

/// <summary>
/// Passion is a skill's vanilla passion level.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<Passion>))]
public enum Passion
{
    [WireName("PASSION_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("PASSION_NONE")]
    None = 1,
    [WireName("PASSION_MINOR")]
    Minor = 2,
    [WireName("PASSION_MAJOR")]
    Major = 3,
}

/// <summary>
/// Closed vanilla value sets read off pawns, stock and bills.
/// BreakRisk is the mental-break band the mood sits in.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<BreakRisk>))]
public enum BreakRisk
{
    [WireName("BREAK_RISK_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("BREAK_RISK_NONE")]
    None = 1,
    [WireName("BREAK_RISK_MINOR")]
    Minor = 2,
    [WireName("BREAK_RISK_MAJOR")]
    Major = 3,
    [WireName("BREAK_RISK_EXTREME")]
    Extreme = 4,
}

/// <summary>
/// HolderKind is what holds a held stack: a building container, a corpse's
/// inventory, a living pawn's inventory or its carry tracker.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<HolderKind>))]
public enum HolderKind
{
    [WireName("HOLDER_KIND_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("HOLDER_KIND_CONTAINER")]
    Container = 1,
    [WireName("HOLDER_KIND_CORPSE")]
    Corpse = 2,
    [WireName("HOLDER_KIND_PAWN_INVENTORY")]
    PawnInventory = 3,
    [WireName("HOLDER_KIND_CARRIED")]
    Carried = 4,
}

/// <summary>
/// WasteKind is why an item is waste: a rotten corpse or spoiled food.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<WasteKind>))]
public enum WasteKind
{
    [WireName("WASTE_KIND_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("WASTE_KIND_CORPSE")]
    Corpse = 1,
    [WireName("WASTE_KIND_SPOILED")]
    Spoiled = 2,
}

/// <summary>
/// BenchUnusableReason is why a bench takes no bills.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<BenchUnusableReason>))]
public enum BenchUnusableReason
{
    [WireName("BENCH_UNUSABLE_REASON_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("BENCH_UNUSABLE_REASON_NOT_PLAYER_OWNED")]
    NotPlayerOwned = 1,
    [WireName("BENCH_UNUSABLE_REASON_FORBIDDEN")]
    Forbidden = 2,
    [WireName("BENCH_UNUSABLE_REASON_BURNING")]
    Burning = 3,
    [WireName("BENCH_UNUSABLE_REASON_NOT_USABLE_FOR_BILLS")]
    NotUsableForBills = 4,
    [WireName("BENCH_UNUSABLE_REASON_OTHER")]
    Other = 5,
}

/// <summary>
/// StockCategory and StockOwnership scope a supplies read; absent means HAULABLE and OURS.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<StockCategory>))]
public enum StockCategory
{
    [WireName("STOCK_CATEGORY_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("STOCK_CATEGORY_HAULABLE")]
    Haulable = 1,
    [WireName("STOCK_CATEGORY_FOOD")]
    Food = 2,
    [WireName("STOCK_CATEGORY_WEAPONS")]
    Weapons = 3,
    [WireName("STOCK_CATEGORY_BUILDINGS")]
    Buildings = 4,
    [WireName("STOCK_CATEGORY_ALL")]
    All = 5,
}

[JsonConverter(typeof(WireEnumConverter<StockOwnership>))]
public enum StockOwnership
{
    [WireName("STOCK_OWNERSHIP_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("STOCK_OWNERSHIP_OURS")]
    Ours = 1,
    [WireName("STOCK_OWNERSHIP_ALL")]
    All = 2,
}

/// <summary>
/// BuildingStatus is the native construction stage of a building row.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<BuildingStatus>))]
public enum BuildingStatus
{
    [WireName("BUILDING_STATUS_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("BUILDING_STATUS_BLUEPRINT")]
    Blueprint = 1,
    [WireName("BUILDING_STATUS_FRAME")]
    Frame = 2,
    [WireName("BUILDING_STATUS_BUILT")]
    Built = 3,
}

/// <summary>
/// How the game's own designators would remove a cover thing.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<CoverKind>))]
public enum CoverKind
{
    [WireName("COVER_KIND_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("COVER_KIND_PLANT")]
    Plant = 1,
    [WireName("COVER_KIND_CHUNK")]
    Chunk = 2,
    [WireName("COVER_KIND_MINEABLE")]
    Mineable = 3,
    [WireName("COVER_KIND_BUILDING")]
    Building = 4,
}

/// <summary>
/// Excavation reads certify a requested set of rock cells for staged room/corridor
/// excavation. Unlike surface mining, roofed cells are admissible; support is a
/// counterfactual check that every remaining roof stays within the native support
/// radius of a holder after all requested cells are removed. Fogged cells are unknown.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<ExcavationSupport>))]
public enum ExcavationSupport
{
    [WireName("EXCAVATION_SUPPORT_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("EXCAVATION_SUPPORT_SUPPORTED")]
    Supported = 1,
    [WireName("EXCAVATION_SUPPORT_UNKNOWN")]
    Unknown = 2,
    [WireName("EXCAVATION_SUPPORT_UNSUPPORTED")]
    Unsupported = 3,
}

[JsonConverter(typeof(WireEnumConverter<WasteLocation>))]
public enum WasteLocation
{
    [WireName("WASTE_LOCATION_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("WASTE_LOCATION_EXPOSED")]
    Exposed = 1,
    [WireName("WASTE_LOCATION_RELOCATED")]
    Relocated = 2,
    [WireName("WASTE_LOCATION_BURIED")]
    Buried = 3,
}

[JsonConverter(typeof(WireEnumConverter<QuestObjectiveKind>))]
public enum QuestObjectiveKind
{
    [WireName("QUEST_OBJECTIVE_KIND_UNKNOWN")]
    Unknown = 0,
    [WireName("QUEST_OBJECTIVE_KIND_PRODUCE_ITEM")]
    ProduceItem = 1,
    [WireName("QUEST_OBJECTIVE_KIND_HARVEST_PLANT")]
    HarvestPlant = 2,
    [WireName("QUEST_OBJECTIVE_KIND_KILL_ANIMALS")]
    KillAnimals = 3,
    [WireName("QUEST_OBJECTIVE_KIND_HOST_LODGERS")]
    HostLodgers = 4,
    [WireName("QUEST_OBJECTIVE_KIND_ACCEPT_REQUIREMENT_UNMET")]
    AcceptRequirementUnmet = 5,
    [WireName("QUEST_OBJECTIVE_KIND_LOAD_PAWNS")]
    LoadPawns = 6,
    [WireName("QUEST_OBJECTIVE_KIND_LOAD_NAMED_PAWNS")]
    LoadNamedPawns = 7,
    [WireName("QUEST_OBJECTIVE_KIND_MONUMENT")]
    Monument = 8,
    [WireName("QUEST_OBJECTIVE_KIND_EXPIRY")]
    Expiry = 9,
    [WireName("QUEST_OBJECTIVE_KIND_RESCUE_PAWNS")]
    RescuePawns = 10,
    [WireName("QUEST_OBJECTIVE_KIND_INSPECT_GRAV_ENGINE")]
    InspectGravEngine = 11,
    [WireName("QUEST_OBJECTIVE_KIND_HOLD_SURVEY_SCANNER")]
    HoldSurveyScanner = 12,
    [WireName("QUEST_OBJECTIVE_KIND_HACK_THINGS")]
    HackThings = 13,
    [WireName("QUEST_OBJECTIVE_KIND_GIVE_ITEMS")]
    GiveItems = 14,
}

[JsonConverter(typeof(WireEnumConverter<WorldSiteState>))]
public enum WorldSiteState
{
    [WireName("WORLD_SITE_STATE_UNKNOWN")]
    Unknown = 0,
    [WireName("WORLD_SITE_STATE_PENDING")]
    Pending = 1,
    [WireName("WORLD_SITE_STATE_SPAWNED")]
    Spawned = 2,
    [WireName("WORLD_SITE_STATE_MAP_LOADED")]
    MapLoaded = 3,
    [WireName("WORLD_SITE_STATE_DESTROYED")]
    Destroyed = 4,
}

/// <summary>
/// Routes census: each colony facility a colonist must
/// reach (bed, work bench, storage, dining surface, defence emplacement) with
/// per-colonist native reachability from where that colonist stands and the
/// cost of the path the game itself would walk; a facility no colonist reaches
/// lists breach candidates: player wall cells of its room whose outer
/// neighbour a colonist can stand on.
/// Traffic counts cell changes of moving pawns per layer, each layer
/// decaying on its own half-life, rebuilt from zero after a load; the
/// nonzero cells are all reported per layer, busiest first, with their terrain.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<TrafficLayer>))]
public enum TrafficLayer
{
    [WireName("TRAFFIC_LAYER_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("TRAFFIC_LAYER_COLONIST")]
    Colonist = 1,
    /// <summary>
    /// Any pawn stepping from soil or sand onto a built floor.
    /// </summary>
    [WireName("TRAFFIC_LAYER_CROSSING")]
    Crossing = 2,
    /// <summary>
    /// Colony animals.
    /// </summary>
    [WireName("TRAFFIC_LAYER_ANIMAL")]
    Animal = 3,
    /// <summary>
    /// Visitors and traders.
    /// </summary>
    [WireName("TRAFFIC_LAYER_VISITOR")]
    Visitor = 4,
    /// <summary>
    /// Hostile pawns; slow decay.
    /// </summary>
    [WireName("TRAFFIC_LAYER_HOSTILE")]
    Hostile = 5,
}

[JsonConverter(typeof(WireEnumConverter<RouteFacilityKind>))]
public enum RouteFacilityKind
{
    [WireName("ROUTE_FACILITY_KIND_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("ROUTE_FACILITY_KIND_BED")]
    Bed = 1,
    [WireName("ROUTE_FACILITY_KIND_BENCH")]
    Bench = 2,
    [WireName("ROUTE_FACILITY_KIND_STORAGE")]
    Storage = 3,
    [WireName("ROUTE_FACILITY_KIND_DINING")]
    Dining = 4,
    [WireName("ROUTE_FACILITY_KIND_DEFENSE")]
    Defense = 5,
    [WireName("ROUTE_FACILITY_KIND_STOCKPILE")]
    Stockpile = 6,
}

/// <summary>
/// Cumulative deliveries counted at the production site of the player's own work
/// (a plant harvested, a catch landed, milk or eggs produced), so hauling and
/// stack merges never double-count. source_id is the growing zone id (CROP), the
/// water body's root cell "x,z" (FISH), the plant def (FORAGE: a harvested plant
/// with no zone or basin) or the animal race (ANIMAL_PRODUCT). units are the
/// produced stack counts and nutrition their human-edible nutrition; last_tick
/// is the game tick of the latest delivery.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<DeliverySourceKind>))]
public enum DeliverySourceKind
{
    [WireName("DELIVERY_SOURCE_KIND_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("DELIVERY_SOURCE_KIND_CROP")]
    Crop = 1,
    [WireName("DELIVERY_SOURCE_KIND_FISH")]
    Fish = 2,
    [WireName("DELIVERY_SOURCE_KIND_FORAGE")]
    Forage = 3,
    [WireName("DELIVERY_SOURCE_KIND_ANIMAL_PRODUCT")]
    AnimalProduct = 4,
}

/// <summary>
/// A food policy holder outside the work census: a prisoner of the
/// colony or a tame animal on the colony map with a food policy. A prisoner
/// carries its diet inputs (trait defNames, its ideoligion's precept
/// defNames); the food an animal's race can ever eat is its catalog race row's.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<FoodEaterKind>))]
public enum FoodEaterKind
{
    [WireName("FOOD_EATER_KIND_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("FOOD_EATER_KIND_PRISONER")]
    Prisoner = 1,
    [WireName("FOOD_EATER_KIND_ANIMAL")]
    Animal = 2,
}

/// <summary>
/// EntityContainmentMode: what the player has ordered done with a held entity.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<EntityContainmentModeKind>))]
public enum EntityContainmentModeKind
{
    [WireName("ENTITY_CONTAINMENT_MODE_KIND_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("ENTITY_CONTAINMENT_MODE_KIND_MAINTAIN_ONLY")]
    MaintainOnly = 1,
    [WireName("ENTITY_CONTAINMENT_MODE_KIND_STUDY")]
    Study = 2,
    [WireName("ENTITY_CONTAINMENT_MODE_KIND_RELEASE")]
    Release = 3,
    [WireName("ENTITY_CONTAINMENT_MODE_KIND_EXECUTE")]
    Execute = 4,
}

/// <summary>
/// All requests are read-only; previews/admission and UI/media have other owners.
/// Validators require context, selected outcome, finite numbers and exact IDs.
/// Every list is complete in one reply: no page size, collection cap or
/// whole-reply size cap. Never silently sample.
/// Native definition/enum names remain open strings, resolved by the game.
/// Reconciliation survives tick advancement and authority revocation.
/// </summary>
public sealed record ReadScope
{
    public Common.Identity? ExpectedIdentity { get; init; }
}

public sealed record SnapshotRef
{
    public Common.ObservationContext? Context { get; init; }

    public string? EntityId { get; init; }

    /// <summary>
    /// Opaque exact entity+settings snapshot CAS token.
    /// </summary>
    public string? Token { get; init; }
}

/// <summary>
/// Rows the query's filters excluded from the scanned set. Replies carry
/// every matching row; there is no paging or per-list cap. Only replies
/// whose query has a filter that can exclude rows carry it.
/// </summary>
public sealed record Completeness
{
    public ulong? Filtered { get; init; }
}

public sealed record ReadIssue
{
    public string? Field { get; init; }

    public Common.Unavailable? Unavailable { get; init; }
}

public sealed record Rectangle
{
    public Common.Cell? Minimum { get; init; }

    public Common.Cell? Maximum { get; init; }
}

public sealed record ClearanceTarget
{
    public string? EntityId { get; init; }

    public string? DefName { get; init; }

    public Rectangle? Occupied { get; init; }

    public bool? Deconstructible { get; init; }

    public ClearanceClass Class { get; init; }

    /// <summary>
    /// Every occupied cell, not just the origin.
    /// </summary>
    public bool? InHome { get; init; }

    public string? RoofBlocker { get; init; }

    public bool? AncientDanger { get; init; }

    /// <summary>
    /// A standing Deconstruct designation; the controller adopts it (no ownership ledger).
    /// </summary>
    public bool? Designated { get; init; }

    public SalvageEvidence? Salvage { get; init; }

    /// <summary>
    /// Set when the salvage time budget ran out before native computed this row:
    /// salvage is absent because it was not computed, not because none exists.
    /// </summary>
    public bool? SalvageSkipped { get; init; }

    /// <summary>
    /// Player-faction rows only (planned ground): the building is a wall or door
    /// on the boundary of a room that is not outdoors.
    /// </summary>
    public bool? EnclosesRoom { get; init; }
}

/// <summary>
/// A constructed floor on planned ground: vanilla's foundation-free floor
/// layer (TerrainGrid.CanRemoveTopLayerAt), one row per cell. designated is a
/// standing RemoveFloor designation.
/// </summary>
public sealed record ClearanceFloor
{
    public Common.Cell? Cell { get; init; }

    public string? DefName { get; init; }

    public bool? Designated { get; init; }
}

public sealed record SalvageYield
{
    public string DefName { get; init; } = "";

    public long Count { get; init; }

    public double UnitValue { get; init; }

    public long StorageHeadroom { get; init; }
}

public sealed record SalvageEvidence
{
    public bool Safe { get; init; }

    public double PathLength { get; init; }

    public double Labor { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<SalvageYield>))]
    public EquatableArray<SalvageYield> Yields { get; init; }

    /// <summary>
    /// Game ticks since native computed this row. Served rows keep their cached
    /// evidence until the refresher recomputes them, so age is how stale it is.
    /// </summary>
    public int AgeTicks { get; init; }
}

/// <summary>
/// A rock or slag chunk stack standing on a Home cell: a haul, never a
/// deconstruction. stored is vanilla's IsInValidStorage; destination is
/// whether ordinary hauling already has a better store cell for it.
/// </summary>
public sealed record ClearanceChunk
{
    public string? EntityId { get; init; }

    public string? DefName { get; init; }

    public Common.Cell? Cell { get; init; }

    public bool? Forbidden { get; init; }

    public bool? Stored { get; init; }

    public bool? Destination { get; init; }
}

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
    /// Salvage evidence tunables, Go-supplied: a cached row older than
    /// salvage_max_age_ticks is recomputed; the frame refresher spends at most
    /// salvage_frame_budget_ms per update; a read computes rows with no cache
    /// entry up to salvage_inline_budget_ms, or salvage_census_budget_ms when its
    /// cache starts empty. Rows past the budget carry salvage_skipped.
    /// </summary>
    public int SalvageMaxAgeTicks { get; init; }

    public double SalvageFrameBudgetMs { get; init; }

    public double SalvageInlineBudgetMs { get; init; }

    public double SalvageCensusBudgetMs { get; init; }
}

public sealed record ClearanceTargetsSnapshot
{
    public Common.ObservationContext? Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ClearanceTarget>))]
    public EquatableArray<ClearanceTarget> Targets { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ClearanceChunk>))]
    public EquatableArray<ClearanceChunk> Chunks { get; init; }

    /// <summary>
    /// Only cells inside the request's planned_ground.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<ClearanceFloor>))]
    public EquatableArray<ClearanceFloor> Floors { get; init; }
}

public sealed record ClearanceTargetsReply
{
    public ClearanceTargetsReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="ClearanceTargetsReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ClearanceTargetsReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(ClearanceTargetsReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(ClearanceTargetsReplyOutcome.FailureCase), "failure")]
public abstract record ClearanceTargetsReplyOutcome
{
    private protected ClearanceTargetsReplyOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public ClearanceTargetsSnapshot? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(ClearanceTargetsSnapshot Value) : ClearanceTargetsReplyOutcome;

    public static implicit operator ClearanceTargetsReplyOutcome(ClearanceTargetsSnapshot value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : ClearanceTargetsReplyOutcome;

    public static implicit operator ClearanceTargetsReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : ClearanceTargetsReplyOutcome;

    public static implicit operator ClearanceTargetsReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record ShrineCasket
{
    public string? EntityId { get; init; }

    public Common.Cell? Cell { get; init; }

    public uint? HitPoints { get; init; }

    public bool? HasContents { get; init; }

    public bool? PlayerClaimed { get; init; }

    /// <summary>
    /// Where a pawn stands to open it; ejected contents drop near it.
    /// </summary>
    public Common.Cell? InteractionCell { get; init; }
}

public sealed record ShrineGuard
{
    public string? EntityId { get; init; }

    public ShrineGuardKind Kind { get; init; }

    public bool? Downed { get; init; }

    public bool? Dead { get; init; }
}

/// <summary>
/// ShrineOccupant is a humanlike the caskets released, or a corpse of one: any
/// non-player humanlike pawn or corpse inside the room.
/// </summary>
public sealed record ShrineOccupant
{
    public string? EntityId { get; init; }

    public bool? Hostile { get; init; }

    public bool? Downed { get; init; }

    public bool? Dead { get; init; }

    /// <summary>
    /// Already the colony's prisoner.
    /// </summary>
    public bool? Prisoner { get; init; }

    /// <summary>
    /// Faction def name; empty when factionless.
    /// </summary>
    public string? Faction { get; init; }
}

public sealed record ShrineBreachWall
{
    public string? EntityId { get; init; }

    public Common.Cell? Cell { get; init; }

    /// <summary>
    /// Adjacent cell outside the room.
    /// </summary>
    public Common.Cell? Outside { get; init; }

    /// <summary>
    /// The wall's ThingDef, the breach designation's definition.
    /// </summary>
    public string? DefName { get; init; }
}

public sealed record AncientShrine
{
    public string? ShrineId { get; init; }

    public Rectangle? Room { get; init; }

    /// <summary>
    /// Interior fogged, no open roof, no map edge.
    /// </summary>
    public bool? Sealed { get; init; }

    /// <summary>
    /// Any room cell in Home.
    /// </summary>
    public bool? InHome { get; init; }

    /// <summary>
    /// False while the interior is fogged.
    /// </summary>
    public bool? GuardsKnown { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ShrineCasket>))]
    public EquatableArray<ShrineCasket> Caskets { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ShrineGuard>))]
    public EquatableArray<ShrineGuard> Guards { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ShrineBreachWall>))]
    public EquatableArray<ShrineBreachWall> BreachWalls { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ShrineOccupant>))]
    public EquatableArray<ShrineOccupant> Occupants { get; init; }
}

public sealed record AncientShrinesRequest
{
    public ReadScope? Scope { get; init; }
}

public sealed record AncientShrinesSnapshot
{
    public Common.ObservationContext? Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<AncientShrine>))]
    public EquatableArray<AncientShrine> Shrines { get; init; }
}

public sealed record AncientShrinesReply
{
    public AncientShrinesReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="AncientShrinesReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AncientShrinesReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(AncientShrinesReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(AncientShrinesReplyOutcome.FailureCase), "failure")]
public abstract record AncientShrinesReplyOutcome
{
    private protected AncientShrinesReplyOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public AncientShrinesSnapshot? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(AncientShrinesSnapshot Value) : AncientShrinesReplyOutcome;

    public static implicit operator AncientShrinesReplyOutcome(AncientShrinesSnapshot value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : AncientShrinesReplyOutcome;

    public static implicit operator AncientShrinesReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : AncientShrinesReplyOutcome;

    public static implicit operator AncientShrinesReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record MapSize
{
    public uint? Width { get; init; }

    public uint? Height { get; init; }
}

public sealed record DefinitionRef
{
    public string? DefName { get; init; }
}

public sealed record EntityRef
{
    public string? Id { get; init; }

    public string? DefName { get; init; }

    public string? Label { get; init; }

    public int? MapId { get; init; }

    public Common.Cell? Position { get; init; }
}

public sealed record TargetRef
{
    public TargetRefTarget? Target { get; init; }
}

/// <summary>The <c>target</c> choice of <see cref="TargetRef"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TargetRefTarget.EntityCase), "entity")]
[JsonDerivedType(typeof(TargetRefTarget.CellCase), "cell")]
[JsonDerivedType(typeof(TargetRefTarget.UnavailableCase), "unavailable")]
public abstract record TargetRefTarget
{
    private protected TargetRefTarget() { }

    /// <summary>The <c>Entity</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Ref? Entity => (this as EntityCase)?.Value;

    public sealed record EntityCase(Common.Ref Value) : TargetRefTarget;

    public static implicit operator TargetRefTarget(Common.Ref value) => new EntityCase(value);

    /// <summary>The <c>Cell</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Cell? Cell => (this as CellCase)?.Value;

    public sealed record CellCase(Common.Cell Value) : TargetRefTarget;

    public static implicit operator TargetRefTarget(Common.Cell value) => new CellCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : TargetRefTarget;

    public static implicit operator TargetRefTarget(Common.Unavailable value) => new UnavailableCase(value);
}

public sealed record Quantity
{
    public string? DefName { get; init; }

    public long? Units { get; init; }
}

public sealed record Amount
{
    public string? DefName { get; init; }

    [JsonPropertyName("amount")]
    public double? Value { get; init; }
}

public sealed record JobEvidence
{
    public string? DefName { get; init; }

    public string? LoadId { get; init; }

    public bool? PlayerForced { get; init; }

    public string? Report { get; init; }

    public TargetRef? TargetA { get; init; }

    public ulong? OrderGeneration { get; init; }

    public uint? QueuedJobs { get; init; }

    public bool? Interruptible { get; init; }

    public double? NativePriority { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }

    /// <summary>
    /// work_type_def_name: the WorkTypeDef of the job's work giver, absent for
    /// a job no work giver issued (a forced order, rest, a meal, wandering).
    /// </summary>
    public string? WorkTypeDefName { get; init; }

    /// <summary>
    /// target_a_cell: a spawned thing target's cell when the job was read
    /// target_a carries the thing's Ref.
    /// </summary>
    public Common.Cell? TargetACell { get; init; }
}

public sealed record PawnNeeds
{
    public double? Food { get; init; }

    public double? Rest { get; init; }

    public double? Mood { get; init; }

    public double? Joy { get; init; }

    public Defs.HungerCategory? HungerCategory { get; init; }

    public BreakRisk? BreakRisk { get; init; }

    public double? BreakThresholdMinor { get; init; }

    public double? BreakThresholdMajor { get; init; }

    public double? BreakThresholdExtreme { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }

    /// <summary>
    /// Psyfocus: Pawn_PsychicEntropyTracker CurrentPsyfocus and
    /// TargetPsyfocus (0-1) and Pawn.GetPsylinkLevel(); all three are absent
    /// when Royalty is inactive or the pawn has no psylink.
    /// </summary>
    public double? Psyfocus { get; init; }

    public double? PsyfocusTarget { get; init; }

    public int? PsylinkLevel { get; init; }
}

public sealed record Hediff
{
    /// <summary>
    /// The HediffDef is the catalog row by def_name; its label is the row's.
    /// </summary>
    public string? DefName { get; init; }

    public string? PartDefName { get; init; }

    public int? PartIndex { get; init; }

    public double? Severity { get; init; }

    public bool? Visible { get; init; }

    public bool? Permanent { get; init; }

    public bool? LifeThreatening { get; init; }

    public bool? TendableNow { get; init; }

    public bool? Tended { get; init; }

    public double? TendQuality { get; init; }

    public long? TendExpiresInTicks { get; init; }

    public long? NextTendInTicks { get; init; }

    public bool? Immunizable { get; init; }

    public double? Immunity { get; init; }

    public bool? FullyImmune { get; init; }

    /// <summary>
    /// Instantaneous native rates per 60000 game ticks; absent when unavailable.
    /// </summary>
    public double? SeverityPerDay { get; init; }

    public double? ImmunityPerDay { get; init; }
}

public sealed record Capacity
{
    public string? DefName { get; init; }

    public double? Level { get; init; }

    public Common.Unavailable? Unavailable { get; init; }
}

public sealed record SurgeryBill
{
    public string? Id { get; init; }

    public string? Recipe { get; init; }

    public int? PartIndex { get; init; }

    public bool? Suspended { get; init; }
}

public sealed record PawnHealth
{
    public double? SummaryFraction { get; init; }

    public bool? NeedsTend { get; init; }

    public bool? Bleeding { get; init; }

    public double? BleedRatePerDay { get; init; }

    public double? Pain { get; init; }

    public double? BloodLoss { get; init; }

    public double? HoursUntilDeathFromBloodLoss { get; init; }

    public bool? LifeThreatening { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Capacity>))]
    public EquatableArray<Capacity> Capacities { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Hediff>))]
    public EquatableArray<Hediff> Hediffs { get; init; }

    public Completeness? HediffCompleteness { get; init; }

    public uint? HiddenHediffs { get; init; }

    public bool? StableRestEligible { get; init; }

    public bool? ShouldSeekMedicalRest { get; init; }

    public bool? UrgentMedicalRest { get; init; }

    public string? BedId { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<SurgeryBill>))]
    public EquatableArray<SurgeryBill> SurgeryBills { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }

    public SnapshotRef? Snapshot { get; init; }

    /// <summary>
    /// Surgery facts. Native computes every value through vanilla; Go
    /// never recomputes them. Absent lists with a read issue are unknown.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<MissingBodyPart>))]
    public EquatableArray<MissingBodyPart> MissingParts { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<SurgeryOperation>))]
    public EquatableArray<SurgeryOperation> Operations { get; init; }

    /// <summary>
    /// Installed added parts: every Hediff_AddedPart on the pawn. A read
    /// failure is a read issue with field "installed_parts", never an empty list.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<InstalledPart>))]
    public EquatableArray<InstalledPart> InstalledParts { get; init; }
}

/// <summary>
/// One installed added part (bionic, prosthetic, peg, archotech). definition is
/// the hediff def; the part item it leaves on removal is that def's spawnThingOnRemoved row.
/// </summary>
public sealed record InstalledPart
{
    public DefinitionRef? Definition { get; init; }

    public string? PartDefName { get; init; }

    public int? PartIndex { get; init; }
}

/// <summary>
/// A missing or destroyed part at its common missing ancestor; descendants are implied.
/// </summary>
public sealed record MissingBodyPart
{
    public int? PartIndex { get; init; }

    public string? PartDefName { get; init; }

    public int? ParentIndex { get; init; }

    public string? ParentDefName { get; init; }

    public bool? Vital { get; init; }
}

/// <summary>
/// One available operation on one part. success_chance is vanilla's
/// SurgeryOutcomeEffectDef quality with the best eligible doctor, the best
/// colony medical bed (or the current bed) and the best permitted medicine on
/// the map; absent when no doctor is eligible or the recipe has no outcome effect.
/// </summary>
public sealed record SurgeryOperation
{
    public DefinitionRef? Recipe { get; init; }

    public int? PartIndex { get; init; }

    public string? PartDefName { get; init; }

    public SurgeryKind Kind { get; init; }

    public double? SuccessChance { get; init; }

    public uint? EligibleDoctors { get; init; }

    public bool? IngredientsOnMap { get; init; }

    public bool? Violation { get; init; }

    public bool? Lethal { get; init; }

    /// <summary>
    /// A harvest's yield, or an added part's on removal:
    /// BaseMarketValue of the spawnThingOnRemoved; absent otherwise.
    /// </summary>
    public double? YieldMarketValue { get; init; }

    /// <summary>
    /// Artificial part removal: the removed Hediff_AddedPart's def and
    /// the thing it spawns; absent unless the target part carries an added part.
    /// </summary>
    public string? AddedPartHediff { get; init; }

    public string? YieldThingDef { get; init; }

    /// <summary>
    /// BaseMarketValue x count of the medicine the operation would use (the
    /// best permitted on the map); absent when it uses none or none is stocked.
    /// </summary>
    public double? MedicineMarketValue { get; init; }

    /// <summary>
    /// The best eligible doctor's chance with an ideal bed and room:
    /// success_chance with the bed factor replaced by a clean, roofed Bed's;
    /// absent exactly when success_chance is.
    /// </summary>
    public double? DoctorSuccessChance { get; init; }

    /// <summary>
    /// True when medicine the recipe accepts is on the map but the patient's
    /// medical care level forbids all of it; ingredients_on_map is
    /// then false.
    /// </summary>
    public bool? MedicineCareLimited { get; init; }

    /// <summary>
    /// Each eligible doctor's chance on this operation, the same score as
    /// success_chance, keyed by pawn id; empty when success_chance is
    /// absent.
    /// </summary>
    public IReadOnlyDictionary<string, double> DoctorChances { get; init; } = new Dictionary<string, double>();
}

public sealed record GearItem
{
    public Common.Ref? Thing { get; init; }

    public string? Stuff { get; init; }

    public Defs.QualityCategory? Quality { get; init; }

    public int? HitPoints { get; init; }

    public int? MaxHitPoints { get; init; }

    public double? ConditionFraction { get; init; }

    public bool? Forced { get; init; }

    public bool? Locked { get; init; }

    public double? ArmorSharp { get; init; }

    public double? ArmorBlunt { get; init; }

    public double? InsulationCold { get; init; }

    public double? InsulationHeat { get; init; }

    /// <summary>
    /// Native pawn ID, only when the coded owner still exists.
    /// </summary>
    public string? BiocodedTo { get; init; }

    /// <summary>
    /// True also when the coded owner reference was lost; absent on older producers.
    /// </summary>
    public bool? Biocoded { get; init; }

    /// <summary>
    /// A loose candidate's supply CAS token.
    /// </summary>
    public SnapshotRef? ThingSnapshot { get; init; }
}

public sealed record PawnEquipment
{
    public string? PrimaryId { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<GearItem>))]
    public EquatableArray<GearItem> Equipped { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<GearItem>))]
    public EquatableArray<GearItem> Apparel { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<GearItem>))]
    public EquatableArray<GearItem> InventoryWeapons { get; init; }

    public string? CarriedThingId { get; init; }

    public uint? InventoryItemCount { get; init; }

    public bool? Armed { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }

    /// <summary>
    /// Defense capacity: the primary ranged verb's damage per second
    /// (projectile damage x burst over aim-adjusted warmup, cooldown and burst
    /// gaps; 0 without a ranged primary) and the pawn's MeleeDPS stat.
    /// </summary>
    public double? RangedDps { get; init; }

    public double? MeleeDps { get; init; }
}

/// <summary>
/// The SkillDef is the catalog row by def_name.
/// </summary>
public sealed record Skill
{
    public string? DefName { get; init; }

    public int? Level { get; init; }

    public double? StoredLevel { get; init; }

    public Passion? Passion { get; init; }

    public bool? Disabled { get; init; }
}

public sealed record Trait
{
    public string? DefName { get; init; }

    public int? Degree { get; init; }
}

public sealed record WorkSetting
{
    public string? DefName { get; init; }

    public int? Priority { get; init; }

    public bool? Disabled { get; init; }
}

public sealed record TimetableSlot
{
    public uint? Hour { get; init; }

    public string? AssignmentDefName { get; init; }
}

public sealed record PawnBiography
{
    public double? BiologicalAgeYears { get; init; }

    public double? ChronologicalAgeYears { get; init; }

    public string? ChildhoodDefName { get; init; }

    public string? AdulthoodDefName { get; init; }

    public string? Title { get; init; }

    public string? TitleSource { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Skill>))]
    public EquatableArray<Skill> Skills { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Trait>))]
    public EquatableArray<Trait> Traits { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> IncapableWorkTypes { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> IncapableSources { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> DisabledWorkTags { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }
}

public sealed record Thought
{
    public string? DefName { get; init; }

    public uint? Count { get; init; }

    public double? MoodOffsetEach { get; init; }

    public double? MoodOffsetTotal { get; init; }
}

public sealed record Relation
{
    public Common.Ref? Other { get; init; }

    public string? RelationDefName { get; init; }

    public int? Opinion { get; init; }

    public bool? OpinionReconstructed { get; init; }
}

public sealed record PawnSocial
{
    public bool? HighExpectations { get; init; }

    public string? ExpectationDefName { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Thought>))]
    public EquatableArray<Thought> Memories { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Thought>))]
    public EquatableArray<Thought> Situational { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Relation>))]
    public EquatableArray<Relation> Relations { get; init; }

    public bool? SituationalCacheStale { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }
}

public sealed record PawnSettings
{
    public Operations.MedicalCare? MedicalCare { get; init; }

    public bool? SelfTend { get; init; }

    public Operations.HostilityResponse? HostilityResponse { get; init; }

    public string? AllowedAreaId { get; init; }

    public string? MasterId { get; init; }

    public bool? FollowDrafted { get; init; }

    public bool? FollowFieldwork { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<WorkSetting>))]
    public EquatableArray<WorkSetting> Work { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<TimetableSlot>))]
    public EquatableArray<TimetableSlot> Schedule { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<DefinitionRef>))]
    public EquatableArray<DefinitionRef> AllowedAreas { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }

    public bool? WorkApplies { get; init; }

    public bool? ManualWorkPriorities { get; init; }

    public FoodRestriction? FoodRestriction { get; init; }

    /// <summary>
    /// Per-pawn policy planner inputs, read with work detail.
    /// </summary>
    public PawnPolicyInputs? PolicyInputs { get; init; }

    /// <summary>
    /// The pawn's effective eat trigger (food level 0..1 under which it seeks food;
    /// 0.30 vanilla when no override), read with work detail. Absent when the
    /// pawn has none (not a humanlike colony pawn with a food need).
    /// </summary>
    public float? EatTrigger { get; init; }

    /// <summary>
    /// Body stats for outdoor dash planning and nearby hazard planners, read
    /// with work detail.
    /// </summary>
    public PawnBodyStats? Body { get; init; }
}

/// <summary>
/// The pawn's body stats the outdoor dash planner and nearby hazard planners
/// read: native values at the read tick (the game's own stat and hediff
/// evaluation, never derived in Go). A scalar is absent when native could not
/// read it (unknown, not zero). comfortable_min_c and comfortable_max_c are
/// StatDefOf.ComfyTemperatureMin/Max (degrees C, apparel included);
/// move_speed is StatDefOf.MoveSpeed in cells per second, with the current
/// Moving capacity; carry_capacity is MassCarryCapacity in kg;
/// toxic_environment_resistance is StatDefOf.ToxicEnvironmentResistance (0-1).
/// cell_temperature_c is the temperature at the pawn's cell. hypothermia,
/// heatstroke, frostbite (highest part) and toxic_buildup are hediff
/// severities, 0 when the pawn has none. moving, manipulation and
/// consciousness are PawnCapacityDefOf levels (1 = full).
/// </summary>
public sealed record PawnBodyStats
{
    public double? ComfortableMinC { get; init; }

    public double? ComfortableMaxC { get; init; }

    public double? MoveSpeed { get; init; }

    public double? CarryCapacity { get; init; }

    public double? ToxicEnvironmentResistance { get; init; }

    public double? CellTemperatureC { get; init; }

    public double? Hypothermia { get; init; }

    public double? Heatstroke { get; init; }

    public double? Frostbite { get; init; }

    public double? ToxicBuildup { get; init; }

    public double? Moving { get; init; }

    public double? Manipulation { get; init; }

    public double? Consciousness { get; init; }
}

/// <summary>
/// The inputs per-pawn outfit/drug/food/reading planners compose from.
/// Traits, mood and break thresholds (biography, needs) and animal
/// bonds (social relations, relation_def_name "Bond") are observed elsewhere.
/// Policy ids are Policy.GetUniqueLoadID(); absent when the pawn has no
/// tracker for that policy kind. A field the pawn has no source for (no
/// Royalty title, no ideoligion, not a guest) is absent or empty, never an
/// issue.
/// </summary>
public sealed record PawnPolicyInputs
{
    public string? OutfitPolicyId { get; init; }

    public string? DrugPolicyId { get; init; }

    public string? ReadingPolicyId { get; init; }

    /// <summary>
    /// Pawn_InventoryStockTracker entries (medicine carry): group def, chosen
    /// thing def and desired count.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<InventoryStockSetting>))]
    public EquatableArray<InventoryStockSetting> InventoryStock { get; init; }

    /// <summary>
    /// One row per ChemicalDef with an addiction or tolerance hediff.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<ChemicalState>))]
    public EquatableArray<ChemicalState> Chemicals { get; init; }

    /// <summary>
    /// ChemicalDef defNames of the pawn's Gene_ChemicalDependency genes.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> DependencyChemicals { get; init; }

    /// <summary>
    /// Most senior royal title's defName (its requiredApparel is the RoyalTitleDef row).
    /// </summary>
    public string? RoyalTitle { get; init; }

    /// <summary>
    /// Ideo.GetUniqueLoadID(), the ideo's precept defNames (diet, apparel and
    /// every other issue), the pawn's Precept_Role defName with its apparel
    /// requirements, and the apparelDef of each Precept_Apparel.
    /// </summary>
    public string? IdeoId { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Precepts { get; init; }

    public string? IdeoRole { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ApparelRequirementFact>))]
    public EquatableArray<ApparelRequirementFact> RoleApparel { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> PreceptApparel { get; init; }

    /// <summary>
    /// GuestStatus name (Guest, Prisoner, Slave) and the prisoner/slave
    /// interaction mode defNames; absent for a pawn that is no guest.
    /// </summary>
    public string? GuestStatus { get; init; }

    public string? PrisonerInteraction { get; init; }

    public string? SlaveInteraction { get; init; }

    /// <summary>
    /// StatDefOf.MedicalTendQuality of the pawn as a doctor, before vanilla's
    /// 0.7 self-tend factor; absent for a pawn that cannot doctor.
    /// </summary>
    public double? MedicalTendQuality { get; init; }

    /// <summary>
    /// Pawn_IdeoTracker.Certainty (0..1) in the pawn's ideoligion;
    /// absent without an ideoligion.
    /// </summary>
    public double? IdeoCertainty { get; init; }
}

public sealed record InventoryStockSetting
{
    public string? Group { get; init; }

    public string? ThingDef { get; init; }

    public int? Count { get; init; }
}

/// <summary>
/// addiction: Hediff_Addiction severity, absent without one; withdrawal: the
/// addiction is in its withdrawal stage; tolerance: the tolerance hediff's
/// severity, absent without one.
/// </summary>
public sealed record ChemicalState
{
    public string? Chemical { get; init; }

    public double? Addiction { get; init; }

    public bool? Withdrawal { get; init; }

    public double? Tolerance { get; init; }
}

/// <summary>
/// A Precept_Role's apparel requirement: ideoligion instance state, not a def row.
/// </summary>
public sealed record ApparelRequirementFact
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> BodyPartGroups { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> RequiredDefs { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> RequiredTags { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> AllowedTags { get; init; }
}

public sealed record TrainingEntry
{
    public string? DefName { get; init; }

    public bool? Learned { get; init; }

    public bool? Wanted { get; init; }

    public bool? Available { get; init; }

    public string? Reason { get; init; }
}

public sealed record AnimalState
{
    public string? Gender { get; init; }

    public double? AgeYears { get; init; }

    public bool? FertileAdult { get; init; }

    public bool? Pregnant { get; init; }

    public double? Gestation { get; init; }

    public double? MilkFullness { get; init; }

    public double? WoolFullness { get; init; }

    public string? PenId { get; init; }

    public bool? Contained { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> ParentIds { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<TrainingEntry>))]
    public EquatableArray<TrainingEntry> Training { get; init; }

    public bool? Release { get; init; }

    public bool? Slaughter { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }

    public bool? Tameable { get; init; }

    public bool? Tame { get; init; }

    public bool? SafeToRelease { get; init; }

    /// <summary>
    /// Player-animal settings the SetAnimalArea/Master/Following writes target:
    /// the current allowed area and master (empty when unassigned), the follow
    /// flags, whether Obedience is learned (master/follow eligibility) and
    /// whether native lets this animal carry an allowed area at all.
    /// </summary>
    public string? AllowedAreaId { get; init; }

    public string? MasterId { get; init; }

    public bool? FollowDrafted { get; init; }

    public bool? FollowFieldwork { get; init; }

    public bool? Obedient { get; init; }

    public bool? SupportsAllowedAreas { get; init; }

    /// <summary>
    /// Herd sizing facts: any hediff that makes colonists feel sick, the
    /// player ideo venerates this race (slaughter_barred is no longer written;
    /// precept effects decide), and adult age stage. The race's own
    /// numbers (body size, life expectancy, minimum handling skill, tame-failure
    /// manhunter chance) are the catalog's race rows.
    /// </summary>
    public bool? Sick { get; init; }

    public bool? SlaughterBarred { get; init; }

    public bool? Venerated { get; init; }

    public bool? Adult { get; init; }

    /// <summary>
    /// Medical care cap inputs, for a player animal: its
    /// MedicalCareCategory name, a Bond relation to any living pawn, and the
    /// same conditions block PopulationPerson.conditions carries.
    /// </summary>
    public Operations.MedicalCare? MedicalCare { get; init; }

    public bool? Bonded { get; init; }

    public PawnHealth? Conditions { get; init; }

    /// <summary>
    /// Sterilized hediff present, and a queued sterilize surgery bill on the
    /// animal (the husbandry sterilize order).
    /// </summary>
    public bool? Sterilized { get; init; }

    public bool? SterilizeQueued { get; init; }

    /// <summary>
    /// Raw slaughter facts of a player animal; policy decides who is protected
    /// (FoodSlaughterChoice). downed and in_mental_state are the live Pawn
    /// flags, colonist_bonded is TrainableUtility.GetAllColonistBondsFor
    /// non-empty, slaughter_designatable is Designator_Slaughter's own
    /// acceptance (false while a slaughter designation stands).
    /// </summary>
    public bool? Downed { get; init; }

    public bool? InMentalState { get; init; }

    public bool? ColonistBonded { get; init; }

    public bool? SlaughterDesignatable { get; init; }

    /// <summary>
    /// Herd growth events: game ticks until a pregnant animal gives birth
    /// ((1 - gestation) * gestationPeriodDays * 60000 / BodyResourceGrowthSpeed),
    /// absent without a pregnancy; the index of the current life stage in the
    /// race's lifeStageAges (Pawn_AgeTracker.CurLifeStageIndex) and game ticks until
    /// the next stage begins ((next minAge in ticks - AgeBiologicalTicks) /
    /// BiologicalTicksPerTick), absent in the last stage. The litter size is rolled
    /// at birth and the stage rows are the catalog's, so neither is sent.
    /// </summary>
    public long? TicksToBirth { get; init; }

    public int? LifeStageIndex { get; init; }

    public long? TicksToNextLifeStage { get; init; }

    /// <summary>
    /// Ids of the living humanlike pawns this animal has a Bond relation with
    /// (any faction or status; the planner keeps those on the colony roster).
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> BondedPawnIds { get; init; }
}

public sealed record PawnState
{
    public EntityRef? Pawn { get; init; }

    public string? KindDefName { get; init; }

    public Common.Ref? Faction { get; init; }

    public bool? Colonist { get; init; }

    public bool? FreeColonist { get; init; }

    public bool? Prisoner { get; init; }

    public bool? Humanlike { get; init; }

    public bool? Animal { get; init; }

    public bool? Mechanoid { get; init; }

    public bool? Tame { get; init; }

    public bool? Wild { get; init; }

    public bool? Dead { get; init; }

    public bool? Downed { get; init; }

    public bool? Drafted { get; init; }

    public bool? Hostile { get; init; }

    public string? HostileReason { get; init; }

    public string? MentalState { get; init; }

    public bool? InBed { get; init; }

    public bool? Burning { get; init; }

    public bool? PlayerControlled { get; init; }

    public JobEvidence? Job { get; init; }

    public PawnNeeds? Needs { get; init; }

    public PawnHealth? Health { get; init; }

    public PawnEquipment? Equipment { get; init; }

    public PawnBiography? Biography { get; init; }

    public PawnSettings? Settings { get; init; }

    public PawnSocial? Social { get; init; }

    public AnimalState? AnimalState { get; init; }

    public Common.Ref? NearestColonist { get; init; }

    public double? NearestColonistDistance { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }

    /// <summary>
    /// Native draft claims were removed: drafts are plan-owned.
    /// </summary>
    public Common.Ref? OwnedBed { get; init; }

    public SnapshotRef? Snapshot { get; init; }

    /// <summary>
    /// Native lord evidence for hostile groups: the LordJob and current LordToil
    /// class names (e.g. LordJob_AssaultColony / LordToil_AssaultColonySappers,
    /// LordJob_Siege). Absent when the pawn has no lord; never inferred.
    /// </summary>
    public string? LordJobClass { get; init; }

    public string? LordToilClass { get; init; }

    /// <summary>
    /// Active MentalStateDef.IsAggro and MentalState.Age (native ticks, 30-tick granularity).
    /// Absent without an active state or on older producers; mental_state is its defName.
    /// </summary>
    public bool? MentalStateIsAggro { get; init; }

    public int? MentalStateTicks { get; init; }

    /// <summary>
    /// Mean of each active spawned hostile's highest natural/worn sharp armor rating.
    /// Equipment detail only; absent when there are no live, standing hostiles.
    /// </summary>
    public double? RaidArmor { get; init; }

    /// <summary>
    /// Doctor-side tend gates, mirroring NativeTendOperations.Prepare so the
    /// controller never proposes a pair the native gate refuses. Present
    /// only when PawnDetails.tend is requested.
    /// </summary>
    public PawnTendDoctor? TendDoctor { get; init; }

    /// <summary>
    /// Verse.IntVec3.Fogged: the pawn stands in fog the colony has not
    /// discovered. A fogged hostile is undiscovered, unreachable and no
    /// emergency -- the ancient-danger mechanoid behind a sealed wall held
    /// every window and deselected every development goal for good.
    /// </summary>
    public bool? Fogged { get; init; }

    /// <summary>
    /// Current InspirationDef defName: empty when the pawn has no
    /// inspiration; absent from producers that do not read it (unknown).
    /// </summary>
    public string? Inspiration { get; init; }

    /// <summary>
    /// The pawn control CAS token, on reads that admit a pawn order.
    /// </summary>
    public SnapshotRef? PawnSnapshot { get; init; }

    /// <summary>
    /// Biotech pawn facts; absent without Biotech, so absent is not
    /// applicable, never a gene-less pawn.
    /// </summary>
    public PawnBiotech? Biotech { get; init; }

    /// <summary>
    /// Anomaly facts; absent without Anomaly.
    /// </summary>
    public PawnAnomaly? Anomaly { get; init; }

    /// <summary>
    /// Royalty facts of a free colonist; absent without Royalty or on a pawn with no royalty tracker. A failed read is a ReadIssue named "royalty".
    /// </summary>
    public PawnRoyalty? Royalty { get; init; }

    /// <summary>
    /// Standing of a spawned humanlike pawn among the factions, colonist
    /// or not; absent for animals and mechanoids.
    /// </summary>
    public PawnStanding? Standing { get; init; }

    /// <summary>
    /// Alive, standing colony builder with native finishing work-type and required
    /// capacities. Jobs, draft, schedule and skill level do not change this fact.
    /// </summary>
    public bool? ConstructionCapable { get; init; }
}

/// <summary>
/// Faction standing of one humanlike pawn. faction_def_name is the
/// pawn's FactionDef (absent without a faction); royal_title the defName of its
/// most senior royal title (absent without Royalty or a title); host_faction
/// the faction holding it as guest, prisoner or slave (absent when none);
/// guest_status the GuestStatus name while a host faction holds it;
/// quest_lodger Pawn.IsQuestLodger().
/// </summary>
public sealed record PawnStanding
{
    public string? FactionDefName { get; init; }

    public string? RoyalTitle { get; init; }

    public Common.Ref? HostFaction { get; init; }

    public string? GuestStatus { get; init; }

    public bool? QuestLodger { get; init; }
}

/// <summary>
/// Biotech facts of one pawn. Each scalar is absent when native could
/// not read it (unknown, not zero); a failed sub-read adds a ReadIssue named
/// for its field (genes, mechanitor, mech, deathrest). Defs are the
/// DefinitionCatalog.biotech rows' names.
/// life_stage is the pawn's LifeStageDef (Pawn_AgeTracker.CurLifeStage) and
/// developmental_stage the DevelopmentalStage name (Newborn, Baby, Child,
/// Adult; None for a race with no stages). learning is Need_Learning's level
/// (0-1) with its LearningCategory name; absent for a pawn with no learning
/// need. genes: every gene of Pawn_GeneTracker (endogenes and xenogenes).
/// xenotype is the XenotypeDef name, absent for a custom xenotype or none;
/// xenotype_name is the pawn's xenotype label (named custom xenotypes
/// included) and hybrid the Pawn_GeneTracker.hybrid flag. mechanitor is set
/// for a pawn with a Pawn_MechanitorTracker; mech for a mechanoid the player
/// can ever control. deathrest is set for a pawn with a deathrest gene.
/// </summary>
public sealed record PawnGene
{
    public string? DefName { get; init; }

    public bool? Xenogene { get; init; }

    public bool? Active { get; init; }
}

public sealed record PawnMechanitor
{
    public int? UsedBandwidth { get; init; }

    public int? TotalBandwidth { get; init; }

    public int? UsedBandwidthFromGestation { get; init; }

    public int? ControlGroups { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Ref>))]
    public EquatableArray<Common.Ref> ControlledMechs { get; init; }
}

/// <summary>
/// energy is the mech's Need_MechEnergy level (0-1); recharge_below and
/// recharge_above are its control group's mechRechargeThresholds (0-1), the
/// band the game itself recharges within; each is absent when not read.
/// </summary>
public sealed record PawnMech
{
    public Common.Ref? Overseer { get; init; }

    public string? WorkMode { get; init; }

    public int? ControlGroup { get; init; }

    public double? Energy { get; init; }

    public double? RechargeBelow { get; init; }

    public double? RechargeAbove { get; init; }
}

public sealed record PawnDeathrest
{
    public bool? Deathresting { get; init; }

    public double? Level { get; init; }

    public int? LastDeathrestTick { get; init; }

    public double? DeathrestPercent { get; init; }

    public int? Capacity { get; init; }

    public int? BoundBuildings { get; init; }

    public bool? AutoWake { get; init; }
}

public sealed record PawnBiotech
{
    public string? LifeStage { get; init; }

    public string? DevelopmentalStage { get; init; }

    public double? Learning { get; init; }

    public string? LearningCategory { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnGene>))]
    public EquatableArray<PawnGene> Genes { get; init; }

    public string? Xenotype { get; init; }

    public string? XenotypeName { get; init; }

    public bool? Hybrid { get; init; }

    public PawnMechanitor? Mechanitor { get; init; }

    public PawnMech? Mech { get; init; }

    public PawnDeathrest? Deathrest { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }

    /// <summary>
    /// Gene lifecycle. xenogerm_regrow_ticks_left and xenogerm_coma_ticks_left are
    /// HediffComp_Disappears.ticksToDisappear of XenogermReplicating and
    /// XenogerminationComa, 0 with no such hediff; in_extractor is a pawn held by a
    /// Building_GeneExtractor (all three failed together: ReadIssue gene_lifecycle).
    /// extractable is Building_GeneExtractor.CanAcceptPawn(pawn).Accepted on a spawned
    /// humanlike pawn of a map with an owned extractor (true if any accepts) and
    /// extractable_reason the game's refusal text, absent when no extractor gave one;
    /// the whole verdict is absent with no extractor, an unspawned pawn or a ReadIssue
    /// named extractable.
    /// </summary>
    public int? XenogermRegrowTicksLeft { get; init; }

    public int? XenogermComaTicksLeft { get; init; }

    public bool? InExtractor { get; init; }

    public bool? Extractable { get; init; }

    public string? ExtractableReason { get; init; }
}

public sealed record PawnTendDoctor
{
    /// <summary>
    /// NativePawnControlState eligibility: a drafter exists, the pawn is spawned,
    /// alive, not downed, not in a mental state and player-controlled. This is
    /// the gate whose native refusal reads "Tend requires an eligible doctor".
    /// </summary>
    public bool? ControlEligible { get; init; }

    public bool? Spawned { get; init; }

    public bool? HasDrafter { get; init; }

    /// <summary>
    /// WorkGiver_Tend.MissingRequiredCapacity(pawn) == null; missing_capacity
    /// names the failing PawnCapacityDef when it does not.
    /// </summary>
    public bool? CapacitiesOk { get; init; }

    public string? MissingCapacity { get; init; }

    /// <summary>
    /// WorkTypeIsDisabled(Doctor): the same gate the work settings report, read
    /// here from the pawn so a doctor row carries it without PawnDetails.work.
    /// </summary>
    public bool? WorkTypeDisabled { get; init; }

    /// <summary>
    /// Pawn IDs from this same reply this pawn can reach
    /// (CanReach ClosestTouch, Danger.Deadly); self is excluded. The list is
    /// exhaustive over the reply's other rows unless reach_skipped is set.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> ReachablePawnIds { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }

    /// <summary>
    /// PawnDetails.tend_reach_budget_ms ran out before this pawn's reachability
    /// was read: reachable_pawn_ids is empty and unknown, not "reaches nothing".
    /// </summary>
    public bool? ReachSkipped { get; init; }
}

public sealed record PawnFilter
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Ids { get; init; }

    public string? NameContains { get; init; }

    public bool? Colonist { get; init; }

    public bool? Prisoner { get; init; }

    public bool? Animal { get; init; }

    public bool? Humanlike { get; init; }

    public bool? Mechanoid { get; init; }

    public bool? Tame { get; init; }

    public bool? Wild { get; init; }

    public bool? Hostile { get; init; }

    public bool? Downed { get; init; }

    public bool? Drafted { get; init; }

    public bool? IncludeDead { get; init; }

    public double? WithinColonistDistance { get; init; }
}

public sealed record PawnDetails
{
    public bool? Needs { get; init; }

    public bool? Health { get; init; }

    public bool? Equipment { get; init; }

    public bool? Biography { get; init; }

    public bool? Settings { get; init; }

    public bool? Social { get; init; }

    public bool? Animals { get; init; }

    public bool? VisibleHediffsOnly { get; init; }

    public bool? Work { get; init; }

    /// <summary>
    /// schedule projects PawnSettings.schedule (the 24 TimetableSlot rows) on its own, independent of settings (care policy) and work (priorities).
    /// </summary>
    public bool? Schedule { get; init; }

    /// <summary>
    /// tend projects PawnState.tend_doctor: the native tend gates plus pairwise
    /// reachability across the rows this query returns. Opt-in; reachability is
    /// quadratic in the page (about 0.3 us a pair), so it runs under
    /// tend_reach_budget_ms: doctors past it report PawnTendDoctor.reach_skipped.
    /// </summary>
    public bool? Tend { get; init; }

    /// <summary>
    /// Main-thread milliseconds the tend pair reachability may spend. Absent: read every pair.
    /// </summary>
    public uint? TendReachBudgetMs { get; init; }
}

public sealed record PawnSnapshot
{
    public Common.ObservationContext? Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnState>))]
    public EquatableArray<PawnState> Pawns { get; init; }

    public Completeness? Completeness { get; init; }

    /// <summary>
    /// Colony fact: the Meditate TimeAssignmentDef exists
    /// (DefDatabase&lt;TimeAssignmentDef&gt;.GetNamedSilentFail("Meditate")); false on Core only.
    /// </summary>
    public bool? MeditateAssignmentAvailable { get; init; }

    /// <summary>
    /// Row ids removed since the base table, in a delta section (SectionWatermark.delta).
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Removed { get; init; }
}

public sealed record ListPawnsRequest
{
    public ReadScope? Scope { get; init; }

    public PawnFilter? Filter { get; init; }

    public PawnDetails? Details { get; init; }
}

public sealed record ListPawnsReply
{
    public ListPawnsReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="ListPawnsReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ListPawnsReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(ListPawnsReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(ListPawnsReplyOutcome.FailureCase), "failure")]
public abstract record ListPawnsReplyOutcome
{
    private protected ListPawnsReplyOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public PawnSnapshot? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(PawnSnapshot Value) : ListPawnsReplyOutcome;

    public static implicit operator ListPawnsReplyOutcome(PawnSnapshot value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : ListPawnsReplyOutcome;

    public static implicit operator ListPawnsReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : ListPawnsReplyOutcome;

    public static implicit operator ListPawnsReplyOutcome(Common.Failure value) => new FailureCase(value);
}

/// <summary>
/// One stack a stock row counts: the thing, the cell its units are
/// at (its holder's when held), its supply CAS token when it is allowable,
/// and for a packed (minified) item the building inside it, for a
/// RelocateIntent install, with that building's quality (QualityCategory
/// ordinal, 0 Awful .. 6 Legendary; absent without one) and the packed
/// item's market value.
/// </summary>
public sealed record StockItem
{
    public Common.Ref? Item { get; init; }

    public Common.Cell? Cell { get; init; }

    public SnapshotRef? Snapshot { get; init; }

    public Common.Ref? Inner { get; init; }

    public string? InnerDefName { get; init; }

    public int? Quality { get; init; }

    public double? MarketValue { get; init; }
}

public sealed record HeldStock
{
    public Common.Ref? Holder { get; init; }

    public HolderKind? HolderKind { get; init; }

    public long? Units { get; init; }
}

public sealed record CorpseState
{
    public Common.Ref? Corpse { get; init; }

    public Common.Ref? InnerPawn { get; init; }

    public string? Race { get; init; }

    public bool? Humanlike { get; init; }

    public bool? WasColonist { get; init; }

    public Common.RotStage? RotStage { get; init; }
}

public sealed record ResourceStock
{
    public DefinitionRef? Definition { get; init; }

    public long? Units { get; init; }

    public long? Stacks { get; init; }

    public long? Spawned { get; init; }

    public long? Carried { get; init; }

    public long? InContainer { get; init; }

    public long? Ours { get; init; }

    public long? OursUnforbidden { get; init; }

    public long? Forbidden { get; init; }

    public long? PlayerFaction { get; init; }

    public long? OtherFaction { get; init; }

    public long? TraderStock { get; init; }

    public long? Fogged { get; init; }

    public long? Reserved { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<StockItem>))]
    public EquatableArray<StockItem> Items { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<HeldStock>))]
    public EquatableArray<HeldStock> Holders { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<CorpseState>))]
    public EquatableArray<CorpseState> Corpses { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }

    public SnapshotRef? Snapshot { get; init; }

    /// <summary>
    /// Biocode facts keyed by items' exact identities.
    /// Only weapon rows carry these; older producers omit them.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<GearItem>))]
    public EquatableArray<GearItem> WeaponItems { get; init; }
}

public sealed record StockFilter
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> DefNames { get; init; }

    public StockCategory? Category { get; init; }

    public StockOwnership? Ownership { get; init; }

    public bool? IncludeHeld { get; init; }

    public bool? ForbiddenOnly { get; init; }

    public bool? ExcludeChunks { get; init; }

    public bool? Corpses { get; init; }

    public Rectangle? Region { get; init; }
}

public sealed record SuppliesSnapshot
{
    public Common.ObservationContext? Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ResourceStock>))]
    public EquatableArray<ResourceStock> Stocks { get; init; }

    public Completeness? Completeness { get; init; }
}

public sealed record ListSuppliesRequest
{
    public ReadScope? Scope { get; init; }

    public StockFilter? Filter { get; init; }
}

public sealed record ListSuppliesReply
{
    public ListSuppliesReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="ListSuppliesReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ListSuppliesReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(ListSuppliesReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(ListSuppliesReplyOutcome.FailureCase), "failure")]
public abstract record ListSuppliesReplyOutcome
{
    private protected ListSuppliesReplyOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public SuppliesSnapshot? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(SuppliesSnapshot Value) : ListSuppliesReplyOutcome;

    public static implicit operator ListSuppliesReplyOutcome(SuppliesSnapshot value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : ListSuppliesReplyOutcome;

    public static implicit operator ListSuppliesReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : ListSuppliesReplyOutcome;

    public static implicit operator ListSuppliesReplyOutcome(Common.Failure value) => new FailureCase(value);
}

/// <summary>
/// alternatives: required units per allowed definition when the count depends on the material chosen.
/// </summary>
public sealed record IngredientRequirement
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> AllowedDefNames { get; init; }

    public double? Required { get; init; }

    public double? Available { get; init; }

    public double? Missing { get; init; }

    public bool? Complete { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Quantity>))]
    public EquatableArray<Quantity> Alternatives { get; init; }
}

public sealed record FilterSpecialRule
{
    public string? DefName { get; init; }

    public bool? Allowed { get; init; }
}

public sealed record StockpileFilter
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> AllowedDefNames { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<FilterSpecialRule>))]
    public EquatableArray<FilterSpecialRule> SpecialRules { get; init; }

    public double? HitPointsMin { get; init; }

    public double? HitPointsMax { get; init; }

    public string? QualityMin { get; init; }

    public string? QualityMax { get; init; }

    public bool? AllowFresh { get; init; }

    public bool? AllowRotten { get; init; }
}

public sealed record BillState
{
    /// <summary>
    /// Filter matches a new Auto bill, including food routing and special filters.
    /// </summary>
    public bool? DefaultIngredients { get; init; }

    /// <summary>
    /// No skill or worker-category restriction; worker_id separately records a pinned pawn.
    /// </summary>
    public bool? UnrestrictedWorker { get; init; }

    /// <summary>
    /// Current-load controller bill with unchanged player settings.
    /// </summary>
    public bool? ManagedUnchanged { get; init; }

    public string? Id { get; init; }

    public DefinitionRef? Recipe { get; init; }

    public string? BillClass { get; init; }

    public Operations.RepeatMode? RepeatMode { get; init; }

    public int? RepeatCount { get; init; }

    public int? TargetCount { get; init; }

    public int? UnpauseBelow { get; init; }

    public bool? PauseWhenSatisfied { get; init; }

    public bool? Paused { get; init; }

    public bool? Suspended { get; init; }

    public bool? Finished { get; init; }

    public Common.Ref? Worker { get; init; }

    public int? SkillMin { get; init; }

    public int? SkillMax { get; init; }

    public double? IngredientRadius { get; init; }

    public StockpileFilter? IngredientFilter { get; init; }

    public Operations.StoreMode? StoreMode { get; init; }

    public Common.Ref? StoreZone { get; init; }

    public bool? CanRunNow { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Blockers { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<IngredientRequirement>))]
    public EquatableArray<IngredientRequirement> Ingredients { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }

    /// <summary>
    /// Live bill jobs' promised ingredients: one row per spawned pawn
    /// whose current job works this bill, the spawned things it has queued or
    /// placed. Go's material budget subtracts them from free stock.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<IngredientReservation>))]
    public EquatableArray<IngredientReservation> Reservations { get; init; }
}

public sealed record IngredientReservation
{
    public string? PawnId { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Quantity>))]
    public EquatableArray<Quantity> Items { get; init; }
}

/// <summary>
/// work_speed is the bench's WorkTableWorkSpeedFactor stat as the game computes it now (stat parts included); absent when the bench has no such stat.
/// </summary>
public sealed record BillStackRow
{
    public SnapshotRef? Snapshot { get; init; }

    public Common.Ref? Bench { get; init; }

    public bool? Usable { get; init; }

    public BenchUnusableReason? UnusableReason { get; init; }

    public uint? Capacity { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<BillState>))]
    public EquatableArray<BillState> Bills { get; init; }

    public double? WorkSpeed { get; init; }
}

/// <summary>
/// What a recipe is, costs and needs is its RecipeDef row in the definition catalog; a frame carries only whether the bench offers it now.
/// </summary>
public sealed record RecipeState
{
    public DefinitionRef? Recipe { get; init; }

    public bool? AvailableNow { get; init; }

    public bool? AvailableOnBench { get; init; }
}

public sealed record BuildingSettings
{
    public SnapshotRef? Snapshot { get; init; }

    public bool? Forbidden { get; init; }

    public bool? DesiredSwitchOn { get; init; }

    public bool? SwitchOn { get; init; }

    public bool? FlickDesignated { get; init; }

    public bool? Connected { get; init; }

    public bool? Powered { get; init; }

    public double? TargetTemperatureC { get; init; }

    public bool? Medical { get; init; }

    public bool? ForPrisoners { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Ref>))]
    public EquatableArray<Common.Ref> AssignedPawns { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Ref>))]
    public EquatableArray<Common.Ref> AssigningCandidates { get; init; }

    public bool? RoomCanBePrisonCell { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }

    /// <summary>
    /// crop_def_name is a plant grower's current crop beside its own CAS snapshot (BuildingPatchIntent.plant_def).
    /// </summary>
    public string? CropDefName { get; init; }

    /// <summary>
    /// player_owned is a claimable building's faction reading beside its own CAS snapshot (BuildingPatchIntent.claim).
    /// </summary>
    public bool? PlayerOwned { get; init; }
}

public sealed record MaterialDeficit
{
    public string? DefName { get; init; }

    public long? Need { get; init; }

    public long? Have { get; init; }

    public long? StillNeeded { get; init; }
}

public sealed record ConstructionState
{
    public double? WorkLeft { get; init; }

    public double? PercentComplete { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<MaterialDeficit>))]
    public EquatableArray<MaterialDeficit> Resources { get; init; }

    public bool? ResourcesComplete { get; init; }

    public bool? CompletableEver { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }

    public int? MinimumFinishingSkill { get; init; }

    public bool? QualitySensitive { get; init; }

    public int? EligibleFinishers { get; init; }

    public string? FinishingBlocker { get; init; }

    public int? NativeFinishingSkill { get; init; }

    /// <summary>
    /// Native per-target construction tier (#2522); absent = ungated.
    /// </summary>
    public int? Tier { get; init; }
}

public sealed record ThermalSide
{
    public string? Side { get; init; }

    public Common.Cell? Position { get; init; }

    public Common.Ref? Room { get; init; }

    public bool? Outdoors { get; init; }

    public double? TemperatureC { get; init; }

    public bool? Fogged { get; init; }

    public bool? Impassable { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }
}

public sealed record BuildingServiceState
{
    public bool? Connected { get; init; }

    public bool? PowerOn { get; init; }

    public double? PowerOutputW { get; init; }

    public bool? SwitchedOn { get; init; }

    public double? Fuel { get; init; }

    public double? TargetFuel { get; init; }

    public bool? OutOfFuel { get; init; }

    public bool? BrokenDown { get; init; }

    public string? PowerNetId { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }
}

/// <summary>
/// BuildingState is the canonical building row, built by one native
/// builder: only the bundle's building table and the list read carry it;
/// every other message points at a building by Ref, which the
/// reader resolves against the table. service and settings.forbidden are set on
/// every row.
/// </summary>
public sealed record BuildingState
{
    public EntityRef? Building { get; init; }

    public Placement.Rotation? Rotation { get; init; }

    public string? Stuff { get; init; }

    public Common.Ref? Faction { get; init; }

    public BuildingStatus? Status { get; init; }

    public string? BuildDefName { get; init; }

    public string? InstallOfDefName { get; init; }

    public int? HitPoints { get; init; }

    public int? MaxHitPoints { get; init; }

    public ConstructionState? Construction { get; init; }

    public BuildingServiceState? Service { get; init; }

    public BuildingSettings? Settings { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ThermalSide>))]
    public EquatableArray<ThermalSide> ThermalSides { get; init; }

    public BillStackRow? Bills { get; init; }

    public string? InspectText { get; init; }

    public bool? Burning { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }

    public SnapshotRef? Snapshot { get; init; }

    /// <summary>
    /// The building's occupied rect, inclusive.
    /// </summary>
    public Rectangle? Occupied { get; init; }

    /// <summary>
    /// Odyssey hack and portal facts; absent on a building with neither.
    /// </summary>
    public OdysseyBuilding? Odyssey { get; init; }

    /// <summary>
    /// Anomaly containment and study facts; absent without Anomaly or on
    /// a building with neither.
    /// </summary>
    public AnomalyBuilding? Anomaly { get; init; }
}

public sealed record PowerNetwork
{
    public string? Id { get; init; }

    public uint? Producers { get; init; }

    public uint? Consumers { get; init; }

    public uint? Batteries { get; init; }

    public uint? Transmitters { get; init; }

    public uint? Connectors { get; init; }

    public double? GenerationW { get; init; }

    public double? ConsumptionW { get; init; }

    public double? NetW { get; init; }

    public double? StoredWattDays { get; init; }

    public double? CapacityWattDays { get; init; }

    public bool? HasSource { get; init; }

    public bool? HasActiveSource { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Flags { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> MemberIds { get; init; }
}

public sealed record BuildingsSnapshot
{
    public Common.ObservationContext? Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<BuildingState>))]
    public EquatableArray<BuildingState> Buildings { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PowerNetwork>))]
    public EquatableArray<PowerNetwork> PowerNetworks { get; init; }

    public Completeness? Completeness { get; init; }

    /// <summary>
    /// Row ids removed since the base table, in a delta section (SectionWatermark.delta).
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Removed { get; init; }
}

public sealed record ListBuildingsRequest
{
    public ReadScope? Scope { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Ids { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> DefNames { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Statuses { get; init; }

    public string? Category { get; init; }

    public bool? PlayerOnly { get; init; }

    public double? DamagedBelowFraction { get; init; }

    public bool? Inspect { get; init; }

    public bool? BillIngredients { get; init; }
}

public sealed record ListBuildingsReply
{
    public ListBuildingsReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="ListBuildingsReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ListBuildingsReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(ListBuildingsReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(ListBuildingsReplyOutcome.FailureCase), "failure")]
public abstract record ListBuildingsReplyOutcome
{
    private protected ListBuildingsReplyOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public BuildingsSnapshot? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(BuildingsSnapshot Value) : ListBuildingsReplyOutcome;

    public static implicit operator ListBuildingsReplyOutcome(BuildingsSnapshot value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : ListBuildingsReplyOutcome;

    public static implicit operator ListBuildingsReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : ListBuildingsReplyOutcome;

    public static implicit operator ListBuildingsReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record RoomStat
{
    public string? DefName { get; init; }

    public double? Value { get; init; }

    public string? Display { get; init; }

    public string? Unit { get; init; }

    public string? Cost { get; init; }

    public Common.Unavailable? Unavailable { get; init; }
}

public sealed record RoomBedMembership
{
    public Common.Ref? Building { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Ref>))]
    public EquatableArray<Common.Ref> Owners { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Ref>))]
    public EquatableArray<Common.Ref> Users { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Ref>))]
    public EquatableArray<Common.Ref> AccessibleTo { get; init; }
}

public sealed record StockpileMembership
{
    public Common.Ref? Zone { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ResourceStock>))]
    public EquatableArray<ResourceStock> Contents { get; init; }
}

public sealed record RoomState
{
    public string? Id { get; init; }

    public string? Role { get; init; }

    public string? Label { get; init; }

    public bool? ProperRoom { get; init; }

    public bool? Doorway { get; init; }

    public bool? Outdoors { get; init; }

    public bool? PsychologicallyOutdoors { get; init; }

    public bool? TouchesMapEdge { get; init; }

    public bool? Fogged { get; init; }

    public double? TemperatureC { get; init; }

    public uint? OpenRoofCount { get; init; }

    public uint? CellCount { get; init; }

    public Rectangle? Extents { get; init; }

    public Common.Cell? Center { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<RoomStat>))]
    public EquatableArray<RoomStat> Stats { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Ref>))]
    public EquatableArray<Common.Ref> Pawns { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Ref>))]
    public EquatableArray<Common.Ref> Beds { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Ref>))]
    public EquatableArray<Common.Ref> StockpileZones { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Quantity>))]
    public EquatableArray<Quantity> Contents { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }

    public SnapshotRef? Snapshot { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<RoomBedMembership>))]
    public EquatableArray<RoomBedMembership> BedMemberships { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<StockpileMembership>))]
    public EquatableArray<StockpileMembership> StockpileMemberships { get; init; }

    /// <summary>
    /// Doors in the room's boundary: door cell, the cell it opens onto, and whether that side is outdoors.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<RoomDoor>))]
    public EquatableArray<RoomDoor> Doors { get; init; }

    /// <summary>
    /// The room's key in the whole-map cell grid (CellGrid.room): the
    /// row-major map index of its first unfogged cell; its cells are the grid
    /// cells carrying it. Absent when every cell is fogged.
    /// </summary>
    public string? GridRoom { get; init; }

    public bool? Burning { get; init; }

    /// <summary>
    /// Temperature-control buildings or heat-pusher comps in this room or its boundary.
    /// </summary>
    public bool? TemperatureControl { get; init; }

    /// <summary>
    /// A contained item has CompRottable.
    /// </summary>
    public bool? PerishableContents { get; init; }
}

public sealed record RoomDoor
{
    public Common.Cell? Cell { get; init; }

    public Common.Cell? Outside { get; init; }

    public bool? Outdoors { get; init; }

    public string? Id { get; init; }

    public bool? PlayerOwned { get; init; }

    public bool? Open { get; init; }

    public bool? HoldOpen { get; init; }

    public bool? BlockedOpen { get; init; }

    public bool? Forbidden { get; init; }
}

public sealed record RoomsSnapshot
{
    public Common.ObservationContext? Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<RoomState>))]
    public EquatableArray<RoomState> Rooms { get; init; }

    public Completeness? Completeness { get; init; }
}

public sealed record ListRoomsRequest
{
    public ReadScope? Scope { get; init; }

    public Rectangle? Region { get; init; }

    public bool? IncludeOutdoors { get; init; }

    public bool? IncludeBoundary { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> RoomIds { get; init; }
}

public sealed record ListRoomsReply
{
    public ListRoomsReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="ListRoomsReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ListRoomsReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(ListRoomsReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(ListRoomsReplyOutcome.FailureCase), "failure")]
public abstract record ListRoomsReplyOutcome
{
    private protected ListRoomsReplyOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public RoomsSnapshot? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(RoomsSnapshot Value) : ListRoomsReplyOutcome;

    public static implicit operator ListRoomsReplyOutcome(RoomsSnapshot value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : ListRoomsReplyOutcome;

    public static implicit operator ListRoomsReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : ListRoomsReplyOutcome;

    public static implicit operator ListRoomsReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record ZoneState
{
    public string? Id { get; init; }

    public string? Label { get; init; }

    public string? Type { get; init; }

    public Rectangle? Bounds { get; init; }

    public string? Priority { get; init; }

    public string? CropDefName { get; init; }

    public bool? ExplicitlySetCrop { get; init; }

    public bool? AllowSow { get; init; }

    public bool? AllowCut { get; init; }

    public StockpileFilter? Filter { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Quantity>))]
    public EquatableArray<Quantity> Contents { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }

    public FarmFacts? Farm { get; init; }

    public bool? FoodStorage { get; init; }
}

public sealed record ZonesSnapshot
{
    public Common.ObservationContext? Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ZoneState>))]
    public EquatableArray<ZoneState> Zones { get; init; }

    public Completeness? Completeness { get; init; }
}

public sealed record ListZonesRequest
{
    public ReadScope? Scope { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Ids { get; init; }

    public string? NameContains { get; init; }

    public Rectangle? Region { get; init; }

    public bool? IncludeContents { get; init; }

    public bool? IncludeFilter { get; init; }
}

public sealed record ListZonesReply
{
    public ListZonesReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="ListZonesReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ListZonesReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(ListZonesReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(ListZonesReplyOutcome.FailureCase), "failure")]
public abstract record ListZonesReplyOutcome
{
    private protected ListZonesReplyOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public ZonesSnapshot? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(ZonesSnapshot Value) : ListZonesReplyOutcome;

    public static implicit operator ListZonesReplyOutcome(ZonesSnapshot value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : ListZonesReplyOutcome;

    public static implicit operator ListZonesReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : ListZonesReplyOutcome;

    public static implicit operator ListZonesReplyOutcome(Common.Failure value) => new FailureCase(value);
}

/// <summary>
/// A thing's canonical row, from one native builder: the cells
/// read's things and the bundle's things table. thing.snapshot is the
/// thing's own CAS token. The food facts (15-23) are set for an ingestible
/// item or a corpse: rot_ticks at the current ambient temperature (present
/// exactly while the thing rots), temperature_c that ambient temperature,
/// roofed and room_id where a spawned thing stands, is_human_meat when the
/// thing or any ingredient it was made from (CompIngredients) is humanlike meat;
/// a corpse adds forbidden, its meat yield (meat_amount), body size and a
/// one-cell footprint. What the def alone says (class, vegetable, raw meat and
/// raw ingredient class, a corpse's humanlike race) is the definition catalog's.
/// </summary>
public sealed record Thing
{
    [JsonPropertyName("thing")]
    public EntityRef? Entity { get; init; }

    public long? StackCount { get; init; }

    public string? Stuff { get; init; }

    public int? HitPoints { get; init; }

    public bool? Forbidden { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Ref>))]
    public EquatableArray<Common.Ref> Owners { get; init; }

    public bool? Medical { get; init; }

    public double? Growth { get; init; }

    public bool? HarvestableNow { get; init; }

    public string? BuildDefName { get; init; }

    public bool? Blueprint { get; init; }

    public bool? Frame { get; init; }

    public long? RotTicks { get; init; }

    public double? TemperatureC { get; init; }

    public bool? Roofed { get; init; }

    public Common.Ref? Room { get; init; }

    public bool? Corpse { get; init; }

    public double? MeatAmount { get; init; }

    public double? BodySize { get; init; }

    public long? TileFootprint { get; init; }

    public bool? IsHumanMeat { get; init; }

    /// <summary>
    /// The thing's own CAS token.
    /// </summary>
    public SnapshotRef? Snapshot { get; init; }
}

public sealed record ThingsSnapshot
{
    public Common.ObservationContext? Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Thing>))]
    public EquatableArray<Thing> Things { get; init; }

    /// <summary>
    /// Row ids removed since the base table, in a delta section (SectionWatermark.delta).
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Removed { get; init; }
}

/// <summary>
/// One observations_get_cells read: the map's dimensions and the
/// requested rectangle as a CellGrid keyframe (mirror.proto's arrays and
/// sentinels: a fogged cell is not held; glow is artificial light only).
/// room keys a native room by the whole-map row-major index of its first
/// held cell inside the read. Things, terrain and foundation affordances ride
/// the grid's thing list and tile columns.
/// </summary>
public sealed record CellsSnapshot
{
    public Common.ObservationContext? Context { get; init; }

    public MapSize? MapSize { get; init; }

    public Mirror.CellGrid? Grid { get; init; }
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

public sealed record GetCellsReply
{
    public GetCellsReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="GetCellsReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(GetCellsReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(GetCellsReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(GetCellsReplyOutcome.FailureCase), "failure")]
public abstract record GetCellsReplyOutcome
{
    private protected GetCellsReplyOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public CellsSnapshot? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(CellsSnapshot Value) : GetCellsReplyOutcome;

    public static implicit operator GetCellsReplyOutcome(CellsSnapshot value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : GetCellsReplyOutcome;

    public static implicit operator GetCellsReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : GetCellsReplyOutcome;

    public static implicit operator GetCellsReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record ResearchProject
{
    /// <summary>
    /// Progress and availability only: the project's definition facts (cost, tab,
    /// prerequisites, techprints, required bench) are ResearchProjectDef rows of
    /// the definition catalog, and what a project unlocks is a reverse index over
    /// the rows that name it as a prerequisite.
    /// </summary>
    public DefinitionRef? Project { get; init; }

    public double? Progress { get; init; }

    public double? ProgressFraction { get; init; }

    public bool? Finished { get; init; }

    public bool? Current { get; init; }

    public bool? Available { get; init; }

    public bool? CanStart { get; init; }

    public uint? TechprintsApplied { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> LockReasons { get; init; }

    public string? LockDetail { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }
}

public sealed record Researcher
{
    public Common.Ref? Pawn { get; init; }

    public double? Intellectual { get; init; }

    public int? Priority { get; init; }

    public bool? Disabled { get; init; }

    public bool? EverWork { get; init; }

    public bool? Active { get; init; }
}

public sealed record ResearchFacility
{
    public DefinitionRef? Definition { get; init; }

    public bool? Active { get; init; }
}

public sealed record ResearchBench
{
    public Common.Ref? Building { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ResearchFacility>))]
    public EquatableArray<ResearchFacility> Facilities { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }
}

public sealed record ResearchSlot
{
    public string? Category { get; init; }

    public string? CurrentProject { get; init; }
}

/// <summary>
/// finished names every finished project, whatever the request filters.
/// </summary>
public sealed record ResearchSnapshot
{
    public Common.ObservationContext? Context { get; init; }

    public SnapshotRef? Snapshot { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ResearchSlot>))]
    public EquatableArray<ResearchSlot> Slots { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ResearchProject>))]
    public EquatableArray<ResearchProject> Projects { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ResearchBench>))]
    public EquatableArray<ResearchBench> Benches { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Researcher>))]
    public EquatableArray<Researcher> Researchers { get; init; }

    public bool? AnomalyActive { get; init; }

    public string? PlayerTechLevel { get; init; }

    public Completeness? Completeness { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Finished { get; init; }
}

/// <summary>
/// progress_only lists only the current projects, with their lock
/// reasons, and the started unfinished ones; the other filters are ignored.
/// </summary>
public sealed record ResearchRequest
{
    public ReadScope? Scope { get; init; }

    public bool? IncludeLocked { get; init; }

    public bool? IncludeFinished { get; init; }

    public bool? IncludeCapability { get; init; }

    public string? NameContains { get; init; }

    public bool? ProgressOnly { get; init; }
}

public sealed record ResearchReply
{
    public ResearchReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="ResearchReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ResearchReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(ResearchReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(ResearchReplyOutcome.FailureCase), "failure")]
public abstract record ResearchReplyOutcome
{
    private protected ResearchReplyOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public ResearchSnapshot? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(ResearchSnapshot Value) : ResearchReplyOutcome;

    public static implicit operator ResearchReplyOutcome(ResearchSnapshot value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : ResearchReplyOutcome;

    public static implicit operator ResearchReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : ResearchReplyOutcome;

    public static implicit operator ResearchReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record AccessTarget
{
    public Common.Cell? Cell { get; init; }

    public bool? NativeReachable { get; init; }

    public bool? ProjectedReachable { get; init; }

    public uint? ProjectedSteps { get; init; }
}

public sealed record PawnAccess
{
    public Common.Ref? Pawn { get; init; }

    public uint? CurrentCells { get; init; }

    public uint? ProjectedCells { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<AccessTarget>))]
    public EquatableArray<AccessTarget> Targets { get; init; }

    public Common.Cell? ProjectedOrigin { get; init; }

    public uint? EgressSteps { get; init; }

    public bool? LosesAccess { get; init; }

    /// <summary>
    /// The pawn's cell when the access was read.
    /// </summary>
    public Common.Cell? Position { get; init; }

    /// <summary>
    /// The request's budget_ms ran out before this colonist: every field but pawn
    /// and position is absent (unknown, not "keeps access").
    /// </summary>
    public bool? Skipped { get; init; }
}

public sealed record SpatialAccessSnapshot
{
    public Common.ObservationContext? Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnAccess>))]
    public EquatableArray<PawnAccess> Pawns { get; init; }

    public uint? MapCells { get; init; }

    public uint? ObservedWalkableCells { get; init; }
}

/// <summary>
/// budget_ms: main-thread milliseconds the audit may spend (about 70-80 ms per colonist,
/// independent of the blocked and target cell counts). Colonists past it come back
/// PawnAccess.skipped. Absent: audit every colonist.
/// </summary>
public sealed record SpatialAccessRequest
{
    public ReadScope? Scope { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public EquatableArray<Common.Cell> BlockedCells { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public EquatableArray<Common.Cell> TargetCells { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> PawnIds { get; init; }

    public uint? BudgetMs { get; init; }
}

public sealed record SpatialAccessReply
{
    public SpatialAccessReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="SpatialAccessReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SpatialAccessReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(SpatialAccessReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(SpatialAccessReplyOutcome.FailureCase), "failure")]
public abstract record SpatialAccessReplyOutcome
{
    private protected SpatialAccessReplyOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public SpatialAccessSnapshot? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(SpatialAccessSnapshot Value) : SpatialAccessReplyOutcome;

    public static implicit operator SpatialAccessReplyOutcome(SpatialAccessSnapshot value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : SpatialAccessReplyOutcome;

    public static implicit operator SpatialAccessReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : SpatialAccessReplyOutcome;

    public static implicit operator SpatialAccessReplyOutcome(Common.Failure value) => new FailureCase(value);
}

/// <summary>
/// Defense site census: bounded terrain, cover and raider-approach facts for
/// layout planning. cover_fill is the native edifice/cover fillPercent the
/// shooting model uses (0 when open); edge_reachable is native ground
/// reachability from the cell to any map edge without opening doors, the
/// ordinary walk-in raid approach. Every cell of the requested rectangle.
/// </summary>
public sealed record DefenseCell
{
    public Common.Cell? Cell { get; init; }

    public bool? Fogged { get; init; }

    public bool? Walkable { get; init; }

    public bool? Passable { get; init; }

    public double? CoverFill { get; init; }

    public string? EdificeDefName { get; init; }

    public bool? PlayerOwned { get; init; }

    public bool? NaturalRock { get; init; }

    public bool? EdgeReachable { get; init; }

    public string? Terrain { get; init; }

    public bool? Door { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }

    /// <summary>
    /// The thing whose fillPercent cover_fill reports (GetCover, else the
    /// edifice), with the clearance designation ordinary work would need.
    /// Absent when the cell is open.
    /// </summary>
    public string? CoverThingId { get; init; }

    public string? CoverDefName { get; init; }

    public CoverKind? CoverKind { get; init; }

    public bool? CoverDesignated { get; init; }

    /// <summary>
    /// The cell's foundation (a Bridge) is designated for removal.
    /// </summary>
    public bool? FoundationRemovalDesignated { get; init; }

    /// <summary>
    /// The edifice's stuff def (WoodLog, Plasteel), absent for a stuffless one.
    /// </summary>
    public string? EdificeStuffDefName { get; init; }

    /// <summary>
    /// The cell is under any roof (a burn-out's seal holds its heat).
    /// </summary>
    public bool? Roofed { get; init; }
}

/// <summary>
/// One hostile lord observed on the map since load: where its first pawn was
/// seen (ground when on the map edge), and a bounded trail of one of its
/// pawns sampled every RaidTrack.SampleInterval ticks, so a census can find
/// where the raid crossed its boundary. Session-scoped, like traffic.
/// </summary>
public sealed record RaidTrack
{
    public string? LordId { get; init; }

    public string? FactionDef { get; init; }

    public long? SpawnTick { get; init; }

    public Common.Cell? Spawn { get; init; }

    public bool? Ground { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public EquatableArray<Common.Cell> Trail { get; init; }

    public long? LastTick { get; init; }
}

public sealed record DefenseSiteSnapshot
{
    public Common.ObservationContext? Context { get; init; }

    public MapSize? MapSize { get; init; }

    public Rectangle? Region { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<DefenseCell>))]
    public EquatableArray<DefenseCell> Cells { get; init; }
}

public sealed record DefenseSiteRequest
{
    public ReadScope? Scope { get; init; }

    public Rectangle? Region { get; init; }
}

public sealed record DefenseSiteReply
{
    public DefenseSiteReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="DefenseSiteReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(DefenseSiteReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(DefenseSiteReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(DefenseSiteReplyOutcome.FailureCase), "failure")]
public abstract record DefenseSiteReplyOutcome
{
    private protected DefenseSiteReplyOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public DefenseSiteSnapshot? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(DefenseSiteSnapshot Value) : DefenseSiteReplyOutcome;

    public static implicit operator DefenseSiteReplyOutcome(DefenseSiteSnapshot value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : DefenseSiteReplyOutcome;

    public static implicit operator DefenseSiteReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : DefenseSiteReplyOutcome;

    public static implicit operator DefenseSiteReplyOutcome(Common.Failure value) => new FailureCase(value);
}

/// <summary>
/// Lines of fire: native GenSight line of sight and CoverUtility overall block
/// chance for every (firing, approach) pair. target_cover is the chance a
/// target at `to` is protected from a shooter at `from`; shooter_cover is the
/// reverse. Pairs are read firing-major under the request's budget_ms (about 3 us a
/// pair); past it the snapshot is skipped and holds only the pairs read.
/// </summary>
public sealed record LineOfFire
{
    public Common.Cell? From { get; init; }

    public Common.Cell? To { get; init; }

    public bool? LineOfSight { get; init; }

    public double? TargetCover { get; init; }

    public double? ShooterCover { get; init; }

    public double? Distance { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }
}

public sealed record LinesOfFireSnapshot
{
    public Common.ObservationContext? Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<LineOfFire>))]
    public EquatableArray<LineOfFire> Lines { get; init; }

    public bool? Skipped { get; init; }
}

public sealed record LinesOfFireRequest
{
    public ReadScope? Scope { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public EquatableArray<Common.Cell> FiringCells { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public EquatableArray<Common.Cell> ApproachCells { get; init; }

    public uint? BudgetMs { get; init; }
}

public sealed record LinesOfFireReply
{
    public LinesOfFireReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="LinesOfFireReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LinesOfFireReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(LinesOfFireReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(LinesOfFireReplyOutcome.FailureCase), "failure")]
public abstract record LinesOfFireReplyOutcome
{
    private protected LinesOfFireReplyOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public LinesOfFireSnapshot? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(LinesOfFireSnapshot Value) : LinesOfFireReplyOutcome;

    public static implicit operator LinesOfFireReplyOutcome(LinesOfFireSnapshot value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : LinesOfFireReplyOutcome;

    public static implicit operator LinesOfFireReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : LinesOfFireReplyOutcome;

    public static implicit operator LinesOfFireReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record RoofSupportCell
{
    public Common.Cell? Cell { get; init; }

    public string? RoofDefName { get; init; }

    public bool? SupportedWithoutTarget { get; init; }

    public string? Blocker { get; init; }
}

public sealed record RoofSupportSnapshot
{
    public Common.ObservationContext? Context { get; init; }

    public Common.Ref? Target { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<RoofSupportCell>))]
    public EquatableArray<RoofSupportCell> Roofs { get; init; }
}

public sealed record RoofSupportRequest
{
    public ReadScope? Scope { get; init; }

    public string? TargetId { get; init; }
}

public sealed record RoofSupportReply
{
    public RoofSupportReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="RoofSupportReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RoofSupportReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(RoofSupportReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(RoofSupportReplyOutcome.FailureCase), "failure")]
public abstract record RoofSupportReplyOutcome
{
    private protected RoofSupportReplyOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public RoofSupportSnapshot? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(RoofSupportSnapshot Value) : RoofSupportReplyOutcome;

    public static implicit operator RoofSupportReplyOutcome(RoofSupportSnapshot value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : RoofSupportReplyOutcome;

    public static implicit operator RoofSupportReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : RoofSupportReplyOutcome;

    public static implicit operator RoofSupportReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record ExcavationCell
{
    public Common.Cell? Cell { get; init; }

    public bool? Fogged { get; init; }

    public string? MineableDefName { get; init; }

    public bool? Walkable { get; init; }

    public bool? MineDesignated { get; init; }

    public bool? Eligible { get; init; }

    public string? Blocker { get; init; }
}

public sealed record ExcavationSiteSnapshot
{
    public Common.ObservationContext? Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ExcavationCell>))]
    public EquatableArray<ExcavationCell> Cells { get; init; }

    public ExcavationSupport SupportAfterRemoval { get; init; }

    public uint? RoofCellsChecked { get; init; }

    public string? SupportBlocker { get; init; }

    public bool? CollapsePending { get; init; }

    public bool? WorkerAvailable { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> WorkerIds { get; init; }

    public bool? AccessReachable { get; init; }
}

public sealed record ExcavationSiteRequest
{
    public ReadScope? Scope { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public EquatableArray<Common.Cell> Cells { get; init; }

    public Common.Cell? AccessCell { get; init; }
}

public sealed record ExcavationSiteReply
{
    public ExcavationSiteReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="ExcavationSiteReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ExcavationSiteReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(ExcavationSiteReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(ExcavationSiteReplyOutcome.FailureCase), "failure")]
public abstract record ExcavationSiteReplyOutcome
{
    private protected ExcavationSiteReplyOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public ExcavationSiteSnapshot? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(ExcavationSiteSnapshot Value) : ExcavationSiteReplyOutcome;

    public static implicit operator ExcavationSiteReplyOutcome(ExcavationSiteSnapshot value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : ExcavationSiteReplyOutcome;

    public static implicit operator ExcavationSiteReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : ExcavationSiteReplyOutcome;

    public static implicit operator ExcavationSiteReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record WallUpgradeSite
{
    public Common.Ref? Target { get; init; }

    public bool? TargetPresent { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public EquatableArray<Common.Cell> BackupCells { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ResourceStock>))]
    public EquatableArray<ResourceStock> Materials { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Ref>))]
    public EquatableArray<Common.Ref> Workers { get; init; }

    public string? Blocker { get; init; }

    public bool? Designated { get; init; }

    public string? RemovalId { get; init; }

    public long? CompletedTick { get; init; }

    public SnapshotRef? Snapshot { get; init; }

    public Common.Cell? Normal { get; init; }

    public Common.Ref? Original { get; init; }

    public Common.Ref? LeftSupport { get; init; }

    public Common.Ref? RightSupport { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Ref>))]
    public EquatableArray<Common.Ref> CompletedBackups { get; init; }

    public Common.Ref? Replacement { get; init; }

    /// <summary>
    /// The target's CAS token for the upgrade's guarded operations.
    /// </summary>
    public SnapshotRef? TargetSnapshot { get; init; }

    public CellsSnapshot? Geometry { get; init; }

    public RoofSupportSnapshot? RoofSupport { get; init; }
}

public sealed record WallUpgradeSnapshot
{
    public Common.ObservationContext? Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<WallUpgradeSite>))]
    public EquatableArray<WallUpgradeSite> Sites { get; init; }
}

public sealed record WallUpgradeSitesRequest
{
    public ReadScope? Scope { get; init; }

    public string? TargetId { get; init; }
}

public sealed record WallUpgradeSitesReply
{
    public WallUpgradeSitesReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="WallUpgradeSitesReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(WallUpgradeSitesReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(WallUpgradeSitesReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(WallUpgradeSitesReplyOutcome.FailureCase), "failure")]
public abstract record WallUpgradeSitesReplyOutcome
{
    private protected WallUpgradeSitesReplyOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public WallUpgradeSnapshot? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(WallUpgradeSnapshot Value) : WallUpgradeSitesReplyOutcome;

    public static implicit operator WallUpgradeSitesReplyOutcome(WallUpgradeSnapshot value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : WallUpgradeSitesReplyOutcome;

    public static implicit operator WallUpgradeSitesReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : WallUpgradeSitesReplyOutcome;

    public static implicit operator WallUpgradeSitesReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record ResourceSource
{
    public Common.Ref? Source { get; init; }

    public double? Yield { get; init; }

    public bool? Reachable { get; init; }

    public bool? Designated { get; init; }

    public string? Risk { get; init; }

    public string? Safety { get; init; }

    public string? Blocker { get; init; }

    public double? Distance { get; init; }

    public bool? Depleted { get; init; }

    public long? Remaining { get; init; }

    public long? PendingYield { get; init; }

    public long? DesignatedTick { get; init; }

    public bool? Taken { get; init; }

    /// <summary>
    /// buried: a supported mine deposit no colonist can reach (usually fogged); a corridor excavation must reach it first.
    /// </summary>
    public bool? Buried { get; init; }

    /// <summary>
    /// A mine source's CAS token and cell: rocks are in no frame's things table.
    /// </summary>
    public SnapshotRef? SourceSnapshot { get; init; }

    public Common.Cell? Cell { get; init; }

    /// <summary>
    /// def_name: the source thing's def; the method (mine, cut, harvest) is a rule over that def row.
    /// </summary>
    public string? DefName { get; init; }
}

public sealed record StorageCapacity
{
    public string? Resource { get; init; }

    public long? Capacity { get; init; }

    public long? Stored { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Ref>))]
    public EquatableArray<Common.Ref> Haulers { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Blockers { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public EquatableArray<Common.Cell> Candidates { get; init; }
}

public sealed record ResourceSourcesSnapshot
{
    public Common.ObservationContext? Context { get; init; }

    public string? Resource { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ResourceSource>))]
    public EquatableArray<ResourceSource> Sources { get; init; }

    public StorageCapacity? Storage { get; init; }

    public Completeness? Completeness { get; init; }
}

public sealed record ResourceSourcesRequest
{
    public ReadScope? Scope { get; init; }

    public string? Resource { get; init; }
}

public sealed record ResourceSourcesReply
{
    public ResourceSourcesReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="ResourceSourcesReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ResourceSourcesReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(ResourceSourcesReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(ResourceSourcesReplyOutcome.FailureCase), "failure")]
public abstract record ResourceSourcesReplyOutcome
{
    private protected ResourceSourcesReplyOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public ResourceSourcesSnapshot? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(ResourceSourcesSnapshot Value) : ResourceSourcesReplyOutcome;

    public static implicit operator ResourceSourcesReplyOutcome(ResourceSourcesSnapshot value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : ResourceSourcesReplyOutcome;

    public static implicit operator ResourceSourcesReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : ResourceSourcesReplyOutcome;

    public static implicit operator ResourceSourcesReplyOutcome(Common.Failure value) => new FailureCase(value);
}

/// <summary>
/// Realized consumption. Native keeps a saved hourly ring (2500-tick
/// hours, 60 days) of what colony stock was spent, per (definition, reason). A
/// read names the last hour Go already holds; the reply carries every
/// completed hour after it (since_hour absent or negative: the whole window),
/// sparse: an hour or row not listed spent nothing. count is signed: an
/// ejected refuel or removed shell subtracts. current_hour is still filling and
/// is never listed; first_hour is the first hour the ring covers.
/// </summary>
public sealed record ConsumptionRow
{
    public string? Definition { get; init; }

    public string? Reason { get; init; }

    public long? Count { get; init; }
}

public sealed record ConsumptionHour
{
    public int? Hour { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ConsumptionRow>))]
    public EquatableArray<ConsumptionRow> Rows { get; init; }
}

public sealed record ConsumptionSnapshot
{
    public int? CurrentHour { get; init; }

    public int? FirstHour { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ConsumptionHour>))]
    public EquatableArray<ConsumptionHour> Hours { get; init; }
}

public sealed record ConsumptionRequest
{
    public int? SinceHour { get; init; }
}

public sealed record ConsumptionReply
{
    public ConsumptionReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="ConsumptionReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ConsumptionReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(ConsumptionReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(ConsumptionReplyOutcome.FailureCase), "failure")]
public abstract record ConsumptionReplyOutcome
{
    private protected ConsumptionReplyOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public ConsumptionSnapshot? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(ConsumptionSnapshot Value) : ConsumptionReplyOutcome;

    public static implicit operator ConsumptionReplyOutcome(ConsumptionSnapshot value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : ConsumptionReplyOutcome;

    public static implicit operator ConsumptionReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : ConsumptionReplyOutcome;

    public static implicit operator ConsumptionReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record HandlerState
{
    public Common.Ref? Pawn { get; init; }

    public int? Skill { get; init; }

    public int? Priority { get; init; }

    public bool? Eligible { get; init; }

    public string? Reason { get; init; }
}

public sealed record HusbandryAnimal
{
    public Common.Ref? Pawn { get; init; }

    public AnimalState? Animal { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<HandlerState>))]
    public EquatableArray<HandlerState> Handlers { get; init; }
}

public sealed record HusbandrySnapshot
{
    public Common.ObservationContext? Context { get; init; }

    public SnapshotRef? Census { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<HusbandryAnimal>))]
    public EquatableArray<HusbandryAnimal> Animals { get; init; }
}

public sealed record HusbandryRequest
{
    public ReadScope? Scope { get; init; }

    public bool? IncludeWild { get; init; }
}

public sealed record HusbandryReply
{
    public HusbandryReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="HusbandryReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HusbandryReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(HusbandryReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(HusbandryReplyOutcome.FailureCase), "failure")]
public abstract record HusbandryReplyOutcome
{
    private protected HusbandryReplyOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public HusbandrySnapshot? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(HusbandrySnapshot Value) : HusbandryReplyOutcome;

    public static implicit operator HusbandryReplyOutcome(HusbandrySnapshot value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : HusbandryReplyOutcome;

    public static implicit operator HusbandryReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : HusbandryReplyOutcome;

    public static implicit operator HusbandryReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record WasteItem
{
    public Common.Ref? Thing { get; init; }

    public long? Count { get; init; }

    public WasteLocation? State { get; init; }

    public Common.Ref? Zone { get; init; }

    public Common.Ref? Grave { get; init; }

    public Common.RotStage? RotStage { get; init; }

    public WasteKind? Kind { get; init; }

    public bool? Eligible { get; init; }

    public string? ProtectedReason { get; init; }

    /// <summary>
    /// A corpse's inner pawn; unset for non-corpses.
    /// </summary>
    public Common.CorpseClass? CorpseClass { get; init; }

    /// <summary>
    /// A corpse's vanilla everBuriedInSarcophagus: set once it has lain in a
    /// sarcophagus, after which a re-burial fires no memory.
    /// </summary>
    public bool? EverBuriedInSarcophagus { get; init; }
}

public sealed record WasteSnapshot
{
    public Common.ObservationContext? Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<WasteItem>))]
    public EquatableArray<WasteItem> Items { get; init; }
}

public sealed record WasteReply
{
    public WasteReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="WasteReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(WasteReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(WasteReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(WasteReplyOutcome.FailureCase), "failure")]
public abstract record WasteReplyOutcome
{
    private protected WasteReplyOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public WasteSnapshot? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(WasteSnapshot Value) : WasteReplyOutcome;

    public static implicit operator WasteReplyOutcome(WasteSnapshot value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : WasteReplyOutcome;

    public static implicit operator WasteReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : WasteReplyOutcome;

    public static implicit operator WasteReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record RecoveryRestriction
{
    public Common.Ref? Pawn { get; init; }

    public string? AreaId { get; init; }

    public bool? Leased { get; init; }

    public long? UntilTick { get; init; }
}

public sealed record RecoverySnapshot
{
    public Common.ObservationContext? Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<RecoveryRestriction>))]
    public EquatableArray<RecoveryRestriction> Restrictions { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Ref>))]
    public EquatableArray<Common.Ref> Buildings { get; init; }
}

public sealed record RecoveryRequest
{
    public ReadScope? Scope { get; init; }
}

public sealed record RecoveryReply
{
    public RecoveryReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="RecoveryReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RecoveryReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(RecoveryReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(RecoveryReplyOutcome.FailureCase), "failure")]
public abstract record RecoveryReplyOutcome
{
    private protected RecoveryReplyOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public RecoverySnapshot? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(RecoverySnapshot Value) : RecoveryReplyOutcome;

    public static implicit operator RecoveryReplyOutcome(RecoverySnapshot value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : RecoveryReplyOutcome;

    public static implicit operator RecoveryReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : RecoveryReplyOutcome;

    public static implicit operator RecoveryReplyOutcome(Common.Failure value) => new FailureCase(value);
}

/// <summary>
/// pawn refers to the bundle pawn table row; its snapshot is the
/// prisoner custody and interaction settings token.
/// </summary>
public sealed record PopulationPerson
{
    public Common.Ref? Pawn { get; init; }

    public SnapshotRef? PawnSnapshot { get; init; }

    public bool? Admitted { get; init; }

    public bool? Guest { get; init; }

    public bool? Recruitable { get; init; }

    public double? Resistance { get; init; }

    public string? Interaction { get; init; }

    public Common.Ref? OwnedBed { get; init; }

    public double? NutritionPerDay { get; init; }

    public long? PrisonerTicks { get; init; }

    /// <summary>
    /// Prisoner prospect facts MaintainPopulation judges a prisoner's use by
    /// Pawn_GuestTracker.will (Ideology), the pawn's ideoligion load
    /// id, Verse.WildManUtility.IsWildMan, and, for prisoners and free
    /// colonists, the skill/trait/age biography and SummaryHealthPercent.
    /// </summary>
    public double? Will { get; init; }

    public string? IdeoId { get; init; }

    public bool? WildMan { get; init; }

    public PawnBiography? Biography { get; init; }

    public double? HealthSummary { get; init; }

    /// <summary>
    /// After combat, for a hostile: a LuciferiumAddiction hediff (not
    /// worth capturing: stripped and finished instead) and any worn apparel
    /// (every downed raider is stripped before it is captured).
    /// </summary>
    public bool? LuciferiumAddicted { get; init; }

    public bool? WearingApparel { get; init; }

    /// <summary>
    /// Organ harvest facts, for a prisoner of the colony only: its
    /// surgery facts (missing_parts, operations and surgery_bills filled as on
    /// the pawn care read, other PawnHealth fields absent), its faction's load
    /// id, and the goodwill change vanilla's Recipe_RemoveBodyPart violation
    /// report (-70) would make with it after CalculateAdjustedGoodwillChange and
    /// the -100 floor; 0 when the faction's goodwill cannot change or none.
    /// </summary>
    public PawnHealth? Surgery { get; init; }

    public Common.Ref? Faction { get; init; }

    public int? HarvestGoodwillChange { get; init; }

    /// <summary>
    /// The MedicalCareCategory name, for a colony prisoner or a guest
    /// of the colony.
    /// </summary>
    public Operations.MedicalCare? MedicalCare { get; init; }

    /// <summary>
    /// Peg-leg control, for a prisoner of the colony only: it carries a
    /// drug addiction (Hediff_Addiction). A prisoner gets no drugs, so an
    /// addiction is withdrawal now or soon, until the addiction clears.
    /// </summary>
    public bool? Withdrawal { get; init; }

    /// <summary>
    /// Medical care cap inputs, for a colony prisoner or guest: life
    /// threat, every hediff (definition, severity, immunity and their rates)
    /// and the completeness; other PawnHealth fields absent.
    /// </summary>
    public PawnHealth? Conditions { get; init; }

    /// <summary>
    /// Policy inputs, for a prisoner of the colony only: its current
    /// drug policy id and chemical state, as on the work census settings row.
    /// </summary>
    public PawnPolicyInputs? PolicyInputs { get; init; }

    public bool? WillJoinIfRescued { get; init; }
}

public sealed record PopulationSnapshot
{
    public Common.ObservationContext? Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PopulationPerson>))]
    public EquatableArray<PopulationPerson> Persons { get; init; }

    /// <summary>
    /// ModsConfig.IdeologyActive, IdeoManager.classicMode, the player faction's
    /// primary ideoligion load id;
    /// absent without Ideology; precepts ride the ideology section.
    /// </summary>
    public bool? IdeologyActive { get; init; }

    public bool? ClassicIdeoMode { get; init; }

    public string? ColonyIdeoId { get; init; }

    /// <summary>
    /// Storyteller population outlook: StorytellerUtilityPopulation intent and
    /// adjusted population, and the capture odds that intent sets for a non-colony
    /// humanlike downed by violence (death chance) and for a new prisoner (unrecruitable).
    /// </summary>
    public double? PopulationIntent { get; init; }

    public double? AdjustedPopulation { get; init; }

    public double? DeathOnDownedChance { get; init; }

    public double? UnrecruitableChance { get; init; }

    /// <summary>
    /// (Organ-use precepts ride the ideology section.)
    /// Owned-pawn names: every living named pawn the colony owns --
    /// player-faction colonists, slaves and animals, and colony prisoners --
    /// on any map, caravan or transporter; unnamed animals are absent.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<OwnedName>))]
    public EquatableArray<OwnedName> OwnedNames { get; init; }
}

/// <summary>
/// One owned pawn's short name (Name.ToStringShort) and thingIDNumber, which
/// orders pawns by creation: the higher number is the newer pawn.
/// </summary>
public sealed record OwnedName
{
    public string? PawnId { get; init; }

    public string? ShortName { get; init; }

    public int? ThingId { get; init; }
}

public sealed record PopulationRequest
{
    public ReadScope? Scope { get; init; }
}

public sealed record PopulationReply
{
    public PopulationReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="PopulationReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PopulationReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(PopulationReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(PopulationReplyOutcome.FailureCase), "failure")]
public abstract record PopulationReplyOutcome
{
    private protected PopulationReplyOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public PopulationSnapshot? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(PopulationSnapshot Value) : PopulationReplyOutcome;

    public static implicit operator PopulationReplyOutcome(PopulationSnapshot value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : PopulationReplyOutcome;

    public static implicit operator PopulationReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : PopulationReplyOutcome;

    public static implicit operator PopulationReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record Settlement
{
    public string? Id { get; init; }

    public string? Label { get; init; }

    public Common.Ref? Faction { get; init; }

    public string? FactionDefName { get; init; }

    public string? Relation { get; init; }

    public int? Goodwill { get; init; }

    public int? Tile { get; init; }

    public double? DistanceTiles { get; init; }

    public bool? Player { get; init; }

    public SnapshotRef? Snapshot { get; init; }

    public SnapshotRef? FactionSnapshot { get; init; }

    public bool? CanTrade { get; init; }

    public string? TraderKind { get; init; }
}

public sealed record WorldTile
{
    public int? Tile { get; init; }

    public DefinitionRef? Biome { get; init; }

    public string? Hilliness { get; init; }

    public double? Elevation { get; init; }

    public double? Latitude { get; init; }

    public double? Longitude { get; init; }

    public double? Rainfall { get; init; }

    public double? Swampiness { get; init; }

    public double? Pollution { get; init; }

    public bool? Coastal { get; init; }

    public double? TemperatureC { get; init; }

    public double? MinTemperatureC { get; init; }

    public double? MaxTemperatureC { get; init; }

    public double? AverageTemperatureC { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<uint>))]
    public EquatableArray<uint> GrowingTwelfths { get; init; }

    public double? GrowingDays { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }
}

public sealed record WorldSnapshot
{
    public Common.ObservationContext? Context { get; init; }

    public WorldTile? Tile { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Settlement>))]
    public EquatableArray<Settlement> Settlements { get; init; }
}

public sealed record WorldRequest
{
    public ReadScope? Scope { get; init; }

    public int? Tile { get; init; }

    public double? SettlementRadius { get; init; }
}

public sealed record WorldReply
{
    public WorldReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="WorldReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(WorldReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(WorldReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(WorldReplyOutcome.FailureCase), "failure")]
public abstract record WorldReplyOutcome
{
    private protected WorldReplyOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public WorldSnapshot? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(WorldSnapshot Value) : WorldReplyOutcome;

    public static implicit operator WorldReplyOutcome(WorldSnapshot value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : WorldReplyOutcome;

    public static implicit operator WorldReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : WorldReplyOutcome;

    public static implicit operator WorldReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record WorldRoute
{
    public string? SettlementId { get; init; }

    public int? Destination { get; init; }

    public bool? Reachable { get; init; }

    public long? EstimatedTicks { get; init; }

    public string? Reason { get; init; }

    public double? TemperatureC { get; init; }

    public bool? Hostile { get; init; }

    public int? Goodwill { get; init; }

    public Common.Ref? Faction { get; init; }
}

public sealed record CaravanState
{
    public EntityRef? Caravan { get; init; }

    public int? Tile { get; init; }

    public int? Destination { get; init; }

    public bool? Moving { get; init; }

    public bool? MovingNow { get; init; }

    public bool? Resting { get; init; }

    public double? MassUsage { get; init; }

    public double? MassCapacity { get; init; }

    public double? FoodDays { get; init; }

    public double? FoodRotDays { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnState>))]
    public EquatableArray<PawnState> Pawns { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<WorldRoute>))]
    public EquatableArray<WorldRoute> HomeRoutes { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Quantity>))]
    public EquatableArray<Quantity> Inventory { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<WorldRoute>))]
    public EquatableArray<WorldRoute> SettlementRoutes { get; init; }
}

public sealed record QuestTradeRequest
{
    public string? Resource { get; init; }

    public long? Count { get; init; }

    public int? Destination { get; init; }
}

public sealed record QuestReward
{
    public uint? ChoiceIndex { get; init; }

    public string? Kind { get; init; }

    public string? Label { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Quantity>))]
    public EquatableArray<Quantity> Items { get; init; }

    public int? Favor { get; init; }

    public int? Goodwill { get; init; }

    public int? Psylink { get; init; }

    public int? PermitPoints { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Permits { get; init; }

    public string? TitleDef { get; init; }

    public string? FactionId { get; init; }
}

public sealed record QuestObjective
{
    public QuestObjectiveKind? Kind { get; init; }

    public string? Def { get; init; }

    public string? Stuff { get; init; }

    public long? Count { get; init; }

    public long? Produced { get; init; }

    public long? DeadlineTicks { get; init; }

    public string? UnmetRequirement { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> PawnIds { get; init; }

    public bool? Active { get; init; }

    public QuestMonument? Monument { get; init; }

    public double? MinimumMood { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<QuestLodgerMood>))]
    public EquatableArray<QuestLodgerMood> LodgerMoods { get; init; }

    /// <summary>
    /// Time available after acceptance; never an offer expiry.
    /// </summary>
    public long? DurationTicks { get; init; }

    public QuestWorkload? Workload { get; init; }

    public QuestGravEngine? GravEngine { get; init; }

    public QuestSurveyScanner? SurveyScanner { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<QuestHackTarget>))]
    public EquatableArray<QuestHackTarget> HackTargets { get; init; }

    public QuestGiftRequest? GiftRequest { get; init; }

    public QuestHackRisk? HackRisk { get; init; }
}

public sealed record QuestHackTarget
{
    public string? Id { get; init; }

    public int? MapId { get; init; }

    public bool? Spawned { get; init; }

    public HackableState? Hackable { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> EligiblePawnIds { get; init; }

    public bool? Satisfied { get; init; }
}

public sealed record QuestGiftRequest
{
    public string? RecipientId { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> PawnIds { get; init; }

    public string? Def { get; init; }

    public long? Remaining { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> HaulingPawnIds { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> EligiblePawnIds { get; init; }

    public int? MapId { get; init; }
}

public sealed record QuestHackRisk
{
    public string? FactionId { get; init; }

    public bool? Hostile { get; init; }
}

public sealed record QuestSurveyScanner
{
    public string? SiteId { get; init; }

    public string? ScannerId { get; init; }

    public long? DurationTicks { get; init; }

    public long? EndTick { get; init; }

    public long? RaidTick { get; init; }

    public bool? Alive { get; init; }

    public bool? Complete { get; init; }
}

public sealed record QuestGravEngine
{
    public string? EngineId { get; init; }

    public int? MapId { get; init; }

    public bool? Spawned { get; init; }

    public Common.Cell? Cell { get; init; }

    public bool? Inspected { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> EligiblePawnIds { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> InspectingPawnIds { get; init; }
}

public sealed record WorldSite
{
    public string? Id { get; init; }

    public string? DefName { get; init; }

    public string? Label { get; init; }

    public int? Tile { get; init; }

    public int? LayerId { get; init; }

    public WorldSiteState? State { get; init; }

    public int? MapId { get; init; }

    public double? DistanceTiles { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> QuestIds { get; init; }

    public bool? Threat { get; init; }

    public double? ThreatPoints { get; init; }

    public bool? Reachable { get; init; }

    public long? TravelTicks { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> RoutePawnIds { get; init; }

    public QuestSiteSecurity? Security { get; init; }

    public QuestSiteExtraction? Extraction { get; init; }

    public QuestPeaceTalks? PeaceTalks { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<QuestSiteMiningTarget>))]
    public EquatableArray<QuestSiteMiningTarget> MiningTargets { get; init; }
}

public sealed record QuestSiteMiningTarget
{
    public string? Id { get; init; }

    public string? Def { get; init; }

    public Common.Cell? Cell { get; init; }

    public int? MapId { get; init; }

    public bool? Designated { get; init; }
}

public sealed record QuestSiteSecurity
{
    public bool? Known { get; init; }

    public double? InitialPoints { get; init; }

    public double? PendingRaidPoints { get; init; }

    public bool? ActiveThreat { get; init; }

    public bool? DormantThreat { get; init; }

    public int? TrapCount { get; init; }

    public bool? DetectionActive { get; init; }

    public long? DetectionTicksLeft { get; init; }

    public int? RaidsSent { get; init; }
}

public sealed record QuestSiteExtraction
{
    public bool? CanReform { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> CrewIds { get; init; }

    public double? CarryCapacity { get; init; }

    public double? CarriedMass { get; init; }

    public double? InventoryFoodDays { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<QuestSiteCargo>))]
    public EquatableArray<QuestSiteCargo> Cargo { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<QuestSiteHomeRoute>))]
    public EquatableArray<QuestSiteHomeRoute> HomeRoutes { get; init; }

    public double? CrewNutritionPerDay { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public EquatableArray<Common.Cell> ExitCells { get; init; }
}

public sealed record QuestSiteCargo
{
    public string? Id { get; init; }

    public string? Def { get; init; }

    public long? Count { get; init; }

    public double? UnitMass { get; init; }

    public double? MarketValue { get; init; }

    public double? Nutrition { get; init; }

    public bool? Held { get; init; }
}

public sealed record QuestSiteHomeRoute
{
    public int? MapId { get; init; }

    public int? Tile { get; init; }

    public bool? Reachable { get; init; }

    public long? TravelTicks { get; init; }
}

public sealed record QuestPeaceTalks
{
    public string? FactionId { get; init; }

    public int? CurrentGoodwill { get; init; }

    public int? WorstGoodwillLoss { get; init; }

    public int? BestGoodwillGain { get; init; }

    public bool? IdeologyActive { get; init; }
}

public sealed record QuestWorkload
{
    public string? Recipe { get; init; }

    public string? Stat { get; init; }

    public double? Work { get; init; }

    public double? RateFactor { get; init; }
}

public sealed record QuestLodgerMood
{
    public string? PawnId { get; init; }

    public double? Mood { get; init; }
}

public sealed record QuestMonument
{
    public string? MarkerId { get; init; }

    public string? DefName { get; init; }

    public int? MapId { get; init; }

    public bool? Packed { get; init; }

    public bool? Installed { get; init; }

    public Common.Cell? Cell { get; init; }

    public bool? Complete { get; init; }

    public bool? AllDone { get; init; }

    public string? DisallowedBuildingId { get; init; }

    public long? DisallowedTicks { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public EquatableArray<Common.Cell> InstallCells { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<QuestMonumentPiece>))]
    public EquatableArray<QuestMonumentPiece> Pieces { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<QuestMonumentResource>))]
    public EquatableArray<QuestMonumentResource> Resources { get; init; }

    public bool? Offered { get; init; }

    /// <summary>
    /// Legal marker placements observed against the complete sketch footprint.
    /// </summary>
    public bool? ClearSite { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Quantity>))]
    public EquatableArray<Quantity> SuppliedResources { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Quantity>))]
    public EquatableArray<Quantity> AvailableResources { get; init; }
}

public sealed record QuestMonumentPiece
{
    public string? DefName { get; init; }

    public string? Stuff { get; init; }

    public Common.Cell? Offset { get; init; }

    public int? Rotation { get; init; }

    public bool? Built { get; init; }

    public bool? Queued { get; init; }

    public bool? Allowed { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public EquatableArray<Common.Cell> Footprint { get; init; }
}

public sealed record QuestWorker
{
    public string? PawnId { get; init; }

    public bool? HealthyAdult { get; init; }

    public bool? CanFight { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<QuestWorkRate>))]
    public EquatableArray<QuestWorkRate> Rates { get; init; }

    public double? CarryCapacity { get; init; }

    public double? CarriedMass { get; init; }

    public int? SocialLevel { get; init; }

    public bool? FactionLeader { get; init; }

    public double? NegotiationAbility { get; init; }
}

public sealed record QuestWorkRate
{
    public string? Stat { get; init; }

    public double? Rate { get; init; }
}

public sealed record QuestMonumentResource
{
    public string? Id { get; init; }

    public string? DefName { get; init; }

    public Common.Cell? Cell { get; init; }

    public bool? InStorage { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> EligibleHaulers { get; init; }
}

public sealed record QuestShuttleState
{
    public string? ShuttleId { get; init; }

    public bool? AutoloadAvailable { get; init; }

    public bool? Autoload { get; init; }

    public bool? Loading { get; init; }

    public bool? AllRequiredLoaded { get; init; }

    public bool? ManualLaunchAvailable { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> PawnIds { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> LoadedPawnIds { get; init; }

    public int? RequiredColonistCount { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> PendingPawnIds { get; init; }
}

public sealed record QuestState
{
    public string? Id { get; init; }

    public string? Label { get; init; }

    public string? Description { get; init; }

    public Receipts.QuestStatus? State { get; init; }

    public long? AcceptedTick { get; init; }

    public long? ExpiresInTicks { get; init; }

    public bool? RequiresAccepter { get; init; }

    public bool? CanAccept { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Ref>))]
    public EquatableArray<Common.Ref> EligiblePawns { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<QuestTradeRequest>))]
    public EquatableArray<QuestTradeRequest> TradeRequests { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<QuestReward>))]
    public EquatableArray<QuestReward> Rewards { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }

    public SnapshotRef? Snapshot { get; init; }

    public string? ScriptDef { get; init; }

    public string? FactionId { get; init; }

    public int? MapId { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<QuestObjective>))]
    public EquatableArray<QuestObjective> Objectives { get; init; }

    public int? ChoicePartCount { get; init; }

    public string? AskerPawnId { get; init; }

    public bool? AskerFactionPlayer { get; init; }

    public bool? ViolentQuestsAllowed { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<QuestShuttleState>))]
    public EquatableArray<QuestShuttleState> Shuttles { get; init; }

    public double? ThreatPoints { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> DeparturePawnIds { get; init; }
}

public sealed record FactionState
{
    public string? Id { get; init; }

    public string? Label { get; init; }

    public bool? Player { get; init; }

    public bool? Hostile { get; init; }

    public int? Goodwill { get; init; }

    public string? Relation { get; init; }

    public SnapshotRef? Snapshot { get; init; }
}

public sealed record WorldMap
{
    public int? Id { get; init; }

    public int? Tile { get; init; }

    public bool? Home { get; init; }

    public string? Label { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnState>))]
    public EquatableArray<PawnState> Pawns { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ResourceStock>))]
    public EquatableArray<ResourceStock> StoredItems { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<QuestWorker>))]
    public EquatableArray<QuestWorker> QuestWorkers { get; init; }
}

public sealed record CaravanAssembly
{
    public string? Id { get; init; }

    public int? MapId { get; init; }

    public string? Status { get; init; }

    public bool? GatheringItems { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Ref>))]
    public EquatableArray<Common.Ref> Pawns { get; init; }
}

public sealed record WorldProgressionSnapshot
{
    public Common.ObservationContext? Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<WorldMap>))]
    public EquatableArray<WorldMap> Maps { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<FactionState>))]
    public EquatableArray<FactionState> Factions { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<CaravanState>))]
    public EquatableArray<CaravanState> Caravans { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<CaravanAssembly>))]
    public EquatableArray<CaravanAssembly> Assemblies { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<QuestState>))]
    public EquatableArray<QuestState> Quests { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<WorldSite>))]
    public EquatableArray<WorldSite> Sites { get; init; }
}

public sealed record WorldProgressionRequest
{
    public ReadScope? Scope { get; init; }

    public bool? IncludeStorage { get; init; }
}

public sealed record WorldProgressionReply
{
    public WorldProgressionReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="WorldProgressionReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(WorldProgressionReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(WorldProgressionReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(WorldProgressionReplyOutcome.FailureCase), "failure")]
public abstract record WorldProgressionReplyOutcome
{
    private protected WorldProgressionReplyOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public WorldProgressionSnapshot? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(WorldProgressionSnapshot Value) : WorldProgressionReplyOutcome;

    public static implicit operator WorldProgressionReplyOutcome(WorldProgressionSnapshot value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : WorldProgressionReplyOutcome;

    public static implicit operator WorldProgressionReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : WorldProgressionReplyOutcome;

    public static implicit operator WorldProgressionReplyOutcome(Common.Failure value) => new FailureCase(value);
}

/// <summary>
/// Mixed legacy tools get separate read requests: no action/dryRun write branch.
/// </summary>
public sealed record BillsSnapshot
{
    public Common.ObservationContext? Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<BillStackRow>))]
    public EquatableArray<BillStackRow> Benches { get; init; }
}

public sealed record BillsRequest
{
    public ReadScope? Scope { get; init; }

    public string? BenchId { get; init; }

    public bool? AllFactions { get; init; }
}

public sealed record BillsReply
{
    public BillsReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="BillsReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(BillsReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(BillsReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(BillsReplyOutcome.FailureCase), "failure")]
public abstract record BillsReplyOutcome
{
    private protected BillsReplyOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public BillsSnapshot? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(BillsSnapshot Value) : BillsReplyOutcome;

    public static implicit operator BillsReplyOutcome(BillsSnapshot value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : BillsReplyOutcome;

    public static implicit operator BillsReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : BillsReplyOutcome;

    public static implicit operator BillsReplyOutcome(Common.Failure value) => new FailureCase(value);
}

/// <summary>
/// bench_def: the bench's ThingDef, which the recipe rows are read against in the catalog.
/// </summary>
public sealed record RecipesSnapshot
{
    public Common.ObservationContext? Context { get; init; }

    public SnapshotRef? Snapshot { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<RecipeState>))]
    public EquatableArray<RecipeState> Recipes { get; init; }

    public string? BenchDef { get; init; }
}

/// <summary>
/// bench_id names the bench whose recipe availability is read; required.
/// </summary>
public sealed record RecipesRequest
{
    public ReadScope? Scope { get; init; }

    public string? BenchId { get; init; }
}

public sealed record RecipesReply
{
    public RecipesReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="RecipesReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RecipesReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(RecipesReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(RecipesReplyOutcome.FailureCase), "failure")]
public abstract record RecipesReplyOutcome
{
    private protected RecipesReplyOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public RecipesSnapshot? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(RecipesSnapshot Value) : RecipesReplyOutcome;

    public static implicit operator RecipesReplyOutcome(RecipesSnapshot value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : RecipesReplyOutcome;

    public static implicit operator RecipesReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : RecipesReplyOutcome;

    public static implicit operator RecipesReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record BuildingSettingsRequest
{
    public ReadScope? Scope { get; init; }

    public string? ThingId { get; init; }
}

public sealed record BuildingSettingsReply
{
    public BuildingSettingsReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="BuildingSettingsReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(BuildingSettingsReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(BuildingSettingsReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(BuildingSettingsReplyOutcome.FailureCase), "failure")]
public abstract record BuildingSettingsReplyOutcome
{
    private protected BuildingSettingsReplyOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public BuildingSettings? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(BuildingSettings Value) : BuildingSettingsReplyOutcome;

    public static implicit operator BuildingSettingsReplyOutcome(BuildingSettings value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : BuildingSettingsReplyOutcome;

    public static implicit operator BuildingSettingsReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : BuildingSettingsReplyOutcome;

    public static implicit operator BuildingSettingsReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record PawnSettingsRequest
{
    public ReadScope? Scope { get; init; }

    public string? PawnId { get; init; }
}

public sealed record PawnSettingsReply
{
    public PawnSettingsReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="PawnSettingsReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PawnSettingsReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(PawnSettingsReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(PawnSettingsReplyOutcome.FailureCase), "failure")]
public abstract record PawnSettingsReplyOutcome
{
    private protected PawnSettingsReplyOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public PawnSettings? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(PawnSettings Value) : PawnSettingsReplyOutcome;

    public static implicit operator PawnSettingsReplyOutcome(PawnSettings value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : PawnSettingsReplyOutcome;

    public static implicit operator PawnSettingsReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : PawnSettingsReplyOutcome;

    public static implicit operator PawnSettingsReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record ResolveTargetSnapshot
{
    public Common.ObservationContext? Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<TargetRef>))]
    public EquatableArray<TargetRef> Candidates { get; init; }

    public bool? Unique { get; init; }
}

public sealed record ResolveTargetRequest
{
    public ReadScope? Scope { get; init; }

    public ResolveTargetRequestQuery? Query { get; init; }
}

/// <summary>The <c>query</c> choice of <see cref="ResolveTargetRequest"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ResolveTargetRequestQuery.EntityIdCase), "entityId")]
[JsonDerivedType(typeof(ResolveTargetRequestQuery.ExactNameCase), "exactName")]
[JsonDerivedType(typeof(ResolveTargetRequestQuery.CellCase), "cell")]
public abstract record ResolveTargetRequestQuery
{
    private protected ResolveTargetRequestQuery() { }

    /// <summary>The <c>EntityId</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public string? EntityId => (this as EntityIdCase)?.Value;

    public sealed record EntityIdCase(string Value) : ResolveTargetRequestQuery;

    /// <summary>The <c>ExactName</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public string? ExactName => (this as ExactNameCase)?.Value;

    public sealed record ExactNameCase(string Value) : ResolveTargetRequestQuery;

    /// <summary>The <c>Cell</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Cell? Cell => (this as CellCase)?.Value;

    public sealed record CellCase(Common.Cell Value) : ResolveTargetRequestQuery;
}

public sealed record ResolveTargetReply
{
    public ResolveTargetReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="ResolveTargetReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ResolveTargetReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(ResolveTargetReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(ResolveTargetReplyOutcome.FailureCase), "failure")]
public abstract record ResolveTargetReplyOutcome
{
    private protected ResolveTargetReplyOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public ResolveTargetSnapshot? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(ResolveTargetSnapshot Value) : ResolveTargetReplyOutcome;

    public static implicit operator ResolveTargetReplyOutcome(ResolveTargetSnapshot value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : ResolveTargetReplyOutcome;

    public static implicit operator ResolveTargetReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : ResolveTargetReplyOutcome;

    public static implicit operator ResolveTargetReplyOutcome(Common.Failure value) => new FailureCase(value);
}

/// <summary>
/// EvaluateStat is the game's own stat evaluation for one subject: a definition
/// (with optional stuff and quality category), a thing, or a pawn. A definition
/// subject without quality is the live parity oracle of the Go evaluator. The oracle and
/// scaffolding of the Go stat evaluator (epic #2621); it carries no new action.
/// </summary>
public sealed record StatDefSubject
{
    public string DefName { get; init; } = "";

    public string? Stuff { get; init; }

    public int? Quality { get; init; }
}

public sealed record StatSubject
{
    public StatSubjectSubject? Subject { get; init; }
}

/// <summary>The <c>subject</c> choice of <see cref="StatSubject"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(StatSubjectSubject.DefinitionCase), "definition")]
[JsonDerivedType(typeof(StatSubjectSubject.ThingIdCase), "thingId")]
[JsonDerivedType(typeof(StatSubjectSubject.PawnIdCase), "pawnId")]
public abstract record StatSubjectSubject
{
    private protected StatSubjectSubject() { }

    /// <summary>The <c>Definition</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public StatDefSubject? Definition => (this as DefinitionCase)?.Value;

    public sealed record DefinitionCase(StatDefSubject Value) : StatSubjectSubject;

    /// <summary>The <c>ThingId</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public string? ThingId => (this as ThingIdCase)?.Value;

    public sealed record ThingIdCase(string Value) : StatSubjectSubject;

    /// <summary>The <c>PawnId</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public string? PawnId => (this as PawnIdCase)?.Value;

    public sealed record PawnIdCase(string Value) : StatSubjectSubject;
}

/// <summary>
/// value is the final stat value (StatWorker.GetValue); explanation_lines are
/// the game's per-part breakdown before final adjustment
/// (GetExplanationUnfinalized), one line per entry; shown is
/// StatWorker.ShouldShowFor for the subject. A stat that is not shown still
/// carries its value.
/// </summary>
public sealed record StatEvaluation
{
    public Common.ObservationContext? Context { get; init; }

    public string Stat { get; init; } = "";

    public double Value { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> ExplanationLines { get; init; }

    public bool Shown { get; init; }
}

public sealed record EvaluateStatRequest
{
    public ReadScope? Scope { get; init; }

    public string Stat { get; init; } = "";

    public StatSubject? Subject { get; init; }
}

/// <summary>
/// An unknown stat or subject is a NOT_FOUND failure; an absent subject or an
/// invalid quality is INVALID_REQUEST.
/// </summary>
public sealed record EvaluateStatReply
{
    public EvaluateStatReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="EvaluateStatReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(EvaluateStatReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(EvaluateStatReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(EvaluateStatReplyOutcome.FailureCase), "failure")]
public abstract record EvaluateStatReplyOutcome
{
    private protected EvaluateStatReplyOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public StatEvaluation? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(StatEvaluation Value) : EvaluateStatReplyOutcome;

    public static implicit operator EvaluateStatReplyOutcome(StatEvaluation value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : EvaluateStatReplyOutcome;

    public static implicit operator EvaluateStatReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : EvaluateStatReplyOutcome;

    public static implicit operator EvaluateStatReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record GearCandidate
{
    public GearItem? Item { get; init; }

    public double? Gain { get; init; }

    public string? Reason { get; init; }

    public string? Blocker { get; init; }
}

/// <summary>
/// candidates are every eligible loose item, best gain first (then thing id).
/// The pawn's wear inputs (gender, developmental_stage, body_part_groups) are
/// what the definition catalog's apparel rows cannot say: Go applies the wear
/// filter (gender, stage, body part groups) from those rows. developmental_stage
/// is the pawn's single DevelopmentalStage (the apparel filter's flag bit);
/// body_part_groups are the BodyPartGroupDef names for which the pawn has a
/// part that is not missing.
/// </summary>
public sealed record GearLoadout
{
    public SnapshotRef? Snapshot { get; init; }

    public Common.Ref? Pawn { get; init; }

    public PawnEquipment? Equipment { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<GearCandidate>))]
    public EquatableArray<GearCandidate> Candidates { get; init; }

    public double? ComfortableMinC { get; init; }

    public double? ComfortableMaxC { get; init; }

    public string? Blocker { get; init; }

    public ApparelPolicyState? ApparelPolicy { get; init; }

    public GearLoadoutModel? LoadoutModel { get; init; }

    public Defs.Gender? Gender { get; init; }

    public Defs.DevelopmentalStage? DevelopmentalStage { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> BodyPartGroups { get; init; }
}

/// <summary>
/// One apparel option the Go loadout model may choose. Layers, body part
/// groups and the def x stuff stats come from the catalog (def rows and stat
/// table); the option carries the instance: id is the thing id for
/// worn/loose/stored items, bill:&lt;def&gt;/&lt;stuff&gt; for a producible definition.
/// source is worn, loose, stored or bill. A bill option's recipe research and
/// ingredients and the def's smoke-pop verb are Go rules over the catalog rows.
/// </summary>
public sealed record GearLoadoutOption
{
    public string? Id { get; init; }

    public string? DefName { get; init; }

    public string? Stuff { get; init; }

    public int? Quality { get; init; }

    public string? Source { get; init; }

    public double? Condition { get; init; }

    public bool? Tainted { get; init; }

    public bool? Locked { get; init; }
}

/// <summary>
/// The loadout model's per-pawn inputs: present only when the producer read the
/// whole catalog. worn is every worn garment; options are the eligible loose or
/// stored candidates (best native gain first) and the producible definitions
/// whose recipe research is finished, one stuff each, at most 64 in total.
/// </summary>
public sealed record GearLoadoutModel
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<Trait>))]
    public EquatableArray<Trait> Traits { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<GearLoadoutOption>))]
    public EquatableArray<GearLoadoutOption> Worn { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<GearLoadoutOption>))]
    public EquatableArray<GearLoadoutOption> Options { get; init; }
}

/// <summary>
/// Temperatures are indexed by native Twelfth (0..11), in degrees Celsius.
/// Absent climate fields retain ambient-only planning for older producers.
/// </summary>
public sealed record GearSnapshot
{
    public Common.ObservationContext? Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<GearLoadout>))]
    public EquatableArray<GearLoadout> Pawns { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<float>))]
    public EquatableArray<float> OutdoorTemperatureByTwelfthC { get; init; }

    public uint? CurrentTwelfth { get; init; }

    public int? TicksToNextTwelfth { get; init; }

    public GearWeatherCondition? ActiveWeather { get; init; }

    public GearStorage? StoredApparel { get; init; }
}

/// <summary>
/// Aggregate only usable, untainted, unforbidden apparel in valid storage.
/// hp_band is floor(condition * 10), clamped to 0..9; band 5 is &gt;50%.
/// </summary>
public sealed record GearStock
{
    public string? DefName { get; init; }

    public string? Stuff { get; init; }

    public int? Quality { get; init; }

    public int? HpBand { get; init; }

    public int? Count { get; init; }
}

public sealed record GearStorage
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<GearStock>))]
    public EquatableArray<GearStock> Rows { get; init; }
}

public sealed record GearWeatherCondition
{
    /// <summary>
    /// ColdSnap or HeatWave
    /// </summary>
    public string? DefName { get; init; }

    /// <summary>
    /// -1 denotes permanent
    /// </summary>
    public long? RemainingTicks { get; init; }

    /// <summary>
    /// Native condition offset, not ambient.
    /// </summary>
    public float? TemperatureOffsetC { get; init; }
}

public sealed record GearRequest
{
    public ReadScope? Scope { get; init; }

    public string? PawnId { get; init; }
}

public sealed record GearReply
{
    public GearReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="GearReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(GearReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(GearReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(GearReplyOutcome.FailureCase), "failure")]
public abstract record GearReplyOutcome
{
    private protected GearReplyOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public GearSnapshot? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(GearSnapshot Value) : GearReplyOutcome;

    public static implicit operator GearReplyOutcome(GearSnapshot value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : GearReplyOutcome;

    public static implicit operator GearReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : GearReplyOutcome;

    public static implicit operator GearReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record Trader
{
    [JsonPropertyName("trader")]
    public EntityRef? Entity { get; init; }

    public string? Kind { get; init; }

    public Common.Ref? Faction { get; init; }

    public long? TicksUntilDeparture { get; init; }

    public bool? CanTrade { get; init; }

    public string? Reason { get; init; }

    public bool? Orbital { get; init; }

    public uint? GoodsStacks { get; init; }

    public bool? Travelling { get; init; }

    public SnapshotRef? TraderSnapshot { get; init; }
}

public sealed record TradersSnapshot
{
    public Common.ObservationContext? Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Trader>))]
    public EquatableArray<Trader> Traders { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Ref>))]
    public EquatableArray<Common.Ref> Negotiators { get; init; }
}

public sealed record TradersRequest
{
    public ReadScope? Scope { get; init; }
}

public sealed record TradersReply
{
    public TradersReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="TradersReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TradersReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(TradersReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(TradersReplyOutcome.FailureCase), "failure")]
public abstract record TradersReplyOutcome
{
    private protected TradersReplyOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public TradersSnapshot? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(TradersSnapshot Value) : TradersReplyOutcome;

    public static implicit operator TradersReplyOutcome(TradersSnapshot value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : TradersReplyOutcome;

    public static implicit operator TradersReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : TradersReplyOutcome;

    public static implicit operator TradersReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record TradeLine
{
    public uint? Index { get; init; }

    public DefinitionRef? Definition { get; init; }

    public string? Stuff { get; init; }

    public long? ColonyCount { get; init; }

    public long? TraderCount { get; init; }

    public double? BuyPrice { get; init; }

    public double? SellPrice { get; init; }

    public Defs.PriceType? BuyPriceType { get; init; }

    public Defs.PriceType? SellPriceType { get; init; }

    public double? MarketValue { get; init; }

    public bool? TraderWillTrade { get; init; }

    public bool? Currency { get; init; }

    public bool? Pawn { get; init; }

    public string? PawnDescription { get; init; }

    public long? TransferCount { get; init; }

    public long? MinimumCount { get; init; }

    public long? MaximumCount { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> CannotSellReasons { get; init; }

    public string? LineId { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Skill>))]
    public EquatableArray<Skill> Skills { get; init; }

    public bool? ViolenceCapable { get; init; }

    public string? PawnId { get; init; }

    public string? ThingId { get; init; }

    public int? Quality { get; init; }

    public string? PawnGender { get; init; }

    public double? HitPointsFraction { get; init; }

    public string? ZoneId { get; init; }

    public bool? Favor { get; init; }

    public string? GuestStatus { get; init; }

    public bool? PrisonerSecure { get; init; }

    public bool? PawnDowned { get; init; }

    public Common.Ref? ExtraHomeFaction { get; init; }

    public Common.Ref? ExtraHostFaction { get; init; }
}

/// <summary>
/// TradeSession is the adapter's one live trade: the negotiator
/// walking to trader_id to open a session (open false), or the pair holding
/// the open session (open true). Both ids are absent when neither exists.
/// </summary>
public sealed record TradeSession
{
    public Common.ObservationContext? Context { get; init; }

    public Common.TradeTarget? Target { get; init; }

    public string? NegotiatorId { get; init; }

    public bool? Open { get; init; }
}

public sealed record TradeSessionRequest
{
    public ReadScope? Scope { get; init; }
}

public sealed record TradeSessionReply
{
    public TradeSessionReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="TradeSessionReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TradeSessionReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(TradeSessionReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(TradeSessionReplyOutcome.FailureCase), "failure")]
public abstract record TradeSessionReplyOutcome
{
    private protected TradeSessionReplyOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public TradeSession? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(TradeSession Value) : TradeSessionReplyOutcome;

    public static implicit operator TradeSessionReplyOutcome(TradeSession value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : TradeSessionReplyOutcome;

    public static implicit operator TradeSessionReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : TradeSessionReplyOutcome;

    public static implicit operator TradeSessionReplyOutcome(Common.Failure value) => new FailureCase(value);
}

/// <summary>
/// currency_kind is the open session's currency (vanilla TradeCurrency): silver,
/// or the trader faction's royal favor (the Royalty tribute collector). In a favor
/// session every row's sell_price is that item's favor value, TradeLine.favor marks
/// the favor currency row (no ThingDef, so an empty definition; its transfer_count
/// is the favor the negotiator gains), and absent reads as silver.
/// </summary>
public sealed record TradeSheet
{
    public SnapshotRef? Snapshot { get; init; }

    public string? SessionId { get; init; }

    public Common.TradeTarget? Target { get; init; }

    public Common.Ref? Negotiator { get; init; }

    public bool? GiftMode { get; init; }

    public bool? NegotiatorAdjacent { get; init; }

    public bool? CanTradeNow { get; init; }

    public double? Balance { get; init; }

    public bool? ColonyCanAfford { get; init; }

    public bool? TraderHasEnoughSilver { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<TradeLine>))]
    public EquatableArray<TradeLine> Lines { get; init; }

    public string? DealSignature { get; init; }

    public Defs.TradeCurrency? CurrencyKind { get; init; }
}

public sealed record TradeSheetRequest
{
    public ReadScope? Scope { get; init; }

    public string? SessionId { get; init; }

    public bool? IncludeUntradeable { get; init; }

    public bool? OnlyChanged { get; init; }

    public string? NameContains { get; init; }
}

public sealed record TradeSheetReply
{
    public TradeSheetReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="TradeSheetReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TradeSheetReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(TradeSheetReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(TradeSheetReplyOutcome.FailureCase), "failure")]
public abstract record TradeSheetReplyOutcome
{
    private protected TradeSheetReplyOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public TradeSheet? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(TradeSheet Value) : TradeSheetReplyOutcome;

    public static implicit operator TradeSheetReplyOutcome(TradeSheet value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : TradeSheetReplyOutcome;

    public static implicit operator TradeSheetReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : TradeSheetReplyOutcome;

    public static implicit operator TradeSheetReplyOutcome(Common.Failure value) => new FailureCase(value);
}

/// <summary>
/// Complete read-only prerequisites, with absent scalars explicitly unknown.
/// </summary>
public sealed record TradeAcquisitionRequest
{
    public ReadScope? Scope { get; init; }

    public Operations.FormCaravanIntent? Pack { get; init; }
}

public sealed record TradeConsole
{
    public string? Id { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> NegotiatorIds { get; init; }
}

public sealed record PassingTradeShip
{
    public string? Id { get; init; }

    public bool? Trader { get; init; }

    public string? TraderKind { get; init; }

    public string? FactionId { get; init; }

    public bool? CanTrade { get; init; }

    public long? DepartureTick { get; init; }
}

public sealed record TradeRequestOption
{
    public string? FactionId { get; init; }

    public Common.TradeRequestKind Kind { get; init; }

    public string? TraderKind { get; init; }

    public int? Goodwill { get; init; }

    public int? GoodwillCost { get; init; }

    public string? RelationAfterPayment { get; init; }

    public long? LastRequestTick { get; init; }

    public long? CooldownRemainingTicks { get; init; }

    public long? ArrivalMinTicks { get; init; }

    public long? ArrivalMaxTicks { get; init; }

    public bool? Eligible { get; init; }

    public string? Reason { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> NegotiatorIds { get; init; }
}

public sealed record TradeAcquisition
{
    public Common.ObservationContext? Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<TradeConsole>))]
    public EquatableArray<TradeConsole> Consoles { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PassingTradeShip>))]
    public EquatableArray<PassingTradeShip> PassingShips { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<TradeRequestOption>))]
    public EquatableArray<TradeRequestOption> Requests { get; init; }

    public bool? OrbitalAvailable { get; init; }

    public TradePackEstimate? Pack { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<TradeRequestArrival>))]
    public EquatableArray<TradeRequestArrival> Arrivals { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<TradeCommsWork>))]
    public EquatableArray<TradeCommsWork> CommsWork { get; init; }
}

public sealed record TradeRequestArrival
{
    public string? FactionId { get; init; }

    public Common.TradeRequestKind Kind { get; init; }

    public string? TraderKind { get; init; }

    public long? ArrivalTick { get; init; }

    public long? RetryTicks { get; init; }

    public bool? TriedToFire { get; init; }
}

public sealed record TradeCommsWork
{
    public string? NegotiatorId { get; init; }

    public string? ConsoleId { get; init; }

    public string? FactionId { get; init; }

    public int? JobId { get; init; }
}

public sealed record TradePackEstimate
{
    public bool? CanPack { get; init; }

    public string? Reason { get; init; }

    public double? MassUsage { get; init; }

    public double? MassCapacity { get; init; }

    public double? FoodDays { get; init; }

    public double? FoodRotDays { get; init; }

    public WorldRoute? Outbound { get; init; }

    public WorldRoute? Home { get; init; }
}

public sealed record TradeAcquisitionReply
{
    public TradeAcquisitionReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="TradeAcquisitionReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TradeAcquisitionReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(TradeAcquisitionReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(TradeAcquisitionReplyOutcome.FailureCase), "failure")]
public abstract record TradeAcquisitionReplyOutcome
{
    private protected TradeAcquisitionReplyOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public TradeAcquisition? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(TradeAcquisition Value) : TradeAcquisitionReplyOutcome;

    public static implicit operator TradeAcquisitionReplyOutcome(TradeAcquisition value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : TradeAcquisitionReplyOutcome;

    public static implicit operator TradeAcquisitionReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : TradeAcquisitionReplyOutcome;

    public static implicit operator TradeAcquisitionReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record FoodConsumer
{
    public string? PawnId { get; init; }

    public double? NutritionPerDay { get; init; }

    public bool? HumanMeatAcceptable { get; init; }
}

/// <summary>
/// A food stock references its thing's row in the bundle's things table
/// and adds what is relative to its eaters: holder when only
/// that pawn eats it, the nutrition the stack gives them and who can eat it
/// (the row's forbidden flag is the stack's; Go decides which forbidden food
/// is the travel reserve).
/// </summary>
public sealed record FoodStock
{
    public Common.Ref? Item { get; init; }

    public Common.Ref? Holder { get; init; }

    public double? Nutrition { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Ref>))]
    public EquatableArray<Common.Ref> Eaters { get; init; }
}

/// <summary>
/// in_storage is whether the corpse lies in a stockpile (false: in the field).
/// </summary>
public sealed record CorpseHandling
{
    public string StockId { get; init; } = "";

    public Common.Cell? Cell { get; init; }

    public string? HaulerId { get; init; }

    public bool FrozenDestination { get; init; }

    public bool? InStorage { get; init; }
}

public sealed record FoodLarderFacts
{
    public double RawMeatNutrition { get; init; }

    public double CookDemandNutrition { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<CorpseHandling>))]
    public EquatableArray<CorpseHandling> Corpses { get; init; }
}

public sealed record FoodSupplyFacts
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<FoodConsumer>))]
    public EquatableArray<FoodConsumer> Consumers { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<FoodStock>))]
    public EquatableArray<FoodStock> Stocks { get; init; }

    public FoodLarderFacts? Larder { get; init; }
}

public sealed record CropForecast
{
    public Common.Ref? Zone { get; init; }

    public string? Crop { get; init; }

    public double? SowWork { get; init; }

    public double? HarvestWork { get; init; }

    public uint? MaturePlants { get; init; }

    public uint? StalledPlants { get; init; }

    public double? StandingYield { get; init; }

    public string? Product { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }
}

public sealed record PatientForecast
{
    public string? PawnId { get; init; }

    public double? BleedRatePerDay { get; init; }

    public double? HoursUntilDeathFromBloodLoss { get; init; }

    public double? Mood { get; init; }

    public double? MoodTarget { get; init; }

    public double? MinorBreakThreshold { get; init; }

    public double? MajorBreakThreshold { get; init; }

    public double? ExtremeBreakThreshold { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }
}

public sealed record ForecastFacts
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> AnimalIds { get; init; }

    public FoodSupplyFacts? CombinedFoodSupply { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<CropForecast>))]
    public EquatableArray<CropForecast> Crops { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PatientForecast>))]
    public EquatableArray<PatientForecast> Patients { get; init; }
}

public sealed record ComfortSurface
{
    public string? Id { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public EquatableArray<Common.Cell> Adjacent { get; init; }

    public Common.Ref? Room { get; init; }
}

public sealed record ComfortFacility
{
    public string? Id { get; init; }

    public string? Kind { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Ref>))]
    public EquatableArray<Common.Ref> AccessibleTo { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Ref>))]
    public EquatableArray<Common.Ref> Users { get; init; }

    public Common.Ref? Room { get; init; }
}

/// <summary>
/// Complete building-backed kind census. Parallel tolerance/bored arrays follow
/// kinds; pawns without a joy need are omitted. Absent census means unavailable.
/// </summary>
public sealed record JoyTolerance
{
    public string Pawn { get; init; } = "";

    [JsonConverter(typeof(EquatableArrayJsonConverter<double>))]
    public EquatableArray<double> Tolerance { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<bool>))]
    public EquatableArray<bool> Bored { get; init; }
}

public sealed record RecreationCensus
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Kinds { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<JoyTolerance>))]
    public EquatableArray<JoyTolerance> Pawns { get; init; }
}

public sealed record ComfortFacts
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> People { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ComfortSurface>))]
    public EquatableArray<ComfortSurface> Surfaces { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ComfortFacility>))]
    public EquatableArray<ComfortFacility> Dining { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ComfortFacility>))]
    public EquatableArray<ComfortFacility> Recreation { get; init; }

    public RecreationCensus? Joy { get; init; }
}

public sealed record UpkeepItem
{
    public Common.Ref? Item { get; init; }

    public long? Count { get; init; }

    public bool? Roofed { get; init; }

    public bool? InStorage { get; init; }

    public long? RotTicks { get; init; }

    public double? TemperatureC { get; init; }

    public bool? Forbidden { get; init; }

    public bool? Burning { get; init; }
}

public sealed record UpkeepBed
{
    public Common.Ref? Bed { get; init; }

    public bool? Medical { get; init; }

    public bool? Prisoners { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Ref>))]
    public EquatableArray<Common.Ref> Owners { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Ref>))]
    public EquatableArray<Common.Ref> Users { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Ref>))]
    public EquatableArray<Common.Ref> AccessibleTo { get; init; }

    public bool? Roofed { get; init; }

    public double? TemperatureC { get; init; }

    public Common.Ref? Room { get; init; }

    public string? Quality { get; init; }

    public string? Stuff { get; init; }

    public bool? ForSlaves { get; init; }
}

public sealed record ItemStorageCapacity
{
    public string? ItemId { get; init; }

    public long? UnreservedCoveredCapacity { get; init; }

    public uint? AcceptingCells { get; init; }
}

public sealed record UpkeepStructure
{
    public Common.Ref? Building { get; init; }

    public bool? Home { get; init; }

    public bool? Roofed { get; init; }
}

public sealed record FireState
{
    public Common.Ref? Fire { get; init; }

    public double? Size { get; init; }

    public bool? Home { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Ref>))]
    public EquatableArray<Common.Ref> SafeWorkers { get; init; }
}

public sealed record FilthState
{
    public Common.Ref? Filth { get; init; }

    public bool? Home { get; init; }

    public uint? Thickness { get; init; }

    public string? RoomRole { get; init; }

    public bool? Cleanable { get; init; }

    public Common.Ref? Room { get; init; }
}

/// <summary>
/// partners: lover, spouse and fiance relations to living pawns on the same
/// map (native direct relations). bed_sharing_allowed: the native willingness
/// to share a bed with every listed partner (the SharedBed precepts; with no
/// partner, the plain SharedBed precept), true without Ideology. royal_title:
/// the most senior royal title's defName, absent without one or when Royalty is
/// inactive; its seniority and bedroom requirements are the RoyalTitleDef row,
/// less what the pawn's own state waives (ascetic trait; precepts is the
/// ideoligion's precept defNames, whose disablingPrecepts drop a requirement).
/// </summary>
public sealed record UpkeepPerson
{
    public Common.Ref? Pawn { get; init; }

    public Common.Ref? OwnedBed { get; init; }

    public double? ComfortableMinC { get; init; }

    public double? ComfortableMaxC { get; init; }

    public double? TemperatureC { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Ref>))]
    public EquatableArray<Common.Ref> Partners { get; init; }

    public bool? BedSharingAllowed { get; init; }

    public string? RoyalTitle { get; init; }

    public bool? Ascetic { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Precepts { get; init; }
}

/// <summary>
/// permit_cooldowns: one row per held permit.
/// last_used_tick is FactionPermit.LastUsedTick (absent when never used) and
/// cooldown_remaining_ticks the ticks until it can be used again (0 when ready),
/// both read at the read's tick.
/// </summary>
public sealed record PermitCooldown
{
    public string? Permit { get; init; }

    public int? LastUsedTick { get; init; }

    public int? CooldownRemainingTicks { get; init; }
}

public sealed record PawnRoyalHolding
{
    public string? FactionDef { get; init; }

    public string? Title { get; init; }

    public int? Favor { get; init; }

    public int? PermitPoints { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Permits { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PermitCooldown>))]
    public EquatableArray<PermitCooldown> PermitCooldowns { get; init; }
}

/// <summary>
/// Psycasts: psycasts holds each psycaster's known psycasts (Ability
/// defs with IsPsycast); the level, costs, cooldown and target kind are the
/// AbilityDef row. neuroformers: the psylink neuroformer
/// and each psycast neurotrainer, with held (unforbidden spawned stacks on the
/// player's home maps) and craftable (a recipe that is available now makes it);
/// what a trainer teaches and whether a trader sells it are its ThingDef row.
/// For combat casts cooldown_remaining_ticks is the
/// psycast's remaining cooldown and the pawn's psyfocus (0-1), entropy and
/// entropy_max are read at the read's tick; each is absent when unread.
/// </summary>
public sealed record PawnPsycast
{
    public string? DefName { get; init; }

    public int? CooldownRemainingTicks { get; init; }
}

public sealed record NeuroformerStock
{
    public string? DefName { get; init; }

    public int? Held { get; init; }

    public bool? Craftable { get; init; }
}

/// <summary>
/// PawnState.royalty: the pawn is the row's.
/// </summary>
public sealed record PawnRoyalty
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnRoyalHolding>))]
    public EquatableArray<PawnRoyalHolding> Holdings { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnPsycast>))]
    public EquatableArray<PawnPsycast> Psycasts { get; init; }

    public double? Psyfocus { get; init; }

    public double? Entropy { get; init; }

    public double? EntropyMax { get; init; }
}

/// <summary>
/// Pending bestowing ceremonies: one per colonist holding an ongoing or
/// offered bestowing-ceremony quest (RoyalTitleUtility.GetCurrentBestowingCeremonyQuest).
/// quest is the quest load id the quest census lists. pawn is the colonist to be bestowed, bestower the Empire pawn, title the def
/// the ceremony awards (GetTitleAwardedWhenUpdating), accepted whether the
/// quest is Ongoing (else NotYetAccepted), bestower_waiting whether the bestower
/// stands on the map in the lord's Wait toil (the ceremony starts only on the
/// player's command), started whether the ritual runs, spot the ceremony cell
/// (the throne spot) and attendees the lord's colonist participants. Each
/// scalar is absent when native could not read it.
/// </summary>
public sealed record BestowingCeremony
{
    public string? Quest { get; init; }

    public Common.Ref? Pawn { get; init; }

    public Common.Ref? Bestower { get; init; }

    public string? FactionDef { get; init; }

    public string? Title { get; init; }

    public bool? Accepted { get; init; }

    public bool? BestowerWaiting { get; init; }

    public bool? Started { get; init; }

    public Common.Cell? Spot { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Ref>))]
    public EquatableArray<Common.Ref> Attendees { get; init; }
}

/// <summary>
/// Throne ownership: thrones lists every spawned player throne
/// (Building_Throne) with its assigned owner; owner is absent when none is
/// assigned. A throne appears once it stands, so a throne missing from the
/// list was built after the read.
/// </summary>
public sealed record RoyalThrone
{
    public Common.Ref? Thing { get; init; }

    public string? DefName { get; init; }

    public Common.Ref? Owner { get; init; }
}

/// <summary>
/// Royalty colony facts, a keyed colony section: the neuroformer
/// stock, the pending bestowing ceremonies and the standing thrones. Absent
/// without Royalty.
/// </summary>
public sealed record RoyaltySection
{
    public RoyaltySectionOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="RoyaltySection"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RoyaltySectionOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(RoyaltySectionOutcome.UnavailableCase), "unavailable")]
public abstract record RoyaltySectionOutcome
{
    private protected RoyaltySectionOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public RoyaltyColonyFacts? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(RoyaltyColonyFacts Value) : RoyaltySectionOutcome;

    public static implicit operator RoyaltySectionOutcome(RoyaltyColonyFacts value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : RoyaltySectionOutcome;

    public static implicit operator RoyaltySectionOutcome(Common.Unavailable value) => new UnavailableCase(value);
}

public sealed record RoyaltyColonyFacts
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<NeuroformerStock>))]
    public EquatableArray<NeuroformerStock> Neuroformers { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<BestowingCeremony>))]
    public EquatableArray<BestowingCeremony> Ceremonies { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<RoyalThrone>))]
    public EquatableArray<RoyalThrone> Thrones { get; init; }
}

/// <summary>
/// pawn refers to the bundle pawn table row, whose animal_state
/// carries the herd facts.
/// </summary>
public sealed record AnimalFeed
{
    public Common.Ref? Pawn { get; init; }

    public bool? RequiresPen { get; init; }

    public Common.Ref? SuitablePen { get; init; }
}

public sealed record DevelopmentPower
{
    public Common.Ref? Building { get; init; }

    public double? StoredWattDays { get; init; }

    public bool? Roofed { get; init; }

    /// <summary>
    /// A turret gun's damage per second from its own verb and burst timing; absent for other consumers.
    /// </summary>
    public double? TurretDps { get; init; }
}

/// <summary>
/// One steam geyser on the map: geyser is the Building_SteamGeyser with its
/// anchor position, cells its 2x2 footprint, and occupied whether a harvester
/// (a geothermal generator, built or under construction) or any other
/// building already stands on it.
/// </summary>
public sealed record SteamGeyser
{
    public Common.Ref? Geyser { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public EquatableArray<Common.Cell> Cells { get; init; }

    public bool? Occupied { get; init; }
}

public sealed record DevelopmentFacts
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<DevelopmentPower>))]
    public EquatableArray<DevelopmentPower> Power { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PowerNetwork>))]
    public EquatableArray<PowerNetwork> Networks { get; init; }

    public long? ShortCircuitTick { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<SteamGeyser>))]
    public EquatableArray<SteamGeyser> Geysers { get; init; }
}

public sealed record EnvironmentCondition
{
    public string? Id { get; init; }

    public string? DefName { get; init; }

    public bool? Permanent { get; init; }

    public long? TicksLeft { get; init; }
}

/// <summary>
/// FoodClimate is the map tile's calendar for food planning: growing_days is
/// the tile's growing period per 60-day year, growing_days_remaining the days
/// until the seasonal temperature next leaves the crop growth range (60 while
/// it never does), growing_days_until the days until it next re-enters it (0
/// while crops grow now, 60 when the tile never grows), and non_growing_days
/// the length of the current or coming non-growing stretch on the same daily
/// walk (first out-of-range day to re-entry: 0 while it never leaves, 60 when
/// it never grows; equal to growing_days_until while crops do not grow).
/// season is the native Season name at the tile and day_of_year the 0-based
/// day of the 60-day year.
/// </summary>
public sealed record FoodClimate
{
    public double? GrowingDays { get; init; }

    public double? GrowingDaysRemaining { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }

    public bool? SowingNow { get; init; }

    public double? GrowingDaysUntil { get; init; }

    public string? Season { get; init; }

    public int? DayOfYear { get; init; }

    public double? NonGrowingDays { get; init; }
}

/// <summary>
/// FarmFacts mirrors one growing zone's raw state; policy computes the harvest
/// lead and the growing-cell count from it and the food climate calendar.
/// growth_* are the planted plants' growth fractions (0..1), fertility_factor_*
/// and light_factor_mean their native growth-rate factors, fertile_planted_cells
/// the planted cells whose fertility factor is above zero, and blighted_plants
/// the planted plants with blight. temperature is the map's outdoor temperature;
/// the crop's growth range and harvest nutrition are its PlantProperties row's.
/// edible_crop stays native's: whether every colonist WillEat the harvest.
/// </summary>
public sealed record FarmFacts
{
    public Common.Ref? Zone { get; init; }

    public string? Crop { get; init; }

    public uint? UsableCells { get; init; }

    public uint? PlantedCells { get; init; }

    public bool? EdibleCrop { get; init; }

    public bool? SowingNow { get; init; }

    public double? GrowthMin { get; init; }

    public double? GrowthMean { get; init; }

    public double? FertilityFactorMin { get; init; }

    public double? FertilityFactorMean { get; init; }

    public uint? FertilePlantedCells { get; init; }

    public double? LightFactorMean { get; init; }

    public uint? BlightedPlants { get; init; }

    public double? Temperature { get; init; }
}

/// <summary>
/// Controlled-environment growing facts within the planning region: sun lamps
/// with the exact cells they grow, plant growers (hydroponics) with their
/// crop cells and crop, roofed rooms with temperature, and per-network
/// power headroom split by source so night and outage margins are computable.
/// </summary>
public sealed record GrowLight
{
    public Common.Ref? Building { get; init; }

    public Common.Ref? Room { get; init; }

    public bool? Powered { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public EquatableArray<Common.Cell> GrowthCells { get; init; }

    public bool? LitNow { get; init; }

    public string? PowerNetId { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }
}

public sealed record PlantGrower
{
    public Common.Ref? Building { get; init; }

    public Common.Ref? Room { get; init; }

    public bool? Powered { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public EquatableArray<Common.Cell> PlantCells { get; init; }

    public string? CropDefName { get; init; }

    public bool? CanSow { get; init; }

    public string? PowerNetId { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }
}

public sealed record GrowRoom
{
    public Common.Ref? Room { get; init; }

    public double? TemperatureC { get; init; }

    public uint? CellCount { get; init; }

    public uint? OpenRoofCount { get; init; }

    public bool? ProperRoom { get; init; }

    public bool? PsychologicallyOutdoors { get; init; }

    public uint? LitCells { get; init; }
}

public sealed record PowerHeadroom
{
    public string? Id { get; init; }

    public double? GenerationW { get; init; }

    public double? SolarW { get; init; }

    public double? WindW { get; init; }

    public double? ConsumptionW { get; init; }

    public double? StoredWattDays { get; init; }

    public double? CapacityWattDays { get; init; }

    public bool? HasActiveSource { get; init; }
}

public sealed record ControlledEnvironment
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<GrowLight>))]
    public EquatableArray<GrowLight> Lights { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PlantGrower>))]
    public EquatableArray<PlantGrower> Growers { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<GrowRoom>))]
    public EquatableArray<GrowRoom> Rooms { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PowerHeadroom>))]
    public EquatableArray<PowerHeadroom> Networks { get; init; }

    public bool? Daylight { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }

    public string? Weather { get; init; }
}

/// <summary>
/// A sowable crop with an edible product: the map's per-day facts beside
/// the catalog's static row.
/// </summary>
public sealed record EdibleCrop
{
    public string? DefName { get; init; }

    public double? NutritionDemandPerDay { get; init; }

    /// <summary>
    /// No colonist ideology penalises this crop product.
    /// </summary>
    public bool? DietAllowed { get; init; }
}

/// <summary>
/// The definitions themselves are the definition catalog's; crops
/// lists every edible crop, sorted by name.
/// </summary>
public sealed record PlanningFacts
{
    public GearSnapshot? Gear { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }

    public ControlledEnvironment? Environment { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<EdibleCrop>))]
    public EquatableArray<EdibleCrop> Crops { get; init; }
}

/// <summary>
/// The static facts of a product (nutrition, rot days, perishability, baby
/// edibility) are its def row's in the definition catalog; the frame
/// carries what varies: count, whether a human can eat it, the colony's demand
/// and the storage room left.
/// </summary>
public sealed record FoodProduct
{
    public string? DefName { get; init; }

    public long? Count { get; init; }

    public bool? Edible { get; init; }

    public double? NutritionDemandPerDay { get; init; }

    /// <summary>
    /// storable is the product count roofed, reachable storage accepting it can
    /// hold: stored stacks plus empty stack slots. Caps reserve bill targets.
    /// </summary>
    public long? Storable { get; init; }
}

public sealed record FoodProduction
{
    public string? Recipe { get; init; }

    public bool? Available { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<FoodProduct>))]
    public EquatableArray<FoodProduct> Products { get; init; }
}

public sealed record CookingFacts
{
    public Common.Ref? Bench { get; init; }

    public bool? Usable { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<RecipeState>))]
    public EquatableArray<RecipeState> Recipes { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<BillState>))]
    public EquatableArray<BillState> Bills { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<FoodProduction>))]
    public EquatableArray<FoodProduction> Production { get; init; }

    public Common.Ref? Room { get; init; }

    /// <summary>
    /// auto_refuel is a refuelable bench's CompRefuelable.allowAutoRefuel (BuildingPatchIntent.auto_refuel).
    /// </summary>
    public bool? AutoRefuel { get; init; }

    /// <summary>
    /// The bench's CAS token.
    /// </summary>
    public SnapshotRef? BenchSnapshot { get; init; }
}

public sealed record AcquisitionFacts
{
    public Common.Ref? Source { get; init; }

    public SnapshotRef? SourceSnapshot { get; init; }

    public string? Resource { get; init; }

    public bool? Food { get; init; }

    /// <summary>
    /// plant rows only: every eater WillEat the harvest
    /// </summary>
    public double? Yield { get; init; }

    public bool? Designated { get; init; }

    public bool? Hunt { get; init; }

    public uint? HerdSize { get; init; }

    public bool? Downed { get; init; }

    public long? DesignatedTick { get; init; }

    public bool? Taken { get; init; }

    public bool? Sleeping { get; init; }

    /// <summary>
    /// herd_size counts same-race animals (the prey included) within the request's herd_radius;
    /// absent when the request carried no radius (unknown, never 0).
    /// Raw hunt prey facts: a hunt row is every wild animal that bears a corpse, whatever
    /// policy then decides. fogged and in_mental_state are native's current reading of the animal.
    /// </summary>
    public bool? Fogged { get; init; }

    public bool? InMentalState { get; init; }

    /// <summary>
    /// meat_amount is the live animal's MeatAmount stat (wounds, missing parts and malnutrition scale it);
    /// Go prices the meat off the race and meat rows and derives food, revenge chance, predator, melee-only and tree.
    /// </summary>
    public double? MeatAmount { get; init; }

    /// <summary>
    /// Plant rows: growth is the plant's growth fraction (0..1) on every plant row.
    /// plantation marks a sown tree of a harvest-destroys crop standing in a growing zone
    /// (RimWorld's growers fell it only at maturity; Go may fell it earlier); wild trees and
    /// crops in a zone never carry it.
    /// </summary>
    public double? Growth { get; init; }

    public bool? Plantation { get; init; }
}

/// <summary>
/// Raw hunt census: what Go needs to decide whether a hunt row is offered. Native states
/// facts and keeps only physical validity; butcher readiness, weapon rules, hunter eligibility and
/// the pending-hunt cap are Go policy (policy.HuntGate).
/// One free colonist. hunting_priority is the raw work priority (0 is off); hunting_active is
/// WorkIsActive; hunting_disabled is WorkTypeIsDisabled. weapon_def is the primary weapon def (absent for an unarmed pawn); Go reads its verbs and projectile off the ThingDef rows.
/// has_hunting_weapon and ranged_blocking_shield mirror WorkGiver_HunterHunt.HasHuntingWeapon and
/// HasShieldAndRangedWeapon. routes carries one HuntRoute per (hunt row, colonist) pair native
/// evaluated: every unfogged hunt row, pests first then nearest first, for a colonist with
/// Hunting active who is neither downed nor in a mental state, until the request's
/// hunt_route_budget_ms runs out. A pair with no row was not evaluated (the colonist is
/// ineligible or the prey is fogged); policy refuses those first.
/// reachable_benches lists the butcher benches the colonist can reach.
/// </summary>
public sealed record HunterFacts
{
    public string? PawnId { get; init; }

    public Common.Cell? Position { get; init; }

    public bool? Downed { get; init; }

    public bool? InMentalState { get; init; }

    public bool? Drafted { get; init; }

    public int? HuntingPriority { get; init; }

    public bool? HuntingActive { get; init; }

    public bool? HuntingDisabled { get; init; }

    public bool? CookingActive { get; init; }

    public string? WeaponDef { get; init; }

    public bool? HasHuntingWeapon { get; init; }

    public bool? RangedBlockingShield { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> ReachableBenches { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<HuntRoute>))]
    public EquatableArray<HuntRoute> Routes { get; init; }
}

/// <summary>
/// One colonist x hunt-row route verdict (HuntingSafety.RouteSafe, a pathing fact). Exactly one
/// of safe (the evaluated answer) and skipped (the budget ran out before this pair) is present.
/// </summary>
public sealed record HuntRoute
{
    public string? PreyId { get; init; }

    public bool? Safe { get; init; }

    public bool? Skipped { get; init; }
}

/// <summary>
/// A butcher-flesh bill on a bench; allowed_corpses are the corpse defs of the census's wild
/// animals the bill's ingredient filter allows; product_count is BillCommon.ProductCount for a
/// target-count bill.
/// </summary>
public sealed record HuntButcherBill
{
    public bool? Suspended { get; init; }

    public bool? Paused { get; init; }

    public Operations.RepeatMode? RepeatMode { get; init; }

    public int? RepeatCount { get; init; }

    public int? TargetCount { get; init; }

    public int? ProductCount { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> AllowedCorpses { get; init; }
}

public sealed record HuntButcherBench
{
    public string? BenchId { get; init; }

    public bool? Usable { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<HuntButcherBill>))]
    public EquatableArray<HuntButcherBill> Bills { get; init; }
}

public sealed record HuntCensus
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<HunterFacts>))]
    public EquatableArray<HunterFacts> Hunters { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<HuntButcherBench>))]
    public EquatableArray<HuntButcherBench> Benches { get; init; }
}

public sealed record ButcheringFacts
{
    public Common.Ref? Bench { get; init; }

    public SnapshotRef? BenchSnapshot { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<BillState>))]
    public EquatableArray<BillState> Bills { get; init; }

    public bool? Usable { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<RecipeState>))]
    public EquatableArray<RecipeState> Recipes { get; init; }

    public Common.Ref? Room { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<HumanButcherCandidate>))]
    public EquatableArray<HumanButcherCandidate> HumanButchers { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<HumanCorpseMeat>))]
    public EquatableArray<HumanCorpseMeat> HumanCorpseMeat { get; init; }
}

/// <summary>
/// The MeatAmount of the fresh humanlike corpses near a bench, summed per race; Go prices it off the race's meat def.
/// </summary>
public sealed record HumanCorpseMeat
{
    public string? Race { get; init; }

    public double? MeatAmount { get; init; }
}

public sealed record HumanButcherCandidate
{
    public string PawnId { get; init; } = "";

    public bool? CanWork { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Trait>))]
    public EquatableArray<Trait> PawnTraits { get; init; }
}

/// <summary>
/// One force-pausing Verse.Dialog_NodeTree the game opened by itself (an
/// incident's caravan demand/meeting, a quest dialog, a finished research
/// project's completion dialog). Options are the current node's DiaOptions in
/// native order; index is the exact native list position AnswerDialog names.
/// selectable is false for a disabled option, for a hyperlink row (never
/// closes the dialog) and for an option that neither closes the tree nor links
/// to a further node: its action opens another window the controller does not
/// drive (CaravanMeeting's Trade opening Dialog_Trade). resolves reports
/// whether the option closes the tree (DiaOption.resolveTree) rather than
/// moving to a linked node. keys are the language-neutral Keyed translation
/// keys whose active- or English-language text produces the label, sorted;
/// empty for a label no keyed entry yields (a mod's literal text, a quest's
/// composed choice).
/// </summary>
public sealed record ChoiceDialogOption
{
    public int? Index { get; init; }

    public string? Label { get; init; }

    public bool? Selectable { get; init; }

    public bool? Resolves { get; init; }

    public string? DisabledReason { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Keys { get; init; }
}

public sealed record ChoiceDialog
{
    public int? WindowId { get; init; }

    public string? WindowType { get; init; }

    public string? Title { get; init; }

    public string? Text { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ChoiceDialogOption>))]
    public EquatableArray<ChoiceDialogOption> Options { get; init; }

    public bool? Interactive { get; init; }
}

/// <summary>
/// Independently unavailable helper sections never masquerade as empty arrays.
/// </summary>
public sealed record ComfortSection
{
    public ComfortSectionOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="ComfortSection"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ComfortSectionOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(ComfortSectionOutcome.UnavailableCase), "unavailable")]
public abstract record ComfortSectionOutcome
{
    private protected ComfortSectionOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public ComfortFacts? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(ComfortFacts Value) : ComfortSectionOutcome;

    public static implicit operator ComfortSectionOutcome(ComfortFacts value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : ComfortSectionOutcome;

    public static implicit operator ComfortSectionOutcome(Common.Unavailable value) => new UnavailableCase(value);
}

public sealed record FoodSupplySection
{
    public FoodSupplySectionOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="FoodSupplySection"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(FoodSupplySectionOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(FoodSupplySectionOutcome.UnavailableCase), "unavailable")]
public abstract record FoodSupplySectionOutcome
{
    private protected FoodSupplySectionOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public FoodSupplyFacts? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(FoodSupplyFacts Value) : FoodSupplySectionOutcome;

    public static implicit operator FoodSupplySectionOutcome(FoodSupplyFacts value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : FoodSupplySectionOutcome;

    public static implicit operator FoodSupplySectionOutcome(Common.Unavailable value) => new UnavailableCase(value);
}

public sealed record ForecastSection
{
    public ForecastSectionOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="ForecastSection"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ForecastSectionOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(ForecastSectionOutcome.UnavailableCase), "unavailable")]
public abstract record ForecastSectionOutcome
{
    private protected ForecastSectionOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public ForecastFacts? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(ForecastFacts Value) : ForecastSectionOutcome;

    public static implicit operator ForecastSectionOutcome(ForecastFacts value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : ForecastSectionOutcome;

    public static implicit operator ForecastSectionOutcome(Common.Unavailable value) => new UnavailableCase(value);
}

public sealed record DevelopmentSection
{
    public DevelopmentSectionOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="DevelopmentSection"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(DevelopmentSectionOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(DevelopmentSectionOutcome.UnavailableCase), "unavailable")]
public abstract record DevelopmentSectionOutcome
{
    private protected DevelopmentSectionOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public DevelopmentFacts? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(DevelopmentFacts Value) : DevelopmentSectionOutcome;

    public static implicit operator DevelopmentSectionOutcome(DevelopmentFacts value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : DevelopmentSectionOutcome;

    public static implicit operator DevelopmentSectionOutcome(Common.Unavailable value) => new UnavailableCase(value);
}

public sealed record PlanningSection
{
    public PlanningSectionOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="PlanningSection"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PlanningSectionOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(PlanningSectionOutcome.UnavailableCase), "unavailable")]
public abstract record PlanningSectionOutcome
{
    private protected PlanningSectionOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public PlanningFacts? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(PlanningFacts Value) : PlanningSectionOutcome;

    public static implicit operator PlanningSectionOutcome(PlanningFacts value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : PlanningSectionOutcome;

    public static implicit operator PlanningSectionOutcome(Common.Unavailable value) => new UnavailableCase(value);
}

public sealed record WallRemovalRecord
{
    public string? Id { get; init; }

    public string? TargetId { get; init; }

    public bool? Complete { get; init; }

    public long? CompletedTick { get; init; }

    public string? Blocker { get; init; }

    public bool? TargetPresent { get; init; }

    public bool? Designated { get; init; }
}

public sealed record WallRemovalFacts
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<WallRemovalRecord>))]
    public EquatableArray<WallRemovalRecord> Records { get; init; }

    public SnapshotRef? Snapshot { get; init; }
}

public sealed record WallRemovalSection
{
    public WallRemovalSectionOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="WallRemovalSection"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(WallRemovalSectionOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(WallRemovalSectionOutcome.UnavailableCase), "unavailable")]
public abstract record WallRemovalSectionOutcome
{
    private protected WallRemovalSectionOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public WallRemovalFacts? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(WallRemovalFacts Value) : WallRemovalSectionOutcome;

    public static implicit operator WallRemovalSectionOutcome(WallRemovalFacts value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : WallRemovalSectionOutcome;

    public static implicit operator WallRemovalSectionOutcome(Common.Unavailable value) => new UnavailableCase(value);
}

/// <summary>
/// Present geometry is complete and independent of the bounded Home write batch.
/// zone is a stockpile target's full native footprint: colony extent
/// takes every stockpile from the census, not only zones the autopilot created.
/// </summary>
public sealed record HomeExtentGeometry
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public EquatableArray<Common.Cell> EnclosedInterior { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public EquatableArray<Common.Cell> Corridor { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public EquatableArray<Common.Cell> Zone { get; init; }
}

public sealed record HomeCoverageTarget
{
    public string? Id { get; init; }

    public string? ShapeToken { get; init; }

    public uint? MissingCells { get; init; }

    public uint? ExcludedCells { get; init; }

    public string? Blocker { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public EquatableArray<Common.Cell> Cells { get; init; }

    public SnapshotRef? Snapshot { get; init; }

    public HomeExtentGeometry? ExtentGeometry { get; init; }
}

public sealed record HomeCoverageFacts
{
    public long? Revision { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<HomeCoverageTarget>))]
    public EquatableArray<HomeCoverageTarget> Targets { get; init; }
}

public sealed record HomeCoverageSection
{
    public HomeCoverageSectionOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="HomeCoverageSection"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HomeCoverageSectionOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(HomeCoverageSectionOutcome.UnavailableCase), "unavailable")]
public abstract record HomeCoverageSectionOutcome
{
    private protected HomeCoverageSectionOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public HomeCoverageFacts? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(HomeCoverageFacts Value) : HomeCoverageSectionOutcome;

    public static implicit operator HomeCoverageSectionOutcome(HomeCoverageFacts value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : HomeCoverageSectionOutcome;

    public static implicit operator HomeCoverageSectionOutcome(Common.Unavailable value) => new UnavailableCase(value);
}

/// <summary>
/// Lighting facts: the measured ground glow at every colonist work bench
/// interaction cell plus the colony's glowing fixtures, so MaintainLighting
/// reasons from illumination at the cell a pawn actually stands on rather than
/// from fixture counts. Glow follows the native GlowGrid (0..1; RimWorld treats
/// &gt;= 0.3 as lit). A lamp is lit only when native currently emits light. A work
/// cell lists plant_defs: the defs of the plants standing in its proper room,
/// and of the plants its growing zones are set to. A lamp's radius is its
/// def's CompProperties_Glower row. Go reads PlantProperties.diesToLight off
/// each def (cave fungus): lighting such a room would kill the crop, so the cell
/// is never a lighting deficit.
/// </summary>
public sealed record WorkLightCell
{
    public Common.Ref? Bench { get; init; }

    public Common.Cell? Cell { get; init; }

    public double? Glow { get; init; }

    public bool? Roofed { get; init; }

    public Common.Ref? Room { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> PlantDefs { get; init; }
}

public sealed record LampState
{
    public Common.Ref? Building { get; init; }

    public bool? Lit { get; init; }

    public Common.Ref? Room { get; init; }
}

public sealed record LightingFacts
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<WorkLightCell>))]
    public EquatableArray<WorkLightCell> WorkCells { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<LampState>))]
    public EquatableArray<LampState> Lamps { get; init; }
}

public sealed record LightingSection
{
    public LightingSectionOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="LightingSection"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LightingSectionOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(LightingSectionOutcome.UnavailableCase), "unavailable")]
public abstract record LightingSectionOutcome
{
    private protected LightingSectionOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public LightingFacts? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(LightingFacts Value) : LightingSectionOutcome;

    public static implicit operator LightingSectionOutcome(LightingFacts value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : LightingSectionOutcome;

    public static implicit operator LightingSectionOutcome(Common.Unavailable value) => new UnavailableCase(value);
}

/// <summary>
/// Flooring facts: every proper indoor home room with the terrain under each
/// of its cells, plus one row per distinct terrain with the native stats
/// MaintainFlooring scores (cleanliness, path cost, beauty, flammability) and
/// whether the terrain is natural ground rather than a laid floor. A cell
/// whose floor is already ordered carries the pending floor definition so the
/// planner never doubles an open blueprint or frame.
/// </summary>
public sealed record FloorCell
{
    public Common.Cell? Cell { get; init; }

    public string? Terrain { get; init; }

    public string? Pending { get; init; }
}

/// <summary>
/// Room stats live on the bundle rooms census (RoomState.stats), not here.
/// </summary>
public sealed record FloorRoom
{
    public Common.Ref? Room { get; init; }

    public string? Role { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<FloorCell>))]
    public EquatableArray<FloorCell> Cells { get; init; }
}

/// <summary>
/// Every terrain a cell can stand on is a catalog TerrainDef row; the frame names none.
/// </summary>
public sealed record FlooringFacts
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<FloorRoom>))]
    public EquatableArray<FloorRoom> Rooms { get; init; }
}

public sealed record FlooringSection
{
    public FlooringSectionOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="FlooringSection"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(FlooringSectionOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(FlooringSectionOutcome.UnavailableCase), "unavailable")]
public abstract record FlooringSectionOutcome
{
    private protected FlooringSectionOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public FlooringFacts? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(FlooringFacts Value) : FlooringSectionOutcome;

    public static implicit operator FlooringSectionOutcome(FlooringFacts value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : FlooringSectionOutcome;

    public static implicit operator FlooringSectionOutcome(Common.Unavailable value) => new UnavailableCase(value);
}

/// <summary>
/// path_skipped: the pair is reachable but its path was not measured because the
/// stream's route_path_budget_ms ran out; path_cost, path_cells and the ordered
/// doors the path crosses are then unknown, not absent.
/// </summary>
public sealed record RouteTravel
{
    public string? PawnId { get; init; }

    public bool? Reachable { get; init; }

    public int? PathCost { get; init; }

    public int? PathCells { get; init; }

    public bool? PathSkipped { get; init; }
}

public sealed record RouteBreach
{
    public Common.Cell? Cell { get; init; }

    public string? Edifice { get; init; }

    public string? Pending { get; init; }

    public int? Distance { get; init; }
}

public sealed record RouteFacility
{
    public Common.Ref? Facility { get; init; }

    public RouteFacilityKind? Kind { get; init; }

    public Common.Cell? Cell { get; init; }

    public Common.Ref? Room { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<RouteTravel>))]
    public EquatableArray<RouteTravel> Travel { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<RouteBreach>))]
    public EquatableArray<RouteBreach> Breaches { get; init; }
}

public sealed record TrafficCell
{
    public Common.Cell? Cell { get; init; }

    public uint? Samples { get; init; }

    public string? Terrain { get; init; }

    public bool? Home { get; init; }

    public string? Pending { get; init; }

    public TrafficLayer Layer { get; init; }
}

public sealed record RoutesFacts
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<RouteFacility>))]
    public EquatableArray<RouteFacility> Facilities { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> PawnIds { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<TrafficCell>))]
    public EquatableArray<TrafficCell> Traffic { get; init; }

    public uint? TrafficSamples { get; init; }

    public int? TrafficSinceTick { get; init; }
}

public sealed record RoutesSection
{
    public RoutesSectionOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="RoutesSection"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RoutesSectionOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(RoutesSectionOutcome.UnavailableCase), "unavailable")]
public abstract record RoutesSectionOutcome
{
    private protected RoutesSectionOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public RoutesFacts? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(RoutesFacts Value) : RoutesSectionOutcome;

    public static implicit operator RoutesSectionOutcome(RoutesFacts value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : RoutesSectionOutcome;

    public static implicit operator RoutesSectionOutcome(Common.Unavailable value) => new UnavailableCase(value);
}

public sealed record UpkeepFacts
{
    /// <summary>
    /// storage_cells (5) and protected_cells (10) were never read.
    /// </summary>
    public ComfortSection? Comfort { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<UpkeepItem>))]
    public EquatableArray<UpkeepItem> Items { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<UpkeepBed>))]
    public EquatableArray<UpkeepBed> Beds { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ItemStorageCapacity>))]
    public EquatableArray<ItemStorageCapacity> StorageCapacity { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<UpkeepStructure>))]
    public EquatableArray<UpkeepStructure> Structures { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<FireState>))]
    public EquatableArray<FireState> Fires { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<FilthState>))]
    public EquatableArray<FilthState> Filth { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<UpkeepPerson>))]
    public EquatableArray<UpkeepPerson> People { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<AnimalFeed>))]
    public EquatableArray<AnimalFeed> Animals { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }

    public WallRemovalSection? WallRemoval { get; init; }

    public HomeCoverageSection? HomeCoverage { get; init; }

    public LightingSection? Lighting { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<AnimalFeed>))]
    public EquatableArray<AnimalFeed> WildAnimals { get; init; }

    public FlooringSection? Flooring { get; init; }

    public RoutesSection? Routes { get; init; }

    /// <summary>
    /// rooms: the bundle rooms census
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<UpkeepPerson>))]
    public EquatableArray<UpkeepPerson> Slaves { get; init; }

    /// <summary>
    /// Find.PlaySettings.autoHomeArea: the game expands the home area around
    /// new player buildings while true (AutoHomeAreaIntent).
    /// </summary>
    public bool? AutoHomeArea { get; init; }

    /// <summary>
    /// Every cell of the map's home area, unordered; the home-area
    /// planner diffs its target against it.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public EquatableArray<Common.Cell> HomeCells { get; init; }

    /// <summary>
    /// Hosted guests (HostFaction == player, quest lodgers included):
    /// people the colony houses but does not own, in the sleeping census.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<UpkeepPerson>))]
    public EquatableArray<UpkeepPerson> Guests { get; init; }
}

public sealed record UpkeepSection
{
    public UpkeepSectionOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="UpkeepSection"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(UpkeepSectionOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(UpkeepSectionOutcome.UnavailableCase), "unavailable")]
public abstract record UpkeepSectionOutcome
{
    private protected UpkeepSectionOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public UpkeepFacts? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(UpkeepFacts Value) : UpkeepSectionOutcome;

    public static implicit operator UpkeepSectionOutcome(UpkeepFacts value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : UpkeepSectionOutcome;

    public static implicit operator UpkeepSectionOutcome(Common.Unavailable value) => new UnavailableCase(value);
}

/// <summary>
/// Threat facts: the colony wealth the storyteller scales raids by
/// and the raid points a default threat incident would draw right now.
/// wealth_* are WealthWatcher's lazy recount (items, buildings, pawns,
/// total); storyteller_wealth is the map's PlayerWealthForStoryteller;
/// raid_points is StorytellerUtility.DefaultThreatPointsNow for the map,
/// already scaled by adaptation_factor (the story watcher's
/// TotalThreatPointsFactor) and difficulty_threat_scale. Read-only.
/// </summary>
public sealed record ThreatFacts
{
    public double? WealthItems { get; init; }

    public double? WealthBuildings { get; init; }

    public double? WealthPawns { get; init; }

    public double? WealthTotal { get; init; }

    public double? StorytellerWealth { get; init; }

    public double? RaidPoints { get; init; }

    public double? AdaptationFactor { get; init; }

    public double? DifficultyThreatScale { get; init; }

    public uint? ColonistCount { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }

    /// <summary>
    /// The hostile lords seen on the map since load (RaidArrivalState): the defense layout ranks the approaches by where ground raids crossed its census.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<RaidTrack>))]
    public EquatableArray<RaidTrack> Raids { get; init; }
}

public sealed record ThreatSection
{
    public ThreatSectionOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="ThreatSection"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ThreatSectionOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(ThreatSectionOutcome.UnavailableCase), "unavailable")]
public abstract record ThreatSectionOutcome
{
    private protected ThreatSectionOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public ThreatFacts? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(ThreatFacts Value) : ThreatSectionOutcome;

    public static implicit operator ThreatSectionOutcome(ThreatFacts value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : ThreatSectionOutcome;

    public static implicit operator ThreatSectionOutcome(Common.Unavailable value) => new UnavailableCase(value);
}

/// <summary>
/// Current autonomous hauling safety for visible haulable items. count is the
/// stack size; path_length is the shortest observed safe colonist route to the
/// item in cells (present only with safe_to_haul true); storage_headroom is the
/// item units the player's accepting storage can still take for this def.
/// </summary>
public sealed record LootItem
{
    public Common.Ref? Item { get; init; }

    public bool? Forbidden { get; init; }

    public bool? SafeToHaul { get; init; }

    public long? Count { get; init; }

    public double? PathLength { get; init; }

    public long? StorageHeadroom { get; init; }
}

public sealed record LootCensus
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<LootItem>))]
    public EquatableArray<LootItem> Items { get; init; }
}

public sealed record LootSection
{
    public LootSectionOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="LootSection"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LootSectionOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(LootSectionOutcome.UnavailableCase), "unavailable")]
public abstract record LootSectionOutcome
{
    private protected LootSectionOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public LootCensus? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(LootCensus Value) : LootSectionOutcome;

    public static implicit operator LootSectionOutcome(LootCensus value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : LootSectionOutcome;

    public static implicit operator LootSectionOutcome(Common.Unavailable value) => new UnavailableCase(value);
}

/// <summary>
/// Food source potential, independent of acquisition instances and policy.
/// Animal rows cover every tame animal: absent components leave their scalars
/// absent. Water rows aggregate fishable cells by native water body; population
/// is shared by that body, not multiplied by its shallow cells. FishableWater
/// is absent without Odyssey. Pollution uses the clipped center +/-22 window.
/// Raw fishing facts; policy chooses the footprint, verdicts and research lead.
/// cells are the water body's fishable cells (vanilla's Designator_ZoneAdd_Fishing
/// rule) with whether any fisher can reach and is not forbidden from each.
/// reachable is any fisher reaching any non-fogged body cell; frozen is every such
/// cell being thin ice. yield_curve_value is the population-to-yield curve at
/// the body's population; zones are the fishing zones touching the body.
/// </summary>
public sealed record FishableCell
{
    public Common.Cell? Cell { get; init; }

    public bool? Reachable { get; init; }

    public double? DistanceSquared { get; init; }
}

public sealed record FishingZoneFacts
{
    public bool? Allowed { get; init; }

    public bool? DoForever { get; init; }

    public double? TargetPopulationPct { get; init; }

    public bool? HasFishableCells { get; init; }
}

public sealed record FishableRegion
{
    public Common.Cell? Root { get; init; }

    public double? Population { get; init; }

    public double? MaxPopulation { get; init; }

    public bool? Zoned { get; init; }

    public uint? CellCount { get; init; }

    public bool? Reachable { get; init; }

    public bool? Frozen { get; init; }

    public double? NutritionPerFish { get; init; }

    public double? FishPerBatch { get; init; }

    public double? WorkTicksPerBatch { get; init; }

    public double? NearestDistanceSquared { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<FishableCell>))]
    public EquatableArray<FishableCell> Cells { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<FishingZoneFacts>))]
    public EquatableArray<FishingZoneFacts> Zones { get; init; }

    public double? YieldCurveValue { get; init; }
}

/// <summary>
/// fishers are the colonists able to fish; researcher_speeds the ResearchSpeed of
/// colonists able to research. Research lead in days is
/// max(0, base_cost - progress) * cost_factor / (max speed * points_per_work_tick
/// * difficulty_speed_factor * work ticks per day).
/// </summary>
public sealed record FisherFacts
{
    public double? FishingYield { get; init; }

    public double? FishingSpeed { get; init; }
}

public sealed record FishableWater
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<FishableRegion>))]
    public EquatableArray<FishableRegion> Regions { get; init; }

    public bool? FishingResearched { get; init; }

    public double? ResearchBaseCost { get; init; }

    public double? ResearchProgress { get; init; }

    public double? ResearchCostFactor { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<double>))]
    public EquatableArray<double> ResearcherSpeeds { get; init; }

    public double? ResearchPointsPerWorkTick { get; init; }

    public double? ResearchDifficultySpeedFactor { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<FisherFacts>))]
    public EquatableArray<FisherFacts> Fishers { get; init; }

    public double? BaseFishingDurationTicks { get; init; }
}

public sealed record GatherableAnimal
{
    public string? PawnId { get; init; }

    public string? Race { get; init; }

    public double? Fullness { get; init; }

    public string? Resource { get; init; }

    public bool? HandlerReachable { get; init; }

    public double? NutritionPerDay { get; init; }

    public double? WorkPerDay { get; init; }

    public double? LeadDays { get; init; }

    public bool? Active { get; init; }
}

public sealed record EggLayerAnimal
{
    public string? PawnId { get; init; }

    public string? Race { get; init; }

    public bool? CanLayNow { get; init; }

    public double? Progress { get; init; }

    public double? NutritionPerDay { get; init; }

    public double? LeadDays { get; init; }

    public bool? Active { get; init; }

    public bool? Fertilized { get; init; }
}

public sealed record PasteDispenser
{
    public string? BuildingId { get; init; }

    public bool? Powered { get; init; }

    public double? HopperNutrition { get; init; }

    public string? AdjacentRoomId { get; init; }
}

/// <summary>
/// Native twelfths (0..11) whose average tile temperature permits growth.
/// The window is climate potential, not a promise of harvestable instances.
/// </summary>
public sealed record ForagePlant
{
    public string? DefName { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<int>))]
    public EquatableArray<int> GrowingTwelfths { get; init; }

    public bool? GrowingNow { get; init; }
}

public sealed record PenGrazing
{
    public string? PenId { get; init; }

    public double? DemandPerDay { get; init; }

    public double? PasturePerDay { get; init; }

    public double? StoredNutrition { get; init; }
}

/// <summary>
/// MapPastureNutritionCalculator.CalculateAverageNutritionPerDay(terrain): the
/// nutrition per day one cell of the terrain grows for pasture animals in the
/// map's worst quadrum (the minimum over the four), summed over the biome's wild
/// grazing plants; the figure PenFoodCalculator sums over a pen's cells. One row
/// per TerrainDef with fertility above zero, sorted by def name.
/// </summary>
public sealed record PastureTerrain
{
    public string? TerrainDef { get; init; }

    public double? WorstQuadrumPerDay { get; init; }
}

public sealed record FoodSlaughterAnimal
{
    public string? PawnId { get; init; }

    public string? Race { get; init; }

    public double? MeatAmount { get; init; }
}

public sealed record FoodChannelsFacts
{
    public FishableWater? FishableWater { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<GatherableAnimal>))]
    public EquatableArray<GatherableAnimal> Gatherable { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<EggLayerAnimal>))]
    public EquatableArray<EggLayerAnimal> EggLayer { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PasteDispenser>))]
    public EquatableArray<PasteDispenser> PasteDispenser { get; init; }

    public uint? PollutedCells { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ForagePlant>))]
    public EquatableArray<ForagePlant> Forage { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PenGrazing>))]
    public EquatableArray<PenGrazing> Grazing { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<FoodSlaughterAnimal>))]
    public EquatableArray<FoodSlaughterAnimal> Slaughter { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PastureTerrain>))]
    public EquatableArray<PastureTerrain> PastureTerrain { get; init; }
}

public sealed record FoodChannelsSection
{
    public FoodChannelsSectionOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="FoodChannelsSection"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(FoodChannelsSectionOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(FoodChannelsSectionOutcome.UnavailableCase), "unavailable")]
public abstract record FoodChannelsSectionOutcome
{
    private protected FoodChannelsSectionOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public FoodChannelsFacts? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(FoodChannelsFacts Value) : FoodChannelsSectionOutcome;

    public static implicit operator FoodChannelsSectionOutcome(FoodChannelsFacts value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : FoodChannelsSectionOutcome;

    public static implicit operator FoodChannelsSectionOutcome(Common.Unavailable value) => new UnavailableCase(value);
}

/// <summary>
/// Discovered DeepResourceGrid cells only, merged into eight-connected components
/// of the same definition. Centre is the member nearest the component centroid
/// (ties by x,z); touching discoveries merge and depleted cells leave the lump.
/// </summary>
public sealed record DeepResourceLump
{
    public string? DefName { get; init; }

    public long? Count { get; init; }

    public Common.Cell? Centre { get; init; }

    public uint? CellCount { get; init; }
}

/// <summary>
/// Built player scanners only (empty list means none built). Working means usable
/// and used within the last 30 ticks. Ticks-to-next-find estimates remaining work
/// to the guaranteed threshold at the last user's speed, excluding idle time and
/// the &lt;=59 tick discovery check interval; random finds may happen sooner.
/// Timing is absent when native progress/speed or a guaranteed threshold is absent.
/// </summary>
public sealed record MineralScannerState
{
    public string? BuildingId { get; init; }

    public string? DefName { get; init; }

    public Common.Cell? Position { get; init; }

    public bool? Built { get; init; }

    public bool? Powered { get; init; }

    public bool? Working { get; init; }

    public long? TicksToNextFind { get; init; }

    public string? TargetMineable { get; init; }
}

/// <summary>
/// Every spawned player deep drill (an observed empty list means none). resource
/// and remaining are the native next deposit under the drill's radius; depleted
/// means no valuable deposit remains there (vanilla then drills stone only), so a
/// lump centre moving as it is mined never implies depletion. Every colonist
/// drill is the controller's under autonomous play; there is no ownership
/// ledger. designated reports a standing Deconstruct designation on the drill.
/// </summary>
public sealed record DeepDrillState
{
    public string? BuildingId { get; init; }

    public string? DefName { get; init; }

    public Common.Cell? Position { get; init; }

    public bool? Powered { get; init; }

    public string? Resource { get; init; }

    public long? Remaining { get; init; }

    public bool? Depleted { get; init; }

    public bool? Designated { get; init; }
}

/// <summary>
/// At most 256 lumps and 256 scanners plus drills total; excess/read failure makes the
/// section unavailable, never a silently truncated census. Absent section means unknown.
/// </summary>
public sealed record DeepResourcesFacts
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<DeepResourceLump>))]
    public EquatableArray<DeepResourceLump> Lumps { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<MineralScannerState>))]
    public EquatableArray<MineralScannerState> GroundScanners { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<MineralScannerState>))]
    public EquatableArray<MineralScannerState> LongRangeScanners { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<DeepDrillState>))]
    public EquatableArray<DeepDrillState> Drills { get; init; }
}

public sealed record DeepResourcesSection
{
    public DeepResourcesSectionOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="DeepResourcesSection"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(DeepResourcesSectionOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(DeepResourcesSectionOutcome.UnavailableCase), "unavailable")]
public abstract record DeepResourcesSectionOutcome
{
    private protected DeepResourcesSectionOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public DeepResourcesFacts? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(DeepResourcesFacts Value) : DeepResourcesSectionOutcome;

    public static implicit operator DeepResourcesSectionOutcome(DeepResourcesFacts value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : DeepResourcesSectionOutcome;

    public static implicit operator DeepResourcesSectionOutcome(Common.Unavailable value) => new UnavailableCase(value);
}

public sealed record DeliveryRow
{
    public DeliverySourceKind? SourceKind { get; init; }

    public string? SourceId { get; init; }

    public string? DefName { get; init; }

    public long? Units { get; init; }

    public double? Nutrition { get; init; }

    public long? LastTick { get; init; }
}

/// <summary>
/// Counters only ever grow within one epoch; epoch changes when the game is
/// loaded or restarted, so a reader re-baselines. Absent means unknown, never zero.
/// A player pawn killed an animal (a hunt, or a slaughter that leaves a corpse).
/// corpse_id is the corpse's thing id (the food census's stock id), race the
/// animal's def, body_size its body size and potential_nutrition the meat it
/// would yield (meat amount x meat nutrition); tick is the game tick of death and
/// pawn_id the killed animal's own thing id (a hunt row's source id).
/// </summary>
public sealed record KillRecord
{
    public string? CorpseId { get; init; }

    public string? Race { get; init; }

    public double? BodySize { get; init; }

    public double? PotentialNutrition { get; init; }

    public long? Tick { get; init; }

    public string? PawnId { get; init; }
}

/// <summary>
/// A butcher recipe made its products from one corpse, counted where the
/// products are made (any bill, any butcher). meat_units and meat_nutrition are
/// the produced meat (meat_def) stack and its nutrition, leather_units the
/// produced leather; Go links it to its KillRecord by corpse_id.
/// </summary>
public sealed record ButcherRecord
{
    public string? CorpseId { get; init; }

    public string? Recipe { get; init; }

    public string? MeatDef { get; init; }

    public long? MeatUnits { get; init; }

    public double? MeatNutrition { get; init; }

    public long? LeatherUnits { get; init; }

    public long? Tick { get; init; }
}

/// <summary>
/// kills and butchers hold every record of the epoch in tick order.
/// </summary>
public sealed record DeliveryLedgerFacts
{
    public string? Epoch { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<DeliveryRow>))]
    public EquatableArray<DeliveryRow> Rows { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<KillRecord>))]
    public EquatableArray<KillRecord> Kills { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ButcherRecord>))]
    public EquatableArray<ButcherRecord> Butchers { get; init; }
}

public sealed record DeliveryLedgerSection
{
    public DeliveryLedgerSectionOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="DeliveryLedgerSection"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(DeliveryLedgerSectionOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(DeliveryLedgerSectionOutcome.UnavailableCase), "unavailable")]
public abstract record DeliveryLedgerSectionOutcome
{
    private protected DeliveryLedgerSectionOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public DeliveryLedgerFacts? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(DeliveryLedgerFacts Value) : DeliveryLedgerSectionOutcome;

    public static implicit operator DeliveryLedgerSectionOutcome(DeliveryLedgerFacts value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : DeliveryLedgerSectionOutcome;

    public static implicit operator DeliveryLedgerSectionOutcome(Common.Unavailable value) => new UnavailableCase(value);
}

public sealed record ColonyFactsSnapshot
{
    public Common.ObservationContext? Context { get; init; }

    public uint? ColonistCount { get; init; }

    public uint? WorkerCount { get; init; }

    public Common.Cell? Center { get; init; }

    public MapSize? MapSize { get; init; }

    public string? Biome { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Quantity>))]
    public EquatableArray<Quantity> Resources { get; init; }

    public double? FoodNutrition { get; init; }

    public double? NutritionPerDay { get; init; }

    public double? FoodRunwayDays { get; init; }

    public double? PendingFoodNutrition { get; init; }

    public uint? BedCapacity { get; init; }

    public uint? IndoorSleepingCapacity { get; init; }

    public double? SleepingTemperatureMinC { get; init; }

    public double? SleepingTemperatureMaxC { get; init; }

    public double? OutdoorTemperatureC { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<EnvironmentCondition>))]
    public EquatableArray<EnvironmentCondition> Environment { get; init; }

    public FoodClimate? FoodClimate { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<FarmFacts>))]
    public EquatableArray<FarmFacts> Farms { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<CookingFacts>))]
    public EquatableArray<CookingFacts> Cooking { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<AcquisitionFacts>))]
    public EquatableArray<AcquisitionFacts> Acquisition { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ButcheringFacts>))]
    public EquatableArray<ButcheringFacts> Butchering { get; init; }

    public bool? FoodStorage { get; init; }

    public FoodSupplySection? FoodSupply { get; init; }

    public ForecastSection? Forecast { get; init; }

    public UpkeepSection? Upkeep { get; init; }

    public DevelopmentSection? Development { get; init; }

    public PlanningSection? Planning { get; init; }

    public RecoveryReply? Recovery { get; init; }

    public WasteReply? Waste { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }

    public double? PendingWoodUnits { get; init; }

    public uint? PendingHunts { get; init; }

    /// <summary>
    /// Player faction TechLevel name (e.g. Neolithic, Industrial); shapes the starter shelter.
    /// </summary>
    public string? PlayerTechLevel { get; init; }

    /// <summary>
    /// Present only while exactly one force-pausing choice dialog is open.
    /// </summary>
    public ChoiceDialog? Dialog { get; init; }

    /// <summary>
    /// Colony wealth and raid points (ThreatFacts), or why they could not be read.
    /// </summary>
    public ThreatSection? Threat { get; init; }

    public LootSection? EventLoot { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<JoinerLetter>))]
    public EquatableArray<JoinerLetter> JoinerLetters { get; init; }

    public FoodChannelsSection? FoodChannels { get; init; }

    public DeepResourcesSection? DeepResources { get; init; }

    public uint? FermentingBarrels { get; init; }

    /// <summary>
    /// Every outfit, drug, food and reading policy and every allowed area.
    /// </summary>
    public PolicySection? Policies { get; init; }

    /// <summary>
    /// Biotech pollution sources, wastepacks, gestators, chargers and baby care; absent without Biotech.
    /// </summary>
    public BiotechSection? Biotech { get; init; }

    /// <summary>
    /// TileMutatorDef defNames of the colony map's world tile (Tile.Mutators), e.g. caves or ancient vents. Empty without Odyssey or with none.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> TileMutators { get; init; }

    /// <summary>
    /// Odyssey eruption, contamination and underground-site state; absent without Odyssey.
    /// </summary>
    public OdysseySection? Odyssey { get; init; }

    /// <summary>
    /// Anomaly containment, knowledge and incident state; absent without Anomaly.
    /// </summary>
    public AnomalySection? Anomaly { get; init; }

    /// <summary>
    /// Royalty neuroformer stock, bestowing ceremonies and thrones; absent without Royalty.
    /// </summary>
    public RoyaltySection? Royalty { get; init; }

    /// <summary>
    /// Cumulative production-site delivery counters (DeliveryLedgerFacts); absent means unknown.
    /// </summary>
    public DeliveryLedgerSection? DeliveryLedger { get; init; }

    /// <summary>
    /// Raw hunt facts for policy.HuntGate; always present on a native read.
    /// </summary>
    public HuntCensus? HuntCensus { get; init; }
}

/// <summary>
/// Odyssey colony facts, a keyed colony section. A scalar
/// absent is unknown, never zero.
/// conditions: every GameCondition active on the colony map, whatever its
/// kind (lava flow, volcanic ash or winter, toxic fallout; the class says
/// which). hazard_terrain: the terrain defs on the colony map whose own
/// flags make them hurt or contaminate (TerrainDef.dangerous, burnDamage,
/// heatPerTick or toxicBuildupFactor above zero) with the cells each covers.
/// lava_emergences: spawned LavaEmergence things (an eruption's vents).
/// sites: each ancient hatch's pocket (underground) map and its hack state.
/// </summary>
public sealed record OdysseySection
{
    public OdysseySectionOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="OdysseySection"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(OdysseySectionOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(OdysseySectionOutcome.UnavailableCase), "unavailable")]
public abstract record OdysseySectionOutcome
{
    private protected OdysseySectionOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public OdysseyColonyFacts? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(OdysseyColonyFacts Value) : OdysseySectionOutcome;

    public static implicit operator OdysseySectionOutcome(OdysseyColonyFacts value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : OdysseySectionOutcome;

    public static implicit operator OdysseySectionOutcome(Common.Unavailable value) => new UnavailableCase(value);
}

public sealed record OdysseyColonyFacts
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<ActiveCondition>))]
    public EquatableArray<ActiveCondition> Conditions { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<HazardTerrain>))]
    public EquatableArray<HazardTerrain> HazardTerrain { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<LavaEmergenceState>))]
    public EquatableArray<LavaEmergenceState> LavaEmergences { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<UndergroundSite>))]
    public EquatableArray<UndergroundSite> Sites { get; init; }
}

/// <summary>
/// def_name is the GameConditionDef; condition_class the GameCondition
/// subclass CLR full name. Permanent conditions carry no ticks_left. causer_id is the
/// Thing causing the condition (a toxic spewer, a climate adjuster), absent
/// for an event's.
/// </summary>
public sealed record ActiveCondition
{
    public string? ConditionId { get; init; }

    public string? DefName { get; init; }

    public string? ConditionClass { get; init; }

    public int? TicksPassed { get; init; }

    public int? TicksLeft { get; init; }

    public bool? Permanent { get; init; }

    public string? CauserId { get; init; }
}

public sealed record HazardTerrain
{
    public string? DefName { get; init; }

    public uint? Cells { get; init; }

    public bool? Dangerous { get; init; }

    public int? BurnDamage { get; init; }

    public double? HeatPerTick { get; init; }

    public double? ToxicBuildupFactor { get; init; }
}

public sealed record LavaEmergenceState
{
    public string? ThingId { get; init; }

    public Common.Cell? Position { get; init; }
}

/// <summary>
/// One AncientHatch with an existing pocket map: pocket_map_id, the hatch's
/// stockpile type and layout (PortalState), the colonists now on the pocket
/// map and every CompHackable thing on it.
/// </summary>
public sealed record UndergroundSite
{
    public string? HatchId { get; init; }

    public int? PocketMapId { get; init; }

    public string? StockpileType { get; init; }

    public string? Layout { get; init; }

    public uint? ColonistsPresent { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<UndergroundHackable>))]
    public EquatableArray<UndergroundHackable> Hackables { get; init; }
}

public sealed record UndergroundHackable
{
    public string? ThingId { get; init; }

    public string? DefName { get; init; }

    public Common.Cell? Position { get; init; }

    public double? ProgressPercent { get; init; }

    public double? Defence { get; init; }

    public bool? Hacked { get; init; }

    public bool? LockedOut { get; init; }

    public bool? Autohack { get; init; }
}

/// <summary>
/// Anomaly colony facts, a keyed colony section. Per-thing
/// state (an entity's hold and study, a platform's containment strength)
/// rides the pawn and building rows; this section carries what no
/// row holds. A scalar absent is unknown, never zero.
/// knowledge: one row per KnowledgeCategoryDef. codex: one row per
/// EntityCategoryDef; discovered_entries are the EntityCodexEntryDef names
/// the colony has discovered. held_entities: each pawn a holding
/// platform holds on the colony map, by id, joining the pawn and building
/// rows. holding_platform_available: StudyUtility.HoldingPlatformAvailableOnCurrentMap.
/// </summary>
public sealed record AnomalySection
{
    public AnomalySectionOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="AnomalySection"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AnomalySectionOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(AnomalySectionOutcome.UnavailableCase), "unavailable")]
public abstract record AnomalySectionOutcome
{
    private protected AnomalySectionOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public AnomalyColonyFacts? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(AnomalyColonyFacts Value) : AnomalySectionOutcome;

    public static implicit operator AnomalySectionOutcome(AnomalyColonyFacts value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : AnomalySectionOutcome;

    public static implicit operator AnomalySectionOutcome(Common.Unavailable value) => new UnavailableCase(value);
}

public sealed record AnomalyColonyFacts
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<KnowledgeProgress>))]
    public EquatableArray<KnowledgeProgress> Knowledge { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<CodexProgress>))]
    public EquatableArray<CodexProgress> Codex { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> DiscoveredEntries { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<HeldEntity>))]
    public EquatableArray<HeldEntity> HeldEntities { get; init; }

    public bool? HoldingPlatformAvailable { get; init; }

    public AnomalyIncidentState? Incidents { get; init; }

    public MonolithState? Monolith { get; init; }
}

/// <summary>
/// current_project is the ResearchProjectDef the research manager funds from
/// this category (absent when none is set); knowledge the amount stored for
/// it (ResearchManager.GetKnowledge); projects_available whether any project
/// of the category can still be researched.
/// </summary>
public sealed record KnowledgeProgress
{
    public string? Category { get; init; }

    public string? CurrentProject { get; init; }

    public double? Knowledge { get; init; }

    public bool? ProjectsAvailable { get; init; }
}

/// <summary>
/// entries: the codex entries in the category, discovered: those discovered.
/// </summary>
public sealed record CodexProgress
{
    public string? Category { get; init; }

    public uint? Entries { get; init; }

    public uint? Discovered { get; init; }
}

public sealed record HeldEntity
{
    public string? PawnId { get; init; }

    public string? PlatformId { get; init; }
}

/// <summary>
/// GameComponent_Anomaly: the state that gates and times the Anomaly
/// threats. level_def and level are the monolith's current MonolithLevelDef
/// and its number; highest_level_reached the highest it has reached.
/// anomaly_threat_fraction_now is the share of threat points the game
/// currently gives Anomaly incidents. awoken_corpse_active: an unnatural
/// corpse has awoken; metalhorror_implant_possible: the game would allow a
/// new metalhorror biosignature implant now.
/// </summary>
public sealed record AnomalyIncidentState
{
    public bool? MonolithSpawned { get; init; }

    public string? LevelDef { get; init; }

    public int? Level { get; init; }

    public int? HighestLevelReached { get; init; }

    public bool? QuestlineEnded { get; init; }

    public int? TicksSinceLevelChange { get; init; }

    public bool? AmbientHorrorMode { get; init; }

    public bool? AnomalyStudyEnabled { get; init; }

    public double? AnomalyThreatFractionNow { get; init; }

    public bool? VoidAwakeningActive { get; init; }

    public bool? AwokenCorpseActive { get; init; }

    public bool? MetalhorrorImplantPossible { get; init; }
}

/// <summary>
/// The void monolith and the endgame it gates, present only while the
/// monolith is spawned (absent otherwise: unknown). can_activate is
/// Building_VoidMonolith.CanActivate. next_level_def is
/// GameComponent_Anomaly.NextLevelDef (absent when the current level does not
/// advance through activation or none follows); next_level_codex_category and
/// next_level_codex_required are that def's entityCatagoryCompletionRequired and
/// entityCountCompletionRequired (both absent when it requires none), and
/// codex_shortfall the entries still undiscovered in that category (0 when met;
/// present with them). blocking_conditions are the active GameConditionDef names
/// on the monolith's map that the next level lists as unreachableDuringConditions
/// (UnnaturalDarkness blocks VoidAwakened). gleaming_interaction_available is
/// CompGleamingMonolith.CanInteract() (true only at the Gleaming level).
/// void_structures counts the spawned VoidStructure things on the monolith's map
/// and void_structures_activated those whose CompVoidStructure is active.
/// void_node_exists: a VoidNode thing is spawned on any loaded map (the node
/// stands in the metal hell pocket map once the Gleaming monolith is used).
/// void_awakening_stage is 0 with the EndGame_VoidAwakening quest running and no
/// structure spawned yet, else one more than the highest stageStructure.N quest
/// tag on a spawned structure; absent when the quest is not running.
/// monolith_id is the monolith's GetUniqueLoadID, the thing a give-job targets.
/// pending_void_structure_ids are the spawned VoidStructures on the
/// monolith's map whose CompInteractable.CanInteract() accepts (not yet
/// activated), sorted; void_node_id is the VoidNode whose CanInteract() accepts
/// and void_node_pawn_ids the free colonists spawned on the node's map (the
/// pawn skipped into the pocket map), sorted.
/// </summary>
public sealed record MonolithState
{
    public string? MonolithId { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> PendingVoidStructureIds { get; init; }

    public string? VoidNodeId { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> VoidNodePawnIds { get; init; }

    public bool? CanActivate { get; init; }

    public string? NextLevelDef { get; init; }

    public string? NextLevelCodexCategory { get; init; }

    public uint? NextLevelCodexRequired { get; init; }

    public uint? CodexShortfall { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> BlockingConditions { get; init; }

    public bool? GleamingInteractionAvailable { get; init; }

    public uint? VoidStructures { get; init; }

    public uint? VoidStructuresActivated { get; init; }

    public bool? VoidNodeExists { get; init; }

    public int? VoidAwakeningStage { get; init; }
}

/// <summary>
/// One policy database row: Policy.GetUniqueLoadID(), label, the player
/// pawns (colonists, prisoners, slaves, tame animals; any map, caravan or
/// transporter) whose current policy it is, and whether it is the
/// database default (index 0). allowed_defs is a reading policy's allowed
/// book ThingDefs or a food policy's allowed food ThingDefs (one of
/// PolicyFacts.foods); empty for the other databases. drug_entries is
/// a drug policy's entries that allow anything (joy, addiction, scheduled
/// use or a carried count); every other drug of it is off.
/// </summary>
public sealed record PolicyEntry
{
    public string? Id { get; init; }

    public string? Label { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> PawnIds { get; init; }

    public bool? Default { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> AllowedDefs { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Operations.DrugPolicyEntry>))]
    public EquatableArray<Operations.DrugPolicyEntry> DrugEntries { get; init; }
}

/// <summary>
/// Area_Allowed on the colony map: Area.GetUniqueLoadID(), label and the
/// pawns restricted to it there.
/// </summary>
public sealed record AllowedAreaEntry
{
    public string? Id { get; init; }

    public string? Label { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> PawnIds { get; init; }
}

public sealed record PolicyFacts
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<PolicyEntry>))]
    public EquatableArray<PolicyEntry> Outfit { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PolicyEntry>))]
    public EquatableArray<PolicyEntry> Drug { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PolicyEntry>))]
    public EquatableArray<PolicyEntry> Food { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PolicyEntry>))]
    public EquatableArray<PolicyEntry> Reading { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<AllowedAreaEntry>))]
    public EquatableArray<AllowedAreaEntry> AllowedAreas { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<FoodEater>))]
    public EquatableArray<FoodEater> FoodEaters { get; init; }
}

public sealed record FoodEater
{
    public string? PawnId { get; init; }

    public FoodEaterKind? Kind { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Precepts { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Trait>))]
    public EquatableArray<Trait> PawnTraits { get; init; }
}

public sealed record PolicySection
{
    public PolicySectionOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="PolicySection"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PolicySectionOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(PolicySectionOutcome.UnavailableCase), "unavailable")]
public abstract record PolicySectionOutcome
{
    private protected PolicySectionOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public PolicyFacts? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(PolicyFacts Value) : PolicySectionOutcome;

    public static implicit operator PolicySectionOutcome(PolicyFacts value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : PolicySectionOutcome;

    public static implicit operator PolicySectionOutcome(Common.Unavailable value) => new UnavailableCase(value);
}

/// <summary>
/// Biotech colony facts, a keyed colony section. The pollution
/// cell grid is a separate code; pollution here is its totals plus what makes
/// and removes it. Every row is a spawned player-map thing; a scalar absent
/// is unknown, never zero.
/// pollution: PollutionGrid totals (total_pollution is PollutionGrid.TotalPollution,
/// pollutable_cells the count of AllPollutableCells) and the player's
/// Area_PollutionClear (the cleanup crew's allowed area, clear_area_cells its
/// size). polluters: buildings with CompToxifier or CompPolluteOverTime.
/// wastepacks: spawned stacks of things with CompDissolution, which turn into
/// pollution when they dissolve. atomizers and pumps remove waste and
/// pollution (pumps: CompPollutionPump, off while disabled_by_artificial_buildings). gestators, chargers: Building_MechGestator and
/// Building_MechCharger, the producers of wastepacks. babies: each player
/// pawn whose developmental stage is Newborn or Baby; breastfeeders are the
/// pawns ChildcareUtility.CanBreastfeedPlayerPawns lists (pawn ids).
/// </summary>
public sealed record BiotechSection
{
    public BiotechSectionOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="BiotechSection"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(BiotechSectionOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(BiotechSectionOutcome.UnavailableCase), "unavailable")]
public abstract record BiotechSectionOutcome
{
    private protected BiotechSectionOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public BiotechColonyFacts? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(BiotechColonyFacts Value) : BiotechSectionOutcome;

    public static implicit operator BiotechSectionOutcome(BiotechColonyFacts value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : BiotechSectionOutcome;

    public static implicit operator BiotechSectionOutcome(Common.Unavailable value) => new UnavailableCase(value);
}

public sealed record BiotechColonyFacts
{
    public PollutionTotals? Pollution { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Polluter>))]
    public EquatableArray<Polluter> Polluters { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Wastepack>))]
    public EquatableArray<Wastepack> Wastepacks { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<WastepackAtomizer>))]
    public EquatableArray<WastepackAtomizer> Atomizers { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PollutionPump>))]
    public EquatableArray<PollutionPump> Pumps { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<MechGestatorState>))]
    public EquatableArray<MechGestatorState> Gestators { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<MechChargerState>))]
    public EquatableArray<MechChargerState> Chargers { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<BabyCare>))]
    public EquatableArray<BabyCare> Babies { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Breastfeeders { get; init; }

    /// <summary>
    /// Gene-building rows: the banks, assemblers, extractors, genepacks
    /// and xenogerms of the colony map.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<GeneBankState>))]
    public EquatableArray<GeneBankState> GeneBanks { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<GeneAssemblerState>))]
    public EquatableArray<GeneAssemblerState> GeneAssemblers { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<GeneExtractorState>))]
    public EquatableArray<GeneExtractorState> GeneExtractors { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<GenepackState>))]
    public EquatableArray<GenepackState> Genepacks { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<XenogermState>))]
    public EquatableArray<XenogermState> Xenogerms { get; init; }
}

/// <summary>
/// Gene-building rows. A pawn or thing reference is a thing id. gene
/// lists are GeneDef defNames, sorted; complexity, metabolism and archites are
/// the game's own GeneSet totals (ComplexityTotal, MetabolismTotal,
/// ArchitesTotal), never summed in Go.
/// GeneBankState: a building with CompGenepackContainer. capacity is
/// CompProperties_GenepackContainer.maxCapacity, pack_ids the contained
/// packs, auto_load the container's own flag.
/// </summary>
public sealed record GeneBankState
{
    public string? ThingId { get; init; }

    public string? DefName { get; init; }

    public Common.Cell? Position { get; init; }

    public bool? Powered { get; init; }

    public int? Capacity { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> PackIds { get; init; }

    public bool? AutoLoad { get; init; }
}

/// <summary>
/// GeneAssemblerState: Building_GeneAssembler. working is Working; the run
/// fields (progress, total_work, pack_ids, archites_owed, can_work_now) are
/// set only while working. max_complexity is MaxComplexity(), archites_owed
/// ArchitesRequiredNow, can_work_now CanBeWorkedOnNow, linked_bank_ids the
/// connected facilities that hold a genepack container.
/// </summary>
public sealed record GeneAssemblerState
{
    public string? ThingId { get; init; }

    public string? DefName { get; init; }

    public Common.Cell? Position { get; init; }

    public bool? Powered { get; init; }

    public bool? Working { get; init; }

    public double? Progress { get; init; }

    public double? TotalWork { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> PackIds { get; init; }

    public int? ArchitesOwed { get; init; }

    public int? MaxComplexity { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> LinkedBankIds { get; init; }

    public bool? CanWorkNow { get; init; }
}

/// <summary>
/// GeneExtractorState: Building_GeneExtractor. selected_pawn_id is
/// Building_Enterable.SelectedPawn, occupant_id the pawn inside; ticks_remaining
/// and power_cut_ticks are set only while working.
/// </summary>
public sealed record GeneExtractorState
{
    public string? ThingId { get; init; }

    public string? DefName { get; init; }

    public Common.Cell? Position { get; init; }

    public bool? Powered { get; init; }

    public bool? Working { get; init; }

    public string? SelectedPawnId { get; init; }

    public string? OccupantId { get; init; }

    public int? TicksRemaining { get; init; }

    public int? PowerCutTicks { get; init; }
}

/// <summary>
/// GenepackState: a Genepack, in a bank (bank_id, no position) or spawned
/// loose (position). deteriorating is Genepack.Deteriorating.
/// </summary>
public sealed record GenepackState
{
    public string? ThingId { get; init; }

    public string? DefName { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Genes { get; init; }

    public string? BankId { get; init; }

    public Common.Cell? Position { get; init; }

    public bool? Deteriorating { get; init; }

    public bool? AutoLoad { get; init; }

    public int? HitPoints { get; init; }

    public int? Complexity { get; init; }

    public int? Metabolism { get; init; }

    public int? Archites { get; init; }
}

/// <summary>
/// XenogermState: a spawned Xenogerm item; target_pawn_id is its pending
/// implant target.
/// </summary>
public sealed record XenogermState
{
    public string? ThingId { get; init; }

    public string? DefName { get; init; }

    public Common.Cell? Position { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Genes { get; init; }

    public string? TargetPawnId { get; init; }

    public int? Complexity { get; init; }

    public int? Metabolism { get; init; }

    public int? Archites { get; init; }

    public bool? Forbidden { get; init; }

    /// <summary>
    /// implant_metabolism: GeneUtility.MetabolismAfterImplanting per colonist, slave or
    /// colony prisoner on the map (the game's number; the implant is refused below the
    /// biostat range minimum), sorted by pawn id.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<XenogermImplantMetabolism>))]
    public EquatableArray<XenogermImplantMetabolism> ImplantMetabolism { get; init; }
}

public sealed record XenogermImplantMetabolism
{
    public string? PawnId { get; init; }

    public int? MetabolismAfter { get; init; }
}

public sealed record PollutionTotals
{
    public int? TotalPollution { get; init; }

    public uint? PollutableCells { get; init; }

    public string? ClearAreaId { get; init; }

    public int? ClearAreaCells { get; init; }

    /// <summary>
    /// polluted_cells counts the pollutable cells PollutionGrid.IsPolluted holds;
    /// polluted_uncovered_cells those outside the pollution-clear area.
    /// </summary>
    public uint? PollutedCells { get; init; }

    public uint? PollutedUncoveredCells { get; init; }
}

/// <summary>
/// polluting is CompToxifier.CanPolluteNow; cells_per_day is
/// CompProperties_PolluteOverTime.cellsToPollutePerDay.
/// </summary>
public sealed record Polluter
{
    public string? ThingId { get; init; }

    public string? DefName { get; init; }

    public Common.Cell? Position { get; init; }

    public bool? Polluting { get; init; }

    public int? CellsPerDay { get; init; }
}

/// <summary>
/// frozen, outdoors, in_atomizer and can_dissolve_now are CompDissolution's
/// own verdicts; deterioration_rate is CompDissolution.DeterioarationRate.
/// </summary>
public sealed record Wastepack
{
    public string? ThingId { get; init; }

    public string? DefName { get; init; }

    public Common.Cell? Position { get; init; }

    public int? Count { get; init; }

    public bool? Frozen { get; init; }

    public bool? Outdoors { get; init; }

    public bool? InAtomizer { get; init; }

    public bool? CanDissolveNow { get; init; }

    public int? DeteriorationRate { get; init; }

    public bool? Forbidden { get; init; }
}

/// <summary>
/// Building_WastepackAtomizer: its CompAtomizer's room and progress.
/// </summary>
public sealed record WastepackAtomizer
{
    public string? ThingId { get; init; }

    public string? DefName { get; init; }

    public Common.Cell? Position { get; init; }

    public bool? Powered { get; init; }

    public int? SpaceLeft { get; init; }

    public double? FillPercent { get; init; }

    public bool? AutoLoad { get; init; }

    public int? TicksLeft { get; init; }
}

public sealed record PollutionPump
{
    public string? ThingId { get; init; }

    public string? DefName { get; init; }

    public Common.Cell? Position { get; init; }

    public bool? Powered { get; init; }

    public bool? DisabledByArtificialBuildings { get; init; }
}

/// <summary>
/// state is the active Bill_Mech's FormingState name (Gathering, Preparing,
/// Forming, Formed), absent with no bill. bound_pawn_id is the mech being
/// gestated, mech_kind its PawnKindDef; forming_percent is
/// CurrentBillFormingPercent. waste_count is the wastepacks held in the
/// gestator's CompWasteProducer.
/// </summary>
public sealed record MechGestatorState
{
    public string? ThingId { get; init; }

    public string? DefName { get; init; }

    public Common.Cell? Position { get; init; }

    public bool? Powered { get; init; }

    public string? BillId { get; init; }

    public string? State { get; init; }

    public string? MechKind { get; init; }

    public string? BoundPawnId { get; init; }

    public int? CyclesCompleted { get; init; }

    public double? BandwidthCost { get; init; }

    public double? FormingPercent { get; init; }

    public int? WasteCount { get; init; }
}

/// <summary>
/// full_of_waste is Building_MechCharger.IsFullOfWaste.
/// </summary>
public sealed record MechChargerState
{
    public string? ThingId { get; init; }

    public string? DefName { get; init; }

    public Common.Cell? Position { get; init; }

    public bool? Powered { get; init; }

    public string? ChargingMechId { get; init; }

    public int? WasteCount { get; init; }

    public bool? FullOfWaste { get; init; }
}

/// <summary>
/// wants_suckle, can_suckle_now and being_played_with are ChildcareUtility's
/// verdicts; autofeeders are the feeders with an AutofeedMode set for the baby
/// (Pawn_MindState.Autofeeders, mode the AutofeedMode name).
/// </summary>
public sealed record BabyCare
{
    public string? PawnId { get; init; }

    public bool? WantsSuckle { get; init; }

    public bool? CanSuckleNow { get; init; }

    public bool? BeingPlayedWith { get; init; }

    public bool? InBed { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<BabyAutofeeder>))]
    public EquatableArray<BabyAutofeeder> Autofeeders { get; init; }
}

public sealed record BabyAutofeeder
{
    public string? PawnId { get; init; }

    public string? Mode { get; init; }
}

/// <summary>
/// Current-map WandererJoins offers and creepjoiner offers (creepjoiner true); the token binds letter, quest, pawn and expiry. expires_tick is the letter's disappearAtTick, negative for no timeout.
/// </summary>
public sealed record JoinerLetter
{
    public int? LetterId { get; init; }

    public string? SnapshotToken { get; init; }

    public string? PawnId { get; init; }

    public long? ExpiresTick { get; init; }

    public string? AcceptLabel { get; init; }

    public bool? CanAccept { get; init; }

    public bool? Creepjoiner { get; init; }
}

/// <summary>
/// herd_radius: cells within which same-race animals count toward a hunt row's herd_size;
/// absent, herd_size is unknown (omitted). hunt_route_budget_ms: main-thread milliseconds the
/// hunt census may spend on RouteSafe pairs (about 0.2-0.45 ms each); pairs past it report
/// HuntRoute.skipped. Absent: evaluate every pair.
/// </summary>
public sealed record ColonyFactsRequest
{
    public ReadScope? Scope { get; init; }

    public bool? Planning { get; init; }

    public uint? HerdRadius { get; init; }

    public uint? HuntRouteBudgetMs { get; init; }

    /// <summary>
    /// Cells from a wild predator a hunt route must stay outside; absent, no pair is evaluated (all report skipped).
    /// </summary>
    public float? HuntPredatorMarginCells { get; init; }
}

public sealed record ColonyFactsReply
{
    public ColonyFactsReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="ColonyFactsReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ColonyFactsReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(ColonyFactsReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(ColonyFactsReplyOutcome.FailureCase), "failure")]
public abstract record ColonyFactsReplyOutcome
{
    private protected ColonyFactsReplyOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public ColonyFactsSnapshot? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(ColonyFactsSnapshot Value) : ColonyFactsReplyOutcome;

    public static implicit operator ColonyFactsReplyOutcome(ColonyFactsSnapshot value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : ColonyFactsReplyOutcome;

    public static implicit operator ColonyFactsReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : ColonyFactsReplyOutcome;

    public static implicit operator ColonyFactsReplyOutcome(Common.Failure value) => new FailureCase(value);
}

/// <summary>
/// Simulation status excludes notifications/UI/clock, exposed by their typed owners.
/// ThreatPawn is one living non-colonist pawn's threat facts: the
/// native emits facts, Go classifies them (bridge.ClassifyThreat). A row is
/// emitted for a pawn in a mental state, of a faction hostile to the
/// player, breaking out of prison or on a PredatorHunt job, and for
/// an unowned downed pawn or predator within the requested predator radius
/// of a colonist; any other pawn costs one field read and no row.
/// pawn references the bundle's pawn table row; ours is a
/// player-faction pawn; faction_id is set with faction_hostile (a held
/// prisoner is never faction_hostile). prey is a PredatorHunt's target pawn
/// (a corpse's inner pawn), prey_is_ours a player-owned or player-held prey.
/// passive (on ThreatPawn and ThreatBuilding) is set for
/// faction-hostile insects and hives (dormant, or awake but not engaging)
/// and other CompCanBeDormant hostiles (asleep); a passive threat is left
/// alone, never held for or attacked. nearest_colonist_distance is
/// Chebyshev cells to the nearest free colonist, absent with none.
/// </summary>
public sealed record ThreatPawn
{
    public Common.Ref? Prey { get; init; }

    public bool? PreyIsOurs { get; init; }

    public bool? Ours { get; init; }

    public bool? Passive { get; init; }

    public Common.Ref? Pawn { get; init; }

    public double? NearestColonistDistance { get; init; }

    public string? MentalState { get; init; }

    public bool? FactionHostile { get; init; }

    public Common.Ref? Faction { get; init; }

    public bool? PrisonBreak { get; init; }

    public bool? PredatorHunt { get; init; }

    public bool? Predator { get; init; }

    public bool? Downed { get; init; }
}

/// <summary>
/// A hostile-faction building that is itself a combat target (an insect hive,
/// a crashed ship part, a mech-cluster piece with combat power): its own CAS
/// token rides on building.snapshot for Operations.AttackTarget, and
/// nearest_colonist_distance is Chebyshev cells like a threat pawn's;
/// occupied is the building's occupied rect (inclusive), the cells a
/// ranged defender needs a line of fire to.
/// A hostile building's row: building is its head (no table holds hostile
/// buildings) and building_snapshot its CAS token.
/// </summary>
public sealed record ThreatBuilding
{
    public EntityRef? Building { get; init; }

    public SnapshotRef? BuildingSnapshot { get; init; }

    public string? HostileReason { get; init; }

    public int? HitPoints { get; init; }

    public int? NearestColonistDistance { get; init; }

    public bool? Passive { get; init; }

    public Rectangle? Occupied { get; init; }
}

public sealed record ThreatsSnapshot
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<ThreatPawn>))]
    public EquatableArray<ThreatPawn> Pawns { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ThreatBuilding>))]
    public EquatableArray<ThreatBuilding> HostileBuildings { get; init; }
}

/// <summary>
/// colonists references every spawned free colonist's pawn table row
/// complete unless issues name "colonists".
/// </summary>
public sealed record StatusSnapshot
{
    public Common.ObservationContext? Context { get; init; }

    public ThreatsSnapshot? Threats { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Ref>))]
    public EquatableArray<Common.Ref> Colonists { get; init; }
}

public sealed record StatusRequest
{
    public ReadScope? Scope { get; init; }

    public bool? Colonists { get; init; }

    public bool? Threats { get; init; }

    public double? PredatorRadius { get; init; }
}

public sealed record StatusReply
{
    public StatusReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="StatusReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(StatusReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(StatusReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(StatusReplyOutcome.FailureCase), "failure")]
public abstract record StatusReplyOutcome
{
    private protected StatusReplyOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public StatusSnapshot? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(StatusSnapshot Value) : StatusReplyOutcome;

    public static implicit operator StatusReplyOutcome(StatusSnapshot value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : StatusReplyOutcome;

    public static implicit operator StatusReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : StatusReplyOutcome;

    public static implicit operator StatusReplyOutcome(Common.Failure value) => new FailureCase(value);
}

/// <summary>
/// A snapshot stream frame, and the scheduler's step read the controller
/// composes (clock_status is set only there: frames never carry it).
/// </summary>
public sealed record BundleSnapshot
{
    public Common.ObservationContext? Context { get; init; }

    public bool? Paused { get; init; }

    public Clock.Status? ClockStatus { get; init; }

    public StatusSnapshot? Emergency { get; init; }

    public ColonyFactsSnapshot? ColonyFacts { get; init; }

    public PopulationSnapshot? Population { get; init; }

    public ResearchSnapshot? Research { get; init; }

    /// <summary>
    /// The pawn table (35) replaces the colonist detail section.
    /// </summary>
    public BuildingsSnapshot? Buildings { get; init; }

    public BillsSnapshot? Bills { get; init; }

    public ZonesSnapshot? Zones { get; init; }

    public TradersSnapshot? Traders { get; init; }

    public WorldProgressionSnapshot? WorldProgression { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ResourceSourcesSnapshot>))]
    public EquatableArray<ResourceSourcesSnapshot> ResourceSources { get; init; }

    /// <summary>
    /// The planning window band (17) is cut from grid.
    /// Combat state, while the map has an active hostile or a combat
    /// epoch runs, else empty: every spawned colonist, hostile and colony
    /// animal, and the native's retained event ring (the newest 1024
    /// events, oldest first). A reader merges events by their watermark, so
    /// a lapped or restarted reader resyncs from any frame.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<Mirror.CombatPawn>))]
    public EquatableArray<Mirror.CombatPawn> CombatPawns { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Mirror.CombatEventRow>))]
    public EquatableArray<Mirror.CombatEventRow> CombatEvents { get; init; }

    /// <summary>
    /// The defense planner's other combat inputs, in the same frame,
    /// while the emergency census lists a threat or a colonist in a mental
    /// state, else unset. The pawn table carries the combat detail (health,
    /// equipment, biography, animals) of every hostile and hunting predator.
    /// combat_lines_of_fire is read_lines_of_fire from every colonist cell
    /// with a ranged primary to every hostile building's occupied cells, at
    /// most 64 cells a side, unset when either side is empty.
    /// </summary>
    public LinesOfFireSnapshot? CombatLinesOfFire { get; init; }

    /// <summary>
    /// Combat rooms are read from the rooms census (27).
    /// Every damaged spawned player door, with the combat inputs, at most
    /// 64: the doors a fight may send a gunner to repair.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<Mirror.CombatDoorRow>))]
    public EquatableArray<Mirror.CombatDoorRow> CombatDoors { get; init; }

    /// <summary>
    /// Planning definitions are the definition catalog's.
    /// list_rooms without outdoor rooms or boundary buildings, with cells:
    /// the room census temperature, comfort and hospital planning read.
    /// </summary>
    public RoomsSnapshot? Rooms { get; init; }

    /// <summary>
    /// Every spawned unroofed player mortar, with the combat inputs, at
    /// most 16: the guns a fight may crew for counter-battery.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<Mirror.CombatMortarRow>))]
    public EquatableArray<Mirror.CombatMortarRow> CombatMortars { get; init; }

    /// <summary>
    /// The outdoor temperature a fight shelters from is
    /// colony_facts.outdoor_temperature_c.
    /// The hottest live hive's cell temperature in degrees Celsius,
    /// with the combat inputs: an infestation's heat-stroke hold and entry check.
    /// </summary>
    public float? CombatHiveTemperatureC { get; init; }

    /// <summary>
    /// The watermark of every omittable section, carried or not:
    /// emergency, colony_facts, population, research, traders,
    /// world_progression and ideology. Native omits such a section while its content,
    /// nested context ticks aside, matches the last one published, and the
    /// reader keeps that one, current at this frame's tick. A section absent
    /// with a seq other than the one the reader holds was published in a
    /// frame the reader missed: it asks for a keyframe.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<SectionWatermark>))]
    public EquatableArray<SectionWatermark> Watermarks { get; init; }

    /// <summary>
    /// The pawn table: every spawned pawn on the map, the rows every
    /// other section's pawn reference resolves against. A free colonist
    /// carries every detail family but tend; a hostile or hunting predator
    /// the emergency census lists the combat detail; another animal its
    /// animal state; any other pawn the core row.
    /// </summary>
    public PawnSnapshot? Pawns { get; init; }

    /// <summary>
    /// The things table: every thing a food stock in colony_facts
    /// references, the rows those references resolve against.
    /// </summary>
    public ThingsSnapshot? Things { get; init; }

    /// <summary>
    /// The whole map as a cell grid, every frame: a keyframe on an
    /// open or keyframe request, on a map change and at least every 30
    /// seconds, else a delta against that keyframe. keyframe_seq numbers
    /// keyframes since the stream opened; a delta whose keyframe_seq is not
    /// the keyframe the reader holds follows a keyframe it missed: it asks
    /// for a keyframe. sky_glow is the map's sky light (0..1), which grid
    /// glow leaves out.
    /// </summary>
    public Mirror.CellGrid? Grid { get; init; }

    public ulong? KeyframeSeq { get; init; }

    public double? SkyGlow { get; init; }

    /// <summary>
    /// The player faction's primary ideoligion as it stands: the
    /// precepts and roles in force, ritual state and building precepts.
    /// Omittable like the singleton sections above (watermark "ideology");
    /// absent, with no watermark, without Ideology or a primary ideoligion.
    /// </summary>
    public IdeologySnapshot? Ideology { get; init; }

    /// <summary>
    /// A frame whose section read threw: the frame carries only this,
    /// with the section name, exception type and message in detail. A reader
    /// refuses the read naming the section; the frame is never carried on
    /// with the section omitted.
    /// </summary>
    public Common.Failure? Failure { get; init; }

    /// <summary>
    /// ModsConfig.IdeologyActive, carried on every frame: false means the
    /// Ideology expansion is not installed, so an absent ideology section is not
    /// an unread ideoligion. Absent stays unknown (unread).
    /// </summary>
    public bool? IdeologyActive { get; init; }
}

/// <summary>
/// seq counts a section's changes since the stream opened; captured_tick is
/// the tick of the frame that last carried it.
/// A keyed table section (pawns, buildings, things) is a keyframe,
/// carrying every row, unless delta is set: then it carries only the rows
/// whose content changed or appeared since the table at base_seq, plus the
/// ids that left it in removed, and the reader merges it into the table it
/// holds at base_seq (a different held seq is a gap: it asks for a keyframe).
/// An unchanged table is omitted like a singleton section.
/// </summary>
public sealed record SectionWatermark
{
    public string? Section { get; init; }

    public ulong? Seq { get; init; }

    public long? CapturedTick { get; init; }

    public bool? Delta { get; init; }

    public ulong? BaseSeq { get; init; }
}

public sealed record ObservationBatchSnapshot
{
    public Common.ObservationContext? StartContext { get; init; }

    public Common.ObservationContext? EndContext { get; init; }

    public StatusReply? StatusBefore { get; init; }

    public ListPawnsReply? Pawns { get; init; }

    public ListSuppliesReply? Supplies { get; init; }

    public ListBuildingsReply? Buildings { get; init; }

    public ListRoomsReply? Rooms { get; init; }

    public ListZonesReply? Zones { get; init; }

    public StatusReply? StatusAfter { get; init; }
}

public sealed record ObservationBatchRequest
{
    public ReadScope? Scope { get; init; }

    public StatusRequest? Status { get; init; }

    public ListPawnsRequest? Pawns { get; init; }

    public ListSuppliesRequest? Supplies { get; init; }

    public ListBuildingsRequest? Buildings { get; init; }

    public ListRoomsRequest? Rooms { get; init; }

    public ListZonesRequest? Zones { get; init; }
}

public sealed record ObservationBatchReply
{
    public ObservationBatchReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="ObservationBatchReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ObservationBatchReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(ObservationBatchReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(ObservationBatchReplyOutcome.FailureCase), "failure")]
public abstract record ObservationBatchReplyOutcome
{
    private protected ObservationBatchReplyOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public ObservationBatchSnapshot? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(ObservationBatchSnapshot Value) : ObservationBatchReplyOutcome;

    public static implicit operator ObservationBatchReplyOutcome(ObservationBatchSnapshot value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : ObservationBatchReplyOutcome;

    public static implicit operator ObservationBatchReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : ObservationBatchReplyOutcome;

    public static implicit operator ObservationBatchReplyOutcome(Common.Failure value) => new FailureCase(value);
}

/// <summary>
/// The snapshot stream: native publishes whole BundleSnapshot frames
/// into a named shared-memory ring and signals a named event; this request
/// opens it (or replaces its subscription) and names the ring. A frame carries
/// every state family whole (no page caps, no field masks) plus the
/// subscription's resource sources, and the whole map as a
/// cell grid. Clock status stays a dedicated read.
/// </summary>
public sealed record SnapshotStreamRequest
{
    /// <summary>
    /// list_resource_sources per named resource, no development
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> ResourceSources { get; init; }

    /// <summary>
    /// Only makes the next frame a keyframe, carrying every section;
    /// the subscription and the other fields are left as they are. Every
    /// other open is a keyframe too.
    /// </summary>
    public bool? Keyframe { get; init; }

    /// <summary>
    /// Main-thread milliseconds one frame may spend measuring route path costs
    /// (FindPathNow per reachable facility x colonist pair, ~0.4 ms each). Pairs
    /// past it report RouteTravel.path_skipped. Absent: measure every pair.
    /// </summary>
    public uint? RoutePathBudgetMs { get; init; }

    /// <summary>
    /// The ColonyFactsRequest herd_radius and hunt_route_budget_ms of each frame's colony facts read.
    /// </summary>
    public uint? HerdRadius { get; init; }

    public uint? HuntRouteBudgetMs { get; init; }

    public float? HuntPredatorMarginCells { get; init; }

    /// <summary>
    /// The LinesOfFireRequest budget_ms of each frame's combat lines of fire.
    /// </summary>
    public uint? LinesOfFireBudgetMs { get; init; }
}

/// <summary>
/// The definition catalog: the generated def rows and the stat environment
/// the planning views read (research projects are ResearchProjectDef rows of
/// `defs`). `context` and `stat_env` are per load; every other field is static for
/// the game process (defs load once at boot) and is the part `fingerprint` names.
/// A read that sends `held_fingerprint` equal to the game's own gets only the per-load
/// fields and `fingerprint`; any other read gets the static part too.
/// </summary>
public sealed record DefinitionCatalog
{
    public Common.ObservationContext? Context { get; init; }

    /// <summary>
    /// Biotech defs; absent without Biotech.
    /// </summary>
    public BiotechCatalog? Biotech { get; init; }

    public CatalogDerived? Derived { get; init; }

    /// <summary>
    /// The game state Go's stat evaluator (stateval) reads beyond the rows.
    /// </summary>
    public StatEnv? StatEnv { get; init; }

    /// <summary>
    /// The base classes of every class the def rows name: the class of each
    /// mirrored object (each def's own class among them) and each System.Type
    /// value in a row (worker, condition, thing classes), sorted by name. Go
    /// matches a family against it (`DefinitionCatalog.ClassIsA`) with no name list.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<ClassChain>))]
    public EquatableArray<ClassChain> ClassChains { get; init; }

    /// <summary>
    /// What the game's own code says about every ThingDef, computed once per
    /// load; Go reads it and never ports the rule.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<ThingDefFacts>))]
    public EquatableArray<ThingDefFacts> ThingFacts { get; init; }

    /// <summary>
    /// Names the static part: game version, language, and each active mod's id,
    /// version and def-file write times. Always set.
    /// </summary>
    public string Fingerprint { get; init; } = "";
}

/// <summary>
/// A class by CLR full name and its base classes, nearest first, up to but not
/// including System.Object.
/// </summary>
public sealed record ClassChain
{
    public string Name { get; init; } = "";

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Bases { get; init; }
}

/// <summary>
/// One ThingDef's game-computed flags: the race facts of a def with RaceProperties.
/// A food's kind and a meal's ingredients, ThingDef.IsMeat, IsMedicine and the
/// room-role names are Go's, over the def rows (bridge/food_rules.go,
/// bridge/catalog_items.go).
/// </summary>
public sealed record ThingDefFacts
{
    public string DefName { get; init; } = "";

    /// <summary>
    /// Set on a def with RaceProperties.
    /// </summary>
    public RaceFacts? Race { get; init; }
}

/// <summary>
/// What the game's own race code says about one race that its def fields do not:
/// `edible_defs` are the foods (ThingDefFacts.food_kind set) RaceProperties
/// .CanEverEat accepts, sorted. The race flags, trainables, meat and life stage
/// ages are Go's, derived from the RaceProperties, FleshTypeDef, LifeStageDef and
/// TrainableDef rows and the evaluated stats (bridge/race_rules.go).
/// </summary>
public sealed record RaceFacts
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> EdibleDefs { get; init; }

    /// <summary>
    /// TrainableUtility.TamenessCanDecay(def) and DegradationPeriodTicks(def): a
    /// tamed animal loses one Tameness step every period unless it cannot decay.
    /// </summary>
    public bool TamenessCanDecay { get; init; }

    public int TamenessDecayPeriodTicks { get; init; }

    /// <summary>
    /// InteractionWorker_RecruitAttempt's wildness curve at the race's Wildness
    /// stat: the factor a tame attempt's chance is multiplied by (before the
    /// tamer's TameAnimalChance, bond, prison and venerated-animal factors).
    /// </summary>
    public float TameChanceFactor { get; init; }

    /// <summary>
    /// SimplifiedPastureNutritionSimulator.NutritionConsumedPerDay(def, adult
    /// life stage): the nutrition per day one adult of the race eats, the
    /// game's own feed figure for an owned adult, so a colony with none of the
    /// race can price its feed. Absent for a race with no life stages.
    /// </summary>
    public double? AdultFeedPerDay { get; init; }
}

/// <summary>
/// The game state a Go stat evaluation reads that the def rows do not hold
/// (stateval.Env), read once per load beside the rows. `active_mods` are the
/// package ids (lower case) of ModsConfig.ActiveModsInLoadOrder. `scenario_factors`
/// are Scenario.GetStatFactor for every StatDef whose factor is not 1.
/// `difficulty_flags` are the public bool fields of Find.Storyteller.difficulty by
/// field name (what CostListForDifficulty.difficultyVar names).
/// </summary>
public sealed record StatEnv
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> ActiveMods { get; init; }

    public bool ClassicMode { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<StatFactor>))]
    public EquatableArray<StatFactor> ScenarioFactors { get; init; }

    public float ButcherYieldFactor { get; init; }

    public float FishingYieldFactor { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<DifficultyFlag>))]
    public EquatableArray<DifficultyFlag> DifficultyFlags { get; init; }
}

public sealed record StatFactor
{
    public string Stat { get; init; } = "";

    public float Factor { get; init; }
}

public sealed record DifficultyFlag
{
    public string Name { get; init; } = "";

    public bool Value { get; init; }
}

/// <summary>
/// The catalog facts the game names or computes in code, which no const of
/// game_constants carries (the numeric constants are the typed GameConstants).
/// </summary>
public sealed record CatalogDerived
{
    /// <summary>
    /// The defName of ThingDefOf.Silver: the def Tradeable.IsCurrency tests, so
    /// the coin every price and the colony census count in.
    /// </summary>
    public string CurrencyDef { get; init; } = "";

    /// <summary>
    /// The temperature (C) at which GenTemperature.RotRateAtTemperature first
    /// reaches its full rate of 1, found by evaluating the game's own function:
    /// food stored at or below it rots slower than the full rate.
    /// </summary>
    public float FullRotRateC { get; init; }

    /// <summary>
    /// The defName of ThingDefOf.Wort: what Building_FermentingBarrel takes in
    /// and turns into ThingDefOf.Beer, so the beer reserve names the
    /// intermediate without typing it.
    /// </summary>
    public string WortDef { get; init; } = "";
}

/// <summary>
/// The player faction's primary ideoligion: FactionIdeosTracker.PrimaryIdeo.
/// Precept ids are Precept.GetUniqueLoadID(); every def name resolves to a
/// catalog def row (PreceptDef, MemeDef, RitualPatternDef).
/// </summary>
public sealed record IdeologySnapshot
{
    public Common.ObservationContext? Context { get; init; }

    public string? IdeoId { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Memes { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<IdeoPrecept>))]
    public EquatableArray<IdeoPrecept> Precepts { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<IdeoRole>))]
    public EquatableArray<IdeoRole> Roles { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<IdeoRitual>))]
    public EquatableArray<IdeoRitual> Rituals { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<IdeoBuilding>))]
    public EquatableArray<IdeoBuilding> Buildings { get; init; }

    /// <summary>
    /// Ideo.ObligationsActive, Ideo.ColonistBelieverCountCached and
    /// Ideo.MinBelieversToEnableObligations.
    /// </summary>
    public bool? ObligationsActive { get; init; }

    public int? Believers { get; init; }

    public int? MinBelieversForObligations { get; init; }

    public bool? Fluid { get; init; }

    public bool? CanReform { get; init; }

    public int? DevelopmentPoints { get; init; }

    public int? ReformCount { get; init; }

    public int? NextReformPoints { get; init; }
}

/// <summary>
/// A precept in force, role, ritual and building precepts excluded.
/// </summary>
public sealed record IdeoPrecept
{
    public string? Id { get; init; }

    public string? DefName { get; init; }
}

/// <summary>
/// A role precept: whether it is active (enough believers) and the pawns
/// holding it (Precept_Role.ChosenPawns), each a pawn row ref.
/// </summary>
public sealed record IdeoRole
{
    public string? Id { get; init; }

    public string? DefName { get; init; }

    public bool? Active { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Ref>))]
    public EquatableArray<Common.Ref> Pawns { get; init; }
}

/// <summary>
/// A ritual precept: its PreceptDef and RitualPatternDef (sourcePattern), the
/// raw Precept_Ritual.lastFinishedTick, the count of active obligations,
/// whether the repeat penalty applies and whether a LordJob_Ritual of the
/// precept is running now.
/// </summary>
public sealed record IdeoRitual
{
    public string? Id { get; init; }

    public string? DefName { get; init; }

    public string? Pattern { get; init; }

    public int? LastFinishedTick { get; init; }

    public int? ActiveObligations { get; init; }

    public bool? RepeatPenaltyActive { get; init; }

    public bool? Running { get; init; }
}

/// <summary>
/// A building precept: the ThingDef it takes.
/// </summary>
public sealed record IdeoBuilding
{
    public string? Id { get; init; }

    public string? DefName { get; init; }

    public string? Building { get; init; }
}

/// <summary>
/// One point of a game SimpleCurve.
/// </summary>
public sealed record CurvePointRow
{
    public double? X { get; init; }

    public double? Y { get; init; }
}

/// <summary>
/// The Biotech tuning constants the defs do not carry, read natively
/// from GeneTuning and the gene extractor, a singleton per load.
/// biostat_min and biostat_max are GeneTuning.BiostatRange (the metabolism
/// bound of a gene set); base_max_complexity GeneTuning.BaseMaxComplexity;
/// creation_hours_curve ComplexityToCreationHoursCurve (complexity to hours of
/// assembler work, ascending x); regrow_days_min and regrow_days_max
/// GeneExtractorRegrowingDurationDaysRange; extract_ticks the extractor's
/// TicksToExtract and no_power_eject_ticks its cumulative power-cut limit
/// (private constants read by reflection, absent when unreadable). An absent
/// scalar is unknown, never zero.
/// </summary>
public sealed record GeneTuningFacts
{
    public int? BiostatMin { get; init; }

    public int? BiostatMax { get; init; }

    public int? BaseMaxComplexity { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<CurvePointRow>))]
    public EquatableArray<CurvePointRow> CreationHoursCurve { get; init; }

    public double? RegrowDaysMin { get; init; }

    public double? RegrowDaysMax { get; init; }

    public int? ExtractTicks { get; init; }

    public int? NoPowerEjectTicks { get; init; }
}

/// <summary>
/// The def names the game picks by role from MechWorkModeDefOf: the mech work
/// modes of work, escort and recharge. A def row cannot say which is which.
/// </summary>
public sealed record MechWorkModeRoles
{
    public string? Work { get; init; }

    public string? Escort { get; init; }

    public string? Recharge { get; init; }
}

/// <summary>
/// The Biotech facts the def mirror (defs.proto) cannot give. The life stage,
/// race, gene, xenotype, mech kind and work mode defs are the mirror's rows;
/// the decoded BiotechCatalog is built from them. Absent without Biotech.
/// </summary>
public sealed record BiotechCatalog
{
    public GeneTuningFacts? GeneTuning { get; init; }

    public MechWorkModeRoles? MechWorkModes { get; init; }
}

/// <summary>
/// Odyssey facts of one building row. hackable: a building with
/// CompHackable; portal: a MapPortal (hatch, pit gate, cave exit). An absent
/// scalar is unknown; a failed sub-read leaves its block absent and adds a
/// ReadIssue named hackable or portal.
/// </summary>
public sealed record OdysseyBuilding
{
    public HackableState? Hackable { get; init; }

    public PortalState? Portal { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }
}

/// <summary>
/// CompHackable: progress_percent is 0..1, defence the hack work required.
/// </summary>
public sealed record HackableState
{
    public double? ProgressPercent { get; init; }

    public double? Defence { get; init; }

    public bool? Hacked { get; init; }

    public bool? LockedOut { get; init; }

    public bool? Autohack { get; init; }
}

/// <summary>
/// MapPortal: pocket_map_exists and the pocket map's id once it exists;
/// an ancient hatch adds its stockpile type (TileMutatorWorker_Stockpile.StockpileType name) and
/// layout (LayoutDef name).
/// </summary>
public sealed record PortalState
{
    public bool? PocketMapExists { get; init; }

    public int? PocketMapId { get; init; }

    public string? StockpileType { get; init; }

    public string? Layout { get; init; }
}

/// <summary>
/// Anomaly facts of one pawn row. entity is Pawn.IsEntity, mutant
/// Pawn.IsMutant and shambler Pawn.IsShambler (hostility is PawnState.hostile).
/// min_containment_strength is the pawn's StatDefOf.MinimumContainmentStrength.
/// held is set for a pawn with a CompHoldingPlatformTarget, study for one with
/// a CompStudiable. A scalar absent is unknown; a failed sub-read leaves its
/// block absent and adds a ReadIssue named entity, held or study; a failed threat fact is absent with a ReadIssue named for it.
/// Threat facts for defense tactics, the combat group: hidden_from_player
/// is InvisibilityUtility.IsHiddenFromPlayer (the player cannot see or target the
/// pawn); psychic_ritual_invoker is a pawn whose role in its lord's psychic ritual
/// is the ritual def's invoker role (the caster); melee_only is a pawn whose
/// CurrentEffectiveVerb is a melee attack and who has no ability its def marks
/// ai_IsOffensive.
/// </summary>
public sealed record PawnAnomaly
{
    public bool? Entity { get; init; }

    public bool? Mutant { get; init; }

    public bool? Shambler { get; init; }

    public double? MinContainmentStrength { get; init; }

    public HeldState? Held { get; init; }

    public StudyState? Study { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }

    public bool? HiddenFromPlayer { get; init; }

    public bool? PsychicRitualInvoker { get; init; }

    public bool? MeleeOnly { get; init; }

    public CreepJoinerState? Creepjoiner { get; init; }
}

/// <summary>
/// A creepjoiner's facts, set for a pawn with a creepjoiner tracker
/// (a failed read leaves it absent with a ReadIssue named creepjoiner). form and
/// benefit are CreepJoinerFormKindDef and CreepJoinerBenefitDef names, which a
/// player sees in the offer letter. downside_triggered is the tracker's
/// triggeredDownside, true once the game has shown the downside; the downside
/// def itself is hidden information and is never read.
/// </summary>
public sealed record CreepJoinerState
{
    public string? Form { get; init; }

    public string? Benefit { get; init; }

    public bool? DownsideTriggered { get; init; }
}

/// <summary>
/// CompHoldingPlatformTarget: held is CurrentlyHeldOnPlatform and platform the
/// Building_HoldingPlatform holding the pawn; mode the ordered containment
/// mode; escaping the pawn has begun to escape; can_be_captured whether the
/// game lets the player capture it. needs_tend is Pawn.health
/// HasHediffsNeedingTend and bleeding a positive BleedRateTotal: a
/// held entity is tended like any patient or bleeds out. harvester_attached is
/// Building_HoldingPlatform.HasAttachedBioferriteHarvester (a powered harvester
/// linked to the platform, which forces extract_bioferrite false) and
/// bioferrite_per_day CompProducesBioferrite.BioferritePerDay: body size
/// times the comp's density, 0 for an entity that produces none and while the
/// BioferriteExtracted hediff stands (8 days after an extraction).
/// </summary>
public sealed record HeldState
{
    public bool? Held { get; init; }

    public Common.Ref? Platform { get; init; }

    public EntityContainmentModeKind? Mode { get; init; }

    public bool? Escaping { get; init; }

    public bool? ExtractBioferrite { get; init; }

    public bool? CanBeCaptured { get; init; }

    public bool? NeedsTend { get; init; }

    public bool? Bleeding { get; init; }

    public bool? HarvesterAttached { get; init; }

    public double? BioferritePerDay { get; init; }
}

/// <summary>
/// CompStudiable: progress_percent is 0..1; knowledge_category and
/// anomaly_knowledge are the values the game resolves for this thing;
/// anomaly_knowledge_gained is what its study has already yielded.
/// </summary>
public sealed record StudyState
{
    public bool? StudyEnabled { get; init; }

    public bool? Completed { get; init; }

    public double? ProgressPercent { get; init; }

    public double? StudyPoints { get; init; }

    public double? AnomalyKnowledgeGained { get; init; }

    public int? StudyInteractions { get; init; }

    public bool? EverStudiable { get; init; }

    public bool? CurrentlyStudiable { get; init; }

    public string? KnowledgeCategory { get; init; }

    public double? AnomalyKnowledge { get; init; }
}

/// <summary>
/// Anomaly facts of one building row: holder is a CompEntityHolder
/// (a holding platform), study a CompStudiable. A failed sub-read leaves its
/// block absent and adds a ReadIssue named holder or study.
/// </summary>
public sealed record AnomalyBuilding
{
    public EntityHolderState? Holder { get; init; }

    public StudyState? Study { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }
}

/// <summary>
/// CompEntityHolder: containment_strength is the strength the holder's room
/// provides now (CompEntityHolder.ContainmentStrength), available whether it can take
/// a pawn, held_pawn the pawn it holds. doors are the Building_Door things the
/// game counts for the holder's room (StatWorker_ContainmentStrength
/// CalculateDoorStats: the doors in its boundary), read in their own try: a
/// failed read leaves them empty with a ReadIssue named doors.
/// </summary>
public sealed record EntityHolderState
{
    public double? ContainmentStrength { get; init; }

    public bool? Available { get; init; }

    public Common.Ref? HeldPawn { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<AnomalyDoor>))]
    public EquatableArray<AnomalyDoor> Doors { get; init; }
}

/// <summary>
/// One door of a holder's room: its cell, Building_Door.Open and HoldOpen,
/// ContainmentBreached (open for 600 or more ticks, Building_Door
/// TicksOpenToBreach, which zeroes the room's door strength) and
/// BlockedOpenMomentary (a thing or pawn stands in the doorway, or the door is
/// stuck open, so it cannot close).
/// </summary>
public sealed record AnomalyDoor
{
    public Common.Cell? Cell { get; init; }

    public bool? Open { get; init; }

    public bool? HoldOpen { get; init; }

    public bool? ContainmentBreached { get; init; }

    public bool? BlockedOpen { get; init; }
}

public sealed record DefinitionCatalogRequest
{
    public ReadScope? Scope { get; init; }

    public bool? Creation { get; init; }

    /// <summary>
    /// The fingerprint of the static part the caller already holds (scoped reads only).
    /// </summary>
    public string? HeldFingerprint { get; init; }
}

/// <summary>
/// The same static def mirror before a world exists; no invented map/load identity.
/// </summary>
public sealed record CreationDefinitionCatalog
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<ClassChain>))]
    public EquatableArray<ClassChain> ClassChains { get; init; }
}

public sealed record DefinitionCatalogReply
{
    public DefinitionCatalogReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="DefinitionCatalogReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(DefinitionCatalogReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(DefinitionCatalogReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(DefinitionCatalogReplyOutcome.FailureCase), "failure")]
[JsonDerivedType(typeof(DefinitionCatalogReplyOutcome.CreationCase), "creation")]
public abstract record DefinitionCatalogReplyOutcome
{
    private protected DefinitionCatalogReplyOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public DefinitionCatalog? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(DefinitionCatalog Value) : DefinitionCatalogReplyOutcome;

    public static implicit operator DefinitionCatalogReplyOutcome(DefinitionCatalog value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : DefinitionCatalogReplyOutcome;

    public static implicit operator DefinitionCatalogReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : DefinitionCatalogReplyOutcome;

    public static implicit operator DefinitionCatalogReplyOutcome(Common.Failure value) => new FailureCase(value);

    /// <summary>The <c>Creation</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public CreationDefinitionCatalog? Creation => (this as CreationCase)?.Value;

    public sealed record CreationCase(CreationDefinitionCatalog Value) : DefinitionCatalogReplyOutcome;

    public static implicit operator DefinitionCatalogReplyOutcome(CreationDefinitionCatalog value) => new CreationCase(value);
}

public sealed record SnapshotStreamOpened
{
    /// <summary>
    /// mapping name; the ready event is name + "-ready"
    /// </summary>
    public string Name { get; init; } = "";

    public uint Slots { get; init; }

    public uint SlotBytes { get; init; }
}

public sealed record SnapshotStreamReply
{
    public SnapshotStreamReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="SnapshotStreamReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SnapshotStreamReplyOutcome.OpenedCase), "opened")]
[JsonDerivedType(typeof(SnapshotStreamReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(SnapshotStreamReplyOutcome.FailureCase), "failure")]
public abstract record SnapshotStreamReplyOutcome
{
    private protected SnapshotStreamReplyOutcome() { }

    /// <summary>The <c>Opened</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public SnapshotStreamOpened? Opened => (this as OpenedCase)?.Value;

    public sealed record OpenedCase(SnapshotStreamOpened Value) : SnapshotStreamReplyOutcome;

    public static implicit operator SnapshotStreamReplyOutcome(SnapshotStreamOpened value) => new OpenedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : SnapshotStreamReplyOutcome;

    public static implicit operator SnapshotStreamReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : SnapshotStreamReplyOutcome;

    public static implicit operator SnapshotStreamReplyOutcome(Common.Failure value) => new FailureCase(value);
}

/// <summary>
/// Makes the next frame capture if any applied write is uncaptured;
/// the reply does not wait for the capture.
/// </summary>
public sealed record FlushSnapshotRequest;

public sealed record FlushSnapshotReply
{
    public FlushSnapshotReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="FlushSnapshotReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(FlushSnapshotReplyOutcome.FlushedCase), "flushed")]
[JsonDerivedType(typeof(FlushSnapshotReplyOutcome.FailureCase), "failure")]
public abstract record FlushSnapshotReplyOutcome
{
    private protected FlushSnapshotReplyOutcome() { }

    /// <summary>The <c>Flushed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public bool? Flushed => (this as FlushedCase)?.Value;

    public sealed record FlushedCase(bool Value) : FlushSnapshotReplyOutcome;

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : FlushSnapshotReplyOutcome;

    public static implicit operator FlushSnapshotReplyOutcome(Common.Failure value) => new FailureCase(value);
}

/// <summary>
/// The pawn's current food policy: its load id and allowed definitions.
/// </summary>
public sealed record FoodRestriction
{
    public string? PolicyId { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> AllowedDefs { get; init; }
}

public sealed record ApparelPolicyState
{
    public string? Token { get; init; }

    public string? Name { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> AllowedDefs { get; init; }

    public float? MinHitPoints { get; init; }

    public float? MaxHitPoints { get; init; }

    public int? MinQuality { get; init; }

    public int? MaxQuality { get; init; }

    public bool? ExcludesTainted { get; init; }

    public bool? Child { get; init; }

    public bool? Slave { get; init; }

    public bool? IncapableOfViolence { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<WorkSetting>))]
    public EquatableArray<WorkSetting> Work { get; init; }

    public bool? Drafted { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Skill>))]
    public EquatableArray<Skill> Skills { get; init; }

    /// <summary>
    /// The pawn's short name (its outfit's label), the load id of its current
    /// outfit, the definitions its royal title, ideoligion role and
    /// ideoligion apparel precepts require, and whether it goes nude (nudist
    /// trait or mandatory nudity precept).
    /// </summary>
    public string? PawnName { get; init; }

    public string? PolicyId { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> RequiredDefs { get; init; }

    public bool? Nude { get; init; }
}

