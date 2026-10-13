using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// Starts a programmatic scenario from the main menu through the production new-colony op
/// (<c>rimgovernor/lifecycle_new_colony</c>), the behaviour Go declared on <c>ScenarioStart</c> (the value type is
/// <see cref="ScenarioSpec"/>). It is how a save variant is generated rather than hand-played. The seed is required
/// and pins the world; the team policy's rerolls make a colony differ from one a different build generated.
/// </summary>
public static class ScenarioStarts
{
    /// <summary>The production storyteller def with no incident comps, used unless the run's QuietMode is Loud.</summary>
    public const string QuietStorytellerDef = "RimGovernorQuiet";

    public const string LoudStorytellerDef = "Cassandra";

    /// <summary>The storyteller a start without its own asks for.</summary>
    public static string DefaultStoryteller(bool quiet) => quiet ? QuietStorytellerDef : LoudStorytellerDef;

    /// <summary>
    /// The op's wire spec for <paramref name="spec"/>. The spec's Storyteller wins; empty takes
    /// <paramref name="defaultStoryteller"/>. A temperature band of 0..0 or the unconstrained -100..100 is left out.
    /// </summary>
    public static JsonObject Spec(ScenarioSpec spec, string defaultStoryteller)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Seed.Length == 0)
        {
            throw new AcceptanceException("scenario start needs a pinned seed");
        }

        var size = spec.Size.WithDefaults();
        var difficulty = spec.Difficulty.Length > 0 ? spec.Difficulty : "Rough";
        var storyteller = spec.Storyteller.Length > 0 ? spec.Storyteller : defaultStoryteller;
        var saveName = spec.SaveName.Length > 0 ? spec.SaveName : ScenarioSpec.DefaultSaveName;
        var wire = new JsonObject
        {
            ["scenario"] = spec.Scenario,
            ["colonistCount"] = spec.Count,
            ["seed"] = spec.Seed,
            ["difficulty"] = difficulty,
            ["storyteller"] = storyteller,
            ["mapSize"] = size.MapSize,
            ["planetCoverage"] = size.PlanetCoverage,
            ["saveName"] = saveName,
        };
        var biomes = SplitBiomes(spec.Biome);
        if (biomes.Count > 0)
        {
            wire["biomes"] = biomes;
        }

        if (spec.Flat)
        {
            wire["flatTile"] = true;
        }

        if (spec.WorldTemperature.Length > 0)
        {
            wire["worldTemperature"] = spec.WorldTemperature;
        }

        var constrained = (spec.MinTemperature != 0 || spec.MaxTemperature != 0) && (spec.MinTemperature != -100 || spec.MaxTemperature != 100);
        if (constrained)
        {
            wire["minTemperature"] = spec.MinTemperature;
            wire["maxTemperature"] = spec.MaxTemperature;
        }

        // The parts hold only what the start names; each part is a top-level member of the spec.
        foreach (var (key, value) in spec.Parts ?? [])
        {
            wire[key] = value?.DeepClone();
        }

        return wire;
    }

    /// <summary>
    /// Starts <paramref name="spec"/> through the op on the main-menu game and waits for the completed colony
    /// (paused, saved). The caller owns resuming the clock.
    /// </summary>
    public static async Task<NewColonyRun> RunAsync(ScenarioSpec spec, Harness harness, bool quiet, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var wire = Spec(spec, DefaultStoryteller(quiet));
        var id = string.Create(CultureInfo.InvariantCulture, $"scenario-start-{DateTime.UtcNow.Ticks}");
        var request = NewColony.NewColonyRequest(id, wire, spec.Timeout);
        try
        {
            return await NewColony.RunNewColonyAsync(harness, request, "new-colony", cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"scenario start {spec.Scenario} seed {spec.Seed}: {e.Message}", e);
        }
    }

    /// <summary>The start's loader: runs the op, records the new_colony block and returns the report row for "start".</summary>
    public static async Task<JsonObject> LoadAsync(ScenarioStart start, Session session, QuietMode quiet, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(session);
        var spec = start.Spec;
        var run = await RunAsync(spec, session.Harness, quiet != QuietMode.Loud, cancellationToken).ConfigureAwait(false);
        session.Report["quiet"] = quiet != QuietMode.Loud;
        var phases = new JsonArray();
        foreach (var phase in run.Phases)
        {
            phases.Add(phase);
        }

        session.Report["new_colony"] = new JsonObject
        {
            ["phases"] = phases,
            ["rerolls"] = run.Rerolls,
            ["seed"] = run.Completed["seed"]?.DeepClone(),
            ["saveName"] = run.Completed["saveName"]?.DeepClone(),
        };
        return new JsonObject
        {
            ["kind"] = "scenario",
            ["scenario"] = spec.Scenario,
            ["seed"] = spec.Seed,
            ["count"] = spec.Count,
            ["biome"] = spec.Biome,
            ["mapSize"] = spec.Size.MapSize,
            ["planetCoverage"] = spec.Size.PlanetCoverage,
        };
    }

    /// <summary>A comma-separated biome preference list as the wire's biomes array, in order, without blanks; empty when none is named.</summary>
    internal static JsonArray SplitBiomes(string list)
    {
        var biomes = new JsonArray();
        foreach (var part in list.Split(','))
        {
            var name = part.Trim();
            if (name.Length > 0)
            {
                biomes.Add(name);
            }
        }

        return biomes;
    }
}
