namespace RimGovernor.RealCombat;

// The wire between a caller and the host. One request is one arena (map, terrain, things, pawns) fought once per
// seed; the host prints one RealFightResult. Pawns, gear and skills are named by the game's own def names and
// built by the game's own generators, so there is nothing to derive: the game computes every stat itself.

public sealed record RealFightRequest
{
    /// <summary>The request format version this host reads.</summary>
    public const int CurrentSchema = 1;

    /// <summary>The request format version; it must equal <see cref="CurrentSchema"/>. A request that omits it or states another is rejected.</summary>
    public int Schema { get; init; }

    public string Name { get; init; } = "";

    /// <summary>The arena's width and height in cells. The map is generated square from the larger.</summary>
    public int Width { get; init; } = 60;

    public int Height { get; init; } = 60;

    /// <summary>The TerrainDef laid on every cell; empty keeps the generated terrain.</summary>
    public string Terrain { get; init; } = "";

    /// <summary>Cell regions laid over the base <see cref="Terrain"/>, each with its own terrain and roof; the map is unroofed where none says otherwise.</summary>
    public IReadOnlyList<RealGround> Ground { get; init; } = [];

    /// <summary>The WeatherDef in force; empty keeps the generated weather.</summary>
    public string Weather { get; init; } = "";

    /// <summary>The FactionDef the hostile side belongs to; it must be permanently hostile to the player.</summary>
    public string HostileFaction { get; init; } = "PirateWaster";

    public IReadOnlyList<RealThing> Things { get; init; } = [];

    public IReadOnlyList<RealPawn> Pawns { get; init; } = [];

    /// <summary>One fight per seed, each from the same arena.</summary>
    public IReadOnlyList<int> Seeds { get; init; } = [];

    /// <summary>Ends an unresolved fight.</summary>
    public int MaxTicks { get; init; } = 6000;

    /// <summary>Plays every seed twice and reports a seed whose two runs differ (the host is not deterministic for it).</summary>
    public bool Verify { get; init; }
}

/// <summary>Reads a request strictly: a JSON member the format does not have rejects the whole request, naming it.</summary>
public static class RequestJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    public static RealFightRequest Read(string json)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<RealFightRequest>(json, Options) ?? throw new RealFightRejected(["request: empty"]);
        }
        catch (System.Text.Json.JsonException e)
        {
            throw new RealFightRejected([$"{e.Path}: {e.Message}"]);
        }
    }
}

public sealed record RealThing(string Def, string Stuff, int X, int Z);

/// <param name="Quality">QualityCategory name (Awful, Poor, Normal, Good, Excellent, Masterwork, Legendary); empty leaves the quality the game rolls. Needs a def with quality.</param>
/// <param name="HitPoints">Current hit points, 1 to the item's maximum; 0 is full condition.</param>
public sealed record RealWear(string Def, string Stuff, string Quality = "", int HitPoints = 0);

/// <summary>A rectangle of cells, MinX..MaxX and MinZ..MaxZ inclusive, set to a terrain and/or a roof. Later regions override earlier ones.</summary>
/// <param name="Terrain">TerrainDef laid on the cells; empty changes no terrain (the base <see cref="RealFightRequest.Terrain"/> or an earlier region's stays).</param>
/// <param name="Roof">RoofDef over the cells (RoofConstructed, RoofRockThin, RoofRockThick); empty changes no roof. Every cell starts unroofed (none).</param>
public sealed record RealGround(string Terrain, string Roof, int MinX, int MinZ, int MaxX, int MaxZ);

public sealed record RealTrait(string Def, int Degree);

/// <param name="Part">The BodyPartRecord label the hediff sits on ("left arm"); empty for a whole-body hediff.</param>
/// <param name="Severity">The hediff's severity; for an injury, its damage.</param>
public sealed record RealHediff(string Def, string Part, double Severity);

public sealed record RealPawn
{
    public string Id { get; init; } = "";

    /// <summary>"colonist" or "hostile".</summary>
    public string Side { get; init; } = "";

    public string Kind { get; init; } = "";

    public int X { get; init; }

    public int Z { get; init; }

    public string Weapon { get; init; } = "";

    public string WeaponStuff { get; init; } = "";

    /// <summary>QualityCategory name of the weapon; empty leaves the rolled quality.</summary>
    public string WeaponQuality { get; init; } = "";

    /// <summary>The weapon's current hit points; 0 is full condition.</summary>
    public int WeaponHitPoints { get; init; }

    public IReadOnlyList<RealWear> Worn { get; init; } = [];

    /// <summary>SkillDef name to level; passion is cleared.</summary>
    public IReadOnlyDictionary<string, int> Skills { get; init; } = new Dictionary<string, int>();

    public IReadOnlyList<RealTrait> Traits { get; init; } = [];

    /// <summary>ThingDef names carried in the pawn's inventory (a combat drug the policy may order taken).</summary>
    public IReadOnlyList<string> Carried { get; init; } = [];

    /// <summary>The pawn's whole health: every hediff it starts with. Nothing the generator rolled survives.</summary>
    public IReadOnlyList<RealHediff> Hediffs { get; init; } = [];

    /// <summary>BodyPartRecord labels of the parts the pawn is missing (a part's children are missing with it).</summary>
    public IReadOnlyList<string> Missing { get; init; } = [];

    /// <summary>Biological and chronological age in years. Required (above zero) for a humanlike pawn; unset for an animal or insect.</summary>
    public double Age { get; init; }

    /// <summary>XenotypeDef name (Baseliner). Required for a humanlike pawn; empty for an animal or insect.</summary>
    public string Xenotype { get; init; } = "";

    /// <summary>AbilityDef names of psycasts the pawn knows (Stun); any gives it a psylink and full psyfocus. Needs Royalty.</summary>
    public IReadOnlyList<string> Psycasts { get; init; } = [];

    /// <summary>RoyalTitleDef name the pawn holds with the Empire (Acolyte); empty for none. Needs Royalty.</summary>
    public string Title { get; init; } = "";

    /// <summary>Favor held with the Empire.</summary>
    public int Favor { get; init; }

    /// <summary>RoyalTitlePermitDef names the pawn holds (CallMilitaryAidSmall).</summary>
    public IReadOnlyList<string> Permits { get; init; } = [];
}

/// <param name="FirstShotTick">Ticks from the fight start to the pawn first shot; -1 when it never fired.</param>
public sealed record RealPawnOutcome(string Id, bool Downed, bool Dead, double HealthPercent, int Shots, int HitsTaken, int FirstShotTick);

public sealed record RealSeedOutcome
{
    public int Seed { get; init; }

    /// <summary>"colonist" or "hostile" when one side was all down or dead; empty when the fight ran out of ticks.</summary>
    public string Winner { get; init; } = "";

    public int Ticks { get; init; }

    public IReadOnlyList<RealPawnOutcome> Pawns { get; init; } = [];

    /// <summary>Set under <see cref="RealFightRequest.Verify"/> when the seed's second run differed from its first.</summary>
    public bool Nondeterministic { get; init; }
}

public sealed record RealFightResult
{
    public string Name { get; init; } = "";

    public IReadOnlyList<RealSeedOutcome> Outcomes { get; init; } = [];

    /// <summary>The wall time of the seeds alone, after the host started up.</summary>
    public double FightMilliseconds { get; init; }

    /// <summary>The wall time of the game's startup and the arena.</summary>
    public double StartupMilliseconds { get; init; }

    /// <summary>What the game holds for the request's arena and pawns, read back before the first tick; it equals the request or the run fails.</summary>
    public RealReadback Readback { get; init; } = new();
}

/// <summary>The request's imported fields as read from the game objects.</summary>
public sealed record RealReadback
{
    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>The TerrainDef on every cell, or "mixed".</summary>
    public string Terrain { get; init; } = "";

    /// <summary>Cells per TerrainDef name. The cell-for-cell comparison with the request is made by the readback gate.</summary>
    public IReadOnlyDictionary<string, int> TerrainCells { get; init; } = new Dictionary<string, int>();

    /// <summary>Cells per RoofDef name; unroofed cells are not counted.</summary>
    public IReadOnlyDictionary<string, int> RoofCells { get; init; } = new Dictionary<string, int>();

    public string Weather { get; init; } = "";

    public string HostileFaction { get; init; } = "";

    public IReadOnlyList<RealThing> Things { get; init; } = [];

    public IReadOnlyList<RealPawnReadback> Pawns { get; init; } = [];
}

/// <summary>One pawn as the game holds it after import. <see cref="Hediffs"/> is its whole hediff set, missing parts apart.</summary>
public sealed record RealPawnReadback
{
    public string Id { get; init; } = "";

    public string Side { get; init; } = "";

    public string Kind { get; init; } = "";

    public int X { get; init; }

    public int Z { get; init; }

    public double Age { get; init; }

    public string Xenotype { get; init; } = "";

    public string Weapon { get; init; } = "";

    public string WeaponStuff { get; init; } = "";

    /// <summary>The weapon's quality and current hit points as held, whether or not the request stated them.</summary>
    public string WeaponQuality { get; init; } = "";

    public int WeaponHitPoints { get; init; }

    public IReadOnlyList<RealWear> Worn { get; init; } = [];

    /// <summary>The levels of the skills the request states.</summary>
    public IReadOnlyDictionary<string, int> Skills { get; init; } = new Dictionary<string, int>();

    public IReadOnlyList<RealTrait> Traits { get; init; } = [];

    public IReadOnlyList<string> Carried { get; init; } = [];

    public IReadOnlyList<RealHediff> Hediffs { get; init; } = [];

    public IReadOnlyList<string> Missing { get; init; } = [];

    public IReadOnlyList<string> Psycasts { get; init; } = [];

    public string Title { get; init; } = "";

    public int Favor { get; init; }

    public IReadOnlyList<string> Permits { get; init; } = [];

    public bool Dead { get; init; }
}

/// <summary>The request cannot be honored; every message names the request field it concerns.</summary>
public sealed class RealFightRejected(IReadOnlyList<string> problems) : Exception("realcombat: request rejected: " + string.Join("; ", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>Drives a fight's colonists instead of the built-in nearest-target retargeting (the policy in the loop).</summary>
public interface IFightController
{
    /// <summary>Called once with the map after the pawns are spawned and drafted.</summary>
    void Begin(Verse.Map map);

    /// <summary>Called before every tick.</summary>
    void Decide(Verse.Map map, int tick);
}
