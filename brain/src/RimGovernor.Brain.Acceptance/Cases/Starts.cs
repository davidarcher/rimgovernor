using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// How the runner brings the game to a case's starting state (Go <c>cases.Start</c>). Exactly one of
/// <see cref="DebugStart"/>, <see cref="LabStart"/>, <see cref="SaveStart"/>, <see cref="ScenarioStart"/>,
/// <see cref="FixtureStart"/> and <see cref="OwnedStart"/>; the set is sealed because the runner switches over it.
/// </summary>
public abstract record Start
{
    private protected Start()
    {
    }

    /// <summary>The start's summary for the report.</summary>
    public abstract JsonObject Describe();
}

/// <summary>
/// Starts RimWorld's debug colony on <see cref="Size"/>, or on the small default when it is zero; <c>Size.Biomes</c>
/// pins the start to a biome the assertion needs (fixture builds only).
/// </summary>
public sealed record DebugStart : Start
{
    public DebugStartSize Size { get; init; } = new();

    public override JsonObject Describe()
    {
        var row = new JsonObject
        {
            ["kind"] = "debug",
            ["mapSize"] = Size.MapSize,
            ["planetCoverage"] = Size.PlanetCoverage,
        };
        if (Size.Biomes.Length > 0)
        {
            row["biomes"] = Size.Biomes;
        }

        if (Size.Seed.Length > 0)
        {
            row["seed"] = Size.Seed;
        }

        if (Size.Flat)
        {
            row["flat"] = true;
        }

        return row;
    }
}

/// <summary>Loads <c>profile/Saves/&lt;Name&gt;.rws</c>. The runner activates the save's expansions.</summary>
public sealed record SaveStart : Start
{
    public string Name { get; init; } = "";

    public override JsonObject Describe() => new() { ["kind"] = "save", ["name"] = Name };
}

/// <summary>Computes more fixture arguments from the loaded game just before the op runs (the starter hut site, for one).</summary>
public delegate Task<JsonObject?> FixtureArgsFrom(Harness harness, CancellationToken cancellationToken);

/// <summary>Brings the game up through <see cref="On"/> (null: the debug colony), then calls the test fixture <see cref="Op"/> with <see cref="Args"/>; the reply is the session's Prepared.</summary>
public sealed record FixtureStart : Start
{
    public string Op { get; init; } = "";

    public JsonObject? Args { get; init; }

    public Start? On { get; init; }

    public FixtureArgsFrom? ArgsFrom { get; init; }

    public override JsonObject Describe()
    {
        var row = new JsonObject
        {
            ["kind"] = "fixture",
            ["op"] = Op,
            ["args"] = Args?.DeepClone(),
        };
        if (On is not null)
        {
            row["on"] = On.Describe();
        }

        return row;
    }
}

/// <summary>
/// The start of a case that drives the process lifecycle itself (a reusable-game or soak case that launches,
/// restarts or retires the game as its assertion): the runner prepares the profile and hands the case a session with
/// the configuration only, and the case leaves the root's process stopped. Owned implies NoKeep. <see cref="Saves"/>
/// names the saves the case loads, so the profile keeps the expansions they record active.
/// </summary>
public sealed record OwnedStart : Start
{
    public IReadOnlyList<string> Saves { get; init; } = [];

    public override JsonObject Describe()
    {
        var row = new JsonObject { ["kind"] = "owned" };
        if (Saves.Count > 0)
        {
            var saves = new JsonArray();
            foreach (string save in Saves)
            {
                saves.Add(save);
            }

            row["saves"] = saves;
        }

        return row;
    }
}

/// <summary>
/// The blank lab start: a fixed-seed 100x100 map wiped to Soil with fixture-made colonists, locked weather and
/// temperature and a quiet storyteller, cached as a save. A fixture case spawns what it needs at known offsets from
/// the centre the op replies.
/// </summary>
public sealed record LabStart : Start
{
    public int Colonists { get; init; }

    public override JsonObject Describe() => new() { ["kind"] = "lab", ["mapSize"] = LabWorld.LabMapSize, ["colonists"] = Colonists };
}

/// <summary>Starts a programmatic scenario from the main menu through the production new-colony op: how a save variant is generated.</summary>
public sealed record ScenarioStart : Start
{
    public ScenarioSpec Spec { get; init; } = new();

    public override JsonObject Describe() => new()
    {
        ["kind"] = "scenario",
        ["scenario"] = Spec.Scenario,
        ["seed"] = Spec.Seed,
        ["count"] = Spec.Count,
        ["biome"] = Spec.Biome,
    };
}
