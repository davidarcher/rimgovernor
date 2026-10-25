using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts;

namespace RimGovernor.Brain.GameFacts.Observations;

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
    ProduceItem,
    HarvestPlant,
    KillAnimals,
    HostLodgers,
    AcceptRequirementUnmet,
    LoadPawns,
    LoadNamedPawns,
    Monument,
    Expiry,
    RescuePawns,
    InspectGravEngine,
    HoldSurveyScanner,
    HackThings,
    GiveItems,
}

[JsonConverter(typeof(WireEnumConverter<WorldSiteState>))]
public enum WorldSiteState
{
    Pending,
    Spawned,
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
/// Each layer counts cell changes of one pawn class and decays on its own half-life; native's
/// counter layer index is the member's value minus one.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<TrafficLayer>))]
public enum TrafficLayer
{
    Colonist = 1,
    /// <summary>
    /// Any pawn stepping from soil or sand onto a built floor: the dirt entry points.
    /// </summary>
    Crossing = 2,
    /// <summary>
    /// Colony animals.
    /// </summary>
    Animal = 3,
    /// <summary>
    /// Visitors and traders.
    /// </summary>
    Visitor = 4,
    /// <summary>
    /// Hostile pawns; slow decay.
    /// </summary>
    Hostile = 5,
}

[JsonConverter(typeof(WireEnumConverter<RouteFacilityKind>))]
public enum RouteFacilityKind
{
    Bed = 1,
    Bench = 2,
    Storage = 3,
    Dining = 4,
    Defense = 5,
    /// <summary>A stockpile zone, its id <c>zone-&lt;id&gt;</c>, at its first standable cell.</summary>
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
    public RimGovernor.Brain.GameFacts.Common.Identity? ExpectedIdentity { get; init; }
}

public sealed record SnapshotRef
{
    public RimGovernor.Brain.GameFacts.Common.ObservationContext? Context { get; init; }

    public string? EntityId { get; init; }

    /// <summary>
    /// Opaque exact entity+settings snapshot CAS token.
    /// </summary>
    public string? Token { get; init; }
}

public sealed record Rectangle
{
    public required RimGovernor.Brain.Domain.Cell Minimum { get; init; }

    public required RimGovernor.Brain.Domain.Cell Maximum { get; init; }
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

public sealed record ClearanceTargetsSnapshot
{
    public required RimGovernor.Brain.GameFacts.Common.ObservationContext Context { get; init; }

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

    public sealed record OnMap(int MapId, [property: JsonRequired] RimGovernor.Brain.Domain.Cell Cell) : EntityPlace;

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
    public RimGovernor.Brain.GameFacts.Common.Ref? Entity => (this as EntityCase)?.Value;

    public sealed record EntityCase(RimGovernor.Brain.GameFacts.Common.Ref Value, EntityPlace Place) : TargetRefTarget;

    public sealed record CellCase([property: JsonRequired] RimGovernor.Brain.Domain.Cell Value) : TargetRefTarget;

    public static implicit operator TargetRefTarget(RimGovernor.Brain.Domain.Cell value) => new CellCase(value);

    public sealed record UnavailableCase(RimGovernor.Brain.GameFacts.Common.Unavailable Value) : TargetRefTarget;

    public static implicit operator TargetRefTarget(RimGovernor.Brain.GameFacts.Common.Unavailable value) => new UnavailableCase(value);
}

public sealed record Quantity
{
    public required string DefName { get; init; }

    public required long Units { get; init; }
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

    public sealed record Has(double Level, RimGovernor.Brain.GameFacts.Defs.HungerCategory Hunger) : FoodNeed;

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

/// <summary>A missing or destroyed part at its common missing ancestor; descendants are implied.</summary>
public sealed record MissingBodyPart
{
    public required BodyPartRef Part { get; init; }
}

/// <summary>One available operation on one part.</summary>
public sealed record SurgeryOperation
{
    /// <summary>The recipe's def name.</summary>
    public required string Recipe { get; init; }

    /// <summary>The part the operation targets; absent for a whole-body recipe.</summary>
    public required BodyPartRef? Part { get; init; }

    public required SurgeryKind Kind { get; init; }

    public required SurgeryChance Chance { get; init; }

    /// <summary>The colonists vanilla would let perform the operation.</summary>
    public required int EligibleDoctors { get; init; }

    public required bool IngredientsOnMap { get; init; }

    /// <summary>The operation is a guest violation: vanilla's goodwill penalty applies.</summary>
    public required bool Violation { get; init; }

    public required bool Lethal { get; init; }

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
    /// BaseMarketValue x count of the medicine the operation would use (the best permitted on the map); 0 when it
    /// uses none or none is stocked, since the operation then spends no medicine value.
    /// </summary>
    public required double MedicineMarketValue { get; init; }

    /// <summary>
    /// True when medicine the recipe accepts is on the map but the patient's medical care level forbids all of it;
    /// ingredients_on_map is then false.
    /// </summary>
    public required bool MedicineCareLimited { get; init; }
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

    public sealed record Graded(RimGovernor.Brain.GameFacts.Defs.QualityCategory Category) : ItemQuality;

    public sealed record Qualityless : ItemQuality;
}

/// <summary>
/// One equipped, worn or stocked item. Its condition is its hit points over their maximum, 1 for a def without hit
/// points (nothing wears it down).
/// </summary>
public sealed record GearItem
{
    public required RimGovernor.Brain.GameFacts.Common.Ref Thing { get; init; }

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
    /// gaps; 0 without a ranged primary or an equipment tracker).
    /// </summary>
    public required double RangedDps { get; init; }

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

    /// <summary>Defense capacity: the pawn's MeleeDPS stat.</summary>
    public required double MeleeDps { get; init; }
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

    public required Passion Passion { get; init; }

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

    /// <summary>
    /// The pawn's value of each work speed stat the catalog maps (<c>DefinitionCatalog.UpkeepStatNames</c>, as the needs
    /// forecast's upkeep reads them: ConstructionSpeed and every recipe's workSpeedStat among them), 0 where
    /// <c>StatWorker.IsDisabledFor</c> the pawn (#3868); none for a pawn without skills.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<WorkSpeedRead>))]
    public required EquatableArray<WorkSpeedRead> WorkSpeeds { get; init; }
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

    /// <summary>The pawn's work settings: whether it ever works, and for one whose tracker is initialized, its priorities.</summary>
    public required PawnWork Work { get; init; }

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
/// A pawn's work settings (Pawn_WorkSettings): <see cref="NeverWorks"/> for a pawn without them or whose EverWork is
/// false; <see cref="Uninitialized"/> for a working pawn whose tracker is not initialized yet (GetPriority would
/// initialize it, so nothing more is read); <see cref="Works"/> with the game's manual-priorities setting and one row
/// per work type WorkSettingsIntent writes.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PawnWork.NeverWorks), "never_works")]
[JsonDerivedType(typeof(PawnWork.Uninitialized), "uninitialized")]
[JsonDerivedType(typeof(PawnWork.Works), "works")]
public abstract record PawnWork
{
    private protected PawnWork() { }

    public sealed record NeverWorks : PawnWork;

    public sealed record Uninitialized : PawnWork;

    public sealed record Works(bool ManualPriorities, [property: JsonConverter(typeof(EquatableArrayJsonConverter<WorkSetting>))] EquatableArray<WorkSetting> Rows) : PawnWork;
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

    public sealed record Configured(RimGovernor.Brain.GameFacts.Operations.MedicalCare MedicalCare, bool SelfTend, RimGovernor.Brain.GameFacts.Operations.HostilityResponse HostilityResponse) : PawnCare;

    public sealed record None : PawnCare;
}

/// <summary>
/// The pawn's body stats the outdoor dash planner and nearby hazard planners read: native values at the read tick
/// (the game's own stat and hediff evaluation, never derived in the brain). ComfortableMinC and ComfortableMaxC
/// are StatDefOf.ComfyTemperatureMin/Max (degrees C, apparel included); MoveSpeed is StatDefOf.MoveSpeed in cells
/// per second, with the current Moving capacity. CellTemperatureC is Thing.AmbientTemperature (the pawn's cell, or
/// its holder's). Hypothermia is the highest Hypothermia hediff severity, 0 when the pawn has none.
/// </summary>
public sealed record PawnBodyStats
{
    public required double ComfortableMinC { get; init; }

    public required double ComfortableMaxC { get; init; }

    public required double MoveSpeed { get; init; }

    public required double CellTemperatureC { get; init; }

    public required double Hypothermia { get; init; }
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

    public sealed record Hosted(RimGovernor.Brain.GameFacts.Defs.GuestStatus Status) : GuestState;

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
/// the husbandry read, every member read for every animal. Training is empty for an animal without a
/// training tracker; release and slaughter are the live designations (false off a map). Bonded (a Bond
/// relation to a living pawn) and Sterilized (the Sterilized hediff) are read for every animal. The care
/// setting, allowed area and conditions are the pawn row's own (<see cref="PawnState.Settings"/>,
/// <see cref="PawnState.Health"/>).
/// </summary>
public sealed record AnimalState
{
    public required RimGovernor.Brain.GameFacts.Defs.Gender Gender { get; init; }

    public required double AgeYears { get; init; }

    /// <summary>
    /// The pregnancy and the game ticks until the birth ((1 - gestation) * gestationPeriodDays * 60000 /
    /// BodyResourceGrowthSpeed).
    /// </summary>
    public required AnimalPregnancy Pregnancy { get; init; }

    /// <summary>The pen holding a rope-managed animal (AnimalPenUtility.GetCurrentPenOf); null when the animal is in none or is no roped live player animal.</summary>
    public required string? PenId { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<TrainingEntry>))]
    public required EquatableArray<TrainingEntry> Training { get; init; }

    public required bool Release { get; init; }

    public required bool Slaughter { get; init; }

    public required bool Tameable { get; init; }

    public required bool Tame { get; init; }

    /// <summary>Designator_ReleaseAnimalToWild's acceptance of a live player animal; false for any other animal.</summary>
    public required bool SafeToRelease { get; init; }

    /// <summary>
    /// Player-animal settings the SetAnimalArea/Master/Following writes target: the master (empty when
    /// unassigned), the follow flags (false without player settings), whether Obedience is learned
    /// (master/follow eligibility) and whether native lets this animal carry an allowed area at all.
    /// </summary>
    public required string MasterId { get; init; }

    public required bool FollowDrafted { get; init; }

    public required bool FollowFieldwork { get; init; }

    public required bool Obedient { get; init; }

    public required bool SupportsAllowedAreas { get; init; }

    /// <summary>
    /// Herd sizing facts: any hediff that makes colonists feel sick, the
    /// player ideo venerates this race (precept effects decide what that
    /// bars), and adult age stage. The race's own
    /// numbers (body size, life expectancy, minimum handling skill, tame-failure
    /// manhunter chance) are the catalog's race rows.
    /// </summary>
    public required bool Sick { get; init; }

    public required bool Venerated { get; init; }

    public required bool Adult { get; init; }

    public required bool Bonded { get; init; }

    /// <summary>
    /// Sterilized hediff present, and a queued sterilize surgery bill on the
    /// animal (the husbandry sterilize order).
    /// </summary>
    public required bool Sterilized { get; init; }

    public required bool SterilizeQueued { get; init; }

    /// <summary>
    /// Raw slaughter facts; policy decides who is protected (FoodSlaughterChoice). downed and
    /// in_mental_state are the live Pawn flags, colonist_bonded is TrainableUtility.GetAllColonistBondsFor
    /// non-empty, slaughter_designatable is Designator_Slaughter's own acceptance of a live player animal
    /// (false while a slaughter designation stands, and for any other animal).
    /// </summary>
    public required bool Downed { get; init; }

    public required bool InMentalState { get; init; }

    public required bool ColonistBonded { get; init; }

    public required bool SlaughterDesignatable { get; init; }

    /// <summary>
    /// The index of the current life stage in the race's lifeStageAges (Pawn_AgeTracker.CurLifeStageIndex) and
    /// the next stage's start. The litter size is rolled at birth and the stage rows are the catalog's, so
    /// neither is sent.
    /// </summary>
    public required int LifeStageIndex { get; init; }

    public required AnimalNextLifeStage NextLifeStage { get; init; }

    /// <summary>
    /// Ids of the living humanlike pawns this animal has a Bond relation with
    /// (any faction or status; the planner keeps those on the colony roster).
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> BondedPawnIds { get; init; }
}

/// <summary>
/// An animal's pregnancy: none, gestating with the game ticks until the birth, or stalled while the
/// body's resource growth speed (PawnUtility.BodyResourceGrowthSpeed) is zero or the animal is dead.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AnimalPregnancy.NotPregnant), "not_pregnant")]
[JsonDerivedType(typeof(AnimalPregnancy.Gestating), "gestating")]
[JsonDerivedType(typeof(AnimalPregnancy.Stalled), "stalled")]
public abstract record AnimalPregnancy
{
    private protected AnimalPregnancy() { }

    public sealed record NotPregnant : AnimalPregnancy;

    public sealed record Gestating(long TicksToBirth) : AnimalPregnancy;

    public sealed record Stalled : AnimalPregnancy;
}

/// <summary>
/// When the animal's next life stage begins: in game ticks ((next minAge in ticks - AgeBiologicalTicks) /
/// BiologicalTicksPerTick), never in the race's last stage, or never while the animal does not age.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AnimalNextLifeStage.InTicks), "in_ticks")]
[JsonDerivedType(typeof(AnimalNextLifeStage.LastStage), "last_stage")]
[JsonDerivedType(typeof(AnimalNextLifeStage.NotAging), "not_aging")]
public abstract record AnimalNextLifeStage
{
    private protected AnimalNextLifeStage() { }

    public sealed record InTicks(long Ticks) : AnimalNextLifeStage;

    public sealed record LastStage : AnimalNextLifeStage;

    public sealed record NotAging : AnimalNextLifeStage;
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
    public required RimGovernor.Brain.GameFacts.Operations.PawnActivityExecution? ActivityExecution { get; init; }

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
    public required RimGovernor.Brain.GameFacts.Common.Ref? OwnedBed { get; init; }

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
    public required bool ConstructionCapable { get; init; }
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
/// Biotech facts of one pawn, read whole: a failed read fails the pawn read. developmental_stage is the
/// DevelopmentalStage (Newborn, Baby, Child, Adult; None for a race with no stages). genes: every gene of the
/// Pawn_GeneTracker (endogenes and xenogenes), empty for a pawn without one. mechanitor is set for a pawn with a
/// Pawn_MechanitorTracker, mech for a mechanoid the player can ever control and deathrest for a pawn with a
/// deathrest gene; each is null for none. Defs are the DefinitionCatalog.biotech rows' names.
/// </summary>
public sealed record PawnBiotech
{
    public required RimGovernor.Brain.GameFacts.Defs.DevelopmentalStage DevelopmentalStage { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnGene>))]
    public required EquatableArray<PawnGene> Genes { get; init; }

    public required PawnMechanitor? Mechanitor { get; init; }

    public required PawnMech? Mech { get; init; }

    public required PawnDeathrest? Deathrest { get; init; }
}

/// <summary>One gene: its GeneDef name and whether it is active (false while another gene overrides it).</summary>
public sealed record PawnGene
{
    public required string DefName { get; init; }

    public required bool Active { get; init; }
}

/// <summary>A mechanitor's bandwidth (used, total, used by gestation), control group count and controlled mechs.</summary>
public sealed record PawnMechanitor
{
    public required int UsedBandwidth { get; init; }

    public required int TotalBandwidth { get; init; }

    public required int UsedBandwidthFromGestation { get; init; }

    public required int ControlGroups { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<RimGovernor.Brain.GameFacts.Common.Ref>))]
    public required EquatableArray<RimGovernor.Brain.GameFacts.Common.Ref> ControlledMechs { get; init; }
}

/// <summary>
/// A controllable mech: its overseer (null without one), MechWorkModeDef (null without one), Need_MechEnergy
/// (<see cref="PawnMechEnergy"/>) and control group (null outside one).
/// </summary>
public sealed record PawnMech
{
    public required RimGovernor.Brain.GameFacts.Common.Ref? Overseer { get; init; }

    public required string? WorkMode { get; init; }

    public required PawnMechEnergy Energy { get; init; }

    public required PawnMechGroup? Group { get; init; }
}

/// <summary>A mech's energy: <see cref="NoEnergyNeed"/> for a mech without Need_MechEnergy, else its level.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PawnMechEnergy.NoEnergyNeed), "no_energy_need")]
[JsonDerivedType(typeof(PawnMechEnergy.Level), "level")]
public abstract record PawnMechEnergy
{
    private protected PawnMechEnergy() { }

    public sealed record NoEnergyNeed : PawnMechEnergy;

    /// <summary>Need_MechEnergy.CurLevelPercentage, 0-1.</summary>
    public sealed record Level : PawnMechEnergy
    {
        public required double Fraction { get; init; }
    }
}

/// <summary>A mech's control group: its index and the group's mechRechargeThresholds band (0-1), within which the game recharges.</summary>
public sealed record PawnMechGroup
{
    public required int Index { get; init; }

    public required double RechargeBelow { get; init; }

    public required double RechargeAbove { get; init; }
}

/// <summary>A deathrester: Need_Deathrest.Deathresting (false without the need) and Gene_Deathrest.DeathrestCapacity.</summary>
public sealed record PawnDeathrest
{
    public required bool Deathresting { get; init; }

    public required int Capacity { get; init; }
}

/// <summary>PawnMembership is what a pawn filter asks of one trait: <see cref="Any"/> pawn, <see cref="Only"/> pawns with the trait, or <see cref="Excluded"/> pawns with it.</summary>
[JsonConverter(typeof(WireEnumConverter<PawnMembership>))]
public enum PawnMembership
{
    [WireName("PAWN_MEMBERSHIP_ANY")]
    Any = 1,
    [WireName("PAWN_MEMBERSHIP_ONLY")]
    Only = 2,
    [WireName("PAWN_MEMBERSHIP_EXCLUDED")]
    Excluded = 3,
}

/// <summary>How near a pawn must stand to a colonist: <see cref="Anywhere"/>, or <see cref="Within"/> a distance in cells.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ColonistRange.Anywhere), "anywhere")]
[JsonDerivedType(typeof(ColonistRange.Within), "within")]
public abstract record ColonistRange
{
    private protected ColonistRange() { }

    public sealed record Anywhere : ColonistRange;

    public sealed record Within : ColonistRange
    {
        public required double Distance { get; init; }
    }
}

/// <summary>Each trait is <see cref="PawnMembership.Any"/> until the request narrows it; the dead are left out unless IncludeDead.</summary>
public sealed record PawnFilter
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Ids { get; init; }

    public string? NameContains { get; init; }

    public PawnMembership Colonist { get; init; } = PawnMembership.Any;

    public PawnMembership Prisoner { get; init; } = PawnMembership.Any;

    public PawnMembership Animal { get; init; } = PawnMembership.Any;

    public PawnMembership Humanlike { get; init; } = PawnMembership.Any;

    public PawnMembership Mechanoid { get; init; } = PawnMembership.Any;

    public PawnMembership Hostile { get; init; } = PawnMembership.Any;

    public PawnMembership Downed { get; init; } = PawnMembership.Any;

    public PawnMembership Drafted { get; init; } = PawnMembership.Any;

    public bool IncludeDead { get; init; }

    public ColonistRange WithinColonistDistance { get; init; } = new ColonistRange.Anywhere();
}

public sealed record PawnSnapshot
{
    public required RimGovernor.Brain.GameFacts.Common.ObservationContext Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnState>))]
    public required EquatableArray<PawnState> Pawns { get; init; }
}

/// <summary>The tend table: one row per requested pawn on the map, keyed by its id, in id order.</summary>
public sealed record TendDoctorSnapshot
{
    public required RimGovernor.Brain.GameFacts.Common.ObservationContext Context { get; init; }

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
/// (<see cref="PackedQuality.Unrated"/> for a building with no quality), and the packed item's market value.
/// </summary>
public sealed record PackedBuilding
{
    public required string Id { get; init; }

    public required string DefName { get; init; }

    public required PackedQuality Quality { get; init; }

    public required double MarketValue { get; init; }
}

/// <summary>A packed building's quality: <see cref="Unrated"/> for a building with none, else <see cref="Rated"/> with its category.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PackedQuality.Unrated), "unrated")]
[JsonDerivedType(typeof(PackedQuality.Rated), "rated")]
public abstract record PackedQuality
{
    private protected PackedQuality() { }

    public sealed record Unrated : PackedQuality;

    public sealed record Rated : PackedQuality
    {
        public required RimGovernor.Brain.GameFacts.Defs.QualityCategory Category { get; init; }
    }
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
    public required RimGovernor.Brain.GameFacts.Common.ObservationContext Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ResourceStock>))]
    public required EquatableArray<ResourceStock> Stocks { get; init; }
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
    public required bool DefaultIngredients { get; init; }

    /// <summary>
    /// No skill or worker-category restriction; worker_id separately records a pinned pawn.
    /// </summary>
    public required bool UnrestrictedWorker { get; init; }

    /// <summary>
    /// Current-load controller bill with unchanged player settings.
    /// </summary>
    public required bool ManagedUnchanged { get; init; }

    public required string Id { get; init; }

    /// <summary>The recipe's def name.</summary>
    public required string Recipe { get; init; }

    /// <summary>The bill's runtime class name (Bill_Production, Bill_GovernorRecipe, ...).</summary>
    public required string BillClass { get; init; }

    /// <summary>Forever, Count or Target: a bench bill is always a Bill_Production in one of vanilla's three modes.</summary>
    public required RimGovernor.Brain.GameFacts.Operations.RepeatMode RepeatMode { get; init; }

    /// <summary>The bill's repeatCount: the iterations left in Count mode (vanilla keeps the field in every mode).</summary>
    public required int RepeatCount { get; init; }

    /// <summary>The bill's targetCount: the stock the bill keeps in Target mode (vanilla keeps the field in every mode).</summary>
    public required int TargetCount { get; init; }

    public required bool Suspended { get; init; }

    public required bool Finished { get; init; }

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

    /// <summary>The unfinished item bound to the bill, if any.</summary>
    public required BillUnfinishedWork UnfinishedWork { get; init; }
}

/// <summary>
/// A bill's bound unfinished item: <see cref="None"/> without one (a bill that makes no unfinished thing, or none bound
/// now), <see cref="Bound"/> with the item and the ingredients embedded in its one initialized iteration (not loose
/// stock or job reservations). Native fails the read on a bound item that is uninitialized, of another recipe, or
/// holds a spawned or empty ingredient: vanilla binds an item only after collecting the whole iteration.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(BillUnfinishedWork.None), "none")]
[JsonDerivedType(typeof(BillUnfinishedWork.Bound), "bound")]
public abstract record BillUnfinishedWork
{
    private protected BillUnfinishedWork() { }

    public sealed record None : BillUnfinishedWork;

    public sealed record Bound : BillUnfinishedWork
    {
        public required string Item { get; init; }

        [JsonConverter(typeof(EquatableArrayJsonConverter<Quantity>))]
        public required EquatableArray<Quantity> Ingredients { get; init; }
    }
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

    /// <summary>The bench's ThingDef, which the catalog's recipes name as their hosts (#3868).</summary>
    public required string BenchDef { get; init; }

    public required bool Usable { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<BillState>))]
    public required EquatableArray<BillState> Bills { get; init; }

    public RimGovernor.Brain.GameFacts.Common.Ref? Room { get; init; }

    public required BenchAutoRefuel AutoRefuel { get; init; }

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

    /// <summary>A humanlike bed's medical and prisoner flags.</summary>
    public sealed record Bed(
        bool Medical,
        bool ForPrisoners) : BuildingControl;

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
    /// A construction blueprint or frame: the definition it builds, the material each cost definition still needs (every cost listed, zero once delivered), and
    /// the native finishing-skill guard's facts: whether the build has quality, the definition's own
    /// constructionSkillPrerequisite, the planner-set minimum (absent when none is set) and the tier gate.
    /// </summary>
    public sealed record Build : ConstructionState
    {
        public required string Definition { get; init; }

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

/// <summary>A building's power, switch, breakdown, fuel and wind state. A building without a flick switch is switched on.</summary>
public sealed record BuildingServiceState
{
    public required BuildingPower Power { get; init; }

    public required bool SwitchedOn { get; init; }

    public required BuildingFuel Fuel { get; init; }

    public required bool BrokenDown { get; init; }

    public required BuildingWind Wind { get; init; }
}

/// <summary>
/// A building's power comp: <see cref="NoPower"/> without a power trader or battery. Network names the power net the
/// building is connected to, absent while it is not connected.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(BuildingPower.NoPower), "no_power")]
[JsonDerivedType(typeof(BuildingPower.Trader), "trader")]
[JsonDerivedType(typeof(BuildingPower.Battery), "battery")]
public abstract record BuildingPower
{
    private protected BuildingPower() { }

    /// <summary>The building has neither a power trader nor a battery comp.</summary>
    public sealed record NoPower : BuildingPower;

    /// <summary>A power trader (CompPowerTrader): whether it is on, its output, and its rated watts with finished upgrades.</summary>
    public sealed record Trader : BuildingPower
    {
        public required bool PowerOn { get; init; }

        public required double OutputW { get; init; }

        /// <summary>Rated output with finished power upgrades applied, negative for consumers.</summary>
        public required double RatedW { get; init; }

        public required string? Network { get; init; }
    }

    /// <summary>A battery (CompPowerBattery): it is on while connected and outputs nothing.</summary>
    public sealed record Battery : BuildingPower
    {
        public required string? Network { get; init; }

        public required double StorageWD { get; init; }
    }
}

/// <summary>A building's refuelable comp: <see cref="NotRefuelable"/> without one.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(BuildingFuel.NotRefuelable), "not_refuelable")]
[JsonDerivedType(typeof(BuildingFuel.Refuelable), "refuelable")]
public abstract record BuildingFuel
{
    private protected BuildingFuel() { }

    public sealed record NotRefuelable : BuildingFuel;

    /// <summary>CompRefuelable's fuel, its target fuel level, and whether it is out of fuel (not HasFuel).</summary>
    public sealed record Refuelable : BuildingFuel
    {
        public required FuelBurn Burn { get; init; }

        public required double Fuel { get; init; }

        public required double TargetFuel { get; init; }

        public required bool OutOfFuel { get; init; }
    }
}

/// <summary>A building's wind plant comp: <see cref="NotWindTurbine"/> on any building without CompPowerPlantWind.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(BuildingWind.NotWindTurbine), "not_wind_turbine")]
[JsonDerivedType(typeof(BuildingWind.WindTurbine), "wind_turbine")]
public abstract record BuildingWind
{
    private protected BuildingWind() { }

    public sealed record NotWindTurbine : BuildingWind;

    /// <summary>
    /// The wind path cells that are off the map, roofed or hold a wind-blocking thing (the cells
    /// CompPowerPlantWind.RecalculateBlockages counts).
    /// </summary>
    public sealed record WindTurbine : BuildingWind
    {
        public required uint BlockedCells { get; init; }
    }
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
    public required bool Roofed { get; init; }

    public required bool RainVulnerable { get; init; }

    public required EquatableArray<string> FuelDefinitions { get; init; }

    public required TurretGun? Turret { get; init; }

    public required ConduitKind Conduit { get; init; }
    public required EntityRef Building { get; init; }

    public required RimGovernor.Brain.GameFacts.Placement.Rotation Rotation { get; init; }

    public required string? Stuff { get; init; }

    public required RimGovernor.Brain.GameFacts.Common.Ref? Faction { get; init; }

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
    public required string Id { get; init; }

    public required double GenerationW { get; init; }

    public required double ConsumptionW { get; init; }

    public required double StoredWD { get; init; }
}

public sealed record BuildingsSnapshot
{
    public required RimGovernor.Brain.GameFacts.Common.ObservationContext Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<BuildingState>))]
    public required EquatableArray<BuildingState> Buildings { get; init; }
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

    [JsonConverter(typeof(EquatableArrayJsonConverter<RimGovernor.Brain.GameFacts.Common.Ref>))]
    public required EquatableArray<RimGovernor.Brain.GameFacts.Common.Ref> Pawns { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<RimGovernor.Brain.GameFacts.Common.Ref>))]
    public required EquatableArray<RimGovernor.Brain.GameFacts.Common.Ref> Beds { get; init; }

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
    public required RimGovernor.Brain.GameFacts.Common.ObservationContext Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<RoomState>))]
    public required EquatableArray<RoomState> Rooms { get; init; }
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
        public required uint ZoneCells { get; init; }

        public required uint UnroofedCells { get; init; }

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
    public required RimGovernor.Brain.GameFacts.Common.ObservationContext Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ZoneState>))]
    public required EquatableArray<ZoneState> Zones { get; init; }
}

/// <summary>
/// A thing's canonical row, from one native builder: the bundle's things
/// table. Identity, forbidden state and corpse classification are set on every row.
/// Food stock evidence belongs to FoodSupplyFacts.Stocks.
/// </summary>
public sealed record Thing
{
    [JsonPropertyName("thing")]
    public required EntityRef Entity { get; init; }

    public required bool Forbidden { get; init; }

    public required bool Corpse { get; init; }

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
    public required RimGovernor.Brain.GameFacts.Common.ObservationContext Context { get; init; }

    public required MapSize MapSize { get; init; }

    public required RimGovernor.Brain.GameFacts.Mirror.CellGrid Grid { get; init; }
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
    public required RimGovernor.Brain.GameFacts.Common.ObservationContext Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ResearchSlot>))]
    public required EquatableArray<ResearchSlot> Slots { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ResearchProject>))]
    public required EquatableArray<ResearchProject> Projects { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> Finished { get; init; }
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
    public required RimGovernor.Brain.GameFacts.Common.ObservationContext Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnAccess>))]
    public required EquatableArray<PawnAccess> Pawns { get; init; }
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
    public required RimGovernor.Brain.GameFacts.Common.ObservationContext Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<DefenseCell>))]
    public required EquatableArray<DefenseCell> Cells { get; init; }
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
    public required RimGovernor.Brain.GameFacts.Common.ObservationContext Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<LineOfFire>))]
    public required EquatableArray<LineOfFire> Lines { get; init; }

    public required bool Skipped { get; init; }
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
    public required RimGovernor.Brain.GameFacts.Common.ObservationContext Context { get; init; }

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
/// One wall-upgrade site: the target wall a stone upgrade replaces (the original itself, or a cleanup's first
/// completed backup), the original wall, its two colonist-wall supports, the outward normal and the backup cells.
/// </summary>
public sealed record WallUpgradeSite
{
    public required RimGovernor.Brain.GameFacts.Common.Ref Target { get; init; }

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

    public required RimGovernor.Brain.GameFacts.Common.Ref Original { get; init; }

    public required RimGovernor.Brain.GameFacts.Common.Ref LeftSupport { get; init; }

    public required RimGovernor.Brain.GameFacts.Common.Ref RightSupport { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<RimGovernor.Brain.GameFacts.Common.Ref>))]
    public required EquatableArray<RimGovernor.Brain.GameFacts.Common.Ref> CompletedBackups { get; init; }

    /// <summary>A cleanup site's completed stone wall; null on a replacement site.</summary>
    public required RimGovernor.Brain.GameFacts.Common.Ref? Replacement { get; init; }

    /// <summary>
    /// The target's CAS token for the upgrade's guarded operations.
    /// </summary>
    public required SnapshotRef TargetSnapshot { get; init; }
}

public sealed record WallUpgradeSnapshot
{
    public required RimGovernor.Brain.GameFacts.Common.ObservationContext Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<WallUpgradeSite>))]
    public required EquatableArray<WallUpgradeSite> Sites { get; init; }
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

/// <summary>
/// One corpse the census counts: the corpse, its inner pawn's class and, for
/// a buried body, the grave holding it (absent while unburied).
/// </summary>
public sealed record WasteItem
{
    public required RimGovernor.Brain.GameFacts.Common.Ref Thing { get; init; }

    public required RimGovernor.Brain.GameFacts.Common.CorpseClass CorpseClass { get; init; }

    public required RimGovernor.Brain.GameFacts.Common.Ref? Grave { get; init; }
}

public sealed record WasteSnapshot
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<WasteItem>))]
    public required EquatableArray<WasteItem> Items { get; init; }
}

/// <summary>A visible blighted plant on growing-zone or home ground.</summary>
public sealed record BlightedPlantState
{
    public required RimGovernor.Brain.GameFacts.Common.Ref Thing { get; init; }

    public required bool Designated { get; init; }
}

/// <summary>The total blight census, independent of the planning window.</summary>
public sealed record BlightSnapshot
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<BlightedPlantState>))]
    public required EquatableArray<BlightedPlantState> Plants { get; init; }
}

/// <summary>One free colonist's player area restriction on the map.</summary>
public sealed record RecoveryRestriction
{
    public required RimGovernor.Brain.GameFacts.Common.Ref Pawn { get; init; }

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

    [JsonConverter(typeof(EquatableArrayJsonConverter<RimGovernor.Brain.GameFacts.Common.Ref>))]
    public required EquatableArray<RimGovernor.Brain.GameFacts.Common.Ref> Buildings { get; init; }
}

/// <summary>
/// One spawned humanlike on the map, joined to its pawn table row by <see cref="Pawn"/>. Admitted is a free colonist
/// of the player faction; Guest is hosted by the player faction (prisoners and slaves included). Recruitable is
/// Pawn_GuestTracker.Recruitable. WildMan is Verse.WildManUtility.IsWildMan. Biography, health and worn apparel
/// are owned by the same frame's joined PawnState.
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

    /// <summary>Whether and how the colony holds the person: a colony prisoner, a colony guest, or neither.</summary>
    public required PersonCustody Custody { get; init; }

    /// <summary>Pawn_MindState.WillJoinColonyIfRescued.</summary>
    public required bool WillJoinIfRescued { get; init; }
}

/// <summary>
/// How the colony holds a person. <see cref="Prisoner"/> is a prisoner of the colony: its resistance, the
/// PrisonerInteractionModeDef it is under, the time held so far (the TimeAsPrisoner record, in ticks: the release
/// path's "held N days with resistance unbroken" clock), its home faction's load id (null without a non-player home faction),
/// and the goodwill change vanilla's Recipe_RemoveBodyPart violation report (-70) would make with it after
/// CalculateAdjustedGoodwillChange and the -100 floor (0 when the faction's goodwill cannot change or there is none).
/// <see cref="Guest"/> is hosted by the colony, neither prisoner nor slave. Medical care, conditions and surgery
/// are read from the joined PawnState. <see cref="None"/> is anyone else (a colonist, a slave, a visitor, a hostile).
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
        RimGovernor.Brain.GameFacts.Common.Ref? Faction,
        int HarvestGoodwillChange) : PersonCustody;

    public sealed record Guest : PersonCustody;

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

/// <summary>A settlement's faction as the player sees it.</summary>
[JsonConverter(typeof(WireEnumConverter<SettlementRelation>))]
public enum SettlementRelation
{
    /// <summary>The player's own settlement.</summary>
    Player,

    Hostile,

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

/// <summary>The settlements within Radius tiles of a world tile (0: that tile only).</summary>
public sealed record SettlementSearch
{
    public required int Tile { get; init; }

    public required double Radius { get; init; }
}

/// <summary>A caravan's route to one destination tile: Reachable with its estimated ticks to arrive, or Unreachable.</summary>
public sealed record WorldRoute
{
    public required int Destination { get; init; }

    public required PackRoute Route { get; init; }
}

/// <summary>Where a caravan is headed: Moving to its destination tile, or Stopped.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CaravanCourse.Moving), "moving")]
[JsonDerivedType(typeof(CaravanCourse.Stopped), "stopped")]
public abstract record CaravanCourse
{
    private protected CaravanCourse() { }

    public sealed record Moving(int Destination) : CaravanCourse;

    public sealed record Stopped : CaravanCourse;
}

/// <summary>
/// One player caravan on the world map. FoodDays and FoodRotDays are the game's days-worth-of-food figures (600, the
/// game's InfiniteDaysWorthOfFood, when nothing eats or nothing rots); HomeRoutes has one route per player home map.
/// </summary>
public sealed record CaravanState
{
    public required EntityRef Caravan { get; init; }

    public required int Tile { get; init; }

    public required CaravanCourse Course { get; init; }

    public required double MassUsage { get; init; }

    public required double MassCapacity { get; init; }

    public required double FoodDays { get; init; }

    public required double FoodRotDays { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnState>))]
    public required EquatableArray<PawnState> Pawns { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<WorldRoute>))]
    public required EquatableArray<WorldRoute> HomeRoutes { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Quantity>))]
    public required EquatableArray<Quantity> Inventory { get; init; }
}

/// <summary>
/// One option of the quest's reward choice, totalled over every reward the option grants. Its position in
/// <see cref="QuestState.Rewards"/> is the option's QuestAccept reward index. Psylink counts bestowed psylink levels and
/// psychic amplifiers; Favor is the option's own royal favor (favor every option grants is <see cref="QuestState.Favor"/>).
/// </summary>
public sealed record QuestReward(
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<QuestRewardItem>))] EquatableArray<QuestRewardItem> Items,
    int Favor,
    int Goodwill,
    int Psylink,
    int PermitPoints,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<string>))] EquatableArray<string> Permits);

/// <summary>An item stack a reward option grants: the exact native def name and stack count.</summary>
public sealed record QuestRewardItem(string DefName, long Units);

/// <summary>
/// One requirement a quest states and its observed progress. Members that only some kinds carry are named per kind:
/// <see cref="Goal"/> (what is counted), <see cref="Timing"/> (when it is due) and <see cref="MinimumMood"/> (hosted
/// lodgers' mood floor).
/// </summary>
public sealed record QuestObjective
{
    public required QuestObjectiveKind Kind { get; init; }

    public required QuestObjectiveGoal Goal { get; init; }

    public required QuestObjectiveTiming Timing { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnId>))]
    public required EquatableArray<PawnId> PawnIds { get; init; }

    /// <summary>
    /// The quest part's enable state; an objective whose part has no enable state is in force while the quest lists it.
    /// </summary>
    public required bool Active { get; init; }

    public QuestMonument? Monument { get; init; }

    public required QuestMoodFloor MinimumMood { get; init; }

    /// <summary>The current mood of every hosted lodger the game tracks a mood for (spawned, alive, with a mood need).</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<QuestLodgerMood>))]
    public required EquatableArray<QuestLodgerMood> LodgerMoods { get; init; }

    public QuestWorkload? Workload { get; init; }

    public QuestGravEngine? GravEngine { get; init; }

    public QuestSurveyScanner? SurveyScanner { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<QuestHackTarget>))]
    public EquatableArray<QuestHackTarget> HackTargets { get; init; }

    public QuestGiftRequest? GiftRequest { get; init; }

    public QuestHackRisk? HackRisk { get; init; }
}

/// <summary>
/// One thing a hack objective names: its load id, CompHackable's LockedOut and Autohack, whether the objective counts
/// it done (hacked, or destroyed where the quest's filter accepts destruction) and where it is.
/// </summary>
public sealed record QuestHackTarget
{
    public required string Id { get; init; }

    public required bool LockedOut { get; init; }

    public required bool Autohack { get; init; }

    public required bool Satisfied { get; init; }

    public required QuestHackTargetPlace Place { get; init; }
}

/// <summary>
/// Where a hack target is: spawned on a map, with the colonists who can hack it now (sorted by load id), or not spawned
/// (still on an unvisited site, carried, or destroyed).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(QuestHackTargetPlace.OnMap), "on_map")]
[JsonDerivedType(typeof(QuestHackTargetPlace.Unspawned), "unspawned")]
public abstract record QuestHackTargetPlace
{
    private protected QuestHackTargetPlace() { }

    public sealed record OnMap(int MapId,
        [property: JsonConverter(typeof(EquatableArrayJsonConverter<PawnId>))] EquatableArray<PawnId> EligibleHackers) : QuestHackTargetPlace;

    public sealed record Unspawned : QuestHackTargetPlace;
}

/// <summary>
/// Whether a gift recipient is asking: not spawned yet (still travelling to the colony), spawned on a map but its lord
/// is not waiting for this item, or waiting for it with the units still owed, the colonists already giving
/// (GiveToPawn jobs of its lord) and the colonists who could give it now (sorted by load id).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(QuestGiftAsk.Away), "away")]
[JsonDerivedType(typeof(QuestGiftAsk.NotAsking), "not_asking")]
[JsonDerivedType(typeof(QuestGiftAsk.Asking), "asking")]
public abstract record QuestGiftAsk
{
    private protected QuestGiftAsk() { }

    public sealed record Away : QuestGiftAsk;

    public sealed record NotAsking : QuestGiftAsk;

    public sealed record Asking(int MapId, long Remaining,
        [property: JsonConverter(typeof(EquatableArrayJsonConverter<PawnId>))] EquatableArray<PawnId> Haulers,
        [property: JsonConverter(typeof(EquatableArrayJsonConverter<PawnId>))] EquatableArray<PawnId> EligibleGivers) : QuestGiftAsk;
}

/// <summary>Whether the faction holding a worshipped terminal's site is hostile to the player.</summary>
public sealed record QuestHackRisk
{
    public required bool Hostile { get; init; }
}

/// <summary>A survey quest's scanner hold at a site: the native hold length, the scanner's condition, the hold clock and native completion.</summary>
public sealed record QuestSurveyScanner
{
    public required string SiteId { get; init; }

    /// <summary>The full hold length the quest asks for (QuestPart_ScannerDurationRemainingAlert.duration).</summary>
    public required long DurationTicks { get; init; }

    public required SurveyScannerCondition Scanner { get; init; }

    public required SurveyHold Hold { get; init; }

    /// <summary>The alert ended with the intact scanner spawned at or after its end tick.</summary>
    public required bool Complete { get; init; }
}

/// <summary>The survey scanner thing the hold alert names.</summary>
[JsonConverter(typeof(WireEnumConverter<SurveyScannerCondition>))]
public enum SurveyScannerCondition
{
    /// <summary>The alert names no scanner thing.</summary>
    Absent,
    Intact,
    Destroyed,
}

/// <summary>The hold clock: <see cref="Running"/> to its end tick while the alert is enabled, otherwise <see cref="Stopped"/>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SurveyHold.Running), "running")]
[JsonDerivedType(typeof(SurveyHold.Stopped), "stopped")]
public abstract record SurveyHold
{
    private protected SurveyHold() { }

    public sealed record Running(Tick EndTick) : SurveyHold;

    public sealed record Stopped : SurveyHold;
}

/// <summary>The one grav engine a MechanoidSignal quest asks the colony to inspect.</summary>
public sealed record QuestGravEngine
{
    public required string EngineId { get; init; }

    /// <summary>The research manager's gravEngineInspected flag.</summary>
    public required bool Inspected { get; init; }

    public required GravEnginePlace Place { get; init; }
}

/// <summary>
/// Where the grav engine is: <see cref="Spawned"/> on a map, with the free colonists there who can reach it and those
/// inspecting it now, or <see cref="Unspawned"/>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(GravEnginePlace.Spawned), "spawned")]
[JsonDerivedType(typeof(GravEnginePlace.Unspawned), "unspawned")]
public abstract record GravEnginePlace
{
    private protected GravEnginePlace() { }

    public sealed record Spawned(int MapId,
        [property: JsonConverter(typeof(EquatableArrayJsonConverter<PawnId>))] EquatableArray<PawnId> EligiblePawnIds,
        [property: JsonConverter(typeof(EquatableArrayJsonConverter<PawnId>))] EquatableArray<PawnId> InspectingPawnIds) : GravEnginePlace;

    public sealed record Unspawned : GravEnginePlace;
}

/// <summary>
/// One world site or quest-anchored world object, read whole: its lifecycle, where it is and its loaded map, the quests
/// that look at it, the surface route from the observed colony map, its security budget and, for peace talks, the
/// goodwill stakes.
/// </summary>
public sealed record WorldSite
{
    public required string Id { get; init; }

    public required WorldSiteState State { get; init; }

    public required SitePlace Place { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<QuestId>))]
    public required EquatableArray<QuestId> QuestIds { get; init; }

    public required SiteRoute Route { get; init; }

    public required SiteThreatBudget Security { get; init; }

    /// <summary>The goodwill stakes of a peace-talks world object; null for every other world object.</summary>
    public QuestPeaceTalks? PeaceTalks { get; init; }
}

/// <summary>
/// Where a world site is: no valid world tile, a planet tile (layer 0 is the root surface) without a map, or a tile
/// whose map is loaded.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SitePlace.Unplaced), "unplaced")]
[JsonDerivedType(typeof(SitePlace.OnTile), "on_tile")]
[JsonDerivedType(typeof(SitePlace.Loaded), "loaded")]
public abstract record SitePlace
{
    private protected SitePlace() { }

    public sealed record Unplaced : SitePlace;

    public sealed record OnTile(int Tile, int Layer) : SitePlace;

    public sealed record Loaded(int Tile, int Layer, SiteMap Map) : SitePlace;
}

/// <summary>A loaded site map: its id, the hostiles left on it, its extraction crew and its quest mining targets.</summary>
public sealed record SiteMap
{
    public required MapId Id { get; init; }

    public required SiteThreat Threat { get; init; }

    public required QuestSiteExtraction Extraction { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<QuestSiteMiningTarget>))]
    public required EquatableArray<QuestSiteMiningTarget> MiningTargets { get; init; }
}

/// <summary>
/// The hostiles on a loaded site map, most pressing first: an active hostile threat, dormant hostiles (sleeping pawns
/// or dormant buildings), hostile traps and complex explosives with no hostile left, or a clear map.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SiteThreat.Active), "active")]
[JsonDerivedType(typeof(SiteThreat.Dormant), "dormant")]
[JsonDerivedType(typeof(SiteThreat.Trapped), "trapped")]
[JsonDerivedType(typeof(SiteThreat.Clear), "clear")]
public abstract record SiteThreat
{
    private protected SiteThreat() { }

    public sealed record Active : SiteThreat;

    public sealed record Dormant : SiteThreat;

    public sealed record Trapped : SiteThreat;

    public sealed record Clear : SiteThreat;
}

/// <summary>
/// The surface caravan route from the observed colony map. Reachable carries the native arrival estimate at full
/// carrying mass for the slowest singleton of <see cref="Reachable.Crew"/>, the eligible adult colonists the bound
/// holds for. The site or the colony is off the root surface, no eligible colonist is on the colony map, or the path
/// finder finds no path (also for a site without a world tile).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SiteRoute.Reachable), "reachable")]
[JsonDerivedType(typeof(SiteRoute.Unreachable), "unreachable")]
[JsonDerivedType(typeof(SiteRoute.OffSurface), "off_surface")]
[JsonDerivedType(typeof(SiteRoute.NoCrew), "no_crew")]
public abstract record SiteRoute
{
    private protected SiteRoute() { }

    public sealed record Reachable(long TravelTicks,
        [property: JsonConverter(typeof(EquatableArrayJsonConverter<PawnId>))] EquatableArray<PawnId> Crew) : SiteRoute;

    public sealed record Unreachable : SiteRoute;

    public sealed record OffSurface : SiteRoute;

    public sealed record NoCrew : SiteRoute;
}

/// <summary>
/// The complete native security bound of a generated site: the initial defenders (dormant ones and resolved complex
/// threats included) and the raid points still to come (timed detection raids, quest raids, sleeping mechs and
/// latent complex triggers). Unbounded when a hidden part, an unsupported generated threat, hostile turrets, an
/// unresolved complex or a raid priced only on arrival leaves the bound open; NotASite for a world object that is not
/// a generated site.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SiteThreatBudget.Bounded), "bounded")]
[JsonDerivedType(typeof(SiteThreatBudget.Unbounded), "unbounded")]
[JsonDerivedType(typeof(SiteThreatBudget.NotASite), "not_a_site")]
public abstract record SiteThreatBudget
{
    private protected SiteThreatBudget() { }

    public sealed record Bounded(double InitialPoints, double PendingRaidPoints) : SiteThreatBudget;

    public sealed record Unbounded : SiteThreatBudget;

    public sealed record NotASite : SiteThreatBudget;
}

/// <summary>A visible, unmined quest mineable on the site map and whether a mine designation is on it.</summary>
public sealed record QuestSiteMiningTarget
{
    public required string Id { get; init; }

    public required string Def { get; init; }

    public required Cell Cell { get; init; }

    public required bool Designated { get; init; }
}

/// <summary>Who can leave a loaded site map: no able free colonist, or the crew with what they would carry home.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(QuestSiteExtraction.NoCrew), "no_crew")]
[JsonDerivedType(typeof(QuestSiteExtraction.Crew), "crew")]
public abstract record QuestSiteExtraction
{
    private protected QuestSiteExtraction() { }

    public sealed record NoCrew : QuestSiteExtraction;

    public sealed record Crew(ExtractionCrew Value) : QuestSiteExtraction;
}

/// <summary>
/// The able free colonists on a site map (never empty) and the vanilla reform dialog's view of them: whether reform is
/// allowed now, carry capacity and the mass already carried, the inventory's food days (capped by rot), the crew's
/// nutrition per day, the reachable cargo, the routes to every player home on the same layer and the exit cells every
/// crew member can reach, nearest first.
/// </summary>
public sealed record ExtractionCrew
{
    public required bool CanReform { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnId>))]
    public required EquatableArray<PawnId> Crew { get; init; }

    public required double CarryCapacity { get; init; }

    public required double CarriedMass { get; init; }

    public required double InventoryFoodDays { get; init; }

    public required double CrewNutritionPerDay { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<QuestSiteCargo>))]
    public required EquatableArray<QuestSiteCargo> Cargo { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<QuestSiteHomeRoute>))]
    public required EquatableArray<QuestSiteHomeRoute> HomeRoutes { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Cell>))]
    public required EquatableArray<Cell> ExitCells { get; init; }
}

/// <summary>
/// One item stack the crew could carry home. Nutrition is what one unit feeds the hungriest crew member when every
/// crew member eats it and it keeps on the road; 0 for anything else.
/// </summary>
public sealed record QuestSiteCargo
{
    public required string Id { get; init; }

    public required string Def { get; init; }

    public required long Count { get; init; }

    public required double UnitMass { get; init; }

    public required double MarketValue { get; init; }

    public required double Nutrition { get; init; }

    public required bool Held { get; init; }
}

/// <summary>A player home map on the site's layer and the caravan route to it.</summary>
public sealed record QuestSiteHomeRoute
{
    public required MapId Map { get; init; }

    public required int Tile { get; init; }

    public required SiteTravel Travel { get; init; }
}

/// <summary>A world route: the native arrival estimate for the crew's slowest member, or no path.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SiteTravel.Reachable), "reachable")]
[JsonDerivedType(typeof(SiteTravel.Unreachable), "unreachable")]
public abstract record SiteTravel
{
    private protected SiteTravel() { }

    public sealed record Reachable(long TravelTicks) : SiteTravel;

    public sealed record Unreachable : SiteTravel;
}

/// <summary>
/// A peace talks site's goodwill stakes: the loss includes vanilla disaster's forced-hostility adjustment, the gain is
/// capped by the faction goodwill limit. Both are observations, not tuning.
/// </summary>
public sealed record QuestPeaceTalks
{
    public required int WorstGoodwillLoss { get; init; }

    public required int BestGoodwillGain { get; init; }

    public required bool IdeologyActive { get; init; }
}

/// <summary>
/// The work an unfinished produce or harvest objective still needs: the work amount, the stat that speeds it and
/// the bench's speed factor.
/// </summary>
public sealed record QuestWorkload
{
    /// <summary>The bill recipe that makes the item; null for a harvest, which no bill hosts.</summary>
    public required string? Recipe { get; init; }

    /// <summary>The work speed stat; null when the recipe names none, so every worker works at rate 1.</summary>
    public required string? Stat { get; init; }

    public required double Work { get; init; }

    /// <summary>The bench's work table speed; 1 for a harvest or a recipe without one.</summary>
    public required double RateFactor { get; init; }
}

public sealed record QuestLodgerMood(double Mood);

/// <summary>
/// What a quest objective counts: <see cref="Things"/> made, harvested or killed of one def (Stuff null when any stuff
/// counts), <see cref="Pawns"/> to load or host, or <see cref="Uncounted"/> for a kind that counts nothing.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(QuestObjectiveGoal.Things), "things")]
[JsonDerivedType(typeof(QuestObjectiveGoal.Pawns), "pawns")]
[JsonDerivedType(typeof(QuestObjectiveGoal.Uncounted), "uncounted")]
public abstract record QuestObjectiveGoal
{
    private protected QuestObjectiveGoal() { }

    public sealed record Things(string Def, string? Stuff, long Count, long Done) : QuestObjectiveGoal;

    public sealed record Pawns(long Count) : QuestObjectiveGoal;

    public sealed record Uncounted : QuestObjectiveGoal;
}

/// <summary>
/// When a quest objective is due: by an absolute <see cref="Deadline"/> tick, within a <see cref="Duration"/> that starts
/// at acceptance (never an offer expiry), or <see cref="Untimed"/>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(QuestObjectiveTiming.Deadline), "deadline")]
[JsonDerivedType(typeof(QuestObjectiveTiming.Duration), "duration")]
[JsonDerivedType(typeof(QuestObjectiveTiming.Untimed), "untimed")]
public abstract record QuestObjectiveTiming
{
    private protected QuestObjectiveTiming() { }

    public sealed record Deadline(long Tick) : QuestObjectiveTiming;

    public sealed record Duration(long Ticks) : QuestObjectiveTiming;

    public sealed record Untimed : QuestObjectiveTiming;
}

/// <summary>The mood hosted lodgers must stay above (the highest QuestPart_MoodBelow threshold), or <see cref="NoFloor"/>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(QuestMoodFloor.Floor), "floor")]
[JsonDerivedType(typeof(QuestMoodFloor.NoFloor), "no_floor")]
public abstract record QuestMoodFloor
{
    private protected QuestMoodFloor() { }

    public sealed record Floor(double Mood) : QuestMoodFloor;

    public sealed record NoFloor : QuestMoodFloor;
}

/// <summary>
/// A monument quest's marker: its load id, def and map, where it stands (<see cref="QuestMonumentState"/>), the sketch
/// pieces, the loose unstored material on the map with the haulers who could store it, and the reachable unforbidden
/// material the colony could build it from.
/// </summary>
public sealed record QuestMonument
{
    public required string MarkerId { get; init; }

    public required string DefName { get; init; }

    public required int MapId { get; init; }

    public required QuestMonumentState State { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<QuestMonumentPiece>))]
    public required EquatableArray<QuestMonumentPiece> Pieces { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<QuestMonumentResource>))]
    public required EquatableArray<QuestMonumentResource> Resources { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<QuestMonumentMaterial>))]
    public required EquatableArray<QuestMonumentMaterial> AvailableResources { get; init; }
}

/// <summary>
/// Where a monument marker stands. Offered: the quest is not accepted and the marker waits in its drop pods, with the
/// material the pods supply. Packed: minified on the map. Both carry the legal install cells (at most 32, nearest the map
/// centre first) checked against the whole sketch footprint; none means no clear site. Installed: placed at
/// <see cref="Installed.Cell"/>, whether every sketch piece is built, and the first building standing where the sketch
/// disallows one (null when none).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(QuestMonumentState.Offered), "offered")]
[JsonDerivedType(typeof(QuestMonumentState.Packed), "packed")]
[JsonDerivedType(typeof(QuestMonumentState.Installed), "installed")]
public abstract record QuestMonumentState
{
    private protected QuestMonumentState() { }

    public sealed record Offered(
        [property: JsonConverter(typeof(EquatableArrayJsonConverter<Cell>))] EquatableArray<Cell> InstallCells,
        [property: JsonConverter(typeof(EquatableArrayJsonConverter<QuestMonumentMaterial>))] EquatableArray<QuestMonumentMaterial> Supplied) : QuestMonumentState;

    public sealed record Packed(
        [property: JsonConverter(typeof(EquatableArrayJsonConverter<Cell>))] EquatableArray<Cell> InstallCells) : QuestMonumentState;

    public sealed record Installed(Cell Cell, bool AllDone, string? DisallowedBuildingId) : QuestMonumentState;
}

/// <summary>
/// One sketch buildable of a monument: its def, offset and rotation in the sketch, the sketch cells it covers, its
/// progress once the marker is installed, and one build option per stuff it can be made of (the chosen stuff, every
/// allowed stuff when the sketch leaves it open, or one option without stuff for a def not made from stuff).
/// </summary>
public sealed record QuestMonumentPiece
{
    public required string DefName { get; init; }

    public required Cell Offset { get; init; }

    public required Rotation Rotation { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Cell>))]
    public required EquatableArray<Cell> Footprint { get; init; }

    public required QuestMonumentPieceSite Site { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<QuestMonumentBuildOption>))]
    public required EquatableArray<QuestMonumentBuildOption> Options { get; init; }
}

/// <summary>
/// A piece's progress: unplaced while the marker is not installed; once placed, whether the exact piece is built,
/// whether it is built or queued as a blueprint or frame, and whether a blueprint for it may be placed now.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(QuestMonumentPieceSite.Unplaced), "unplaced")]
[JsonDerivedType(typeof(QuestMonumentPieceSite.Placed), "placed")]
public abstract record QuestMonumentPieceSite
{
    private protected QuestMonumentPieceSite() { }

    public sealed record Unplaced : QuestMonumentPieceSite;

    public sealed record Placed(bool Built, bool Queued, bool Allowed) : QuestMonumentPieceSite;
}

/// <summary>One way to build a piece: the stuff (null for a def not made from stuff), its adjusted cost list and WorkToBuild.</summary>
public sealed record QuestMonumentBuildOption
{
    public required string? Stuff { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<QuestMonumentMaterial>))]
    public required EquatableArray<QuestMonumentMaterial> Costs { get; init; }

    public required double Work { get; init; }
}

/// <summary>Units of one thing def.</summary>
public sealed record QuestMonumentMaterial(string DefName, long Units);

/// <summary>One free colonist spawned on the map as quest staffing reads it.</summary>
public sealed record QuestWorker
{
    public required PawnId PawnId { get; init; }

    /// <summary>CompShuttle.PawnIsHealthyEnoughForShuttle and adulthood (the shuttle's acceptChildren=false).</summary>
    public required bool HealthyAdult { get; init; }

    public required bool CanFight { get; init; }

    /// <summary>Every recipe work speed stat plus ConstructionSpeed and PlantWorkSpeed; 0 for a stat disabled for the pawn.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<QuestWorkRate>))]
    public required EquatableArray<QuestWorkRate> Rates { get; init; }

    public required double CarryCapacity { get; init; }

    public required double CarriedMass { get; init; }

    public required bool FactionLeader { get; init; }

    public required QuestNegotiation Negotiation { get; init; }
}

/// <summary>
/// A colonist's diplomacy: the NegotiationAbility stat and Social skill level, or Incapable when the stat is disabled
/// for the pawn.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(QuestNegotiation.Able), "able")]
[JsonDerivedType(typeof(QuestNegotiation.Incapable), "incapable")]
public abstract record QuestNegotiation
{
    private protected QuestNegotiation() { }

    public sealed record Able(double Ability, int Social) : QuestNegotiation;

    public sealed record Incapable : QuestNegotiation;
}

public sealed record QuestWorkRate
{
    public required string Stat { get; init; }

    public required double Rate { get; init; }
}

/// <summary>One loose unstored stack of monument material: its load id, def, cell and the haulers who could store it.</summary>
public sealed record QuestMonumentResource
{
    public required string Id { get; init; }

    public required string DefName { get; init; }

    public required Cell Cell { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnId>))]
    public required EquatableArray<PawnId> EligibleHaulers { get; init; }
}

/// <summary>One shuttle a quest reserves: its loading gizmos and state, and the pawns aboard and still to board.</summary>
public sealed record QuestShuttleState
{
    /// <summary>Vanilla offers an enabled autoload toggle.</summary>
    public required bool AutoloadAvailable { get; init; }

    public required bool Autoload { get; init; }

    /// <summary>Transporter.LoadingInProgressOrReadyToLaunch: boarding orders already exist.</summary>
    public required bool Loading { get; init; }

    public required bool AllRequiredLoaded { get; init; }

    /// <summary>Loaded, launchable and offering an enabled send command.</summary>
    public required bool ManualLaunchAvailable { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnId>))]
    public required EquatableArray<PawnId> LoadedPawnIds { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnId>))]
    public required EquatableArray<PawnId> PendingPawnIds { get; init; }
}

/// <summary>
/// One visible quest, read whole. Rewards lists the reward choice's options in the game's order (empty without a
/// choice part); Favor is the royal favor the quest grants whatever option is taken.
/// </summary>
public sealed record QuestState
{
    public required QuestId Id { get; init; }

    public required RimGovernor.Brain.GameFacts.Receipts.QuestStatus State { get; init; }

    public required QuestAcceptance Acceptance { get; init; }

    public required QuestExpiry Expiry { get; init; }

    public required bool RequiresAccepter { get; init; }

    public required bool CanAccept { get; init; }

    /// <summary>The spawned free colonists the game lets accept the quest.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnId>))]
    public required EquatableArray<PawnId> EligiblePawns { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<QuestReward>))]
    public required EquatableArray<QuestReward> Rewards { get; init; }

    public required int Favor { get; init; }

    /// <summary>The root QuestScriptDef's defName.</summary>
    public required string ScriptDef { get; init; }

    public required QuestFaction Faction { get; init; }

    public required QuestPlace Place { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<QuestObjective>))]
    public required EquatableArray<QuestObjective> Objectives { get; init; }

    public required int ChoicePartCount { get; init; }

    public required DecreeAsker Asker { get; init; }

    /// <summary>The storyteller difficulty's allowViolentQuests.</summary>
    public required bool ViolentQuestsAllowed { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<QuestShuttleState>))]
    public required EquatableArray<QuestShuttleState> Shuttles { get; init; }

    public required QuestThreat Threat { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnId>))]
    public required EquatableArray<PawnId> DeparturePawnIds { get; init; }
}

/// <summary>Whether the quest was accepted, and at which game tick.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(QuestAcceptance.Accepted), "accepted")]
[JsonDerivedType(typeof(QuestAcceptance.NotAccepted), "not_accepted")]
public abstract record QuestAcceptance
{
    private protected QuestAcceptance() { }

    public sealed record Accepted(long Tick) : QuestAcceptance;

    public sealed record NotAccepted : QuestAcceptance;
}

/// <summary>The ticks left before an unaccepted offer expires, or <see cref="NoExpiry"/> for an offer that never does.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(QuestExpiry.ExpiresIn), "expires_in")]
[JsonDerivedType(typeof(QuestExpiry.NoExpiry), "no_expiry")]
public abstract record QuestExpiry
{
    private protected QuestExpiry() { }

    public sealed record ExpiresIn(long Ticks) : QuestExpiry;

    public sealed record NoExpiry : QuestExpiry;
}

/// <summary>
/// The first map any look target of the quest sits on (a map parent's own map counts), or <see cref="OffMap"/> for a
/// quest anchored only to world objects.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(QuestPlace.OnMap), "on_map")]
[JsonDerivedType(typeof(QuestPlace.OffMap), "off_map")]
public abstract record QuestPlace
{
    private protected QuestPlace() { }

    public sealed record OnMap(int MapId) : QuestPlace;

    public sealed record OffMap : QuestPlace;
}

/// <summary>
/// The first non-player faction the quest involves and whether it is hostile to the player, or <see cref="NoFaction"/>
/// for a quest that involves none.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(QuestFaction.Involved), "involved")]
[JsonDerivedType(typeof(QuestFaction.NoFaction), "no_faction")]
public abstract record QuestFaction
{
    private protected QuestFaction() { }

    public sealed record Involved(bool Hostile) : QuestFaction;

    public sealed record NoFaction : QuestFaction;
}

/// <summary>
/// The threat a quest generates: the summed points of its big and small threat incidents plus the combat power of its
/// hostile arrivals, or <see cref="NoThreat"/> when it carries neither.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(QuestThreat.Points), "points")]
[JsonDerivedType(typeof(QuestThreat.NoThreat), "no_threat")]
public abstract record QuestThreat
{
    private protected QuestThreat() { }

    public sealed record Points(double Value) : QuestThreat;

    public sealed record NoThreat : QuestThreat;
}

/// <summary>Whose decree a quest is: the faction of the one pawn carrying its DecreeUnmet thought.</summary>
[JsonConverter(typeof(WireEnumConverter<DecreeAsker>))]
public enum DecreeAsker
{
    /// <summary>The quest root is not a Decree_ script.</summary>
    NotDecree,
    PlayerFaction,
    OtherFaction,
    /// <summary>A decree whose DecreeUnmet thought sits on no single pawn with a faction.</summary>
    NoSingleAsker,
}

/// <summary>One loaded map: its free colonists as whole pawn rows, and its quest workers.</summary>
public sealed record WorldMap
{
    public required int Id { get; init; }

    public required int Tile { get; init; }

    public required bool Home { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnState>))]
    public required EquatableArray<PawnState> Pawns { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<QuestWorker>))]
    public required EquatableArray<QuestWorker> QuestWorkers { get; init; }
}

/// <summary>A caravan forming on a map: the form-and-send lord and the pawns it owns.</summary>
public sealed record CaravanAssembly
{
    public required int MapId { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnId>))]
    public required EquatableArray<PawnId> Pawns { get; init; }
}

public sealed record WorldProgressionSnapshot
{
    public required RimGovernor.Brain.GameFacts.Common.ObservationContext Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<WorldMap>))]
    public required EquatableArray<WorldMap> Maps { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<CaravanState>))]
    public required EquatableArray<CaravanState> Caravans { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<CaravanAssembly>))]
    public required EquatableArray<CaravanAssembly> Assemblies { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<QuestState>))]
    public required EquatableArray<QuestState> Quests { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<WorldSite>))]
    public required EquatableArray<WorldSite> Sites { get; init; }
}

/// <summary>
/// Mixed legacy tools get separate read requests: no action/dryRun write branch.
/// </summary>
public sealed record BillsSnapshot
{
    public required RimGovernor.Brain.GameFacts.Common.ObservationContext Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<BillStackRow>))]
    public required EquatableArray<BillStackRow> Benches { get; init; }
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

    [JsonConverter(typeof(EquatableArrayJsonConverter<GearCandidate>))]
    public required EquatableArray<GearCandidate> Candidates { get; init; }

    public required bool Blocked { get; init; }

    public required ApparelPolicyState? ApparelPolicy { get; init; }

    public required GearLoadoutModel LoadoutModel { get; init; }

    public required RimGovernor.Brain.GameFacts.Defs.Gender Gender { get; init; }

    public required RimGovernor.Brain.GameFacts.Defs.DevelopmentalStage DevelopmentalStage { get; init; }

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
    Worn,

    Loose,

    Stored,

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
    /// <summary>Exact current browse participant, approach location and already-held priced stock. Null when no legal participant exists.</summary>
    public TradeVisit? Visit { get; init; }
    public required string Id { get; init; }

    public required bool Orbital { get; init; }

    public required string Kind { get; init; }

    public required string FactionId { get; init; }

    public required bool CanTrade { get; init; }

    public required bool Travelling { get; init; }

    public required int GoodsStacks { get; init; }
}

public sealed record TradeVisit(PawnId Negotiator, Cell Location,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<SettlementStockItem>))] EquatableArray<SettlementStockItem> Stock);

/// <summary>
/// The map's traders, in id order, and the colonists eligible to negotiate (not dead, downed, in a mental state or
/// social-incapable), best trade price improvement first.
/// </summary>
public sealed record TradersSnapshot
{
    public required RimGovernor.Brain.GameFacts.Common.ObservationContext Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Trader>))]
    public required EquatableArray<Trader> Traders { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnId>))]
    public required EquatableArray<PawnId> Negotiators { get; init; }
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

    public required RimGovernor.Brain.GameFacts.Defs.Gender Gender { get; init; }

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

    public sealed record Tracked(RimGovernor.Brain.GameFacts.Defs.GuestStatus Status, bool PrisonerSecure) : TradePawnGuest;

    public sealed record NoGuestTracker : TradePawnGuest;
}

/// <summary>The adapter's one live trade: Idle, or a negotiator engaged with a trader, walking to open (Open false) or holding the open session.</summary>
public sealed record TradeSession
{
    public required RimGovernor.Brain.GameFacts.Common.ObservationContext Context { get; init; }

    public required TradeEngagement Engagement { get; init; }
}

/// <summary>Whether a negotiator is walking to or trading with a trader.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TradeEngagement.Engaged), "engaged")]
[JsonDerivedType(typeof(TradeEngagement.Idle), "idle")]
public abstract record TradeEngagement
{
    private protected TradeEngagement() { }

    public sealed record Engaged(RimGovernor.Brain.GameFacts.Common.TradeTarget Target, PawnId Negotiator, bool Open) : TradeEngagement;

    public sealed record Idle : TradeEngagement;
}

/// <summary>
/// The open session's whole sheet. CurrencyKind is the session's currency (vanilla TradeCurrency): silver, or the
/// trader faction's royal favor (the Royalty tribute collector), where every line's SellPrice is its favor value and
/// the currency line's TransferCount the favor the negotiator gains. DealSignature is the token AcceptTrade carries.
/// </summary>
public sealed record TradeSheet
{
    public required RimGovernor.Brain.GameFacts.Common.ObservationContext Context { get; init; }

    public required string SessionId { get; init; }

    public required RimGovernor.Brain.GameFacts.Common.TradeTarget Target { get; init; }

    public required PawnId Negotiator { get; init; }

    public required bool GiftMode { get; init; }

    public required bool CanTradeNow { get; init; }

    public required TradeBalance Balance { get; init; }

    public required bool TraderHasEnoughSilver { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<TradeLine>))]
    public required EquatableArray<TradeLine> Lines { get; init; }

    public required string DealSignature { get; init; }

    public required RimGovernor.Brain.GameFacts.Defs.TradeCurrency CurrencyKind { get; init; }
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
    public string ConsoleId { get; init; } = "";
    public Cell ConsoleCell { get; init; }
    public required string FactionId { get; init; }

    public required RimGovernor.Brain.GameFacts.Common.TradeRequestKind Kind { get; init; }

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
    public required RimGovernor.Brain.GameFacts.Common.ObservationContext Context { get; init; }

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

    public required RimGovernor.Brain.GameFacts.Common.TradeRequestKind Kind { get; init; }

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

    public sealed record Packed(double MassUsage, double MassCapacity, double FoodDays, double FoodRotDays, PackRoute Outbound, PackRoute Home) : TradePackEstimate
    {
        /// <summary>Null for a destination without a settlement trader, including returning home.</summary>
        public SettlementTradePreview? SettlementTrade { get; init; }
    }

    public sealed record Unpackable : TradePackEstimate;
}

/// <summary>One world route leg (a packed trip's, or a caravan's way home): the estimated ticks to arrive, or Unreachable when the world pather finds no path.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PackRoute.Reachable), "reachable")]
[JsonDerivedType(typeof(PackRoute.Unreachable), "unreachable")]
public abstract record PackRoute
{
    private protected PackRoute() { }

    public sealed record Reachable(long EstimatedTicks) : PackRoute;

    public sealed record Unreachable : PackRoute;
}

public sealed record FoodSupplyFacts
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<FoodConsumer>))]
    public required EquatableArray<FoodConsumer> Consumers { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<FoodStock>))]
    public required EquatableArray<FoodStock> Stocks { get; init; }

    public required FoodLarderFacts Larder { get; init; }
}

/// <summary>A colonist with a mood need and the level its mood is heading to now (Need_Mood.CurInstantLevel); a colonist without a mood need has no row.</summary>
public sealed record PatientForecast
{
    public required string PawnId { get; init; }

    public required double MoodTarget { get; init; }
}

/// <summary>
/// The forecast read, whole: the needs and coupled activity reads, the food supply the colony's people and animals share,
/// and each patient's mood target.
/// </summary>
public sealed record ForecastFacts
{
    public required NeedsForecastRead Needs { get; init; }
    public required CoupledForecastRead Coupled { get; init; }

    public required FoodSupplyFacts CombinedFoodSupply { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PatientForecast>))]
    public required EquatableArray<PatientForecast> Patients { get; init; }
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

    /// <summary>The bed's ambient temperature (Building_Bed.AmbientTemperature).</summary>
    public required double TemperatureC { get; init; }

    /// <summary>The native id (Room.ID) of the room the bed stands in, outdoors included; the room census holds its stats.</summary>
    public required string Room { get; init; }

    public required BedQuality Quality { get; init; }

    public required bool ForSlaves { get; init; }
}

/// <summary>A bed's quality: its QualityCategory, or ungraded for a def without CompQuality (a sleeping spot).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(BedQuality.Graded), "graded")]
[JsonDerivedType(typeof(BedQuality.Ungraded), "ungraded")]
public abstract record BedQuality
{
    private protected BedQuality() { }

    public sealed record Graded(RimGovernor.Brain.GameFacts.Defs.QualityCategory Category) : BedQuality;

    public sealed record Ungraded : BedQuality;
}

/// <summary>
/// One damageable (useHitPoints) spawned player building's Home coverage. Identity joins the canonical building
/// row for physical state; flammability and repair priority are catalog metadata.
/// </summary>
public sealed record UpkeepStructure
{
    public required string Id { get; init; }

    public required bool Home { get; init; }
}

/// <summary>
/// One person of the sleeping census, joined by Pawn to its canonical pawn row for bed ownership and comfort. Partners: lover,
/// spouse and fiance relations to living pawns on the same map (native direct relations). BedSharingAllowed: the
/// native willingness to share a bed with every listed partner (the SharedBed precepts; with no partner, the plain
/// SharedBed precept), true without Ideology. Title: the most senior royal title, or untitled.
/// </summary>
public sealed record UpkeepPerson
{
    public required PawnId Pawn { get; init; }

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
    public required RimGovernor.Brain.GameFacts.Common.Ref Thing { get; init; }

    public required RimGovernor.Brain.GameFacts.Common.Ref? Owner { get; init; }
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

/// <summary>Whether a short circuit was seen on the map: <see cref="NoneSeen"/>, or <see cref="Seen"/> at the latest letter's tick.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ObservedShortCircuit.NoneSeen), "none_seen")]
[JsonDerivedType(typeof(ObservedShortCircuit.Seen), "seen")]
public abstract record ObservedShortCircuit
{
    private protected ObservedShortCircuit() { }

    public sealed record NoneSeen : ObservedShortCircuit;

    public sealed record Seen : ObservedShortCircuit
    {
        public required long Tick { get; init; }
    }
}

/// <summary>
/// One steam geyser on the map: geyser is the Building_SteamGeyser with its
/// anchor position, cells its 2x2 footprint, and occupied whether a harvester
/// (a geothermal generator, built or under construction) or any other
/// building already stands on it.
/// </summary>
public sealed record SteamGeyser
{
    public required string Id { get; init; }
    public required Cell Cell { get; init; }
    public required EquatableArray<Cell> Cells { get; init; }
    public required bool Occupied { get; init; }
}

public sealed record DevelopmentFacts
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<PowerNetwork>))]
    public required EquatableArray<PowerNetwork> Networks { get; init; }

    public required ObservedShortCircuit ShortCircuit { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<SteamGeyser>))]
    public required EquatableArray<SteamGeyser> Geysers { get; init; }
}

/// <summary>An active game condition affecting the map: its unique id, its def and how long it lasts.</summary>
public sealed record EnvironmentCondition
{
    public required string Id { get; init; }

    public required string DefName { get; init; }

    public required ConditionDuration Duration { get; init; }
}

/// <summary>
/// How long a game condition lasts: <see cref="Permanent"/> (GameCondition.Permanent) or <see cref="Timed"/> with the
/// game's remaining ticks (GameCondition.TicksLeft, never below zero).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ConditionDuration.Permanent), "permanent")]
[JsonDerivedType(typeof(ConditionDuration.Timed), "timed")]
public abstract record ConditionDuration
{
    private protected ConditionDuration() { }

    public sealed record Permanent : ConditionDuration;

    public sealed record Timed : ConditionDuration
    {
        public required long Ticks { get; init; }
    }
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
    public required EquatableArray<bool> GrowingYear { get; init; }

    public required double GrowingDays { get; init; }

    public required double GrowingDaysRemaining { get; init; }

    public required bool SowingNow { get; init; }

    public required double GrowingDaysUntil { get; init; }

    public required string Season { get; init; }

    public required int DayOfYear { get; init; }

    public required double NonGrowingDays { get; init; }
}

/// <summary>
/// A sowable crop with an edible product: the map's per-day facts beside
/// the catalog's static row.
/// </summary>
public sealed record EdibleCrop
{
    public required string DefName { get; init; }

    public required double NutritionDemandPerDay { get; init; }

    /// <summary>
    /// No colonist ideology penalises this crop product.
    /// </summary>
    public required bool DietAllowed { get; init; }
}

/// <summary>
/// The static facts of a product (nutrition, rot days, perishability, baby
/// edibility) are its def row's in the definition catalog; the frame
/// carries what varies: count, whether a human can eat it, the colony's demand
/// and the storage room left.
/// </summary>
public sealed record FoodProduct
{
    public required string DefName { get; init; }

    /// <summary>Whether a human eats the product.</summary>
    public required bool Edible { get; init; }

    /// <summary>
    /// storable is the product count roofed, reachable storage accepting it can
    /// hold: stored stacks plus empty stack slots. Caps reserve bill targets.
    /// </summary>
    public required long Storable { get; init; }
}

public sealed record FoodProduction
{
    public required string Recipe { get; init; }

    /// <summary>Whether the bench offers the recipe now and every product is one the colony takes.</summary>
    public required bool Available { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<FoodProduct>))]
    public required EquatableArray<FoodProduct> Products { get; init; }
}

/// <summary>A cooking bench's auto-refuel setting: <see cref="NotRefuelable"/> without a refuelable comp.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(BenchAutoRefuel.NotRefuelable), "not_refuelable")]
[JsonDerivedType(typeof(BenchAutoRefuel.Refuelable), "refuelable")]
public abstract record BenchAutoRefuel
{
    private protected BenchAutoRefuel() { }

    public sealed record NotRefuelable : BenchAutoRefuel;

    /// <summary>CompRefuelable.allowAutoRefuel.</summary>
    public sealed record Refuelable : BenchAutoRefuel
    {
        public required bool Allowed { get; init; }
    }
}

/// <summary>
/// One harvest or hunt source of the colony read: the source thing (its head is a table row), its CAS token, the
/// resource it yields and how many units, whether it is designated (<see cref="AcquisitionDesignation"/>), whether a
/// pawn's reservation or a colonist's current job targets it, and its kind's own facts (<see cref="AcquisitionKind"/>).
/// </summary>
public sealed record AcquisitionFacts
{
    public required RimGovernor.Brain.GameFacts.Common.Ref Source { get; init; }

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

/// <summary>
/// The MeatAmount of the fresh humanlike corpses near a bench, summed per race; the brain prices it off the race's meat def.
/// </summary>
public sealed record HumanCorpseMeat
{
    public required string Race { get; init; }

    public required double MeatAmount { get; init; }
}

public sealed record HumanButcherCandidate
{
    public required string PawnId { get; init; }

    public required bool CanWork { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Trait>))]
    public required EquatableArray<Trait> PawnTraits { get; init; }
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
    public required int Index { get; init; }

    public required string Label { get; init; }

    public required bool Selectable { get; init; }

    public required bool Resolves { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Keys { get; init; }
}

public sealed record ChoiceDialog
{
    public required int WindowId { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ChoiceDialogOption>))]
    public EquatableArray<ChoiceDialogOption> Options { get; init; }

    public required bool Interactive { get; init; }
}

public sealed record DevelopmentSection
{
    public required DevelopmentFacts Outcome { get; init; }
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
/// One mobile colonist's travel to a facility: the game's CanReach answer from where the colonist stands and, for a
/// reachable pair, the path the game itself would walk.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RouteTravel.Unreachable), "unreachable")]
[JsonDerivedType(typeof(RouteTravel.Skipped), "skipped")]
[JsonDerivedType(typeof(RouteTravel.Unpathed), "unpathed")]
[JsonDerivedType(typeof(RouteTravel.Measured), "measured")]
public abstract record RouteTravel
{
    private protected RouteTravel() { }

    public required string Pawn { get; init; }

    /// <summary>The colonist cannot reach the facility.</summary>
    public sealed record Unreachable : RouteTravel;

    /// <summary>
    /// Reachable, but not pathed: the stream's route_path_budget_ms ran out first, so the path's cost and the ordered
    /// doors it crosses were not measured.
    /// </summary>
    public sealed record Skipped : RouteTravel;

    /// <summary>Reachable, but the game's path search (FindPathNow) found no path from where the colonist stands.</summary>
    public sealed record Unpathed : RouteTravel;

    /// <summary>Reachable along the game's path: its total cost and its length in cells.</summary>
    public sealed record Measured : RouteTravel
    {
        public required int Cost { get; init; }

        public required int Cells { get; init; }
    }
}

/// <summary>
/// A wall cell a door would open the facility's room through, or a door already ordered on a reachable facility's path.
/// </summary>
public sealed record RouteBreach
{
    public required Cell Cell { get; init; }

    /// <summary>The edifice standing on the cell: the wall, or the ordered door's frame or blueprint.</summary>
    public required string Edifice { get; init; }

    /// <summary>The door already ordered on the cell; null when none is.</summary>
    public string? Pending { get; init; }

    /// <summary>The squared distance from the nearest colonist who reaches the cell's outer side; 0 for an ordered door.</summary>
    public required int Distance { get; init; }
}

public sealed record RouteFacility
{
    /// <summary>The facility's load id, or <c>zone-&lt;id&gt;</c> for a stockpile.</summary>
    public required string Id { get; init; }

    public required RouteFacilityKind Kind { get; init; }

    public required Cell Cell { get; init; }

    /// <summary>The proper indoor room the facility stands in; null when it stands outdoors.</summary>
    public string? Room { get; init; }

    /// <summary>One row per mobile colonist.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<RouteTravel>))]
    public required EquatableArray<RouteTravel> Travel { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<RouteBreach>))]
    public required EquatableArray<RouteBreach> Breaches { get; init; }
}

public sealed record TrafficCell
{
    public required Cell Cell { get; init; }

    /// <summary>The pawn class the count belongs to.</summary>
    public required TrafficLayer Layer { get; init; }

    public required uint Samples { get; init; }

    public required string Terrain { get; init; }

    public required bool Home { get; init; }

    /// <summary>The floor already ordered on the cell; null when none is.</summary>
    public string? Pending { get; init; }
}

/// <summary>
/// The routes census, read whole. The traffic counter is the map's TrafficState component, which the game builds on
/// every map, so its counts are always read.
/// </summary>
public sealed record RoutesFacts
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<RouteFacility>))]
    public required EquatableArray<RouteFacility> Facilities { get; init; }

    /// <summary>The mobile colonists measured.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> PawnIds { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<TrafficCell>))]
    public required EquatableArray<TrafficCell> Traffic { get; init; }

    /// <summary>The colonist layer's decayed sample total; zero right after a load.</summary>
    public required uint TrafficSamples { get; init; }
}

/// <summary>
/// The upkeep censuses, read whole: a census native cannot read fails the colony read, the routes census included (#3782).
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

    public required RoutesFacts Routes { get; init; }

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

    /// <summary>GenDate.DaysPassedSinceSettleFloat, the day count storyteller comps read.</summary>
    public required double DaysPassed { get; init; }

    /// <summary>
    /// StorytellerUtility.GetProgressScore(map): the free colonists (not quest lodgers) plus the storyteller's wealth × 0.0001, which
    /// an OnOffCycle comp's acceptPercentFactorPerProgressScoreCurve reads (#3860).
    /// </summary>
    public required double ProgressScore { get; init; }

    /// <summary>
    /// Whether an OnOffCycle comp runs its onDaysNoTreeConnectors / offDaysNoTreeConnectors: the player faction tracks ideoligions
    /// (Faction.OfPlayer.ideos) and none of them has the TreeConnection meme (StorytellerComp_OnOffCycle, #3860).
    /// </summary>
    public required bool NoTreeConnection { get; init; }

    /// <summary>Difficulty.threatScale: ThreatsGenerator scales its incident counts by it (#3860).</summary>
    public required double ThreatScale { get; init; }

    /// <summary>The map's primary biome by def name: a CategoryIndividualMTBByBiome comp reads each incident's mtbDaysByBiome row of it (#3860).</summary>
    public required string Biome { get; init; }

    /// <summary>The incidents a home-map ThreatsGenerator can draw, RaidEnemy and MechCluster (#3860); each is read for forced incident parms.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<ThreatIncidentRead>))]
    public required EquatableArray<ThreatIncidentRead> Generator { get; init; }

    /// <summary>Every home-map ThreatBig incident with biome MTB rows, with its native CanFireNow result at this census's default, unforced category parms. A false result is a real block, not an unread worker.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<ThreatBiomeIncidentRead>))]
    public required EquatableArray<ThreatBiomeIncidentRead> BiomeIncidents { get; init; }

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

    /// <summary>What the storyteller's disease comps read of the colony now (#3824); every threat census reads it.</summary>
    public required DiseaseFacts Disease { get; init; }

    /// <summary>What the storyteller's trader visits read of the colony now (#3869); every threat census reads it.</summary>
    public required TradeVisitFacts Trade { get; init; }
}

/// <summary>
/// One incident a ThreatsGenerator draws (#3860). ThreatsGenerator.MakeThreat takes the incidents whose worker CanFireNow for its
/// forced parms (IncidentWorker.CanFireNow skips every gate but CanFireNowSub, and an active void awakening) and picks one by
/// <see cref="Weight"/> (IncidentWorker.BaseChanceThisGame). <see cref="CanFire"/> mirrors CanFireNowSub at the map's raid points: for
/// RaidEnemy a faction that can be a group source (FactionCanBeGroupSource), for MechCluster Royalty and a mechanoid faction.
/// </summary>
public sealed record ThreatIncidentRead(string Incident, double Weight, bool CanFire);

/// <summary>A biome incident's observed native legality. Future rates are conditional on this observed game state remaining unchanged.</summary>
public sealed record ThreatBiomeIncidentRead(string Incident, bool CanFire);

/// <summary>
/// The colony facts a trader visit reads (#3869), read without changing game state: StorytellerUtility.AllyIncidentFraction for
/// fullAlliesOnly false and true (StorytellerComp_FactionInteraction's accept fraction; at or below 0 no faction can visit),
/// GenDate.DaysPassedSinceSettleFloat (IncidentCycleUtility counts from settling), whether a colonist comms console is powered
/// (IncidentWorker_OrbitalTraderArrival announces, and so lets the colony trade with, a ship only then) and an orbital trade beacon
/// is powered (a ship drops its goods and takes its silver there), the silver within a powered beacon's range (<see cref="BeaconSilver"/>:
/// TradeUtility.AllLaunchableThingsForTrade, the only silver an orbital trader is paid from), the best eligible negotiator's TradePriceImprovement (<see cref="Negotiator"/> false: no free colonist can negotiate), the
/// difficulty's tradePriceFactorLoss, the map's DangerWatcher.DangerRating (a FactionInteraction comp's minDanger gate, #3909),
/// StorytellerUtilityPopulation.PopulationIntent (StorytellerComp.TrySelectRandomIncident and IncidentChanceFinal, #3909), and one
/// row per incident a trader comp can draw (#3909: every incident of an exact TraderCaravanArrival, OrbitalTraderArrival,
/// VisitorGroup, TravelerGroup or CaravanArrivalTributeCollector worker).
/// </summary>
public sealed record TradeVisitFacts
{
    /// <summary>Native request options, read on every frame independently of demand.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<TradeRequestOption>))]
    public EquatableArray<TradeRequestOption> Requests { get; init; }

    public StoryDangerLevel Danger { get; init; }

    public double PopulationIntent { get; init; }

    public double AllyFraction { get; init; }

    public double FullAllyFraction { get; init; }

    public double DaysSinceSettle { get; init; }

    public bool CommsConsole { get; init; }

    public bool TradeBeacon { get; init; }

    public long BeaconSilver { get; init; }

    public bool Negotiator { get; init; }

    public double NegotiatorPriceImprovement { get; init; }

    public double PriceFactorLoss { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<TraderIncidentRead>))]
    public EquatableArray<TraderIncidentRead> Incidents { get; init; }
}

/// <summary>
/// One incident a trader comp can draw, on the map now (#3869, #3909): Blocked names the game-state gates of IncidentWorker.CanFireNow and its worker's
/// CanFireNowSub that hold now (<c>scenario</c>, <c>difficulty</c>, <c>planet_layer</c> as a disease incident's;
/// <c>target</c>: the map is not among its target tags; <c>biome</c>; <c>min_population</c>; <c>greatest_population</c>;
/// <c>refire</c>: IncidentWorker.FiredTooRecently; <c>condition</c>: a condition preventing incidents or neutral
/// visitors; <c>void_awakening</c>; <c>ships_full</c>: 5 passing ships; <c>no_faction</c>: no faction can be a group's source
/// (a tribute collector's: no Empire that can be)). Weight is StorytellerComp.IncidentChanceFinal's factors but the
/// population-intent one: BaseChanceThisGame × chanceFactorByPopulationCurve at the colonists × ChanceFactorNow × the
/// QuestTuning.RecentStoryWeightFactors of its places among the map's recent random incidents (none when it ignores them).
/// Sources are the draws a visit makes: a caravan's or visitor group's candidate
/// factions (FactionCanBeGroupSource, drawn uniformly), each with its caravanTraderKinds by
/// IncidentWorker_TraderCaravanArrival.TraderKindCommonality (a visitor group: its visitorTraderKinds by CalculatedCommonality);
/// a tribute collector's one source of the TributeCollector kinds by CalculatedCommonality; an orbital trader's one source of the
/// orbital kinds that can pass CanSpawn (a Qualifies above 0), by CalculatedCommonality (empty when none can: the ship fails in
/// TryExecuteWorker, past the draw); none for a traveler group.
/// </summary>
public sealed record TraderIncidentRead(string Incident)
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Blocked { get; init; }

    public double Weight { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<TraderSourceRead>))]
    public EquatableArray<TraderSourceRead> Sources { get; init; }
}

/// <summary>
/// One draw a trader visit can make (a caravan's or visitor group's candidate faction by its FactionDef, an orbital trader's or
/// tribute collector's one draw, of no faction): its trader kinds' weights.
/// </summary>
public sealed record TraderSourceRead
{
    public string Faction { get; init; } = "";

    [JsonConverter(typeof(EquatableArrayJsonConverter<TraderKindWeight>))]
    public EquatableArray<TraderKindWeight> Kinds { get; init; }
}

/// <summary>RimWorld.StoryDanger: the map's danger rating (DangerWatcher.DangerRating), ordered as the game compares it.</summary>
[JsonConverter(typeof(WireEnumConverter<StoryDangerLevel>))]
public enum StoryDangerLevel
{
    None,
    Low,
    High,
}

/// <summary>
/// A trader kind's draw weight: its TraderKindDef and the commonality the incident draws it by now. <see cref="Qualifies"/> is the
/// chance the faction the game draws for the kind (IncidentWorker_OrbitalTraderArrival.GetFaction: one of its faction def's factions
/// at random, drawn once for CanSpawn and again for the ship itself) is one a free colonist can trade with (Pawn.CanTradeWith): the
/// share of those factions that qualify, 0 when the def has none, and 1 for a kind of no faction and for every draw but an orbital
/// trader's.
/// </summary>
public sealed record TraderKindWeight(string Kind, double Weight)
{
    public double Qualifies { get; init; } = 1;
}

/// <summary>
/// The colony facts StorytellerComp_Disease and IncidentWorker_DiseaseHuman read (#3824). Read-only: the storyteller and the
/// map's biome by def name, DebugSettings.enableRandomDiseases, the difficulty's diseaseIntervalFactor,
/// GenDate.DaysPassedSinceSettleFloat, the incident's victim candidates (the map's free colonists and prisoners outside a
/// biosculpter pod) and the free colonists the minPopulation gate counts, and one row per DiseaseHuman incident. The other comps
/// that draw a DiseaseHuman incident (#3860) read the raid points, progress score and tree-connection state of an OnOffCycle and the
/// StorytellerUtilityPopulation.PopulationIntent of a category draw (retained on <see cref="ThreatFacts"/> and
/// <see cref="TradeVisitFacts"/>).
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
/// <c>planet_layer</c>: the layer lists). Weight is StorytellerComp.IncidentChanceFinal's factors but the population-intent one
/// (BaseChanceThisGame × chanceFactorByPopulationCurve × ChanceFactorNow × the recent-incident factors), by which a comp other than
/// StorytellerComp_Disease draws the incident (#3860); StorytellerComp_Disease draws by the biome's commonality instead.
/// </summary>
public sealed record DiseaseIncidentRead(string Incident, int Potential)
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Blocked { get; init; }

    public double Weight { get; init; }
}

/// <summary>The threat section of the colony census. A threat read that fails fails the census.</summary>
public sealed record ThreatSection
{
    public required ThreatFacts Observed { get; init; }
}

/// <summary>
/// Whether a colonist can haul a stack safely: <see cref="Safe"/> when some colonist who can reach it has a hazard-free
/// route there and back (with that route's length in cells), <see cref="Unsafe"/> when colonists reach it but every
/// route is exposed, <see cref="Unreached"/> when no colonist reaches it.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HaulSafety.Safe), "safe")]
[JsonDerivedType(typeof(HaulSafety.Unsafe), "unsafe")]
[JsonDerivedType(typeof(HaulSafety.Unreached), "unreached")]
public abstract record HaulSafety
{
    private protected HaulSafety() { }

    public sealed record Safe : HaulSafety
    {
        public required double PathLength { get; init; }
    }

    public sealed record Unsafe : HaulSafety;

    public sealed record Unreached : HaulSafety;
}

/// <summary>
/// Current autonomous hauling safety for visible haulable items. count is the
/// stack size; safety is the colonists' verdict on hauling it (<see cref="HaulSafety"/>);
/// storage_headroom is the
/// item units the player's accepting storage can still take for this def;
/// accepted_by, set only on an unforbidden row with storage_headroom 0 and
/// a safety that is not unsafe, names the player's storage groups (stockpile zones and
/// storage buildings, by load id) whose settings accept the item, ignoring space.
/// deterioration is the stack's deterioration where it lies (#3798), absent when it
/// cannot deteriorate.
/// </summary>
public sealed record LootItem
{
    public required RimGovernor.Brain.GameFacts.Common.Ref Item { get; init; }

    public required bool Forbidden { get; init; }

    public required HaulSafety Safety { get; init; }

    public required long Count { get; init; }

    public required long StorageHeadroom { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<RimGovernor.Brain.GameFacts.Common.Ref>))]
    public required EquatableArray<RimGovernor.Brain.GameFacts.Common.Ref> AcceptedBy { get; init; }

    public LootDeterioration? Deterioration { get; init; }
}

/// <summary>
/// A loot stack's hauling verdict. <see cref="Safe"/>: some colonist who can reach the stack has a hazard-free route
/// there and back, PathLength the shortest such route in cells. <see cref="Exposed"/>: colonists reach it but every
/// route meets a hazard (or the stack itself lies in one). <see cref="Unreached"/>: no colonist reaches it.
/// </summary>

/// <summary>
/// A stack's deterioration where it lies (#3798): its MaxHitPoints, SteadyEnvironmentEffects.FinalDeteriorationRate now
/// (every DeteriorationRate part applied at its cell), whether StatPart_EnvironmentalEffects applies
/// (deteriorateFromEnvironmentalEffects), the cell terrain's extraDeteriorationFactor, SteadyEnvironmentEffects.ProtectedByEdifice,
/// whether the cell is roofed and whether its room uses the outdoor temperature (the part's two offsets). Read only for a
/// stack that can deteriorate (useHitPoints and CanEverDeteriorate).
/// </summary>
public sealed record LootDeterioration
{
    /// <summary>The def and actual stuff rate before environmental exposure.</summary>
    public required double BaseRatePerDay { get; init; }

    public double MaxHitPoints { get; init; }

    public double RatePerDay { get; init; }

    public bool Environmental { get; init; }

    public double TerrainFactor { get; init; }

    public bool Protected { get; init; }

    public bool Roofed { get; init; }

    public bool Outdoors { get; init; }
}

/// <summary>
/// Food source potential, independent of acquisition instances and policy.
/// Gather rows are one per gatherable comp and egg rows one per egg layer; slaughter
/// rows cover the animals whose meat the colony eats. Water rows aggregate fishable cells by native water body; population
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

/// <summary>A fish-bearing water body whose fish the colony eats; nutrition_per_fish is its least nutritious fish.</summary>
public sealed record FishableRegion
{
    public required Cell Root { get; init; }

    public required double Population { get; init; }

    public required double MaxPopulation { get; init; }

    public required bool Zoned { get; init; }

    public required bool Reachable { get; init; }

    public required bool Frozen { get; init; }

    public required double NutritionPerFish { get; init; }

    public required double NearestDistanceSquared { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<FishableCell>))]
    public required EquatableArray<FishableCell> Cells { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<FishingZoneFacts>))]
    public required EquatableArray<FishingZoneFacts> Zones { get; init; }

    public required double YieldCurveValue { get; init; }
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

    public required bool FishingResearched { get; init; }

    public required double ResearchBaseCost { get; init; }

    public required double ResearchProgress { get; init; }

    public required double ResearchCostFactor { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<double>))]
    public required EquatableArray<double> ResearcherSpeeds { get; init; }

    public required double ResearchPointsPerWorkTick { get; init; }

    public required double ResearchDifficultySpeedFactor { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<FisherFacts>))]
    public required EquatableArray<FisherFacts> Fishers { get; init; }

    public required double BaseFishingDurationTicks { get; init; }
}

/// <summary>
/// What one animal body resource yields the colony now: inactive (not a milk comp, the comp is inactive, or the
/// colony does not eat the product), stalled (active, but the interval or PawnUtility.BodyResourceGrowthSpeed is
/// zero), or producing nutrition per day, handling work per day (JobDriver_Milk.WorkTotal per interval, travel
/// excluded; zero for eggs) and the days until the next yield.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AnimalYield.Inactive), "inactive")]
[JsonDerivedType(typeof(AnimalYield.Stalled), "stalled")]
[JsonDerivedType(typeof(AnimalYield.Producing), "producing")]
public abstract record AnimalYield
{
    private protected AnimalYield() { }

    public sealed record Inactive : AnimalYield;

    public sealed record Stalled : AnimalYield;

    public sealed record Producing(double NutritionPerDay, double WorkPerDay, double LeadDays) : AnimalYield;
}

/// <summary>One gatherable body resource comp (CompHasGatherableBodyResource) of a player animal.</summary>
public sealed record GatherableAnimal
{
    public required string PawnId { get; init; }

    public required string Race { get; init; }

    public required double Fullness { get; init; }

    public required string Resource { get; init; }

    public required bool HandlerReachable { get; init; }

    public required AnimalYield Yield { get; init; }
}

/// <summary>A player animal with a CompEggLayer; eggs need no handler.</summary>
public sealed record EggLayerAnimal
{
    public required string PawnId { get; init; }

    public required string Race { get; init; }

    public required AnimalYield Yield { get; init; }
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

/// <summary>One player pen's PenFoodCalculator: its daily demand, the worst quadrum's pasture per day and the stockpiled nutrition.</summary>
public sealed record PenGrazing
{
    public required double DemandPerDay { get; init; }

    public required double PasturePerDay { get; init; }

    public required double StoredNutrition { get; init; }
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

/// <summary>A player animal whose meat the colony eats, with its live MeatAmount stat; other animals have no row.</summary>
public sealed record FoodSlaughterAnimal
{
    public required string PawnId { get; init; }

    public required string Race { get; init; }

    public required double MeatAmount { get; init; }
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

/// <summary>The food on hand over the colonists' daily need: <see cref="NoDemand"/> when nothing eats, else the days it lasts.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(FoodRunway.NoDemand), "no_demand")]
[JsonDerivedType(typeof(FoodRunway.Days), "days")]
public abstract record FoodRunway
{
    private protected FoodRunway() { }

    public sealed record NoDemand : FoodRunway;

    public sealed record Days : FoodRunway
    {
        public required double Value { get; init; }
    }
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
/// One colony facts read, all of it from one game-thread frame. A read that cannot produce a member fails whole;
/// the members a later facts family owns keep their section types until it hardens them.
/// </summary>
public sealed record ColonyFactsSnapshot
{
    /// <summary>The complete gear census from this game-thread frame.</summary>
    public required GearSnapshot Gear { get; init; }

    /// <summary>Every edible sowable crop with its current colony diet and nutrition demand.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<EdibleCrop>))]
    public required EquatableArray<EdibleCrop> Crops { get; init; }

    /// <summary>Exact thing identities contributing to Resources.</summary>
    public required EquatableArray<string> ResourceThings { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<WorkResourcePawn>))]
    public required EquatableArray<WorkResourcePawn> WorkResources { get; init; }
    public required RimGovernor.Brain.GameFacts.Common.ObservationContext Context { get; init; }

    public required uint ColonistCount { get; init; }

    public required uint WorkerCount { get; init; }

    public required MapSize MapSize { get; init; }

    public required string Biome { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Quantity>))]
    public required EquatableArray<Quantity> Resources { get; init; }

    public double FoodNutrition { get; init; }

    public double NutritionPerDay { get; init; }

    public required FoodRunway FoodRunway { get; init; }

    /// <summary>The human-edible nutrition of every designated harvest and hunt not yet taken.</summary>
    public required double PendingFoodNutrition { get; init; }

    public required uint BedCapacity { get; init; }

    public required uint IndoorSleepingCapacity { get; init; }

    public required SleepingTemperature SleepingTemperature { get; init; }

    public required double OutdoorTemperatureC { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<EnvironmentCondition>))]
    public EquatableArray<EnvironmentCondition> Environment { get; init; }

    public required FoodClimate FoodClimate { get; init; }

    public required ControlledEnvironment ControlledEnvironment { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ProductionBenchFacts>))]
    public required EquatableArray<ProductionBenchFacts> ProductionBenches { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<AcquisitionFacts>))]
    public required EquatableArray<AcquisitionFacts> Acquisition { get; init; }


    public required FoodSupplyFacts FoodSupply { get; init; }

    public required ForecastFacts Forecast { get; init; }

    public required UpkeepFacts Upkeep { get; init; }

    public required DevelopmentSection Development { get; init; }

    public required RecoverySnapshot Recovery { get; init; }

    public required WasteSnapshot Waste { get; init; }

    public required BlightSnapshot Blight { get; init; }

    /// <summary>The wild animals designated for hunting.</summary>
    public required uint PendingHunts { get; init; }

    /// <summary>
    /// Player faction TechLevel; the floor of the construction tier.
    /// </summary>
    public required RimGovernor.Brain.GameFacts.Defs.TechLevel PlayerTechLevel { get; init; }

    /// <summary>
    /// Present only while exactly one force-pausing choice dialog is open.
    /// </summary>
    public ChoiceDialog? Dialog { get; init; }

    /// <summary>
    /// Colony wealth and raid points (ThreatFacts), or why they could not be read.
    /// </summary>
    public required ThreatSection Threat { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<LootItem>))]
    public required EquatableArray<LootItem> EventLoot { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<JoinerLetter>))]
    public required EquatableArray<JoinerLetter> JoinerLetters { get; init; }

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

    [JsonConverter(typeof(EquatableArrayJsonConverter<RimGovernor.Brain.GameFacts.Operations.DrugPolicyEntry>))]
    public required EquatableArray<RimGovernor.Brain.GameFacts.Operations.DrugPolicyEntry> DrugEntries { get; init; }
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
/// A pending current-map WandererJoins or creepjoiner letter: its letter id, the opaque token that binds letter, quest,
/// pawn, map, expiry and choices, the accept option's label and whether that option is enabled.
/// </summary>
public sealed record JoinerLetter
{
    public required int Id { get; init; }

    public required string Token { get; init; }

    public required string Label { get; init; }

    public required bool CanAccept { get; init; }
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
    public required RimGovernor.Brain.GameFacts.Common.Ref Pawn { get; init; }

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
    public required RimGovernor.Brain.GameFacts.Common.Ref Pawn { get; init; }

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

    public required RimGovernor.Brain.GameFacts.Common.ObservationContext Context { get; init; }

    public required ThreatsSnapshot Threats { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<RimGovernor.Brain.GameFacts.Common.Ref>))]
    public required EquatableArray<RimGovernor.Brain.GameFacts.Common.Ref> Colonists { get; init; }
}

/// <summary>
/// A snapshot stream frame, and the scheduler's step read the controller
/// composes (clock_status is set only there: frames never carry it).
/// </summary>
public sealed record BundleSnapshot
{
    public RimGovernor.Brain.GameFacts.Common.ObservationContext? Context { get; init; }

    public RimGovernor.Brain.GameFacts.Clock.Status? ClockStatus { get; init; }

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
    [JsonConverter(typeof(EquatableArrayJsonConverter<RimGovernor.Brain.GameFacts.Mirror.CombatPawn>))]
    public EquatableArray<RimGovernor.Brain.GameFacts.Mirror.CombatPawn> CombatPawns { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<RimGovernor.Brain.GameFacts.Mirror.CombatEventRow>))]
    public EquatableArray<RimGovernor.Brain.GameFacts.Mirror.CombatEventRow> CombatEvents { get; init; }

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
    [JsonConverter(typeof(EquatableArrayJsonConverter<RimGovernor.Brain.GameFacts.Mirror.CombatDoorRow>))]
    public EquatableArray<RimGovernor.Brain.GameFacts.Mirror.CombatDoorRow> CombatDoors { get; init; }

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
    [JsonConverter(typeof(EquatableArrayJsonConverter<RimGovernor.Brain.GameFacts.Mirror.CombatMortarRow>))]
    public EquatableArray<RimGovernor.Brain.GameFacts.Mirror.CombatMortarRow> CombatMortars { get; init; }

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
    public RimGovernor.Brain.GameFacts.Mirror.CellGrid? Grid { get; init; }

    /// <summary>
    /// The ideology section: <see cref="IdeologyFrame.NotInstalled"/> without the Ideology expansion, else the player
    /// faction's primary ideoligion as it stands (the precepts and roles in force, ritual state and building precepts;
    /// a colony without a primary ideoligion fails the section read). A routine frame always carries it; the brain
    /// refuses one without it.
    /// </summary>
    public IdeologyFrame? Ideology { get; init; }

    /// <summary>
    /// A frame whose section read threw: the frame carries only this,
    /// with the section name, exception type and message in detail. A reader
    /// refuses the read naming the section; the frame is never carried on
    /// with the section omitted.
    /// </summary>
    public RimGovernor.Brain.GameFacts.Common.Failure? Failure { get; init; }
}

/// <summary>ModsConfig.IdeologyActive and the section it gates: <see cref="NotInstalled"/> without the expansion.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(IdeologyFrame.NotInstalled), "not_installed")]
[JsonDerivedType(typeof(IdeologyFrame.Installed), "installed")]
public abstract record IdeologyFrame
{
    private protected IdeologyFrame() { }

    public sealed record NotInstalled : IdeologyFrame;

    public sealed record Installed : IdeologyFrame
    {
        public required IdeologySnapshot Snapshot { get; init; }
    }
}

/// <summary>A frame's time allowance: <see cref="Unlimited"/>, or <see cref="Millis"/> main-thread milliseconds.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(FrameBudget.Unlimited), "unlimited")]
[JsonDerivedType(typeof(FrameBudget.Millis), "millis")]
public abstract record FrameBudget
{
    private protected FrameBudget() { }

    public sealed record Unlimited : FrameBudget;

    public sealed record Millis : FrameBudget
    {
        public required uint Value { get; init; }
    }
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
/// downside's timers run (#3844). The downside def itself is hidden information and is never read. combat_power is its
/// kindDef.combatPower, the points of the fight an aggressive downside starts (#3844 D12).
/// </summary>
public sealed record CreepJoinerState
{
    public required bool DownsideTriggered { get; init; }

    public required int JoinedTick { get; init; }

    public required double CombatPower { get; init; }
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

    public required RimGovernor.Brain.GameFacts.Common.Ref? HeldPawn { get; init; }

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

/// <summary>What the catalog prepare answers: the load's stat table and race facts are ready. A refused or unavailable prepare throws.</summary>
public sealed record DefinitionCatalogAck;

/// <summary>The stream open reply: opened when <see cref="Unavailable"/> is absent.</summary>
public sealed record SnapshotStreamReply
{
    public RimGovernor.Brain.GameFacts.Common.Unavailable? Unavailable { get; init; }
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
    public RimGovernor.Brain.GameFacts.Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(RimGovernor.Brain.GameFacts.Common.Unavailable Value) : SnapshotReadReplyOutcome;

    public static implicit operator SnapshotReadReplyOutcome(RimGovernor.Brain.GameFacts.Common.Unavailable value) => new UnavailableCase(value);
}

public sealed record FlushSnapshotReply
{
    public bool Flushed { get; init; }
}

/// <summary>
/// One pawn's current outfit and its wearer inputs: the outfit's label (Name), load id (PolicyId), allowed apparel
/// definitions, hit-point fraction and quality ranges (QualityCategory indices) and whether it excludes tainted
/// apparel; whether the pawn is a slave of the colony; its short name
/// (PawnName), the definitions its royal title, ideoligion role and ideoligion apparel precepts require, and
/// whether it goes nude (nudist trait or mandatory nudity precept).
/// Equipment, comfort, traits, skills, work settings, draft state and disabled work tags belong to the canonical
/// pawn row; the gear census carries the pawn's universal developmental stage and wear inputs.
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

    public required bool Slave { get; init; }

    public required string PawnName { get; init; }

    public required string PolicyId { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> RequiredDefs { get; init; }

    public required bool Nude { get; init; }
}

/// <summary>
/// One beg-for-items request: the pawn the item goes to, the requested def, the beggar group, and whether the
/// recipient is asking for it now.
/// </summary>
public sealed record QuestGiftRequest
{
    public required PawnId Recipient { get; init; }

    public required string DefName { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnId>))]
    public required EquatableArray<PawnId> Pawns { get; init; }

    public required QuestGiftAsk Ask { get; init; }
}

/// <summary>A non-mutating stock read for the proposed negotiator. An unseen or empty pool draws on arrival;
/// held stock survives until RestockInTicks, then the next visit draws the native kind afresh.</summary>
public sealed record SettlementTradePreview(string Settlement, string TraderKind, bool RegeneratesOnVisit,
    long RestockInTicks, double PriceFactor,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<SettlementStockItem>))] EquatableArray<SettlementStockItem> CurrentStock);

/// <summary>A held sale stack with its native unit buy price before restocking.</summary>
public sealed record SettlementStockItem(string Definition, int Count, double UnitPrice);
