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
