using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>What <see cref="CaseRunner.ExecuteAsync"/> returns: the finalized report and the exit code, plus what the report holds only as JSON.</summary>
/// <param name="Report">The finalized report (also written as result.json).</param>
/// <param name="Code">The exit code <see cref="Report.Finalize"/> computed: 0 passed, 1 failed.</param>
/// <param name="Diagnosis">The postmortem digest of a failed case, null otherwise.</param>
/// <param name="Drift">The metrics past their rule against the series' trailing median; advisory, never changes the verdict.</param>
public sealed record CaseOutcome(Report Report, int Code, Digest? Diagnosis, IReadOnlyList<DriftFlag> Drift)
{
    /// <summary>The <c>(report, code)</c> pair.</summary>
    public void Deconstruct(out Report report, out int code)
    {
        report = Report;
        code = Code;
    }
}

/// <summary>
/// The shared runner over the case registry: boot, keep/reuse, quiet and freeze defaults, crew
/// staffing, timing, the report, service handling and the postmortem phase. A case declares what it needs and the runner
/// owns everything around its Run body.
/// </summary>
public sealed class CaseRunner
{
    private static readonly TimeSpan TimeoutMargin = TimeSpan.FromMinutes(5);

    // The stall environment variable as the process started, restored before a case that declares no Stall of its own.
    private static readonly string? InitialStallEnv = Environment.GetEnvironmentVariable(Wait.StallEnv);

    private readonly RunDependencies _deps;
    private readonly ISessionHost _host;

    public CaseRunner(RunDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _deps = dependencies;
        _host = dependencies.Host ?? SessionHost.Default;
    }

    /// <summary>
    /// Runs one case end to end (prepare, open, start, quiet, freeze, Run, close) and writes its report. A hung body is
    /// cut at its hang guard; Finalize stamps its timing and metrics block; the report records under wait_stats how many of its waits
    /// stalled and under game_log the slice of the game's own log the case wrote, copied to &lt;output&gt;/game.log. The
    /// block is appended to the run's series (<see cref="RunOptions.SeriesPath"/>) and the report lists under "drift" the
    /// metrics past their rule against the series' trailing median; neither changes the verdict.
    /// </summary>
    public async Task<CaseOutcome> ExecuteAsync(AcceptanceCase c, RunOptions opts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(opts);
        var output = opts.CaseOutput(c);
        var headless = opts.Headless;
        var report = Report.Create(c.Scope, headless);
        report["case"] = c.Name;
        if (opts.Healed.Count > 0)
        {
            report["healed"] = new JsonArray([.. opts.Healed.Select(code => (JsonNode?)JsonValue.Create(code))]);
        }

        WaitStatistics.Reset();
        TickStats.Reset();
        var logConfig = new Config { Root = opts.Root, Headless = headless, GameId = opts.GameId };
        var gameLog = GameLogCapture.Open(logConfig.StartupLogPath());

        Digest? diagnosis = null;
        IReadOnlyList<DriftFlag> drift = [];
        CaseOutcome Finish()
        {
            report["wait_stats"] = WaitStatistics.Snapshot();

            // What this result proves, derived from the case and from what the run recorded.
            Provenance.Stamp(c, report);
            var exit = report.Finalize(output);
            if (!opts.NoSeries)
            {
                var seed = WorldRecords.WorldOf(report)?.Seed ?? "";
                drift = MetricSeries.RecordSeries(opts.SeriesPath(), c.Name, opts.RunId(), report, seed);
                report.Write(output);
            }

            return new CaseOutcome(report, exit, diagnosis, drift);
        }

        try
        {
            Directory.CreateDirectory(output);
            if (Directory.EnumerateFileSystemEntries(output).Any())
            {
                report["error"] = $"{output} is not empty: every run needs a fresh output directory";
                return Finish();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            report["error"] = e.Message;
            return Finish();
        }

        // A case is held to its ticks, not the wall clock: its hang guard cuts a hung body (RunOpenedAsync), and a
        // passing claim never fails on time, so the report carries no budget.
        var budget = opts.Budget > TimeSpan.Zero ? opts.Budget : c.Typed.HangGuard;
        report["ticks"] = c.Typed.Ticks;
        report["hang_guard_ms"] = (long)budget.TotalMilliseconds;

        // The shared waits read the stall budget from the environment; a flag override is set for this case and the
        // process's original value put back for the next.
        var stall = opts.Stall;
        Environment.SetEnvironmentVariable(Wait.StallEnv, stall > TimeSpan.Zero ? GoDuration.Format(stall) : InitialStallEnv);
        report["stall_ms"] = (long)Wait.StallBudget().TotalMilliseconds;
        EvidenceBudget.SetEvidenceMode(opts.Evidence);
        report["evidence"] = EvidenceBudget.CurrentEvidenceMode() == EvidenceMode.Full ? "full" : "capped";
        var timeout = opts.Timeout > TimeSpan.Zero ? opts.Timeout : RunOptions.DefaultTimeout;
        if (timeout < budget + TimeoutMargin)
        {
            // A case whose budget outruns the safety net still fails on its budget, not on a cut a few minutes short of it.
            timeout = budget + TimeoutMargin;
        }

        using var runTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        runTimeout.CancelAfter(timeout);
        Exception? error = null;
        var cfg = NewConfig(c, opts, output);
        var started = DateTime.UtcNow;
        try
        {
            await ExecuteCoreAsync(c, opts, output, report, cfg, runTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            error = runTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested && e is OperationCanceledException
                ? new AcceptanceException($"case timed out after {GoDuration.Format(timeout)} (the -timeout safety net)", e)
                : e;
        }

        if (error is NativeTransportException or NativeRefusedException && report.ProcessExit.Length > 0)
            error = new RunLostException(report.ProcessExit + "\n" + ErrorText(error));

        if (report.SoftFailure() is { } soft)
        {
            error = error is null ? soft : new AcceptanceException(ErrorText(error) + "\n" + ErrorText(soft));
        }

        // The log copy lands only in an output directory this run owns (the early returns above own none), and before the
        // postmortem, which reads its tail after a startup crash.
        gameLog.Close(output, report);

        // The brain's rings and store tables as bounded text in the output, before the postmortem, which reads the log ring
        // there and the same case rows of the live store.
        var store = CaseRows.Of(cfg, started);
        if (cfg.Configuration.Length > 0
            && CaseEvidence.Capture(GameSessions.EndpointDirectory(cfg.Configuration, cfg.GameId), store, output) is { Length: > 0 } evidenceError)
        {
            report["case_evidence_error"] = evidenceError;
        }

        if (error is not null)
        {
            report["error"] = ErrorText(error);
            diagnosis = Diagnose(output, report, store, cancellationToken);
        }
        else
        {
            report["passed"] = true;
        }

        return Finish();
    }

    /// <summary>
    /// The case's Start with <paramref name="seed"/> pinned on it; an empty seed leaves the start as declared. Only a
    /// debug or scenario start (bare or under a Fixture) generates a world to pin.
    /// </summary>
    internal static Start SeededStart(Start start, string seed)
    {
        if (seed.Length == 0)
        {
            return start;
        }

        switch (start)
        {
            case DebugStart debug:
                return debug with { Size = debug.Size with { Seed = seed } };
            case ScenarioStart scenario:
                return scenario with { Spec = scenario.Spec with { Seed = seed } };
            case FixtureStart fixture:
                return fixture with { On = SeededStart(fixture.On ?? new DebugStart(), seed) };
            default:
                throw new AcceptanceException($"-seed {seed}: the case starts from {start.Describe().ToJsonString()}, which carries its own world; only a debug or scenario start takes a seed");
        }
    }

    /// <summary>
    /// Makes the opened game the case's declared crew (<see cref="AcceptanceCase.Crew"/>): the colony topped up to the
    /// crew size, every colonist a superpawn. It runs before the Run body and again after a Reload, and is a no-op on a
    /// colony that is already staffed.
    /// </summary>
    internal static async Task StaffAsync(AcceptanceCase c, Harness harness, Report report, CancellationToken cancellationToken)
    {
        if (!c.Staffed)
        {
            return;
        }

        try
        {
            var (colonists, added) = await DebugGame.StaffCrewAsync(harness, c.Crew.Size, cancellationToken).ConfigureAwait(false);
            report["crew"] = new JsonObject { { "declared", c.Crew.Size }, { "colonists", colonists }, { "added", added } };
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"staff the crew of {c.Crew.Size}: {e.Message}", e);
        }
    }

    // Under -headless, ends a windowed process an earlier run kept, so the case does not boot beside it, recording the
    // stop on the report under "stopped_rendered". Without -headless every case shares the windowed profile.
    private static async Task StopOtherProfileAsync(RunOptions opts, Report report, CancellationToken cancellationToken)
    {
        if (!opts.Headless)
        {
            return;
        }

        try
        {
            if (await Game.StopRenderedGameAsync(opts.Root, opts.GameId, cancellationToken).ConfigureAwait(false))
            {
                report["stopped_rendered"] = true;
            }
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException("stop kept rendered game before headless start: " + e.Message, e);
        }
    }

    // Writes the postmortem digest of a failed case onto the report ("diagnosis", which Finalize emits first) and to
    // output/diagnosis.txt, read from the evidence the run left behind and the case's rows of the live store. It is
    // collected under the caller's token, not the run's, which a timeout may already have cut. The report gets its
    // installed_package first, which Finalize would add only after, so the revision section reads the source revision.
    internal static Digest Diagnose(string output, Report report, CaseRows store, CancellationToken cancellationToken)
    {
        report.StampInstalledPackage();
        Digest digest;
        try
        {
            digest = Postmortems.Collect(output, report.Data, store, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A collector bug must not hide the case's verdict: record what failed and carry on.
            var failed = new DigestSection("postmortem");
            failed.Note = "postmortem collection failed: " + e.GetType().Name + ": " + e.Message;
            var facts = ReportFacts.Of(report.Data);
            digest = new Digest(facts.Case ?? "", facts.Error ?? "", [failed]);
        }

        report["diagnosis"] = digest.ToJson();
        try
        {
            File.WriteAllText(Path.Combine(output, "diagnosis.txt"), digest.Text());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The digest is on the report too.
        }

        return digest;
    }

    private static string ErrorText(Exception e) => e switch
    {
        AggregateException aggregate => string.Join('\n', aggregate.InnerExceptions.Select(ErrorText)),
        AcceptanceException or OperationCanceledException => e.Message,
        _ => e.GetType().Name + ": " + e.Message,
    };

    private static string QuietText(QuietMode mode) => mode switch
    {
        QuietMode.Required => "required",
        QuietMode.IfAvailable => "if-available",
        QuietMode.Loud => "loud",
        _ => string.Create(CultureInfo.InvariantCulture, $"QuietMode({(int)mode})"),
    };

    private async Task ExecuteCoreAsync(AcceptanceCase c, RunOptions opts, string output, Report report, Config cfg, CancellationToken cancellationToken)
    {
        var session = new CaseSession(c, report);
        session.UseConfig(cfg);
        if (c.Expansions.Count > 0)
        {
            cfg.Expansions = [.. c.Expansions];
            try
            {
                _ = await Game.StopGameAsync(opts.Root, opts.GameId, cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException("stop kept game before DLC fixture: " + e.Message, e);
            }
        }

        report["keep"] = !c.NoKeep && Game.KeepGame();
        var start = SeededStart(c.Start!, opts.Seed);
        if (c.Start is OwnedStart owned)
        {
            // The case launches, drives and stops the process itself on this profile (the expansions of the saves it
            // names), so a game an earlier case kept is stopped first: its profile may differ, and a kept process cannot
            // change expansions.
            await cfg.UseSaveExpansionsAsync(owned.Saves, cancellationToken).ConfigureAwait(false);
            try
            {
                cfg.PrepareConfig();
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException("prepare profile: " + e.Message, e);
            }

            report["start"] = owned.Describe();
            report["quiet_mode"] = QuietText(c.Quiet);
            try
            {
                _ = await Game.StopGameAsync(opts.Root, opts.GameId, cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException("stop kept game before owned start: " + e.Message, e);
            }

            await c.Run!(session, cancellationToken).ConfigureAwait(false);
            return;
        }

        await StopOtherProfileAsync(opts, report, cancellationToken).ConfigureAwait(false);

        // Session.OpenAsync is the shared preamble: stale-package check, profile, a kept process, discovery, the start,
        // pause, the fixture op, frozen needs and the identity, each on the report.
        var lifecycle = await Session.OpenAsync(cfg, new RunnerHost(_host, c.NoKeep), report, start, c.Quiet, c.KeepNeeds(), cancellationToken).ConfigureAwait(false);
        session.UseSession(lifecycle);
        try
        {
            await RunOpenedAsync(c, opts, output, report, cfg, session, lifecycle, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await session.StopServicesAsync().ConfigureAwait(false);
            lifecycle.Close();
        }
    }

    // Everything after the game is open and prepared: crew, Run, the postmortem phase, the closing checks.
    private static async Task RunOpenedAsync(
        AcceptanceCase c,
        RunOptions opts,
        string output,
        Report report,
        Config cfg,
        CaseSession session,
        Session lifecycle,
        CancellationToken cancellationToken)
    {
        await StaffAsync(c, lifecycle.Harness, report, cancellationToken).ConfigureAwait(false);
        report["quiet_mode"] = QuietText(c.Quiet);

        // The hang guard cuts the Run body where Finalize would fail it anyway: the -timeout safety net sits minutes past
        // it, long enough for a hung serve loop to run on unseen. It runs from the body's start: boot and staging are
        // the runner's, not the claim's.
        using var body = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        body.CancelAfter(opts.Budget > TimeSpan.Zero ? opts.Budget : c.Typed.HangGuard);
        Exception? runError = null;
        try
        {
            await c.Run!(session, body.Token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            runError = e;
        }

        if (runError is not null && body.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            runError = new AcceptanceException("run cut at its hang guard (ticks / 300 per second + 1 min): " + ErrorText(runError), runError);
        }

        if (runError is null && c.Postmortem is not null)
        {
            await session.PostmortemAsync(cancellationToken).ConfigureAwait(false);
        }

        if (runError is not null)
        {
            throw runError;
        }
    }

    internal static Config NewConfig(AcceptanceCase c, RunOptions opts, string output)
    {
        var cfg = new Config
        {
            Root = opts.Root,
            Output = output,
            Headless = opts.Headless,
            GameId = opts.GameId,
            QuietWorld = c.QuietWorld,
        };
        cfg.LaunchEnv[HeadlessProfile.StateVariable] = Path.Combine(output, Checkpoints.CheckpointStoreFile);
        foreach (var (name, value) in CrashDump.LaunchEnv(output))
        {
            cfg.LaunchEnv[name] = value;
        }

        if (c.Serve?.Families is { } families)
        {
            cfg.LaunchEnv["RIMGOVERNOR_ROUTINE_FAMILIES"] = string.Join(",", families);
        }

        foreach (var (name, value) in ServiceLaunchEnv.Applied(c.Serve?.Env ?? []))
        {
            cfg.LaunchEnv[name] = value;
        }

        return cfg;
    }

    // The session host the runner opens with: the shared one, plus NoKeep: a case that ends or replaces the process has
    // nothing to keep, so the hold it gets closes by stopping the game.
    private sealed class RunnerHost(ISessionHost inner, bool noKeep) : ISessionHost
    {
        public ProfileStartCache StartCache => inner.StartCache;

        public Task PrepareAsync(Config config, IReadOnlyList<string> saves, CancellationToken cancellationToken) =>
            inner.PrepareAsync(config, saves, cancellationToken);

        public JsonObject PackageFiles(Config config) => inner.PackageFiles(config);

        public async Task<Game> OpenGameAsync(Config config, Report report, CancellationToken cancellationToken)
        {
            var hold = await inner.OpenGameAsync(config, report, cancellationToken).ConfigureAwait(false);
            if (noKeep)
            {
                hold.Keep = false;
            }

            return hold;
        }

        public JsonObject RecordWorld(Config config, Start start, WorldSource source) => inner.RecordWorld(config, start, source);

        public string FixtureBuildHint(string op) => inner.FixtureBuildHint(op);
    }
}
