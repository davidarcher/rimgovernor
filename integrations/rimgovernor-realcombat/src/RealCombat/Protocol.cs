namespace RimGovernor.RealCombat;

// The wire between a caller and the host. One request is one arena (map, terrain, things, pawns) fought once per
// seed; the host prints one RealFightResult. Pawns, gear and skills are named by the game's own def names and
// built by the game's own generators, so there is nothing to derive: the game computes every stat itself.

public sealed record RealFightRequest
{
    public string Name { get; init; } = "";

    /// <summary>The arena's width and height in cells. The map is generated square from the larger.</summary>
    public int Width { get; init; } = 60;

    public int Height { get; init; } = 60;

    /// <summary>The TerrainDef laid on every cell; empty keeps the generated terrain.</summary>
    public string Terrain { get; init; } = "";

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

public sealed record RealThing(string Def, string Stuff, int X, int Z);

public sealed record RealWear(string Def, string Stuff);

public sealed record RealTrait(string Def, int Degree);

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

    public IReadOnlyList<RealWear> Worn { get; init; } = [];

    /// <summary>SkillDef name to level; passion is cleared.</summary>
    public IReadOnlyDictionary<string, int> Skills { get; init; } = new Dictionary<string, int>();

    public IReadOnlyList<RealTrait> Traits { get; init; } = [];
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
}
