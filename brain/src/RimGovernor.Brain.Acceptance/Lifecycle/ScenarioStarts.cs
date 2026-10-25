using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// Starts a programmatic scenario from the main menu through the production new-colony op
/// (<c>lifecycle_new_colony</c>); the value type is
/// <see cref="ScenarioSpec"/>. It is how a save variant is generated rather than hand-played. The seed is required
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
    /// The op's spec for <paramref name="spec"/>: its parts with the base members set. The spec's Storyteller wins; empty
    /// takes <paramref name="defaultStoryteller"/>. A temperature band of 0..0 or the unconstrained -100..100 is left out.
    /// </summary>
    public static RimGovernor.Brain.GameOrders.Lifecycle.NewColonySpec Spec(ScenarioSpec spec, string defaultStoryteller)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Seed.Length == 0)
        {
            throw new AcceptanceException("scenario start needs a pinned seed");
        }

        var size = spec.Size.WithDefaults();
        var constrained = (spec.MinTemperature != 0 || spec.MaxTemperature != 0) && (spec.MinTemperature != -100 || spec.MaxTemperature != 100);
        return new RimGovernor.Brain.GameOrders.Lifecycle.NewColonySpec
        {
            StartingThings = [.. spec.Parts.StartingThings],
            ForcedTraits = [.. spec.Parts.ForcedTraits],
            ForcedHediffs = [.. spec.Parts.ForcedHediffs],
            Naked = [.. spec.Parts.Naked],
            SetNeedLevels = [.. spec.Parts.SetNeedLevels],
            PlayerFaction = RimGovernor.Brain.GameOrders.Lifecycle.ScenarioPlayerFaction.Base,
            ArrivalMethod = RimGovernor.Brain.GameOrders.Lifecycle.ScenarioArrivalMethod.Base,
            GovernorIdeoligion = false,
            Scenario = spec.Scenario,
            ColonistCount = (uint)spec.Count,
            Seed = spec.Seed,
            Difficulty = spec.Difficulty.Length > 0 ? spec.Difficulty : "Rough",
            Storyteller = spec.Storyteller.Length > 0 ? spec.Storyteller : defaultStoryteller,
            MapSize = (uint)size.MapSize,
            PlanetCoverage = (float)size.PlanetCoverage,
            SaveName = spec.SaveName.Length > 0 ? spec.SaveName : ScenarioSpec.DefaultSaveName,
            Biomes = [.. SplitBiomes(spec.Biome)],
            FlatTile = spec.Flat,
            WorldTemperature = spec.WorldTemperature.Length > 0 ? spec.WorldTemperature : null,
            MinTemperature = constrained ? new RimGovernor.Brain.GameOrders.Lifecycle.TemperatureLimit.AtCase((float)spec.MinTemperature) : new RimGovernor.Brain.GameOrders.Lifecycle.TemperatureLimit.OpenCase(),
            MaxTemperature = constrained ? new RimGovernor.Brain.GameOrders.Lifecycle.TemperatureLimit.AtCase((float)spec.MaxTemperature) : new RimGovernor.Brain.GameOrders.Lifecycle.TemperatureLimit.OpenCase(),
        };
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
        using var phase = LaunchTrace.Span("start.generate");
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
        session.Report["new_colony"] = new JsonObject
        {
            { "phases", new JsonArray([.. run.Phases.Select(p => (JsonNode?)JsonValue.Create(p.ToString()))]) },
            { "rerolls", run.Rerolls },
            { "seed", run.Completed.Seed },
            { "saveName", run.Completed.SaveName },
        };
        return new JsonObject
        {
            { "kind", "scenario" },
            { "scenario", spec.Scenario },
            { "seed", spec.Seed },
            { "count", spec.Count },
            { "biome", spec.Biome },
            { "mapSize", spec.Size.MapSize },
            { "planetCoverage", spec.Size.PlanetCoverage },
        };
    }

    /// <summary>A comma-separated biome preference list, in order, without blanks; empty when none is named.</summary>
    internal static IReadOnlyList<string> SplitBiomes(string list) =>
        [.. list.Split(',').Select(part => part.Trim()).Where(name => name.Length > 0)];
}
