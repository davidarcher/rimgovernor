using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Contracts.Wire.Observations;

/// <summary>
/// The game state a hunt encounter prediction is built from: every colonist who could join a hunt and every
/// huntable animal, each stated as the encounter predictor's actor. It carries no chosen roster, no target and
/// no estimate; policy proposes rosters and the runtime asks the prediction service about them.
/// </summary>
public sealed record HuntActivityRead
{
    public bool Known { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<HuntActorRead>))]
    public EquatableArray<HuntActorRead> Hunters { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<HuntActorRead>))]
    public EquatableArray<HuntActorRead> Prey { get; init; }
    public HuntSceneRead? Scene { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Findings { get; init; }
}

/// <summary>A pawn as the predictor's actor: Pawn is the game's unique load id, Kind the race (prey) or pawn kind (hunter).</summary>
public sealed record HuntActorRead(string Pawn, string Kind, Cell Location, double Age)
{
    public string Xenotype { get; init; } = "";
    public HuntGearRead? Weapon { get; init; }
    /// <summary>The weapon's longest attack range in cells, unknown when it has no ranged attack verb.</summary>
    public Fact<double> WeaponRange { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<HuntGearRead>))]
    public EquatableArray<HuntGearRead> Apparel { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<HuntSkillRead>))]
    public EquatableArray<HuntSkillRead> Skills { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<HuntTraitRead>))]
    public EquatableArray<HuntTraitRead> Traits { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<HuntHediffRead>))]
    public EquatableArray<HuntHediffRead> Health { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> MissingParts { get; init; }
    /// <summary>Hunters: able to take a hunt order now (free, standing, not mentally broken, hunting not disabled).</summary>
    public bool Available { get; init; }
    /// <summary>Prey: asleep, downed, or in a mental state; unknown fields stay out of the request.</summary>
    public bool Downed { get; init; }
    public bool Mental { get; init; }
    /// <summary>Prey: the game's herd of this animal group, empty when it is alone.</summary>
    public string Herd { get; init; } = "";
    /// <summary>Prey meat if it is killed and butchered by a colonist, unknown when native has no stat for it.</summary>
    public Fact<double> Meat { get; init; }
    /// <summary>Prey: nutrition one unit of its meat gives when eaten.</summary>
    public Fact<double> MeatNutrition { get; init; }
}

public sealed record HuntGearRead(string Definition, string Stuff, string Quality, int HitPoints);
public sealed record HuntSkillRead(string Skill, int Level);
public sealed record HuntTraitRead(string Definition, int Degree);
public sealed record HuntHediffRead(string Definition, string Part, double Severity);

/// <summary>The map window around the actors, as the predictor's scene identity and ground.</summary>
public sealed record HuntSceneRead(string World, string GameBuild, string Mods, string Weather, string Terrain,
    int MinX, int MinZ, int Width, int Height, double OutdoorTemperature, double SkyGlow)
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<HuntGroundRead>))]
    public EquatableArray<HuntGroundRead> Ground { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<HuntObstacleRead>))]
    public EquatableArray<HuntObstacleRead> Obstacles { get; init; }
}
public sealed record HuntGroundRead(int MinX, int MinZ, int MaxX, int MaxZ, string Terrain, string Roof);
public sealed record HuntObstacleRead(string Definition, string Stuff, int X, int Z);
