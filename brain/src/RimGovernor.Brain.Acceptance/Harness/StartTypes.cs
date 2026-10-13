using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The map size and planet coverage a start is generated with, and optionally the biomes it settles (Go
/// <c>nativeaccept.DebugStart</c>; the case-authoring start of the same name is <see cref="DebugStart"/>).
/// <see cref="Biomes"/> is a comma-separated list of BiomeDef names in preference order: the start lands on a random
/// valid settlement tile of the first one the generated planet offers. <see cref="Seed"/> pins the world seed (empty
/// draws one, which the report's world block records); a pinned seed caches under its own save. <see cref="Flat"/>
/// settles a flat tile without rivers, roads or tile mutators when the planet offers one. All of it is the production
/// new-colony op's spec.
/// </summary>
public sealed record DebugStartSize
{
    public const int DefaultMapSize = 200;
    public const int MinMapSize = 100;
    public const int MaxMapSize = 400;
    public const double DefaultPlanetCoverage = 0.05;

    /// <summary>Environment variable overriding <see cref="DefaultMapSize"/> for a whole run.</summary>
    public const string MapSizeEnv = "RIMGOVERNOR_ACCEPT_MAP_SIZE";

    /// <summary>Environment variable overriding <see cref="DefaultPlanetCoverage"/> for a whole run.</summary>
    public const string PlanetCoverageEnv = "RIMGOVERNOR_ACCEPT_PLANET_COVERAGE";

    public int MapSize { get; init; }

    public double PlanetCoverage { get; init; }

    public string Biomes { get; init; } = "";

    public string Seed { get; init; } = "";

    public bool Flat { get; init; }

    /// <summary>The small start, or the environment's override.</summary>
    public static DebugStartSize Default()
    {
        int mapSize = DefaultMapSize;
        double coverage = DefaultPlanetCoverage;
        string? v = Environment.GetEnvironmentVariable(MapSizeEnv);
        if (!string.IsNullOrEmpty(v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
        {
            mapSize = n;
        }

        v = Environment.GetEnvironmentVariable(PlanetCoverageEnv);
        if (!string.IsNullOrEmpty(v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double f))
        {
            coverage = f;
        }

        return new DebugStartSize { MapSize = mapSize, PlanetCoverage = coverage };
    }

    /// <summary>Fills a zero size and coverage from <see cref="Default"/>, keeping the biome preference.</summary>
    public DebugStartSize WithDefaults()
    {
        if (MapSize == 0 && PlanetCoverage == 0)
        {
            DebugStartSize def = Default();
            return this with { MapSize = def.MapSize, PlanetCoverage = def.PlanetCoverage };
        }

        return this;
    }

    /// <summary>Applies the op's own bounds.</summary>
    public void Validate()
    {
        if (MapSize < MinMapSize || MapSize > MaxMapSize)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"map size {MapSize} outside {MinMapSize}..{MaxMapSize}"));
        }

        if (PlanetCoverage < 0.05 || PlanetCoverage > 1)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"planet coverage {PlanetCoverage:R} outside 0.05..1"));
        }
    }
}

/// <summary>What the debug-colony start does about the storyteller once the colony exists (Go <c>QuietMode</c>).</summary>
public enum QuietMode
{
    /// <summary>The harness depends on a fixture build, so the quiet op must be discoverable and is applied; its absence is a stale-mod error.</summary>
    Required = 0,

    /// <summary>The harness also runs against a production build: the quiet op is applied when discoverable and skipped otherwise.</summary>
    IfAvailable = 1,

    /// <summary>An interruption harness (raids, manhunters, hostile holds) keeps the ordinary storyteller.</summary>
    Loud = 2,
}

/// <summary>Lab-world constants (Go <c>labstart.go</c>): a fixed seed on a temperate tile, wiped at <see cref="LabMapSize"/>.</summary>
public static class LabWorld
{
    public const int LabMapSize = 100;
    public const string LabSeed = "rimgovernor-lab";
    public const string LabBiome = "TemperateForest";
    public const int DefaultLabColonist = 3;
}

/// <summary>
/// Starts a programmatic scenario from the main menu through the production new-colony op (Go
/// <c>nativeaccept.ScenarioStart</c>): world and colony generation under the built-in team policy, the clock paused,
/// the naming dialog confirmed and the colony saved as <see cref="SaveName"/>. The seed is required and pins the world.
/// </summary>
public sealed record ScenarioSpec
{
    /// <summary>The save a spec without a <see cref="SaveName"/> leaves behind.</summary>
    public const string DefaultSaveName = "RimGovernor-scenario-start";

    public string Scenario { get; init; } = "";

    public int Count { get; init; }

    public string Seed { get; init; } = "";

    /// <summary>One BiomeDef name or a comma-separated preference list; the start settles the first the planet offers.</summary>
    public string Biome { get; init; } = "";

    public bool Flat { get; init; }

    public string Difficulty { get; init; } = "";

    public double MinTemperature { get; init; }

    public double MaxTemperature { get; init; }

    /// <summary>A RimWorld OverallTemperature name; empty leaves native's default.</summary>
    public string WorldTemperature { get; init; } = "";

    public DebugStartSize Size { get; init; } = new();

    /// <summary>A StorytellerDef name; empty takes the run's default.</summary>
    public string Storyteller { get; init; } = "";

    /// <summary>Scenario parts added to the base scenario, in the wire's JSON shape.</summary>
    public JsonObject? Parts { get; init; }

    public string SaveName { get; init; } = "";

    /// <summary>Bounds the whole start (timeoutMs); zero is the new-colony default.</summary>
    public TimeSpan Timeout { get; init; }
}

/// <summary>
/// Declares the in-mod brain session a service-driven case runs against (Go <c>nativeaccept.ServeSpec</c>). The brain
/// is in-process in the mod, so there is no child binary or extra argument list: what remains is what the harness
/// sets before the service takes over.
/// </summary>
public sealed record ServeSpec
{
    /// <summary>When set, loaded through the harness's own session (clock paused) before the service takes over. Empty means the game already holds the case's state.</summary>
    public string Save { get; init; } = "";

    /// <summary>The service acquires authority for the observed world by itself, so the harness skips Acquire.</summary>
    public bool Resume { get; init; }

    /// <summary>RIMGOVERNOR_ROUTINE_FAMILIES; null composes the autonomous default (every family).</summary>
    public IReadOnlyList<string>? Families { get; init; }

    /// <summary>Further environment entries (KEY=value).</summary>
    public IReadOnlyList<string> Env { get; init; } = [];

    /// <summary>The service's native-call timeout; zero means 15s.</summary>
    public TimeSpan NativeTimeout { get; init; }

    /// <summary>How long the scheduler may go without admitting a clock window before a step stall is reported; zero disables.</summary>
    public TimeSpan StepStall { get; init; }

    /// <summary>Disambiguates the requestIds the handle issues; empty means "serve".</summary>
    public string Prefix { get; init; } = "";

    /// <summary>The speed set as the player's choice before the service starts; empty is the run's clock speed.</summary>
    public string PlayerSpeed { get; init; } = "";
}
