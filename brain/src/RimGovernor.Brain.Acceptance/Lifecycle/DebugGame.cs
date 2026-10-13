using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// One <see cref="DebugGame.LabSpawnAsync"/>: <see cref="Def"/> names a PawnKindDef (a generated pawn) or a ThingDef (a
/// building, or an item stack of <see cref="Count"/>). <see cref="Stuff"/> empty takes the def's default;
/// <see cref="Unowned"/> spawns it factionless instead of the player's. <see cref="Gender"/> ("male" or "female") and
/// <see cref="Age"/> (years) fix a pawn's sex and age; zero values leave them generated.
/// </summary>
public sealed record LabThing
{
    public string Def { get; init; } = "";

    public string Stuff { get; init; } = "";

    public int X { get; init; }

    public int Z { get; init; }

    public int Rotation { get; init; }

    public int Count { get; init; }

    public bool Unowned { get; init; }

    public string Gender { get; init; } = "";

    public double Age { get; init; }
}

/// <summary>Total units of one item def laid by <see cref="DebugGame.LabStockAsync"/>.</summary>
public sealed record Stock
{
    public string Def { get; init; } = "";

    public int Total { get; init; }

    /// <summary>The cell the stacks are laid nearest to.</summary>
    public int X { get; init; }

    public int Z { get; init; }

    /// <summary>The stuff of a stuffed item; empty takes the def's default.</summary>
    public string Stuff { get; init; } = "";
}

/// <summary>Whether the harness's storyteller is quiet (Go <c>Harness.quiet</c>, set by the quiet op and read by the lab start).</summary>
public static class HarnessQuiet
{
    private static readonly ConditionalWeakTable<Harness, StrongBox<bool>> States = new();

    /// <summary>Whether the last quiet op applied on <paramref name="harness"/> left the storyteller quiet.</summary>
    public static bool Get(Harness harness)
    {
        ArgumentNullException.ThrowIfNull(harness);
        return States.TryGetValue(harness, out var box) && box.Value;
    }

    /// <summary>Records the harness quiet or not.</summary>
    public static void Set(Harness harness, bool quiet)
    {
        ArgumentNullException.ThrowIfNull(harness);
        States.GetOrCreateValue(harness).Value = quiet;
    }
}

/// <summary>The debug quick start, the quiet storyteller and the lab fixture tools (Go <c>debuggame.go</c>).</summary>
public static class DebugGame
{
    /// <summary>The test fixture op that silences the storyteller (every fixture build carries it).</summary>
    public const string QuietStorytellerTool = "test/quiet_storyteller";

    /// <summary>Marks the loaded game quiet-world (a tool in QuietStorytellerFixture, so every fixture build carries it).</summary>
    public const string QuietWorldTool = "test/quiet_world";

    /// <summary>Wipes the loaded map to a blank Soil lab with fixture colonists (DebugStartFixture).</summary>
    public const string LabStartTool = "test/lab_start";

    /// <summary>Spawns one building, item or pawn on the lab.</summary>
    public const string LabSpawnTool = "test/lab_spawn";

    public const string LabCrewTool = "test/lab_crew";

    public const string LabStockTool = "test/lab_stock";

    /// <summary>The colony the acceptance debug start asks the op for.</summary>
    public const string DebugScenario = "Crashlanded";

    public const int DebugColonists = 3;

    public const string DebugDifficulty = "Rough";

    /// <summary>
    /// The positive traits every superpawn carries so a crew works and walks fast: Jogger (SpeedOffset 2) and
    /// Industrious (Industriousness 2).
    /// </summary>
    public const string SuperTraits = "SpeedOffset:2,Industriousness:2";

    /// <summary>The level <see cref="StaffCrewAsync"/> sets every skill of a crew colonist to.</summary>
    public const int CrewSkillLevel = 20;

    /// <summary>
    /// Spawns <paramref name="thing"/> through <see cref="LabSpawnTool"/> and returns the thing's load id (the id
    /// observation reads key on) and the op's reply.
    /// </summary>
    public static async Task<(string Id, JsonObject Reply)> LabSpawnAsync(Harness harness, LabThing thing, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(thing);
        var args = new JsonObject { ["def"] = thing.Def, ["x"] = thing.X, ["z"] = thing.Z, ["rotation"] = thing.Rotation };
        if (thing.Stuff.Length > 0)
        {
            args["stuff"] = thing.Stuff;
        }

        if (thing.Count > 0)
        {
            args["count"] = thing.Count;
        }

        if (thing.Unowned)
        {
            args["faction"] = "none";
        }

        if (thing.Gender.Length > 0)
        {
            args["gender"] = thing.Gender;
        }

        if (thing.Age > 0)
        {
            args["age"] = thing.Age;
        }

        JsonObject reply;
        try
        {
            reply = await harness.CallAsync("lab-spawn-" + thing.Def, LabSpawnTool, args, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"{LabSpawnTool} {thing.Def}: {e.Message}", e);
        }

        var id = reply["id"].AsString();
        if (reply["success"].AsBool() != true || id.Length == 0)
        {
            throw new AcceptanceException($"{LabSpawnTool} {thing.Def} refused: {reply.ToJson()}");
        }

        return (id, reply);
    }

    /// <summary>
    /// Starts the acceptance debug colony through the production new-colony op (a Crashlanded start of
    /// <see cref="DebugColonists"/> colonists, Rough, paused, names confirmed) with the default size.
    /// </summary>
    public static Task<JsonObject?> StartDebugGameAsync(Harness harness, IStartCache cache, IReadOnlyList<string>? names, QuietMode mode, CancellationToken cancellationToken) =>
        StartDebugGameSizedAsync(harness, cache, names, mode, DebugStartSize.Default(), cancellationToken);

    /// <summary>
    /// Starts the debug colony on the RimGovernorQuiet storyteller (Loud keeps Cassandra) and then, where the mode
    /// asks for it, the fixture's quiet op for the residuals the storyteller def does not cover. For the harnesses
    /// that reason about surrounding terrain (excavation, defense layout, map scope) and need more map than the
    /// default. <paramref name="names"/> are the discovered tool names; null fetches them. The result is the quiet
    /// op's reply, or null when it was not applied.
    /// </summary>
    public static async Task<JsonObject?> StartDebugGameSizedAsync(
        Harness harness,
        IStartCache cache,
        IReadOnlyList<string>? names,
        QuietMode mode,
        DebugStartSize start,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(start);
        names ??= await harness.DiscoveryAsync(cancellationToken).ConfigureAwait(false) ?? [];
        var apply = StartOps.QuietDecision(names, mode);
        var quietDef = mode != QuietMode.Loud;
        var cached = cache.Enabled;
        var name = cache.NameFor(start, quietDef);
        cache.Seed = "";
        cache.Save = "";
        if (cache.TryGetPath(name, out var path) && cached && !cache.IsStale(path, name))
        {
            await cache.LoadAsync(harness, name, cancellationToken).ConfigureAwait(false);
            cache.Save = name;
            return await ApplyQuietAsync(harness, apply, "apply", cancellationToken).ConfigureAwait(false);
        }

        await GenerateDebugStartAsync(harness, cache, start, quietDef, name, cancellationToken).ConfigureAwait(false);
        if (cached)
        {
            // The op saved the colony as name in the running profile.
            try
            {
                cache.PersistSave(name);
            }
            catch (IOException e)
            {
                throw new AcceptanceException($"cached start {name}: {e.Message}", e);
            }

            cache.Save = name;
        }

        return await ApplyQuietAsync(harness, apply, "apply", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The new-colony spec of the debug start: <paramref name="seed"/> is the pinned one or the one the caller drew, <paramref name="saveName"/> the save the op writes.</summary>
    public static ScenarioSpec ScenarioStartOf(DebugStartSize size, string seed, string saveName)
    {
        ArgumentNullException.ThrowIfNull(size);
        return new ScenarioSpec
        {
            Scenario = DebugScenario,
            Count = DebugColonists,
            Seed = seed,
            Difficulty = DebugDifficulty,
            Biome = size.Biomes,
            Flat = size.Flat,
            Size = size,
            SaveName = saveName,
        };
    }

    /// <summary>
    /// Sets the loaded game's quiet-world marker through <see cref="QuietWorldTool"/> and returns its reply
    /// (quietWorld, active, launched).
    /// </summary>
    public static async Task<JsonObject> ApplyQuietWorldAsync(Harness harness, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);
        JsonObject reply;
        try
        {
            reply = await harness.CallAsync("quiet-world", QuietWorldTool, new JsonObject { ["action"] = "apply" }, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"{QuietWorldTool}: {e.Message}", e);
        }

        return reply["success"].AsBool() == true
            ? reply
            : throw new AcceptanceException($"{QuietWorldTool} refused: {reply.ToJson()}");
    }

    /// <summary>
    /// Applies the quiet storyteller when the mode asked for it; the result is the op's reply, or null when it was
    /// not applied. The harness is marked quiet only when the op succeeded.
    /// </summary>
    public static async Task<JsonObject?> ApplyQuietAsync(Harness harness, bool apply, string action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);
        HarnessQuiet.Set(harness, false);
        if (!apply)
        {
            return null;
        }

        JsonObject quiet;
        try
        {
            quiet = await harness.CallAsync("quiet-storyteller", QuietStorytellerTool, new JsonObject { ["action"] = action }, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"{QuietStorytellerTool}: {e.Message}", e);
        }

        if (quiet["success"].AsBool() != true)
        {
            throw new AcceptanceException($"{QuietStorytellerTool} refused: {quiet.ToJson()}");
        }

        HarnessQuiet.Set(harness, true);
        return quiet;
    }

    /// <summary>
    /// Makes the loaded colony a crew of superpawns (<see cref="LabCrewTool"/>): it tops the free colonists up to
    /// <paramref name="size"/> with fixture-made adults, then makes each capable of every work type with every skill
    /// at 20 and <see cref="SuperTraits"/>. A colony already larger than size is left as it is. Returns the colonists
    /// the colony holds and the number added.
    /// </summary>
    public static async Task<(int Colonists, int Added)> StaffCrewAsync(Harness harness, int size, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);
        JsonObject reply;
        try
        {
            reply = await harness.CallAsync(
                "lab-crew",
                LabCrewTool,
                new JsonObject { ["size"] = size, ["level"] = CrewSkillLevel, ["traits"] = SuperTraits },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"{LabCrewTool}: {e.Message}", e);
        }

        return reply["success"].AsBool() == true
            ? ((int)reply["colonists"].AsNumber(), (int)reply["added"].AsNumber())
            : throw new AcceptanceException($"{LabCrewTool} refused: {reply.ToJson()}");
    }

    /// <summary>
    /// Stocks the loaded map with <paramref name="stock"/> (<see cref="LabStockTool"/>): full stacks and a remainder
    /// on the free cells nearest (X, Z), joining stacks that have room. It is the one way a case puts a resource on
    /// the map in quantity: never one LabSpawn per stack, which merges into what is already on the cell. Returns the
    /// units placed.
    /// </summary>
    public static async Task<int> LabStockAsync(Harness harness, Stock stock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(stock);
        var args = new JsonObject { ["def"] = stock.Def, ["total"] = stock.Total, ["x"] = stock.X, ["z"] = stock.Z };
        if (stock.Stuff.Length > 0)
        {
            args["stuff"] = stock.Stuff;
        }

        JsonObject reply;
        try
        {
            reply = await harness.CallAsync("lab-stock-" + stock.Def, LabStockTool, args, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"{LabStockTool} {stock.Def}: {e.Message}", e);
        }

        if (reply["success"].AsBool() != true)
        {
            throw new AcceptanceException($"{LabStockTool} {stock.Def} refused: {reply.ToJson()}");
        }

        var placed = (int)reply["placed"].AsNumber();
        return placed != stock.Total
            ? throw new AcceptanceException($"{LabStockTool} {stock.Def} placed {placed} of {stock.Total}")
            : placed;
    }

    /// <summary>
    /// Runs the debug start through the new-colony op from the main menu, saving it as <paramref name="saveName"/>,
    /// with the RimGovernorQuiet storyteller when <paramref name="quiet"/>; the cache's seed records the seed it
    /// used. The op needs a seed, so an unpinned start draws one here and the record knows it even when no cached
    /// save is kept.
    /// </summary>
    internal static async Task GenerateDebugStartAsync(Harness harness, IStartCache cache, DebugStartSize start, bool quiet, string saveName, CancellationToken cancellationToken)
    {
        start.Validate();
        var seed = start.Seed.Length > 0 ? start.Seed : cache.RandomSeed();
        cache.Seed = seed;
        await ScenarioStarts.RunAsync(ScenarioStartOf(start, seed, saveName), harness, quiet, cancellationToken).ConfigureAwait(false);
    }
}
