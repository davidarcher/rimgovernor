using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Contracts.Wire.Operations;

/// <summary>
/// Batched combat micro, the Actions/Apply combat_orders arm:
/// every order applies in request order within one apply, so on one game
/// tick. Each pawn's snapshot token, when sent, is checked once, before any
/// order runs; a pawn may carry several orders (fire mode then move). A pawn
/// order needs an eligible drafted pawn, which a draft order earlier in the
/// batch may make. A door or mortar_fire order names no pawn. Per-order refusals come back
/// in CombatOrdersEffect; only a malformed batch refuses the whole action.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<CombatFireMode>))]
public enum CombatFireMode
{
    [WireName("COMBAT_FIRE_MODE_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("COMBAT_FIRE_MODE_AT_WILL")]
    AtWill = 1,
    [WireName("COMBAT_FIRE_MODE_HOLD")]
    Hold = 2,
}

/// <summary>
/// FORBID and ALLOW set the door's forbidden flag, which every player
/// pawn's path respects, drafted or not (ForbidUtility.IsForbiddenToPass).
/// </summary>
[JsonConverter(typeof(WireEnumConverter<CombatDoorMode>))]
public enum CombatDoorMode
{
    [WireName("COMBAT_DOOR_MODE_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("COMBAT_DOOR_MODE_HOLD_OPEN")]
    HoldOpen = 1,
    [WireName("COMBAT_DOOR_MODE_CLOSE")]
    Close = 2,
    [WireName("COMBAT_DOOR_MODE_FORBID")]
    Forbid = 3,
    [WireName("COMBAT_DOOR_MODE_ALLOW")]
    Allow = 4,
}

[JsonConverter(typeof(WireEnumConverter<ThingDesignation>))]
public enum ThingDesignation
{
    [WireName("THING_DESIGNATION_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("THING_DESIGNATION_ALLOW")]
    Allow = 1,
    [WireName("THING_DESIGNATION_FORBID")]
    Forbid = 2,
    [WireName("THING_DESIGNATION_HUNT")]
    Hunt = 3,
    [WireName("THING_DESIGNATION_HARVEST_PLANT")]
    HarvestPlant = 4,
    [WireName("THING_DESIGNATION_DECONSTRUCT")]
    Deconstruct = 5,
    /// <summary>
    /// CutPlant on one exact blighted plant (read from the cell mirror's plant state).
    /// </summary>
    [WireName("THING_DESIGNATION_CUT_PLANT")]
    CutPlant = 6,
    /// <summary>
    /// Strip on one exact spawned pawn or corpse wearing or carrying
    /// strippable gear; colonists strip it through Hauling work.
    /// </summary>
    [WireName("THING_DESIGNATION_STRIP")]
    Strip = 7,
    /// <summary>
    /// Removal designations, on DesignateIntent target or cell.
    /// </summary>
    [WireName("THING_DESIGNATION_MINE")]
    Mine = 8,
    [WireName("THING_DESIGNATION_HAUL")]
    Haul = 9,
    [WireName("THING_DESIGNATION_REMOVE_FOUNDATION")]
    RemoveFoundation = 10,
}

[JsonConverter(typeof(WireEnumConverter<PowerSetting>))]
public enum PowerSetting
{
    [WireName("POWER_SETTING_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("POWER_SETTING_ON")]
    On = 1,
    [WireName("POWER_SETTING_OFF")]
    Off = 2,
}

/// <summary>
/// HostilityResponse is vanilla HostilityResponseMode.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<HostilityResponse>))]
public enum HostilityResponse
{
    [WireName("HOSTILITY_RESPONSE_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("HOSTILITY_RESPONSE_IGNORE")]
    Ignore = 1,
    [WireName("HOSTILITY_RESPONSE_ATTACK")]
    Attack = 2,
    [WireName("HOSTILITY_RESPONSE_FLEE")]
    Flee = 3,
}

[JsonConverter(typeof(WireEnumConverter<MedicalCare>))]
public enum MedicalCare
{
    [WireName("MEDICAL_CARE_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("MEDICAL_CARE_NO_CARE")]
    NoCare = 1,
    [WireName("MEDICAL_CARE_NO_MEDICINE")]
    NoMedicine = 2,
    [WireName("MEDICAL_CARE_HERBAL_OR_WORSE")]
    HerbalOrWorse = 3,
    [WireName("MEDICAL_CARE_NORMAL_OR_WORSE")]
    NormalOrWorse = 4,
    [WireName("MEDICAL_CARE_BEST")]
    Best = 5,
}

[JsonConverter(typeof(WireEnumConverter<RepeatMode>))]
public enum RepeatMode
{
    [WireName("REPEAT_MODE_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("REPEAT_MODE_FOREVER")]
    Forever = 1,
    [WireName("REPEAT_MODE_COUNT")]
    Count = 2,
    [WireName("REPEAT_MODE_TARGET")]
    Target = 3,
}

[JsonConverter(typeof(WireEnumConverter<StoreMode>))]
public enum StoreMode
{
    [WireName("STORE_MODE_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("STORE_MODE_BEST_STOCKPILE")]
    BestStockpile = 1,
    [WireName("STORE_MODE_DROP_ON_FLOOR")]
    DropOnFloor = 2,
}

[JsonConverter(typeof(WireEnumConverter<ZoneType>))]
public enum ZoneType
{
    [WireName("ZONE_TYPE_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("ZONE_TYPE_STOCKPILE")]
    Stockpile = 1,
    [WireName("ZONE_TYPE_DUMPING")]
    Dumping = 2,
    [WireName("ZONE_TYPE_GROWING")]
    Growing = 3,
    [WireName("ZONE_TYPE_FISHING")]
    Fishing = 4,
}

[JsonConverter(typeof(WireEnumConverter<StoragePriority>))]
public enum StoragePriority
{
    [WireName("STORAGE_PRIORITY_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("STORAGE_PRIORITY_LOW")]
    Low = 1,
    [WireName("STORAGE_PRIORITY_NORMAL")]
    Normal = 2,
    [WireName("STORAGE_PRIORITY_PREFERRED")]
    Preferred = 3,
    [WireName("STORAGE_PRIORITY_IMPORTANT")]
    Important = 4,
    [WireName("STORAGE_PRIORITY_CRITICAL")]
    Critical = 5,
}

[JsonConverter(typeof(WireEnumConverter<FilterPreset>))]
public enum FilterPreset
{
    [WireName("FILTER_PRESET_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("FILTER_PRESET_EVERYTHING")]
    Everything = 1,
    [WireName("FILTER_PRESET_NOTHING")]
    Nothing = 2,
    [WireName("FILTER_PRESET_FOOD")]
    Food = 3,
    [WireName("FILTER_PRESET_PERISHABLES")]
    Perishables = 4,
    [WireName("FILTER_PRESET_NONPERISHABLES")]
    Nonperishables = 5,
    [WireName("FILTER_PRESET_OUTDOOR_SAFE")]
    OutdoorSafe = 6,
    [WireName("FILTER_PRESET_INDOOR_ONLY")]
    IndoorOnly = 7,
}

[JsonConverter(typeof(WireEnumConverter<Need>))]
public enum Need
{
    [WireName("NEED_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("NEED_FOOD")]
    Food = 1,
    [WireName("NEED_REST")]
    Rest = 2,
    [WireName("NEED_JOY")]
    Joy = 3,
}

/// <summary>
/// Exclusive prisoner interaction modes. REDUCE_RESISTANCE and RELEASE are
/// Core; ENSLAVE and CONVERT resolve only while Ideology is active and are
/// refused as unsupported otherwise. Execution and non-exclusive modes stay
/// player-only.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<PrisonerInteraction>))]
public enum PrisonerInteraction
{
    [WireName("PRISONER_INTERACTION_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("PRISONER_INTERACTION_ATTEMPT_RECRUIT")]
    AttemptRecruit = 1,
    [WireName("PRISONER_INTERACTION_MAINTAIN_ONLY")]
    MaintainOnly = 2,
    [WireName("PRISONER_INTERACTION_REDUCE_RESISTANCE")]
    ReduceResistance = 3,
    [WireName("PRISONER_INTERACTION_RELEASE")]
    Release = 4,
    [WireName("PRISONER_INTERACTION_ENSLAVE")]
    Enslave = 5,
    [WireName("PRISONER_INTERACTION_CONVERT")]
    Convert = 6,
}

[JsonConverter(typeof(WireEnumConverter<EndTradeKind>))]
public enum EndTradeKind
{
    [WireName("END_TRADE_KIND_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("END_TRADE_KIND_CANCEL")]
    Cancel = 1,
    [WireName("END_TRADE_KIND_CLOSE_DIALOG")]
    CloseDialog = 2,
}

/// <summary>
/// Create, edit or delete one bot-owned allowed area, or edit the home area.
/// A bot area is the Area_Allowed labelled with its key;
/// native never touches an area the player made. home and key are exclusive;
/// home takes set_cells and clear_cells only. create refuses when the map
/// cannot make another allowed area, applies again on an existing bot area,
/// and sets any given cells. set_cells and clear_cells need an existing area
/// and in-bounds cells; delete of a missing bot area applies again.
/// Evidence is the AreaEffect: the area's load id and cell count after the
/// edit.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<AreaOperation>))]
public enum AreaOperation
{
    [WireName("AREA_OPERATION_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("AREA_OPERATION_CREATE")]
    Create = 1,
    [WireName("AREA_OPERATION_SET_CELLS")]
    SetCells = 2,
    [WireName("AREA_OPERATION_CLEAR_CELLS")]
    ClearCells = 3,
    [WireName("AREA_OPERATION_DELETE")]
    Delete = 4,
}

/// <summary>
/// Delete policies no pawn should keep: each delete_ids entry is a
/// Policy.GetUniqueLoadID() in the named outfit, drug, food or reading
/// database, or an Area_Allowed load id. Every live pawn still holding the
/// policy first moves onto its own per-pawn policy (the one labelled with its
/// short name, made from the database's defaults when missing), or off the
/// area (unrestricted); then vanilla TryDelete (Area.Delete) removes it. An id
/// already gone applies again. Evidence is the PolicyPruneEffect.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<PolicyDatabase>))]
public enum PolicyDatabase
{
    [WireName("POLICY_DATABASE_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("POLICY_DATABASE_OUTFIT")]
    Outfit = 1,
    [WireName("POLICY_DATABASE_DRUG")]
    Drug = 2,
    [WireName("POLICY_DATABASE_FOOD")]
    Food = 3,
    [WireName("POLICY_DATABASE_READING")]
    Reading = 4,
    [WireName("POLICY_DATABASE_ALLOWED_AREA")]
    AllowedArea = 5,
}

/// <summary>
/// The tick-level safety a guarded designation keeps. Native checks
/// the guard at admission and re-checks it before the job's work lands until
/// the job finishes: a failed check cancels the job and drops the designation.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<DesignationGuard>))]
public enum DesignationGuard
{
    [WireName("DESIGNATION_GUARD_UNSPECIFIED")]
    Unspecified = 0,
    /// <summary>
    /// Building removal (DECONSTRUCT): player deconstructible, visible, not
    /// forbidden or burning and no pending wall upgrade; an enclosing wall is
    /// admitted. A removal that would collapse a roof (unproven roof support, or
    /// rooms of cleared ground still roofed) holds at the work giver without
    /// cancelling the designation or losing progress, and releases when the
    /// support is proven (waiting_for_roof).
    /// </summary>
    [WireName("DESIGNATION_GUARD_ENCLOSURE")]
    Enclosure = 1,
    /// <summary>
    /// Rock removal (MINE): no pending roof collapse, an unowned visible rock
    /// with no structure on the cell, away from the map edge, and counterfactual
    /// roof support for the cell.
    /// </summary>
    [WireName("DESIGNATION_GUARD_MINE_SAFETY")]
    MineSafety = 2,
    /// <summary>
    /// Stone-shell wall upgrade (DECONSTRUCT on cell): native resolves
    /// the wall-upgrade site from the colonist wall at cell (the original a
    /// replacement rebuilds, or a backup that held the shell open; several
    /// admissible sites refuse) and holds the demolition to the site's
    /// enclosure, supports, materials and roof support until a pawn finishes.
    /// target, when set, must name the wall at cell. A cell whose wall is gone
    /// applies again; evidence is the WallEffect. Go waits for the census to
    /// show the cell clear before releasing work that needs it.
    /// </summary>
    [WireName("DESIGNATION_GUARD_WALL_UPGRADE")]
    WallUpgrade = 3,
    /// <summary>
    /// Resource acquisition: HUNT, HARVEST_PLANT or MINE on one census
    /// source (target, cell, expected_def the resource it yields), the
    /// designation the source takes. Native checks the source's census rules
    /// live (a hunt follows the animal wherever it is, cell the hint); a
    /// designation already in the requested state applies again and evidence
    /// is the AcquisitionEffect. A MINE is re-checked for excavation safety
    /// before each pick, and a mined cell that opens protected colony space is
    /// walled.
    /// </summary>
    [WireName("DESIGNATION_GUARD_ACQUISITION")]
    Acquisition = 4,
    /// <summary>
    /// Wastepack hauling: HAUL on one exact wastepack (a thing with the
    /// game's CompDissolution) that is neither frozen nor inside an atomizer, so
    /// a hauler carries it to storage that stops its deterioration. The guard
    /// is accepted on HAUL only; a forbidden pack is refused (allow it first).
    /// </summary>
    [WireName("DESIGNATION_GUARD_WASTEPACK")]
    Wastepack = 5,
}

/// <summary>
/// Activate one option of the force-pausing choice dialog ColonyFactsSnapshot.dialog
/// observed (Verse.Dialog_NodeTree): window_id plus the exact observed option index
/// and label stand in for an entity precondition, and native refuses on any drift.
/// With joiner_letter_token, window_id is a WandererJoins letter ID and option 0
/// accepts the joiner; a joiner already accepted by that letter applies again.
/// One animal's husbandry order: a recursive training request, a slaughter,
/// tame or release-to-wild designation or the cancel of one, or an
/// Animals-tab setting. trainable_def is TRAIN's argument; target_id is the
/// allowed area or master colonist (absent clears it); follow is the flag a
/// FOLLOW order sets. Tame targets a wild animal, every other order a player
/// animal. Native checks the animal and the order's own eligibility
/// (canTrain, safe to slaughter or release, tameable, Obedience for master
/// and following, a safe reachable area) live when it applies; an order that
/// already holds applies again.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<HusbandryOrder>))]
public enum HusbandryOrder
{
    [WireName("HUSBANDRY_ORDER_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("HUSBANDRY_ORDER_TRAIN")]
    Train = 1,
    [WireName("HUSBANDRY_ORDER_SLAUGHTER")]
    Slaughter = 2,
    [WireName("HUSBANDRY_ORDER_TAME")]
    Tame = 3,
    [WireName("HUSBANDRY_ORDER_RELEASE")]
    Release = 4,
    [WireName("HUSBANDRY_ORDER_ALLOWED_AREA")]
    AllowedArea = 5,
    [WireName("HUSBANDRY_ORDER_MASTER")]
    Master = 6,
    [WireName("HUSBANDRY_ORDER_FOLLOW_DRAFTED")]
    FollowDrafted = 7,
    [WireName("HUSBANDRY_ORDER_FOLLOW_FIELDWORK")]
    FollowFieldwork = 8,
    [WireName("HUSBANDRY_ORDER_CANCEL_SLAUGHTER")]
    CancelSlaughter = 9,
    [WireName("HUSBANDRY_ORDER_CANCEL_RELEASE")]
    CancelRelease = 10,
    /// <summary>
    /// Queue the animal sterilize surgery bill (the recipe is found live on the
    /// race's recipes); applied means queued, the sterilized state reads back
    /// when the surgery is done.
    /// </summary>
    [WireName("HUSBANDRY_ORDER_STERILIZE")]
    Sterilize = 11,
}

/// <summary>
/// Declarative native rules: "on trigger, if predicates, do a whitelisted
/// action". Go authors and attaches every rule (replace-all, each Round, with a
/// lease); native only executes. Rules live in native memory, never in the
/// save, and fire only while native holds Auto authority. v1 is one rule
/// shape: PREY_KILLED, then GIVE_JOB Hunt on the nearest designated prey,
/// built by the same path as GiveJobIntent (options.prioritized). There are no
/// draft actions. A firing is journaled (clock.v1.RuleFired) before its write;
/// native adds no count, radius or per-pawn firing limit.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<RuleTrigger>))]
public enum RuleTrigger
{
    [WireName("RULE_TRIGGER_UNSPECIFIED")]
    Unspecified = 0,
    /// <summary>
    /// A player pawn killed a wild animal; fired in-process by the kill hook the
    /// delivery ledger shares. The actor is the killer.
    /// </summary>
    [WireName("RULE_TRIGGER_PREY_KILLED")]
    PreyKilled = 1,
}

[JsonConverter(typeof(WireEnumConverter<RulePredicate>))]
public enum RulePredicate
{
    [WireName("RULE_PREDICATE_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("RULE_PREDICATE_ACTOR_UNDRAFTED")]
    ActorUndrafted = 1,
    /// <summary>
    /// The actor has the Hunting work type active.
    /// </summary>
    [WireName("RULE_PREDICATE_ACTOR_HUNTING_WORK_ACTIVE")]
    ActorHuntingWorkActive = 2,
    /// <summary>
    /// A designated hunt prey exists within radius, reachable and route-safe.
    /// </summary>
    [WireName("RULE_PREDICATE_TARGET_AVAILABLE")]
    TargetAvailable = 3,
}

[JsonConverter(typeof(WireEnumConverter<RuleAction>))]
public enum RuleAction
{
    [WireName("RULE_ACTION_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("RULE_ACTION_GIVE_JOB")]
    GiveJob = 1,
}

[JsonConverter(typeof(WireEnumConverter<RuleTargetSelector>))]
public enum RuleTargetSelector
{
    [WireName("RULE_TARGET_SELECTOR_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("RULE_TARGET_SELECTOR_NEAREST_DESIGNATED_PREY")]
    NearestDesignatedPrey = 1,
}

[JsonConverter(typeof(WireEnumConverter<RuleRefusalReason>))]
public enum RuleRefusalReason
{
    [WireName("RULE_REFUSAL_REASON_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("RULE_REFUSAL_REASON_INVALID_ID")]
    InvalidId = 1,
    [WireName("RULE_REFUSAL_REASON_DUPLICATE_ID")]
    DuplicateId = 2,
    [WireName("RULE_REFUSAL_REASON_UNSUPPORTED_TRIGGER")]
    UnsupportedTrigger = 4,
    [WireName("RULE_REFUSAL_REASON_UNSUPPORTED_PREDICATE")]
    UnsupportedPredicate = 5,
    [WireName("RULE_REFUSAL_REASON_UNSUPPORTED_ACTION")]
    UnsupportedAction = 6,
    [WireName("RULE_REFUSAL_REASON_UNSUPPORTED_JOB")]
    UnsupportedJob = 7,
    [WireName("RULE_REFUSAL_REASON_UNSUPPORTED_TARGET")]
    UnsupportedTarget = 8,
    [WireName("RULE_REFUSAL_REASON_INVALID_PREDATOR_MARGIN")]
    InvalidPredatorMargin = 10,
}

/// <summary>
/// Tokens are opaque native snapshots scoped to request identity and exact entity.
/// </summary>
public sealed record EntityPrecondition
{
    public string? EntityId { get; init; }

    public string? ExpectedSnapshotToken { get; init; }
}

public sealed record Clear;

public sealed record Assignment
{
    public AssignmentValue? Value { get; init; }
}

/// <summary>The <c>value</c> choice of <see cref="Assignment"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AssignmentValue.EntityIdCase), "entityId")]
[JsonDerivedType(typeof(AssignmentValue.ClearCase), "clear")]
public abstract record AssignmentValue
{
    private protected AssignmentValue() { }

    public sealed record EntityIdCase(string Value) : AssignmentValue;

    public sealed record ClearCase(Clear Value) : AssignmentValue;

    public static implicit operator AssignmentValue(Clear value) => new ClearCase(value);
}

public sealed record DefCount
{
    public string? DefName { get; init; }

    public int? Count { get; init; }
}

public sealed record CellList
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public EquatableArray<Common.Cell> Cells { get; init; }
}

public sealed record Rectangle
{
    public Common.Cell? Origin { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }
}

public sealed record Cells
{
    public CellsSelection? Selection { get; init; }
}

/// <summary>The <c>selection</c> choice of <see cref="Cells"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CellsSelection.ExplicitCellsCase), "explicitCells")]
[JsonDerivedType(typeof(CellsSelection.RectangleCase), "rectangle")]
public abstract record CellsSelection
{
    private protected CellsSelection() { }

    public sealed record ExplicitCellsCase(CellList Value) : CellsSelection;

    public static implicit operator CellsSelection(CellList value) => new ExplicitCellsCase(value);

    public sealed record RectangleCase(Rectangle Value) : CellsSelection;

    public static implicit operator CellsSelection(Rectangle value) => new RectangleCase(value);
}

/// <summary>
/// Rescue: the pawn carries the downed colonist to a bed by the
/// vanilla rescue job, under the pawn-target rescue order's eligibility
/// (HealthAIUtility.CanRescueNow, a non-hostile patient) and bed search.
/// dest, when set, is the bed's cell; absent, the vanilla bed search picks.
/// Refusals: cannot_rescue, no_bed, unreachable.
/// </summary>
public sealed record CombatRescue
{
    public EntityPrecondition? Downed { get; init; }

    public Common.Cell? Dest { get; init; }
}

/// <summary>
/// Repair: the drafted pawn repairs the damaged player building on
/// cell (a door the fight uses) by the vanilla Repair job, staying drafted.
/// Refusals: cannot_repair (no damaged player building there, or the pawn
/// cannot construct), unreachable.
/// </summary>
public sealed record CombatRepair
{
    public Common.Cell? Cell { get; init; }
}

/// <summary>
/// MortarFire names no pawn: the unroofed player mortar on mortar
/// force-targets target, as the vanilla attack gizmo (OrderAttack); a
/// man_mortar order crews it. Refusals: not_a_mortar, cannot_hit (inside
/// the minimum or past the maximum range). shell, when set, is the
/// shell ThingDef to fire: a different loaded shell is unloaded beside the
/// mortar, the mortar's shell filter allows only shell, and a pawn manning
/// it retakes ManTurret to load it. Refusals: unknown_shell (not a shell
/// this mortar accepts), no_shell (no unforbidden stack of it, in the
/// manning pawn's reach when one mans it).
/// No target clears the mortar's forced target, as the vanilla
/// stop-forced-attack gizmo; it names no shell. Refusal: not_a_mortar.
/// </summary>
public sealed record CombatMortarFire
{
    public Common.Cell? Mortar { get; init; }

    public Common.Cell? Target { get; init; }

    public string? Shell { get; init; }
}

/// <summary>
/// Animal orders name a player-faction animal, not a drafted pawn:
/// no draft claim is needed, and the animal carries no snapshot token.
/// Refusals: not_ours (not a spawned player animal), untrained (release
/// without the Release training).
/// animal_area restricts the animal to one cell by a native-owned allowed
/// area; clear restores the area it had before (none when unknown, as
/// after a reload) and removes the native area.
/// </summary>
public sealed record CombatAnimalArea
{
    public CombatAnimalAreaArea? Area { get; init; }
}

/// <summary>The <c>area</c> choice of <see cref="CombatAnimalArea"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CombatAnimalAreaArea.CellCase), "cell")]
[JsonDerivedType(typeof(CombatAnimalAreaArea.ClearCase), "clear")]
public abstract record CombatAnimalAreaArea
{
    private protected CombatAnimalAreaArea() { }

    public sealed record CellCase(Common.Cell Value) : CombatAnimalAreaArea;

    public static implicit operator CombatAnimalAreaArea(Common.Cell value) => new CellCase(value);

    public sealed record ClearCase(Clear Value) : CombatAnimalAreaArea;

    public static implicit operator CombatAnimalAreaArea(Clear value) => new ClearCase(value);
}

public sealed record CombatDoor
{
    public Common.Cell? Cell { get; init; }

    public CombatDoorMode? Mode { get; init; }
}

public sealed record CombatOrder
{
    public EntityPrecondition? Pawn { get; init; }

    public CombatOrderOrder? Order { get; init; }
}

/// <summary>The <c>order</c> choice of <see cref="CombatOrder"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CombatOrderOrder.MoveCase), "move")]
[JsonDerivedType(typeof(CombatOrderOrder.AttackCase), "attack")]
[JsonDerivedType(typeof(CombatOrderOrder.AttackGroundCase), "attackGround")]
[JsonDerivedType(typeof(CombatOrderOrder.FireModeCase), "fireMode")]
[JsonDerivedType(typeof(CombatOrderOrder.HoldPositionCase), "holdPosition")]
[JsonDerivedType(typeof(CombatOrderOrder.DoorCase), "door")]
[JsonDerivedType(typeof(CombatOrderOrder.StopCase), "stop")]
[JsonDerivedType(typeof(CombatOrderOrder.RescueCase), "rescue")]
[JsonDerivedType(typeof(CombatOrderOrder.RepairCase), "repair")]
[JsonDerivedType(typeof(CombatOrderOrder.DraftCase), "draft")]
[JsonDerivedType(typeof(CombatOrderOrder.ManMortarCase), "manMortar")]
[JsonDerivedType(typeof(CombatOrderOrder.MortarFireCase), "mortarFire")]
[JsonDerivedType(typeof(CombatOrderOrder.ReleaseCase), "release")]
[JsonDerivedType(typeof(CombatOrderOrder.AnimalAreaCase), "animalArea")]
[JsonDerivedType(typeof(CombatOrderOrder.CombatDrugCase), "combatDrug")]
public abstract record CombatOrderOrder
{
    private protected CombatOrderOrder() { }

    public sealed record MoveCase(Common.Cell Value) : CombatOrderOrder;

    /// <summary>
    /// A spawned pawn or a hostile building thing: any spawned
    /// hostile-faction building or frame with hit points (a crashed ship
    /// part, its cluster buildings, a siege mortar or its frame). Native
    /// picks melee or ranged as the vanilla float menu does for the pawn's
    /// weapon; there is no other attack order.
    /// </summary>
    public sealed record AttackCase(EntityPrecondition Value) : CombatOrderOrder;

    /// <summary>
    /// Force-fire a ground-targetable verb (grenades, launchers) at a cell.
    /// </summary>
    public sealed record AttackGroundCase(Common.Cell Value) : CombatOrderOrder;

    public sealed record FireModeCase(CombatFireMode Value) : CombatOrderOrder;

    public sealed record HoldPositionCase(Clear Value) : CombatOrderOrder;

    public sealed record DoorCase(CombatDoor Value) : CombatOrderOrder;

    public sealed record StopCase(Clear Value) : CombatOrderOrder;

    public sealed record RescueCase(CombatRescue Value) : CombatOrderOrder;

    public sealed record RepairCase(CombatRepair Value) : CombatOrderOrder;

    /// <summary>
    /// Draft: draft an eligible pawn; one already drafted applies
    /// again. The controller undrafts pawns no live plan needs.
    /// Refusal: cannot_draft (not eligible).
    /// </summary>
    public sealed record DraftCase(Clear Value) : CombatOrderOrder;

    /// <summary>
    /// ManMortar: the drafted pawn mans the unroofed player mortar
    /// on the cell (vanilla ManTurret, which loads shells from stock); one
    /// already manning it applies. Refusals: not_a_mortar, unreachable.
    /// </summary>
    public sealed record ManMortarCase(Common.Cell Value) : CombatOrderOrder;

    public sealed record MortarFireCase(CombatMortarFire Value) : CombatOrderOrder;

    /// <summary>
    /// Release: a release-trained player animal attacks one spawned
    /// pawn by the vanilla melee attack job, as a released animal does.
    /// Refusals: not_ours, untrained, not_found, cannot_hit, unreachable.
    /// </summary>
    public sealed record ReleaseCase(EntityPrecondition Value) : CombatOrderOrder;

    public sealed record AnimalAreaCase(CombatAnimalArea Value) : CombatOrderOrder;

    /// <summary>
    /// CombatDrug: the drafted pawn ingests the carried drug of this
    /// def name (Go chooses it) by the vanilla Ingest job.
    /// Refusals: not_a_drug (the def is not a drug), no_drug (none carried),
    /// native_refused (vanilla declined the ordered job).
    /// </summary>
    public sealed record CombatDrugCase(string Value) : CombatOrderOrder;
}

public sealed record CombatOrders
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<CombatOrder>))]
    public EquatableArray<CombatOrder> Orders { get; init; }
}

public sealed record WorkPriority
{
    public string? WorkTypeDef { get; init; }

    public int? Priority { get; init; }
}

public sealed record Schedule
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> AssignmentDefs { get; init; }
}

public sealed record FilterSelector
{
    public FilterSelectorDefinition? Definition { get; init; }
}

/// <summary>The <c>definition</c> choice of <see cref="FilterSelector"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(FilterSelectorDefinition.ThingDefCase), "thingDef")]
[JsonDerivedType(typeof(FilterSelectorDefinition.CategoryDefCase), "categoryDef")]
[JsonDerivedType(typeof(FilterSelectorDefinition.SpecialFilterDefCase), "specialFilterDef")]
public abstract record FilterSelectorDefinition
{
    private protected FilterSelectorDefinition() { }

    public sealed record ThingDefCase(string Value) : FilterSelectorDefinition;

    public sealed record CategoryDefCase(string Value) : FilterSelectorDefinition;

    public sealed record SpecialFilterDefCase(string Value) : FilterSelectorDefinition;
}

public sealed record SelectorList
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<FilterSelector>))]
    public EquatableArray<FilterSelector> Selectors { get; init; }
}

public sealed record FilterPatch
{
    /// <summary>
    /// Absent replacement preserves existing membership; present empty denies all.
    /// </summary>
    public SelectorList? Replace { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<FilterSelector>))]
    public EquatableArray<FilterSelector> Allow { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<FilterSelector>))]
    public EquatableArray<FilterSelector> Disallow { get; init; }

    /// <summary>
    /// Hit-point range as fractions in [0, 1] (the storage tab's percent slider)
    /// and quality range by QualityCategory name (Awful..Legendary); both ends
    /// of a range are required together. Absent ranges are preserved.
    /// </summary>
    public double? HitPointsMin { get; init; }

    public double? HitPointsMax { get; init; }

    public string? QualityMin { get; init; }

    public string? QualityMax { get; init; }
}

public sealed record BillStore
{
    public BillStoreDestination? Destination { get; init; }
}

/// <summary>The <c>destination</c> choice of <see cref="BillStore"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(BillStoreDestination.ModeCase), "mode")]
[JsonDerivedType(typeof(BillStoreDestination.ZoneIdCase), "zoneId")]
public abstract record BillStoreDestination
{
    private protected BillStoreDestination() { }

    public sealed record ModeCase(StoreMode Value) : BillStoreDestination;

    public sealed record ZoneIdCase(string Value) : BillStoreDestination;
}

public sealed record BillSettings
{
    public RepeatMode? RepeatMode { get; init; }

    public int? RepeatCount { get; init; }

    public int? TargetCount { get; init; }

    public int? UnpauseThreshold { get; init; }

    public bool? PauseWhenSatisfied { get; init; }

    public bool? Suspended { get; init; }

    public float? IngredientSearchRadius { get; init; }

    public int? SkillMin { get; init; }

    public int? SkillMax { get; init; }

    public Assignment? Worker { get; init; }

    public BillStore? Store { get; init; }

    public FilterPatch? Ingredients { get; init; }

    /// <summary>
    /// Wort bill whose target counts wort, fermenting contents and finished beer.
    /// </summary>
    public bool? BeerReserve { get; init; }

    /// <summary>
    /// Corpse bill ingredient filter: whose corpses a corpse recipe
    /// (ButcherCorpseFlesh) takes. Required on corpse recipes,
    /// refused on others; a butcher bill with a pinned worker is humanlike.
    /// </summary>
    public Common.CorpseClass? CorpseClass { get; init; }
}

public sealed record DefinitionList
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Defs { get; init; }
}

public sealed record StockpileSettings
{
    public StoragePriority? Priority { get; init; }

    public FilterPreset? Preset { get; init; }

    public FilterPatch? Filter { get; init; }
}

public sealed record GrowingSettings
{
    public string? PlantDef { get; init; }

    public bool? AllowSow { get; init; }

    public bool? AllowCut { get; init; }
}

public sealed record FishingSettings
{
    public double? PopulationFloor { get; init; }
}

/// <summary>
/// The one zone write. Its shape selects the edit; each applies again
/// once the map already holds it:
/// - create: kind set, zone empty; stockpiles take an add_cells rectangle drag
/// and create every free vanilla-zoneable cardinal component. Growing and
/// fishing take explicit connected cells. Native checks ground live.
/// A fishing create with zone set extends that fishing zone to
/// add_cells, its complete larger footprint.
/// - cells: zone set, kind unset, exactly one of add_cells (free zoneable
/// ground) and remove_cells (the zone's own cells); the result stays
/// contiguous, and removing the last cell deletes the zone.
/// - delete: zone set and delete true. Native refuses a zone holding a phantom
/// cell or a stockpile whose haul grid disagrees with its cells; a zone
/// already gone applies again.
/// - settings: zone set and stockpile only. Replaces the priority and patches
/// the filter of a stockpile zone or of a player storage building (a shelf),
/// resolved against the target's storable definitions; absent parts keep
/// the live setting.
/// </summary>
public sealed record ZoneIntent
{
    public Common.Ref? Zone { get; init; }

    public ZoneType? Kind { get; init; }

    public string? Label { get; init; }

    public Cells? AddCells { get; init; }

    public Cells? RemoveCells { get; init; }

    public bool? Delete { get; init; }

    public StockpileSettings? Stockpile { get; init; }

    public GrowingSettings? Growing { get; init; }

    public FishingSettings? Fishing { get; init; }

    public bool? RequireCoveredEmpty { get; init; }
}

/// <summary>
/// Vanilla's RemoveFloor designation on one cell whose constructed floor is
/// def_name. A cell already designated, or without that floor,
/// applies again.
/// </summary>
public sealed record RemoveFloorIntent
{
    public Common.Cell? Cell { get; init; }

    public string? DefName { get; init; }
}

/// <summary>
/// Trade steps, sent as TradeIntent through Actions/Apply, act on RimWorld's
/// one live trade session. Native validates each against live state when it
/// applies and refuses with a reason when the session is gone or held by a
/// different trader or negotiator; repeating one is harmless. OpenTrade
/// reuses a matching open session or walk.
/// </summary>
public sealed record OpenTrade
{
    public bool? GiftMode { get; init; }
}

public sealed record TradeLine
{
    public string? LineId { get; init; }

    public int? AbsoluteCount { get; init; }
}

public sealed record SetTradeLines
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<TradeLine>))]
    public EquatableArray<TradeLine> Lines { get; init; }

    public bool? AllowPawns { get; init; }
}

public sealed record AcceptTrade
{
    public string? ExpectedDealSignature { get; init; }

    public bool? AllowEmpty { get; init; }

    public bool? ReceiveQuest { get; init; }
}

/// <summary>
/// A cancel with no live session is a no-op, not a refusal.
/// </summary>
public sealed record EndTrade
{
    public EndTradeKind? Kind { get; init; }

    public bool? ReceiveQuest { get; init; }
}

public sealed record ZonePreviewRequest
{
    public Common.Identity? Identity { get; init; }

    public ZoneIntent? Zone { get; init; }
}

/// <summary>
/// reason names the ground rule a refused (accepted false) zone broke.
/// </summary>
public sealed record ZonePreview
{
    public Common.ObservationContext? Context { get; init; }

    public bool? Accepted { get; init; }

    public string? Reason { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<CellList>))]
    public EquatableArray<CellList> Components { get; init; }
}

public sealed record ZonePreviewReply
{
    public ZonePreviewReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="ZonePreviewReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ZonePreviewReplyOutcome.EvaluatedCase), "evaluated")]
[JsonDerivedType(typeof(ZonePreviewReplyOutcome.FailureCase), "failure")]
public abstract record ZonePreviewReplyOutcome
{
    private protected ZonePreviewReplyOutcome() { }

    public sealed record EvaluatedCase(ZonePreview Value) : ZonePreviewReplyOutcome;

    public static implicit operator ZonePreviewReplyOutcome(ZonePreview value) => new EvaluatedCase(value);

    public sealed record FailureCase(Common.Failure Value) : ZonePreviewReplyOutcome;

    public static implicit operator ZonePreviewReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record ApplyRequest
{
    public Common.Identity? Identity { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Action>))]
    public EquatableArray<Action> Actions { get; init; }

    /// <summary>
    /// Record the write without capturing a snapshot frame for it:
    /// the next frame after observations_flush_snapshot, or 1 s after the
    /// oldest uncaptured deferred write, captures it. Unset captures at once.
    /// </summary>
    public bool? DeferSnapshot { get; init; }
}

/// <summary>
/// One intent kind per arm.
/// </summary>
public sealed record Action
{
    public string? Key { get; init; }

    /// <summary>
    /// Why the controller applies this action, one short ASCII line such as
    /// "Food supply: hunt, food runway 1.5d". Native keeps it with
    /// the jobs, blueprints and designations the apply places and the
    /// "activity" overlay shows it over the pawn; it never changes the apply.
    /// </summary>
    public string? Purpose { get; init; }

    /// <summary>
    /// Melee attacks are combat orders; subdue is GiveJobIntent AttackMelee.
    /// </summary>
    public ActionIntent? Intent { get; init; }
}

/// <summary>The <c>intent</c> choice of <see cref="Action"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ActionIntent.TradeCase), "trade")]
[JsonDerivedType(typeof(ActionIntent.BuildingCase), "building")]
[JsonDerivedType(typeof(ActionIntent.MoveCase), "move")]
[JsonDerivedType(typeof(ActionIntent.HaulCase), "haul")]
[JsonDerivedType(typeof(ActionIntent.ApparelPolicyCase), "apparelPolicy")]
[JsonDerivedType(typeof(ActionIntent.ResearchCase), "research")]
[JsonDerivedType(typeof(ActionIntent.DialogCase), "dialog")]
[JsonDerivedType(typeof(ActionIntent.PrisonerCase), "prisoner")]
[JsonDerivedType(typeof(ActionIntent.AcceptQuestCase), "acceptQuest")]
[JsonDerivedType(typeof(ActionIntent.FormCaravanCase), "formCaravan")]
[JsonDerivedType(typeof(ActionIntent.AssignCase), "assign")]
[JsonDerivedType(typeof(ActionIntent.WorkSettingsCase), "workSettings")]
[JsonDerivedType(typeof(ActionIntent.ProductionBillCase), "productionBill")]
[JsonDerivedType(typeof(ActionIntent.HusbandryCase), "husbandry")]
[JsonDerivedType(typeof(ActionIntent.ZoneCase), "zone")]
[JsonDerivedType(typeof(ActionIntent.DesignateCase), "designate")]
[JsonDerivedType(typeof(ActionIntent.RelocateCase), "relocate")]
[JsonDerivedType(typeof(ActionIntent.BuildingPatchCase), "buildingPatch")]
[JsonDerivedType(typeof(ActionIntent.GiveJobCase), "giveJob")]
[JsonDerivedType(typeof(ActionIntent.DraftCase), "draft")]
[JsonDerivedType(typeof(ActionIntent.CombatOrdersCase), "combatOrders")]
[JsonDerivedType(typeof(ActionIntent.AreaCase), "area")]
[JsonDerivedType(typeof(ActionIntent.AutoHomeAreaCase), "autoHomeArea")]
[JsonDerivedType(typeof(ActionIntent.PawnSettingsCase), "pawnSettings")]
[JsonDerivedType(typeof(ActionIntent.PolicyPruneCase), "policyPrune")]
[JsonDerivedType(typeof(ActionIntent.RemoveFloorCase), "removeFloor")]
[JsonDerivedType(typeof(ActionIntent.RemoveRoofCase), "removeRoof")]
[JsonDerivedType(typeof(ActionIntent.ReadingPolicyCase), "readingPolicy")]
[JsonDerivedType(typeof(ActionIntent.DrugPolicyCase), "drugPolicy")]
[JsonDerivedType(typeof(ActionIntent.AreaPlantCutCase), "areaPlantCut")]
[JsonDerivedType(typeof(ActionIntent.FoodPolicyCase), "foodPolicy")]
[JsonDerivedType(typeof(ActionIntent.RitualCase), "ritual")]
[JsonDerivedType(typeof(ActionIntent.AbilityCase), "ability")]
[JsonDerivedType(typeof(ActionIntent.IgniteCase), "ignite")]
[JsonDerivedType(typeof(ActionIntent.RulesAttachCase), "rulesAttach")]
[JsonDerivedType(typeof(ActionIntent.RemoveProductionBillCase), "removeProductionBill")]
[JsonDerivedType(typeof(ActionIntent.IdeoligionReformCase), "ideoligionReform")]
[JsonDerivedType(typeof(ActionIntent.QuestShuttleCase), "questShuttle")]
[JsonDerivedType(typeof(ActionIntent.HackDesignationCase), "hackDesignation")]
[JsonDerivedType(typeof(ActionIntent.GiveItemCase), "giveItem")]
[JsonDerivedType(typeof(ActionIntent.CommsTradeRequestCase), "commsTradeRequest")]
[JsonDerivedType(typeof(ActionIntent.GatheringCase), "gathering")]
[JsonDerivedType(typeof(ActionIntent.BuildRoofCase), "buildRoof")]
public abstract record ActionIntent
{
    private protected ActionIntent() { }

    public sealed record TradeCase(TradeIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(TradeIntent value) => new TradeCase(value);

    public sealed record BuildingCase(BuildingIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(BuildingIntent value) => new BuildingCase(value);

    public sealed record MoveCase(MoveIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(MoveIntent value) => new MoveCase(value);

    public sealed record HaulCase(HaulIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(HaulIntent value) => new HaulCase(value);

    public sealed record ApparelPolicyCase(ApparelPolicyIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(ApparelPolicyIntent value) => new ApparelPolicyCase(value);

    public sealed record ResearchCase(ResearchIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(ResearchIntent value) => new ResearchCase(value);

    public sealed record DialogCase(DialogIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(DialogIntent value) => new DialogCase(value);

    public sealed record PrisonerCase(PrisonerInteractionIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(PrisonerInteractionIntent value) => new PrisonerCase(value);

    public sealed record AcceptQuestCase(AcceptQuestIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(AcceptQuestIntent value) => new AcceptQuestCase(value);

    public sealed record FormCaravanCase(FormCaravanIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(FormCaravanIntent value) => new FormCaravanCase(value);

    public sealed record AssignCase(AssignIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(AssignIntent value) => new AssignCase(value);

    public sealed record WorkSettingsCase(WorkSettingsIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(WorkSettingsIntent value) => new WorkSettingsCase(value);

    public sealed record ProductionBillCase(ProductionBillIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(ProductionBillIntent value) => new ProductionBillCase(value);

    public sealed record HusbandryCase(HusbandryIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(HusbandryIntent value) => new HusbandryCase(value);

    /// <summary>
    /// Applied evidence names the zone (or the storage building's settings).
    /// </summary>
    public sealed record ZoneCase(ZoneIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(ZoneIntent value) => new ZoneCase(value);

    public sealed record DesignateCase(DesignateIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(DesignateIntent value) => new DesignateCase(value);

    public sealed record RelocateCase(RelocateIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(RelocateIntent value) => new RelocateCase(value);

    public sealed record BuildingPatchCase(BuildingPatchIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(BuildingPatchIntent value) => new BuildingPatchCase(value);

    public sealed record GiveJobCase(GiveJobIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(GiveJobIntent value) => new GiveJobCase(value);

    public sealed record DraftCase(DraftIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(DraftIntent value) => new DraftCase(value);

    public sealed record CombatOrdersCase(CombatOrders Value) : ActionIntent;

    public static implicit operator ActionIntent(CombatOrders value) => new CombatOrdersCase(value);

    public sealed record AreaCase(AreaIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(AreaIntent value) => new AreaCase(value);

    public sealed record AutoHomeAreaCase(AutoHomeAreaIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(AutoHomeAreaIntent value) => new AutoHomeAreaCase(value);

    public sealed record PawnSettingsCase(PawnSettingsIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(PawnSettingsIntent value) => new PawnSettingsCase(value);

    public sealed record PolicyPruneCase(PolicyPruneIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(PolicyPruneIntent value) => new PolicyPruneCase(value);

    public sealed record RemoveFloorCase(RemoveFloorIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(RemoveFloorIntent value) => new RemoveFloorCase(value);

    public sealed record RemoveRoofCase(RemoveRoofIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(RemoveRoofIntent value) => new RemoveRoofCase(value);

    public sealed record ReadingPolicyCase(ReadingPolicyIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(ReadingPolicyIntent value) => new ReadingPolicyCase(value);

    public sealed record DrugPolicyCase(DrugPolicyIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(DrugPolicyIntent value) => new DrugPolicyCase(value);

    public sealed record AreaPlantCutCase(AreaPlantCutIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(AreaPlantCutIntent value) => new AreaPlantCutCase(value);

    public sealed record FoodPolicyCase(FoodPolicyIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(FoodPolicyIntent value) => new FoodPolicyCase(value);

    public sealed record RitualCase(RitualIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(RitualIntent value) => new RitualCase(value);

    public sealed record AbilityCase(AbilityIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(AbilityIntent value) => new AbilityCase(value);

    public sealed record IgniteCase(IgniteIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(IgniteIntent value) => new IgniteCase(value);

    public sealed record RulesAttachCase(RulesAttachIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(RulesAttachIntent value) => new RulesAttachCase(value);

    public sealed record RemoveProductionBillCase(RemoveProductionBillIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(RemoveProductionBillIntent value) => new RemoveProductionBillCase(value);

    public sealed record IdeoligionReformCase(IdeoligionReformIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(IdeoligionReformIntent value) => new IdeoligionReformCase(value);

    public sealed record QuestShuttleCase(QuestShuttleIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(QuestShuttleIntent value) => new QuestShuttleCase(value);

    public sealed record HackDesignationCase(HackDesignationIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(HackDesignationIntent value) => new HackDesignationCase(value);

    public sealed record GiveItemCase(GiveItemIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(GiveItemIntent value) => new GiveItemCase(value);

    public sealed record CommsTradeRequestCase(CommsTradeRequestIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(CommsTradeRequestIntent value) => new CommsTradeRequestCase(value);

    public sealed record GatheringCase(GatheringIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(GatheringIntent value) => new GatheringCase(value);

    public sealed record BuildRoofCase(BuildRoofIntent Value) : ActionIntent;

    public static implicit operator ActionIntent(BuildRoofIntent value) => new BuildRoofCase(value);
}

public sealed record HackDesignationIntent
{
    public Common.Ref? Target { get; init; }

    public bool? Enabled { get; init; }
}

/// <summary>
/// The count fences the whole native request, after items already being hauled.
/// </summary>
public sealed record GiveItemIntent
{
    public Common.Ref? Hauler { get; init; }

    public Common.Ref? Recipient { get; init; }

    public string? Definition { get; init; }

    public long? ExpectedRemaining { get; init; }
}

/// <summary>
/// A normal fluid reformation; the current design and count fence an uncertain
/// retry even after native's short request replay window has expired.
/// </summary>
public sealed record IdeoligionReformIntent
{
    public string? IdeoId { get; init; }

    public Common.IdeoligionDesign? Expected { get; init; }

    public int? ExpectedReformCount { get; init; }

    public Common.IdeoligionDesign? Design { get; init; }
}

/// <summary>
/// One drafted colonist throws a molotov at one cell: the
/// CombatOrder attack_ground force-fire with the molotov in the pawn's
/// primary slot. Native refuses while any pawn stands in the cell's room, and
/// when the pawn is not drafted, holds no molotov or cannot hit the cell. Applied means the throw order
/// was taken; whether the fire caught is a separate observed state.
/// </summary>
public sealed record IgniteIntent
{
    public string? PawnId { get; init; }

    public Common.Cell? Cell { get; init; }
}

/// <summary>
/// Use one ability of one pawn: one generic shape for every
/// ability source. source names where the ability comes from and native's
/// guard for that source decides eligibility by calling the game's own
/// validation (never trusting Go for favor, cooldown or range); target is what
/// the ability aims at and must match what the source takes. Applied means the
/// ability was used and its native cooldown started; the evidence is an
/// AbilityEffect. A use that already took effect is never re-sent: a retry
/// refuses "on cooldown", and an uncertain receipt is resolved by re-reading
/// the cooldown on the royalty read. Drafted callers are allowed; a downed,
/// dead, unspawned or non-colonist pawn is refused.
/// </summary>
public sealed record AbilityIntent
{
    public string? PawnId { get; init; }

    public AbilityIntentSource? Source { get; init; }

    public AbilityIntentTarget? Target { get; init; }
}

/// <summary>The <c>source</c> choice of <see cref="AbilityIntent"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AbilityIntentSource.PermitCase), "permit")]
[JsonDerivedType(typeof(AbilityIntentSource.PsycastCase), "psycast")]
public abstract record AbilityIntentSource
{
    private protected AbilityIntentSource() { }

    /// <summary>
    /// An acting royal permit (royalAid) the pawn holds with a faction.
    /// </summary>
    public sealed record PermitCase(PermitAbility Value) : AbilityIntentSource;

    public static implicit operator AbilityIntentSource(PermitAbility value) => new PermitCase(value);

    /// <summary>
    /// A psycast the pawn knows.
    /// </summary>
    public sealed record PsycastCase(PsycastAbility Value) : AbilityIntentSource;

    public static implicit operator AbilityIntentSource(PsycastAbility value) => new PsycastCase(value);
}

/// <summary>The <c>target</c> choice of <see cref="AbilityIntent"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AbilityIntentTarget.NoTargetCase), "noTarget")]
[JsonDerivedType(typeof(AbilityIntentTarget.CellCase), "cell")]
[JsonDerivedType(typeof(AbilityIntentTarget.PawnCase), "pawn")]
[JsonDerivedType(typeof(AbilityIntentTarget.ThingCase), "thing")]
public abstract record AbilityIntentTarget
{
    private protected AbilityIntentTarget() { }

    public sealed record NoTargetCase(AbilityNoTarget Value) : AbilityIntentTarget;

    public sealed record CellCase(Common.Cell Value) : AbilityIntentTarget;

    public sealed record PawnCase(string Value) : AbilityIntentTarget;

    public sealed record ThingCase(string Value) : AbilityIntentTarget;
}

/// <summary>
/// The permit source: the faction and RoyalTitlePermitDef defNames. Native
/// guards, in order, named in the refusal text: pawn (living, spawned, free
/// colonist, not downed), held (the pawn holds the permit for the faction),
/// cooldown (FactionPermit not on cooldown), faction (not hostile, not
/// defunct), favor (at least the permit's royalAid favor cost), aid (the
/// worker's own aid options are enabled: map can receive aid, temperature),
/// target (a cell inside the map, not fogged, within royalAid.targetingRange
/// of the pawn; none for a permit that takes no target).
/// </summary>
public sealed record PermitAbility
{
    public string? FactionDef { get; init; }

    public string? Permit { get; init; }
}

/// <summary>
/// The psycast source: the AbilityDef defName of a psycast the pawn
/// knows. Native guards, in order, named in the refusal text: pawn, ability
/// (a defined psycast the pawn knows), cooldown, casting (the pawn is not
/// already ordered to cast it), target (the arm the ability takes: none for a
/// self-cast, else pawn, thing or cell, accepted by the ability's own
/// CanApplyOn and ValidateTarget; no destination or world target), psyfocus
/// (affordable now), entropy (the cast would not overflow neural heat), cast
/// (the game's own Ability.CanCast and GizmoDisabled), range (the game's own
/// Verb.CanHitTarget from where the pawn stands) and confirmation (the game
/// asks for none). Apply queues the cast through the game's own
/// Ability.QueueCastingJob, the order the player's targeter gives.
/// </summary>
public sealed record PsycastAbility
{
    public string? Ability { get; init; }
}

public sealed record AbilityNoTarget;

/// <summary>
/// Set one per-pawn Assign-tab setting on one colony pawn: exactly one arm per intent. hostility_response is the
/// pawn's HostilityResponseMode; native refuses a
/// violence-incapable pawn set to Attack. A setting that already holds
/// applies again. Evidence is a SettingsEffect on the pawn with the arm's
/// field. self_tend is playerSettings.selfTend, refused for a pawn
/// that cannot doctor. nickname names the short name an owned pawn
/// must leave: native draws a fresh name from the pawn's own name bank
/// (never a numbered one) that no other owned pawn holds, and applies
/// unchanged once the pawn no longer holds that name. medicine_carry
/// is the pawn's Medicine inventory-stock count (0-3); native stocks
/// the best medicine the pawn's own medical care allows and refuses a
/// positive count when it allows none. reading_policy is the label
/// of the ReadingPolicy the pawn should hold (its short name; a
/// ReadingPolicyIntent writes the contents), refused when no policy or
/// several carry it. drug_policy is the label of
/// the DrugPolicy the pawn should hold (its short name; a DrugPolicyIntent
/// writes the contents), refused when no policy or several carry it.
/// food_policy is the label of the FoodPolicy the pawn should hold,
/// likewise written by a FoodPolicyIntent and refused when no policy or
/// several carry it. medical_care is
/// the pawn's MedicalCareCategory, any of the five tiers, on a living
/// colonist, slave, prisoner, guest or tame animal of the colony that has
/// medical care settings. mech_work_mode is the MechWorkModeDef the
/// pawn's control group runs, and mech_control_group the index of the
/// overseer's control group the pawn belongs to. Both take a mechanoid the
/// colony owns whose overseer is a living colonist with a mechanitor tracker;
/// native refuses any other pawn, an unknown mode or an index past the
/// overseer's control groups, and reads the group back. choose_permit
/// takes one permit def of one faction def for a colonist of any map through
/// Pawn_RoyaltyTracker.AddPermit after the checks the game's permit window
/// applies (the permit belongs to the faction, the colonist's title reaches its
/// minimum, its prerequisite is held, the faction's permit points cover its
/// cost); native reads the held permit back, UNCHANGED when it was already held.
/// extract_bioferrite is a held entity's extractBioferrite flag, which
/// the game's own Doctor work giver reads to extract; it takes the entity on a
/// holding platform and reads the flag back, UNCHANGED when it already held.
/// Native refuses an entity whose platform has an attached bioferrite harvester
/// (the game forces the flag false) and a true flag before BioferriteExtraction
/// is researched.
/// eat_trigger is the food-bar level (0 to 1) below which a humanlike colony
/// pawn starts seeking food, read by a postfix on JobGiver_GetFood.GetPriority
/// (vanilla 0.30). A value at or above 0.30 clears the pawn's override and
/// restores vanilla; below it the pawn eats later. Native keeps it per pawn in
/// the save and reads it back; UNCHANGED when the effective trigger already held.
/// </summary>
public sealed record PawnSettingsIntent
{
    public string? PawnId { get; init; }

    public PawnSettingsIntentSetting? Setting { get; init; }
}

/// <summary>The <c>setting</c> choice of <see cref="PawnSettingsIntent"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PawnSettingsIntentSetting.HostilityResponseCase), "hostilityResponse")]
[JsonDerivedType(typeof(PawnSettingsIntentSetting.SelfTendCase), "selfTend")]
[JsonDerivedType(typeof(PawnSettingsIntentSetting.ReadingPolicyCase), "readingPolicy")]
[JsonDerivedType(typeof(PawnSettingsIntentSetting.MedicineCarryCase), "medicineCarry")]
[JsonDerivedType(typeof(PawnSettingsIntentSetting.NicknameCase), "nickname")]
[JsonDerivedType(typeof(PawnSettingsIntentSetting.MedicalCareCase), "medicalCare")]
[JsonDerivedType(typeof(PawnSettingsIntentSetting.DrugPolicyCase), "drugPolicy")]
[JsonDerivedType(typeof(PawnSettingsIntentSetting.FoodPolicyCase), "foodPolicy")]
[JsonDerivedType(typeof(PawnSettingsIntentSetting.MechWorkModeCase), "mechWorkMode")]
[JsonDerivedType(typeof(PawnSettingsIntentSetting.MechControlGroupCase), "mechControlGroup")]
[JsonDerivedType(typeof(PawnSettingsIntentSetting.ChoosePermitCase), "choosePermit")]
[JsonDerivedType(typeof(PawnSettingsIntentSetting.ExtractBioferriteCase), "extractBioferrite")]
[JsonDerivedType(typeof(PawnSettingsIntentSetting.EatTriggerCase), "eatTrigger")]
public abstract record PawnSettingsIntentSetting
{
    private protected PawnSettingsIntentSetting() { }

    public sealed record HostilityResponseCase(HostilityResponse Value) : PawnSettingsIntentSetting;

    public sealed record SelfTendCase(bool Value) : PawnSettingsIntentSetting;

    public sealed record ReadingPolicyCase(string Value) : PawnSettingsIntentSetting;

    public sealed record MedicineCarryCase(int Value) : PawnSettingsIntentSetting;

    public sealed record NicknameCase(string Value) : PawnSettingsIntentSetting;

    public sealed record MedicalCareCase(MedicalCare Value) : PawnSettingsIntentSetting;

    public sealed record DrugPolicyCase(string Value) : PawnSettingsIntentSetting;

    public sealed record FoodPolicyCase(string Value) : PawnSettingsIntentSetting;

    public sealed record MechWorkModeCase(string Value) : PawnSettingsIntentSetting;

    public sealed record MechControlGroupCase(int Value) : PawnSettingsIntentSetting;

    public sealed record ChoosePermitCase(PermitChoice Value) : PawnSettingsIntentSetting;

    public sealed record ExtractBioferriteCase(bool Value) : PawnSettingsIntentSetting;

    public sealed record EatTriggerCase(float Value) : PawnSettingsIntentSetting;
}

/// <summary>
/// The faction def and the RoyalTitlePermitDef a choose_permit setting takes.
/// </summary>
public sealed record PermitChoice
{
    public string? FactionDef { get; init; }

    public string? Permit { get; init; }
}

/// <summary>
/// pollution_clear names the game's pollution-clear area
/// (map.areaManager.PollutionClear), the cells the cleanup crew cleans; like
/// home it is exclusive with key and takes set_cells and clear_cells only.
/// rects are inclusive cell rectangles; the area edit applies to their union.
/// </summary>
public sealed record AreaRect
{
    public int? MinX { get; init; }

    public int? MinZ { get; init; }

    public int? MaxX { get; init; }

    public int? MaxZ { get; init; }
}

public sealed record AreaIntent
{
    public AreaOperation? Operation { get; init; }

    public string? Key { get; init; }

    public bool? Home { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<AreaRect>))]
    public EquatableArray<AreaRect> Rects { get; init; }

    public bool? PollutionClear { get; init; }
}

public sealed record PolicyPruneIntent
{
    public PolicyDatabase? Database { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> DeleteIds { get; init; }
}

/// <summary>
/// Write one reading policy's contents: the ReadingPolicy labelled
/// name (made when missing) allows exactly allowed_defs, book ThingDefs
/// (CompProperties_Book), and every book effect. Empty allowed_defs allows
/// no book. Native refuses a name several policies carry. A policy that
/// already matches applies again. PawnSettingsIntent.reading_policy assigns
/// it. Evidence is a SettingsEffect on the policy's load id with the
/// READING_POLICY field.
/// </summary>
public sealed record ReadingPolicyIntent
{
    public string? Name { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> AllowedDefs { get; init; }
}

/// <summary>
/// Write one drug policy's full contents: the DrugPolicy labelled
/// name (made when missing) carries exactly entries; every drug the intent
/// does not name is off (no joy, addiction or scheduled use, nothing
/// carried). An entry names a drug ThingDef the policy lists, at most once.
/// Native refuses a name several policies carry. A policy that already
/// matches applies again. PawnSettingsIntent.drug_policy assigns it.
/// Evidence is a SettingsEffect on the policy's load id with the
/// DRUG_POLICY field.
/// </summary>
public sealed record DrugPolicyEntry
{
    public string? DrugDef { get; init; }

    public bool? AllowedForJoy { get; init; }

    public bool? AllowedForAddiction { get; init; }

    public bool? AllowScheduled { get; init; }

    public float? DaysFrequency { get; init; }

    public float? OnlyIfMoodBelow { get; init; }

    public float? OnlyIfJoyBelow { get; init; }

    public int? TakeToInventory { get; init; }
}

public sealed record DrugPolicyIntent
{
    public string? Name { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<DrugPolicyEntry>))]
    public EquatableArray<DrugPolicyEntry> Entries { get; init; }
}

/// <summary>
/// Write one food policy's contents: the FoodPolicy labelled name
/// (made when missing) allows exactly allowed_defs among the food ThingDefs
/// (PolicyFacts.foods); special filters keep their values. Native refuses a name
/// several policies carry or a definition that is not a food. A policy that
/// already matches applies again. PawnSettingsIntent.food_policy assigns it.
/// Evidence is a SettingsEffect on the policy's load id with the
/// FOOD_RESTRICTION field.
/// </summary>
public sealed record FoodPolicyIntent
{
    public string? Name { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> AllowedDefs { get; init; }
}

/// <summary>
/// Set the game's home-area auto-expand (Find.PlaySettings.autoHomeArea, a
/// save-level setting). Evidence is a SettingsEffect with the
/// AUTO_HOME_AREA field: APPLIED when it changed, UNCHANGED when it already
/// held. The colony read reports the value (UpkeepFacts.auto_home_area).
/// </summary>
public sealed record AutoHomeAreaIntent
{
    public bool? Enabled { get; init; }
}

/// <summary>
/// Draft or undraft one eligible colonist. Drafts are plan-owned: the
/// controller drafts for a plan and undrafts any drafted colonist no live plan
/// needs. A pawn already in the wanted state applies again.
/// </summary>
public sealed record DraftIntent
{
    public string? PawnId { get; init; }

    public bool? Drafted { get; init; }
}

/// <summary>
/// Give one pawn one job: job names a vanilla JobDef, targets the
/// things it acts on in JobDef target order (targetA, targetB, ...). Go
/// chooses the pawn and the targets; native checks the game rules
/// (reservation, reachability, CanDoJob, the job's own preconditions) live
/// and builds the job the game's work giver or float menu builds. A pawn
/// already running that job on those targets applies again. Applied means
/// ordered, not finished; the next census reads the outcome. Evidence is the
/// JobEffect.
/// Repair, Clean, Open (a casket), TendPatient, Equip, Wear, Rescue and
/// Capture: targets [target]. Arrest: targets [target, prisoner bed]; it
/// needs an armed, drafted arrester and a target in an arrestable mental
/// state. AttackMelee: a blunt containment attack on an aggressive
/// colonist (drafting the pawn if it is not), ending once the target is
/// downed or out of the break; drafted combat is CombatOrders.
/// UseItem: targets [item, target pawn]; the target verb of an item the
/// pawn wears or equips (Verb_CastTargetEffect) or the use job of a
/// CompTargetable item, each with its own target checks. The pawn as its
/// own target uses a CompUsable item without a target comp (a
/// neuroformer) through CanBeUsedBy and its use job.
/// FixBrokenDownBuilding, Refuel: targets [colony building]; an undrafted
/// colonist services it with the game's own WorkGiver job (the turret
/// rearm giver included for Refuel), refused once it is fixed or holds a
/// quarter of its target fuel.
/// options.relieve_need with no job offers an undrafted colonist the job its
/// own need giver issues for that deficient need (food, rest, recreation);
/// the giver picks the target, a timetable or medical rest refuses it.
/// options.prioritized is the vanilla "Prioritize" order for any other job
/// (FinishFrame, HaulToContainer to a blueprint or frame, Deconstruct, ...):
/// the first work giver the pawn may do that builds exactly this JobDef on
/// targets[0] with forced set builds it, and the pawn takes it as a
/// player-forced order.
/// </summary>
public sealed record GiveJobOptions
{
    public bool? Prioritized { get; init; }

    public Need? RelieveNeed { get; init; }
}

public sealed record GiveJobIntent
{
    public Common.Ref? Pawn { get; init; }

    public string? Job { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Ref>))]
    public EquatableArray<Common.Ref> Targets { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public EquatableArray<Common.Cell> Cells { get; init; }

    public int? Count { get; init; }

    public GiveJobOptions? Options { get; init; }
}

/// <summary>
/// Place one designation. With thing_id: allow or forbid one exact
/// colony supply item, cut one exact blighted plant (CUT_PLANT) or strip one
/// pawn (STRIP). With target or cell: the removal designations DECONSTRUCT,
/// MINE, CUT_PLANT (chop-wood on a harvestable tree), HAUL and
/// REMOVE_FOUNDATION, through the game's own designator, held by the named
/// guard. target is the thing (cell, when also set, is where it must stand);
/// cell alone names a MINE rock cell or a REMOVE_FOUNDATION cell, whose
/// expected_def is the rock or foundation definition. Native checks the target,
/// the designator and the guard live; the designation already standing (a
/// player one is adopted) or a cell already cleared applies again. Evidence:
/// DeconstructEffect for DECONSTRUCT, ExcavationEffect for MINE, otherwise
/// DesignationEffect. Revoking authority releases every guarded designation.
/// </summary>
public sealed record DesignateIntent
{
    public string? ThingId { get; init; }

    public ThingDesignation? Designation { get; init; }

    public Common.Ref? Target { get; init; }

    public Common.Cell? Cell { get; init; }

    public DesignationGuard? Guard { get; init; }

    public string? ExpectedDef { get; init; }

    /// <summary>
    /// DECONSTRUCT only: swap a 1x1 player door for a Wall of its own
    /// stuff, ordering the nearest capable builder (player-forced work).
    /// </summary>
    public bool? ReplaceWithWall { get; init; }

    /// <summary>
    /// ACQUISITION only: remove the designation instead (the planner's
    /// stall withdraw); a hunter already on the prey is stopped and a plant's
    /// CutPlant goes too. A source that is gone applies.
    /// </summary>
    public bool? Withdraw { get; init; }
}

/// <summary>
/// Move one exact installed player building (or install a packed item, named
/// by its own id or its inner building's) at destination/rotation through the
/// game's reinstall or install blueprint; with uninstall, designate the
/// building for vanilla Uninstall where it stands (destination ignored).
/// Evidence is the InstallationEffect: QUEUED with the blueprint id, or
/// UNINSTALL_QUEUED. A blueprint or designation already standing for the same
/// order applies again.
/// </summary>
public sealed record RelocateIntent
{
    public string? ThingId { get; init; }

    public Common.Cell? Destination { get; init; }

    public Placement.Rotation? Rotation { get; init; }

    public bool? Uninstall { get; init; }
}

/// <summary>
/// One settings change on one exact building: a CompTempControl target,
/// a humanlike bed's medical flag, prisoner use or slave use (for_prisoners and
/// for_slaves drop every owner; only setting it is meaningful), a grower's crop under the game's
/// sow rules, or a claim of a claimable building. Native checks it live; an
/// already-applied change applies again.
/// </summary>
public sealed record BuildingPatchIntent
{
    public string? ThingId { get; init; }

    public BuildingPatchIntentChange? Change { get; init; }
}

/// <summary>The <c>change</c> choice of <see cref="BuildingPatchIntent"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(BuildingPatchIntentChange.TargetTemperatureCase), "targetTemperature")]
[JsonDerivedType(typeof(BuildingPatchIntentChange.MedicalCase), "medical")]
[JsonDerivedType(typeof(BuildingPatchIntentChange.ForPrisonersCase), "forPrisoners")]
[JsonDerivedType(typeof(BuildingPatchIntentChange.PlantDefCase), "plantDef")]
[JsonDerivedType(typeof(BuildingPatchIntentChange.ClaimCase), "claim")]
[JsonDerivedType(typeof(BuildingPatchIntentChange.ForSlavesCase), "forSlaves")]
[JsonDerivedType(typeof(BuildingPatchIntentChange.AutoRefuelCase), "autoRefuel")]
public abstract record BuildingPatchIntentChange
{
    private protected BuildingPatchIntentChange() { }

    public sealed record TargetTemperatureCase(float Value) : BuildingPatchIntentChange;

    public sealed record MedicalCase(bool Value) : BuildingPatchIntentChange;

    public sealed record ForPrisonersCase(Clear Value) : BuildingPatchIntentChange;

    public sealed record PlantDefCase(string Value) : BuildingPatchIntentChange;

    public sealed record ClaimCase(Clear Value) : BuildingPatchIntentChange;

    public sealed record ForSlavesCase(Clear Value) : BuildingPatchIntentChange;

    public sealed record AutoRefuelCase(bool Value) : BuildingPatchIntentChange;
}

/// <summary>
/// Mark cells in the vanilla remove-roof (NoRoof) area: clearance issues it over an
/// enclosed room's roofed cells before deconstructing its walls. Native
/// refuses a fogged or out-of-bounds cell and a thick (overhead mountain)
/// roof; a cell already unroofed or already designated applies again.
/// </summary>
public sealed record RemoveRoofIntent
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public EquatableArray<Common.Cell> Cells { get; init; }
}

/// <summary>
/// Mark cells in the vanilla build-roof (BuildRoof) area, the mirror of RemoveRoofIntent: pawns
/// roof the designated cells that are unroofed, beside a wall or other roof support (the
/// shelter porch holding the first batteries). Native refuses a fogged or out-of-bounds cell
/// and a thick (overhead mountain) roof; a cell already roofed or designated applies again.
/// </summary>
public sealed record BuildRoofIntent
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public EquatableArray<Common.Cell> Cells { get; init; }
}

/// <summary>
/// Order every non-crop plant on cells cut: wild plants get CutPlant,
/// a harvestable tree gets chop-wood (HarvestPlant). Plants in a growing zone,
/// on a plant grower and sown crops are never touched. Native refuses an
/// out-of-bounds or repeated cell; a fogged cell, a cell with nothing to cut
/// and a plant already designated are no-ops.
/// </summary>
public sealed record AreaPlantCutIntent
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public EquatableArray<Common.Cell> Cells { get; init; }
}

public sealed record HusbandryIntent
{
    public string? AnimalId { get; init; }

    public HusbandryOrder? Order { get; init; }

    public string? TrainableDef { get; init; }

    public string? TargetId { get; init; }

    public bool? Follow { get; init; }
}

/// <summary>
/// Add one production bill to a player bench: the recipe, its repeat
/// settings, ingredient filter, corpse class and pinned worker. Native checks
/// the bench, recipe, ingredients and a skilled reaching worker live when it
/// applies; a bench already carrying a matching bill applies again. With
/// replace_owned_bill the named bill (same recipe on this bench, or an
/// ordinary meal tier on this map) is deleted in the same apply.
///
/// A medical bill sets patient instead of bench_id and settings: it
/// queues the recipe on the patient's body part at part_index in the race
/// body's AllParts (absent for a whole-body recipe). Native re-checks the
/// patient, the recipe and the part live and refuses a recipe that is a
/// violation on the patient unless acknowledge_violation is set. Other bills
/// on the patient never block; a recipe whose hediff already stands on the
/// part is refused. Evidence is the SurgeryEffect: QUEUED with the
/// Bill_Medical id; the same recipe and part already queued applies again.
/// Native doctor jobs choose the surgeon; applied means queued, and the
/// health read decides success. surgeon, when set, restricts the bill to
/// that colonist (Bill_Medical pawn restriction).
/// </summary>
public sealed record ProductionBillIntent
{
    public string? BenchId { get; init; }

    public string? RecipeDef { get; init; }

    public BillSettings? Settings { get; init; }

    public Common.Ref? ReplaceOwnedBill { get; init; }

    public Common.Ref? Patient { get; init; }

    public int? PartIndex { get; init; }

    public bool? AcknowledgeViolation { get; init; }

    public Common.Ref? Surgeon { get; init; }
}

/// <summary>
/// Delete one ordinary production bill from a player bench.
/// Native re-resolves the bench and the bill live and refuses when either is
/// gone or the bill is not an ordinary production bill. A pawn working the bill
/// or an unfinished item bound to it does not refuse: the job fails cleanly and
/// the unfinished item stays for any same-recipe bill to resume. With
/// cancel_unfinished the bound unfinished item is destroyed with
/// DestroyMode.Cancel, returning its ingredient share (a recipe change orphans
/// it). Applied evidence is the BillEffect naming the deleted bill; a bill that
/// is already gone is refused, not applied again.
/// </summary>
public sealed record RemoveProductionBillIntent
{
    public string? BenchId { get; init; }

    public Common.Ref? Bill { get; init; }

    public bool CancelUnfinished { get; init; }
}

/// <summary>
/// Write one free colonist's settings: work priorities, allowed area and the
/// 24-hour timetable together, or the medicine ceiling
/// alone. Native
/// checks the pawn and each field live when it applies; settings that
/// already hold apply again.
/// </summary>
public sealed record WorkSettingsIntent
{
    public string? PawnId { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<WorkPriority>))]
    public EquatableArray<WorkPriority> Work { get; init; }

    public Assignment? AllowedArea { get; init; }

    public Schedule? Schedule { get; init; }
}

/// <summary>
/// Assign one free colonist to one assignable thing (a bed, a throne, a
/// sarcophagus: anything with CompAssignableToPawn), replacing the thing of
/// that kind the pawn owned when the planner chose (expected_previous: that
/// thing or clear). Native resolves the thing's CompAssignableToPawn, refuses
/// a thing that has none, and checks the pawn, the thing and that ownership
/// live; a pawn that already owns the thing applies again. swap (beds)
/// evicts the thing's current owners instead of refusing an owned bed.
/// </summary>
public sealed record AssignIntent
{
    public string? PawnId { get; init; }

    public string? ThingId { get; init; }

    public Assignment? ExpectedPrevious { get; init; }

    public bool? Swap { get; init; }
}

/// <summary>
/// Set one colony prisoner's exclusive interaction mode. Native checks the
/// prisoner and the mode's eligibility (recruitable, wild man, Ideology) live;
/// a prisoner already set to the mode applies again.
/// </summary>
public sealed record PrisonerInteractionIntent
{
    public string? PawnId { get; init; }

    public PrisonerInteraction? Interaction { get; init; }
}

public sealed record DialogIntent
{
    public int? WindowId { get; init; }

    public int? OptionIndex { get; init; }

    public string? OptionLabel { get; init; }

    public string? JoinerLetterToken { get; init; }
}

/// <summary>
/// Make the project the ordinary research slot's current project; native
/// refuses a finished, unstartable or anomaly knowledge project, and a
/// project already current is applied again.
/// </summary>
public sealed record ResearchIntent
{
    public string? ProjectDef { get; init; }
}

/// <summary>
/// Create or update the named role apparel policy and assign it to one free
/// colonist; tainted apparel is always excluded and forced or locked apparel
/// is released. A pawn already on a matching policy is applied again.
/// </summary>
public sealed record ApparelPolicyIntent
{
    public string? PawnId { get; init; }

    public string? Name { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> AllowedDefs { get; init; }

    public float? MinHitPoints { get; init; }

    public float? MaxHitPoints { get; init; }

    public int? MinQuality { get; init; }

    public int? MaxQuality { get; init; }
}

/// <summary>
/// Form a caravan of the named home colonists carrying cargo (by definition,
/// journey food included) toward destination_tile, and send it: RimWorld's
/// own formation, whose crew gathers the cargo and walks to the exit before
/// the caravan exists. Native checks the crew, the cargo, at least one
/// colonist left home, the exit route, mass, a day of food and the game's own
/// formation errors when it applies. A crew already forming or already a
/// caravan is applied again. Applied says formation started, not that the
/// caravan left.
/// </summary>
public sealed record FormCaravanIntent
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> PawnIds { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<DefCount>))]
    public EquatableArray<DefCount> Cargo { get; init; }

    public int? DestinationTile { get; init; }
}

/// <summary>
/// Accept one visible quest offer, choosing reward option reward_choice of its
/// single choice part (-1 when it has none) and, when the quest requires one,
/// naming the accepting colonist. Native checks the game's own acceptance
/// rules when it applies; a quest already accepted is applied again.
/// </summary>
public sealed record AcceptQuestIntent
{
    public string? QuestId { get; init; }

    public string? AccepterPawnId { get; init; }

    public int? RewardChoice { get; init; }
}

/// <summary>
/// Loading starts ordinary vanilla pawn work. Launch requires the shuttle's
/// requirements already loaded; the loading order alone never proves departure.
/// </summary>
public sealed record ShuttlePawnList
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> PawnIds { get; init; }
}

public sealed record QuestShuttleIntent
{
    public string? QuestId { get; init; }

    public QuestShuttleIntentLoading? Loading { get; init; }

    public bool? Launch { get; init; }
}

/// <summary>The <c>loading</c> choice of <see cref="QuestShuttleIntent"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(QuestShuttleIntentLoading.AutoloadCase), "autoload")]
[JsonDerivedType(typeof(QuestShuttleIntentLoading.ExplicitPawnsCase), "explicitPawns")]
public abstract record QuestShuttleIntentLoading
{
    private protected QuestShuttleIntentLoading() { }

    public sealed record AutoloadCase(bool Value) : QuestShuttleIntentLoading;

    public sealed record ExplicitPawnsCase(ShuttlePawnList Value) : QuestShuttleIntentLoading;

    public static implicit operator QuestShuttleIntentLoading(ShuttlePawnList value) => new ExplicitPawnsCase(value);
}

/// <summary>
/// Command one ritual: a closed verb set.
/// verb "start" begins a ritual whose lord waits for the player. ritual
/// "bestowing" is the Empire bestowing ceremony of pawn_id (the colonist being
/// bestowed): native finds the waiting bestower and runs the action of its
/// Command_BestowerCeremony gizmo, refusing unless the bestower stands waiting
/// and the game offers the gizmo (the throne room stands). A ritual already
/// started applies again.
/// verb "begin" starts a held ritual precept with no lord waiting, the way the
/// game's begin-ritual dialog does: ritual is the Precept_Ritual load id (the
/// ideology read's ritual id), pawn_id the organizer, spot the ritual's target
/// cell (the game's spotOverride; native takes the target the precept's filters
/// accept there). roles are the exact assignments, one entry per
/// RitualBehaviorDef role slot id (RitualBehaviorDef.roles, RitualRole.id in the catalog's def rows) with the
/// pawns that fill it, and spectator_pawn_ids the pawns that attend without a
/// role (RitualRoleAssignments.assignedRoles and spectators). Native applies
/// the game's own gizmo, assignment and blocking-issue checks and refuses with
/// the game's reason; the ritual's lord job already running applies again.
/// </summary>
public sealed record RitualIntent
{
    public string? PawnId { get; init; }

    public string? Ritual { get; init; }

    public string? Verb { get; init; }

    public Common.Cell? Spot { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<RitualRoleAssignment>))]
    public EquatableArray<RitualRoleAssignment> Roles { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> SpectatorPawnIds { get; init; }
}

public sealed record RitualRoleAssignment
{
    public string? Slot { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> PawnIds { get; init; }
}

/// <summary>
/// Order one alive, spawned, drafted pawn to walk to one standable,
/// reachable cell. Applied means the order was given, or already matched (the
/// pawn stands on the cell or is walking there); arrival is not reported.
/// </summary>
public sealed record MoveIntent
{
    public string? PawnId { get; init; }

    public Common.Cell? Destination { get; init; }
}

/// <summary>
/// Order one colonist to haul one loose item to the storage the game's own
/// hauling WorkGiver picks. Native refuses when the pawn cannot take the job
/// or the item is gone; a pawn already hauling the item is applied again.
/// </summary>
public sealed record HaulIntent
{
    public string? PawnId { get; init; }

    public string? ThingId { get; init; }
}

/// <summary>
/// Place one ordinary blueprint (or an instant building). Native validates the
/// placement against live state and refuses with the placement reason; a
/// matching blueprint, frame or building already on the cell counts as
/// applied. Go reads what stands from the building census by definition,
/// stuff, anchor and rotation; nothing is stamped or saved.
/// </summary>
public sealed record BuildingIntent
{
    public Placement.PlacementCandidate? Placement { get; init; }

    public int? MinimumFinishingSkill { get; init; }

    /// <summary>
    /// Adoption changes only this exact pending target; never places a replacement.
    /// </summary>
    public string? ExistingTargetId { get; init; }

    /// <summary>
    /// Construction tier 0-5 (#2522); absent = ungated. With existing_target_id it re-tiers
    /// that exact blueprint or frame; the tier stays mutable while the finishing floor is write-once.
    /// </summary>
    public int? Tier { get; init; }

    /// <summary>
    /// Replace the built wall at the cell in place (#2529): a vanilla replacement
    /// blueprint over it, the cell impassable at every tick of the swap. Native
    /// refuses with the wall-replacement failure codes; exclusive of existing_target_id.
    /// </summary>
    public bool? ReplaceWall { get; init; }
}

/// <summary>
/// One step against RimWorld's single live trade session, named by its
/// trader and negotiator.
/// </summary>
public sealed record TradeIntent
{
    public Common.TradeTarget? Target { get; init; }

    public string? NegotiatorId { get; init; }

    public TradeIntentStep? Step { get; init; }
}

/// <summary>The <c>step</c> choice of <see cref="TradeIntent"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TradeIntentStep.OpenCase), "open")]
[JsonDerivedType(typeof(TradeIntentStep.SetLinesCase), "setLines")]
[JsonDerivedType(typeof(TradeIntentStep.AcceptCase), "accept")]
[JsonDerivedType(typeof(TradeIntentStep.EndCase), "end")]
public abstract record TradeIntentStep
{
    private protected TradeIntentStep() { }

    public sealed record OpenCase(OpenTrade Value) : TradeIntentStep;

    public static implicit operator TradeIntentStep(OpenTrade value) => new OpenCase(value);

    public sealed record SetLinesCase(SetTradeLines Value) : TradeIntentStep;

    public static implicit operator TradeIntentStep(SetTradeLines value) => new SetLinesCase(value);

    public sealed record AcceptCase(AcceptTrade Value) : TradeIntentStep;

    public static implicit operator TradeIntentStep(AcceptTrade value) => new AcceptCase(value);

    public sealed record EndCase(EndTrade Value) : TradeIntentStep;

    public static implicit operator TradeIntentStep(EndTrade value) => new EndCase(value);
}

/// <summary>
/// Queues ordinary comms work; queued is distinct from a paid request.
/// Hands reconciles the faction's request tick before any retry.
/// </summary>
public sealed record CommsTradeRequestIntent
{
    public Common.TradeRequestKind Kind { get; init; }

    public string? FactionId { get; init; }

    public string? TraderKind { get; init; }

    public string? ConsoleId { get; init; }

    public string? NegotiatorId { get; init; }

    public long? ExpectedLastRequestTick { get; init; }
}

public sealed record Refusal
{
    public Common.FailureCode? Code { get; init; }

    public string? Reason { get; init; }

    public Common.RefusalClass? RefusalClass { get; init; }
}

public sealed record ActionResult
{
    public string? Key { get; init; }

    public ActionResultOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="ActionResult"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ActionResultOutcome.AppliedCase), "applied")]
[JsonDerivedType(typeof(ActionResultOutcome.RefusedCase), "refused")]
[JsonDerivedType(typeof(ActionResultOutcome.FailedCase), "failed")]
public abstract record ActionResultOutcome
{
    private protected ActionResultOutcome() { }

    public sealed record AppliedCase(Receipts.Receipt Value) : ActionResultOutcome;

    public static implicit operator ActionResultOutcome(Receipts.Receipt value) => new AppliedCase(value);

    public sealed record RefusedCase(Refusal Value) : ActionResultOutcome;

    public static implicit operator ActionResultOutcome(Refusal value) => new RefusedCase(value);

    public sealed record FailedCase(Common.Failure Value) : ActionResultOutcome;

    public static implicit operator ActionResultOutcome(Common.Failure value) => new FailedCase(value);
}

/// <summary>
/// batch_failure means no action in the batch was applied.
/// </summary>
public sealed record ApplyReply
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<ActionResult>))]
    public EquatableArray<ActionResult> Results { get; init; }

    public Common.Failure? BatchFailure { get; init; }
}

/// <summary>
/// job is a JobDef name (v1: Hunt). radius is Go-authored, in cells around the
/// actor. The selector skips prey that fail TARGET_AVAILABLE's checks; with no
/// target the rule does not fire.
/// </summary>
public sealed record Rule
{
    public string? Id { get; init; }

    public RuleTrigger? Trigger { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<RulePredicate>))]
    public EquatableArray<RulePredicate> Predicates { get; init; }

    public RuleAction? Action { get; init; }

    public string? Job { get; init; }

    public RuleTargetSelector? Target { get; init; }

    public uint? Radius { get; init; }

    /// <summary>
    /// Cells from a wild predator a hunt route must stay outside; finite, &gt;= 0.
    /// </summary>
    public float? PredatorMarginCells { get; init; }
}

public sealed record RuleRefusal
{
    public string? RuleId { get; init; }

    public RuleRefusalReason? Reason { get; init; }
}

/// <summary>
/// Replace every active rule with the accepted ones (idempotent by id) and set
/// the lease: when the game tick reaches expires_at_tick native deactivates
/// all rules and emits one RuleLeaseExpired. A lease already past is a failure.
/// </summary>
public sealed record RulesAttachRequest
{
    public Common.Identity? Identity { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Rule>))]
    public EquatableArray<Rule> Rules { get; init; }

    public long? ExpiresAtTick { get; init; }
}

public sealed record RulesAttached
{
    public Common.ObservationContext? Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> AcceptedIds { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<RuleRefusal>))]
    public EquatableArray<RuleRefusal> Refused { get; init; }
}

/// <summary>
/// Rules attach as a journaled intent: the same replace-all as Rules/Attach,
/// with the lease relative to the apply tick (expiry = tick + lease_ticks) so a
/// resent key never carries a stale absolute tick. An empty rules list clears. Any
/// refused rule refuses the whole action and changes nothing. Evidence is a
/// SettingsEffect with the RULES field: APPLIED.
/// </summary>
public sealed record RulesAttachIntent
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<Rule>))]
    public EquatableArray<Rule> Rules { get; init; }

    public long? LeaseTicks { get; init; }
}

public sealed record RulesAttachReply
{
    public RulesAttachReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="RulesAttachReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RulesAttachReplyOutcome.AttachedCase), "attached")]
[JsonDerivedType(typeof(RulesAttachReplyOutcome.FailureCase), "failure")]
public abstract record RulesAttachReplyOutcome
{
    private protected RulesAttachReplyOutcome() { }

    public sealed record AttachedCase(RulesAttached Value) : RulesAttachReplyOutcome;

    public static implicit operator RulesAttachReplyOutcome(RulesAttached value) => new AttachedCase(value);

    public sealed record FailureCase(Common.Failure Value) : RulesAttachReplyOutcome;

    public static implicit operator RulesAttachReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record RulesClearRequest
{
    public Common.Identity? Identity { get; init; }
}

public sealed record RulesCleared
{
    public Common.ObservationContext? Context { get; init; }

    public uint? Cleared { get; init; }
}

public sealed record RulesClearReply
{
    public RulesClearReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="RulesClearReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RulesClearReplyOutcome.ClearedCase), "cleared")]
[JsonDerivedType(typeof(RulesClearReplyOutcome.FailureCase), "failure")]
public abstract record RulesClearReplyOutcome
{
    private protected RulesClearReplyOutcome() { }

    public sealed record ClearedCase(RulesCleared Value) : RulesClearReplyOutcome;

    public static implicit operator RulesClearReplyOutcome(RulesCleared value) => new ClearedCase(value);

    public sealed record FailureCase(Common.Failure Value) : RulesClearReplyOutcome;

    public static implicit operator RulesClearReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record RulesStatusRequest
{
    public Common.Identity? Identity { get; init; }
}

/// <summary>
/// One active rule: its firings since attach and the last one.
/// </summary>
public sealed record RuleStatus
{
    public string? RuleId { get; init; }

    public ulong? FiringCount { get; init; }

    public long? LastFiredTick { get; init; }

    public string? LastActorId { get; init; }

    public string? LastTargetId { get; init; }
}

/// <summary>
/// rules is empty once the lease expired or the rules were cleared.
/// lease_remaining_ticks is 0 with no active rule; lease_expired holds from the
/// expiry until the next attach or clear.
/// </summary>
public sealed record RulesStatus
{
    public Common.ObservationContext? Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<RuleStatus>))]
    public EquatableArray<RuleStatus> Rules { get; init; }

    public long? ExpiresAtTick { get; init; }

    public long? LeaseRemainingTicks { get; init; }

    public bool? LeaseExpired { get; init; }
}

public sealed record RulesStatusReply
{
    public RulesStatusReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="RulesStatusReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RulesStatusReplyOutcome.StatusCase), "status")]
[JsonDerivedType(typeof(RulesStatusReplyOutcome.FailureCase), "failure")]
public abstract record RulesStatusReplyOutcome
{
    private protected RulesStatusReplyOutcome() { }

    public sealed record StatusCase(RulesStatus Value) : RulesStatusReplyOutcome;

    public static implicit operator RulesStatusReplyOutcome(RulesStatus value) => new StatusCase(value);

    public sealed record FailureCase(Common.Failure Value) : RulesStatusReplyOutcome;

    public static implicit operator RulesStatusReplyOutcome(Common.Failure value) => new FailureCase(value);
}

/// <summary>
/// Starts one vanilla (non-Ideology) gathering. The game picks the spot, which a
/// built PartySpot controls; the intent carries none.
/// </summary>
public sealed record GatheringIntent
{
    public string? GatheringDef { get; init; }

    public Common.Ref? Organizer { get; init; }
}

