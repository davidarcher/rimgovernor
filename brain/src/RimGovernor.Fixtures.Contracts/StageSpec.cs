#nullable enable
using System.Collections.Generic;

namespace RimGovernor.Fixtures.Contracts;

/// <summary>
/// What <c>test/stage</c> builds on the loaded map in one call, primitive by primitive and in this order: find the
/// site, clear areas, set cells, build rooms, place buildings, spawn things, spawn or set pawns (with the raid's arrival
/// mode and lords), then zones. Every cell and rect is relative to the found site's south-west corner, or absolute when
/// there is no site. Def names are the game's; the fixture resolves each by name and refuses an unknown one. A name keys
/// what an entry made in the <see cref="StageReply"/>.
/// </summary>
public sealed record StageSpec
{
    /// <summary>
    /// Zero is the pinned staging. Non-zero draws each <see cref="StagePawn.SkillSpread"/> from a state derived from it,
    /// and reseeds the game's Rand from it once staging ends, so the fight after it is a function of the seed.
    /// </summary>
    public int Seed { get; init; }

    /// <summary>
    /// Refuses the staging unless the game is paused: a setup start that must not race the clock. Off for a staging
    /// mid-fight (a combat case adds a manhunter to a served window).
    /// </summary>
    public bool Paused { get; init; }

    /// <summary>The site the rest of the spec is laid out on; null lays it out on absolute cells.</summary>
    public StageSite? Site { get; init; }

    public IReadOnlyList<StageClear> Clear { get; init; } = [];

    public IReadOnlyList<StageCells> Cells { get; init; } = [];

    public IReadOnlyList<StageRoom> Rooms { get; init; } = [];

    public IReadOnlyList<StageBuilding> Buildings { get; init; } = [];

    public IReadOnlyList<StageThing> Things { get; init; } = [];

    /// <summary>Generated pawns and existing colonists, handled in list order (the order the staging draws in).</summary>
    public IReadOnlyList<StagePawn> Pawns { get; init; } = [];

    public IReadOnlyList<StageZone> Zones { get; init; } = [];

    /// <summary>How the staged hostiles arrive and fight; null spawns them at their cells under an assault lord.</summary>
    public StageRaid? Raid { get; init; }
}

/// <summary>One map cell.</summary>
public readonly record struct StageCell(int X, int Z);

/// <summary>Width by Height cells whose south-west corner is X, Z.</summary>
public readonly record struct StageRect(int X, int Z, int Width, int Height);

/// <summary>
/// Find site: a Width by Height rect whose corner is the anchor plus the offset, or, with a Radius, the cell nearest it
/// (radially, within Radius) whose rect lies in bounds and meets every requirement. No such rect refuses the staging.
/// </summary>
public sealed record StageSite
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    public StageAnchor Near { get; init; }

    public int OffsetX { get; init; }

    public int OffsetZ { get; init; }

    /// <summary>Zero takes the anchored corner as it is.</summary>
    public int Radius { get; init; }

    public IReadOnlyList<StageGround> Require { get; init; } = [];
}

public enum StageAnchor
{
    /// <summary>The map's centre cell.</summary>
    MapCenter,

    /// <summary>The first free colonist (by thing id) that is neither dead, downed, drafted nor in a mental state.</summary>
    Colonist,
}

/// <summary>What every cell of a found site must be.</summary>
public enum StageGround
{
    Standable,

    /// <summary>No thing at all: no building, item, plant, filth or pawn.</summary>
    Empty,

    Unzoned,

    Unfogged,

    Unroofed,

    /// <summary>Terrain that carries heavy buildings (walls).</summary>
    Heavy,
}

/// <summary>Clear area: removes each kind from the rect.</summary>
public sealed record StageClear
{
    public required StageRect Rect { get; init; }

    public required IReadOnlyList<StageClearKind> Kinds { get; init; }
}

public enum StageClearKind
{
    /// <summary>Every thing but pawns, including buildings, natural rock, plants, items and filth.</summary>
    Things,

    Pawns,

    /// <summary>The rect's cells leave their zones.</summary>
    Zones,

    Roofs,

    Fog,

    Snow,

    /// <summary>The rect's cells leave the Home area.</summary>
    Home,
}

/// <summary>Set cells: each non-null field is written to every cell of the rect.</summary>
public sealed record StageCells
{
    public required StageRect Rect { get; init; }

    /// <summary>A TerrainDef (any foundation under it is removed).</summary>
    public string? Terrain { get; init; }

    /// <summary>A RoofDef.</summary>
    public string? Roof { get; init; }

    public bool? Home { get; init; }

    public bool? Fogged { get; init; }
}

/// <summary>
/// Build room: a ring of finished player Walls of Stuff around the rect, a Door of Stuff at each door cell (each on the
/// ring), and, with Roof, a constructed roof over the interior. Regions are rebuilt so the room exists at once.
/// </summary>
public sealed record StageRoom
{
    public required string Name { get; init; }

    public required StageRect Rect { get; init; }

    public required string Stuff { get; init; }

    public IReadOnlyList<StageCell> Doors { get; init; } = [];

    public bool Roof { get; init; }
}

/// <summary>Place building: Def at each cell, as a finished building, a frame or a blueprint.</summary>
public sealed record StageBuilding
{
    public required string Name { get; init; }

    public required string Def { get; init; }

    /// <summary>A stuffed def's stuff; null takes the def's default stuff.</summary>
    public string? Stuff { get; init; }

    public required IReadOnlyList<StageCell> Cells { get; init; }

    /// <summary>0..3: north, east, south, west.</summary>
    public int Rotation { get; init; }

    public StageBuildState State { get; init; }

    /// <summary>The owner, when the def can have one (a Hive always belongs to the insects).</summary>
    public StageFaction Faction { get; init; }

    /// <summary>Above zero stages a finished building damaged to that many hit points.</summary>
    public int HitPoints { get; init; }
}

public enum StageBuildState
{
    Complete,
    Frame,
    Blueprint,
}

/// <summary>
/// Spawn thing: Count units of the item Def at the cell, one stack there; with Near, full stacks and a remainder on the
/// free cells nearest it.
/// </summary>
public sealed record StageThing
{
    public required string Name { get; init; }

    public required string Def { get; init; }

    public string? Stuff { get; init; }

    public int Count { get; init; } = 1;

    public required StageCell Cell { get; init; }

    public bool Near { get; init; }

    public bool Forbidden { get; init; }
}

/// <summary>
/// Spawn pawn and set pawn. A pawn is generated from Kind, or is the existing free colonist at index Colonist (thing id
/// order), moved to the cell. A generated pawn starts without bad hediffs; a generated humanlike is a violence-capable
/// adult baseliner without addictions or relations. The remaining fields apply to either, in the order they are listed.
/// </summary>
public sealed record StagePawn
{
    public required string Name { get; init; }

    public int? Colonist { get; init; }

    /// <summary>A PawnKindDef to generate.</summary>
    public string? Kind { get; init; }

    public StageFaction Faction { get; init; }

    /// <summary>Held as the colony's prisoner, with no lord.</summary>
    public bool Prisoner { get; init; }

    /// <summary>A wild animal gone permanently manhunter.</summary>
    public bool Manhunter { get; init; }

    /// <summary>Biological and chronological age in years; null leaves it generated.</summary>
    public float? Age { get; init; }

    public StageGender? Gender { get; init; }

    /// <summary>Every skill at this level without passion; null leaves the skills generated.</summary>
    public int? SkillLevel { get; init; }

    /// <summary>Under a non-zero spec seed each skill is drawn from SkillLevel plus or minus this.</summary>
    public int SkillSpread { get; init; }

    /// <summary>SkillDef to level, after SkillLevel.</summary>
    public IReadOnlyDictionary<string, int>? Skills { get; init; }

    /// <summary>Replaces the traits (empty removes them all); null leaves them generated.</summary>
    public IReadOnlyList<StageTrait>? Traits { get; init; }

    /// <summary>Replaces what the pawn holds; null leaves it.</summary>
    public StageGear? Gear { get; init; }

    /// <summary>TrainableDefs a player animal has learned.</summary>
    public IReadOnlyList<string> Trained { get; init; } = [];

    /// <summary>Downed by anesthetic, without wounds.</summary>
    public bool Downed { get; init; }

    /// <summary>HediffDefs added.</summary>
    public IReadOnlyList<string> Hediffs { get; init; } = [];

    /// <summary>Bruised by one blunt blow, without bleeding.</summary>
    public bool Injured { get; init; }

    public int StunTicks { get; init; }

    public required StageCell Cell { get; init; }

    /// <summary>Every work type the pawn can do at this priority (0..4), before Work.</summary>
    public int? AllWork { get; init; }

    /// <summary>WorkTypeDef to priority 0..4.</summary>
    public IReadOnlyDictionary<string, int>? Work { get; init; }

    /// <summary>The assignment of every hour.</summary>
    public StageTimeAssignment? Timetable { get; init; }

    /// <summary>NeedDef to level 0..1.</summary>
    public IReadOnlyDictionary<string, float>? Needs { get; init; }

    /// <summary>The label of an existing apparel policy.</summary>
    public string? ApparelPolicy { get; init; }

    /// <summary>The label of an existing food policy.</summary>
    public string? FoodPolicy { get; init; }

    /// <summary>The label of an existing drug policy.</summary>
    public string? DrugPolicy { get; init; }
}

/// <summary>A TraitDef at a degree.</summary>
public sealed record StageTrait(string Def, int Degree);

public enum StageGender
{
    Male,
    Female,
}

public enum StageTimeAssignment
{
    Anything,
    Work,
    Joy,
    Sleep,
    Meditate,
}

/// <summary>
/// What a pawn holds: its equipment becomes Weapon (none when null), then it wears Apparel (a shield charged) and carries
/// one of each Inventory def. Strip first destroys the generated apparel and inventory.
/// </summary>
public sealed record StageGear
{
    public string? Weapon { get; init; }

    public string? WeaponStuff { get; init; }

    public IReadOnlyList<string> Apparel { get; init; } = [];

    public IReadOnlyList<string> Inventory { get; init; } = [];

    public bool Strip { get; init; }
}

public enum StageFaction
{
    Player,

    /// <summary>Pirates when the world has them, else the first hostile humanlike faction by def name.</summary>
    Hostile,

    Insect,

    Mechanoid,

    None,
}

/// <summary>Zone: a stockpile or growing zone over the cells.</summary>
public sealed record StageZone
{
    public required string Name { get; init; }

    public StageZoneKind Kind { get; init; }

    public string? Label { get; init; }

    public required IReadOnlyList<StageCell> Cells { get; init; }

    /// <summary>A stockpile's priority; null leaves the preset's.</summary>
    public StageStoragePriority? Priority { get; init; }

    /// <summary>A stockpile's filter: only these ThingDefs (empty allows nothing); null leaves the preset's.</summary>
    public IReadOnlyList<string>? Allow { get; init; }

    /// <summary>A growing zone's plant ThingDef; null leaves the default.</summary>
    public string? Plant { get; init; }
}

public enum StageZoneKind
{
    Stockpile,
    Growing,
}

public enum StageStoragePriority
{
    Low,
    Normal,
    Preferred,
    Important,
    Critical,
}

/// <summary>
/// The staged hostiles' raid. Every staged hostile, insect and mechanoid group gets an assault lord (no kidnapping,
/// fleeing or stealing) unless Siege camps the hostiles under a siege lord.
/// </summary>
public sealed record StageRaid
{
    /// <summary>A PawnsArrivalModeDef that drops the hostiles in around the first one's cell instead of spawning them.</summary>
    public string? Arrival { get; init; }

    /// <summary>The hostiles' assault lord digs (a sapper raid).</summary>
    public bool Sappers { get; init; }

    public StageSiege? Siege { get; init; }

    /// <summary>The first prisoner starts the game's own prison break.</summary>
    public bool PrisonBreak { get; init; }
}

/// <summary>A siege lord camped at the cell, placing blueprints worth Points.</summary>
public sealed record StageSiege(StageCell Camp, float Points);
