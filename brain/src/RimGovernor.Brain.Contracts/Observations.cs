using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Contracts.Wire.Observations;

/// <summary>
/// Complete census of visible, deconstructible non-player buildings touching Home.
/// A null roof_blocker means safe removal.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<ClearanceClass>))]
public enum ClearanceClass
{
    AncientWallDoor,
    ShipChunk,
    AncientCasket,
    Other,
}

[JsonConverter(typeof(WireEnumConverter<SurgeryKind>))]
public enum SurgeryKind
{
    /// <summary>
    /// Installs a part where the body part is missing.
    /// </summary>
    Restore,
    /// <summary>
    /// Removes a condition.
    /// </summary>
    Cure,
    /// <summary>
    /// Removes a damaged or artificial part.
    /// </summary>
    Amputate,
    /// <summary>
    /// Installs over a present part, or an implant.
    /// </summary>
    Install,
    /// <summary>
    /// Removes a clean natural part.
    /// </summary>
    Harvest,
    /// <summary>
    /// Any other medical recipe (administer, execute).
    /// </summary>
    Other,
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
/// HolderKind is what holds a held stack: a building container, a corpse's
/// inventory, a living pawn's inventory or its carry tracker.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<HolderKind>))]
public enum HolderKind
{
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
/// StockCategory and StockOwnership scope a supplies read: which things it counts and whose.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<StockCategory>))]
public enum StockCategory
{
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
    Blueprint,
    Frame,
    Built,
}

/// <summary>
/// How the game's own designators would remove a cover thing.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<CoverKind>))]
public enum CoverKind
{
    Plant,
    Chunk,
    Mineable,
    Building,
}

/// <summary>
/// Excavation reads certify a requested set of rock cells for staged room/corridor
/// excavation. Unlike surface mining, roofed cells are admissible; support is a
/// counterfactual check that every remaining roof stays within the native support
/// radius of a holder after all requested cells are removed, judged through fog.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<ExcavationSupport>))]
public enum ExcavationSupport
{
    Supported,
    Unsupported,
}

[JsonConverter(typeof(WireEnumConverter<QuestObjectiveKind>))]
public enum QuestObjectiveKind
{
    [WireName("produce_item")]
    ProduceItem,
    [WireName("harvest_plant")]
    HarvestPlant,
    [WireName("kill_animals")]
    KillAnimals,
    [WireName("host_lodgers")]
    HostLodgers,
    [WireName("accept_requirement_unmet")]
    AcceptRequirementUnmet,
    [WireName("load_pawns")]
    LoadPawns,
    [WireName("load_named_pawns")]
    LoadNamedPawns,
    [WireName("monument")]
    Monument,
    [WireName("expiry")]
    Expiry,
    [WireName("rescue_pawns")]
    RescuePawns,
    [WireName("inspect_grav_engine")]
    InspectGravEngine,
    [WireName("hold_survey_scanner")]
    HoldSurveyScanner,
    [WireName("hack_things")]
    HackThings,
    [WireName("give_items")]
    GiveItems,
}

[JsonConverter(typeof(WireEnumConverter<WorldSiteState>))]
public enum WorldSiteState
{
    [WireName("pending")]
    Pending,
    [WireName("spawned")]
    Spawned,
    [WireName("map_loaded")]
    MapLoaded,
    [WireName("destroyed")]
    Destroyed,
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
/// The production a delivery was counted under. Its wire name (<c>crop</c>, <c>fish</c>, <c>forage</c>,
/// <c>animal_product</c>) also prefixes the brain's counter groups (<c>crop:&lt;zone&gt;</c>).
/// </summary>
[JsonConverter(typeof(WireEnumConverter<DeliveryKind>))]
public enum DeliveryKind
{
    Crop,
    Fish,
    Forage,
    AnimalProduct,
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
    Prisoner,
    Animal,
}

/// <summary>
/// EntityContainmentMode: what the player has ordered done with a held entity.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<EntityContainmentModeKind>))]
public enum EntityContainmentModeKind
{
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

/// <summary>
/// One deconstructible building of the clearance census: every non-player building touching Home or deconstructible
/// anywhere, and the player's own buildings on planned ground. Its whole footprint is in bounds and unfogged.
/// </summary>
public sealed record ClearanceTarget
{
    public required string EntityId { get; init; }

    public required string DefName { get; init; }

    /// <summary>The footprint's minimum corner.</summary>
    public required Cell Minimum { get; init; }

    /// <summary>The footprint's maximum corner, inclusive.</summary>
    public required Cell Maximum { get; init; }

    public required ClearanceClass Class { get; init; }

    /// <summary>Why removing the building would collapse roof; null when removal is safe.</summary>
    public required string? RoofBlocker { get; init; }

    public required bool AncientDanger { get; init; }

    /// <summary>
    /// A standing Deconstruct designation; the controller adopts it (no ownership ledger).
    /// </summary>
    public required bool Designated { get; init; }

    /// <summary>The building is the player's own (reported only on planned ground).</summary>
    public required bool Player { get; init; }

    /// <summary>
    /// The building is a player wall or door on the boundary of a room that is not outdoors; false for every
    /// non-player row.
    /// </summary>
    public required bool EnclosesRoom { get; init; }

    public required ClearanceSalvage Salvage { get; init; }
}

/// <summary>A clearance row's salvage evidence: not asked for, past the read's time budget, or computed.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ClearanceSalvage.NotRequested), "not_requested")]
[JsonDerivedType(typeof(ClearanceSalvage.Skipped), "skipped")]
[JsonDerivedType(typeof(ClearanceSalvage.Computed), "computed")]
public abstract record ClearanceSalvage
{
    private protected ClearanceSalvage() { }

    /// <summary>The read did not ask for salvage (include_salvage false).</summary>
    public sealed record NotRequested : ClearanceSalvage;

    /// <summary>The salvage time budget ran out before native computed this row; the frame refresher fills it later.</summary>
    public sealed record Skipped : ClearanceSalvage;

    public sealed record Computed(SalvageEvidence Evidence) : ClearanceSalvage;
}

/// <summary>
/// A constructed floor on planned ground: vanilla's foundation-free floor
/// layer (TerrainGrid.CanRemoveTopLayerAt), one row per cell. designated is a
/// standing RemoveFloor designation.
/// </summary>
public sealed record ClearanceFloor
{
    public required Cell Cell { get; init; }

    public required string DefName { get; init; }

    public required bool Designated { get; init; }
}

public sealed record SalvageYield
{
    public required string DefName { get; init; }

    public required long Count { get; init; }

    public required double UnitValue { get; init; }

    public required long StorageHeadroom { get; init; }
}

public sealed record SalvageEvidence
{
    public required bool Safe { get; init; }

    public required double PathLength { get; init; }

    public required double Labor { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<SalvageYield>))]
    public required EquatableArray<SalvageYield> Yields { get; init; }
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

public sealed record ClearanceTargetsSnapshot
{
    public required Common.ObservationContext Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ClearanceTarget>))]
    public required EquatableArray<ClearanceTarget> Targets { get; init; }

    /// <summary>
    /// Only cells inside the request's planned_ground.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<ClearanceFloor>))]
    public required EquatableArray<ClearanceFloor> Floors { get; init; }
}

public sealed record MapSize
{
    public uint Width { get; init; }

    public uint Height { get; init; }
}

public sealed record DefinitionRef
{
    public string DefName { get; init; } = "";
}

/// <summary>
/// A row's head: the thing's load id, def name and label, and where it is (<see cref="EntityPlace"/>).
/// </summary>
public sealed record EntityRef
{
    public required string Id { get; init; }

    public required string DefName { get; init; }

    public required string Label { get; init; }

    public required EntityPlace Place { get; init; }
}

/// <summary>
/// Where a row's thing is: <see cref="OnMap"/> at a map's cell (a held thing reads at its holder's cell), or
/// <see cref="Unplaced"/> for a thing on no map (a caravan, a world pawn's gear).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(EntityPlace.OnMap), "on_map")]
[JsonDerivedType(typeof(EntityPlace.Unplaced), "unplaced")]
public abstract record EntityPlace
{
    private protected EntityPlace() { }

    public sealed record OnMap(int MapId, Common.Cell Cell) : EntityPlace;

    public sealed record Unplaced : EntityPlace;
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

/// <summary>
/// The pawn's job tracker: the current job (def_name, load_id and target_a absent when it has none),
/// whether the player forced it and how many jobs are queued behind it.
/// </summary>
public sealed record JobEvidence
{
    public string? DefName { get; init; }

    public bool PlayerForced { get; init; }

    public TargetRef? TargetA { get; init; }

    public uint QueuedJobs { get; init; }

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

    /// <summary>
    /// preemptible: the pawn is controlled with no committed coupled order, so its job is vanilla job
    /// selection any coupled order may replace (#3670). Occupancy reads it as free; the job and target
    /// are still reported so target claims see what it holds.
    /// </summary>
    public bool Preemptible { get; init; }
}

/// <summary>
/// The pawn's need levels (0-1). A need the pawn lacks (an animal has no joy, a mechanoid no food) is
/// <see cref="NeedLevel.Lacks"/>; the food need carries its hunger category.
/// </summary>
public sealed record PawnNeeds
{
    public required FoodNeed Food { get; init; }

    public required NeedLevel Rest { get; init; }

    public required NeedLevel Mood { get; init; }

    public required NeedLevel Joy { get; init; }

    /// <summary>
    /// MentalBreaker.BreakThresholdMinor: the mood below which minor breaks can start.
    /// </summary>
    public required double BreakThresholdMinor { get; init; }

    /// <summary>
    /// Pawn.GetPsylinkLevel(): 0 without a psylink, and always 0 when Royalty is inactive.
    /// </summary>
    public required int PsylinkLevel { get; init; }
}

/// <summary>One need's level (0-1), or Lacks when the pawn has no such need.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NeedLevel.Has), "has")]
[JsonDerivedType(typeof(NeedLevel.Lacks), "lacks")]
public abstract record NeedLevel
{
    private protected NeedLevel() { }

    public sealed record Has(double Level) : NeedLevel;

    public sealed record Lacks : NeedLevel;
}

/// <summary>The food need's level (0-1) and hunger category, or Lacks when the pawn has no food need.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(FoodNeed.Has), "has")]
[JsonDerivedType(typeof(FoodNeed.Lacks), "lacks")]
public abstract record FoodNeed
{
    private protected FoodNeed() { }

    public sealed record Has(double Level, Defs.HungerCategory Hunger) : FoodNeed;

    public sealed record Lacks : FoodNeed;
}

/// <summary>A body part of the pawn's race: its def and its index in the race's body (#3770).</summary>
public sealed record BodyPartRef
{
    public required int Index { get; init; }

    public required string DefName { get; init; }
}

public sealed record Hediff
{
    /// <summary>
    /// The HediffDef is the catalog row by def_name; its label is the row's.
    /// </summary>
    public required string DefName { get; init; }

    /// <summary>The body part the hediff is on; absent for a whole-body hediff.</summary>
    public required BodyPartRef? Part { get; init; }

    public required double Severity { get; init; }

    public required bool Visible { get; init; }

    public required HediffTend Tend { get; init; }

    /// <summary>The disease's immunity facts; absent unless the hediff is immunizable.</summary>
    public required HediffImmunity? Immunizable { get; init; }
}

/// <summary>
/// Whether a hediff is tended now. Only a hediff with a tend duration can be tended, so a tended hediff always
/// carries its tend quality.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HediffTend.Untended), "untended")]
[JsonDerivedType(typeof(HediffTend.Tended), "tended")]
public abstract record HediffTend
{
    private HediffTend()
    {
    }

    public sealed record Untended : HediffTend;

    public sealed record Tended(double Quality) : HediffTend;
}

/// <summary>
/// An immunizable hediff's immunity and its instantaneous native rates per 60000 game ticks. The severity rate
/// includes tending's severity modifier and the disease's randomized native progression.
/// </summary>
public sealed record HediffImmunity
{
    public required double Immunity { get; init; }

    public required double SeverityPerDay { get; init; }

    public required ImmunityGain Gain { get; init; }
}

/// <summary>
/// The pawn's immunity gain against the hediff: a rate per day, none yet because the pawn has no immunity record
/// for the def, or none because the pawn is dead. Read only: native never creates an immunity record.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ImmunityGain.PerDay), "per_day")]
[JsonDerivedType(typeof(ImmunityGain.NoRecord), "no_record")]
[JsonDerivedType(typeof(ImmunityGain.Dead), "dead")]
public abstract record ImmunityGain
{
    private ImmunityGain()
    {
    }

    public sealed record PerDay(double Value) : ImmunityGain;

    public sealed record NoRecord : ImmunityGain;

    public sealed record Dead : ImmunityGain;
}

public sealed record SurgeryBill
{
    public required string Id { get; init; }

    public required string Recipe { get; init; }

    /// <summary>The part the bill operates on; absent for a whole-body bill.</summary>
    public required BodyPartRef? Part { get; init; }
}

/// <summary>
/// The pawn's health detail: every hediff, none filtered, and the vanilla health summaries.
/// </summary>
public sealed record PawnHealth
{
    public required double SummaryFraction { get; init; }

    public required bool NeedsTend { get; init; }

    public required bool Bleeding { get; init; }

    public required double BleedRatePerDay { get; init; }

    public required double BloodLoss { get; init; }

    public required BleedOut BleedOut { get; init; }

    public required bool LifeThreatening { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Hediff>))]
    public required EquatableArray<Hediff> Hediffs { get; init; }

    public required bool ShouldSeekMedicalRest { get; init; }

    /// <summary>
    /// The bed the pawn lies in; absent when it is in none.
    /// </summary>
    public required string? BedId { get; init; }

    public required SnapshotRef Snapshot { get; init; }

    /// <summary>
    /// The pawn's surgery facts; absent for a dead pawn or one off every map, which has no operations.
    /// </summary>
    public required PawnSurgery? Surgery { get; init; }
}

/// <summary>
/// HealthUtility.TicksUntilDeathDueToBloodLoss for a living, bleeding pawn: <see cref="Within"/> a finite number
/// of hours, or <see cref="NotBleedingOut"/> (not bleeding, dead, or bleeding toward no finite death).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(BleedOut.Within), "within")]
[JsonDerivedType(typeof(BleedOut.NotBleedingOut), "not_bleeding_out")]
public abstract record BleedOut
{
    private BleedOut()
    {
    }

    public sealed record Within(double Hours) : BleedOut;

    public sealed record NotBleedingOut : BleedOut;
}

/// <summary>
/// The medical care cap inputs of a pawn: the life threat and every hediff, none filtered.
/// </summary>
public sealed record PawnConditions
{
    public required bool LifeThreatening { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Hediff>))]
    public required EquatableArray<Hediff> Hediffs { get; init; }
}

/// <summary>
/// Surgery facts. Native computes every value through vanilla; the brain never recomputes them.
/// </summary>
public sealed record PawnSurgery
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<SurgeryBill>))]
    public required EquatableArray<SurgeryBill> SurgeryBills { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<MissingBodyPart>))]
    public required EquatableArray<MissingBodyPart> MissingParts { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<SurgeryOperation>))]
    public required EquatableArray<SurgeryOperation> Operations { get; init; }
}

/// <summary>
/// A missing or destroyed part at its common missing ancestor; descendants are implied. Native always sets both;
/// they stay optional until their only reader, Policy MissingPart, is hardened (#3788).
/// </summary>
public sealed record MissingBodyPart
{
    public int? PartIndex { get; init; }

    public string? PartDefName { get; init; }
}

/// <summary>
/// One available operation on one part. Native always sets the recipe, eligible doctors, ingredients, violation,
/// lethal and care-limited facts; they stay optional until their only reader, Policy SurgeryOperation, is hardened
/// (#3788).
/// </summary>
public sealed record SurgeryOperation
{
    public DefinitionRef? Recipe { get; init; }

    /// <summary>The part the operation targets; absent for a whole-body recipe.</summary>
    public required BodyPartRef? Part { get; init; }

    public required SurgeryKind Kind { get; init; }

    public required SurgeryChance Chance { get; init; }

    public uint? EligibleDoctors { get; init; }

    public bool? IngredientsOnMap { get; init; }

    public bool? Violation { get; init; }

    public bool? Lethal { get; init; }

    /// <summary>
    /// The thing the operation spawns: a harvest's organ, or an added part's on its removal; absent when it
    /// spawns none.
    /// </summary>
    public required SurgeryYield? Yield { get; init; }

    /// <summary>
    /// Artificial part removal: the removed Hediff_AddedPart's def; absent unless the target part carries an added
    /// part.
    /// </summary>
    public required string? AddedPartHediff { get; init; }

    /// <summary>
    /// BaseMarketValue x count of the medicine the operation would use (the best permitted on the map); absent when
    /// it uses none or none is stocked.
    /// </summary>
    public double? MedicineMarketValue { get; init; }

    /// <summary>
    /// True when medicine the recipe accepts is on the map but the patient's medical care level forbids all of it;
    /// ingredients_on_map is then false.
    /// </summary>
    public bool? MedicineCareLimited { get; init; }
}

/// <summary>A thing an operation spawns: its def and BaseMarketValue.</summary>
public sealed record SurgeryYield
{
    public required string ThingDef { get; init; }

    public required double MarketValue { get; init; }
}

/// <summary>
/// An operation's success chance. <see cref="Rated"/>: vanilla's SurgeryOutcomeEffectDef quality with the best
/// eligible doctor, the best colony medical bed (or the current bed) and the best permitted medicine on the map,
/// the same with an ideal bed and room (a clean, roofed Bed's factor), and each eligible doctor's chance keyed by
/// pawn id. Else the recipe has <see cref="NoOutcomeEffect"/>, or there is <see cref="NoEligibleDoctor"/>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SurgeryChance.Rated), "rated")]
[JsonDerivedType(typeof(SurgeryChance.NoOutcomeEffect), "no_outcome_effect")]
[JsonDerivedType(typeof(SurgeryChance.NoEligibleDoctor), "no_eligible_doctor")]
public abstract record SurgeryChance
{
    private SurgeryChance()
    {
    }

    public sealed record Rated(double Success, double IdealSuccess, IReadOnlyDictionary<string, double> Doctors) : SurgeryChance;

    public sealed record NoOutcomeEffect : SurgeryChance;

    public sealed record NoEligibleDoctor : SurgeryChance;
}

/// <summary>
/// An item's quality: <see cref="Graded"/> at its QualityCategory (TryGetQuality), or <see cref="Qualityless"/> for a def
/// without CompQuality, which counts toward no royal title's minimum apparel quality (#3841).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ItemQuality.Graded), "graded")]
[JsonDerivedType(typeof(ItemQuality.Qualityless), "qualityless")]
public abstract record ItemQuality
{
    private protected ItemQuality() { }

    public sealed record Graded(Defs.QualityCategory Category) : ItemQuality;

    public sealed record Qualityless : ItemQuality;
}

/// <summary>
/// One equipped, worn or stocked item. Its condition is its hit points over their maximum, 1 for a def without hit
/// points (nothing wears it down).
/// </summary>
public sealed record GearItem
{
    public required Common.Ref Thing { get; init; }

    public required ItemQuality Quality { get; init; }

    public required double ConditionFraction { get; init; }

    /// <summary>
    /// Worn apparel the outfit cannot drop automatically (the forced handler's own test); false for other gear.
    /// </summary>
    public required bool Forced { get; init; }

    /// <summary>
    /// Worn apparel the player locked; false for other gear.
    /// </summary>
    public required bool Locked { get; init; }

    /// <summary>
    /// Native pawn ID, only when the coded owner still exists.
    /// </summary>
    public required string? BiocodedTo { get; init; }

    /// <summary>
    /// True also when the coded owner reference was lost.
    /// </summary>
    public required bool Biocoded { get; init; }
}

/// <summary>
/// The pawn's equipment and worn apparel. A pawn without an equipment or apparel tracker (an animal) carries
/// neither and is unarmed.
/// </summary>
public sealed record PawnEquipment
{
    /// <summary>
    /// The equipped primary's load id; absent when the pawn is unarmed.
    /// </summary>
    public required string? PrimaryId { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<GearItem>))]
    public required EquatableArray<GearItem> Equipped { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<GearItem>))]
    public required EquatableArray<GearItem> Apparel { get; init; }

    public required bool Armed { get; init; }

    /// <summary>
    /// Defense capacity: the primary ranged verb's damage per second
    /// (projectile damage x burst over aim-adjusted warmup, cooldown and burst
    /// gaps; 0 without a ranged primary) and the pawn's MeleeDPS stat.
    /// </summary>
    public double? RangedDps { get; init; }

    /// <summary>
    /// Defense capacity (#3845): the pawn's game hit chance with its primary over the reference gunner's, the factor
    /// its ranged DPS counts for. The hit chance is <c>ShotReport</c>'s standard-target aim chance (the shooter's
    /// <c>HitFactorFromShooter</c> x the weapon's <c>GetHitChanceFactor</c>) at the engagement distance,
    /// <c>CastPositionFinder.OptimalRangeFactor</c> x the verb's range. The reference gunner is the shooter the
    /// raid-point scale and the exposure calibration were measured with: the game's no-skill shooting accuracy
    /// (<c>ShootingAccuracyPawn.noSkillOffset</c>, a level-8 shooter) with a normal assault rifle. 0 without a ranged
    /// primary.
    /// </summary>
    public required double RangedHitFactor { get; init; }

    public double? MeleeDps { get; init; }
}

/// <summary>
/// The SkillDef is the catalog row by def_name.
/// </summary>
public sealed record Skill
{
    /// <summary>
    /// The learning forecast inputs; only the pawn biography read carries them.
    /// </summary>
    public SkillLearning? Learning { get; init; }

    public string DefName { get; init; } = "";

    public int Level { get; init; }

    /// <summary>
    /// The unclamped stored level (SkillRecord.levelInt).
    /// </summary>
    public int StoredLevel { get; init; }

    public Passion Passion { get; init; }

    public bool Disabled { get; init; }
}

public sealed record Trait
{
    public string DefName { get; init; } = "";

    public int Degree { get; init; }
}

public sealed record WorkSetting
{
    public required string DefName { get; init; }

    public required int Priority { get; init; }

    public required bool Disabled { get; init; }
}

public sealed record TimetableSlot
{
    public uint Hour { get; init; }

    public string AssignmentDefName { get; init; } = "";
}

/// <summary>
/// The pawn's age, skills, traits and work incapabilities. A pawn without a story or skill tracker (an animal)
/// has no traits or skills: both lists are empty.
/// </summary>
public sealed record PawnBiography
{
    public required double BiologicalAgeYears { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Skill>))]
    public required EquatableArray<Skill> Skills { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Trait>))]
    public required EquatableArray<Trait> Traits { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> IncapableWorkTypes { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> DisabledWorkTags { get; init; }

    /// <summary>
    /// Medical stats at each stored Medicine level (MedicalTendQuality, MedicalTendSpeed, MedicalOperationSpeed,
    /// MedicalSurgerySuccessChance) and MeleeDPS at each stored Melee level (#3813) for a pawn with skills whose stat
    /// pipeline qualifies.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<SkillStatCurve>))]
    public required EquatableArray<SkillStatCurve> SkillStatCurves { get; init; }

    /// <summary>
    /// "Stat: reason" for each of those stats that yields no curve.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> SkillStatFindings { get; init; }
}

/// <summary>
/// One Mood-tab row: identical thoughts stacked, with their summed mood offset.
/// </summary>
public sealed record Thought
{
    public required string DefName { get; init; }

    public required double MoodOffsetTotal { get; init; }
}

/// <summary>
/// The pawn's expectation and thoughts, read without recalculating the situational cache. A pawn without a
/// mood need has no thoughts.
/// </summary>
public sealed record PawnSocial
{
    /// <summary>The current expectation is at least ExpectationDefOf.High.</summary>
    public required bool HighExpectations { get; init; }

    /// <summary>ExpectationsUtility.CurrentExpectationFor(pawn): the current expectation's defName.</summary>
    public required string ExpectationDefName { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Thought>))]
    public required EquatableArray<Thought> Memories { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Thought>))]
    public required EquatableArray<Thought> Situational { get; init; }

    /// <summary>
    /// The situational handler's own dirty flag: the cached situational thoughts are due a recalculation.
    /// </summary>
    public required bool SituationalCacheStale { get; init; }
}

/// <summary>
/// The pawn's player settings: its care policy, work priorities, allowed area, food restriction, policy inputs,
/// body stats and the 24 timetable slots. Every member is read for every pawn.
/// </summary>
public sealed record PawnSettings
{
    /// <summary>The care policy PawnSettingsIntent writes; <see cref="PawnCare.None"/> for a pawn without player settings.</summary>
    public required PawnCare Care { get; init; }

    /// <summary>
    /// The current-map area restriction's load id; null when the pawn is unrestricted or has no player settings.
    /// </summary>
    public required string? AllowedAreaId { get; init; }

    /// <summary>
    /// One row per work type WorkSettingsIntent writes; empty when the pawn never works or its work settings are
    /// uninitialized. Hardening the work block waits on the work planner's Fact copy (#3787).
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<WorkSetting>))]
    public EquatableArray<WorkSetting> Work { get; init; }

    public bool? WorkApplies { get; init; }

    public bool? ManualWorkPriorities { get; init; }

    /// <summary>
    /// The 24 hourly slots, hour 0 first; empty for a pawn without a timetable (an animal, another faction's pawn).
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<TimetableSlot>))]
    public required EquatableArray<TimetableSlot> Schedule { get; init; }

    /// <summary>
    /// Whether the pawn eats under a food policy: it respects one, has a food need and is past babyhood.
    /// </summary>
    public required bool FoodPolicyRespected { get; init; }

    /// <summary>Per-pawn policy planner inputs.</summary>
    public required PawnPolicyInputs PolicyInputs { get; init; }

    /// <summary>Body stats for outdoor dash planning and nearby hazard planners.</summary>
    public required PawnBodyStats Body { get; init; }
}

/// <summary>
/// A pawn's care policy: <see cref="Configured"/> from its player settings (the hostility response with whether the
/// game lets the player configure it), or <see cref="None"/> for a pawn without player settings.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PawnCare.Configured), "configured")]
[JsonDerivedType(typeof(PawnCare.None), "none")]
public abstract record PawnCare
{
    private protected PawnCare() { }

    public sealed record Configured(Operations.MedicalCare MedicalCare, bool SelfTend, Operations.HostilityResponse HostilityResponse) : PawnCare;

    public sealed record None : PawnCare;
}

/// <summary>
/// The pawn's body stats the outdoor dash planner and nearby hazard planners read: native values at the read tick
/// (the game's own stat and hediff evaluation, never derived in the brain). ComfortableMinC and ComfortableMaxC
/// are StatDefOf.ComfyTemperatureMin/Max (degrees C, apparel included); MoveSpeed is StatDefOf.MoveSpeed in cells
/// per second, with the current Moving capacity. CellTemperatureC is Thing.AmbientTemperature (the pawn's cell, or
/// its holder's). Hypothermia is the highest Hypothermia hediff severity, 0 when the pawn has none. Native writes
/// every scalar; making them required waits on the work planner's Fact copy (#3787).
/// </summary>
public sealed record PawnBodyStats
{
    public double? ComfortableMinC { get; init; }

    public double? ComfortableMaxC { get; init; }

    public double? MoveSpeed { get; init; }

    public double? CellTemperatureC { get; init; }

    public double? Hypothermia { get; init; }
}

/// <summary>
/// The inputs per-pawn outfit/drug/food/reading planners compose from. Traits, mood and break thresholds
/// (biography, needs) are observed elsewhere. Policy ids are Policy.GetUniqueLoadID().
/// </summary>
public sealed record PawnPolicyInputs
{
    /// <summary>The current reading policy's id; null for a pawn without a reading tracker.</summary>
    public required string? ReadingPolicyId { get; init; }

    /// <summary>
    /// Pawn_InventoryStockTracker entries (medicine carry): group def and desired count; empty for a pawn
    /// without the tracker.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<InventoryStockSetting>))]
    public required EquatableArray<InventoryStockSetting> InventoryStock { get; init; }

    /// <summary>The pawn's ideoligion; <see cref="PawnIdeo.None"/> without Ideology or without an ideoligion.</summary>
    public required PawnIdeo Ideo { get; init; }

    /// <summary>The pawn's guest status under its host faction; <see cref="GuestState.Free"/> for a pawn no faction hosts.</summary>
    public required GuestState Guest { get; init; }
}

/// <summary>
/// A pawn's ideoligion: <see cref="Member"/> with Ideo.GetUniqueLoadID(), the ideo's precept defNames (diet,
/// apparel and every other issue, sorted and distinct), the pawn's Precept_Role defName (null without a role)
/// and Pawn_IdeoTracker.Certainty (0..1); or <see cref="None"/>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PawnIdeo.Member), "member")]
[JsonDerivedType(typeof(PawnIdeo.None), "none")]
public abstract record PawnIdeo
{
    private protected PawnIdeo() { }

    public sealed record Member(string IdeoId, [property: JsonConverter(typeof(EquatableArrayJsonConverter<string>))] EquatableArray<string> Precepts, string? Role, double Certainty) : PawnIdeo;

    public sealed record None : PawnIdeo;
}

/// <summary>
/// A pawn's guest status: <see cref="Hosted"/> with its GuestStatus (Guest, Prisoner, Slave) while a faction hosts
/// it, or <see cref="Free"/>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(GuestState.Hosted), "hosted")]
[JsonDerivedType(typeof(GuestState.Free), "free")]
public abstract record GuestState
{
    private protected GuestState() { }

    public sealed record Hosted(Defs.GuestStatus Status) : GuestState;

    public sealed record Free : GuestState;
}

public sealed record InventoryStockSetting
{
    public required string Group { get; init; }

    public required int Count { get; init; }
}


public sealed record TrainingEntry
{
    public required string DefName { get; init; }

    public required bool Learned { get; init; }

    public required bool Wanted { get; init; }

    public required bool Available { get; init; }
}

/// <summary>
/// One animal's facts, from one native builder shared by the pawn detail read and
/// the husbandry read: body, herd and training facts for every animal; the player-animal
/// settings, guards and care only for a live player animal (absent otherwise).
/// Training is empty for an animal without a training tracker; release and
/// slaughter are the live designations (false off a map). Bonded (a Bond relation to a
/// living pawn) and Sterilized (the Sterilized hediff) are read for every animal.
/// </summary>
public sealed record AnimalState
{
    public required Defs.Gender Gender { get; init; }

    public required double AgeYears { get; init; }

    public required bool Pregnant { get; init; }

    public required string? PenId { get; init; }

    public required bool? Contained { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<TrainingEntry>))]
    public required EquatableArray<TrainingEntry> Training { get; init; }

    public required bool Release { get; init; }

    public required bool Slaughter { get; init; }

    public required bool Tameable { get; init; }

    public required bool Tame { get; init; }

    public required bool? SafeToRelease { get; init; }

    /// <summary>
    /// Player-animal settings the SetAnimalArea/Master/Following writes target:
    /// the current allowed area and master (empty when unassigned), the follow
    /// flags, whether Obedience is learned (master/follow eligibility) and
    /// whether native lets this animal carry an allowed area at all.
    /// </summary>
    public required string? AllowedAreaId { get; init; }

    public required string? MasterId { get; init; }

    public required bool? FollowDrafted { get; init; }

    public required bool? FollowFieldwork { get; init; }

    public required bool? Obedient { get; init; }

    public required bool? SupportsAllowedAreas { get; init; }

    /// <summary>
    /// Herd sizing facts: any hediff that makes colonists feel sick, the
    /// player ideo venerates this race (precept effects decide what that
    /// bars), and adult age stage. The race's own
    /// numbers (body size, life expectancy, minimum handling skill, tame-failure
    /// manhunter chance) are the catalog's race rows.
    /// </summary>
    public required bool? Sick { get; init; }

    public required bool? Venerated { get; init; }

    public required bool Adult { get; init; }

    /// <summary>
    /// Medical care cap inputs, for a player animal: its
    /// MedicalCareCategory name, a Bond relation to any living pawn, and the
    /// same conditions block the population read's PersonCustody carries.
    /// </summary>
    public required Operations.MedicalCare? MedicalCare { get; init; }

    public required bool Bonded { get; init; }

    public required PawnConditions? Conditions { get; init; }

    /// <summary>
    /// Sterilized hediff present, and a queued sterilize surgery bill on the
    /// animal (the husbandry sterilize order).
    /// </summary>
    public required bool Sterilized { get; init; }

    public required bool? SterilizeQueued { get; init; }

    /// <summary>
    /// Raw slaughter facts of a player animal; policy decides who is protected
    /// (FoodSlaughterChoice). downed and in_mental_state are every animal's live Pawn
    /// flags, colonist_bonded is TrainableUtility.GetAllColonistBondsFor
    /// non-empty, slaughter_designatable is Designator_Slaughter's own
    /// acceptance (false while a slaughter designation stands).
    /// </summary>
    public required bool Downed { get; init; }

    public required bool InMentalState { get; init; }

    public required bool? ColonistBonded { get; init; }

    public required bool? SlaughterDesignatable { get; init; }

    /// <summary>
    /// Herd growth events: game ticks until a pregnant animal gives birth
    /// ((1 - gestation) * gestationPeriodDays * 60000 / BodyResourceGrowthSpeed),
    /// absent without a pregnancy; the index of the current life stage in the
    /// race's lifeStageAges (Pawn_AgeTracker.CurLifeStageIndex) and game ticks until
    /// the next stage begins ((next minAge in ticks - AgeBiologicalTicks) /
    /// BiologicalTicksPerTick), absent in the last stage. The litter size is rolled
    /// at birth and the stage rows are the catalog's, so neither is sent.
    /// </summary>
    public required long? TicksToBirth { get; init; }

    public required int LifeStageIndex { get; init; }

    public required long? TicksToNextLifeStage { get; init; }

    /// <summary>
    /// Ids of the living humanlike pawns this animal has a Bond relation with
    /// (any faction or status; the planner keeps those on the colony roster).
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> BondedPawnIds { get; init; }
}

/// <summary>
/// One pawn's row: every section is read for every pawn. A section only some pawns have is a named state
/// (<see cref="PawnAnimalState.NotAnimal"/>, a DLC's <c>Inactive</c>); a dead pawn's needs are all Lacks, its
/// job tracker gone (no job) and its work never assigned.
/// </summary>
public sealed record PawnState
{
    public required EntityRef Pawn { get; init; }

    public required string KindDefName { get; init; }

    public required bool Colonist { get; init; }

    public required bool FreeColonist { get; init; }

    public required bool Prisoner { get; init; }

    public required bool Humanlike { get; init; }

    public required bool Animal { get; init; }

    public required bool Mechanoid { get; init; }

    public required bool Dead { get; init; }

    public required bool Downed { get; init; }

    public required bool Drafted { get; init; }

    public required bool Hostile { get; init; }

    /// <summary>
    /// The active mental state; null when the pawn is in none.
    /// </summary>
    public required PawnMentalState? MentalState { get; init; }

    public required bool InBed { get; init; }

    /// <summary>The coupled order the pawn executes; null when it carries none.</summary>
    public required Operations.PawnActivityExecution? ActivityExecution { get; init; }

    public required JobEvidence Job { get; init; }

    public required PawnNeeds Needs { get; init; }

    public required PawnHealth Health { get; init; }

    public required PawnEquipment Equipment { get; init; }

    public required PawnBiography Biography { get; init; }

    public required PawnSettings Settings { get; init; }

    public required PawnSocial Social { get; init; }

    public required PawnAnimalState AnimalState { get; init; }

    /// <summary>
    /// Chebyshev distance to the nearest other live colonist on the map.
    /// </summary>
    public required NearestColonist NearestColonist { get; init; }

    /// <summary>The bed the pawn owns; null when it owns none.</summary>
    public required Common.Ref? OwnedBed { get; init; }

    /// <summary>
    /// Native lord evidence for hostile groups: the LordJob and current LordToil class names (e.g.
    /// LordJob_AssaultColony / LordToil_AssaultColonySappers, LordJob_Siege). Null when the pawn has no lord (or the
    /// lord no current toil); never inferred.
    /// </summary>
    public required string? LordJobClass { get; init; }

    public required string? LordToilClass { get; init; }

    /// <summary>
    /// Mean of each active spawned hostile's highest natural/worn sharp armor rating on the pawn's map.
    /// </summary>
    public required RaidArmor RaidArmor { get; init; }

    /// <summary>
    /// Verse.IntVec3.Fogged: the pawn stands in fog the colony has not
    /// discovered. A fogged hostile is undiscovered, unreachable and no
    /// emergency -- the ancient-danger mechanoid behind a sealed wall held
    /// every window and deselected every development goal for good.
    /// </summary>
    public required bool Fogged { get; init; }

    /// <summary>
    /// The current InspirationDef defName; null when the pawn has no inspiration.
    /// </summary>
    public required string? Inspiration { get; init; }

    /// <summary>
    /// The pawn control CAS token; null when native admits no pawn order (the pawn is not spawned on the current
    /// map, or its control state is not ready).
    /// </summary>
    public required SnapshotRef? PawnSnapshot { get; init; }

    public required PawnBiotechState Biotech { get; init; }

    public required PawnAnomalyState Anomaly { get; init; }

    public required PawnRoyaltyState Royalty { get; init; }

    /// <summary>
    /// The pawn's FactionDef; null without a faction.
    /// </summary>
    public required string? FactionDefName { get; init; }

    /// <summary>
    /// Alive, standing colony builder with native finishing work-type and required
    /// capacities. Jobs, draft, schedule and skill level do not change this fact.
    /// </summary>
    public bool? ConstructionCapable { get; init; }
}

/// <summary>An animal's facts, or <see cref="NotAnimal"/> for a pawn whose race is no animal.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PawnAnimalState.Animal), "animal")]
[JsonDerivedType(typeof(PawnAnimalState.NotAnimal), "not_animal")]
public abstract record PawnAnimalState
{
    private protected PawnAnimalState() { }

    public sealed record Animal(AnimalState State) : PawnAnimalState;

    public sealed record NotAnimal : PawnAnimalState;
}

/// <summary>The Chebyshev distance to the nearest other live colonist on the map, or <see cref="None"/> when there is none.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NearestColonist.At), "at")]
[JsonDerivedType(typeof(NearestColonist.None), "none")]
public abstract record NearestColonist
{
    private protected NearestColonist() { }

    public sealed record At(double Distance) : NearestColonist;

    public sealed record None : NearestColonist;
}

/// <summary>The hostile armor on the pawn's map, or <see cref="NoHostiles"/> when no live, standing hostile is spawned there.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RaidArmor.Rated), "rated")]
[JsonDerivedType(typeof(RaidArmor.NoHostiles), "no_hostiles")]
public abstract record RaidArmor
{
    private protected RaidArmor() { }

    public sealed record Rated(double Mean) : RaidArmor;

    public sealed record NoHostiles : RaidArmor;
}

/// <summary>The pawn's Biotech facts, or <see cref="Inactive"/> without Biotech.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PawnBiotechState.Active), "active")]
[JsonDerivedType(typeof(PawnBiotechState.Inactive), "inactive")]
public abstract record PawnBiotechState
{
    private protected PawnBiotechState() { }

    public sealed record Active(PawnBiotech Facts) : PawnBiotechState;

    public sealed record Inactive : PawnBiotechState;
}

/// <summary>The pawn's Anomaly facts, or <see cref="Inactive"/> without Anomaly.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PawnAnomalyState.Active), "active")]
[JsonDerivedType(typeof(PawnAnomalyState.Inactive), "inactive")]
public abstract record PawnAnomalyState
{
    private protected PawnAnomalyState() { }

    public sealed record Active(PawnAnomaly Facts) : PawnAnomalyState;

    public sealed record Inactive : PawnAnomalyState;
}

/// <summary>
/// The pawn's royalty facts, <see cref="Inactive"/> without Royalty, or <see cref="NoTracker"/> for a pawn the game
/// gives no royalty tracker (a non-humanlike).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PawnRoyaltyState.Active), "active")]
[JsonDerivedType(typeof(PawnRoyaltyState.Inactive), "inactive")]
[JsonDerivedType(typeof(PawnRoyaltyState.NoTracker), "no_tracker")]
public abstract record PawnRoyaltyState
{
    private protected PawnRoyaltyState() { }

    public sealed record Active(PawnRoyalty Facts) : PawnRoyaltyState;

    public sealed record Inactive : PawnRoyaltyState;

    public sealed record NoTracker : PawnRoyaltyState;
}

/// <summary>
/// A pawn's active mental state: its MentalStateDef defName, MentalStateDef.IsAggro
/// and MentalState.Age (native ticks, 30-tick granularity).
/// </summary>
public sealed record PawnMentalState
{
    public required string DefName { get; init; }

    public required bool IsAggro { get; init; }

    public required int Ticks { get; init; }
}

/// <summary>
/// Biotech facts of one pawn. Each scalar is absent when native could
/// not read it (unknown, not zero); a failed sub-read adds a ReadIssue named
/// for its field (genes, mechanitor, mech, deathrest). Defs are the
/// DefinitionCatalog.biotech rows' names.
/// life_stage is the pawn's LifeStageDef (Pawn_AgeTracker.CurLifeStage) and
/// developmental_stage the DevelopmentalStage (Newborn, Baby, Child,
/// Adult; None for a race with no stages). learning is Need_Learning's level
/// (0-1) with its LearningCategory; absent for a pawn with no learning
/// need. genes: every gene of Pawn_GeneTracker (endogenes and xenogenes).
/// xenotype is the XenotypeDef name, absent for a custom xenotype or none;
/// xenotype_name is the pawn's xenotype label (named custom xenotypes
/// included) and hybrid the Pawn_GeneTracker.hybrid flag. mechanitor is set
/// for a pawn with a Pawn_MechanitorTracker; mech for a mechanoid the player
/// can ever control. deathrest is set for a pawn with a deathrest gene.
/// </summary>
public sealed record PawnGene
{
    public string DefName { get; init; } = "";

    public bool Xenogene { get; init; }

    public bool Active { get; init; }
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

    public Defs.DevelopmentalStage? DevelopmentalStage { get; init; }

    public double? Learning { get; init; }

    public Defs.LearningCategory? LearningCategory { get; init; }

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

    public bool? Hostile { get; init; }

    public bool? Downed { get; init; }

    public bool? Drafted { get; init; }

    public bool? IncludeDead { get; init; }

    public double? WithinColonistDistance { get; init; }
}

public sealed record PawnSnapshot
{
    public required Common.ObservationContext Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnState>))]
    public required EquatableArray<PawnState> Pawns { get; init; }
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

/// <summary>The tend table: one row per requested pawn on the map, keyed by its id, in id order.</summary>
public sealed record TendDoctorSnapshot
{
    public required Common.ObservationContext Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<TendDoctor>))]
    public required EquatableArray<TendDoctor> Doctors { get; init; }
}

/// <summary>
/// One pawn's doctor-side tend gates, mirroring NativeTendOperations.Prepare so the controller never proposes a pair
/// the native gate refuses.
/// </summary>
public sealed record TendDoctor
{
    public required string PawnId { get; init; }

    /// <summary>
    /// NativePawnControlState eligibility: a drafter exists, the pawn is spawned, alive, not downed, not in a mental
    /// state and player-controlled. This is the gate whose native refusal reads "Tend requires an eligible doctor".
    /// </summary>
    public required bool ControlEligible { get; init; }

    public required bool Spawned { get; init; }

    public required bool HasDrafter { get; init; }

    /// <summary>WorkGiver_Tend.MissingRequiredCapacity(pawn) == null.</summary>
    public required bool CapacitiesOk { get; init; }

    public required TendReach Reach { get; init; }
}

/// <summary>
/// <see cref="Read"/>: the ids of the request's other pawns this pawn can reach (CanReach ClosestTouch,
/// Danger.Deadly), exhaustive; an unspawned or dead pawn reaches nothing. <see cref="Skipped"/>: the reach budget ran
/// out before this pawn was read.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TendReach.Read), "read")]
[JsonDerivedType(typeof(TendReach.Skipped), "reach_skipped")]
public abstract record TendReach
{
    private protected TendReach() { }

    public sealed record Read([property: JsonConverter(typeof(EquatableArrayJsonConverter<string>))] EquatableArray<string> PawnIds) : TendReach;

    public sealed record Skipped : TendReach;
}

/// <summary>
/// One stack a stock row counts: the thing's load id and the cell its units are at (its holder's when held).
/// Snapshot is its supply CAS token; it is absent exactly when the item is a loose supply the Allow rules do not
/// admit (every held item carries a stateless holder token). Packed is the building inside a packed (minified)
/// item, absent for any other thing.
/// </summary>
public sealed record StockItem
{
    public required string Id { get; init; }

    public required Cell Cell { get; init; }

    public SnapshotRef? Snapshot { get; init; }

    public PackedBuilding? Packed { get; init; }
}

/// <summary>
/// The building inside a packed (minified) item, for a RelocateIntent install: its load id and def, its quality
/// (absent for a building with no quality), and the packed item's market value.
/// </summary>
public sealed record PackedBuilding
{
    public required string Id { get; init; }

    public required string DefName { get; init; }

    public Defs.QualityCategory? Quality { get; init; }

    public required double MarketValue { get; init; }
}

/// <summary>A holder's units of one definition: the holder thing's load id, what kind of holder it is, and the units.</summary>
public sealed record HeldStock
{
    public required string Holder { get; init; }

    public required HolderKind HolderKind { get; init; }

    public required long Units { get; init; }
}

/// <summary>
/// One definition's stock: unit and stack counts by place, ownership and state, every stack, the held units by
/// holder, and the weapon rows' biocode facts keyed by the items' ids.
/// </summary>
public sealed record ResourceStock
{
    public required string DefName { get; init; }

    public required long Units { get; init; }

    public required long Stacks { get; init; }

    public required long Spawned { get; init; }

    public required long Ours { get; init; }

    public required long OursUnforbidden { get; init; }

    public required long Forbidden { get; init; }

    public required long PlayerFaction { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<StockItem>))]
    public required EquatableArray<StockItem> Items { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<HeldStock>))]
    public required EquatableArray<HeldStock> Holders { get; init; }

    /// <summary>Only weapon rows carry these.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<GearItem>))]
    public required EquatableArray<GearItem> WeaponItems { get; init; }
}

/// <summary>
/// A supplies read's scope. DefNames (empty: every def) narrows to exact defs; Category, Ownership and IncludeHeld
/// choose the census (the usual read is Haulable, Ours, held included); ForbiddenOnly, ExcludeChunks and Corpses
/// narrow it further; Region (absent: the whole map) bounds the root cells.
/// </summary>
public sealed record StockFilter
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> DefNames { get; init; }

    public required StockCategory Category { get; init; }

    public required StockOwnership Ownership { get; init; }

    public required bool IncludeHeld { get; init; }

    public bool ForbiddenOnly { get; init; }

    public bool ExcludeChunks { get; init; }

    public bool Corpses { get; init; }

    public Rectangle? Region { get; init; }
}

public sealed record SuppliesSnapshot
{
    public required Common.ObservationContext Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ResourceStock>))]
    public required EquatableArray<ResourceStock> Stocks { get; init; }
}

public sealed record ListSuppliesRequest
{
    public ReadScope? Scope { get; init; }

    public required StockFilter Filter { get; init; }
}

/// <summary>
/// A storage or ingredient ThingFilter as the game holds it: the allowed definitions (sorted), the allowed hit-point
/// fraction and quality ranges, and the AllowFresh and AllowRotten special filters.
/// </summary>
public sealed record StockpileFilter
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> AllowedDefNames { get; init; }

    public required double HitPointsMin { get; init; }

    public required double HitPointsMax { get; init; }

    [JsonConverter(typeof(WireEnumConverter<Quality>))]
    public required Quality QualityMin { get; init; }

    [JsonConverter(typeof(WireEnumConverter<Quality>))]
    public required Quality QualityMax { get; init; }

    public required bool AllowFresh { get; init; }

    public required bool AllowRotten { get; init; }
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
    public required bool ManagedUnchanged { get; init; }

    public required string Id { get; init; }

    /// <summary>The recipe's def name.</summary>
    public required string Recipe { get; init; }

    /// <summary>The bill's runtime class name (Bill_Production, Bill_GovernorRecipe, ...).</summary>
    public required string BillClass { get; init; }

    public Operations.RepeatMode? RepeatMode { get; init; }

    public int? RepeatCount { get; init; }

    public int? TargetCount { get; init; }

    public required bool Suspended { get; init; }

    public bool? Finished { get; init; }

    /// <summary>The pinned worker's pawn id; absent when no pawn is pinned (a real none).</summary>
    public string? Worker { get; init; }

    public required StockpileFilter IngredientFilter { get; init; }

    /// <summary>
    /// Live bill jobs' promised ingredients: one row per spawned pawn
    /// whose current job works this bill, the spawned things it has queued or
    /// placed. The brain's material budget subtracts them from free stock.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<IngredientReservation>))]
    public EquatableArray<IngredientReservation> Reservations { get; init; }

    /// <summary>Exact bound unfinished work; absent means unread, an empty Item means no bound item.</summary>
    public BillUnfinishedWork? UnfinishedWork { get; init; }
}

/// <summary>Ingredients embedded in one initialized unfinished iteration; these are not loose stock or job reservations.</summary>
public sealed record BillUnfinishedWork
{
    public string? Item { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Quantity>))]
    public EquatableArray<Quantity> Ingredients { get; init; }
}

public sealed record IngredientReservation
{
    public required string PawnId { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Quantity>))]
    public EquatableArray<Quantity> Items { get; init; }
}

/// <summary>
/// One bench's ordered bill stack under its CAS token. work_speed is the bench's WorkTableWorkSpeedFactor stat as the
/// game computes it now (stat parts included): a def stat every thing has, so it is always read.
/// </summary>
public sealed record BillStackRow
{
    public required SnapshotRef Snapshot { get; init; }

    /// <summary>The bench's thing id.</summary>
    public required string Bench { get; init; }

    public required bool Usable { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<BillState>))]
    public required EquatableArray<BillState> Bills { get; init; }

    public required double WorkSpeed { get; init; }
}

/// <summary>
/// What a recipe is, costs and needs is its RecipeDef row in the definition catalog; a frame carries only whether the bench offers it now.
/// </summary>
public sealed record RecipeState
{
    /// <summary>The recipe's def name.</summary>
    public required string Recipe { get; init; }

    public required bool AvailableNow { get; init; }

    public required bool AvailableOnBench { get; init; }
}

public sealed record BuildingSettings
{
    public required bool Forbidden { get; init; }

    /// <summary>The building's one writable setting family the brain reads, or none (#3770).</summary>
    public required BuildingControl Control { get; init; }
}

/// <summary>
/// A building's writable settings the brain reads. A temperature-controlled building is <see cref="Temperature"/>
/// (with the CAS snapshot its BuildingPatchIntent arm sends back); else a humanlike bed is <see cref="Bed"/>; else a
/// plant grower is <see cref="Grower"/>; any other building is <see cref="None"/>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(BuildingControl.Temperature), "temperature")]
[JsonDerivedType(typeof(BuildingControl.Bed), "bed")]
[JsonDerivedType(typeof(BuildingControl.Grower), "grower")]
[JsonDerivedType(typeof(BuildingControl.None), "none")]
public abstract record BuildingControl
{
    private BuildingControl()
    {
    }

    /// <summary>A temperature-controlled building and its target temperature.</summary>
    public sealed record Temperature(SnapshotRef Snapshot, double TargetC) : BuildingControl;

    /// <summary>A humanlike bed: its medical and prisoner flags and its assigned owners.</summary>
    public sealed record Bed(
        bool Medical,
        bool ForPrisoners,
        [property: JsonConverter(typeof(EquatableArrayJsonConverter<Common.Ref>))] EquatableArray<Common.Ref> AssignedPawns) : BuildingControl;

    /// <summary>A plant grower and its current crop; the crop is null when the grower is set to grow nothing.</summary>
    public sealed record Grower(string? CropDefName) : BuildingControl;

    /// <summary>A building with no writable setting family.</summary>
    public sealed record None : BuildingControl;
}

public sealed record MaterialDeficit
{
    public required string DefName { get; init; }

    public required long StillNeeded { get; init; }
}

/// <summary>
/// A blueprint's or frame's construction state. A construction blueprint or frame is a <see cref="Build"/>; an
/// installation blueprint (reinstalling a minified or uninstalled thing) is an <see cref="Installation"/>, which
/// carries no work, no materials and no finishing guard.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ConstructionState.Build), "build")]
[JsonDerivedType(typeof(ConstructionState.Installation), "installation")]
public abstract record ConstructionState
{
    private protected ConstructionState() { }

    /// <summary>
    /// A construction blueprint or frame: the definition it builds, the native work left (WorkToBuild on a
    /// blueprint), the material each cost definition still needs (every cost listed, zero once delivered), and
    /// the native finishing-skill guard's facts: whether the build has quality, the definition's own
    /// constructionSkillPrerequisite, the planner-set minimum (absent when none is set) and the tier gate.
    /// </summary>
    public sealed record Build : ConstructionState
    {
        public required string Definition { get; init; }

        public required double WorkLeft { get; init; }

        [JsonConverter(typeof(EquatableArrayJsonConverter<MaterialDeficit>))]
        public required EquatableArray<MaterialDeficit> Resources { get; init; }

        public required bool QualitySensitive { get; init; }

        public required int NativeFinishingSkill { get; init; }

        public required FinishingMinimum? Minimum { get; init; }

        public required ConstructionGate Gate { get; init; }
    }

    public sealed record Installation : ConstructionState;
}

/// <summary>A planner-set minimum finishing skill on a build, and how many free colonists meet it now.</summary>
public sealed record FinishingMinimum
{
    public required int Skill { get; init; }

    public required int EligibleFinishers { get; init; }
}

/// <summary>A build's native per-target construction tier (#2522): ungated, or gated at a tier.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ConstructionGate.Ungated), "ungated")]
[JsonDerivedType(typeof(ConstructionGate.Tiered), "tiered")]
public abstract record ConstructionGate
{
    private protected ConstructionGate() { }

    public sealed record Ungated : ConstructionGate;

    public sealed record Tiered : ConstructionGate
    {
        [JsonConverter(typeof(WireEnumConverter<ConstructionTier>))]
        public required ConstructionTier Tier { get; init; }
    }
}

/// <summary>A building's hit points: a definition without hit points (useHitPoints false) is <see cref="Untracked"/>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(BuildingHitPoints.Untracked), "untracked")]
[JsonDerivedType(typeof(BuildingHitPoints.Tracked), "tracked")]
public abstract record BuildingHitPoints
{
    private protected BuildingHitPoints() { }

    public sealed record Untracked : BuildingHitPoints;

    public sealed record Tracked : BuildingHitPoints
    {
        public required int Current { get; init; }

        public required int Maximum { get; init; }
    }
}

/// <summary>
/// A building's power, switch, breakdown and fuel state. The power facts are absent without a power
/// trader or battery (a battery is on while connected and outputs nothing), power_net_id without a
/// network, producer without a power trader, the fuel facts without a refuelable comp. A building
/// without a flick switch is switched on.
/// </summary>
public sealed record BuildingServiceState
{
    public bool? Connected { get; init; }

    public bool? PowerOn { get; init; }

    public double? PowerOutputW { get; init; }

    public bool SwitchedOn { get; init; }

    public double? Fuel { get; init; }

    public double? TargetFuel { get; init; }

    public bool? OutOfFuel { get; init; }

    public bool BrokenDown { get; init; }

    public string? PowerNetId { get; init; }

    /// <summary>
    /// The building is a power producer (a power trader whose base consumption is negative: a generator); absent
    /// without a power trader.
    /// </summary>
    public bool? Producer { get; init; }

    /// <summary>
    /// A wind turbine's wind path cells that are off the map, roofed or hold a wind-blocking thing (the cells
    /// CompPowerPlantWind.RecalculateBlockages counts); absent on any other building.
    /// </summary>
    public uint? WindPathBlockedCells { get; init; }
}

/// <summary>
/// BuildingState is the canonical building row, built by one native
/// builder: only the bundle's building table and the list read carry it;
/// every other message points at a building by Ref, which the
/// reader resolves against the table. Stuff is absent for a stuffless def
/// and faction for an unowned thing; construction is present exactly on a
/// blueprint or frame.
/// </summary>
public sealed record BuildingState
{
    public required EntityRef Building { get; init; }

    public required Placement.Rotation Rotation { get; init; }

    public required string? Stuff { get; init; }

    public required Common.Ref? Faction { get; init; }

    public required BuildingStatus Status { get; init; }

    public required BuildingHitPoints HitPoints { get; init; }

    /// <summary>What a blueprint or frame builds and how far it is; absent on a built row.</summary>
    public required ConstructionState? Construction { get; init; }

    public required BuildingServiceState Service { get; init; }

    public required BuildingSettings Settings { get; init; }

    public required bool Burning { get; init; }

    /// <summary>
    /// The building's occupied rect, inclusive.
    /// </summary>
    public required Rectangle Occupied { get; init; }

    /// <summary>
    /// Anomaly containment facts; absent without Anomaly or on a building
    /// that is no entity holder.
    /// </summary>
    public required AnomalyBuilding? Anomaly { get; init; }

    /// <summary>
    /// A door's state (Building_Door); absent on any other building.
    /// </summary>
    public required BuildingDoor? Door { get; init; }
}

/// <summary>
/// A door building's state: open is Building_Door.Open (physically open
/// now), hold_open the player's hold-open latch (Building_Door.HoldOpen).
/// </summary>
public sealed record BuildingDoor
{
    public required bool Open { get; init; }

    public required bool HoldOpen { get; init; }
}

/// <summary>
/// One power network: its frame-local id, the output of its running producers and the draw of its
/// running consumers, and its batteries' stored energy and capacity.
/// </summary>
public sealed record PowerNetwork
{
    public string Id { get; init; } = "";

    public double GenerationW { get; init; }

    public double ConsumptionW { get; init; }

    public double StoredWattDays { get; init; }
}

public sealed record BuildingsSnapshot
{
    public required Common.ObservationContext Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<BuildingState>))]
    public required EquatableArray<BuildingState> Buildings { get; init; }
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

/// <summary>
/// RimWorld's own RoomRoleDef, named by its defName, as the native room census reports a room's role. The game
/// scores every role worker against a room's contents and picks one: a room with beds is a Bedroom or Barracks
/// whatever else it holds, so a role is a fact about the whole room, never something the controller assigns.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<RoomRole>))]
public enum RoomRole
{
    [WireName("None")]
    None,

    [WireName("Room")]
    Room,

    [WireName("Bedroom")]
    Bedroom,

    [WireName("PrisonCell")]
    PrisonCell,

    [WireName("DiningRoom")]
    DiningRoom,

    [WireName("RecRoom")]
    RecRoom,

    [WireName("Hospital")]
    Hospital,

    [WireName("Laboratory")]
    Laboratory,

    [WireName("Workshop")]
    Workshop,

    [WireName("Storeroom")]
    Storeroom,

    [WireName("Barracks")]
    Barracks,

    [WireName("PrisonBarracks")]
    PrisonBarracks,

    [WireName("Kitchen")]
    Kitchen,

    [WireName("Tomb")]
    Tomb,

    [WireName("Barn")]
    Barn,

    // Royalty
    [WireName("ThroneRoom")]
    ThroneRoom,

    // Ideology
    [WireName("WorshipRoom")]
    WorshipRoom,

    // Biotech
    [WireName("Nursery")]
    Nursery,

    [WireName("Playroom")]
    Playroom,

    [WireName("Classroom")]
    Classroom,

    [WireName("DeathrestChamber")]
    DeathrestChamber,

    // Anomaly
    [WireName("ContainmentCell")]
    ContainmentCell,

    [WireName("CeremonialChamber")]
    CeremonialChamber,

    /// <summary>A RoomRoleDef this build does not name (another mod's): the census reports it as this value instead of failing the read.</summary>
    [WireName("Unrecognized")]
    Unrecognized,
}

/// <summary>One of the room stats the native census reads (Cleanliness, Wealth, Space, Beauty, Impressiveness), by defName. A stat that cannot be read fails the census.</summary>
public sealed record RoomStat
{
    public required string DefName { get; init; }

    public required double Value { get; init; }
}

/// <summary>The cells at the low and high corners of the rectangle around a room's or a zone's cells.</summary>
public sealed record CellExtents
{
    public required Cell Minimum { get; init; }

    public required Cell Maximum { get; init; }
}

/// <summary>The count of one building definition standing in a room (its boundary too when the read asked for it).</summary>
public sealed record RoomContent
{
    public required string DefName { get; init; }

    public required long Units { get; init; }
}

/// <summary>
/// One room of the native census. Every member is read; a room the native cannot read completely fails the
/// census. Its cells are not on the row: a reader takes them from the frame cell grid by <see cref="GridRoom"/>.
/// </summary>
public sealed record RoomState
{
    public required string Id { get; init; }

    public required RoomRole Role { get; init; }

    public required bool ProperRoom { get; init; }

    public required bool Doorway { get; init; }

    /// <summary>The room uses the outdoor temperature.</summary>
    public required bool Outdoors { get; init; }

    public required bool PsychologicallyOutdoors { get; init; }

    public required bool TouchesMapEdge { get; init; }

    public required bool Fogged { get; init; }

    public required double TemperatureC { get; init; }

    public required uint OpenRoofCount { get; init; }

    public required uint CellCount { get; init; }

    public required CellExtents Extents { get; init; }

    public required Cell Center { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<RoomStat>))]
    public required EquatableArray<RoomStat> Stats { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Ref>))]
    public required EquatableArray<Common.Ref> Pawns { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Ref>))]
    public required EquatableArray<Common.Ref> Beds { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<RoomContent>))]
    public required EquatableArray<RoomContent> Contents { get; init; }

    /// <summary>
    /// Doors in the room's boundary: door cell, the cell it opens onto, and whether that side is outdoors.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<RoomDoor>))]
    public required EquatableArray<RoomDoor> Doors { get; init; }

    /// <summary>
    /// The room's key in the cell grid (CellGrid.Room): the row-major map index of its first unfogged cell, whose
    /// grid cells carry it. A room whose every cell is fogged keys by its first cell, which no held grid cell
    /// carries, so it has no grid cells.
    /// </summary>
    public required int GridRoom { get; init; }

    public required bool Burning { get; init; }

    /// <summary>
    /// Temperature-control buildings or heat-pusher comps in this room or its boundary.
    /// </summary>
    public required bool TemperatureControl { get; init; }

    /// <summary>
    /// A contained item has CompRottable.
    /// </summary>
    public required bool PerishableContents { get; init; }
}

/// <summary>
/// One door in a room's boundary: the door cell, the cell across it from the room, and whether that far side is a
/// psychologically outdoor room. A far side with no room (off the map, impassable) or this room itself is not
/// outdoors.
/// </summary>
public sealed record RoomDoor
{
    public required Cell Cell { get; init; }

    public required Cell Outside { get; init; }

    public required bool Outdoors { get; init; }

    public required bool PlayerOwned { get; init; }

    public required bool Open { get; init; }

    public required bool HoldOpen { get; init; }

    public required bool BlockedOpen { get; init; }

    public required bool Forbidden { get; init; }
}

/// <summary>The native room census: every proper room the request selects. A census the native cannot read fails the read.</summary>
public sealed record RoomsSnapshot
{
    public required Common.ObservationContext Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<RoomState>))]
    public required EquatableArray<RoomState> Rooms { get; init; }
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

/// <summary>
/// One zone of the native census, a case per zone class the planners read: a growing zone with its crop and
/// planted state, a stockpile with its storage facts, and every other zone class (fishing, another mod's) as
/// <see cref="Other"/>, which carries only what every zone has. Every member is read; a zone the native cannot
/// read completely fails the census. Its cells are not on the row: a reader takes them from the cell grid by
/// <see cref="Id"/> (CellGrid.ZoneId).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Growing), "growing")]
[JsonDerivedType(typeof(Stockpile), "stockpile")]
[JsonDerivedType(typeof(Other), "other")]
public abstract record ZoneState
{
    private protected ZoneState() { }

    /// <summary>The zone's unique load id.</summary>
    public required string Id { get; init; }

    public required string Label { get; init; }

    public required CellExtents Bounds { get; init; }

    /// <summary>
    /// A growing zone. Crop is the plant def it grows; the counts are over its unfogged cells: usable cells meet
    /// the crop's minimum fertility, planted cells carry a plant of the crop, fertile planted ones have a fertility
    /// growth factor above zero, blighted ones are blighted. GrowthTotal and FertilityFactorTotal sum the planted
    /// plants' growth fraction (0..1 each) and native fertility growth-rate factor, so each mean is the total over
    /// PlantedCells. EdibleCrop is whether every colonist WillEat the harvest. Temperature is the map's outdoor
    /// temperature; the crop's growth range and harvest nutrition are its PlantProperties row's.
    /// </summary>
    public sealed record Growing : ZoneState
    {
        public required string Crop { get; init; }

        public required uint UsableCells { get; init; }

        public required uint PlantedCells { get; init; }

        public required uint FertilePlantedCells { get; init; }

        public required uint BlightedPlants { get; init; }

        public required bool EdibleCrop { get; init; }

        public required double GrowthTotal { get; init; }

        public required double FertilityFactorTotal { get; init; }

        public required double Temperature { get; init; }
    }

    /// <summary>
    /// A stockpile. Priority is the native StoragePriority's name; FoodStorage is whether it allows a perishable
    /// every colonist eats. Filter is the zone's thing filter, carried only when the request asks for it.
    /// </summary>
    public sealed record Stockpile : ZoneState
    {
        public required string Priority { get; init; }

        public required bool FoodStorage { get; init; }

        public StockpileFilter? Filter { get; init; }
    }

    /// <summary>A zone of a class the planners read nothing more of: a fishing zone, or another mod's.</summary>
    public sealed record Other : ZoneState;
}

/// <summary>The native zone census: every zone with a cell the request selects, in id order. A census the native cannot read fails the read.</summary>
public sealed record ZonesSnapshot
{
    public required Common.ObservationContext Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ZoneState>))]
    public required EquatableArray<ZoneState> Zones { get; init; }
}

public sealed record ListZonesRequest
{
    public ReadScope? Scope { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Ids { get; init; }

    public string? NameContains { get; init; }

    public Rectangle? Region { get; init; }

    public bool? IncludeFilter { get; init; }
}

/// <summary>
/// A thing's canonical row, from one native builder: the bundle's things
/// table. Head, stack count, forbidden and corpse are set on every row. The
/// food facts are set for an ingestible item or a corpse: rot_ticks at the
/// current ambient temperature (present exactly while the thing rots),
/// temperature_c that ambient temperature, roofed and room where a spawned
/// thing stands, is_human_meat when the thing or any ingredient it was made
/// from (CompIngredients) is humanlike meat; a corpse adds its meat yield
/// (meat_amount), body size and a one-cell footprint. What the def alone says
/// (class, vegetable, raw meat and raw ingredient class, a corpse's humanlike
/// race) is the definition catalog's.
/// </summary>
public sealed record Thing
{
    [JsonPropertyName("thing")]
    public required EntityRef Entity { get; init; }

    public long StackCount { get; init; }

    public bool Forbidden { get; init; }

    public bool Corpse { get; init; }

    public long? RotTicks { get; init; }

    public double? TemperatureC { get; init; }

    public bool? Roofed { get; init; }

    public Common.Ref? Room { get; init; }

    public double? MeatAmount { get; init; }

    public double? BodySize { get; init; }

    public bool? IsHumanMeat { get; init; }
}

public sealed record ThingsSnapshot
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<Thing>))]
    public EquatableArray<Thing> Things { get; init; }
}

/// <summary>
/// One observations_get_cells read: the map's dimensions and the
/// requested rectangle as a CellGrid, the arrays the snapshot frame's
/// grid carries. A rectangle the native cannot read fails the read.
/// </summary>
public sealed record CellsSnapshot
{
    public required Common.ObservationContext Context { get; init; }

    public required MapSize MapSize { get; init; }

    public required Mirror.CellGrid Grid { get; init; }
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
/// Progress and availability only: the project's definition facts (cost, tab,
/// prerequisites, techprints, required bench) are ResearchProjectDef rows of
/// the definition catalog, and what a project unlocks is a reverse index over
/// the rows that name it as a prerequisite. ProgressFraction is the share of
/// the cost researched (0 to 1; a zero-cost project has nothing left to
/// research and reads 1). LockReasons are the native CanStartNow predicates
/// the project fails, empty when it can start now.
/// </summary>
public sealed record ResearchProject
{
    public required DefinitionRef Project { get; init; }

    public required double ProgressFraction { get; init; }

    public required bool Finished { get; init; }

    public required bool Current { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> LockReasons { get; init; }
}

/// <summary>A research slot: Category is empty on the ordinary slot and names the knowledge category otherwise; CurrentProject is empty while the slot has none.</summary>
public sealed record ResearchSlot
{
    public required string Category { get; init; }

    public required string CurrentProject { get; init; }
}

/// <summary>
/// finished names every finished project, whatever the request filters.
/// </summary>
public sealed record ResearchSnapshot
{
    public required Common.ObservationContext Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ResearchSlot>))]
    public required EquatableArray<ResearchSlot> Slots { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ResearchProject>))]
    public required EquatableArray<ResearchProject> Projects { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> Finished { get; init; }
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
/// One target cell of the access audit: whether the pawn reaches it natively now (CanReach, no danger), and whether it
/// reaches it in the projected layout, the blocked cells impassable.
/// </summary>
public sealed record AccessTarget
{
    public required Cell Cell { get; init; }

    public required bool NativeReachable { get; init; }

    public required bool ProjectedReachable { get; init; }
}

/// <summary>
/// One colonist of the access audit, at <see cref="Position"/> when the access was read: <see cref="Audited"/> with
/// its targets, <see cref="Stranded"/> when the blocked cells leave it no safe exit, or <see cref="Skipped"/> when the
/// request's budget ran out before it (not audited, and not assumed to keep access).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Audited), "audited")]
[JsonDerivedType(typeof(Stranded), "stranded")]
[JsonDerivedType(typeof(Skipped), "skipped")]
public abstract record PawnAccess
{
    private PawnAccess()
    {
    }

    public required PawnId Pawn { get; init; }

    public required Cell Position { get; init; }

    public sealed record Audited : PawnAccess
    {
        [JsonConverter(typeof(EquatableArrayJsonConverter<AccessTarget>))]
        public required EquatableArray<AccessTarget> Targets { get; init; }
    }

    public sealed record Stranded : PawnAccess;

    public sealed record Skipped : PawnAccess;
}

public sealed record SpatialAccessSnapshot
{
    public required Common.ObservationContext Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnAccess>))]
    public required EquatableArray<PawnAccess> Pawns { get; init; }
}

/// <summary>
/// budget_ms: main-thread milliseconds the audit may spend (about 70-80 ms per colonist,
/// independent of the blocked and target cell counts). Colonists past it come back
/// <see cref="PawnAccess.Skipped"/>. An empty <see cref="PawnIds"/> audits every mobile free colonist.
/// </summary>
public sealed record SpatialAccessRequest
{
    public required ReadScope Scope { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public required EquatableArray<Common.Cell> BlockedCells { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public required EquatableArray<Common.Cell> TargetCells { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> PawnIds { get; init; }

    public required uint BudgetMs { get; init; }
}

/// <summary>
/// Defense site census: bounded terrain, cover and raider-approach facts for layout planning, every cell of the
/// requested rectangle. A <see cref="Fogged"/> cell carries no facts; a <see cref="Seen"/> one carries the game's own.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Fogged), "fogged")]
[JsonDerivedType(typeof(Seen), "seen")]
public abstract record DefenseCell
{
    private DefenseCell()
    {
    }

    public required Cell Cell { get; init; }

    public sealed record Fogged : DefenseCell;

    /// <summary>
    /// <see cref="CoverFill"/> is the native edifice/cover fillPercent the shooting model uses (0 when open);
    /// <see cref="EdgeReachable"/> is native ground reachability from the cell to any map edge without opening doors,
    /// the ordinary walk-in raid approach.
    /// </summary>
    public sealed record Seen : DefenseCell
    {
        public required string Terrain { get; init; }

        public required bool Walkable { get; init; }

        public required bool Passable { get; init; }

        public required double CoverFill { get; init; }

        public required bool EdgeReachable { get; init; }

        /// <summary>The cell's foundation (a Bridge) is designated for removal.</summary>
        public required bool FoundationRemovalDesignated { get; init; }

        /// <summary>The cell is under any roof.</summary>
        public required bool Roofed { get; init; }

        /// <summary>The edifice standing on the cell; null for none.</summary>
        public required DefenseEdifice? Edifice { get; init; }

        /// <summary>
        /// The thing whose fillPercent <see cref="CoverFill"/> reports (GetCover, else the edifice) when the game's
        /// designators can remove it; null when the cell is open or its cover cannot be cleared.
        /// </summary>
        public required DefenseCover? Cover { get; init; }
    }
}

/// <summary>A defense cell's edifice: its def, its stuff (null for a stuffless one) and what it is.</summary>
public sealed record DefenseEdifice
{
    public required string DefName { get; init; }

    /// <summary>The stuff def (WoodLog, Plasteel); null for a stuffless edifice.</summary>
    public required string? Stuff { get; init; }

    public required bool PlayerOwned { get; init; }

    public required bool NaturalRock { get; init; }

    public required bool Door { get; init; }
}

/// <summary>
/// The clearance identity of one cover thing: what it is, which designation removes it and whether one already
/// stands.
/// </summary>
public sealed record DefenseCover
{
    public required string ThingId { get; init; }

    public required string DefName { get; init; }

    public required CoverKind Kind { get; init; }

    public required bool Designated { get; init; }
}

/// <summary>
/// One hostile lord observed on the map since load: where its first pawn was
/// seen (ground when on the map edge), and a bounded trail of one of its
/// pawns sampled every RaidTrack.SampleInterval ticks, so a census can find
/// where the raid crossed its boundary. Session-scoped, like traffic.
/// </summary>
public sealed record RaidTrack
{
    public required string LordId { get; init; }

    public required Tick SpawnTick { get; init; }

    public required Cell Spawn { get; init; }

    public required bool Ground { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Cell>))]
    public required EquatableArray<Cell> Trail { get; init; }
}

public sealed record DefenseSiteSnapshot
{
    public required Common.ObservationContext Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<DefenseCell>))]
    public required EquatableArray<DefenseCell> Cells { get; init; }
}

public sealed record DefenseSiteRequest
{
    public required ReadScope Scope { get; init; }

    public required Rectangle Region { get; init; }
}

/// <summary>
/// Lines of fire: native GenSight line of sight for every (firing, approach) pair. A pair with a fogged end is
/// <see cref="Fogged"/>, its distance only. Pairs are read firing-major under the request's budget_ms (about 3 us a pair); past it
/// the snapshot is skipped and holds only the pairs read.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Fogged), "fogged")]
[JsonDerivedType(typeof(Traced), "traced")]
public abstract record LineOfFire
{
    private LineOfFire()
    {
    }

    public required Cell From { get; init; }

    public required Cell To { get; init; }

    public required double Distance { get; init; }

    public sealed record Fogged : LineOfFire;

    public sealed record Traced : LineOfFire
    {
        public required bool LineOfSight { get; init; }
    }
}

/// <summary>
/// The pairs a read produced, firing-major. <see cref="Skipped"/> means the budget ran out: the pairs after
/// <see cref="Lines"/> were not read, so a pair absent from them was not traced, not blocked.
/// </summary>
public sealed record LinesOfFireSnapshot
{
    public required Common.ObservationContext Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<LineOfFire>))]
    public required EquatableArray<LineOfFire> Lines { get; init; }

    public required bool Skipped { get; init; }
}

public sealed record LinesOfFireRequest
{
    public required ReadScope Scope { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public required EquatableArray<Common.Cell> FiringCells { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public required EquatableArray<Common.Cell> ApproachCells { get; init; }

    public required uint BudgetMs { get; init; }
}

/// <summary>One requested cell of an excavation site read, in request order.</summary>
public sealed record ExcavationCell
{
    public required Cell Cell { get; init; }

    public required bool Fogged { get; init; }

    /// <summary>The mineable rock standing on the cell; null when none does.</summary>
    public required string? Definition { get; init; }

    /// <summary>False for a cell outside the map.</summary>
    public required bool Walkable { get; init; }

    public required bool MineDesignated { get; init; }

    /// <summary>Native accepts a mining designation on the cell.</summary>
    public required bool Eligible { get; init; }
}

/// <summary>
/// The site-level answer for one ordered cell set: the counterfactual roof support once every requested cell is
/// gone (unsupported while a collapse is pending), plus worker access to the access cell.
/// </summary>
public sealed record ExcavationSiteSnapshot
{
    public required Common.ObservationContext Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ExcavationCell>))]
    public required EquatableArray<ExcavationCell> Cells { get; init; }

    public required ExcavationSupport Support { get; init; }

    /// <summary>Why the site is unsupported; null when nothing names a blocker.</summary>
    public required string? SupportBlocker { get; init; }

    public required bool CollapsePending { get; init; }

    /// <summary>The free colonists who can mine from the access; empty when the access is unreachable.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> Workers { get; init; }

    public required bool AccessReachable { get; init; }
}

/// <summary>
/// One or more unique nonnegative cells, at least one of them on the map, and the access cell a miner works from.
/// </summary>
public sealed record ExcavationSiteRequest
{
    public ReadScope? Scope { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public EquatableArray<Common.Cell> Cells { get; init; }

    public Common.Cell? AccessCell { get; init; }
}

/// <summary>
/// One wall-upgrade site: the target wall a stone upgrade replaces (the original itself, or a cleanup's first
/// completed backup), the original wall, its two colonist-wall supports, the outward normal and the backup cells.
/// </summary>
public sealed record WallUpgradeSite
{
    public required Common.Ref Target { get; init; }

    /// <summary>The original wall's cell.</summary>
    public required Cell Origin { get; init; }

    /// <summary>The outward normal, a unit step (diagonal at a corner).</summary>
    public required Cell Normal { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Cell>))]
    public required EquatableArray<Cell> BackupCells { get; init; }

    /// <summary>Why the site cannot proceed (roof support, corner access); null when nothing blocks it.</summary>
    public required string? Blocker { get; init; }

    public required bool Designated { get; init; }

    /// <summary>The pending removal record that claims the target's designation; null when none does.</summary>
    public required string? RemovalId { get; init; }

    public required Common.Ref Original { get; init; }

    public required Common.Ref LeftSupport { get; init; }

    public required Common.Ref RightSupport { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Ref>))]
    public required EquatableArray<Common.Ref> CompletedBackups { get; init; }

    /// <summary>A cleanup site's completed stone wall; null on a replacement site.</summary>
    public required Common.Ref? Replacement { get; init; }

    /// <summary>
    /// The target's CAS token for the upgrade's guarded operations.
    /// </summary>
    public required SnapshotRef TargetSnapshot { get; init; }
}

public sealed record WallUpgradeSnapshot
{
    public required Common.ObservationContext Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<WallUpgradeSite>))]
    public required EquatableArray<WallUpgradeSite> Sites { get; init; }
}

public sealed record WallUpgradeSitesRequest
{
    public ReadScope? Scope { get; init; }

    public string? TargetId { get; init; }
}

/// <summary>
/// One source of the requested resource: a plant to cut or harvest, or a rock to mine. Id and DefName are the source
/// thing's load id and def (the method, mine or cut/harvest, is a rule over that def row); Cell is where it stands.
/// Yield is what taking it gives now; Distance is the nearest free colonist's straight-line distance. Buried is a
/// supported mine deposit no colonist can reach (usually fogged): a corridor excavation must reach it first, and only
/// a source that is not buried is reachable. Safety is the native excavation verdict: native_eligible for a plant,
/// supported_roof or open_surface for a rock. Taken: a reservation or a colonist's current job targets the source.
/// </summary>
public sealed record ResourceSource
{
    public required string Id { get; init; }

    public required string DefName { get; init; }

    public required Cell Cell { get; init; }

    public required double Yield { get; init; }

    public required bool Buried { get; init; }

    public required bool Designated { get; init; }

    public required string Safety { get; init; }

    public required double Distance { get; init; }

    public required bool Taken { get; init; }
}

/// <summary>
/// One resource's protected, hauler-reachable storage: its empty-slot capacity, the count already stored there, the
/// haulers and the candidate cells for a new stockpile.
/// </summary>
public sealed record StorageCapacity
{
    public required long Capacity { get; init; }

    public required long Stored { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnId>))]
    public required EquatableArray<PawnId> Haulers { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Cell>))]
    public required EquatableArray<Cell> Candidates { get; init; }
}

/// <summary>The requested resource's sources, designated first and then nearest first, and its storage.</summary>
public sealed record ResourceSourcesSnapshot
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<ResourceSource>))]
    public required EquatableArray<ResourceSource> Sources { get; init; }

    public required StorageCapacity Storage { get; init; }
}

/// <summary>Resource is the def name of the resource whose sources are read; a def the game does not hold is refused.</summary>
public sealed record ResourceSourcesRequest
{
    public ReadScope? Scope { get; init; }

    public required string Resource { get; init; }
}

/// <summary>
/// Realized consumption. Native keeps a saved hourly ring (2500-tick hours, 60 days) of what colony stock was
/// spent, per (definition, reason). A read names the last hour the brain already holds; the reply carries every
/// completed hour after it, sparse: an hour or row not listed spent nothing. Count is signed: an ejected refuel or
/// removed shell subtracts.
/// </summary>
public sealed record ConsumptionRow
{
    public required string Definition { get; init; }

    public required string Reason { get; init; }

    public required long Count { get; init; }
}

public sealed record ConsumptionHour
{
    public required int Hour { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ConsumptionRow>))]
    public required EquatableArray<ConsumptionRow> Rows { get; init; }
}

/// <summary>CurrentHour is still filling and is never listed; FirstHour is the first hour the ring covers.</summary>
public sealed record ConsumptionSnapshot
{
    public required int CurrentHour { get; init; }

    public required int FirstHour { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ConsumptionHour>))]
    public required EquatableArray<ConsumptionHour> Hours { get; init; }
}

/// <summary>SinceHour is the last hour the reader holds; the reply lists the completed hours after it (-1 reads the whole ring).</summary>
public sealed record ConsumptionRequest
{
    public required int SinceHour { get; init; }
}

/// <summary>
/// Storage occupancy. Native keeps a saved hourly ring (2500-tick hours, 15 days) of the item stacks held by each
/// of the player's storage groups (stockpile zones and storage buildings, by load id) on home maps, sampled at each
/// hour's first tick. A read names the groups and the last hour the reader does not need; the reply lists, per
/// requested group that has samples, every sample after SinceHour in ascending hour order. A group not listed has no
/// samples: a group created mid-window starts its own samples, a deleted group drops out. CurrentHour is the hour
/// now running.
/// </summary>
public sealed record StorageOccupancySample
{
    public required int Hour { get; init; }

    public required int Stacks { get; init; }
}

public sealed record StorageOccupancyGroup
{
    public required string Group { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<StorageOccupancySample>))]
    public required EquatableArray<StorageOccupancySample> Samples { get; init; }
}

public sealed record StorageOccupancySnapshot
{
    public required int CurrentHour { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<StorageOccupancyGroup>))]
    public required EquatableArray<StorageOccupancyGroup> Groups { get; init; }
}

/// <summary>SinceHour is the last hour the reader does not need (-1 reads the whole ring).</summary>
public sealed record StorageOccupancyRequest
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> Groups { get; init; }

    public required int SinceHour { get; init; }
}

/// <summary>
/// One corpse the census counts: the corpse, its inner pawn's class and, for
/// a buried body, the grave holding it (absent while unburied).
/// </summary>
public sealed record WasteItem
{
    public Common.Ref Thing { get; init; } = new();

    public Common.CorpseClass CorpseClass { get; init; }

    public Common.Ref? Grave { get; init; }
}

public sealed record WasteSnapshot
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<WasteItem>))]
    public EquatableArray<WasteItem> Items { get; init; }
}

public sealed record WasteReply
{
    public WasteSnapshot Outcome { get; init; } = null!;
}

/// <summary>One free colonist's player area restriction on the map.</summary>
public sealed record RecoveryRestriction
{
    public required Common.Ref Pawn { get; init; }

    /// <summary>The restricting area's load id; null when the pawn is unrestricted.</summary>
    public required string? AreaId { get; init; }
}

/// <summary>
/// The recovery census (#3770): every spawned free colonist's restriction and every unfogged colonist
/// building, each building's facts being its row in the bundle's building table. A read that cannot make it
/// fails the colony read.
/// </summary>
public sealed record RecoverySnapshot
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<RecoveryRestriction>))]
    public required EquatableArray<RecoveryRestriction> Restrictions { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Ref>))]
    public required EquatableArray<Common.Ref> Buildings { get; init; }
}

/// <summary>
/// One spawned humanlike on the map, joined to its pawn table row by <see cref="Pawn"/>. Admitted is a free colonist
/// of the player faction; Guest is hosted by the player faction (prisoners and slaves included). Recruitable is
/// Pawn_GuestTracker.Recruitable. The prospect facts (prisoners/recruit weighs a prisoner's use, a hostile's skills
/// rank it as a lance target) are read for every person: Verse.WildManUtility.IsWildMan, the skill/trait/age
/// biography and SummaryHealthPercent. After combat, a LuciferiumAddiction hediff (not worth capturing: stripped and
/// finished instead) and any worn apparel (every downed raider is stripped before it is captured).
/// </summary>
public sealed record PopulationPerson
{
    public required PawnId Pawn { get; init; }

    public required bool Admitted { get; init; }

    public required bool Guest { get; init; }

    public required bool Recruitable { get; init; }

    /// <summary>The pawn's ideoligion load id; null without Ideology or without an ideoligion.</summary>
    public required string? IdeoId { get; init; }

    public required bool WildMan { get; init; }

    public required PawnBiography Biography { get; init; }

    public required double HealthSummary { get; init; }

    public required bool LuciferiumAddicted { get; init; }

    public required bool WearingApparel { get; init; }

    /// <summary>Whether and how the colony holds the person: a colony prisoner, a colony guest, or neither.</summary>
    public required PersonCustody Custody { get; init; }

    /// <summary>Pawn_MindState.WillJoinColonyIfRescued.</summary>
    public required bool WillJoinIfRescued { get; init; }
}

/// <summary>
/// How the colony holds a person. <see cref="Prisoner"/> is a prisoner of the colony: its resistance, the
/// PrisonerInteractionModeDef it is under, the time held so far (the TimeAsPrisoner record, in ticks: the release
/// path's "held N days with resistance unbroken" clock), its MedicalCareCategory and care-cap conditions, its surgery
/// facts as on the pawn care read, its home faction's load id (null for a pawn without a non-player home faction),
/// and the goodwill change vanilla's Recipe_RemoveBodyPart violation report (-70) would make with it after
/// CalculateAdjustedGoodwillChange and the -100 floor (0 when the faction's goodwill cannot change or there is none).
/// <see cref="Guest"/> is hosted by the colony, neither prisoner nor slave: its MedicalCareCategory and care-cap
/// conditions. <see cref="None"/> is anyone else (a colonist, a slave, a visitor, a hostile).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PersonCustody.Prisoner), "prisoner")]
[JsonDerivedType(typeof(PersonCustody.Guest), "guest")]
[JsonDerivedType(typeof(PersonCustody.None), "none")]
public abstract record PersonCustody
{
    private protected PersonCustody() { }

    public sealed record Prisoner(
        double Resistance,
        string Interaction,
        long HeldTicks,
        Operations.MedicalCare MedicalCare,
        PawnConditions Conditions,
        PawnSurgery Surgery,
        Common.Ref? Faction,
        int HarvestGoodwillChange) : PersonCustody;

    public sealed record Guest(Operations.MedicalCare MedicalCare, PawnConditions Conditions) : PersonCustody;

    public sealed record None : PersonCustody;
}

public sealed record PopulationSnapshot
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<PopulationPerson>))]
    public required EquatableArray<PopulationPerson> Persons { get; init; }

    /// <summary>Whether Ideology is installed and, if so, its classic mode and the player faction's primary ideoligion.</summary>
    public required ColonyIdeology Ideology { get; init; }

    /// <summary>
    /// Owned-pawn names: every living named pawn the colony owns --
    /// player-faction colonists, slaves and animals, and colony prisoners --
    /// on any map, caravan or transporter; unnamed animals are absent.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<OwnedName>))]
    public required EquatableArray<OwnedName> OwnedNames { get; init; }
}

/// <summary>
/// The colony's ideology mode: <see cref="NotInstalled"/> without the Ideology expansion (ModsConfig.IdeologyActive
/// false), or <see cref="Installed"/> with IdeoManager.classicMode and the player faction's primary ideoligion load id
/// (null without one). Precepts ride the frame's ideology section.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ColonyIdeology.NotInstalled), "not_installed")]
[JsonDerivedType(typeof(ColonyIdeology.Installed), "installed")]
public abstract record ColonyIdeology
{
    private protected ColonyIdeology() { }

    public sealed record NotInstalled : ColonyIdeology;

    public sealed record Installed(bool Classic, string? PrimaryIdeoId) : ColonyIdeology;
}

/// <summary>
/// One owned pawn's short name (Name.ToStringShort) and thingIDNumber, which
/// orders pawns by creation: the higher number is the newer pawn.
/// </summary>
public sealed record OwnedName
{
    public required PawnId PawnId { get; init; }

    public required string ShortName { get; init; }

    public required int ThingId { get; init; }
}

public sealed record PopulationRequest
{
    public ReadScope? Scope { get; init; }
}

/// <summary>A settlement's faction as the player sees it.</summary>
[JsonConverter(typeof(WireEnumConverter<SettlementRelation>))]
public enum SettlementRelation
{
    /// <summary>The player's own settlement.</summary>
    [WireName("player")]
    Player,

    [WireName("hostile")]
    Hostile,

    [WireName("neutral")]
    Neutral,
}

/// <summary>A world settlement: its tile, its faction's relation and whether it trades now (TraderKind is empty for a settlement with no trader kind).</summary>
public sealed record Settlement
{
    public required string Id { get; init; }

    public required int Tile { get; init; }

    public required SettlementRelation Relation { get; init; }

    public required bool CanTrade { get; init; }

    public required string TraderKind { get; init; }
}

/// <summary>The read scope's map tile.</summary>
public sealed record WorldTile
{
    public required int Tile { get; init; }

    public required string Hilliness { get; init; }
}

/// <summary>The scope map's tile and the settlements within the requested radius of the requested tile (none without a tile).</summary>
public sealed record WorldSnapshot
{
    public required WorldTile Tile { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Settlement>))]
    public required EquatableArray<Settlement> Settlements { get; init; }
}

/// <summary>SettlementRadius is in tiles (0 when omitted: the requested tile only).</summary>
public sealed record WorldRequest
{
    public ReadScope? Scope { get; init; }

    public int? Tile { get; init; }

    public double SettlementRadius { get; init; }
}

public sealed record WorldRoute
{
    public int? Destination { get; init; }

    public bool? Reachable { get; init; }

    public long? EstimatedTicks { get; init; }
}

public sealed record CaravanState
{
    public required EntityRef Caravan { get; init; }

    public int? Tile { get; init; }

    public int? Destination { get; init; }

    public bool? Moving { get; init; }

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
}

public sealed record QuestReward
{
    public uint? ChoiceIndex { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Quantity>))]
    public EquatableArray<Quantity> Items { get; init; }

    public int? Favor { get; init; }

    public int? Goodwill { get; init; }

    public int? Psylink { get; init; }

    public int? PermitPoints { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Permits { get; init; }
}

public sealed record QuestObjective
{
    public required QuestObjectiveKind Kind { get; init; }

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

    public bool Spawned { get; init; }

    /// <summary>CompHackable.LockedOut and Autohack.</summary>
    public bool LockedOut { get; init; }

    public bool Autohack { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> EligiblePawnIds { get; init; }

    public bool Satisfied { get; init; }
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

    public long? DurationTicks { get; init; }

    public long? EndTick { get; init; }

    public bool? Alive { get; init; }

    public bool? Complete { get; init; }
}

public sealed record QuestGravEngine
{
    public string? EngineId { get; init; }

    public int? MapId { get; init; }

    public bool? Spawned { get; init; }

    public bool? Inspected { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> EligiblePawnIds { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> InspectingPawnIds { get; init; }
}

public sealed record WorldSite
{
    public string? Id { get; init; }

    public int? Tile { get; init; }

    public int? LayerId { get; init; }

    public required WorldSiteState State { get; init; }

    public int? MapId { get; init; }

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

    public bool? AllDone { get; init; }

    public string? DisallowedBuildingId { get; init; }

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
    public bool? AutoloadAvailable { get; init; }

    public bool? Autoload { get; init; }

    public bool? Loading { get; init; }

    public bool? AllRequiredLoaded { get; init; }

    public bool? ManualLaunchAvailable { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> LoadedPawnIds { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> PendingPawnIds { get; init; }
}

public sealed record QuestState
{
    public string? Id { get; init; }

    public Receipts.QuestStatus? State { get; init; }

    public long? AcceptedTick { get; init; }

    public long? ExpiresInTicks { get; init; }

    public bool? RequiresAccepter { get; init; }

    public bool? CanAccept { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Ref>))]
    public EquatableArray<Common.Ref> EligiblePawns { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<QuestReward>))]
    public EquatableArray<QuestReward> Rewards { get; init; }

    public string? ScriptDef { get; init; }

    public string? FactionId { get; init; }

    public int? MapId { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<QuestObjective>))]
    public EquatableArray<QuestObjective> Objectives { get; init; }

    public int? ChoicePartCount { get; init; }

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

    public bool? Player { get; init; }

    public bool? Hostile { get; init; }
}

public sealed record WorldMap
{
    public int? Id { get; init; }

    public int? Tile { get; init; }

    public bool? Home { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnState>))]
    public EquatableArray<PawnState> Pawns { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<QuestWorker>))]
    public EquatableArray<QuestWorker> QuestWorkers { get; init; }
}

public sealed record CaravanAssembly
{
    public string? Id { get; init; }

    public int? MapId { get; init; }

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
}

/// <summary>
/// Mixed legacy tools get separate read requests: no action/dryRun write branch.
/// </summary>
public sealed record BillsSnapshot
{
    public required Common.ObservationContext Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<BillStackRow>))]
    public required EquatableArray<BillStackRow> Benches { get; init; }
}

/// <summary>bench_id narrows the census to one bench; absent reads every player bench.</summary>
public sealed record BillsRequest
{
    public ReadScope? Scope { get; init; }

    public string? BenchId { get; init; }
}

/// <summary>
/// bench_def: the bench's ThingDef, which the recipe rows are read against in the catalog.
/// </summary>
public sealed record RecipesSnapshot
{
    public required SnapshotRef Snapshot { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<RecipeState>))]
    public required EquatableArray<RecipeState> Recipes { get; init; }

    public required string BenchDef { get; init; }
}

/// <summary>
/// bench_id names the bench whose recipe availability is read; required.
/// </summary>
public sealed record RecipesRequest
{
    public ReadScope? Scope { get; init; }

    public required string BenchId { get; init; }
}

/// <summary>One loose garment a pawn could wear: the item and the game's apparel score gain over what it wears now.</summary>
public sealed record GearCandidate
{
    public required GearItem Item { get; init; }

    public required double Gain { get; init; }
}

/// <summary>
/// One free colonist's loadout read. Token is the loadout's CAS token (the pawn, its outfit and its worn apparel's
/// forced and locked state). Candidates are every eligible loose item, best gain first (then thing id); a pawn that
/// cannot change apparel now (<see cref="Blocked"/>: unavailable, drafted, without manipulation or an outfit) has none.
/// The pawn's wear inputs (gender, developmental stage, body part groups) are what the definition catalog's apparel
/// rows cannot say: the brain applies the wear filter from those rows. The developmental stage is the pawn's single
/// DevelopmentalStage (the apparel filter's flag bit); body part groups are the BodyPartGroupDef names for which the
/// pawn has a part that is not missing. A pawn without an outfit tracker has no <see cref="ApparelPolicy"/>.
/// </summary>
public sealed record GearLoadout
{
    public required string Token { get; init; }

    public required PawnId Pawn { get; init; }

    public required PawnEquipment Equipment { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<GearCandidate>))]
    public required EquatableArray<GearCandidate> Candidates { get; init; }

    public required double ComfortableMinC { get; init; }

    public required double ComfortableMaxC { get; init; }

    public required bool Blocked { get; init; }

    public required ApparelPolicyState? ApparelPolicy { get; init; }

    public required GearLoadoutModel LoadoutModel { get; init; }

    public required Defs.Gender Gender { get; init; }

    public required Defs.DevelopmentalStage DevelopmentalStage { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> BodyPartGroups { get; init; }
}

/// <summary>
/// Where a loadout option comes from: worn now, loose on the map, in valid storage, or a bill to make it. The declaration
/// order is the planner's tie-break rank (worn first).
/// </summary>
[JsonConverter(typeof(WireEnumConverter<GearSource>))]
public enum GearSource
{
    [WireName("worn")]
    Worn,

    [WireName("loose")]
    Loose,

    [WireName("stored")]
    Stored,

    [WireName("bill")]
    Bill,
}

/// <summary>
/// One apparel option the loadout model may choose. Layers, body part groups and the def x stuff stats come from the
/// catalog (def rows and stat table); the option carries the instance: the id is the thing id for worn, loose and stored
/// items, bill:&lt;def&gt;/&lt;stuff&gt; for a producible definition. Stuff is absent for a def not made from stuff. A
/// bill option's recipe research and ingredients and the def's smoke-pop verb are brain rules over the catalog rows.
/// </summary>
public sealed record GearLoadoutOption
{
    public required string Id { get; init; }

    public required string DefName { get; init; }

    public required string? Stuff { get; init; }

    /// <summary>
    /// The item's quality; a bill's is Normal, its crafted quality being its <see cref="Crafters"/>' (a qualityless def's
    /// bill is qualityless).
    /// </summary>
    public required ItemQuality Quality { get; init; }

    public required GearSource Source { get; init; }

    /// <summary>The item's hit points over their maximum, at most 1; 1 for a bill and for a def without hit points.</summary>
    public required double Condition { get; init; }

    /// <summary>Worn by a corpse once; false for a bill.</summary>
    public required bool Tainted { get; init; }

    /// <summary>A worn garment the pawn cannot drop (locked, or forced by its outfit); false for every unworn option.</summary>
    public required bool Locked { get; init; }

    /// <summary>
    /// A bill option's crafters (#3850): the distinct facts <c>QualityUtility.GenerateQualityCreatedByPawn</c> reads of each
    /// free colonist able to work the recipe (a bill giver of one of its benches whose work the pawn can do, and the recipe's
    /// skill requirements). The native read offers no bill nobody can craft; empty for an item and for a bill without quality,
    /// whose crafter sets nothing.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<GearCrafter>))]
    public required EquatableArray<GearCrafter> Crafters { get; init; }
}

/// <summary>
/// One crafter's quality facts (#3850): the recipe's work skill level (<c>SkillRecord.Level</c>), whether the pawn has
/// Inspired_Creativity, and the <c>RoleEffect_ProductionQualityOffset</c> of its ideoligion role (0 with none or without Ideology).
/// </summary>
public sealed record GearCrafter(int Skill, bool Inspired, int QualityOffset);

/// <summary>
/// The loadout model's per-pawn inputs. Worn is every worn garment; options are the eligible loose or stored candidates
/// (best native gain first) and the producible definitions whose recipe research is finished, one per allowed stuff.
/// </summary>
public sealed record GearLoadoutModel
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<Trait>))]
    public required EquatableArray<Trait> Traits { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<GearLoadoutOption>))]
    public required EquatableArray<GearLoadoutOption> Worn { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<GearLoadoutOption>))]
    public required EquatableArray<GearLoadoutOption> Options { get; init; }
}

/// <summary>
/// The gear census: every free colonist's loadout, the map's seasonal climate, the stored apparel and the locked bills
/// (one row per apparel def whose recipe, the first by defName that makes it, is not available now, so no pawn's bill
/// options list it: who could craft it once it is, #3850).
/// </summary>
public sealed record GearSnapshot
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<GearLoadout>))]
    public required EquatableArray<GearLoadout> Pawns { get; init; }

    public required GearClimate Climate { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<GearStock>))]
    public required EquatableArray<GearStock> StoredApparel { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<GearLockedBill>))]
    public required EquatableArray<GearLockedBill> LockedBills { get; init; }
}

/// <summary>
/// The map's seasonal climate the gear planner dresses for: the outdoor temperature from season at the middle of each
/// local twelfth (0..11, degrees Celsius, without weather or indoor heating), the hours of daylight
/// (<c>GenCelestial.IsDaytime</c> at the tile) on the same sampled days, the current local twelfth and the ticks to the
/// next, and the strongest temperature condition affecting the map, absent when none is.
/// </summary>
public sealed record GearClimate
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<double>))]
    public required EquatableArray<double> Temperatures { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<double>))]
    public required EquatableArray<double> DaylightHours { get; init; }

    public required int CurrentTwelfth { get; init; }

    public required long TicksToNextTwelfth { get; init; }
}

/// <summary>
/// The crafters of a locked apparel bill (#3850). <see cref="Craftable"/> is false when no free colonist can work the recipe,
/// or the def has a quality and the recipe no work skill. A craftable def with a quality lists every able colonist's quality
/// facts; a craftable def without CompQuality lists none.
/// </summary>
public sealed record GearLockedBill
{
    public required string DefName { get; init; }

    public required bool Craftable { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<GearCrafter>))]
    public required EquatableArray<GearCrafter> Crafters { get; init; }
}

/// <summary>
/// Stored apparel of one (def, stuff, quality, hit-point band), aggregated over usable, untainted, unforbidden apparel in
/// valid storage over half its hit points. The band is floor(condition x 10), at most 9 (9 for a def without hit points).
/// Stuff is absent for a def not made from stuff.
/// </summary>
public sealed record GearStock
{
    public required string DefName { get; init; }

    public required string? Stuff { get; init; }

    public required ItemQuality Quality { get; init; }

    public required int HpBand { get; init; }

    public required int Count { get; init; }
}

/// <summary>
/// One trader a session could open with: a map caravan's trader pawn, or a passing orbital trade ship (Orbital).
/// CanTrade is native's verdict that a session can open now (a pawn: CanTradeNow, not dismissed and arrived; a ship:
/// CanTradeNow, a powered beacon and a colonist able to use a console with it). Travelling is a caravan still walking
/// to its trade spot. FactionId is empty for a trader without a faction.
/// </summary>
public sealed record Trader
{
    public required string Id { get; init; }

    public required bool Orbital { get; init; }

    public required string Kind { get; init; }

    public required string FactionId { get; init; }

    public required bool CanTrade { get; init; }

    public required bool Travelling { get; init; }

    public required int GoodsStacks { get; init; }
}

/// <summary>
/// The map's traders, in id order, and the colonists eligible to negotiate (not dead, downed, in a mental state or
/// social-incapable), best trade price improvement first.
/// </summary>
public sealed record TradersSnapshot
{
    public required Common.ObservationContext Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Trader>))]
    public required EquatableArray<Trader> Traders { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnId>))]
    public required EquatableArray<PawnId> Negotiators { get; init; }
}

public sealed record TradersRequest
{
    public ReadScope? Scope { get; init; }
}

/// <summary>
/// One trade sheet line: the line id SetTradeLines addresses, the definition (empty on the favor row, which has no
/// ThingDef), both sides' counts, both prices, the staged transfer and its bounds. Currency marks the currency row
/// (the royal favor row too). Pawn is the offered pawn on a pawn line. ThingId is the
/// line's first colony thing (empty when the colony holds none, and on a pawn line), ZoneId the stockpile it lies in
/// (empty off a stockpile) and Grade its quality and hit points.
/// </summary>
public sealed record TradeLine
{
    public required string LineId { get; init; }

    public required string DefName { get; init; }

    public required string Stuff { get; init; }

    public required long ColonyCount { get; init; }

    public required long TraderCount { get; init; }

    public required double BuyPrice { get; init; }

    public required double SellPrice { get; init; }

    public required bool TraderWillTrade { get; init; }

    public required bool Currency { get; init; }

    /// <summary>The offered pawn on a pawn line, null on every other line.</summary>
    public required TradePawn? Pawn { get; init; }

    public required long TransferCount { get; init; }

    public required long MinimumCount { get; init; }

    public required long MaximumCount { get; init; }

    public required string ThingId { get; init; }

    public required TradeThingGrade Grade { get; init; }

    public required string ZoneId { get; init; }
}

/// <summary>
/// A line thing's quality (the QualityCategory index) and hit-point fraction, what a gear sale ranks; Ungraded for a
/// line without a colony thing, or a thing lacking either.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TradeThingGrade.Graded), "graded")]
[JsonDerivedType(typeof(TradeThingGrade.Ungraded), "ungraded")]
public abstract record TradeThingGrade
{
    private protected TradeThingGrade() { }

    public sealed record Graded(int Quality, double HitPointsFraction) : TradeThingGrade;

    public sealed record Ungraded : TradeThingGrade;
}

/// <summary>
/// A pawn trade line's pawn: its load id, gender, skills and violence capability (what a purchase ranks), and the
/// prisoner sale facts PreTraded and AllSellableColonyPawns read (guest status, downed, and the extra home / host
/// faction a sale angers; each faction id empty when the pawn has none).
/// </summary>
public sealed record TradePawn
{
    public required string Id { get; init; }

    public required Defs.Gender Gender { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Skill>))]
    public required EquatableArray<Skill> Skills { get; init; }

    public required bool ViolenceCapable { get; init; }

    public required TradePawnGuest Guest { get; init; }

    public required bool Downed { get; init; }

    public required string ExtraHomeFactionId { get; init; }

    public required string ExtraHostFactionId { get; init; }
}

/// <summary>A trade pawn's guest status and whether a prisoner is secure, or NoGuestTracker for a pawn without one.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TradePawnGuest.Tracked), "tracked")]
[JsonDerivedType(typeof(TradePawnGuest.NoGuestTracker), "no_guest_tracker")]
public abstract record TradePawnGuest
{
    private protected TradePawnGuest() { }

    public sealed record Tracked(Defs.GuestStatus Status, bool PrisonerSecure) : TradePawnGuest;

    public sealed record NoGuestTracker : TradePawnGuest;
}

/// <summary>The adapter's one live trade: Idle, or a negotiator engaged with a trader, walking to open (Open false) or holding the open session.</summary>
public sealed record TradeSession
{
    public required Common.ObservationContext Context { get; init; }

    public required TradeEngagement Engagement { get; init; }
}

/// <summary>Whether a negotiator is walking to or trading with a trader.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TradeEngagement.Engaged), "engaged")]
[JsonDerivedType(typeof(TradeEngagement.Idle), "idle")]
public abstract record TradeEngagement
{
    private protected TradeEngagement() { }

    public sealed record Engaged(Common.TradeTarget Target, PawnId Negotiator, bool Open) : TradeEngagement;

    public sealed record Idle : TradeEngagement;
}

public sealed record TradeSessionRequest
{
    public ReadScope? Scope { get; init; }
}

/// <summary>
/// The open session's whole sheet. CurrencyKind is the session's currency (vanilla TradeCurrency): silver, or the
/// trader faction's royal favor (the Royalty tribute collector), where every line's SellPrice is its favor value and
/// the currency line's TransferCount the favor the negotiator gains. DealSignature is the token AcceptTrade carries.
/// </summary>
public sealed record TradeSheet
{
    public required Common.ObservationContext Context { get; init; }

    public required string SessionId { get; init; }

    public required Common.TradeTarget Target { get; init; }

    public required PawnId Negotiator { get; init; }

    public required bool GiftMode { get; init; }

    public required bool CanTradeNow { get; init; }

    public required TradeBalance Balance { get; init; }

    public required bool TraderHasEnoughSilver { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<TradeLine>))]
    public required EquatableArray<TradeLine> Lines { get; init; }

    public required string DealSignature { get; init; }

    public required Defs.TradeCurrency CurrencyKind { get; init; }
}

/// <summary>
/// The deal's balance on the currency line: its signed transfer (negative when the colony pays) and whether the colony
/// can afford it; NoCurrencyLine when the deal has no currency tradeable.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TradeBalance.Counted), "counted")]
[JsonDerivedType(typeof(TradeBalance.NoCurrencyLine), "no_currency_line")]
public abstract record TradeBalance
{
    private protected TradeBalance() { }

    public sealed record Counted(long Transfer, bool ColonyCanAfford) : TradeBalance;

    public sealed record NoCurrencyLine : TradeBalance;
}

public sealed record TradeSheetRequest
{
    public ReadScope? Scope { get; init; }

    public string? SessionId { get; init; }

}

public sealed record TradeAcquisitionRequest
{
    public ReadScope? Scope { get; init; }

    public Operations.FormCaravanIntent? Pack { get; init; }
}

/// <summary>A usable comms console and the colonists able to use it now.</summary>
public sealed record TradeConsole
{
    public required string Id { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> NegotiatorIds { get; init; }
}

/// <summary>A passing ship; TraderKind is empty for a ship that is not a trade ship.</summary>
public sealed record PassingTradeShip
{
    public required string TraderKind { get; init; }
}

/// <summary>
/// One requestable trader: the faction, channel and trader kind, the goodwill it costs and the relation after paying
/// (Hostile, Neutral or Ally), the last request tick, the cooldown left, the longest arrival delay, whether vanilla
/// would accept the request now and the colonists able to make it.
/// </summary>
public sealed record TradeRequestOption
{
    public required string FactionId { get; init; }

    public required Common.TradeRequestKind Kind { get; init; }

    public required string TraderKind { get; init; }

    public required int Goodwill { get; init; }

    public required int GoodwillCost { get; init; }

    public required string RelationAfterPayment { get; init; }

    public required long LastRequestTick { get; init; }

    public required long CooldownRemainingTicks { get; init; }

    public required long ArrivalMaxTicks { get; init; }

    public required bool Eligible { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> NegotiatorIds { get; init; }
}

/// <summary>The comms consoles, passing ships, requestable traders, queued trader arrivals and queued comms work of the map.</summary>
public sealed record TradeAcquisition
{
    public required Common.ObservationContext Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<TradeConsole>))]
    public required EquatableArray<TradeConsole> Consoles { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PassingTradeShip>))]
    public required EquatableArray<PassingTradeShip> PassingShips { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<TradeRequestOption>))]
    public required EquatableArray<TradeRequestOption> Requests { get; init; }

    public required bool OrbitalAvailable { get; init; }

    /// <summary>The estimate of the requested pack, null when the request names none.</summary>
    public required TradePackEstimate? Pack { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<TradeRequestArrival>))]
    public required EquatableArray<TradeRequestArrival> Arrivals { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<TradeCommsWork>))]
    public required EquatableArray<TradeCommsWork> CommsWork { get; init; }
}

/// <summary>A queued trader arrival incident: the faction, channel, trader kind and the tick it fires.</summary>
public sealed record TradeRequestArrival
{
    public required string FactionId { get; init; }

    public required Common.TradeRequestKind Kind { get; init; }

    public required string TraderKind { get; init; }

    public required long ArrivalTick { get; init; }
}

/// <summary>A colonist's queued or current comms console job with a faction.</summary>
public sealed record TradeCommsWork
{
    public required string NegotiatorId { get; init; }

    public required string ConsoleId { get; init; }

    public required string FactionId { get; init; }
}

/// <summary>
/// Native's caravan-formation calculation for one exact crew and cargo: Unpackable when the game refuses the pack,
/// otherwise its mass, the days of food and till rot it carries, and both legs of the round trip.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TradePackEstimate.Packed), "packed")]
[JsonDerivedType(typeof(TradePackEstimate.Unpackable), "unpackable")]
public abstract record TradePackEstimate
{
    private protected TradePackEstimate() { }

    public sealed record Packed(double MassUsage, double MassCapacity, double FoodDays, double FoodRotDays, PackRoute Outbound, PackRoute Home) : TradePackEstimate;

    public sealed record Unpackable : TradePackEstimate;
}

/// <summary>One leg of a packed trip: the estimated ticks to arrive, or Unreachable when the world pather finds no path.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PackRoute.Reachable), "reachable")]
[JsonDerivedType(typeof(PackRoute.Unreachable), "unreachable")]
public abstract record PackRoute
{
    private protected PackRoute() { }

    public sealed record Reachable(long EstimatedTicks) : PackRoute;

    public sealed record Unreachable : PackRoute;
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
/// (the row's forbidden flag is the stack's; the brain decides which forbidden food
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

public sealed record CorpseHandling
{
    public string StockId { get; init; } = "";

    public Common.Cell? Cell { get; init; }

    public string? HaulerId { get; init; }

    public bool FrozenDestination { get; init; }
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

public sealed record PatientForecast
{
    public string? PawnId { get; init; }

    public double? MoodTarget { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }
}

public sealed record ForecastFacts
{
    public required NeedsForecastRead Needs { get; init; }
    public CoupledForecastRead? Coupled { get; init; }

    public FoodSupplyFacts? CombinedFoodSupply { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PatientForecast>))]
    public EquatableArray<PatientForecast> Patients { get; init; }
}

/// <summary>
/// The comfort census (#3771), read whole: the free, conscious colonists able to manipulate (sorted by load id), every
/// indoor eating surface, every indoor dining seat beside one, every recreation source (a joy building not burning and,
/// when powered, switched on), and the joy tolerance matrix. The native read throws when it cannot read the census.
/// </summary>
public sealed record ComfortFacts
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnId>))]
    public required EquatableArray<PawnId> People { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ComfortSurface>))]
    public required EquatableArray<ComfortSurface> Surfaces { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<DiningSeat>))]
    public required EquatableArray<DiningSeat> Dining { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<RecreationSource>))]
    public required EquatableArray<RecreationSource> Recreation { get; init; }

    public required RecreationCensus Joy { get; init; }
}

/// <summary>
/// An indoor eating surface: the proper indoor room hosting it (the room census's Room.ID) and its standable, unbuilt
/// cardinal neighbours, sorted by z then x.
/// </summary>
public sealed record ComfortSurface(string Room,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<Cell>))] EquatableArray<Cell> Adjacent);

/// <summary>
/// An indoor seat beside an eating surface, in the proper indoor room hosting it: who of the census people can safely
/// reach it and who is eating at it now.
/// </summary>
public sealed record DiningSeat(string Id, string Room,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<PawnId>))] EquatableArray<PawnId> AccessibleTo,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<PawnId>))] EquatableArray<PawnId> Users);

/// <summary>A recreation source of joy kind <paramref name="Kind"/>, indoors in a proper room or outdoors: who can reach it and who is using it now.</summary>
public sealed record RecreationSource(string Id, string Kind, ComfortHost Host,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<PawnId>))] EquatableArray<PawnId> AccessibleTo,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<PawnId>))] EquatableArray<PawnId> Users);

/// <summary>Where a recreation source stands: in a proper indoor room (the room census's Room.ID), or outdoors.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ComfortHost.Indoors), "indoors")]
[JsonDerivedType(typeof(ComfortHost.Outdoors), "outdoors")]
public abstract record ComfortHost
{
    private protected ComfortHost() { }

    public sealed record Indoors(string Room) : ComfortHost;

    public sealed record Outdoors : ComfortHost;
}

/// <summary>
/// The joy kinds of the recreation sources (sorted by def name) and, for every census colonist with a joy need, whether
/// the colonist is bored of each kind (the vector follows <see cref="Kinds"/>).
/// </summary>
public sealed record RecreationCensus(
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<string>))] EquatableArray<string> Kinds,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<JoyTolerance>))] EquatableArray<JoyTolerance> Pawns);

public sealed record JoyTolerance(PawnId Pawn,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<bool>))] EquatableArray<bool> Bored);

/// <summary>
/// One loose player or unowned item an upkeep concern may haul, read whole: its id and def, its stack count, whether it
/// rots (and in how many ticks at the current temperature) and whether it is forbidden.
/// </summary>
public sealed record UpkeepItem
{
    public required string Id { get; init; }

    public required string Definition { get; init; }

    public required long Count { get; init; }

    public required ItemRot Rot { get; init; }

    public required bool Forbidden { get; init; }
}

/// <summary>Whether an item rots: an active CompRottable's ticks until it rots at the current temperature, or never.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ItemRot.Rotting), "rotting")]
[JsonDerivedType(typeof(ItemRot.Never), "never")]
public abstract record ItemRot
{
    private protected ItemRot() { }

    public sealed record Rotting(long Ticks) : ItemRot;

    public sealed record Never : ItemRot;
}

/// <summary>
/// One player bed, read whole: its kind flags, owners, users and the census people who can reach it, its roof,
/// ambient temperature and room, its quality and its stuff (null for a stuffless def). Its def and cell are its
/// building table row's.
/// </summary>
public sealed record UpkeepBed
{
    public required string Id { get; init; }

    public required bool Medical { get; init; }

    public required bool Prisoners { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnId>))]
    public required EquatableArray<PawnId> Owners { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnId>))]
    public required EquatableArray<PawnId> Users { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnId>))]
    public required EquatableArray<PawnId> AccessibleTo { get; init; }

    public required bool Roofed { get; init; }

    /// <summary>The bed's ambient temperature (Building_Bed.AmbientTemperature).</summary>
    public required double TemperatureC { get; init; }

    /// <summary>The native id (Room.ID) of the room the bed stands in, outdoors included; the room census holds its stats.</summary>
    public required string Room { get; init; }

    public required BedQuality Quality { get; init; }

    public required string? Stuff { get; init; }

    public required bool ForSlaves { get; init; }
}

/// <summary>A bed's quality: its QualityCategory, or ungraded for a def without CompQuality (a sleeping spot).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(BedQuality.Graded), "graded")]
[JsonDerivedType(typeof(BedQuality.Ungraded), "ungraded")]
public abstract record BedQuality
{
    private protected BedQuality() { }

    public sealed record Graded(Defs.QualityCategory Category) : BedQuality;

    public sealed record Ungraded : BedQuality;
}


/// <summary>
/// One damageable (useHitPoints) spawned player building, read whole: its def, stuff (null for a def not made from
/// stuff) and position, whether the Home area covers its whole footprint, and its hit points. Its flammability and
/// repair priority are the def rows' (static-data census).
/// </summary>
public sealed record UpkeepStructure
{
    public required string Id { get; init; }

    public required string Definition { get; init; }

    public required string? Stuff { get; init; }

    public required Cell Cell { get; init; }

    public required bool Home { get; init; }

    public required int HitPoints { get; init; }

    public required int MaxHitPoints { get; init; }
}

/// <summary>
/// One person of the sleeping census, read whole. OwnedBed: the bed the pawn owns, null without one. Partners: lover,
/// spouse and fiance relations to living pawns on the same map (native direct relations). BedSharingAllowed: the
/// native willingness to share a bed with every listed partner (the SharedBed precepts; with no partner, the plain
/// SharedBed precept), true without Ideology. Title: the most senior royal title, or untitled.
/// </summary>
public sealed record UpkeepPerson
{
    public required PawnId Pawn { get; init; }

    public required string? OwnedBed { get; init; }

    /// <summary>StatDefOf.ComfyTemperatureMin of the pawn.</summary>
    public required double ComfortableMinC { get; init; }

    /// <summary>StatDefOf.ComfyTemperatureMax of the pawn.</summary>
    public required double ComfortableMaxC { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnId>))]
    public required EquatableArray<PawnId> Partners { get; init; }

    public required bool BedSharingAllowed { get; init; }

    public required RoyalStanding Title { get; init; }
}

/// <summary>
/// A person's royal standing: untitled (no title, or Royalty inactive), or the most senior title's defName, whose
/// seniority and bedroom requirements are the RoyalTitleDef row less what the pawn's own state waives (the Ascetic trait;
/// Precepts are the ideoligion's precept defNames, sorted, whose disablingPrecepts drop a requirement; empty without an
/// ideoligion).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RoyalStanding.Titled), "titled")]
[JsonDerivedType(typeof(RoyalStanding.Untitled), "untitled")]
public abstract record RoyalStanding
{
    private protected RoyalStanding() { }

    public sealed record Titled(string Title, bool Ascetic,
        [property: JsonConverter(typeof(EquatableArrayJsonConverter<string>))] EquatableArray<string> Precepts) : RoyalStanding;

    public sealed record Untitled : RoyalStanding;
}

/// <summary>
/// One held permit's cooldown: the ticks until it can be used again (0 when ready), read at the read's tick.
/// </summary>
public sealed record PermitCooldown
{
    public required string Permit { get; init; }

    public required int CooldownRemainingTicks { get; init; }
}

/// <summary>
/// A pawn's standing with one royal faction: its current title (null while it holds none), favor, permit points,
/// held permits (sorted) and each held permit's cooldown.
/// </summary>
public sealed record PawnRoyalHolding
{
    public required string FactionDef { get; init; }

    public required string? Title { get; init; }

    public required int Favor { get; init; }

    public required int PermitPoints { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> Permits { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PermitCooldown>))]
    public required EquatableArray<PermitCooldown> PermitCooldowns { get; init; }
}

/// <summary>
/// One known psycast (an Ability def with IsPsycast): the level, costs, cooldown and target kind are the AbilityDef
/// row; cooldown_remaining_ticks is its remaining cooldown at the read's tick.
/// </summary>
public sealed record PawnPsycast
{
    public required string DefName { get; init; }

    public required int CooldownRemainingTicks { get; init; }
}

/// <summary>
/// The psylink neuroformer or a psycast neurotrainer: held counts the unforbidden spawned stacks on the player's
/// home maps, craftable whether a recipe that is available now makes it. What a trainer teaches and whether a
/// trader sells it are its ThingDef row.
/// </summary>
public sealed record NeuroformerStock
{
    public required string DefName { get; init; }

    public required int Held { get; init; }

    public required bool Craftable { get; init; }
}

/// <summary>
/// A psylinked pawn's psyfocus (0-1), neural heat (entropy) and its ceiling (PsychicEntropyMax), read at the read's
/// tick from Pawn_PsychicEntropyTracker.
/// </summary>
public sealed record PawnPsylink
{
    public required double Psyfocus { get; init; }

    public required double Entropy { get; init; }

    public required double EntropyMax { get; init; }
}

/// <summary>
/// PawnState.royalty: the pawn's holdings per royal faction, its known psycasts and its psylink (null for a pawn
/// with no psylink).
/// </summary>
public sealed record PawnRoyalty
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnRoyalHolding>))]
    public required EquatableArray<PawnRoyalHolding> Holdings { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnPsycast>))]
    public required EquatableArray<PawnPsycast> Psycasts { get; init; }

    public required PawnPsylink? Psylink { get; init; }
}

/// <summary>
/// Pending bestowing ceremonies: one per colonist holding an ongoing or
/// offered bestowing-ceremony quest (RoyalTitleUtility.GetCurrentBestowingCeremonyQuest).
/// quest is the quest load id the quest census lists. pawn is the colonist to be bestowed, title the def
/// the ceremony awards (GetTitleAwardedWhenUpdating; null when the favor awards none), accepted whether the
/// quest is Ongoing (else NotYetAccepted), bestower_waiting whether the bestower
/// stands on the map in the lord's Wait toil (the ceremony starts only on the
/// player's command) and started whether the bestower's ceremony lord job has started the ritual.
/// </summary>
public sealed record BestowingCeremony
{
    public required string Quest { get; init; }

    public required PawnId Pawn { get; init; }

    public required string? Title { get; init; }

    public required bool Accepted { get; init; }

    public required bool BestowerWaiting { get; init; }

    public required bool Started { get; init; }
}

/// <summary>
/// Throne ownership: thrones lists every spawned player throne
/// (Building_Throne) with its assigned owner; owner is null when none is
/// assigned. A throne appears once it stands, so a throne missing from the
/// list was built after the read.
/// </summary>
public sealed record RoyalThrone
{
    public required Common.Ref Thing { get; init; }

    public required Common.Ref? Owner { get; init; }
}

/// <summary>
/// The Royalty colony section: the colony facts, or <see cref="Inactive"/> without Royalty. A failed read fails the
/// colony read.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RoyaltySection.Active), "active")]
[JsonDerivedType(typeof(RoyaltySection.Inactive), "inactive")]
public abstract record RoyaltySection
{
    private protected RoyaltySection() { }

    public sealed record Active(RoyaltyColonyFacts Facts) : RoyaltySection;

    public sealed record Inactive : RoyaltySection;
}

/// <summary>The neuroformer stock, the pending bestowing ceremonies and the standing thrones of the player's home maps.</summary>
public sealed record RoyaltyColonyFacts
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<NeuroformerStock>))]
    public required EquatableArray<NeuroformerStock> Neuroformers { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<BestowingCeremony>))]
    public required EquatableArray<BestowingCeremony> Ceremonies { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<RoyalThrone>))]
    public required EquatableArray<RoyalThrone> Thrones { get; init; }
}

/// <summary>
/// pawn refers to the bundle pawn table row, whose animal_state
/// carries the herd facts.
/// </summary>
/// <summary>One player animal of the upkeep census, read whole: its pawn (the herd facts ride its pawn table row) and whether native manages it by rope.</summary>
public sealed record ColonyAnimal
{
    public required PawnId Pawn { get; init; }

    public required AnimalRoping Roping { get; init; }
}

/// <summary>
/// Whether native manages an animal by rope (AnimalPenUtility.NeedsToBeManagedByRope, which tests Pawn.Roamer): a roped
/// animal carries its roaming facts (#3812) and the closest pen that suits it (its pen comp's parent id, null when none
/// does); an unroped one is never walked off the map.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AnimalRoping.Roped), "roped")]
[JsonDerivedType(typeof(AnimalRoping.Unroped), "unroped")]
public abstract record AnimalRoping
{
    private protected AnimalRoping() { }

    public sealed record Roped(AnimalRoam Roam, string? SuitablePen) : AnimalRoping;

    public sealed record Unroped : AnimalRoping;
}

/// <summary>
/// A roamer's facts (#3812): its race def; the game's Pawn.RoamMtbDays, the mean days between the Roaming mental states
/// that walk it off the map (ThinkNode_ChancePerDay_Roam); its MarketValue stat, what that loses; and whether it can reach
/// the map edge now (Pawn.CanReachMapEdge, the gate MentalStateWorker_Roaming.CanRoamNow checks), which a pen takes away.
/// </summary>
public sealed record AnimalRoam
{
    public required string Race { get; init; }

    public required double MtbDays { get; init; }

    public required double MarketValue { get; init; }

    public required bool ReachesMapEdge { get; init; }
}

/// <summary>
/// One player power trader or battery: the building (its service state is its building table row) and
/// whether a roof covers its whole footprint.
/// </summary>
public sealed record DevelopmentPower
{
    public Common.Ref Building { get; init; } = new();

    public bool Roofed { get; init; }

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
    public Common.Ref Geyser { get; init; } = new();

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public EquatableArray<Common.Cell> Cells { get; init; }

    public bool Occupied { get; init; }
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
    /// <summary>Native seasonal growth eligibility at the observation instant and each following day for one 60-day year.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<bool>))]
    public EquatableArray<bool> GrowingYear { get; init; }

    public double? GrowingDays { get; init; }

    public double? GrowingDaysRemaining { get; init; }

    public bool? SowingNow { get; init; }

    public double? GrowingDaysUntil { get; init; }

    public string? Season { get; init; }

    public int? DayOfYear { get; init; }

    public double? NonGrowingDays { get; init; }
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

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public EquatableArray<Common.Cell> GrowthCells { get; init; }

    public bool? LitNow { get; init; }

    public string? PowerNetId { get; init; }
}

public sealed record PlantGrower
{
    public Common.Ref? Building { get; init; }

    public string? CropDefName { get; init; }

    public bool? CanSow { get; init; }
}

public sealed record GrowRoom
{
    public Common.Ref? Room { get; init; }

    /// <summary>The room's key in the cell grid (RoomState.GridRoom); absent when every cell is fogged.</summary>
    public int? GridRoom { get; init; }

    public double? TemperatureC { get; init; }
}

public sealed record PowerHeadroom
{
    public string? Id { get; init; }

    public double? GenerationW { get; init; }

    public double? SolarW { get; init; }

    public double? WindW { get; init; }

    public double? ConsumptionW { get; init; }

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
    public required GearSnapshot Gear { get; init; }

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

    public bool? Edible { get; init; }

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

/// <summary>
/// One harvest or hunt source of the colony read: the source thing (its head is a table row), its CAS token, the
/// resource it yields and how many units, whether it is designated (<see cref="AcquisitionDesignation"/>), whether a
/// pawn's reservation or a colonist's current job targets it, and its kind's own facts (<see cref="AcquisitionKind"/>).
/// </summary>
public sealed record AcquisitionFacts
{
    public required Common.Ref Source { get; init; }

    /// <summary>The source's exact entity snapshot CAS token.</summary>
    public required string Token { get; init; }

    public required string Resource { get; init; }

    /// <summary>The units one harvest yields (a sampled YieldNow for a plant, one corpse for a hunt).</summary>
    public required double Yield { get; init; }

    public required AcquisitionDesignation Designation { get; init; }

    public required bool Taken { get; init; }

    public required AcquisitionKind Kind { get; init; }
}

/// <summary>
/// A source's harvest or hunt designation: <see cref="Open"/> without one, <see cref="Designated"/> with the tick
/// native first saw it (reset on load).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AcquisitionDesignation.Open), "open")]
[JsonDerivedType(typeof(AcquisitionDesignation.Designated), "designated")]
public abstract record AcquisitionDesignation
{
    private protected AcquisitionDesignation() { }

    public sealed record Open : AcquisitionDesignation;

    public sealed record Designated(Tick Since) : AcquisitionDesignation;
}

/// <summary>
/// What a source is. <see cref="Plant"/>: Food is every eater WillEat the harvest, Growth the plant's growth fraction
/// (0..1), Plantation a sown tree of a harvest-destroys crop standing in a growing zone (RimWorld's growers fell it
/// only at maturity; the brain may fell it earlier). <see cref="Hunt"/>: a wild animal that bears a corpse, whatever
/// policy then decides. MeatAmount is the live animal's MeatAmount stat (wounds, missing parts and malnutrition scale
/// it); HerdSize counts same-race animals (the prey included) within the request's herd radius; Fogged and
/// InMentalState are native's current reading of the animal. The brain prices the meat off the race and meat rows and
/// derives food, revenge chance, predator, melee-only and tree.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AcquisitionKind.Plant), "plant")]
[JsonDerivedType(typeof(AcquisitionKind.Hunt), "hunt")]
public abstract record AcquisitionKind
{
    private protected AcquisitionKind() { }

    public sealed record Plant(bool Food, double Growth, bool Plantation) : AcquisitionKind;

    public sealed record Hunt(double MeatAmount, uint HerdSize, bool Downed, bool Sleeping, bool Fogged, bool InMentalState) : AcquisitionKind;
}

/// <summary>
/// Raw hunt census: what the brain needs to decide whether a hunt row is offered. Native states
/// facts and keeps only physical validity; weapon rules, hunter eligibility and
/// the pending-hunt cap are brain policy (policy.HuntGate).
/// One free colonist. HuntingActive is WorkIsActive. WeaponDef is the primary weapon def (null for an unarmed pawn);
/// the brain reads its verbs and projectile off the ThingDef rows.
/// RangedBlockingShield mirrors WorkGiver_HunterHunt.HasShieldAndRangedWeapon.
/// </summary>
public sealed record HunterFacts
{
    public required PawnId Pawn { get; init; }

    public required Cell Position { get; init; }

    public required bool Downed { get; init; }

    public required bool InMentalState { get; init; }

    public required bool HuntingActive { get; init; }

    public required string? WeaponDef { get; init; }

    public required bool RangedBlockingShield { get; init; }
}

public sealed record HuntCensus
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<HunterFacts>))]
    public required EquatableArray<HunterFacts> Hunters { get; init; }
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
/// The MeatAmount of the fresh humanlike corpses near a bench, summed per race; the brain prices it off the race's meat def.
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

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Keys { get; init; }
}

public sealed record ChoiceDialog
{
    public int? WindowId { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ChoiceDialogOption>))]
    public EquatableArray<ChoiceDialogOption> Options { get; init; }

    public bool? Interactive { get; init; }
}


public sealed record FoodSupplySection
{
    public FoodSupplyFacts Outcome { get; init; } = null!;
}

public sealed record ForecastSection
{
    public ForecastFacts Outcome { get; init; } = null!;
}

public sealed record DevelopmentSection
{
    public DevelopmentFacts Outcome { get; init; } = null!;
}

/// <summary>
/// The Home census (#3771): the Home revision (every Home edit advances it), the game's auto-expand setting
/// (Find.PlaySettings.autoHomeArea: the game expands Home around new player buildings while true), every current Home
/// cell (unordered; the home-area planner diffs its target against them), and one row per census target: every
/// colonist building and every stockpile, the governor owns them whoever built them. The native read throws when it
/// cannot read the census.
/// </summary>
public sealed record HomeCoverageFacts
{
    public required long Revision { get; init; }

    public required bool AutoHomeArea { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Cell>))]
    public required EquatableArray<Cell> HomeCells { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<HomeCoverageTarget>))]
    public required EquatableArray<HomeCoverageTarget> Targets { get; init; }
}

/// <summary>One Home census target (a colonist building or a stockpile) by its unique load id.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HomeCoverageTarget.Scoped), "scoped")]
[JsonDerivedType(typeof(HomeCoverageTarget.Unscoped), "unscoped")]
public abstract record HomeCoverageTarget(string Id)
{
    /// <summary>A target whose bounded visible geometry was read: its complete geometry.</summary>
    public sealed record Scoped(string Id, HomeExtentGeometry Extent) : HomeCoverageTarget(Id);

    /// <summary>
    /// A target with no bounded visible geometry (a forbidden or burning building, a fogged or empty footprint): it cannot
    /// establish colony territory and is reported, never covered.
    /// </summary>
    public sealed record Unscoped(string Id) : HomeCoverageTarget(Id);
}

/// <summary>
/// A target's complete geometry: a building's enclosed roofed interior and
/// the internal doors connecting it (its occupied footprint excluded), or a stockpile's whole footprint
/// (<see cref="Zone"/>; a building leaves it empty). Colony extent takes every stockpile from the census, not only the
/// zones the autopilot created.
/// </summary>
public sealed record HomeExtentGeometry(
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<Cell>))] EquatableArray<Cell> EnclosedInterior,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<Cell>))] EquatableArray<Cell> Corridor,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<Cell>))] EquatableArray<Cell> Zone);

/// <summary>
/// The lighting census (#3831), read whole: the measured ground glow at every colonist work bench interaction cell
/// plus the colony's glowing fixtures, so the rooms/lighting Need reasons from illumination at the cell a pawn
/// actually stands on rather than from fixture counts. Both lists are sorted by thing id. The native read throws when
/// it cannot read the census.
/// </summary>
public sealed record LightingFacts
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<WorkLightCell>))]
    public required EquatableArray<WorkLightCell> WorkCells { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<LampState>))]
    public required EquatableArray<LampState> Lamps { get; init; }
}

/// <summary>
/// A work bench's interaction cell: its ground glow on the native GlowGrid (0..1; RimWorld treats &gt;= 0.3 as lit),
/// whether it is roofed, and the defs of the plants its proper room grows, standing or set in its growing zones (empty
/// outside a proper room), sorted. The brain reads PlantProperties.diesToLight off each def (cave fungus): lighting
/// such a room would kill the crop, so the cell is never a lighting deficit.
/// </summary>
public sealed record WorkLightCell(string Bench, Cell Cell, double Glow, bool Roofed,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<string>))] EquatableArray<string> PlantDefs);

/// <summary>A glowing fixture: lit only while native currently emits light. Its radius is its def's CompProperties_Glower row.</summary>
public sealed record LampState(string Building, bool Lit);

/// <summary>
/// One cell of a flooring room: the terrain under it and, when a floor blueprint or frame stands on it, the ordered
/// floor's def (null when none is ordered), so the planner never doubles an open order.
/// </summary>
public sealed record FloorCell
{
    public required Cell Cell { get; init; }

    public required string Terrain { get; init; }

    public required string? Pending { get; init; }
}

/// <summary>
/// One proper indoor home room (any cell in the Home area, not psychologically outdoors, off the map edge, unfogged):
/// its native id (Room.ID), its role and its cells sorted by z then x. Room stats live on the rooms census.
/// </summary>
public sealed record FloorRoom
{
    public required string Room { get; init; }

    public required RoomRole Role { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<FloorCell>))]
    public required EquatableArray<FloorCell> Cells { get; init; }
}

/// <summary>
/// The flooring census, read whole: every flooring room. Every terrain a cell can stand on is a catalog TerrainDef row,
/// which carries the stats the rooms/flooring Need scores.
/// </summary>
public sealed record FlooringFacts
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<FloorRoom>))]
    public required EquatableArray<FloorRoom> Rooms { get; init; }
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
}

public sealed record RoutesSection
{
    public RoutesFacts Outcome { get; init; } = null!;
}

/// <summary>
/// The upkeep censuses, read whole: a census native cannot read fails the colony read. Routes stays a section, null
/// when native could not read it (#3782).
/// </summary>
public sealed record UpkeepFacts
{
    public required ComfortFacts Comfort { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<UpkeepItem>))]
    public required EquatableArray<UpkeepItem> Items { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<UpkeepBed>))]
    public required EquatableArray<UpkeepBed> Beds { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<UpkeepStructure>))]
    public required EquatableArray<UpkeepStructure> Structures { get; init; }

    /// <summary>The free colonists.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<UpkeepPerson>))]
    public required EquatableArray<UpkeepPerson> People { get; init; }

    /// <summary>The player animals.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<ColonyAnimal>))]
    public required EquatableArray<ColonyAnimal> Animals { get; init; }

    public required HomeCoverageFacts HomeCoverage { get; init; }

    public required LightingFacts Lighting { get; init; }

    /// <summary>The factionless animals an animals/herd tame write can target; their tame facts ride their pawn table rows.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnId>))]
    public required EquatableArray<PawnId> WildAnimals { get; init; }

    public required FlooringFacts Flooring { get; init; }

    public RoutesSection? Routes { get; init; }

    /// <summary>The colony's slaves: shelter/sleeping gives each a bed set for slaves.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<UpkeepPerson>))]
    public required EquatableArray<UpkeepPerson> Slaves { get; init; }

    /// <summary>
    /// Hosted guests (HostFaction == player, quest lodgers included):
    /// people the colony houses but does not own, in the sleeping census.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<UpkeepPerson>))]
    public required EquatableArray<UpkeepPerson> Guests { get; init; }
}

/// <summary>
/// Threat facts: the colony wealth the storyteller scales raids by
/// and the raid points a default threat incident would draw right now.
/// wealth_* are WealthWatcher's lazy recount (items, buildings, pawns,
/// total); raid_points is StorytellerUtility.DefaultThreatPointsNow for the
/// map, already scaled by the story watcher's adaptation and the
/// difficulty's threat scale. Read-only.
/// </summary>
public sealed record ThreatFacts
{
    public required double WealthItems { get; init; }

    public required double WealthBuildings { get; init; }

    public required double WealthPawns { get; init; }

    public required double WealthTotal { get; init; }

    public required double RaidPoints { get; init; }

    /// <summary>Find.Storyteller.def.defName: the storyteller whose comps schedule the threats (#3539).</summary>
    public required string Storyteller { get; init; }

    /// <summary>GenDate.DaysPassedFloat.</summary>
    public required double DaysPassed { get; init; }

    /// <summary>
    /// The def names of the world's factions that are hostile to the player and have a Combat pawn group maker: the factions a
    /// raid can come from (<c>AllFactionsVisible</c> where <c>HostileTo(Faction.OfPlayer)</c>). The raid composition decodes
    /// their pawn kinds and weapons from the defs.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> HostileFactions { get; init; }

    /// <summary>
    /// The hostile lords seen on the map since load (RaidArrivalState): the defense layout ranks the approaches by where ground raids crossed its census.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<RaidTrack>))]
    public required EquatableArray<RaidTrack> Raids { get; init; }

    /// <summary>What the storyteller's disease comps read of the colony now (#3824); absent when the native read did not run.</summary>
    public DiseaseFacts? Disease { get; init; }
}

/// <summary>
/// The colony facts StorytellerComp_Disease and IncidentWorker_DiseaseHuman read (#3824). Read-only: the storyteller and the
/// map's biome by def name, DebugSettings.enableRandomDiseases, the difficulty's diseaseIntervalFactor,
/// GenDate.DaysPassedSinceSettleFloat, the incident's victim candidates (the map's free colonists and prisoners outside a
/// biosculpter pod) and the free colonists the minPopulation gate counts, and one row per DiseaseHuman incident.
/// </summary>
public sealed record DiseaseFacts
{
    public string Storyteller { get; init; } = "";

    public string Biome { get; init; } = "";

    public bool RandomDiseases { get; init; }

    public double IntervalFactor { get; init; }

    public double DaysSinceSettle { get; init; }

    public int Candidates { get; init; }

    public int FreeColonists { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<DiseaseIncidentRead>))]
    public EquatableArray<DiseaseIncidentRead> Incidents { get; init; }
}

/// <summary>
/// One DiseaseHuman incident on the map now: Potential is the count of its potential victims (IncidentWorker_Disease.PotentialVictims:
/// not in cryptosleep, not a dryad, its development stage, a free affected part, flesh); Blocked names the game-state gates of
/// IncidentWorker.CanFireNow that hold now (<c>scenario</c>: a ScenPart_DisableIncident, <c>difficulty</c>: disabledWhen,
/// <c>planet_layer</c>: the layer lists).
/// </summary>
public sealed record DiseaseIncidentRead(string Incident, int Potential)
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Blocked { get; init; }
}

/// <summary>The threat section of the colony census. A threat read that fails fails the census.</summary>
public sealed record ThreatSection
{
    public required ThreatFacts Observed { get; init; }
}

/// <summary>
/// Current autonomous hauling safety for visible haulable items. count is the
/// stack size; path_length is the shortest observed safe colonist route to the
/// item in cells (present only with safe_to_haul true); storage_headroom is the
/// item units the player's accepting storage can still take for this def;
/// accepted_by, set only on an unforbidden row with storage_headroom 0 and
/// safe_to_haul not false, names the player's storage groups (stockpile zones and
/// storage buildings, by load id) whose settings accept the item, ignoring space.
/// deterioration is the stack's deterioration where it lies (#3798), absent when it
/// cannot deteriorate.
/// </summary>
public sealed record LootItem
{
    public Common.Ref? Item { get; init; }

    public bool? Forbidden { get; init; }

    public bool? SafeToHaul { get; init; }

    public long? Count { get; init; }

    public double? PathLength { get; init; }

    public long? StorageHeadroom { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Ref>))]
    public EquatableArray<Common.Ref> AcceptedBy { get; init; }

    public LootDeterioration? Deterioration { get; init; }
}

/// <summary>
/// A stack's deterioration where it lies (#3798): its MaxHitPoints, SteadyEnvironmentEffects.FinalDeteriorationRate now
/// (every DeteriorationRate part applied at its cell), whether StatPart_EnvironmentalEffects applies
/// (deteriorateFromEnvironmentalEffects), the cell terrain's extraDeteriorationFactor, SteadyEnvironmentEffects.ProtectedByEdifice,
/// whether the cell is roofed and whether its room uses the outdoor temperature (the part's two offsets). Read only for a
/// stack that can deteriorate (useHitPoints and CanEverDeteriorate).
/// </summary>
public sealed record LootDeterioration
{
    public double MaxHitPoints { get; init; }

    public double RatePerDay { get; init; }

    public bool Environmental { get; init; }

    public double TerrainFactor { get; init; }

    public bool Protected { get; init; }

    public bool Roofed { get; init; }

    public bool Outdoors { get; init; }
}

public sealed record LootCensus
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<LootItem>))]
    public EquatableArray<LootItem> Items { get; init; }
}

public sealed record LootSection
{
    public LootCensus Outcome { get; init; } = null!;
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
    public required Cell Cell { get; init; }

    public required bool Reachable { get; init; }

    public required double DistanceSquared { get; init; }
}

public sealed record FishingZoneFacts
{
    public required bool Allowed { get; init; }

    public required bool DoForever { get; init; }

    public required bool HasFishableCells { get; init; }
}

public sealed record FishableRegion
{
    public required Cell Root { get; init; }

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
    public required EquatableArray<FishableCell> Cells { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<FishingZoneFacts>))]
    public required EquatableArray<FishingZoneFacts> Zones { get; init; }

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
    public required double FishingYield { get; init; }

    public required double FishingSpeed { get; init; }
}

public sealed record FishableWater
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<FishableRegion>))]
    public required EquatableArray<FishableRegion> Regions { get; init; }

    public bool? FishingResearched { get; init; }

    public double? ResearchBaseCost { get; init; }

    public double? ResearchProgress { get; init; }

    public double? ResearchCostFactor { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<double>))]
    public required EquatableArray<double> ResearcherSpeeds { get; init; }

    public double? ResearchPointsPerWorkTick { get; init; }

    public double? ResearchDifficultySpeedFactor { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<FisherFacts>))]
    public required EquatableArray<FisherFacts> Fishers { get; init; }

    public double? BaseFishingDurationTicks { get; init; }
}

public sealed record GatherableAnimal
{
    public required string PawnId { get; init; }

    public required string Race { get; init; }

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
    public required string PawnId { get; init; }

    public required string Race { get; init; }

    public bool? CanLayNow { get; init; }

    public double? Progress { get; init; }

    public double? NutritionPerDay { get; init; }

    public double? LeadDays { get; init; }

    public bool? Active { get; init; }

    public bool? Fertilized { get; init; }
}

/// <summary>A player nutrient paste dispenser: its power and the eligible feed nutrition in its hoppers.</summary>
public sealed record PasteDispenser
{
    public required bool Powered { get; init; }

    public required double HopperNutrition { get; init; }
}

/// <summary>A biome wild plant whose harvest product the colony eats.</summary>
public sealed record ForagePlant
{
    public required string DefName { get; init; }
}

public sealed record PenGrazing
{
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
    public required string TerrainDef { get; init; }

    public required double WorstQuadrumPerDay { get; init; }
}

public sealed record FoodSlaughterAnimal
{
    public required string PawnId { get; init; }

    public required string Race { get; init; }

    public double? MeatAmount { get; init; }
}

/// <summary>The food source census, read whole every colony read; FishableWater is null without Odyssey.</summary>
public sealed record FoodChannelsFacts
{
    public required FishableWater? FishableWater { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<GatherableAnimal>))]
    public required EquatableArray<GatherableAnimal> Gatherable { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<EggLayerAnimal>))]
    public required EquatableArray<EggLayerAnimal> EggLayer { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PasteDispenser>))]
    public required EquatableArray<PasteDispenser> PasteDispenser { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ForagePlant>))]
    public required EquatableArray<ForagePlant> Forage { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PenGrazing>))]
    public required EquatableArray<PenGrazing> Grazing { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<FoodSlaughterAnimal>))]
    public required EquatableArray<FoodSlaughterAnimal> Slaughter { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PastureTerrain>))]
    public required EquatableArray<PastureTerrain> PastureTerrain { get; init; }
}

/// <summary>
/// Discovered DeepResourceGrid cells only, merged into eight-connected components
/// of the same definition. Centre is the member nearest the component centroid
/// (ties by x,z); touching discoveries merge and depleted cells leave the lump.
/// </summary>
public sealed record DeepResourceLump
{
    public required string Definition { get; init; }

    public required long Count { get; init; }

    public required Cell Centre { get; init; }

    public required int CellCount { get; init; }
}

/// <summary>
/// One built player mineral scanner (ground-penetrating or long-range). Working means usable and used within the
/// last 30 ticks. TicksToNextFind is the remaining work to the guaranteed find at the last user's speed, excluding
/// idle time and the &lt;=59 tick discovery check interval; random finds may happen sooner.
/// </summary>
public sealed record MineralScanner
{
    public required string Definition { get; init; }

    public required Cell Position { get; init; }

    public required bool Powered { get; init; }

    public required bool Working { get; init; }

    public required long TicksToNextFind { get; init; }
}

/// <summary>The valuable deposit a deep drill mines next: the drill's own lookup over its radius.</summary>
public sealed record DrillDeposit
{
    public required string Resource { get; init; }

    public required long Remaining { get; init; }
}

/// <summary>
/// One spawned player deep drill. Deposit is the next valuable deposit under the drill's radius, null when none
/// remains there (the drill is depleted; vanilla then drills stone only), so a lump centre moving as it is mined
/// never implies depletion. Every colonist drill is the controller's under autonomous play; there is no ownership
/// ledger. Designated reports a standing Deconstruct designation on the drill.
/// </summary>
public sealed record DeepDrill
{
    public required string Id { get; init; }

    public required string Definition { get; init; }

    public required Cell Position { get; init; }

    public required DrillDeposit? Deposit { get; init; }

    public required bool Designated { get; init; }
}

/// <summary>The colony map's discovered deep resource lumps, its built mineral scanners and its deep drills.</summary>
public sealed record DeepResources
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<DeepResourceLump>))]
    public required EquatableArray<DeepResourceLump> Lumps { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<MineralScanner>))]
    public required EquatableArray<MineralScanner> GroundScanners { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<MineralScanner>))]
    public required EquatableArray<MineralScanner> LongRangeScanners { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<DeepDrill>))]
    public required EquatableArray<DeepDrill> Drills { get; init; }
}

/// <summary>
/// One cumulative counter of the player's own production, counted at the production site (a plant harvested, a
/// catch landed, milk, wool or eggs produced), so hauling and stack merges never double-count. Source is the
/// growing zone or plant grower id (crop), the water body's root cell "x,z" (fish), the plant def (forage: a
/// harvested food plant with no zone or grower) or the animal race (animal product); Units are the produced stack
/// counts of Def and Nutrition their human-edible nutrition.
/// </summary>
public sealed record DeliveryCount
{
    public required DeliveryKind Kind { get; init; }

    public required string Source { get; init; }

    public required string Def { get; init; }

    public required long Units { get; init; }

    public required double Nutrition { get; init; }
}

/// <summary>
/// A player pawn killed an animal (a hunt, or a slaughter that leaves a corpse). CorpseId is the corpse's thing id
/// (the food census's stock id), Pawn the killed animal, Race its def, BodySize its body size, PotentialNutrition the
/// meat the corpse would yield (meat amount x meat nutrition; zero for a race without meat) and Tick the tick of death.
/// </summary>
public sealed record Kill
{
    public required string CorpseId { get; init; }

    public required PawnId Pawn { get; init; }

    public required string Race { get; init; }

    public required double BodySize { get; init; }

    public required double PotentialNutrition { get; init; }

    public required Tick Tick { get; init; }
}

/// <summary>
/// A butcher recipe made its products from one corpse, counted where the products are made (any bill, any
/// butcher): MeatUnits and MeatNutrition are the produced meat stack and its nutrition, LeatherUnits the produced
/// leather. A reader links it to its <see cref="Kill"/> by CorpseId.
/// </summary>
public sealed record Butcher
{
    public required string CorpseId { get; init; }

    public required long MeatUnits { get; init; }

    public required double MeatNutrition { get; init; }

    public required long LeatherUnits { get; init; }
}

/// <summary>
/// Native's cumulative production-site delivery counters. Counters only grow within one LoadToken, which changes when
/// the game is loaded or restarted, so a reader diffs successive ledgers of one token and re-baselines on a change.
/// Kills and Butchers hold every record of the token in tick order. A colony facts read that cannot count fails.
/// </summary>
public sealed record DeliveryLedger
{
    public required string LoadToken { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<DeliveryCount>))]
    public required EquatableArray<DeliveryCount> Counts { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Kill>))]
    public required EquatableArray<Kill> Kills { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Butcher>))]
    public required EquatableArray<Butcher> Butchers { get; init; }
}

/// <summary>
/// The colony's eligible indoor sleeping places: the min and max room temperature over them (Indoor), or
/// NoIndoorBed when the colony has no reachable player bed in a roofed, proper indoor room.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SleepingTemperature.Indoor), "indoor")]
[JsonDerivedType(typeof(SleepingTemperature.NoIndoorBed), "no_indoor_bed")]
public abstract record SleepingTemperature
{
    private protected SleepingTemperature() { }

    public sealed record Indoor(double MinC, double MaxC) : SleepingTemperature;

    public sealed record NoIndoorBed : SleepingTemperature;
}

/// <summary>
/// The planning section of a colony facts read: its facts when the read asked for them
/// (<see cref="ColonyFactsRequest.Planning"/>), else NotRequested. A planner that needs them and holds a
/// NotRequested read fails with <c>RequiredFactException("planning")</c>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Planning.Requested), "requested")]
[JsonDerivedType(typeof(Planning.NotRequested), "not_requested")]
public abstract record Planning
{
    private protected Planning() { }

    public sealed record Requested(PlanningFacts Facts) : Planning;

    public sealed record NotRequested : Planning;
}

/// <summary>
/// One colony facts read, all of it from one game-thread frame. A read that cannot produce a member fails whole;
/// the members a later facts family owns keep their section types until it hardens them.
/// </summary>
public sealed record ColonyFactsSnapshot
{
    /// <summary>Exact thing identities contributing to Resources.</summary>
    public required EquatableArray<string> ResourceThings { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<WorkResourcePawn>))]
    public required EquatableArray<WorkResourcePawn> WorkResources { get; init; }
    public required Common.ObservationContext Context { get; init; }

    public required uint ColonistCount { get; init; }

    public required uint WorkerCount { get; init; }

    public required MapSize MapSize { get; init; }

    public required string Biome { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Quantity>))]
    public EquatableArray<Quantity> Resources { get; init; }

    public double FoodNutrition { get; init; }

    public double NutritionPerDay { get; init; }

    public double? FoodRunwayDays { get; init; }

    /// <summary>The human-edible nutrition of every designated harvest and hunt not yet taken.</summary>
    public required double PendingFoodNutrition { get; init; }

    public required uint BedCapacity { get; init; }

    public required uint IndoorSleepingCapacity { get; init; }

    public required SleepingTemperature SleepingTemperature { get; init; }

    public required double OutdoorTemperatureC { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<EnvironmentCondition>))]
    public EquatableArray<EnvironmentCondition> Environment { get; init; }

    public FoodClimate? FoodClimate { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<CookingFacts>))]
    public EquatableArray<CookingFacts> Cooking { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<AcquisitionFacts>))]
    public required EquatableArray<AcquisitionFacts> Acquisition { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ButcheringFacts>))]
    public EquatableArray<ButcheringFacts> Butchering { get; init; }

    public required FoodSupplySection FoodSupply { get; init; }

    public required ForecastSection Forecast { get; init; }

    public required UpkeepFacts Upkeep { get; init; }

    public required DevelopmentSection Development { get; init; }

    public required Planning Planning { get; init; }

    public required RecoverySnapshot Recovery { get; init; }

    public required WasteReply Waste { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ReadIssue>))]
    public EquatableArray<ReadIssue> Issues { get; init; }

    /// <summary>The wild animals designated for hunting.</summary>
    public required uint PendingHunts { get; init; }

    /// <summary>
    /// Player faction TechLevel; the floor of the construction tier.
    /// </summary>
    public required Defs.TechLevel PlayerTechLevel { get; init; }

    /// <summary>
    /// Present only while exactly one force-pausing choice dialog is open.
    /// </summary>
    public ChoiceDialog? Dialog { get; init; }

    /// <summary>
    /// Colony wealth and raid points (ThreatFacts), or why they could not be read.
    /// </summary>
    public required ThreatSection Threat { get; init; }

    public required LootSection EventLoot { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<JoinerLetter>))]
    public EquatableArray<JoinerLetter> JoinerLetters { get; init; }

    public required FoodChannelsFacts FoodChannels { get; init; }

    public required DeepResources DeepResources { get; init; }

    public required uint FermentingBarrels { get; init; }

    /// <summary>
    /// Every outfit, drug, food and reading policy and every allowed area.
    /// </summary>
    public required Policies Policies { get; init; }

    /// <summary>
    /// Biotech pollution, wastepacks, gestators, chargers, gene banks and babies, or inactive without Biotech.
    /// </summary>
    public required BiotechSection Biotech { get; init; }

    /// <summary>
    /// Anomaly incident and monolith state, or inactive without Anomaly.
    /// </summary>
    public required AnomalySection Anomaly { get; init; }

    /// <summary>
    /// Royalty neuroformer stock, bestowing ceremonies and thrones, or inactive without Royalty.
    /// </summary>
    public required RoyaltySection Royalty { get; init; }

    /// <summary>Cumulative production-site delivery counters.</summary>
    public required DeliveryLedger DeliveryLedger { get; init; }

    /// <summary>Raw hunt facts for policy.HuntGate.</summary>
    public required HuntCensus HuntCensus { get; init; }
}

/// <summary>
/// The Anomaly colony section: the colony facts, or <see cref="Inactive"/> without Anomaly. Per-thing state (an
/// entity's hold and study, a platform's containment strength) rides the pawn and building rows; this section carries
/// what no row holds. A failed read fails the colony read.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AnomalySection.Active), "active")]
[JsonDerivedType(typeof(AnomalySection.Inactive), "inactive")]
public abstract record AnomalySection
{
    private protected AnomalySection() { }

    public sealed record Active(AnomalyColonyFacts Facts) : AnomalySection;

    public sealed record Inactive : AnomalySection;
}

/// <summary>GameComponent_Anomaly's level state and the void monolith (null unless it is spawned).</summary>
public sealed record AnomalyColonyFacts
{
    public required AnomalyIncidentState Incidents { get; init; }

    public required MonolithState? Monolith { get; init; }
}

/// <summary>
/// GameComponent_Anomaly: the state that gates the monolith. monolith_spawned is MonolithSpawned, level the
/// monolith's current level number and ambient_horror_mode AmbientHorrorMode (the Anomaly threats run without the
/// monolith).
/// </summary>
public sealed record AnomalyIncidentState
{
    public required bool MonolithSpawned { get; init; }

    public required int Level { get; init; }

    public required bool AmbientHorrorMode { get; init; }
}

/// <summary>
/// The void monolith and the endgame it gates, read only while the monolith is spawned. can_activate is
/// Building_VoidMonolith.CanActivate. next_level_def is GameComponent_Anomaly.NextLevelDef (null when the current
/// level does not advance through activation or none follows); codex_shortfall the entries of the next level's
/// entityCatagoryCompletionRequired still undiscovered (0 when met or when it requires none). blocking_conditions are
/// the active GameConditionDef names on the monolith's map that the next level lists as unreachableDuringConditions
/// (UnnaturalDarkness blocks VoidAwakened). gleaming_interaction_available is CompGleamingMonolith.CanInteract() (true
/// only at the Gleaming level). monolith_id is the monolith's GetUniqueLoadID, the thing a give-job targets.
/// pending_void_structure_ids are the spawned VoidStructures on the monolith's map whose CompInteractable.CanInteract()
/// accepts (not yet activated), sorted; void_node_id is the VoidNode whose CanInteract() accepts (null for none) and
/// void_node_pawn_ids the free colonists spawned on the node's map (the pawn skipped into the pocket map) that can
/// touch it, sorted.
/// </summary>
public sealed record MonolithState
{
    public required string MonolithId { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> PendingVoidStructureIds { get; init; }

    public required string? VoidNodeId { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> VoidNodePawnIds { get; init; }

    public required bool CanActivate { get; init; }

    public required string? NextLevelDef { get; init; }

    public required int CodexShortfall { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> BlockingConditions { get; init; }

    public required bool GleamingInteractionAvailable { get; init; }
}

/// <summary>
/// One policy database row: Policy.GetUniqueLoadID(), label, the player
/// pawns (colonists, prisoners, slaves, tame animals; any map, caravan or
/// transporter) whose current policy it is. allowed_defs is a reading policy's allowed
/// book ThingDefs or a food policy's allowed food ThingDefs (a catalog
/// food definition); empty for the other databases. drug_entries is
/// a drug policy's entries that allow anything (joy, addiction, scheduled
/// use or a carried count); every other drug of it is off.
/// </summary>
public sealed record PolicyEntry
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnId>))]
    public required EquatableArray<PawnId> Pawns { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> AllowedDefs { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Operations.DrugPolicyEntry>))]
    public required EquatableArray<Operations.DrugPolicyEntry> DrugEntries { get; init; }
}

/// <summary>
/// Area_Allowed on the colony map: Area.GetUniqueLoadID(), label, the
/// pawns restricted to it there and the cells it holds (Area.TrueCount).
/// </summary>
public sealed record AllowedArea
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnId>))]
    public required EquatableArray<PawnId> Pawns { get; init; }

    public required int CellCount { get; init; }
}

/// <summary>Every outfit, drug, food and reading policy, every allowed area on the colony map and the food policy holders outside the work census.</summary>
public sealed record Policies
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<PolicyEntry>))]
    public required EquatableArray<PolicyEntry> Outfit { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PolicyEntry>))]
    public required EquatableArray<PolicyEntry> Drug { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PolicyEntry>))]
    public required EquatableArray<PolicyEntry> Food { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PolicyEntry>))]
    public required EquatableArray<PolicyEntry> Reading { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<AllowedArea>))]
    public required EquatableArray<AllowedArea> AllowedAreas { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<FoodEater>))]
    public required EquatableArray<FoodEater> FoodEaters { get; init; }
}

public sealed record FoodEater
{
    public required PawnId Pawn { get; init; }

    public required FoodEaterKind Kind { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> Precepts { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Trait>))]
    public required EquatableArray<Trait> Traits { get; init; }
}

/// <summary>
/// The Biotech colony section: the colony facts, or <see cref="Inactive"/> without Biotech. A failed read fails the
/// colony read.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(BiotechSection.Active), "active")]
[JsonDerivedType(typeof(BiotechSection.Inactive), "inactive")]
public abstract record BiotechSection
{
    private protected BiotechSection() { }

    public sealed record Active(BiotechColonyFacts Facts) : BiotechSection;

    public sealed record Inactive : BiotechSection;
}

/// <summary>
/// Biotech colony facts; every row is a spawned player-owned thing on the colony map, sorted by thing id number.
/// polluted_uncovered_cells counts the PollutionGrid's polluted pollutable cells outside the player's
/// Area_PollutionClear (the cleanup crew's allowed area). wastepacks: spawned stacks of things with CompDissolution,
/// which turn into pollution when they dissolve. atomizers: Building_WastepackAtomizer. gestators, chargers:
/// Building_MechGestator and Building_MechCharger, the producers of wastepacks. gene_banks: buildings with
/// CompGenepackContainer; gene_assemblers: Building_GeneAssembler; genepacks: each Genepack held in a bank or spawned
/// loose. babies: the pawn ids of the player's humanlike pawns whose developmental stage is Newborn or Baby;
/// breastfeeders: the pawn ids ChildcareUtility.CanBreastfeedPlayerPawns lists on the map. Id lists are sorted.
/// </summary>
public sealed record BiotechColonyFacts
{
    public required uint PollutedUncoveredCells { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Wastepack>))]
    public required EquatableArray<Wastepack> Wastepacks { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<WastepackAtomizer>))]
    public required EquatableArray<WastepackAtomizer> Atomizers { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<MechGestatorState>))]
    public required EquatableArray<MechGestatorState> Gestators { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<MechChargerState>))]
    public required EquatableArray<MechChargerState> Chargers { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<GeneBankState>))]
    public required EquatableArray<GeneBankState> GeneBanks { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<GeneAssemblerState>))]
    public required EquatableArray<GeneAssemblerState> GeneAssemblers { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<GenepackState>))]
    public required EquatableArray<GenepackState> Genepacks { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> Babies { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> Breastfeeders { get; init; }
}

/// <summary>
/// A building with CompGenepackContainer: its cell, capacity (CompProperties_GenepackContainer.maxCapacity) and the
/// packs it holds (thing ids, sorted).
/// </summary>
public sealed record GeneBankState
{
    public required Cell Position { get; init; }

    public required int Capacity { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> PackIds { get; init; }
}

/// <summary>A Building_GeneAssembler's cell.</summary>
public sealed record GeneAssemblerState
{
    public required Cell Position { get; init; }
}

/// <summary>A Genepack: bank_id is the bank holding it, null for a pack spawned loose on the map.</summary>
public sealed record GenepackState
{
    public required string? BankId { get; init; }
}

/// <summary>
/// A spawned stack of a thing with CompDissolution: count is its stackCount, frozen CompDissolution.IsFrozen,
/// in_atomizer InAtomizer and forbidden IsForbidden to the player.
/// </summary>
public sealed record Wastepack
{
    public required string ThingId { get; init; }

    public required string DefName { get; init; }

    public required Cell Position { get; init; }

    public required int Count { get; init; }

    public required bool Frozen { get; init; }

    public required bool InAtomizer { get; init; }

    public required bool Forbidden { get; init; }
}

/// <summary>A Building_WastepackAtomizer's cell.</summary>
public sealed record WastepackAtomizer
{
    public required Cell Position { get; init; }
}

/// <summary>
/// A Building_MechGestator: bill_id is its ActiveMechBill (null while none runs) and waste_count the stack its
/// CompWasteProducer holds (0 when none).
/// </summary>
public sealed record MechGestatorState
{
    public required string? BillId { get; init; }

    public required int WasteCount { get; init; }
}

/// <summary>
/// A Building_MechCharger: powered is IsPowered, full_of_waste IsFullOfWaste and charging_mech_id the
/// CurrentlyChargingMech (null while none is attached).
/// </summary>
public sealed record MechChargerState
{
    public required bool Powered { get; init; }

    public required bool FullOfWaste { get; init; }

    public required string? ChargingMechId { get; init; }
}

/// <summary>
/// Current-map WandererJoins offers and creepjoiner offers (creepjoiner true); the token binds letter, quest, pawn and expiry. expires_tick is the letter's disappearAtTick, negative for no timeout.
/// </summary>
public sealed record JoinerLetter
{
    public int? LetterId { get; init; }

    public string? SnapshotToken { get; init; }

    public string? AcceptLabel { get; init; }

    public bool? CanAccept { get; init; }

}

/// <summary>
/// HerdRadius: the cells within which same-race animals count toward a hunt row's herd size.
/// </summary>
public sealed record ColonyFactsRequest
{
    public ReadScope? Scope { get; init; }

    /// <summary>Whether the read carries the planning facts (<see cref="Observations.Planning.Requested"/>); false when omitted.</summary>
    public bool Planning { get; init; }

    public uint HerdRadius { get; init; } = 25;
}

/// <summary>
/// Chebyshev cells from a threat to the nearest free colonist on the map, or <see cref="NoColonist"/> when the map has none.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ColonistDistance.Nearest), "nearest")]
[JsonDerivedType(typeof(ColonistDistance.NoColonist), "no_colonist")]
public abstract record ColonistDistance
{
    private protected ColonistDistance() { }

    public sealed record Nearest(int Cells) : ColonistDistance;

    public sealed record NoColonist : ColonistDistance;
}

/// <summary>A PredatorHunt's target pawn (a corpse's inner pawn): whether it is player-owned or player-held.</summary>
public sealed record ThreatPrey(bool Ours);

/// <summary>
/// Simulation status excludes notifications/UI/clock, exposed by their typed owners.
/// ThreatPawn is one living non-colonist pawn's threat facts: the
/// native emits facts, the brain classifies them (bridge.ClassifyThreat). A row is
/// emitted for a pawn in a mental state, of a faction hostile to the
/// player, breaking out of prison or on a PredatorHunt job, and for
/// an unowned downed pawn or predator within the predator radius
/// of a colonist; any other pawn costs one field read and no row.
/// Pawn references the bundle's pawn table row; Ours is a
/// player-faction pawn; a held prisoner is never FactionHostile.
/// Prey is a PredatorHunt's resolved target; null when the pawn hunts nothing that resolves to a pawn.
/// Passive (on ThreatPawn and ThreatBuilding) is true for
/// faction-hostile insects and hives (dormant, or awake but not engaging)
/// and other CompCanBeDormant hostiles (asleep); a passive threat is left
/// alone, never held for or attacked. Any other pawn has no dormancy and is not passive.
/// MentalState is the pawn's mental state def; null when it is in none.
/// </summary>
public sealed record ThreatPawn
{
    public required Common.Ref Pawn { get; init; }

    public ThreatPrey? Prey { get; init; }

    public required bool Ours { get; init; }

    public required bool Passive { get; init; }

    public required ColonistDistance NearestColonist { get; init; }

    public string? MentalState { get; init; }

    public required bool FactionHostile { get; init; }

    public required bool PrisonBreak { get; init; }

    public required bool PredatorHunt { get; init; }

    public required bool Predator { get; init; }

    public required bool Downed { get; init; }
}

/// <summary>
/// A hostile-faction building that is itself a combat target (an insect hive,
/// a crashed ship part, a mech-cluster piece with combat power): Building is its head (no table holds hostile
/// buildings), Snapshot its own CAS token for Operations.AttackTarget, and
/// NearestColonist is in Chebyshev cells like a threat pawn's;
/// Occupied is the building's occupied rect (inclusive), the cells a
/// ranged defender needs a line of fire to.
/// </summary>
public sealed record ThreatBuilding
{
    public required EntityRef Building { get; init; }

    public required string Snapshot { get; init; }

    public required ColonistDistance NearestColonist { get; init; }

    public required bool Passive { get; init; }

    /// <summary>The building's position cell stands in fog the colony has not explored.</summary>
    public required bool Fogged { get; init; }

    public required Rectangle Occupied { get; init; }
}

public sealed record ThreatsSnapshot
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<ThreatPawn>))]
    public required EquatableArray<ThreatPawn> Pawns { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ThreatBuilding>))]
    public required EquatableArray<ThreatBuilding> HostileBuildings { get; init; }
}

/// <summary>
/// One supported dependent patient and its observed care applicability.
/// </summary>
public sealed record DependentPatientObservation
{
    public required Common.Ref Pawn { get; init; }

    public required DependentPatientGroup Group { get; init; }

    public required bool Rescue { get; init; }

    public required bool Tend { get; init; }
}

/// <summary>The complete dependent-patient census, including an empty set.</summary>
public sealed record DependentPatientCensus
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<DependentPatientObservation>))]
    public required EquatableArray<DependentPatientObservation> Patients { get; init; }
}

/// <summary>
/// The status census: the free colonists, the dependent patients and the threats, each complete. Colonists and
/// threats are pawn-table references. A census that cannot be read whole fails the read.
/// </summary>
public sealed record StatusSnapshot
{
    public required DependentPatientCensus DependentPatients { get; init; }

    public required Common.ObservationContext Context { get; init; }

    public required ThreatsSnapshot Threats { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Ref>))]
    public required EquatableArray<Common.Ref> Colonists { get; init; }
}

public sealed record StatusRequest
{
    public ReadScope? Scope { get; init; }
}

/// <summary>
/// A snapshot stream frame, and the scheduler's step read the controller
/// composes (clock_status is set only there: frames never carry it).
/// </summary>
public sealed record BundleSnapshot
{
    public Common.ObservationContext? Context { get; init; }

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
    /// list_rooms without outdoor rooms or boundary buildings (cells from the cell grid):
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
    /// The whole map as a cell grid, read on the game thread with the rest
    /// of the frame; absent when its read failed.
    /// </summary>
    public Mirror.CellGrid? Grid { get; init; }

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
/// The snapshot stream: native captures whole BundleSnapshot frames and hands
/// the newest to the in-process brain; this request opens it (or replaces its
/// subscription). A frame carries every state family whole (no page caps, no
/// field masks) plus the subscription's resource sources, and the whole map as
/// a cell grid. Clock status stays a dedicated read.
/// </summary>
public sealed record SnapshotStreamRequest
{
    /// <summary>
    /// Main-thread milliseconds one frame may spend measuring route path costs
    /// (FindPathNow per reachable facility x colonist pair, ~0.4 ms each). Pairs
    /// past it report RouteTravel.path_skipped. Absent: measure every pair.
    /// </summary>
    public uint? RoutePathBudgetMs { get; init; }

    /// <summary>
    /// The LinesOfFireRequest budget_ms of each frame's combat lines of fire.
    /// </summary>
    public uint? LinesOfFireBudgetMs { get; init; }
}

/// <summary>
/// The player faction's primary ideoligion: FactionIdeosTracker.PrimaryIdeo.
/// Precept ids are Precept.GetUniqueLoadID(); every def name resolves to a
/// catalog def row (PreceptDef, MemeDef, RitualPatternDef).
/// </summary>
public sealed record IdeologySnapshot
{
    public required string IdeoId { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> Memes { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<IdeoPrecept>))]
    public required EquatableArray<IdeoPrecept> Precepts { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<IdeoRole>))]
    public required EquatableArray<IdeoRole> Roles { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<IdeoRitual>))]
    public required EquatableArray<IdeoRitual> Rituals { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<IdeoBuilding>))]
    public required EquatableArray<IdeoBuilding> Buildings { get; init; }

    /// <summary>Ideo.ColonistBelieverCountCached.</summary>
    public required int Believers { get; init; }

    /// <summary>The free non-slave colonists on the frame's map holding the ideoligion (#3808), by load id.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnId>))]
    public required EquatableArray<PawnId> BelieverPawns { get; init; }

    /// <summary>Whether the ideoligion is fluid and, if so, its reform state.</summary>
    public required IdeoDevelopment Development { get; init; }

    /// <summary>The ThingStyleCategoryDef names of Ideo.thingStyleCategories, sorted.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> StyleCategories { get; init; }

    /// <summary>The ThingDef names of Ideo.VeneratedAnimals, sorted.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> VeneratedAnimals { get; init; }
}

/// <summary>
/// An ideoligion's development: <see cref="Fixed"/> for a non-fluid ideoligion, which never reforms, or
/// <see cref="Fluid"/> with Ideo.development's CanReformNow, Points, reformCount and
/// NextReformationDevelopmentPoints (what the next reform costs).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(IdeoDevelopment.Fixed), "fixed")]
[JsonDerivedType(typeof(IdeoDevelopment.Fluid), "fluid")]
public abstract record IdeoDevelopment
{
    private protected IdeoDevelopment() { }

    public sealed record Fixed : IdeoDevelopment;

    public sealed record Fluid(bool CanReform, int Points, int ReformCount, int NextReformPoints) : IdeoDevelopment;
}

/// <summary>
/// A precept in force, role, ritual and building precepts excluded.
/// </summary>
public sealed record IdeoPrecept
{
    public required string Id { get; init; }

    public required string DefName { get; init; }
}

/// <summary>
/// A role precept: whether it is active (enough believers) and the pawns
/// holding it (Precept_Role.ChosenPawns), by load id.
/// </summary>
public sealed record IdeoRole
{
    public required string Id { get; init; }

    public required string DefName { get; init; }

    public required bool Active { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnId>))]
    public required EquatableArray<PawnId> Pawns { get; init; }

    /// <summary>Whether the role grants the Convert ability (the moral guide's).</summary>
    public required bool Converts { get; init; }

    /// <summary>Whether a holder has a Convert ability that is charged: off cooldown and not already ordered.</summary>
    public required bool ConvertReady { get; init; }
}

/// <summary>
/// A ritual precept: its id and RitualPatternDef (Precept_Ritual.sourcePattern), the
/// raw Precept_Ritual.lastFinishedTick, the count of active obligations,
/// whether the repeat penalty applies and whether a LordJob_Ritual of the
/// precept is running now.
/// </summary>
public sealed record IdeoRitual
{
    public required string Id { get; init; }

    public required string Pattern { get; init; }

    public required int LastFinishedTick { get; init; }

    public required int ActiveObligations { get; init; }

    public required bool RepeatPenaltyActive { get; init; }

    public required bool Running { get; init; }
}

/// <summary>
/// A building precept: the ThingDef it takes and its presence demand (Precept_Building.presenceDemand, #3808) on the
/// frame's map, as the game's own checks read it: Applies is IdeoBuildingPresenceDemand.AppliesTo, Present its
/// BuildingPresent, Satisfied its RequirementsSatisfied (every requirement MetOrDisabled in the effective room of one
/// of the precept's buildings, computed without the demand's caches). The requirements are its roomRequirements by
/// kind: the largest RoomRequirement_Area and RoomRequirement_Impressiveness (0 none), the
/// RoomRequirement_TerrainWithTags tags, the RoomRequirement_ThingCount and RoomRequirement_Thing counts, the
/// RoomRequirement_ForbiddenBuildings building tags, and the class name of any requirement of another kind.
/// </summary>
public sealed record IdeoBuilding
{
    public required string Building { get; init; }

    public required bool Applies { get; init; }

    public required bool Present { get; init; }

    public required bool Satisfied { get; init; }

    public required int MinArea { get; init; }

    public required int MinImpressiveness { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> FloorTags { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<IdeoThingCount>))]
    public required EquatableArray<IdeoThingCount> Things { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> ForbiddenBuildingTags { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> Unread { get; init; }
}

/// <summary>A room requirement's thing count: at least Count of the ThingDef Def in the room.</summary>
public sealed record IdeoThingCount(string Def, int Count);



/// <summary>
/// Anomaly facts of one pawn row. entity is Pawn.IsEntity and mutant Pawn.IsMutant (hostility is PawnState.hostile).
/// min_containment_strength is the pawn's StatDefOf.MinimumContainmentStrength. held is the pawn's
/// CompHoldingPlatformTarget block (null for a pawn without one), study its CompStudiable block (null for a pawn
/// without one) and creepjoiner its creepjoiner tracker block (null for a pawn without one). A failed read fails
/// the pawn read. Threat facts for defense tactics, the combat group: hidden_from_player is
/// InvisibilityUtility.IsHiddenFromPlayer (the player cannot see or target the pawn); psychic_ritual_invoker is a pawn
/// whose role in its lord's psychic ritual is the ritual def's invoker role (the caster); melee_only is a pawn whose
/// CurrentEffectiveVerb is a melee attack and who has no ability its def marks ai_IsOffensive.
/// </summary>
public sealed record PawnAnomaly
{
    public required bool Entity { get; init; }

    public required bool Mutant { get; init; }

    public required double MinContainmentStrength { get; init; }

    public required HeldState? Held { get; init; }

    public required StudyState? Study { get; init; }

    public required bool HiddenFromPlayer { get; init; }

    public required bool PsychicRitualInvoker { get; init; }

    public required bool MeleeOnly { get; init; }

    public required CreepJoinerState? Creepjoiner { get; init; }
}

/// <summary>
/// A creepjoiner's facts. downside_triggered is the tracker's triggeredDownside, true once the game has shown the
/// downside; joined_tick is its public joinedTick, the tick it joined the colony (Notify_ChangedFaction), from which its
/// downside's timers run (#3844). The downside def itself is hidden information and is never read.
/// </summary>
public sealed record CreepJoinerState
{
    public required bool DownsideTriggered { get; init; }

    public required int JoinedTick { get; init; }
}

/// <summary>
/// CompHoldingPlatformTarget: held is CurrentlyHeldOnPlatform; mode the ordered containment
/// mode; escaping the pawn has begun to escape; can_be_captured whether the
/// game lets the player capture it. needs_tend is Pawn.health
/// HasHediffsNeedingTend and bleeding a positive BleedRateTotal: a
/// held entity is tended like any patient or bleeds out. harvester_attached is
/// Building_HoldingPlatform.HasAttachedBioferriteHarvester of the platform holding it (false off a platform: a
/// powered harvester linked to the platform forces extract_bioferrite false) and
/// bioferrite_per_day CompProducesBioferrite.BioferritePerDay: body size
/// times the comp's density, 0 for an entity that produces none and while the
/// BioferriteExtracted hediff stands (8 days after an extraction).
/// </summary>
public sealed record HeldState
{
    public required bool Held { get; init; }

    public required EntityContainmentModeKind Mode { get; init; }

    public required bool Escaping { get; init; }

    public required bool ExtractBioferrite { get; init; }

    public required bool CanBeCaptured { get; init; }

    public required bool NeedsTend { get; init; }

    public required bool Bleeding { get; init; }

    public required bool HarvesterAttached { get; init; }

    public required double BioferritePerDay { get; init; }

    /// <summary>The ContainmentUtility.InitiateEscapeMtbDays inputs (#3811), read with the rest of the block.</summary>
    public required EntityEscapeState Escape { get; init; }
}

/// <summary>
/// An entity's escape facts (#3811), the inputs of ContainmentUtility.InitiateEscapeMtbDays other than the
/// platform's strength: base_mtb_days is the comp's baseEscapeIntervalMtbDays; moving_factor
/// 1 / Clamp(Moving, 0.01, 1); days_in_captivity mindState.entityTicksInCaptivity in days; cold_factor
/// TryGetColdContainmentBonus's modifier where the entity stands (1 without the bonus); combat_power the
/// kind's combatPower; can_escape ContainmentUtility.CanParticipateInEscape.
/// </summary>
public sealed record EntityEscapeState
{
    public required double BaseMtbDays { get; init; }

    public required double MovingFactor { get; init; }

    public required double DaysInCaptivity { get; init; }

    public required double ColdFactor { get; init; }

    public required double CombatPower { get; init; }

    public required bool CanEscape { get; init; }
}

/// <summary>
/// CompStudiable: whether the thing can be studied now and what one study gives.
/// </summary>
public sealed record StudyState
{
    public required bool CurrentlyStudiable { get; init; }

    /// <summary>What one study gives (#3811), read with the rest of the block.</summary>
    public required StudyYieldState Yield { get; init; }
}

/// <summary>
/// A study's yield (#3811): knowledge is CompStudiable.AnomalyKnowledge (before the containment strength
/// curve and the ÷ 5 per study), category its KnowledgeCategory's defName (null for none) and frequency_ticks
/// Props.frequencyTicks, the least interval between studies.
/// </summary>
public sealed record StudyYieldState
{
    public required double Knowledge { get; init; }

    public required string? Category { get; init; }

    public required int FrequencyTicks { get; init; }
}

/// <summary>
/// Anomaly facts of one building row: holder is its CompEntityHolder (a holding platform). A building that is no
/// entity holder, or any building without Anomaly, has no block (BuildingState.anomaly is null).
/// </summary>
public sealed record AnomalyBuilding
{
    public required EntityHolderState Holder { get; init; }
}

/// <summary>
/// CompEntityHolder: containment_strength is the strength the holder's room
/// provides now (CompEntityHolder.ContainmentStrength), available whether it can take
/// a pawn, held_pawn the pawn it holds (null for none). doors are the Building_Door things the
/// game counts for the holder's room (StatWorker_ContainmentStrength
/// CalculateDoorStats: the doors in its boundary; none for a holder in no room or an outdoors one).
/// </summary>
public sealed record EntityHolderState
{
    public required double ContainmentStrength { get; init; }

    public required bool Available { get; init; }

    public required Common.Ref? HeldPawn { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<AnomalyDoor>))]
    public required EquatableArray<AnomalyDoor> Doors { get; init; }
}

/// <summary>
/// One door of a holder's room: its cell, Building_Door.HoldOpen,
/// ContainmentBreached (open for 600 or more ticks, Building_Door
/// TicksOpenToBreach, which zeroes the room's door strength) and
/// BlockedOpenMomentary (a thing or pawn stands in the doorway, or the door is
/// stuck open, so it cannot close).
/// </summary>
public sealed record AnomalyDoor
{
    public required Cell Cell { get; init; }

    public required bool HoldOpen { get; init; }

    public required bool ContainmentBreached { get; init; }

    public required bool BlockedOpen { get; init; }
}

public sealed record DefinitionCatalogRequest
{
    public required ReadScope Scope { get; init; }
}

/// <summary>What the catalog prepare answers: the load's stat table and race facts are ready. A refused or unavailable prepare throws.</summary>
public sealed record DefinitionCatalogAck;

/// <summary>The stream open reply: opened when <see cref="Unavailable"/> is absent.</summary>
public sealed record SnapshotStreamReply
{
    public Common.Unavailable? Unavailable { get; init; }
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
    public ulong? AfterNumber { get; init; }
}

public sealed record SnapshotRead
{
    /// <summary>
    /// the newest captured frame's number, 0 before the first
    /// </summary>
    public ulong Head { get; init; }

    /// <summary>
    /// the applied-write counter now: a frame captured at or past it reflects every write that returned before this read
    /// </summary>
    public long Writes { get; init; }

    /// <summary>
    /// the write counter the returned frame was captured at
    /// </summary>
    public long FrameWrites { get; init; }

    /// <summary>
    /// the newest frame when it is numbered past the request's after_number
    /// </summary>
    public BundleSnapshot? Frame { get; init; }
}

public sealed record SnapshotReadReply
{
    public SnapshotReadReplyOutcome Outcome { get; init; } = null!;
}

/// <summary>The <c>outcome</c> choice of <see cref="SnapshotReadReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SnapshotReadReplyOutcome.ReadCase), "read")]
[JsonDerivedType(typeof(SnapshotReadReplyOutcome.UnavailableCase), "unavailable")]
public abstract record SnapshotReadReplyOutcome
{
    private protected SnapshotReadReplyOutcome() { }

    /// <summary>The <c>Read</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public SnapshotRead? Read => (this as ReadCase)?.Value;

    public sealed record ReadCase(SnapshotRead Value) : SnapshotReadReplyOutcome;

    public static implicit operator SnapshotReadReplyOutcome(SnapshotRead value) => new ReadCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : SnapshotReadReplyOutcome;

    public static implicit operator SnapshotReadReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);
}

/// <summary>
/// Makes the next frame capture if any applied write is uncaptured;
/// the reply does not wait for the capture.
/// </summary>
public sealed record FlushSnapshotRequest;

public sealed record FlushSnapshotReply
{
    public bool Flushed { get; init; }
}

/// <summary>
/// One pawn's current outfit and its wearer inputs: the outfit's label (Name), load id (PolicyId), allowed apparel
/// definitions, hit-point fraction and quality ranges (QualityCategory indices) and whether it excludes tainted
/// apparel; the pawn's stage, slave and violence flags, work settings, draft state and skills; its short name
/// (PawnName), the definitions its royal title, ideoligion role and ideoligion apparel precepts require, and
/// whether it goes nude (nudist trait or mandatory nudity precept).
/// </summary>
public sealed record ApparelPolicyState
{
    public required string Token { get; init; }

    public required string Name { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> AllowedDefs { get; init; }

    public required float MinHitPoints { get; init; }

    public required float MaxHitPoints { get; init; }

    public required int MinQuality { get; init; }

    public required int MaxQuality { get; init; }

    public required bool ExcludesTainted { get; init; }

    public required bool Child { get; init; }

    public required bool Slave { get; init; }

    public required bool IncapableOfViolence { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<WorkSetting>))]
    public required EquatableArray<WorkSetting> Work { get; init; }

    public required bool Drafted { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Skill>))]
    public required EquatableArray<Skill> Skills { get; init; }

    public required string PawnName { get; init; }

    public required string PolicyId { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> RequiredDefs { get; init; }

    public required bool Nude { get; init; }
}
