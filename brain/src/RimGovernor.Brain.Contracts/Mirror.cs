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

    /// <summary>The <c>CoverBehindLine</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public CombatCoverBehindLine? CoverBehindLine => (this as CoverBehindLineCase)?.Value;

    public sealed record CoverBehindLineCase(CombatCoverBehindLine Value) : CombatGeometryProposeRole;

    public static implicit operator CombatGeometryProposeRole(CombatCoverBehindLine value) => new CoverBehindLineCase(value);

    /// <summary>The <c>AdjacentToChoke</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public CombatAdjacentToChoke? AdjacentToChoke => (this as AdjacentToChokeCase)?.Value;

    public sealed record AdjacentToChokeCase(CombatAdjacentToChoke Value) : CombatGeometryProposeRole;

    public static implicit operator CombatGeometryProposeRole(CombatAdjacentToChoke value) => new AdjacentToChokeCase(value);

    /// <summary>The <c>FiringCells</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public CombatFiringCells? FiringCells => (this as FiringCellsCase)?.Value;

    public sealed record FiringCellsCase(CombatFiringCells Value) : CombatGeometryProposeRole;

    public static implicit operator CombatGeometryProposeRole(CombatFiringCells value) => new FiringCellsCase(value);

    /// <summary>The <c>RescuePath</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public CombatRescuePath? RescuePath => (this as RescuePathCase)?.Value;

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

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public CombatGeometry? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(CombatGeometry Value) : CombatGeometryReplyOutcome;

    public static implicit operator CombatGeometryReplyOutcome(CombatGeometry value) => new ObservedCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

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
/// A cell grid: the facts of every cell of a rect of the map (the whole map in a snapshot frame), one typed array per
/// fact, indexed row-major by <c>(z - Z) * Width + (x - X)</c>. The native read builds it on the game thread as a copy
/// (def names are the defs' own strings, rooms are keys, never live game objects) and nothing writes it after: the
/// brain reads the arrays where they lie. A cell <see cref="Held"/> is false for (fogged) holds no facts; the other
/// arrays' entries there mean nothing. Glow is artificial light only (GroundGlowAt ignoring the sky).
/// </summary>
public sealed class CellGrid
{
    public int X { get; init; }

    public int Z { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>The cell is not fogged: every other array holds its facts.</summary>
    public bool[] Held { get; init; } = [];

    public bool[] Walkable { get; init; } = [];

    /// <summary>The cell lies in a proper room that is not psychologically outdoors.</summary>
    public bool[] Indoors { get; init; } = [];

    /// <summary>A door, or a door blueprint or frame, stands on the cell.</summary>
    public bool[] Doorway { get; init; } = [];

    public bool[] Polluted { get; init; } = [];

    /// <summary>Inside the home area.</summary>
    public bool[] InHome { get; init; } = [];

    /// <summary>The top terrain layer can be removed.</summary>
    public bool[] TopLayerRemovable { get; init; } = [];

    /// <summary>A RemoveFoundation designation stands on the cell.</summary>
    public bool[] FoundationRemoval { get; init; } = [];

    /// <summary>The item stacks on the cell (0 for none; up to StorageCapacity).</summary>
    public int[] StorageStacks { get; init; } = [];

    /// <summary>
    /// The stacks the cell can hold: a storage building's maxItemsInCell (3 on a shelf), 0 under any other edifice, 1
    /// on open ground.
    /// </summary>
    public int[] StorageCapacity { get; init; } = [];

    public float[] Fertility { get; init; } = [];

    public float[] Glow { get; init; } = [];

    public float[] SnowDepth { get; init; } = [];

    /// <summary>The terrain def's name.</summary>
    public string[] Terrain { get; init; } = [];

    /// <summary>The terrain def under a floor or bridge (its affordances are what a foundation stands on).</summary>
    public string[] BaseTerrain { get; init; } = [];

    /// <summary>The roof def's name; null for no roof.</summary>
    public string?[] Roof { get; init; } = [];

    /// <summary>The covering zone's load id; null for no zone.</summary>
    public string?[] ZoneId { get; init; } = [];

    /// <summary>
    /// The room holding the cell, null for none: keyed by the map index (z * map width + x) of the room's first held
    /// cell, which stays put across the room regeneration an edifice change triggers (the room census's GridRoom).
    /// </summary>
    public int?[] Room { get; init; } = [];

    /// <summary>The cell's non-pawn things in native order, null for none.</summary>
    public RimGovernor.Brain.Policy.Thing[]?[] Things { get; init; } = [];
}
