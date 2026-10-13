using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Contracts.Wire.Mirror;

/// <summary>
/// Which side a combat pawn fights on.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<CombatSide>))]
public enum CombatSide
{
    [WireName("COMBAT_SIDE_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("COMBAT_SIDE_COLONIST")]
    Colonist = 1,
    [WireName("COMBAT_SIDE_HOSTILE")]
    Hostile = 2,
    [WireName("COMBAT_SIDE_COLONY_ANIMAL")]
    ColonyAnimal = 3,
    /// <summary>
    /// a prisoner of the colony breaking out
    /// </summary>
    [WireName("COMBAT_SIDE_PRISONER")]
    Prisoner = 4,
    /// <summary>
    /// a wild predator or large animal near a hostile
    /// </summary>
    [WireName("COMBAT_SIDE_WILD_ANIMAL")]
    WildAnimal = 5,
}

/// <summary>
/// What a combat pawn's stance is doing.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<CombatStance>))]
public enum CombatStance
{
    [WireName("COMBAT_STANCE_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("COMBAT_STANCE_IDLE")]
    Idle = 1,
    /// <summary>
    /// aiming
    /// </summary>
    [WireName("COMBAT_STANCE_WARMUP")]
    Warmup = 2,
    /// <summary>
    /// after a shot or blow
    /// </summary>
    [WireName("COMBAT_STANCE_COOLDOWN")]
    Cooldown = 3,
    [WireName("COMBAT_STANCE_MOVING")]
    Moving = 4,
    /// <summary>
    /// in a melee job
    /// </summary>
    [WireName("COMBAT_STANCE_MELEE")]
    Melee = 5,
}

/// <summary>
/// What a combat event row records. The combat stop kinds and the non-stops.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<CombatLogKind>))]
public enum CombatLogKind
{
    [WireName("COMBAT_LOG_KIND_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("COMBAT_LOG_KIND_DOWNED")]
    Downed = 1,
    [WireName("COMBAT_LOG_KIND_KILLED")]
    Killed = 2,
    /// <summary>
    /// detail carries the damage
    /// </summary>
    [WireName("COMBAT_LOG_KIND_DAMAGED")]
    Damaged = 3,
    [WireName("COMBAT_LOG_KIND_SHIELD_BROKEN")]
    ShieldBroken = 4,
    [WireName("COMBAT_LOG_KIND_SHOT_FIRED")]
    ShotFired = 5,
    [WireName("COMBAT_LOG_KIND_PROJECTILE_LAUNCHED")]
    ProjectileLaunched = 6,
    [WireName("COMBAT_LOG_KIND_EXPLOSION")]
    Explosion = 7,
    [WireName("COMBAT_LOG_KIND_FIRE_STARTED")]
    FireStarted = 8,
    [WireName("COMBAT_LOG_KIND_DOOR_OPENED")]
    DoorOpened = 9,
    [WireName("COMBAT_LOG_KIND_DOOR_CLOSED")]
    DoorClosed = 10,
    [WireName("COMBAT_LOG_KIND_BUILDING_DESTROYED")]
    BuildingDestroyed = 11,
    /// <summary>
    /// detail "&lt;from&gt; -&gt; &lt;to&gt;", raid_strategy set
    /// </summary>
    [WireName("COMBAT_LOG_KIND_LORD_TOIL")]
    LordToil = 12,
    [WireName("COMBAT_LOG_KIND_MENTAL_STATE")]
    MentalState = 13,
    [WireName("COMBAT_LOG_KIND_HOSTILE_ARRIVED")]
    HostileArrived = 14,
}

/// <summary>
/// What a thing is. Spawned pawns are never listed.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<ThingCategory>))]
public enum ThingCategory
{
    /// <summary>
    /// refused
    /// </summary>
    [WireName("THING_CATEGORY_UNSPECIFIED")]
    Unspecified = 0,
    /// <summary>
    /// state: item
    /// </summary>
    [WireName("THING_CATEGORY_ITEM")]
    Item = 1,
    /// <summary>
    /// state: building (blueprints and frames too, by flag)
    /// </summary>
    [WireName("THING_CATEGORY_BUILDING")]
    Building = 2,
    /// <summary>
    /// state: plant
    /// </summary>
    [WireName("THING_CATEGORY_PLANT")]
    Plant = 3,
    /// <summary>
    /// state: filth
    /// </summary>
    [WireName("THING_CATEGORY_FILTH")]
    Filth = 4,
    /// <summary>
    /// state: corpse
    /// </summary>
    [WireName("THING_CATEGORY_CORPSE")]
    Corpse = 5,
    /// <summary>
    /// no state
    /// </summary>
    [WireName("THING_CATEGORY_OTHER")]
    Other = 6,
}

/// <summary>
/// Whose a thing is.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<ThingFaction>))]
public enum ThingFaction
{
    /// <summary>
    /// unowned
    /// </summary>
    [WireName("THING_FACTION_NONE")]
    None = 0,
    [WireName("THING_FACTION_PLAYER")]
    Player = 1,
    [WireName("THING_FACTION_NEUTRAL")]
    Neutral = 2,
    [WireName("THING_FACTION_HOSTILE")]
    Hostile = 3,
}

/// <summary>
/// Thing.flags bits (a value is a mask; any other bit is refused).
/// </summary>
[JsonConverter(typeof(WireEnumConverter<ThingFlagBit>))]
public enum ThingFlagBit
{
    [WireName("THING_FLAG_NONE")]
    None = 0,
    [WireName("THING_FLAG_EDIFICE")]
    Edifice = 1,
    [WireName("THING_FLAG_BLUEPRINT")]
    Blueprint = 2,
    [WireName("THING_FLAG_FRAME")]
    Frame = 4,
    [WireName("THING_FLAG_IMPASSABLE")]
    Impassable = 8,
    [WireName("THING_FLAG_HOLDS_ROOF")]
    HoldsRoof = 16,
    [WireName("THING_FLAG_DECONSTRUCTIBLE")]
    Deconstructible = 32,
    [WireName("THING_FLAG_MINIFIABLE")]
    Minifiable = 64,
    [WireName("THING_FLAG_CLAIMABLE")]
    Claimable = 128,
    [WireName("THING_FLAG_DESIGNATED")]
    Designated = 256,
    [WireName("THING_FLAG_FORBIDDEN")]
    Forbidden = 512,
    [WireName("THING_FLAG_HAULABLE")]
    Haulable = 1024,
    [WireName("THING_FLAG_ANCIENT_DANGER")]
    AncientDanger = 2048,
    [WireName("THING_FLAG_NATURAL_ROCK")]
    NaturalRock = 4096,
}

[JsonConverter(typeof(WireEnumConverter<CorpseClass>))]
public enum CorpseClass
{
    [WireName("CORPSE_CLASS_OTHER")]
    Other = 0,
    [WireName("CORPSE_CLASS_HUMANLIKE")]
    Humanlike = 1,
    [WireName("CORPSE_CLASS_ANIMAL")]
    Animal = 2,
}

/// <summary>
/// One combat pawn row (BundleSnapshot.combat_pawns). Numbers are thresholded so a
/// row only changes when a decision could: fractions to 0.01, bleed and pain
/// to 0.05, move speed to 0.1 cells/s.
/// </summary>
public sealed record CombatPawn
{
    /// <summary>
    /// load id
    /// </summary>
    public string? Id { get; init; }

    public CombatSide? Side { get; init; }

    public Common.Ref? Faction { get; init; }

    public string? LordId { get; init; }

    public Common.Cell? Cell { get; init; }

    public bool? Downed { get; init; }

    public bool? Dead { get; init; }

    /// <summary>
    /// def name, absent when none
    /// </summary>
    public string? MentalState { get; init; }

    public bool? Drafted { get; init; }

    /// <summary>
    /// drafted colonists
    /// </summary>
    public Operations.CombatFireMode? FireMode { get; init; }

    /// <summary>
    /// current job def
    /// </summary>
    public string? Job { get; init; }

    /// <summary>
    /// current job's target thing
    /// </summary>
    public string? TargetId { get; init; }

    public CombatStance? Stance { get; init; }

    /// <summary>
    /// warmup or cooldown ticks remaining
    /// </summary>
    public int? StanceTicksLeft { get; init; }

    /// <summary>
    /// summary health fraction
    /// </summary>
    public double? Health { get; init; }

    /// <summary>
    /// per day
    /// </summary>
    public double? BleedRate { get; init; }

    public double? Pain { get; init; }

    /// <summary>
    /// cells per second
    /// </summary>
    public double? MoveSpeed { get; init; }

    /// <summary>
    /// fraction of max, absent without a shield
    /// </summary>
    public double? ShieldEnergy { get; init; }

    public bool? ShieldBroken { get; init; }

    /// <summary>
    /// def name, absent when unarmed
    /// </summary>
    public string? Weapon { get; init; }

    public int? WeaponWarmupTicks { get; init; }

    public int? WeaponCooldownTicks { get; init; }

    /// <summary>
    /// Colonists: worn sharp armor, the stronger of natural armor and
    /// the best worn layer (the raid_armor rule per pawn), to 0.05.
    /// </summary>
    public double? Armor { get; init; }

    /// <summary>
    /// The watermark of the row's last change: when events concerned the pawn
    /// since the last compare, the newest of the highest rank (downing or
    /// death, then another combat stop kind, then any), the matching combat_events
    /// row's; otherwise the capture's. Not part of the change comparison.
    /// </summary>
    public Watermark? Changed { get; init; }

    /// <summary>
    /// Rescuer choice: a worn CompShield apparel (shield belt), and the
    /// Medicine skill level (absent without skills).
    /// </summary>
    public bool? ShieldBelt { get; init; }

    public int? MedicalSkill { get; init; }

    /// <summary>
    /// Melee strength: the MeleeDPS stat, to 0.1.
    /// </summary>
    public double? MeleePower { get; init; }

    /// <summary>
    /// Enemy drugs: a GoJuiceHigh hediff (the pawn fights on past
    /// the pain that would down it) and a LuciferiumAddiction hediff.
    /// </summary>
    public bool? GoJuiceHigh { get; init; }

    public bool? LuciferiumAddicted { get; init; }

    /// <summary>
    /// EMP timing: the stun's ticks left, rounded up to 30, absent when
    /// not stunned.
    /// </summary>
    public int? StunTicksLeft { get; init; }

    /// <summary>
    /// The job target is a mortar, or a frame that will build one:
    /// a turret whose verb fires mortar shells.
    /// </summary>
    public bool? TargetMortar { get; init; }

    /// <summary>
    /// Present after a complete inventory scan; absent means unknown.
    /// </summary>
    public CarriedDrugs? CarriedDrugs { get; init; }
}

/// <summary>
/// Distinct drug ThingDefs with positive stack counts in this pawn's inventory.
/// A present empty message establishes that the pawn carries no drugs.
/// </summary>
public sealed record CarriedDrugs
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Defs { get; init; }
}

/// <summary>
/// A damaged player door (BundleSnapshot.combat_doors): hit points
/// below max.
/// </summary>
public sealed record CombatDoorRow
{
    /// <summary>
    /// load id
    /// </summary>
    public string? Id { get; init; }

    public Common.Cell? Cell { get; init; }

    public int? HitPoints { get; init; }

    public int? MaxHitPoints { get; init; }
}

/// <summary>
/// A player mortar (BundleSnapshot.combat_mortars) and its range.
/// </summary>
public sealed record CombatMortarRow
{
    /// <summary>
    /// load id
    /// </summary>
    public string? Id { get; init; }

    public Common.Cell? Cell { get; init; }

    public float? MinRange { get; init; }

    public float? MaxRange { get; init; }

    /// <summary>
    /// The loaded shell's ThingDef; absent when unloaded.
    /// </summary>
    public string? LoadedShell { get; init; }
}

/// <summary>
/// One combat event row (BundleSnapshot.combat_events).
/// </summary>
public sealed record CombatEventRow
{
    /// <summary>
    /// the event's own mark; the row id is "&lt;tick&gt;.&lt;seq&gt;"
    /// </summary>
    public Watermark? At { get; init; }

    public CombatLogKind? Kind { get; init; }

    /// <summary>
    /// The combat stop kind this event is, armed or not; unset for non-stops.
    /// </summary>
    public Clock.CombatEvent? Stop { get; init; }

    /// <summary>
    /// the actor or subject
    /// </summary>
    public string? ThingId { get; init; }

    /// <summary>
    /// the victim, target or instigator
    /// </summary>
    public string? TargetId { get; init; }

    /// <summary>
    /// weapon, projectile, building or state def
    /// </summary>
    public string? DefName { get; init; }

    public Common.Cell? Cell { get; init; }

    public string? Detail { get; init; }

    /// <summary>
    /// Lord toil rows: the raid strategy def. Hostile arrived rows from a
    /// drop-pod arrival mode: "pods".
    /// </summary>
    public string? RaidStrategy { get; init; }

    /// <summary>
    /// Drop-pod arrival rows: the tick the last pod opens and the
    /// cells the pods land on.
    /// </summary>
    public int? OpenTick { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public EquatableArray<Common.Cell> LandingCells { get; init; }
}

/// <summary>
/// rimgovernor/combat_geometry: an on-demand read, not mirrored, that
/// answers DecideCombat's geometry questions at a stop by the game's own
/// rules. Caps: at most MaxGeometryCells cells and MaxGeometryHostiles
/// hostiles (the native's CombatGeometry constants, bridge.CombatGeometry*),
/// measured on lab-ranged under the 50 ms main-thread budget.
/// </summary>
public sealed record CombatGeometryRequest
{
    /// <summary>
    /// required
    /// </summary>
    public Common.Identity? Identity { get; init; }

    /// <summary>
    /// 1..cap, distinct, in bounds; 0..cap with propose. Named plus proposed
    /// cells together score at most the cells cap.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public EquatableArray<Common.Cell> Cells { get; init; }

    /// <summary>
    /// 1..cap, distinct pawn load ids; 0..cap for named cells alone (their
    /// standability), with no propose and no pawn.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> HostileIds { get; init; }

    /// <summary>
    /// optional: path ticks for this pawn to each cell
    /// </summary>
    public string? PawnId { get; init; }

    /// <summary>
    /// Optional: the native proposes candidate cells for one role and
    /// scores them like named cells, in the same read.
    /// </summary>
    public CombatGeometryPropose? Propose { get; init; }
}

/// <summary>
/// A role and its anchor. Every proposal is a standable, in-bounds
/// cell not among the named cells, scored against the request's hostiles,
/// ranked best first, at most the cells cap minus the named cells (so a
/// propose request names at most cap-1 cells). Anchor cells are in bounds.
/// </summary>
public sealed record CombatGeometryPropose
{
    public CombatGeometryProposeRole? Role { get; init; }
}

/// <summary>The <c>role</c> choice of <see cref="CombatGeometryPropose"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CombatGeometryProposeRole.CoverBehindLineCase), "coverBehindLine")]
[JsonDerivedType(typeof(CombatGeometryProposeRole.AdjacentToChokeCase), "adjacentToChoke")]
[JsonDerivedType(typeof(CombatGeometryProposeRole.FiringCellsCase), "firingCells")]
[JsonDerivedType(typeof(CombatGeometryProposeRole.RescuePathCase), "rescuePath")]
public abstract record CombatGeometryProposeRole
{
    private protected CombatGeometryProposeRole() { }

    public sealed record CoverBehindLineCase(CombatCoverBehindLine Value) : CombatGeometryProposeRole;

    public static implicit operator CombatGeometryProposeRole(CombatCoverBehindLine value) => new CoverBehindLineCase(value);

    public sealed record AdjacentToChokeCase(CombatAdjacentToChoke Value) : CombatGeometryProposeRole;

    public static implicit operator CombatGeometryProposeRole(CombatAdjacentToChoke value) => new AdjacentToChokeCase(value);

    public sealed record FiringCellsCase(CombatFiringCells Value) : CombatGeometryProposeRole;

    public static implicit operator CombatGeometryProposeRole(CombatFiringCells value) => new FiringCellsCase(value);

    public sealed record RescuePathCase(CombatRescuePath Value) : CombatGeometryProposeRole;

    public static implicit operator CombatGeometryProposeRole(CombatRescuePath value) => new RescuePathCase(value);
}

/// <summary>
/// The request pawn's route to `to`, from the game pathfinder
/// (TraverseParms.For(pawn, Danger.Deadly), touch end), in walking order,
/// start cell excluded; each cell scored and flagged hostile_line_of_fire
/// and door. Needs pawn_id. Not ranked and not filtered by standable or the
/// named cells; a route longer than the cap is cut, so the last cell is
/// then not next to `to`. No path: no proposals.
/// </summary>
public sealed record CombatRescuePath
{
    /// <summary>
    /// required: the downed pawn's cell
    /// </summary>
    public Common.Cell? To { get; init; }
}

/// <summary>
/// Cells within one step (8-way) of the line, not on it, whose best cover
/// against the named hostiles (CoverUtility) is above zero; ranked by that
/// cover, then by distance to the line.
/// </summary>
public sealed record CombatCoverBehindLine
{
    /// <summary>
    /// 1..cells cap, distinct
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public EquatableArray<Common.Cell> Line { get; init; }
}

/// <summary>
/// The choke's 8-way neighbours strictly nearer our_side than the choke;
/// ranked by distance to our_side.
/// </summary>
public sealed record CombatAdjacentToChoke
{
    /// <summary>
    /// required
    /// </summary>
    public Common.Cell? Choke { get; init; }

    /// <summary>
    /// required: any cell on our side
    /// </summary>
    public Common.Cell? OurSide { get; init; }
}

/// <summary>
/// Cells within radius of from with line of fire (GenSight) to at least
/// one target; ranked by targets in sight, then best cover, then distance
/// to from.
/// </summary>
public sealed record CombatFiringCells
{
    /// <summary>
    /// 1..hostiles cap, distinct
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public EquatableArray<Common.Cell> Targets { get; init; }

    /// <summary>
    /// required
    /// </summary>
    public Common.Cell? From { get; init; }

    /// <summary>
    /// required, 1..12
    /// </summary>
    public int? Radius { get; init; }
}

/// <summary>
/// One (cell, hostile) pair.
/// </summary>
public sealed record CombatSightLine
{
    public string? HostileId { get; init; }

    /// <summary>
    /// CoverUtility.CalculateOverallBlockChance at the cell against a shot
    /// from the hostile's cell, 0..1.
    /// </summary>
    public double? Cover { get; init; }

    /// <summary>
    /// A shooter at the cell can hit the hostile (a verb shoot line exists).
    /// </summary>
    public bool? LineOfFire { get; init; }

    /// <summary>
    /// A colonist stands on the shoot line between them.
    /// </summary>
    public bool? ColonistInPath { get; init; }

    /// <summary>
    /// CoverUtility.CalculateOverallBlockChance at the hostile's cell against
    /// a shot from the cell, 0..1: the cover the hostile keeps against a
    /// shooter here.
    /// </summary>
    public double? HostileCover { get; init; }
}

public sealed record CombatGeometryCell
{
    public Common.Cell? Cell { get; init; }

    /// <summary>
    /// request hostile order
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<CombatSightLine>))]
    public EquatableArray<CombatSightLine> Lines { get; init; }

    /// <summary>
    /// Path ticks for the request's pawn; absent without a pawn or a path.
    /// </summary>
    public int? PathTicks { get; init; }

    public bool? Standable { get; init; }

    /// <summary>
    /// Any request hostile has line of fire to the cell.
    /// </summary>
    public bool? HostileLineOfFire { get; init; }

    /// <summary>
    /// A player door stands on the cell.
    /// </summary>
    public bool? Door { get; init; }
}

public sealed record CombatGeometry
{
    public Common.ObservationContext? Context { get; init; }

    /// <summary>
    /// request cell order
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<CombatGeometryCell>))]
    public EquatableArray<CombatGeometryCell> Cells { get; init; }

    /// <summary>
    /// the read's own main-thread time
    /// </summary>
    public double? MainThreadMs { get; init; }

    /// <summary>
    /// The propose block's cells, ranked best first, scored like
    /// cells; empty without propose.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<CombatGeometryCell>))]
    public EquatableArray<CombatGeometryCell> Proposed { get; init; }
}

public sealed record CombatGeometryReply
{
    public CombatGeometryReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="CombatGeometryReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CombatGeometryReplyOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(CombatGeometryReplyOutcome.FailureCase), "failure")]
public abstract record CombatGeometryReplyOutcome
{
    private protected CombatGeometryReplyOutcome() { }

    public sealed record ObservedCase(CombatGeometry Value) : CombatGeometryReplyOutcome;

    public static implicit operator CombatGeometryReplyOutcome(CombatGeometry value) => new ObservedCase(value);

    public sealed record FailureCase(Common.Failure Value) : CombatGeometryReplyOutcome;

    public static implicit operator CombatGeometryReplyOutcome(Common.Failure value) => new FailureCase(value);
}

/// <summary>
/// A combat row's watermark: the tick, and the event sequence within it.
/// </summary>
public sealed record Watermark
{
    public long? Tick { get; init; }

    public uint? Seq { get; init; }
}

/// <summary>
/// The whole-map cell grid: native sends it in every snapshot frame
/// (BundleSnapshot.grid); the Go snapshot recorder stores planning_cells
/// section lines in it too.
/// </summary>
public sealed record CellRect
{
    public int? X { get; init; }

    public int? Z { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }
}

/// <summary>
/// One field's array over a CellGrid's rect, row-major (index
/// (z - rect.z) * rect.width + x - rect.x). Dense forms hold every cell;
/// sparse holds (index, value) pairs over a base: in a keyframe the
/// all-sentinel array, in a delta the keyframe's array. The sender picks
/// whichever form is smaller.
/// </summary>
public sealed record FieldArray
{
    public FieldArrayForm? Form { get; init; }
}

/// <summary>The <c>form</c> choice of <see cref="FieldArray"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(FieldArrayForm.CodesCase), "codes")]
[JsonDerivedType(typeof(FieldArrayForm.IndexesCase), "indexes")]
[JsonDerivedType(typeof(FieldArrayForm.NumbersCase), "numbers")]
[JsonDerivedType(typeof(FieldArrayForm.SparseCase), "sparse")]
public abstract record FieldArrayForm
{
    private protected FieldArrayForm() { }

    /// <summary>
    /// presence and Fact[bool] fields, one byte per cell
    /// </summary>
    public sealed record CodesCase(byte[] Value) : FieldArrayForm;

    /// <summary>
    /// string fields: string table indexes
    /// </summary>
    public sealed record IndexesCase(PackedUint32 Value) : FieldArrayForm;

    public static implicit operator FieldArrayForm(PackedUint32 value) => new IndexesCase(value);

    /// <summary>
    /// Fact[float64] fields
    /// </summary>
    public sealed record NumbersCase(PackedDouble Value) : FieldArrayForm;

    public static implicit operator FieldArrayForm(PackedDouble value) => new NumbersCase(value);

    public sealed record SparseCase(SparseArray Value) : FieldArrayForm;

    public static implicit operator FieldArrayForm(SparseArray value) => new SparseCase(value);
}

public sealed record PackedUint32
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<uint>))]
    public EquatableArray<uint> Values { get; init; }
}

public sealed record PackedDouble
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<double>))]
    public EquatableArray<double> Values { get; init; }
}

/// <summary>
/// index ascending and distinct; value in code for code and index fields, in
/// number for number fields, one per index.
/// </summary>
public sealed record SparseArray
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<uint>))]
    public EquatableArray<uint> Index { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<uint>))]
    public EquatableArray<uint> Code { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<double>))]
    public EquatableArray<double> Number { get; init; }
}

/// <summary>
/// A cell grid: one array per policy.SiteCell field over
/// rect. Sentinels mark a cell the grid does not hold (fogged, or outside
/// the read) and an unknown fact:
/// cell: 0 not held, 1 held;
/// Fact[bool] codes: 0 unknown, 1 false, 2 true;
/// Fact[float64] numbers: NaN unknown (native values are finite);
/// Fact[string] indexes: 0 unknown, k for strings[k-1].
/// A keyframe carries every array; a delta, on the same rect, only the
/// arrays that differ from its keyframe's (deltas are cumulative against
/// the keyframe, never chained), and never every array: a frame whose grid
/// carries every array is a keyframe. strings is this grid's own table.
/// glow is artificial light only (GroundGlowAt ignoring the sky); the
/// frame's sky_glow is the map's sky light, lighting an unroofed cell to
/// max(glow, sky_glow). room keys the native room holding the cell by the
/// index of that room's first held cell (row-major), stable across the room
/// regeneration an edifice change triggers; it is not the native room id.
/// </summary>
public sealed record CellGrid
{
    public CellRect? Rect { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Strings { get; init; }

    public FieldArray? Cell { get; init; }

    public FieldArray? Walkable { get; init; }

    public FieldArray? Zone { get; init; }

    public FieldArray? Roofed { get; init; }

    public FieldArray? Indoors { get; init; }

    public FieldArray? StorageEmpty { get; init; }

    public FieldArray? Doorway { get; init; }

    public FieldArray? Fertility { get; init; }

    public FieldArray? Polluted { get; init; }

    public FieldArray? Glow { get; init; }

    public FieldArray? Roof { get; init; }

    public FieldArray? ZoneId { get; init; }

    public FieldArray? Room { get; init; }

    /// <summary>
    /// Columns 23 on carry the per-cell thing list and tile facts; a keyframe carries every one, things included. A fogged cell
    /// (cell code 0) stays unknown, distinct from a held cell with no things.
    /// string: the terrain def
    /// </summary>
    public FieldArray? Terrain { get; init; }

    /// <summary>
    /// bool codes: inside the home area
    /// </summary>
    public FieldArray? InHome { get; init; }

    /// <summary>
    /// string: the base terrain def under a floor or bridge (its affordances are what a foundation stands on)
    /// </summary>
    public FieldArray? BaseTerrain { get; init; }

    /// <summary>
    /// numbers
    /// </summary>
    public FieldArray? SnowDepth { get; init; }

    /// <summary>
    /// bool codes: the top terrain layer can be removed
    /// </summary>
    public FieldArray? TopLayerRemovable { get; init; }

    public ThingList? Things { get; init; }

    /// <summary>
    /// bool codes: a RemoveFoundation designation stands on the cell
    /// </summary>
    public FieldArray? FoundationRemoval { get; init; }
}

/// <summary>
/// The things on a grid's cells, sparse over a base: in a keyframe
/// every cell has none, in a delta the keyframe's lists (cumulative, never
/// chained). cells lists, ascending and distinct, the cells whose list
/// is replaced; offsets (cells.length + 1 prefix sums, from 0, never
/// decreasing, ending at things.length) cut things into one list per listed
/// cell, an empty list clearing the cell. A delta lists only cells whose list
/// differs from the keyframe's.
/// </summary>
public sealed record ThingList
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<uint>))]
    public EquatableArray<uint> Cells { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<uint>))]
    public EquatableArray<uint> Offsets { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Thing>))]
    public EquatableArray<Thing> Things { get; init; }
}

/// <summary>
/// One thing on a cell. def and the string fields below are string table
/// indexes (k for strings[k-1]; 0 is refused). state must match category.
/// A blueprint or frame lists the def it builds. A thing spanning cells is listed
/// on each, with the same id. Fractions are held to 1/100.
/// </summary>
public sealed record Thing
{
    public uint Def { get; init; }

    public ThingCategory Category { get; init; }

    public ThingFaction Faction { get; init; }

    public uint Flags { get; init; }

    public ulong Id { get; init; }

    /// <summary>
    /// stack count
    /// </summary>
    public uint Count { get; init; }

    public ThingState? State { get; init; }
}

/// <summary>The <c>state</c> choice of <see cref="Thing"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ThingState.PlantCase), "plant")]
[JsonDerivedType(typeof(ThingState.CorpseCase), "corpse")]
[JsonDerivedType(typeof(ThingState.FilthCase), "filth")]
[JsonDerivedType(typeof(ThingState.BuildingCase), "building")]
[JsonDerivedType(typeof(ThingState.ItemCase), "item")]
public abstract record ThingState
{
    private protected ThingState() { }

    public sealed record PlantCase(PlantState Value) : ThingState;

    public static implicit operator ThingState(PlantState value) => new PlantCase(value);

    public sealed record CorpseCase(CorpseState Value) : ThingState;

    public static implicit operator ThingState(CorpseState value) => new CorpseCase(value);

    public sealed record FilthCase(FilthState Value) : ThingState;

    public static implicit operator ThingState(FilthState value) => new FilthCase(value);

    public sealed record BuildingCase(BuildingState Value) : ThingState;

    public static implicit operator ThingState(BuildingState value) => new BuildingCase(value);

    public sealed record ItemCase(ItemState Value) : ThingState;

    public static implicit operator ThingState(ItemState value) => new ItemCase(value);
}

public sealed record PlantState
{
    /// <summary>
    /// 0..1
    /// </summary>
    public float Growth { get; init; }

    public bool Blighted { get; init; }
}

public sealed record CorpseState
{
    public CorpseClass Class { get; init; }

    /// <summary>
    /// rot progress, 0 fresh .. 1 dessicated
    /// </summary>
    public float Rot { get; init; }
}

public sealed record FilthState
{
    public uint Thickness { get; init; }
}

public sealed record MaterialNeed
{
    /// <summary>
    /// string table index
    /// </summary>
    public uint Def { get; init; }

    public uint Count { get; init; }
}

public sealed record BuildingState
{
    public uint HitPoints { get; init; }

    public bool Burning { get; init; }

    /// <summary>
    /// a blueprint or frame's materials still owed
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<MaterialNeed>))]
    public EquatableArray<MaterialNeed> Needed { get; init; }

    /// <summary>
    /// def indexes of what a casket holds
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<uint>))]
    public EquatableArray<uint> Casket { get; init; }

    public bool Reserved { get; init; }
}

public sealed record ItemState
{
    /// <summary>
    /// 0 sound .. 1 ruined
    /// </summary>
    public float Deterioration { get; init; }
}

