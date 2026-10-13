using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The runner's <see cref="ISession"/> over the lifecycle's <see cref="Session"/> (Go <c>cases.session</c>), plus the
/// services a case launched so the runner can stop them before the game closes. The lifecycle session is null for an
/// Owned case, which opens its own game on <see cref="Config"/>.
/// </summary>
public sealed class CaseSession : ISession, IRingHost
{
    /// <summary>
    /// The checkpoint state key under which the ring records the last stage a run completed, so a resume from a later
    /// ring bundle skips the stages it already passed.
    /// </summary>
    public const string StageStateKey = "stage_completed";

    private readonly AcceptanceCase _case;
    private readonly Report _report;
    private readonly IScenarioDriver? _scenario;
    private readonly List<ServiceProcess> _services = [];
    private readonly JsonArray _stageRows = [];
    private readonly string _seed;
    private readonly string _stagesDir;
    private readonly string _through;
    private readonly StagePlan _stagePlan;
    private Config? _config;
    private Session? _opened;
    private JsonObject? _prepared;
    private int _stageNext;
    private IScenarioRuntime? _runtime;
    private Harness? _runtimeHarness;

    internal CaseSession(AcceptanceCase c, Report report, IScenarioDriver? scenario, string seed, int runSeed, StagePlan stagePlan, string stagesDir, string through)
    {
        _case = c;
        _report = report;
        _scenario = scenario;
        _seed = seed;
        RunSeed = runSeed;
        _stagePlan = stagePlan;
        _stagesDir = stagesDir;
        _through = through;
    }

    /// <inheritdoc />
    public Config Config => _config ?? throw new InvalidOperationException("the session has no configuration yet");

    /// <inheritdoc />
    public Harness Harness => Opened.Harness;

    /// <inheritdoc />
    public IReadOnlyList<string> Names => Opened.Names;

    /// <inheritdoc />
    public JsonObject Identity => Opened.Identity;

    /// <inheritdoc />
    public JsonObject? Prepared => _prepared ?? _opened?.Prepared;

    /// <inheritdoc />
    public Report Report => _report;

    /// <inheritdoc />
    public ServeSpec Spec => _case.Serve ?? new ServeSpec();

    /// <inheritdoc />
    public int RunSeed { get; }

    /// <inheritdoc />
    public Checkpoint? Resumed { get; internal set; }

    /// <inheritdoc />
    public Checkpoint? Staged => _stagePlan.Staged ? _stagePlan.Entry : null;

    /// <inheritdoc />
    public JsonObject? Prior { get; internal set; }

    /// <summary>What <see cref="RequestId"/> appends on a resumed run, empty on a fresh one.</summary>
    internal string ResumeSuffix { get; set; } = "";

    /// <summary>The run's checkpoint ring, null when it never checkpoints.</summary>
    internal CheckpointRing? Ring { get; set; }

    /// <summary>The ring stage bundles are captured into, null when staging is off.</summary>
    internal CheckpointRing? Stages { get; set; }

    /// <summary>Times the run phase, for a stage bundle's offset when there is no ring.</summary>
    internal Stopwatch RunStarted { get; set; } = Stopwatch.StartNew();

    /// <summary>Cuts the Run body with the given cause (a breakpoint, or the -through stage being cached).</summary>
    internal Action<Exception>? CutRun { get; set; }

    /// <summary>The per-stage rows the report carries.</summary>
    internal JsonArray StageRows => _stageRows;

    /// <summary>The lifecycle session; null for an Owned case.</summary>
    internal Session? OpenedSession => _opened;

    /// <inheritdoc />
    Harness? IRingHost.LiveHarness => _opened?.Harness;

    private Session Opened => _opened ?? throw new AcceptanceException("no game open: an Owned case holds its own session");

    /// <summary>Installs the run's configuration (the runner builds it before anything opens).</summary>
    internal void UseConfig(Config config) => _config = config;

    /// <summary>Installs the opened lifecycle session.</summary>
    internal void UseSession(Session opened) => _opened = opened;

    /// <summary>The fixture op's reply a bundle recorded replaces the one the session found (a resumed or staged run opens on a save, not the op).</summary>
    internal void UsePrepared(JsonObject prepared) => _prepared = prepared;

    /// <inheritdoc />
    public string RequestId(string baseId) => ResumeSuffix.Length == 0 ? baseId : baseId + "-" + ResumeSuffix;

    /// <inheritdoc />
    public void Release()
    {
        _ = Opened;
    }

    /// <inheritdoc />
    public Harness Reattach() => Opened.Harness;

    /// <inheritdoc />
    public async Task<Harness> ReloadAsync(CancellationToken cancellationToken)
    {
        var opened = Opened;
        var start = CaseRunner.SeededStart(_case.Start!, _seed);
        await opened.ReopenAsync(start, _case.Quiet, _case.KeepNeeds(), cancellationToken).ConfigureAwait(false);
        await CaseRunner.StaffAsync(_case, opened.Harness, _report, cancellationToken).ConfigureAwait(false);
        return opened.Harness;
    }

    /// <inheritdoc />
    public async Task<ServiceProcess> LaunchAsync(ServiceLaunch launch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var opened = Opened;

        // The player's speed lives in the loaded game, so a relaunch keeps the one written before the first launch.
        await WritePlayerSpeedAsync(opened.Harness, launch.PlayerSpeed, cancellationToken).ConfigureAwait(false);
        Release();
        var service = await ServiceProcess.LaunchServiceAsync(Config, launch, _report, cancellationToken).ConfigureAwait(false);
        _services.Add(service);
        return service;
    }

    /// <inheritdoc />
    public async Task<ServiceProcess> ServeAsync(ServeSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var opened = Opened;

        // The handle's own request ids (resume, acknowledge, the watch's commands) carry the run suffix like the case's:
        // a resumed or staged run's restored store already holds the earlier run's under the case's prefix with that
        // run's identity, and the same requestId with a new loadToken is a conflict (409).
        var prefix = spec.Prefix.Length == 0 ? "serve" : spec.Prefix;
        spec = spec with { Prefix = RequestId(prefix) };
        var service = await ServiceProcess.ServeAsync(Config, opened.Harness, opened.Identity, spec, _report, cancellationToken).ConfigureAwait(false);
        _services.Add(service);
        return service;
    }

    /// <summary>
    /// The case's scenario runtime over a controller clock acquired on first use; the discovered tools let an advance
    /// dismiss the letters it acknowledges. The runtime is cached per harness: a runtime built on another harness would
    /// bind a closed transport.
    /// </summary>
    public async Task<IScenarioRuntime> RuntimeAsync(CancellationToken cancellationToken)
    {
        var driver = _scenario ?? throw new AcceptanceException("no scenario clock is wired into this runner (RunDependencies.Scenario)");
        var harness = Harness;
        if (_runtime is not null && ReferenceEquals(_runtimeHarness, harness))
        {
            return _runtime;
        }

        var runtime = await driver.CreateRuntimeAsync(harness, Identity, _report, Names, cancellationToken).ConfigureAwait(false);
        _runtime = runtime;
        _runtimeHarness = harness;
        return runtime;
    }

    /// <inheritdoc />
    public async Task<JsonObject> AdvanceAsync(ulong ticks, IReadOnlyList<IScenarioAdvanceOption>? options, CancellationToken cancellationToken)
    {
        var runtime = await RuntimeAsync(cancellationToken).ConfigureAwait(false);
        return await _scenario!.AdvanceAsync(runtime, ticks, _case.Letters, options ?? [], cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task StageAsync(string name, Func<CancellationToken, Task> stage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stage);
        var index = -1;
        for (var i = 0; i < _case.Stages.Count; i++)
        {
            if (_case.Stages[i] == name)
            {
                index = i;
            }
        }

        if (index < 0)
        {
            throw new AcceptanceException($"stage \"{name}\" is not declared by {_case.Name} (Stages: [{string.Join(' ', _case.Stages)}])");
        }

        if (index != _stageNext)
        {
            var next = _stageNext < _case.Stages.Count ? _case.Stages[_stageNext] : "(none)";
            throw new AcceptanceException($"stage \"{name}\" out of order: {_case.Name} declares [{string.Join(' ', _case.Stages)}] and the next stage is \"{next}\"");
        }

        _stageNext++;
        var began = Stopwatch.StartNew();
        var row = new JsonObject { ["name"] = name };
        _stageRows.Add(row);
        if (index <= _stagePlan.Hit)
        {
            row["outcome"] = "hit";
            row["path"] = Path.Combine(_stagesDir, name);
            row["wall_ms"] = 0L;
            TripStage(name);
            EndThrough(name, "hit");
            return;
        }

        try
        {
            await stage(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            row["outcome"] = "failed";
            row["wall_ms"] = began.ElapsedMilliseconds;
            throw;
        }

        if (_opened is null)
        {
            throw new AcceptanceException($"stage \"{name}\": no game open");
        }

        var holder = HoldingService();
        if (holder is not null)
        {
            throw new AcceptanceException($"stage \"{name}\": a service still holds the game ({holder.Label}); stop it before the staging block returns");
        }

        // The ring's later bundles record the stage as done, so a resume from one (which replays the body from the top)
        // skips it as a hit would.
        Checkpoints.SetState(StageStateKey, JsonValue.Create(name));
        try
        {
            if (Stages is null)
            {
                row["outcome"] = "uncached";
                row["wall_ms"] = began.ElapsedMilliseconds;
                if (name == _through)
                {
                    throw new AcceptanceException($"stage \"{name}\": -through needs the bundle cached, but staging is off ({_stagePlan.Off})");
                }

                return;
            }

            // The bundle's offset is the run's, so a hit sets the ring's Base and its labels stay monotonic; the case
            // state travels with it.
            Stages.Base = RunStarted.Elapsed;
            if (Ring is not null)
            {
                Stages.Base = Ring.Offset();
                Stages.State = Ring.StateSnapshot() ?? [];
            }

            Checkpoint entry;
            try
            {
                entry = await Stages.CaptureAsync(name, cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"stage \"{name}\": capture: {e.Message}", e);
            }

            row["outcome"] = "captured";
            row["path"] = entry.Path;
            row["wall_ms"] = began.ElapsedMilliseconds;
            row["capture_ms"] = entry.WallMs;
            EndThrough(name, "captured");
        }
        finally
        {
            TripStage(name);
        }
    }

    /// <inheritdoc />
    ICheckpointService? IRingHost.LatestService() => ServiceProcess.LatestService() ?? _services.LastOrDefault(p => p.Running());

    /// <summary>Stops any service still running (stopping is idempotent) before the session closes.</summary>
    internal async Task StopServicesAsync()
    {
        foreach (var service in _services)
        {
            _ = await service.StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs the case's Postmortem phase after Run: the services the case launched are stopped and the harness holds the
    /// game again.
    /// </summary>
    internal async Task PostmortemAsync(CancellationToken cancellationToken)
    {
        await StopServicesAsync().ConfigureAwait(false);
        _ = Reattach();
        try
        {
            await _case.Postmortem!(this, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException("postmortem: " + e.Message, e);
        }
    }

    // A service that still holds the game: the package's latest launch, else any of ours still running.
    private ServiceProcess? HoldingService() => ServiceProcess.LatestService() ?? _services.FirstOrDefault(p => p.Running());

    // Fires the run's stage breakpoint once the named stage is done, whether its block ran or a bundle covered it.
    private void TripStage(string name)
    {
        if (Ring is { } ring && ring.Break.Stage == name)
        {
            ring.Trip("stage " + name + " done");
        }
    }

    // Ends the run after the named stage when it is the run's -through stage: the Run body is cut and the report says
    // which stage the run ended on and whether its bundle was taken by this run or found cached.
    private void EndThrough(string name, string outcome)
    {
        if (name != _through)
        {
            return;
        }

        _report["staged_through"] = new JsonObject { ["stage"] = name, ["outcome"] = outcome, ["path"] = Path.Combine(_stagesDir, name) };
        var cause = new StagedThroughException(name, outcome);
        CutRun?.Invoke(cause);
        throw cause;
    }

    // Writes the speed the service will follow as the player's own choice, as ServiceProcess.ServeAsync does for a case
    // that lets it load: a boosted run (Ultrafast, the headless profile's test acceleration) writes nothing.
    private static async Task WritePlayerSpeedAsync(Harness harness, string playerSpeed, CancellationToken cancellationToken)
    {
        var speed = playerSpeed;
        if (speed.Length == 0)
        {
            speed = Environment.GetEnvironmentVariable(ServiceProcess.ClockSpeedEnv) is { Length: > 0 } configured ? configured : "Ultrafast";
            if (speed == "Ultrafast")
            {
                return;
            }
        }

        _ = await harness.WireAsync("player-speed-hook", "lifecycle_read_identity", new JsonObject(), cancellationToken).ConfigureAwait(false);
        foreach (var value in new[] { speed, "Paused" })
        {
            _ = await harness.CallAsync(
                "player-speed-" + value.ToLowerInvariant(),
                "rimgovernor/set_time_speed",
                new JsonObject { ["speed"] = value, ["ultraSpeedBoost"] = false },
                cancellationToken).ConfigureAwait(false);
        }
    }

    internal static string Tag(int value) => value.ToString(CultureInfo.InvariantCulture);
}
