using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

// The service-driven family's lifecycle (Go serve.go). The Go harness launched a `rimgovernor serve` child beside the
// game and held the sole GABP slot until it released it. The brain now runs in-process in the mod, so a
// ServiceProcess is a handle on the player API the mod's local HTTP channel serves: "launching" the service means
// finding the channel's endpoint file and checking the brain answers, and "stopping" it ends the handle's own work
// (keep-alive, evidence) without touching the game.

/// <summary>
/// Names <see cref="ServiceProcess.LaunchServiceAsync"/>'s inputs beyond the harness's <see cref="Config"/>: the
/// routine families the brain must run and where the launch is recorded. It is <see cref="ServeSpec"/>'s older
/// shape; new code uses <see cref="ServiceProcess.ServeAsync"/>.
/// </summary>
public sealed record ServiceLaunch
{
    /// <summary>The routine families the brain must be composed with (RIMGOVERNOR_ROUTINE_FAMILIES); null accepts whatever it runs.</summary>
    public IReadOnlyList<string>? Families { get; init; }

    /// <summary>Further environment entries (KEY=value). The brain already runs inside the game, so they cannot be applied; they are recorded on the launch entry.</summary>
    public IReadOnlyList<string> Env { get; init; } = [];

    /// <summary>
    /// When set, where this launch's service directory (evidence) goes instead of <see cref="Config.Output"/>, and
    /// <see cref="Report"/>, when set, where the launch is recorded instead of the harness's report: a case that
    /// launches once per sub-run (speedmatrix) keeps each apart.
    /// </summary>
    public string Output { get; init; } = "";

    public Report? Report { get; init; }

    /// <summary>The player's speed choice, as <see cref="ServeSpec.PlayerSpeed"/>.</summary>
    public string PlayerSpeed { get; init; } = "";
}

/// <summary>
/// One handle on the brain's player API, obtained by <see cref="ServeAsync"/> (or <see cref="LaunchServiceAsync"/>)
/// against the game the harness prepared. Its waits (<see cref="WaitReviewAsync"/>, <see cref="WaitPlanAsync"/>,
/// <see cref="WaitMethodAsync"/>, <see cref="WaitPlanTerminalAsync"/>, <see cref="WaitStepAsync"/>) end as soon as
/// the brain's process, the game, exits on its own. It is also the running service a checkpoint capture saves
/// through (<see cref="ICheckpointService"/>).
/// </summary>
public sealed partial class ServiceProcess : ICheckpointService
{
    /// <summary>The player API's base address from the channel's endpoint file (the Go serve URL).</summary>
    public string Url { get; private set; } = "";

    /// <summary>The process that hosts the brain: the game's pid, from the endpoint file.</summary>
    public int Pid { get; private set; }

    /// <summary>
    /// The explicit-player session token. The channel has no token (its request guard is the only access check), so
    /// this is empty; it stays so callers that thread it through keep working.
    /// </summary>
    public string Token { get; private set; } = "";

    /// <summary>The loaded colony the service attached to, set by <see cref="ServeAsync"/>.</summary>
    public JsonObject? Identity { get; private set; }

    /// <summary>1 for the first service of a run, 2 for a restart, and so on.</summary>
    public int Launch { get; private set; }

    /// <summary>What the service was launched with.</summary>
    public ServeSpec Spec { get; private set; } = new();

    /// <summary>The brain's OTLP log ring (logs.jsonl in its OTLP directory): the journal <see cref="LogTail"/> follows.</summary>
    public string LogPath { get; private set; } = "";

    /// <summary>The report's record of this launch (report["service"], report["service_2"], ...): pid, url and what followed.</summary>
    public JsonObject Entry => _entry;

    private static readonly ConcurrentDictionary<string, int> s_launches = new(StringComparer.Ordinal);
    private static ServiceProcess? s_latest;

    private readonly Lock _gate = new();
    private string _output = "";
    private Harness? _harness;
    private Report? _report;
    private JsonObject _entry = [];
    private string _dir = "";
    private CancellationToken _lifetime;
    private long _pidStartTime;
    private bool _stopped;
    private Exception? _exit;
    private bool _stepped;
    private Action<bool>? _holdKeep;
    private Func<Task<JsonObject>>? _stopKeep;

    private ServiceProcess()
    {
    }

    /// <summary>
    /// The serve-driven family's one lifecycle: it loads <c>spec.Save</c> through the harness's session when asked,
    /// reads the loaded identity unless the caller supplies one, sets the player's speed, checks the player API's
    /// health and session and waits until the brain has attached to the same identity. <paramref name="harness"/>
    /// may be null when the caller already prepared the game itself (a GameReuse case), in which case
    /// <paramref name="identity"/> is required. The launch is recorded under report["service"]
    /// (report["service_N"] for a restart). The Go version released the harness's GABP session here; with the
    /// HTTP channel there is no sole slot, so the harness stays usable.
    /// </summary>
    public static Task<ServiceProcess> ServeAsync(Config cfg, Harness? harness, JsonObject? identity, ServeSpec spec, Report? report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        return ServeCoreAsync(cfg.Output, harness, identity, spec, 1, report, cancellationToken);
    }

    // Serve with the launch number a harness-less restart carries; with a harness the output's own count wins.
    private static async Task<ServiceProcess> ServeCoreAsync(string output, Harness? harness, JsonObject? identity, ServeSpec spec, int launch, Report? report, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(output);
        ArgumentNullException.ThrowIfNull(spec);
        if (harness is not null)
        {
            if (spec.Save.Length > 0)
            {
                await LoadSaveAsync(harness, spec.Save, cancellationToken).ConfigureAwait(false);
            }

            if (identity is null)
            {
                identity = await ReadIdentityAsync(harness, "identity", cancellationToken).ConfigureAwait(false);
                if (report is not null)
                {
                    report["identity"] = identity.DeepClone();
                }
            }

            await WritePlayerSpeedAsync(harness, spec, cancellationToken).ConfigureAwait(false);
            launch = s_launches.AddOrUpdate(Path.GetFullPath(output), 1, (_, count) => count + 1);
        }

        if (identity is null)
        {
            throw new AcceptanceException("serve: the loaded identity is required once the harness session is released");
        }

        var p = await LaunchServeAsync(output, harness, spec, launch, report, cancellationToken).ConfigureAwait(false);
        p.Identity = identity.DeepClone().AsObject();
        try
        {
            await p.AttachAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is AcceptanceException or OperationCanceledException)
        {
            await p.StopAsync().ConfigureAwait(false);
            throw;
        }

        return p;
    }

    /// <summary>
    /// Finds the brain's player API for a harness that manages the attach and authority steps itself (the routine
    /// verticals); <see cref="ServeAsync"/> is the full lifecycle. The service is recorded under report["service"].
    /// </summary>
    public static Task<ServiceProcess> LaunchServiceAsync(Config cfg, ServiceLaunch launch, Report? report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(launch);
        report = launch.Report ?? report;
        var spec = new ServeSpec { Families = launch.Families, Env = launch.Env, PlayerSpeed = launch.PlayerSpeed };
        return LaunchServeAsync(launch.Output.Length > 0 ? launch.Output : cfg.Output, null, spec, 1, report, cancellationToken);
    }

    /// <summary>
    /// Takes the service again after <see cref="StopAsync"/> so a harness can exercise its post-stop handling: the
    /// new handle attaches to the same identity under report["service_N"]. The in-mod brain is not restarted (it
    /// lives as long as the game does), so a restart-reconciliation case no longer exercises a brain restart here;
    /// the launch entry records that.
    /// </summary>
    public async Task<ServiceProcess> RestartAsync(CancellationToken cancellationToken)
    {
        bool stopped;
        lock (_gate)
        {
            stopped = _stopped || _exit is not null;
        }

        if (!stopped)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"restart: service {Pid} is still running"));
        }

        var spec = new ServeSpec
        {
            Resume = Spec.Resume,
            Families = Spec.Families,
            Env = Spec.Env,
            NativeTimeout = Spec.NativeTimeout,
            StepStall = Spec.StepStall,
            Prefix = Spec.Prefix,
            PlayerSpeed = Spec.PlayerSpeed,
        };
        var next = await ServeCoreAsync(_output, _harness, Identity, spec, Launch + 1, _report, cancellationToken).ConfigureAwait(false);
        lock (next._gate)
        {
            next._entry["restart"] = "re-attached to the in-mod brain; it was not restarted";
        }

        return next;
    }

    // Finds the player API under output/service (output/service-N for a restart), checks the brain answers as the
    // process the endpoint file names and records the launch on the report.
    private static async Task<ServiceProcess> LaunchServeAsync(string output, Harness? harness, ServeSpec spec, int launch, Report? report, CancellationToken cancellationToken)
    {
        var serviceDir = Path.Combine(output, "service");
        var key = "service";
        if (launch > 1)
        {
            serviceDir = Path.Combine(output, string.Create(CultureInfo.InvariantCulture, $"service-{launch}"));
            key = string.Create(CultureInfo.InvariantCulture, $"service_{launch}");
        }

        Directory.CreateDirectory(serviceDir);
        var otlpDir = ServiceLogs.OtlpDir(output);
        var endpointDir = harness?.Transport is HttpNativeTransport http ? http.EndpointDirectory : otlpDir;
        var entry = new JsonObject { ["state"] = "starting", ["launch"] = launch, ["endpoint"] = endpointDir };
        if (report is not null)
        {
            report[key] = entry;
        }

        if (spec.Families is { } families)
        {
            entry["families"] = string.Join(',', families);
        }

        if (spec.Env.Count > 0)
        {
            entry["env_unapplied"] = new JsonArray([.. spec.Env.Select(e => (JsonNode?)JsonValue.Create(e))]);
        }

        var (url, pid) = await ResolveEndpointAsync(endpointDir, entry, cancellationToken).ConfigureAwait(false);
        var p = new ServiceProcess
        {
            Url = url,
            Pid = pid,
            Launch = launch,
            Spec = spec,
            LogPath = ServiceLogs.LogsPath(otlpDir),
            _output = output,
            _harness = harness,
            _report = report,
            _entry = entry,
            _dir = serviceDir,
            _lifetime = cancellationToken,
        };
        GameProcess.TryProcessStartTime(pid, out p._pidStartTime);
        entry["pid"] = pid;
        entry["url"] = url;
        entry["state"] = "running";
        Volatile.Write(ref s_latest, p);
        return p;
    }

    // The endpoint file the channel publishes beside the OTLP rings, waited for until the game has started it.
    private static async Task<(string Url, int Pid)> ResolveEndpointAsync(string endpointDir, JsonObject entry, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + EndpointWait;
        while (true)
        {
            if (RimGovernor.Brain.HttpChannel.EndpointFile.Read(endpointDir) is { Address.Length: > 0 } endpoint)
            {
                return (endpoint.Address.TrimEnd('/'), endpoint.Pid);
            }

            if (DateTime.UtcNow > deadline)
            {
                entry["state"] = "start failed: no endpoint file";
                throw new AcceptanceException($"start player API: no {RimGovernor.Brain.HttpChannel.EndpointFile.FileName} appeared in {endpointDir}: the game has not started its channel");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
    }

    private static readonly TimeSpan EndpointWait = TimeSpan.FromSeconds(90);

    // Serve's post-launch half: the session check and the brain seeing the harness's identity.
    private async Task AttachAsync(CancellationToken cancellationToken)
    {
        Token = await SessionTokenAsync(cancellationToken).ConfigureAwait(false);
        var state = await WaitAttachedAsync(Identity!, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
        await VerifyFamiliesAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _entry["attached"] = state.DeepClone();
        }
    }

    /// <summary>
    /// Checks the routine families the brain runs against <see cref="ServeSpec.Families"/>. The families are fixed by
    /// the game's launch environment (the Go harness set them on the child it started), so a mismatch means the game
    /// was launched with another selection and the case would silently exercise it. A brain that does not report its
    /// families yet is accepted, and the entry says so.
    /// </summary>
    public async Task VerifyFamiliesAsync(CancellationToken cancellationToken)
    {
        if (Spec.Families is not { } wanted)
        {
            return;
        }

        var reply = await ApiAsync("GET", "/api/routines", null, "", cancellationToken).ConfigureAwait(false);
        if (reply.Status != 200 || reply.Body?["activeFamilies"] is not JsonArray active)
        {
            lock (_gate)
            {
                _entry["families_check"] = "unavailable";
            }

            return;
        }

        var have = active.Select(n => n.AsString()).Order(StringComparer.Ordinal).ToList();
        var want = wanted.Order(StringComparer.Ordinal).ToList();
        if (!have.SequenceEqual(want, StringComparer.Ordinal))
        {
            throw new AcceptanceException($"the brain runs routine families [{string.Join(',', have)}] but the case asked for [{string.Join(',', want)}]; set RIMGOVERNOR_ROUTINE_FAMILIES when launching the game");
        }

        lock (_gate)
        {
            _entry["families_check"] = "matched";
        }
    }

    /// <summary>The requestId prefix for this launch: &lt;prefix&gt;-&lt;launch&gt;.</summary>
    public string Label
    {
        get
        {
            var prefix = Spec.Prefix.Length == 0 ? "serve" : Spec.Prefix;
            return string.Create(CultureInfo.InvariantCulture, $"{prefix}-{Launch}");
        }
    }

    /// <summary>
    /// Resumes automatic control for the attached identity (authority is the world's own root plan, created on
    /// first resume) and returns that root plan id. A service started with <see cref="ServeSpec.Resume"/> does this
    /// by itself.
    /// </summary>
    public Task<string> AcquireAsync(CancellationToken cancellationToken) =>
        ResumeAsync(Label, Identity!, Token, new Report(_entry), cancellationToken);

    /// <summary>
    /// Runs an <see cref="AuthorityKeepAlive"/> for the attached identity until <see cref="StopAsync"/>, which
    /// records its counters under the launch's report entry and returns them.
    /// </summary>
    public void KeepAuthority(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_holdKeep is not null)
            {
                return;
            }

            var keep = new AuthorityKeepAlive(this, Label, Identity!, Token);
            _holdKeep = keep.Hold;
            _stopKeep = keep.Start(cancellationToken);
        }
    }

    /// <summary>
    /// Pauses (true) or resumes (false) the keep-alive's re-acquisition, for a harness that pauses the clock on
    /// purpose.
    /// </summary>
    public void HoldAuthority(bool hold)
    {
        Action<bool>? holdKeep;
        lock (_gate)
        {
            holdKeep = _holdKeep;
        }

        holdKeep?.Invoke(hold);
    }

    /// <summary>
    /// Ends the keep-alive, collects the service's last routine view as evidence and closes the handle. The brain
    /// itself keeps running with the game (the Go version killed its serve child here). It returns the keep-alive
    /// counters when <see cref="KeepAuthority"/> ran, else null.
    /// </summary>
    public async Task<JsonObject?> StopAsync()
    {
        Func<Task<JsonObject>>? stopKeep;
        lock (_gate)
        {
            if (_stopped)
            {
                return null;
            }

            _stopped = true;
            stopKeep = _stopKeep;
        }

        // The keep-alive may be inside ApiAsync, which numbers its record: join it before the shutdown itself.
        JsonObject? keep = null;
        if (stopKeep is not null)
        {
            keep = await stopKeep().ConfigureAwait(false);
        }

        // Retain the current read-only routine view before closing: ApiAsync records the exchange for acceptance
        // why, including unavailable evidence.
        if (Exited() is null)
        {
            try
            {
                await ApiAsync("GET", "/api/routines", null, "", CancellationToken.None).ConfigureAwait(false);
            }
            catch (AcceptanceException)
            {
                // Evidence only: a brain that stopped answering is recorded by its absence.
            }
        }

        lock (_gate)
        {
            if (keep is not null)
            {
                _entry["keepalive"] = keep.DeepClone();
            }

            _entry["state"] = "stopped";
            RecordClock();
        }

        Interlocked.CompareExchange(ref s_latest, null, this);
        return keep;
    }

    // Summarizes the clock the handle saw into its report entry ("clock": first/last tick and wall seconds of the
    // /api/state samples), so every serve-driven result.json shows how far the game advanced under the service. The
    // caller holds the gate.
    private void RecordClock()
    {
        if (_tickSamples == 0)
        {
            return;
        }

        _entry["clock"] = new JsonObject
        {
            ["first_tick"] = _firstTick,
            ["last_tick"] = _lastTick,
            ["ticks_advanced"] = _lastTick >= _firstTick ? _lastTick - _firstTick : 0,
            ["wall_seconds"] = (_lastTickAt - _firstTickAt).TotalSeconds,
            ["clock_samples"] = _tickSamples,
        };
    }

    /// <summary>
    /// Reports, without blocking, whether the service has already ended on its own: the process that hosts the brain
    /// (the game) is gone. The exception names its exit; null while it runs. It is a <see cref="Wait.Terminal"/> for
    /// the poll loops that verify the service's state.
    /// </summary>
    public Exception? Exited()
    {
        lock (_gate)
        {
            if (_exit is null)
            {
                if (Pid > 0 && GameProcess.TryProcessStartTime(Pid, out var start) && (_pidStartTime == 0 || start == _pidStartTime))
                {
                    return null;
                }

                _exit = new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"service {Pid} exited: the game process that hosts the brain is gone"));
                _entry["state"] = "exited: " + _exit.Message;
            }

            return _exit;
        }
    }

    /// <summary>
    /// The service most recently launched in this process, however it was launched (Serve, LaunchService, Restart),
    /// while it runs: the one a checkpoint capture saves through. Null once it stopped or exited (or when none was
    /// launched).
    /// </summary>
    public static ServiceProcess? LatestService()
    {
        var p = Volatile.Read(ref s_latest);
        return p is not null && p.Running() ? p : null;
    }

    /// <summary>Reports whether the service is up: neither stopped nor exited.</summary>
    public bool Running()
    {
        bool stopped;
        lock (_gate)
        {
            stopped = _stopped;
        }

        return !stopped && Exited() is null;
    }

    /// <summary>
    /// Verifies a stopped service handle was stopped. The Go check also proved the stopped serve process no longer
    /// answered, so a game reused afterwards could not be reached through the retired controller; the in-mod brain
    /// belongs to the game and answers for as long as it runs, so only the handle's state is checked here and, when
    /// the game has exited, that its endpoint stopped answering.
    /// </summary>
    public async Task AssertStoppedAsync(CancellationToken cancellationToken)
    {
        bool stopped;
        lock (_gate)
        {
            stopped = _stopped;
        }

        if (!stopped)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"service {Pid} has not been stopped"));
        }

        if (Exited() is null)
        {
            return;
        }

        try
        {
            var reply = await ApiAsync("GET", "/api/health", null, "", cancellationToken).ConfigureAwait(false);
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"stopped service {Pid} still answers /api/health with status {reply.Status}"));
        }
        catch (NativeTransportException)
        {
            // Not answering is the expected state.
        }
    }

    // Fills w's defaults for this service: the stall budget and the service's exit as the terminal.
    private Wait Defaults(Wait w)
    {
        if (w.Stall <= TimeSpan.Zero)
        {
            w = w with { Stall = Wait.StallBudget() };
        }

        if (w.Terminal is null)
        {
            w = w with { Terminal = Exited };
        }

        return w;
    }

    /// <summary><see cref="ServiceWaits.WaitReviewAsync"/> on this service's routines, ended by its exit.</summary>
    public Task<JsonObject> WaitReviewAsync(Wait w, Func<JsonObject, bool> ready, CancellationToken cancellationToken) =>
        ServiceWaits.WaitReviewAsync(ApiAsync, Defaults(w), ready, cancellationToken);

    /// <summary><see cref="ServiceWaits.WaitPlanAsync"/> on this service's plans, ended by its exit.</summary>
    public Task<JsonObject> WaitPlanAsync(Wait w, string planId, Func<JsonObject, (string Signature, bool Done)> check, CancellationToken cancellationToken) =>
        ServiceWaits.WaitPlanAsync(ApiAsync, Defaults(w), planId, check, cancellationToken);

    /// <summary><see cref="ServiceWaits.WaitMethodAsync"/> on this service, ended by its exit.</summary>
    public Task<ServiceMethod> WaitMethodAsync(string need, string? previousPlan, CancellationToken cancellationToken)
    {
        IReadOnlySet<string>? seen = previousPlan is null ? null : new HashSet<string>([previousPlan], StringComparer.Ordinal);
        return WaitMethodExcludingAsync(need, seen, cancellationToken);
    }

    /// <summary><see cref="ServiceWaits.WaitMethodExcludingAsync"/> on this service, ended by its exit.</summary>
    public Task<ServiceMethod> WaitMethodExcludingAsync(string need, IReadOnlySet<string>? seen, CancellationToken cancellationToken) =>
        ServiceWaits.WaitMethodCoreAsync(ApiAsync, Defaults(new Wait { Interval = TimeSpan.FromSeconds(1) }), need, seen, cancellationToken);

    /// <summary><see cref="ServiceWaits.WaitPlanTerminalAsync"/> on this service's plans, ended by its exit.</summary>
    public Task<(JsonObject State, bool Incidental)> WaitPlanTerminalAsync(string planId, CancellationToken cancellationToken) =>
        ServiceWaits.WaitPlanTerminalCoreAsync(ApiAsync, Defaults(new Wait { Interval = TimeSpan.FromSeconds(1) }), planId, cancellationToken);

    /// <summary>
    /// Reports whether the scheduler has admitted a clock window since the service started, recording when the first
    /// one was seen. The journaled clock attempts the Go version counted are not on the player API, so a window is
    /// admitted once the brain is automating and the game's tick has moved past the first one this handle read. Once
    /// <see cref="ServeSpec.StepStall"/> has elapsed since <paramref name="since"/> with none, it throws a
    /// <see cref="StepStallException"/> naming the last step failure the service logged; with StepStall zero it only
    /// reports.
    /// </summary>
    public async Task<bool> StepAdmittedAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_stepped)
            {
                return true;
            }
        }

        var reply = await ApiAsync("GET", "/api/state", null, "", cancellationToken).ConfigureAwait(false);
        if (reply.Status == 200 && reply.Body is { } state && state["mode"].AsString() == "automate")
        {
            var tick = state["game"].AsMap()?["tick"].AsUInt64();
            lock (_gate)
            {
                if (tick is { } now)
                {
                    _stepBaseline ??= now;
                    if (now > _stepBaseline)
                    {
                        _stepped = true;
                        _entry["first_clock_attempt_after"] = WaitError.FormatDuration(DateTimeOffset.UtcNow - since);
                        return true;
                    }
                }
            }
        }

        if (Spec.StepStall > TimeSpan.Zero && DateTimeOffset.UtcNow - since >= Spec.StepStall)
        {
            throw new StepStallException(Spec.StepStall, string.Join(',', Spec.Families ?? []), StepStall.LastStepFailure(LogPath));
        }

        return false;
    }

    /// <summary>
    /// Blocks until <see cref="StepAdmittedAsync"/>, ended by the service's exit or the token; it fails at once when
    /// <see cref="ServeSpec.StepStall"/> is zero and nothing has been admitted within the stall budget.
    /// </summary>
    public Task WaitStepAsync(CancellationToken cancellationToken)
    {
        var since = DateTimeOffset.UtcNow;
        var stall = Spec.StepStall > TimeSpan.Zero ? Spec.StepStall : Wait.StallBudget();
        var wait = new Wait { Ceiling = stall + TimeSpan.FromSeconds(1), Stall = stall, Interval = TimeSpan.FromSeconds(1), Terminal = Exited };
        return wait.WaitProgressAsync(async ct => ("", await StepAdmittedAsync(since, ct).ConfigureAwait(false)), cancellationToken);
    }

    /// <summary>Loads profile/Saves/&lt;save&gt;.rws through <paramref name="harness"/> (visual readiness) and pauses the clock.</summary>
    public static async Task LoadSaveAsync(Harness harness, string save, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);
        await harness.CallAsync("load-save", "rimgovernor/load_game_ready", new JsonObject
        {
            ["saveName"] = save,
            ["readiness"] = "visual",
            ["timeoutMs"] = 90000,
            ["ignoreModCompatibility"] = false,
        }, cancellationToken).ConfigureAwait(false);
        await harness.CallAsync("pause", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);
    }

    // The game tick of the first state read once automating: a later, larger tick means a window ran.
    private ulong? _stepBaseline;
}
