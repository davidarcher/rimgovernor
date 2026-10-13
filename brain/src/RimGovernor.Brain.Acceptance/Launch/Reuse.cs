using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>Raised once a <see cref="GameReuse"/> has stopped its game: the RimWorld process is gone and the caller must not attempt another case (Go <c>ErrReuseRetired</c>).</summary>
public sealed class ReuseRetiredException : AcceptanceException
{
    public ReuseRetiredException(string reason)
        : base(reason.Length == 0 ? "reusable game retired" : "reusable game retired: " + reason)
    {
        Reason = reason;
    }

    /// <summary>Why the game was retired.</summary>
    public string Reason { get; }
}

/// <summary>
/// The slice of a resource's stock a reload must restore: units from the colony-facts census and, when a bounded
/// supplies read was available, the forbidden count (-1 when it was not sampled).
/// </summary>
public readonly record struct StockCounts(long Units, long Forbidden);

/// <summary>What BeginCase/EndCase observe about the loaded game; plain data so <see cref="GameReuse.CheckReset"/> needs no game.</summary>
public sealed record ResetState
{
    public string ColonyId { get; init; } = "";

    public string LoadToken { get; init; } = "";

    public double Tick { get; init; }

    public bool Paused { get; init; }

    public bool AuthorityActive { get; init; }

    public int Drafted { get; init; }

    public IReadOnlyDictionary<string, StockCounts> Stock { get; init; } = new Dictionary<string, StockCounts>(StringComparer.Ordinal);
}

/// <summary>One case's handle: its loaded identity and a Harness recording evidence under its own output directory.</summary>
public sealed class ReuseCase
{
    internal ReuseCase(string name, string save, string output, Harness harness)
    {
        Name = name;
        Save = save;
        Output = output;
        Harness = harness;
        Row = new JsonObject { ["case"] = name, ["save"] = save, ["output"] = output };
    }

    public string Name { get; }

    public string Save { get; }

    public string Output { get; }

    public JsonObject Identity { get; internal set; } = [];

    public Harness Harness { get; }

    public ResetState Reset { get; internal set; } = new();

    internal Stopwatch Began { get; } = Stopwatch.StartNew();

    internal JsonObject Row { get; }
}

/// <summary>
/// Keeps one owned RimWorld process alive across several acceptance cases (Go <c>GameReuse</c>). Every case begins with a
/// reload of its save into the same process and a reset check (<see cref="ObserveResetAsync"/>/<see cref="CheckReset"/>)
/// that the reload actually gave the case a clean baseline; a case that fails, or that leaves ownership behind, retires
/// the whole game rather than letting the next case inherit its state. Fresh-process mode (one session per binary) stays
/// the default everywhere; this is an opt-in optimisation for multi-case loops (the case runner's kept process is the
/// registry's form of it).
/// <para>
/// There is no sole-slot constraint any more: the brain runs in the mod and serves its HTTP channel to any number of
/// clients, so a case that drives a service does not have to release the harness first. <see cref="ReleaseSessionAsync"/>
/// stays for callers that want the session closed anyway; the next use reattaches to the same process.
/// </para>
/// <para>
/// Mod static state is process-scoped and is NOT reset by a reload (see contracts/native-static-state.md). Cases asserting
/// on those statics must stay in fresh-process mode.
/// </para>
/// </summary>
public sealed class GameReuse : IAsyncDisposable
{
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RetireBudget = TimeSpan.FromSeconds(60);

    private readonly Dictionary<string, string> _tokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResetState> _baselines = new(StringComparer.Ordinal);
    private readonly List<JsonObject> _rows = [];
    private Harness? _harness;
    private ReuseCase? _current;
    private bool _retired;
    private string _reason = "";

    private GameReuse(Config config, string output)
    {
        Config = config;
        Output = output;
    }

    public Config Config { get; }

    /// <summary>This lifecycle's own evidence (session/retire rows); each case records under its own directory.</summary>
    public string Output { get; }

    /// <summary>The one launch this lifecycle paid for.</summary>
    public TimeSpan StartupDuration { get; private set; }

    /// <summary>Whether the game has been stopped.</summary>
    public bool IsRetired => _retired;

    /// <summary>Why the game was retired; empty while it lives.</summary>
    public string RetiredReason => _reason;

    /// <summary>
    /// Lists every way <paramref name="now"/> fails the reset contract against what earlier cases established:
    /// <paramref name="baseline"/> is the first observation of the same save (null when this is its first load),
    /// <paramref name="tokens"/> the load tokens every prior case received. Returns without throwing when the reload is clean.
    /// </summary>
    public static void CheckReset(ResetState now, ResetState? baseline, IReadOnlyDictionary<string, string> tokens)
    {
        ArgumentNullException.ThrowIfNull(now);
        ArgumentNullException.ThrowIfNull(tokens);
        var problems = new List<string>();
        if (now.LoadToken.Length == 0 || now.ColonyId.Length == 0)
        {
            problems.Add("missing colony id or load token");
        }

        if (tokens.TryGetValue(now.LoadToken, out var prior))
        {
            problems.Add($"load token \"{now.LoadToken}\" was already issued to case \"{prior}\"");
        }

        if (!now.Paused)
        {
            problems.Add("game is not paused after the reload");
        }

        if (now.AuthorityActive)
        {
            problems.Add("authority is still active after the reload");
        }

        if (now.Drafted > 0)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{now.Drafted} drafted colonist(s) survive the reload"));
        }

        // ColonyId is deliberately not compared: a fixture save never written by the mod has no persisted colony id, so
        // each load mints a new one.
        if (baseline is not null)
        {
            if (now.Tick != baseline.Tick)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"tick {now.Tick} differs from the baseline tick {baseline.Tick}"));
            }

            var diff = StockDiff(baseline.Stock, now.Stock);
            if (diff.Length > 0)
            {
                problems.Add("stock differs from the baseline: " + diff);
            }
        }

        if (problems.Count > 0)
        {
            throw new AcceptanceException(string.Join("; ", problems));
        }
    }

    private static string StockDiff(IReadOnlyDictionary<string, StockCounts> want, IReadOnlyDictionary<string, StockCounts> got)
    {
        var diffs = new List<string>();
        var names = new SortedSet<string>(want.Keys, StringComparer.Ordinal);
        names.UnionWith(got.Keys);
        foreach (var name in names)
        {
            var wok = want.TryGetValue(name, out var w);
            var gok = got.TryGetValue(name, out var g);
            if (!wok || !gok || w.Units != g.Units || (w.Forbidden >= 0 && g.Forbidden >= 0 && w.Forbidden != g.Forbidden))
            {
                diffs.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{name} want={{Units:{w.Units} Forbidden:{w.Forbidden}}} got={{Units:{g.Units} Forbidden:{g.Forbidden}}}"));
            }
        }

        return string.Join(", ", diffs);
    }

    /// <summary>Launches the game once and returns a lifecycle at the main menu; nothing is loaded until the first <see cref="BeginCaseAsync"/>.</summary>
    public static async Task<GameReuse> OpenReusableGameAsync(Config config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Configuration.Length == 0)
        {
            throw new AcceptanceException("reuse: Config.PrepareConfig must run first");
        }

        var output = Path.Combine(config.Output, "reuse");
        try
        {
            _ = Directory.CreateDirectory(output);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new AcceptanceException($"create {output}: {e.Message}", e);
        }

        var reuse = new GameReuse(config, output);
        var started = Stopwatch.StartNew();
        _ = await reuse.SessionAsync(cancellationToken).ConfigureAwait(false);
        reuse.StartupDuration = started.Elapsed;
        return reuse;
    }

    /// <summary>The live harness session, reopened (an attach to the same process) when a case released it.</summary>
    public async Task<Harness> SessionAsync(CancellationToken cancellationToken)
    {
        if (_retired)
        {
            throw new ReuseRetiredException("");
        }

        if (_harness is not null)
        {
            return _harness;
        }

        INativeTransport transport;
        try
        {
            transport = await GameSessions.OpenBridgeSessionAsync(Config.Configuration, GameIdOf(Config), SessionTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"reuse: reopen session: {e.Message}", e);
        }

        _harness = new Harness(transport, Output);
        return _harness;
    }

    /// <summary>Closes the harness session; the game keeps running (use <see cref="RetireAsync"/> to stop it).</summary>
    public ValueTask DisposeAsync() => new(ReleaseSessionAsync());

    /// <summary>Closes the harness session without stopping the game; the next use reattaches.</summary>
    public async Task ReleaseSessionAsync()
    {
        var harness = _harness;
        _harness = null;
        if (harness is not null)
        {
            await harness.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reloads <paramref name="save"/> into the running game, verifies the reset contract and returns the case handle. Any
    /// contract violation retires the game and throws <see cref="ReuseRetiredException"/>.
    /// </summary>
    public async Task<ReuseCase> BeginCaseAsync(string name, string save, string output, CancellationToken cancellationToken)
    {
        if (_retired)
        {
            throw new ReuseRetiredException(_reason);
        }

        if (_current is not null)
        {
            throw new AcceptanceException($"reuse: case \"{_current.Name}\" is still open");
        }

        try
        {
            _ = Directory.CreateDirectory(output);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new AcceptanceException($"create {output}: {e.Message}", e);
        }

        var session = await SessionAsync(cancellationToken).ConfigureAwait(false);

        // A harness over the shared session whose evidence lands in this case's directory; it is never closed itself,
        // because that would end the session.
        var h = new Harness(session.Transport, output) { Tools = session.Tools };
        var c = new ReuseCase(name, save, output, h);
        _current = c;
        try
        {
            await LoadAsync(h, save, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw await AbandonAsync(c, $"case \"{name}\" load: {e.Message}", cancellationToken).ConfigureAwait(false);
        }

        try
        {
            _ = await h.CallAsync("reuse-pause", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw await AbandonAsync(c, $"case \"{name}\" pause: {e.Message}", cancellationToken).ConfigureAwait(false);
        }

        ResetState state;
        JsonObject identity;
        try
        {
            (state, identity) = await ObserveResetAsync(h, "reuse-begin", cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw await AbandonAsync(c, $"case \"{name}\" observe: {e.Message}", cancellationToken).ConfigureAwait(false);
        }

        var baseline = _baselines.TryGetValue(save, out var first) ? first : null;
        try
        {
            CheckReset(state, baseline, _tokens);
        }
        catch (AcceptanceException e)
        {
            c.Row["reset_violation"] = e.Message;
            throw await AbandonAsync(c, $"case \"{name}\" reset contract: {e.Message}", cancellationToken).ConfigureAwait(false);
        }

        if (baseline is null)
        {
            _baselines[save] = state;
            _rows.Add(new JsonObject { ["baseline"] = save, ["colonyId"] = state.ColonyId, ["tick"] = state.Tick, ["stocks"] = state.Stock.Count });
        }

        _tokens[state.LoadToken] = name;
        c.Identity = identity;
        c.Reset = state;
        c.Row["loadToken"] = state.LoadToken;
        c.Row["colonyId"] = state.ColonyId;
        c.Row["tick"] = state.Tick;
        c.Row["readiness_ms"] = (long)c.Began.Elapsed.TotalMilliseconds;
        return c;
    }

    /// <summary>Records a case that never got past BeginCase and retires the game; the result is the exception to throw.</summary>
    private async Task<ReuseRetiredException> AbandonAsync(ReuseCase c, string reason, CancellationToken cancellationToken)
    {
        c.Row["failed"] = true;
        c.Row["duration_ms"] = (long)c.Began.Elapsed.TotalMilliseconds;
        _rows.Add(c.Row);
        _current = null;
        return await RetireCoreAsync(reason, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes the case. A failed case retires the game. A successful case must have released everything it owned:
    /// authority inactive and no drafted colonist; otherwise the game is retired too, since the next case could not trust
    /// its baseline. Retirement throws <see cref="ReuseRetiredException"/>.
    /// </summary>
    public async Task EndCaseAsync(ReuseCase c, bool failed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (!ReferenceEquals(_current, c))
        {
            throw new AcceptanceException("reuse: EndCase for a case that is not open");
        }

        try
        {
            if (_retired)
            {
                throw new ReuseRetiredException(_reason);
            }

            if (failed)
            {
                throw await RetireCoreAsync($"case \"{c.Name}\" failed", cancellationToken).ConfigureAwait(false);
            }

            try
            {
                _ = await SessionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                throw await RetireCoreAsync($"case \"{c.Name}\" end: {e.Message}", cancellationToken).ConfigureAwait(false);
            }

            ResetState state;
            try
            {
                (state, _) = await ObserveResetAsync(c.Harness, "reuse-end", cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                throw await RetireCoreAsync($"case \"{c.Name}\" end observe: {e.Message}", cancellationToken).ConfigureAwait(false);
            }

            var problems = new List<string>();
            if (!string.Equals(state.LoadToken, c.Reset.LoadToken, StringComparison.Ordinal))
            {
                problems.Add("the case changed the loaded map");
            }

            if (state.AuthorityActive)
            {
                problems.Add("authority left active");
            }

            if (state.Drafted > 0)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{state.Drafted} drafted colonist(s) left behind"));
            }

            if (problems.Count > 0)
            {
                var joined = string.Join("; ", problems);
                c.Row["quiescence_violation"] = joined;
                throw await RetireCoreAsync($"case \"{c.Name}\" left the game unclean: {joined}", cancellationToken).ConfigureAwait(false);
            }

            c.Row["quiescent"] = true;
        }
        finally
        {
            c.Row["duration_ms"] = (long)c.Began.Elapsed.TotalMilliseconds;
            c.Row["failed"] = failed;
            _rows.Add(c.Row);
            _current = null;
        }
    }

    /// <summary>Stops the game and closes the session. Idempotent.</summary>
    public async Task RetireAsync(string reason, CancellationToken cancellationToken)
    {
        if (_retired)
        {
            return;
        }

        _ = await RetireCoreAsync(reason, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ReuseRetiredException> RetireCoreAsync(string reason, CancellationToken cancellationToken)
    {
        if (_retired)
        {
            return new ReuseRetiredException(_reason);
        }

        _retired = true;
        _reason = reason;
        var row = new JsonObject { ["retired"] = reason };
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(RetireBudget);
        try
        {
            var stopped = await GameProcess.StopAsync(HeadlessProfile.MustAbs(Config.Configuration), GameIdOf(Config), bound.Token).ConfigureAwait(false);
            row["stop"] = stopped ? "stopped" : "not running";
        }
        catch (AcceptanceException e)
        {
            row["stop_error"] = e.Message;
        }

        if (_harness is not null)
        {
            await _harness.CloseAsync().ConfigureAwait(false);
            _harness = null;
        }

        _rows.Add(row);
        return new ReuseRetiredException(reason);
    }

    /// <summary>
    /// The lifecycle's evidence rows for a report: the one startup, each baseline sample, each case (load token,
    /// readiness, quiescence) and the retirement, in order.
    /// </summary>
    public IReadOnlyList<JsonObject> Rows()
    {
        var rows = new List<JsonObject> { new() { ["startup_ms"] = (long)StartupDuration.TotalMilliseconds } };
        rows.AddRange(_rows);
        return rows;
    }

    /// <summary>Writes the lifecycle's rows into <paramref name="report"/> under "reuse".</summary>
    public void Record(Report report)
    {
        ArgumentNullException.ThrowIfNull(report);
        report["reuse_game"] = true;
        var rows = new JsonArray();
        foreach (var row in Rows())
        {
            rows.Add(row.DeepClone());
        }

        report["reuse"] = rows;
        if (_retired)
        {
            report["reuse_retired"] = _reason;
        }
    }

    private static string GameIdOf(Config config) => config.GameId.Length > 0 ? config.GameId : HeadlessProfile.TrialGameId;

    // Every load goes through the harness-level rimgovernor/load_game_ready. The trusted rimgovernor/lifecycle_load path is
    // not usable here: it requires the expected identity to survive the load, but a fixture save that was never written by
    // the mod carries no persisted colony id, so native mints a fresh one per load and lifecycle_load reports the load as
    // superseded. loadaccept covers that path with a save it wrote itself.
    private static async Task LoadAsync(Harness h, string save, CancellationToken cancellationToken) =>
        _ = await h.CallAsync(
            "reuse-load",
            "rimgovernor/load_game_ready",
            new JsonObject
            {
                ["saveName"] = save,
                ["readiness"] = "visual",
                ["timeoutMs"] = 90000,
                ["ignoreModCompatibility"] = false,
            },
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Samples the loaded game's <see cref="ResetState"/> (identity, tick, pause, authority, owned drafts and the
    /// resource census) and returns it with the identity read; it is the reset check's sample and the world census
    /// <c>acceptance fixture</c> prints after an op.
    /// </summary>
    public static async Task<(ResetState State, JsonObject Identity)> ObserveResetAsync(Harness h, string label, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(h);
        var identityReply = await h.WireAsync(label + "-identity", "lifecycle_read_identity", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var (_, loaded) = Harness.Outcome(identityReply, "loaded");
        var loadedContext = loaded["context"].AsMap();
        var identity = loadedContext?["identity"].AsMap() ?? [];
        var colonyId = identity["colonyId"].AsString();
        var loadToken = identity["loadToken"].AsString();
        var tick = loadedContext?["tick"].AsNumber() ?? 0;
        var paused = loaded["paused"].AsBool() == true;

        var statusReply = await h.WireAsync(
            label + "-authority",
            "authority_read_status",
            new JsonObject { ["identity"] = identity.DeepClone() },
            cancellationToken).ConfigureAwait(false);
        var (_, status) = Harness.Outcome(statusReply, "status");
        var authorityActive = status.ContainsKey("active");

        var pawnsReply = await h.WireAsync(
            label + "-pawns",
            "observations_list_pawns",
            new JsonObject
            {
                ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() },
                ["filter"] = new JsonObject { ["colonist"] = true },
            },
            cancellationToken).ConfigureAwait(false);
        var (_, pawns) = Harness.Outcome(pawnsReply, "observed");
        var drafted = pawns["pawns"].AsSlice().Count(row => row.AsMap()?["drafted"].AsBool() == true);

        // Stock: the colony-facts resource census (units per definition) is the bounded, save-independent sample; an
        // unfiltered supplies listing exceeds the native 256-row item limit on an ordinary colony. Forbidden counts come
        // from a supplies read scoped to those definitions and are skipped (-1) when native declines that read as too
        // large as well.
        var factsReply = await h.WireAsync(
            label + "-stock",
            "observations_read_colony_facts",
            new JsonObject
            {
                ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() },
                ["planning"] = false,
            },
            cancellationToken).ConfigureAwait(false);
        var (_, facts) = Harness.Outcome(factsReply, "observed");
        var stock = new Dictionary<string, StockCounts>(StringComparer.Ordinal);
        var defNames = new JsonArray();
        foreach (var node in facts["resources"].AsSlice())
        {
            var row = node.AsMap();
            var name = row?["defName"].AsString() ?? "";
            if (name.Length == 0)
            {
                continue;
            }

            stock[name] = new StockCounts((long)row!["units"].AsNumber(), -1);
            defNames.Add(name);
        }

        if (defNames.Count > 0)
        {
            var suppliesReply = await h.WireAsync(
                label + "-forbidden",
                "observations_list_supplies",
                new JsonObject
                {
                    ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() },
                    ["filter"] = new JsonObject { ["defNames"] = defNames, ["ownership"] = "STOCK_OWNERSHIP_ALL" },
                },
                cancellationToken).ConfigureAwait(false);
            try
            {
                var (_, supplies) = Harness.Outcome(suppliesReply, "observed");
                foreach (var node in supplies["stocks"].AsSlice())
                {
                    var row = node.AsMap();
                    var defName = row?["definition"].AsMap()?["defName"].AsString() ?? "";
                    if (stock.TryGetValue(defName, out var counts))
                    {
                        stock[defName] = counts with { Forbidden = (long)row!["forbidden"].AsNumber() };
                    }
                }
            }
            catch (AcceptanceException e) when (suppliesReply["unavailable"].AsMap() is null)
            {
                throw new AcceptanceException($"supplies: {e.Message} (reply: {suppliesReply.ToJson()})", e);
            }
            catch (AcceptanceException)
            {
                // Native declined the bounded read; the forbidden counts stay unsampled.
            }
        }

        var state = new ResetState
        {
            ColonyId = colonyId,
            LoadToken = loadToken,
            Tick = tick,
            Paused = paused,
            AuthorityActive = authorityActive,
            Drafted = drafted,
            Stock = stock,
        };
        return (state, identity.DeepClone().AsMap()!);
    }

    /// <summary>
    /// Drives <c>rimgovernor/lifecycle_read_load</c> until a completed outcome, a native failure, or a bounded number of
    /// attempts elapses. A pending or superseded outcome is otherwise a definite non-completion the caller must fail on
    /// rather than loop past. Returns the completed outcome's value.
    /// </summary>
    public static async Task<JsonObject> PollLoadAsync(Harness h, JsonObject first, string requestId, string label, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(h);
        ArgumentNullException.ThrowIfNull(first);
        var reply = first;
        for (var attempt = 0; attempt < 300; attempt++)
        {
            if (Case(reply, "completed") is { } completed)
            {
                return completed;
            }

            if (Case(reply, "superseded") is { } superseded)
            {
                throw new AcceptanceException($"load was superseded: {superseded["detail"].ToJson()}");
            }

            if (reply["failure"].AsMap() is { } failure)
            {
                throw new AcceptanceException($"load failed: {failure["code"].AsString()}");
            }

            if (Case(reply, "pending") is null)
            {
                throw new AcceptanceException($"unexpected load reply shape: {reply.ToJson()}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
            reply = await h.WireAsync(
                string.Create(CultureInfo.InvariantCulture, $"{label}-{attempt}"),
                "lifecycle_read_load",
                new JsonObject { ["requestId"] = requestId },
                cancellationToken).ConfigureAwait(false);
        }

        throw new AcceptanceException("load did not complete within the polling budget");
    }

    /// <summary>The value of the reply's single-field oneof when it is the named case.</summary>
    private static JsonObject? Case(JsonObject reply, string name) =>
        reply.Count == 1 && reply.TryGetPropertyValue(name, out var value) ? value as JsonObject : null;
}
