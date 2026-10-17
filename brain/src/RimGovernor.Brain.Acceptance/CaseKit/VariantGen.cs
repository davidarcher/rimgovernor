using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// One scenario-start spec, field for field <see cref="ScenarioSpec"/>'s (Go <c>variantgen.Variant</c>): a review case's
/// start, driven through RimWorld's programmatic scenario start by the production new-colony op, which generates the
/// colony and saves it under the requested name.
/// </summary>
public sealed record Variant
{
    /// <summary>The planet a biome- or temperature-constrained variant defaults to: large enough to hold every settleable biome.</summary>
    public const double ConstrainedPlanetCoverage = 0.3;

    public string Save { get; init; } = "";

    public string Scenario { get; init; } = "";

    public int Count { get; init; }

    public string Seed { get; init; } = "";

    public string Biome { get; init; } = "";

    public string Difficulty { get; init; } = "";

    public double MinTemperature { get; init; }

    public double MaxTemperature { get; init; }

    public string WorldTemperature { get; init; } = "";

    /// <summary>
    /// MapSize and PlanetCoverage default to the small start (<see cref="DebugStartSize.DefaultMapSize"/>,
    /// <see cref="DebugStartSize.DefaultPlanetCoverage"/>); a variant that picks a biome or a temperature band gets
    /// <see cref="ConstrainedPlanetCoverage"/> instead, since a 5% planet has no guaranteed tundra or extreme-desert
    /// tile. A variant whose stressor is map- or world-level sets them explicitly.
    /// </summary>
    public int MapSize { get; init; }

    public double PlanetCoverage { get; init; }

    /// <summary>
    /// Fills in the start's defaults for any field the spec left zero, so a minimal spec
    /// (<c>{"save": ..., "scenario": "LostTribe", "count": 8, "seed": "..."}</c>) is enough.
    /// </summary>
    public Variant WithDefaults()
    {
        var v = this;
        if (v.Difficulty.Length == 0)
        {
            v = v with { Difficulty = "Rough" };
        }

        if (v.WorldTemperature.Length == 0)
        {
            v = v with { WorldTemperature = "Normal" };
        }

        if (v.MinTemperature == 0 && v.MaxTemperature == 0)
        {
            v = v with { MinTemperature = -100, MaxTemperature = 100 };
        }

        if (v.MapSize == 0)
        {
            v = v with { MapSize = DebugStartSize.DefaultMapSize };
        }

        if (v.PlanetCoverage == 0)
        {
            var constrained = v.Biome.Length > 0 || v.MinTemperature != -100 || v.MaxTemperature != 100;
            v = v with { PlanetCoverage = constrained ? ConstrainedPlanetCoverage : DebugStartSize.DefaultPlanetCoverage };
        }

        return v;
    }

    /// <summary>The programmatic scenario start that generates this variant.</summary>
    public ScenarioStart Start()
    {
        var v = WithDefaults();
        return new ScenarioStart
        {
            Spec = new ScenarioSpec
            {
                Scenario = v.Scenario,
                Count = v.Count,
                Seed = v.Seed,
                Biome = v.Biome,
                Difficulty = v.Difficulty,
                MinTemperature = v.MinTemperature,
                MaxTemperature = v.MaxTemperature,
                WorldTemperature = v.WorldTemperature,
                Size = new DebugStartSize { MapSize = v.MapSize, PlanetCoverage = v.PlanetCoverage },
                SaveName = v.Save,
            },
        };
    }
}

/// <summary>Persisting a generated scenario-start save (tools/baselinegen; Go package <c>variantgen</c>).</summary>
public static class VariantGen
{
    /// <summary>
    /// Where a variant's generated save durably lives: profile/Saves under the disposable worker root, beside the
    /// generated tribal8 baseline. PrepareRendered points RimWorld straight at this directory; Prepare (headless)
    /// mirrors every .rws here into its own disposable headless-profile copy on each run.
    /// </summary>
    public static string SavePath(string root, string save) => Path.Combine(root, "profile", "Saves", save + ".rws");

    /// <summary>
    /// Confirms the loaded, paused game's save and persists it into root/profile/Saves (the durable location Prepare
    /// mirrors into the headless profile). The new-colony op already saved the colony under the variant's save name
    /// when the start completed (<see cref="Variant.Start"/> sets it); this checks the colony is paused and copies the
    /// file out of the running profile. The generated path is recorded as <c>row["path"]</c>.
    /// </summary>
    public static async Task SaveVariantAsync(Harness harness, string root, bool headless, string save, JsonObject row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(row);
        var identityReply = await harness.WireAsync("identity", "lifecycle_read_identity", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var (_, loaded) = Harness.Outcome(identityReply, "loaded");
        if (loaded["paused"].AsBool() != true)
        {
            throw new AcceptanceException("generated colony did not end up paused before save");
        }

        try
        {
            PersistSave(root, headless, save);
        }
        catch (IOException e)
        {
            throw new AcceptanceException($"persist generated save to profile/Saves: {e.Message}", e);
        }

        row["path"] = SavePath(root, save);
    }

    // Copies the save the running process just wrote from its own (possibly disposable) profile directory into
    // profile/Saves, the durable location every harness's Prepare reads from. In rendered mode the running profile
    // already is profile/Saves; in headless mode it is headless-profile/Saves, which Prepare overwrites from
    // profile/Saves on every run.
    private static void PersistSave(string root, bool headless, string save)
    {
        var full = Path.GetFullPath(root);
        var dst = Path.Combine(full, "profile", "Saves", save + ".rws");
        var src = Path.Combine(full, headless ? "headless-profile" : "profile", "Saves", save + ".rws");
        if (src == dst)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Copy(src, dst, overwrite: true);
    }
}
