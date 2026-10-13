using System.Diagnostics;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// Names a RimWorld NeedDef (Food, Rest, Joy, ...) that <see cref="Session.OpenAsync"/> leaves live when it freezes
/// the rest (Go <c>NeedDef</c>).
/// </summary>
public readonly record struct NeedDef(string Value)
{
    public static readonly NeedDef Food = new("Food");

    public static readonly NeedDef Rest = new("Rest");

    public static readonly NeedDef Joy = new("Joy");

    /// <summary>
    /// As the keep list, leaves every need live and skips the freeze, for a harness whose colony must behave as it
    /// always has (the animals/containment case's builder never finishes the pen marker when frozen). The report's
    /// frozen_needs is then absent.
    /// </summary>
    public static readonly NeedDef LiveNeeds = new("*");

    public override string ToString() => Value;
}

/// <summary>What a start contributes to the report's world block (Go <c>worldSource</c>): the save it loaded, the seed it knows and whether that seed was pinned.</summary>
public sealed record WorldSource
{
    /// <summary>The save the start loaded by profile name; empty when it generated or reused a world.</summary>
    public string Save { get; init; } = "";

    public string Seed { get; init; } = "";

    public bool Pinned { get; init; }
}

/// <summary>
/// The harness's hold on the running game (Go <c>Game</c>): the harness that reaches it and the close that keeps
/// the process at the main menu or stops it. The game-host slice implements it over its process management.
/// </summary>
public interface IGameHold
{
    /// <summary>The harness the opened game is driven through.</summary>
    Harness Harness { get; }

    /// <summary>Ends the hold: the process is kept at the main menu or stopped, and game_reuse lands on <paramref name="report"/>.</summary>
    void Close(Report report);
}

/// <summary>
/// The start cache the debug and lab starts consult (Go <c>startCache</c> and the cached-start functions of
/// <c>startcache.go</c>): one per harness process, shared state beside the config. The start-cache slice implements
/// it; the lifecycle only calls it.
/// </summary>
public interface IStartCache
{
    /// <summary>Whether the saved start is used: cached starts are on and the cache knows its root (Go <c>CachedStart() &amp;&amp; startCache.root != ""</c>).</summary>
    bool Enabled { get; }

    /// <summary>The disposable worker root the profile lives under.</summary>
    string Root { get; }

    /// <summary>The expansions the prepared profile keeps active.</summary>
    IReadOnlyList<string> Expansions { get; }

    /// <summary>The seed the last debug start generated with, empty when it loaded the cached save.</summary>
    string Seed { get; set; }

    /// <summary>The cached save the last debug start loaded, empty when it generated.</summary>
    string Save { get; set; }

    /// <summary>The save name a start caches as (Go <c>cachedStartName</c>).</summary>
    string NameFor(DebugStartSize start, bool quiet);

    /// <summary>Where the running profile would hold the save, and whether it is there (Go <c>cachedStartPath</c>).</summary>
    bool TryGetPath(string name, out string path);

    /// <summary>Whether the cached save requires different expansions from the active profile (Go <c>cachedStartStale</c>).</summary>
    bool IsStale(string path, string name);

    /// <summary>Loads the save and waits for it the way a quick start is waited for (Go <c>loadCachedStart</c>).</summary>
    Task LoadAsync(Harness harness, string name, CancellationToken cancellationToken);

    /// <summary>Writes the just-started game as <paramref name="name"/> and copies it into profile/Saves (Go <c>saveCachedStart</c>).</summary>
    Task SaveAsync(Harness harness, string name, CancellationToken cancellationToken);

    /// <summary>Copies the op's save into the durable profile location (Go <c>PersistSave</c>).</summary>
    void PersistSave(string name);

    /// <summary>Draws a world seed for a debug start nothing pinned (Go <c>RandomSeed</c>).</summary>
    string RandomSeed();
}

/// <summary>
/// Everything around the lifecycle that depends on the profile and the game process, which other slices own (Go
/// <c>Config.EnsureSave</c>, <c>UseSaveExpansions</c>, <c>PrepareConfig</c>, <c>PackageFiles</c>, <c>OpenGame</c>,
/// <c>RecordWorld</c>, <c>FixtureBuildHint</c>). The lifecycle calls it in the order Go did.
/// </summary>
public interface ISessionHost
{
    /// <summary>The start cache of this process.</summary>
    IStartCache StartCache { get; }

    /// <summary>
    /// Prepares the profile for a start: the saves are ensured present and, unless the config already names
    /// expansions, their recorded expansions are kept active; then the configuration directory is prepared.
    /// </summary>
    Task PrepareAsync(Config config, IReadOnlyList<string> saves, CancellationToken cancellationToken);

    /// <summary>The installed package's file hashes (Go <c>PackageFiles</c>), name to hash; also fails a stale package.</summary>
    JsonObject PackageFiles(Config config);

    /// <summary>Opens the game (a kept process is reused) and returns the hold; <paramref name="report"/> receives its launch rows.</summary>
    Task<IGameHold> OpenGameAsync(Config config, Report report, CancellationToken cancellationToken);

    /// <summary>The report's world block for <paramref name="start"/> once it has loaded (Go <c>RecordWorld</c>); never fails the run.</summary>
    JsonObject RecordWorld(Config config, Start start, WorldSource source);

    /// <summary>The " -Fixture ..." build hint the stale-mod error appends for <paramref name="op"/>; empty when unknown.</summary>
    string FixtureBuildHint(string op);
}

/// <summary>
/// The game is already loaded in a reused process (<see cref="Config.KeepLoaded"/> left it there): the start loads
/// nothing and fails at the main menu. The storyteller is left as it is (Go <c>Loaded</c>).
/// </summary>
public sealed record LoadedStart : Start
{
    public override JsonObject Describe() => new() { ["kind"] = "loaded" };
}

/// <summary>
/// The start behaviour Go declared on the <c>Start</c> interface (saves, fixtureOps, load, world), dispatched over the
/// sealed set of start records.
/// </summary>
public static class StartOps
{
    /// <summary>The save names the start loads, for the profile's expansion activation.</summary>
    public static IReadOnlyList<string> Saves(Start start) => start switch
    {
        SaveStart save => [save.Name],
        FixtureStart fixture => Saves(fixture.On ?? new DebugStart()),
        OwnedStart owned => owned.Saves,
        _ => [],
    };

    /// <summary>The test ops the start calls, for <see cref="Config.FixtureOps"/>.</summary>
    public static IReadOnlyList<string> FixtureOps(Start start) => start switch
    {
        FixtureStart fixture => [.. FixtureOps(fixture.On ?? new DebugStart()), fixture.Op],
        _ => [],
    };

    /// <summary>What the start contributes to the report's world block, once loaded.</summary>
    public static WorldSource World(Start start, IStartCache cache)
    {
        switch (start)
        {
            case DebugStart debug:
                return new WorldSource { Seed = cache.Seed, Save = cache.Save, Pinned = debug.Size.Seed.Length > 0 };
            case SaveStart save:
                return new WorldSource { Save = save.Name };
            case FixtureStart fixture:
                return World(fixture.On ?? new DebugStart(), cache);
            case LabStart lab:
                return LabStarts.World(lab, cache);
            case ScenarioStart scenario:
                return new WorldSource { Seed = scenario.Spec.Seed, Pinned = true };
            default:
                return new WorldSource();
        }
    }

    /// <summary>Leaves the game loaded (not necessarily paused) and returns the report row for "start".</summary>
    public static Task<JsonObject> LoadAsync(Start start, Session session, QuietMode quiet, CancellationToken cancellationToken) => start switch
    {
        DebugStart debug => LoadDebugAsync(debug, session, quiet, cancellationToken),
        SaveStart save => LoadSaveAsync(save, session, quiet, cancellationToken),
        FixtureStart fixture => LoadFixtureAsync(fixture, session, quiet, cancellationToken),
        LoadedStart => LoadLoadedAsync(session, cancellationToken),
        LabStart lab => LabStarts.LoadAsync(lab, session, quiet, cancellationToken),
        ScenarioStart scenario => ScenarioStarts.LoadAsync(scenario, session, quiet, cancellationToken),
        _ => throw new AcceptanceException($"start {start.GetType().Name} is not opened through the session lifecycle"),
    };

    private static async Task<JsonObject> LoadDebugAsync(DebugStart debug, Session session, QuietMode quiet, CancellationToken cancellationToken)
    {
        var size = debug.Size.WithDefaults();
        var quietReply = await DebugGame.StartDebugGameSizedAsync(session.Harness, session.Host.StartCache, session.Names, quiet, size, cancellationToken).ConfigureAwait(false);
        session.Report["quiet"] = quietReply is not null;
        var row = new JsonObject { ["kind"] = "debug", ["mapSize"] = size.MapSize, ["planetCoverage"] = size.PlanetCoverage };
        if (size.Biomes.Length > 0)
        {
            row["biomes"] = size.Biomes;
        }

        if (size.Flat)
        {
            row["flat"] = true;
        }

        return row;
    }

    // A Save start's timeout, mod-compatibility and resumed flags are not on the SaveStart record yet: the defaults
    // are Go's zero values (90 s, strict mods, apply).
    private static async Task<JsonObject> LoadSaveAsync(SaveStart save, Session session, QuietMode quiet, CancellationToken cancellationToken)
    {
        if (save.Name.Length == 0)
        {
            throw new AcceptanceException("save start: empty save name");
        }

        await LoadSaveModsAsync(session.Harness, save.Name, TimeSpan.Zero, ignoreMods: false, cancellationToken).ConfigureAwait(false);
        var apply = QuietDecision(session.Names, quiet);
        var quietReply = await DebugGame.ApplyQuietAsync(session.Harness, apply, "apply", cancellationToken).ConfigureAwait(false);
        session.Report["quiet"] = quietReply is not null;
        return new JsonObject { ["kind"] = "save", ["save"] = save.Name };
    }

    /// <summary>
    /// Loads a save from the profile (<c>rimgovernor/load_game_ready</c>); a zero <paramref name="timeout"/> is 90 s.
    /// <paramref name="ignoreMods"/> loads a save whose recorded mods are not all active: a downloaded CI bundle
    /// predates mods the build has since dropped.
    /// </summary>
    public static async Task LoadSaveModsAsync(Harness harness, string name, TimeSpan timeout, bool ignoreMods, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);
        if (timeout <= TimeSpan.Zero)
        {
            timeout = TimeSpan.FromSeconds(90);
        }

        try
        {
            await harness.CallAsync(
                "load-save",
                "rimgovernor/load_game_ready",
                new JsonObject
                {
                    ["saveName"] = name,
                    ["readiness"] = "visual",
                    ["timeoutMs"] = (long)timeout.TotalMilliseconds,
                    ["ignoreModCompatibility"] = ignoreMods,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"load save {name}: {e.Message}", e);
        }
    }

    /// <summary>Resolves whether to apply the quiet op given the discovered tool names and the harness's mode.</summary>
    public static bool QuietDecision(IReadOnlyList<string> names, QuietMode mode)
    {
        var available = Session.HasTool(names, DebugGame.QuietStorytellerTool);
        switch (mode)
        {
            case QuietMode.Loud:
                return false;
            case QuietMode.IfAvailable:
                return available;
            case QuietMode.Required:
                return available
                    ? true
                    : throw new AcceptanceException($"{DebugGame.QuietStorytellerTool} not in discovery: rebuild the native mod with any -Fixture flag (every fixture build includes QuietStorytellerFixture)");
            default:
                throw new AcceptanceException($"unknown quiet mode {(int)mode}");
        }
    }

    private static async Task<JsonObject> LoadFixtureAsync(FixtureStart fixture, Session session, QuietMode quiet, CancellationToken cancellationToken)
    {
        var row = await LoadAsync(fixture.On ?? new DebugStart(), session, quiet, cancellationToken).ConfigureAwait(false);
        row["fixture"] = fixture.Op;
        return row;
    }

    /// <summary>
    /// Runs the fixture op (<c>test/&lt;name&gt;_prepare</c>) against the paused, identified game. The op must be
    /// discoverable, must reply success and, when it reports an identity, must report the loaded game's; the reply
    /// is <see cref="Session.Prepared"/> and the report's "prepared".
    /// </summary>
    public static async Task<JsonObject> PrepareFixtureAsync(FixtureStart fixture, Session session, JsonObject identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(session);
        if (fixture.Op.Length == 0)
        {
            throw new AcceptanceException("fixture start: empty op");
        }

        if (!Session.HasTool(session.Names, fixture.Op))
        {
            throw new AcceptanceException($"missing {fixture.Op} in discovery; rebuild the native mod with its fixture{session.Host.FixtureBuildHint(fixture.Op)}");
        }

        var args = (fixture.Args ?? []).DeepClone().AsMap() ?? [];
        var prepared = await session.Harness.CallAsync("prepare", fixture.Op, args, cancellationToken).ConfigureAwait(false);
        if (prepared["success"].AsBool() != true)
        {
            throw new AcceptanceException($"{fixture.Op} refused: {prepared.ToJson()}");
        }

        if (prepared.ContainsKey("loadToken") && !Session.MatchesIdentity(prepared, identity))
        {
            throw new AcceptanceException($"{fixture.Op} identity does not match the loaded game: {prepared.ToJson()}");
        }

        return prepared;
    }

    private static async Task<JsonObject> LoadLoadedAsync(Session session, CancellationToken cancellationToken)
    {
        var reply = await session.Harness.WireAsync("loaded-identity", "lifecycle_read_identity", new JsonObject(), cancellationToken).ConfigureAwait(false);
        if (reply["loaded"].AsMap() is null)
        {
            throw new AcceptanceException("no game is loaded in the kept process; start from a save instead");
        }

        return new JsonObject { ["kind"] = "loaded" };
    }
}

/// <summary>
/// A loaded, paused, quiet game with frozen needs for native-only cases (Go <c>Session</c>). The brain is in-process
/// in the mod, so a service-driven case does not release a sole bridge slot to a child: <see cref="Release"/> and
/// <see cref="Reattach"/> are trivial.
/// </summary>
public sealed class Session
{
    private readonly IGameHold? _game;

    public Session(Config config, ISessionHost host, IGameHold? game, Harness harness, Report report, IReadOnlyList<string> names)
    {
        Config = config;
        Host = host;
        _game = game;
        Harness = harness;
        Report = report;
        Names = names;
    }

    public Config Config { get; }

    public ISessionHost Host { get; }

    public Harness Harness { get; private set; }

    /// <summary>The discovered tool catalog; empty when the transport cannot enumerate it (every tool is then assumed present).</summary>
    public IReadOnlyList<string> Names { get; private set; }

    /// <summary>The loaded game's colony/load/map identity.</summary>
    public JsonObject Identity { get; private set; } = [];

    /// <summary>The fixture op's reply for a Fixture start, else null.</summary>
    public JsonObject? Prepared { get; private set; }

    public Report Report { get; }

    /// <summary>How long OpenAsync took, from Prepare to the frozen needs.</summary>
    public TimeSpan Boot { get; private set; }

    /// <summary>
    /// Whether the discovered catalog holds <paramref name="tool"/>. An empty catalog (the transport cannot enumerate
    /// tools) assumes every tool is present, as the transport contract says callers do.
    /// </summary>
    public static bool HasTool(IReadOnlyList<string> names, string tool)
    {
        ArgumentNullException.ThrowIfNull(names);
        return names.Count == 0 || Dyn.Contains(names, tool);
    }

    /// <summary>Whether two identity objects name the same colony, load and map.</summary>
    internal static bool MatchesIdentity(JsonObject value, JsonObject want) =>
        value["colonyId"].AsString() == want["colonyId"].AsString()
        && value["loadToken"].AsString() == want["loadToken"].AsString()
        && value["mapId"].AsNumber() == want["mapId"].AsNumber();

    /// <summary>
    /// Runs the native-only preamble: the stale-package check and profile preparation (a Save start's expansions
    /// first), opening the game (a kept process is reused), discovery, the start (a cached debug start, a save load
    /// and, for a Fixture, its op), pause, the frozen needs except <paramref name="keep"/> (LiveNeeds skips the
    /// freeze), and the initial identity. It records package_files, discovery, start, world, quiet, prepared,
    /// frozen_needs and boot_ms on the report; Close adds game_reuse. A failure after the game opened closes it
    /// before the exception leaves.
    /// </summary>
    public static async Task<Session> OpenAsync(
        Config config,
        ISessionHost host,
        Report? report,
        Start? start,
        QuietMode quiet,
        IReadOnlyList<NeedDef>? keep,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(host);
        var began = Stopwatch.StartNew();
        start ??= new DebugStart();
        report ??= new Report();
        if (config.Configuration.Length == 0)
        {
            if (config.FixtureOps.Count == 0)
            {
                config.FixtureOps = StartOps.FixtureOps(start);
            }

            await host.PrepareAsync(config, StartOps.Saves(start), cancellationToken).ConfigureAwait(false);
        }

        report["package_files"] = host.PackageFiles(config);
        var held = await host.OpenGameAsync(config, report, cancellationToken).ConfigureAwait(false);
        var session = new Session(config, host, held, held.Harness, report, []);
        try
        {
            await session.OpenCoreAsync(start, quiet, keep ?? [], cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            held.Close(report);
            throw;
        }

        session.Boot = began.Elapsed;
        report["boot_ms"] = (long)session.Boot.TotalMilliseconds;
        return session;
    }

    /// <summary>
    /// Adopts an already-open harness without opening a game (Go <c>StartLab</c> builds its session this way): the
    /// session is for the start helpers only and holds no game to close.
    /// </summary>
    public static async Task<Session> AdoptAsync(Config config, ISessionHost host, Harness harness, Report report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);
        var names = await harness.DiscoveryAsync(cancellationToken).ConfigureAwait(false) ?? [];
        return new Session(config, host, null, harness, report, names);
    }

    /// <summary>
    /// Runs the start again over the running game: a world change that keeps the durable journal, with the fixture
    /// op, pause, frozen needs and the identity repeated as open did them, and Identity replaced by the reloaded
    /// game's (a kept load token is an error). The report's start, prepared and frozen_needs rows are those of the
    /// reopened world; identity_reloaded records the new identity.
    /// </summary>
    public async Task ReopenAsync(Start start, QuietMode quiet, IReadOnlyList<NeedDef>? keep, CancellationToken cancellationToken)
    {
        var before = Identity["loadToken"].AsString();
        await OpenCoreAsync(start, quiet, keep ?? [], cancellationToken).ConfigureAwait(false);
        if (Identity["loadToken"].AsString() == before)
        {
            throw new AcceptanceException($"reopen kept load token {before}");
        }

        Report["identity_reloaded"] = Identity;
    }

    /// <summary>Pauses the game.</summary>
    public Task PauseAsync(CancellationToken cancellationToken) =>
        Harness.CallAsync("pause", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken);

    /// <summary>Re-reads the loaded identity into <see cref="Identity"/>, recording the read under <paramref name="label"/>.</summary>
    public async Task RefreshIdentityAsync(string label, CancellationToken cancellationToken) =>
        Identity = await ReadIdentityAsync(Harness, label, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Reads <c>lifecycle_read_identity</c> and returns the loaded game's identity (colonyId, loadToken, mapId); the
    /// main menu is an error. The result is a detached copy, safe to place in another request.
    /// </summary>
    public static async Task<JsonObject> ReadIdentityAsync(Harness harness, string label, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);
        var reply = await harness.WireAsync(label, "lifecycle_read_identity", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var (_, loaded) = Harness.Outcome(reply, "loaded");
        var identity = loaded["context"].Member("identity").AsMap();
        return identity is null
            ? throw new AcceptanceException($"{label}: loaded game reports no identity")
            : identity.DeepClone().AsMap()!;
    }

    /// <summary>
    /// Hands the game to a service (Go <c>Release</c> gave up the sole GABP slot). The brain runs in the mod and the
    /// HTTP channel has no exclusive slot, so the harness stays usable and this does nothing.
    /// </summary>
    public static void Release()
    {
    }

    /// <summary>Takes the game back once the service has stopped (Go <c>Reattach</c> reopened the bridge client); the harness never went away.</summary>
    public Harness Reattach() => Harness;

    /// <summary>Ends the hold: the process is kept at the main menu or stopped, and game_reuse lands on the report.</summary>
    public void Close() => _game?.Close(Report);

    private async Task OpenCoreAsync(Start start, QuietMode quiet, IReadOnlyList<NeedDef> keep, CancellationToken cancellationToken)
    {
        var names = await Harness.DiscoveryAsync(cancellationToken).ConfigureAwait(false) ?? [];
        Names = names;
        Harness.Tools = names;
        Report["discovery"] = ToArray(names);
        var row = await StartOps.LoadAsync(start, this, quiet, cancellationToken).ConfigureAwait(false);
        Report["start"] = row;
        if (start is LabStart)
        {
            // A bare lab start replies the lab itself (centre, colonist ids).
            Prepared = row["lab"].AsMap();
        }

        Report["world"] = Host.RecordWorld(Config, start, StartOps.World(start, Host.StartCache));
        await PauseAsync(cancellationToken).ConfigureAwait(false);
        if (Config.QuietWorld)
        {
            // Before the fixture op, so a stage the op saves carries the marker.
            if (!HasTool(names, DebugGame.QuietWorldTool))
            {
                if (quiet == QuietMode.Required)
                {
                    throw new AcceptanceException($"{DebugGame.QuietWorldTool} not in discovery: rebuild the native mod with any -Fixture flag (every fixture build includes it)");
                }

                Report["quiet_world"] = false;
            }
            else
            {
                Report["quiet_world"] = await DebugGame.ApplyQuietWorldAsync(Harness, cancellationToken).ConfigureAwait(false);
            }
        }

        await RefreshIdentityAsync("identity", cancellationToken).ConfigureAwait(false);
        if (start is FixtureStart fixture)
        {
            if (fixture.ArgsFrom is not null)
            {
                JsonObject extra;
                try
                {
                    extra = await fixture.ArgsFrom(Harness, cancellationToken).ConfigureAwait(false) ?? [];
                }
                catch (AcceptanceException e)
                {
                    throw new AcceptanceException($"{fixture.Op} arguments: {e.Message}", e);
                }

                fixture = fixture with { Args = Dyn.Merge(fixture.Args, extra) };
                Report["fixture_args"] = extra;
            }

            var prepared = await StartOps.PrepareFixtureAsync(fixture, this, Identity, cancellationToken).ConfigureAwait(false);
            Prepared = prepared;
            Report["prepared"] = prepared;
        }

        // A QuietIfAvailable harness also runs against a production build, which carries no freeze tool; needs stay
        // live there and the report has no frozen_needs.
        if (quiet == QuietMode.IfAvailable && !HasTool(names, Needs.FreezeNeedsTool))
        {
            return;
        }

        await Needs.RecordFrozenNeedsAsync(Harness, names, Report, keep, cancellationToken).ConfigureAwait(false);
    }

    private static JsonArray ToArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }
}
