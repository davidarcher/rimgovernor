using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// One scenario-start spec, field for field <see cref="ScenarioSpec"/>'s (Go <c>variantgen.Variant</c>): the
/// scenario-start save-generation mechanics behind the tools/variantsavegen and sustained/matrix cases. They drive
/// RimWorld's programmatic scenario start through the production new-colony op, which generates the colony and saves
/// it under the requested name. The sustained/matrix cases generate any variant save missing from a manifest before
/// running their watch window against it, so a caller never needs to run tools/variantsavegen as a separate step or
/// hand-check in a .rws fixture: the checked-in artifact is the JSON variant spec, not a multi-megabyte binary save.
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
    /// Fills in the start's defaults for any field a hand-written manifest entry left zero, so a minimal spec
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

    /// <summary>Checks the fields the op would otherwise reject, so a manifest typo fails before spending a native session on it.</summary>
    public void Validate()
    {
        if (Save.Length == 0 || Scenario.Length == 0 || Seed.Length == 0)
        {
            throw new AcceptanceException($"variant missing save/scenario/seed: {ToJson().ToJsonString()}");
        }

        if (Count is < 1 or > 10)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"variant \"{Save}\" count must be 1..10, got {Count}"));
        }

        try
        {
            new DebugStartSize { MapSize = MapSize, PlanetCoverage = PlanetCoverage }.Validate();
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"variant \"{Save}\": {e.Message}", e);
        }
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

    /// <summary>The variant in its manifest JSON shape (optional fields omitted when empty).</summary>
    public JsonObject ToJson()
    {
        var row = new JsonObject
        {
            ["save"] = Save,
            ["scenario"] = Scenario,
            ["count"] = Count,
            ["seed"] = Seed,
        };
        if (Biome.Length > 0)
        {
            row["biome"] = Biome;
        }

        if (Difficulty.Length > 0)
        {
            row["difficulty"] = Difficulty;
        }

        row["minTemperature"] = MinTemperature;
        row["maxTemperature"] = MaxTemperature;
        if (WorldTemperature.Length > 0)
        {
            row["worldTemperature"] = WorldTemperature;
        }

        if (MapSize != 0)
        {
            row["mapSize"] = MapSize;
        }

        if (PlanetCoverage != 0)
        {
            row["planetCoverage"] = PlanetCoverage;
        }

        return row;
    }
}

/// <summary>The scenario-start save generation shared by tools/variantsavegen and the sustained matrix (Go package <c>variantgen</c>).</summary>
public static class VariantGen
{
    private static readonly HashSet<string> ManifestFields = new(StringComparer.Ordinal)
    {
        "save", "scenario", "count", "seed", "biome", "difficulty", "minTemperature", "maxTemperature", "worldTemperature", "mapSize", "planetCoverage",
    };

    /// <summary>
    /// Where a variant's generated save durably lives: profile/Saves under the disposable worker root, beside the
    /// generated tribal8 baseline. PrepareRendered points RimWorld straight at this directory; Prepare (headless)
    /// mirrors every .rws here into its own disposable headless-profile copy on each run.
    /// </summary>
    public static string SavePath(string root, string save) => Path.Combine(root, "profile", "Saves", save + ".rws");

    /// <summary>Maps a save name onto the characters a case name and a directory admit.</summary>
    public static string Sanitize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var b = new StringBuilder(name.Length);
        foreach (var r in name)
        {
            b.Append(r is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_' ? r : '-');
        }

        return b.ToString();
    }

    /// <summary>
    /// Decodes a JSON array of variant specs, applies the defaults and validates every entry (unknown fields,
    /// out-of-range counts, an empty seed and a duplicate save name are errors).
    /// </summary>
    public static IReadOnlyList<Variant> Manifest(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException e)
        {
            throw new AcceptanceException($"manifest must be a JSON array of variant specs: {e.Message}", e);
        }

        if (root is not JsonArray entries)
        {
            throw new AcceptanceException("manifest must be a JSON array of variant specs");
        }

        if (entries.Count == 0)
        {
            throw new AcceptanceException("manifest names no variants");
        }

        var variants = new List<Variant>(entries.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var variant = Decode(entry).WithDefaults();
            variant.Validate();
            if (!seen.Add(variant.Save))
            {
                throw new AcceptanceException($"duplicate save name \"{variant.Save}\"");
            }

            variants.Add(variant);
        }

        return variants;
    }

    private static Variant Decode(JsonNode? entry)
    {
        if (entry is not JsonObject map)
        {
            throw new AcceptanceException("manifest must be a JSON array of variant specs: entry is not an object");
        }

        foreach (var (key, _) in map)
        {
            if (!ManifestFields.Contains(key))
            {
                throw new AcceptanceException($"manifest must be a JSON array of variant specs: unknown field \"{key}\"");
            }
        }

        return new Variant
        {
            Save = map["save"].AsString(),
            Scenario = map["scenario"].AsString(),
            Count = (int)Math.Max(map["count"].AsNumber(), 0),
            Seed = map["seed"].AsString(),
            Biome = map["biome"].AsString(),
            Difficulty = map["difficulty"].AsString(),
            MinTemperature = Number(map["minTemperature"]),
            MaxTemperature = Number(map["maxTemperature"]),
            WorldTemperature = map["worldTemperature"].AsString(),
            MapSize = (int)Number(map["mapSize"]),
            PlanetCoverage = Number(map["planetCoverage"]),
        };
    }

    private static double Number(JsonNode? value) => SampleJson.TryFloat(value, out var n) ? n : 0;

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
