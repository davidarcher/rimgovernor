using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>What <see cref="CaseRunner.ExecuteAsync"/> returns: the finalized report and the exit code, plus what the report holds only as JSON.</summary>
/// <param name="Report">The finalized report (also written as result.json).</param>
/// <param name="Code">The exit code <see cref="Report.Finalize"/> computed: 0 passed, <see cref="Report.ExitBreak"/> paused at a breakpoint, 1 failed.</param>
/// <param name="Diagnosis">The postmortem digest of a failed case, null otherwise.</param>
/// <param name="Drift">The metrics past their rule against the series' trailing median; advisory, never changes the verdict.</param>
public sealed record CaseOutcome(Report Report, int Code, Digest? Diagnosis, IReadOnlyList<DriftFlag> Drift)
{
    /// <summary>Go's <c>(report, code)</c> pair.</summary>
    public void Deconstruct(out Report report, out int code)
    {
        report = Report;
        code = Code;
    }
}

/// <summary>
/// The shared runner over the case registry (Go <c>cases/run.go</c>): boot, keep/reuse, quiet and freeze defaults, crew
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
    /// Runs one case end to end (prepare, open, start, quiet, freeze, Run, close) and writes its report. A case that
    /// fails Lint is refused before the game opens; Finalize fails the run when it took longer than the budget
    /// (budget_ms) and stamps its timing and metrics block; the report records under wait_stats how many of its waits
    /// stalled and under game_log the slice of the game's own log the case wrote, copied to &lt;output&gt;/game.log. The
    /// block is appended to the run's series (<see cref="RunOptions.SeriesPath"/>) and the report lists under "drift" the
    /// metrics past their rule against the series' trailing median; neither changes the verdict.
    /// </summary>
    public async Task<CaseOutcome> ExecuteAsync(AcceptanceCase c, RunOptions opts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(opts);
        var output = opts.CaseOutput(c);
        if (opts.Dev || !opts.Break.IsZero || opts.Through.Length > 0)
        {
            // An iteration over a pinned bundle, or a run cut at a breakpoint or after a stage, is not a measurement of
            // the case.
            opts = opts with { NoSeries = true };
        }

        var headless = opts.Headless && !c.Rendered;
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

        // The log copy lands only in an output directory this run owns.
        var opened = false;
        Digest? diagnosis = null;
        IReadOnlyList<DriftFlag> drift = [];
        CaseOutcome Finish()
        {
            report["wait_stats"] = WaitStatistics.Snapshot();

            // What this result proves, derived from the case and from what the run recorded about resume/stage/dev origin.
            Provenance.Stamp(c, report);
            if (opened)
            {
                gameLog.Close(output, report);
            }

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

        opened = true;
        var lint = c.Lint();
        if (lint.Count > 0)
        {
            report["error"] = string.Join('\n', lint);
            return Finish();
        }

        var budget = c.Budget;
        if (opts.Budget > TimeSpan.Zero)
        {
            budget = opts.Budget;
        }

        report.SetBudget(budget);

        // The shared waits read the stall budget from the environment; a flag override or the case's own Stall is set
        // for this case and the process's original value put back for the next.
        var stall = opts.Stall > TimeSpan.Zero ? opts.Stall : c.Stall;
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
        try
        {
            await ExecuteCoreAsync(c, opts, output, report, runTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            error = runTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested && e is OperationCanceledException and not (BreakException or StagedThroughException)
                ? new AcceptanceException($"case timed out after {GoDuration.Format(timeout)} (the -timeout safety net)", e)
                : e;
        }

        if (report.SoftFailure() is { } soft && error is not RunBrokeException)
        {
            error = error is null ? soft : new AcceptanceException(ErrorText(error) + "\n" + ErrorText(soft));
        }

        switch (error)
        {
            case RunBrokeException:
                // Paused at the breakpoint: neither passed nor failed; the report's "break" block says where (Finalize
                // exits Report.ExitBreak).
                _ = report.Data.Remove("passed");
                break;
            case not null:
                report["error"] = ErrorText(error);
                diagnosis = Diagnose(output, report, cancellationToken);
                break;
            default:
                report["passed"] = true;
                break;
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
            report["crew"] = new JsonObject { ["declared"] = c.Crew.Size, ["colonists"] = colonists, ["added"] = added };
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"staff the crew of {c.Crew.Size}: {e.Message}", e);
        }
    }

    // Which process profile a case under -headless must end before it opens, or "" when it reuses whatever runs: a
    // rendered case cannot run on the headless process ("headless"), and a headless case after a rendered one would boot
    // beside the windowed process the rendered case kept ("rendered"). Without -headless every case shares the windowed
    // profile and nothing is stopped.
    private static string ProfileToStop(AcceptanceCase c, bool headless)
    {
        if (!headless)
        {
            return "";
        }

        return c.Rendered ? "headless" : "rendered";
    }

    // Ends the process ProfileToStop names, recording a rendered stop on the report under "stopped_rendered".
    private static async Task StopOtherProfileAsync(AcceptanceCase c, RunOptions opts, Report report, CancellationToken cancellationToken)
    {
        switch (ProfileToStop(c, opts.Headless))
        {
            case "headless":
                try
                {
                    _ = await Game.StopGameAsync(opts.Root, opts.GameId, cancellationToken).ConfigureAwait(false);
                }
                catch (AcceptanceException e)
                {
                    throw new AcceptanceException("stop kept headless game before rendered start: " + e.Message, e);
                }

                break;
            case "rendered":
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

                break;
            default:
                break;
        }
    }

    // Writes the postmortem digest of a failed case onto the report ("diagnosis", which Finalize emits first) and to
    // output/diagnosis.txt, read from the evidence the run left behind. It is collected under the caller's token, not the
    // run's, which a timeout may already have cut.
    private static Digest Diagnose(string output, Report report, CancellationToken cancellationToken)
    {
        var digest = Postmortems.Collect(output, report.Data, cancellationToken);
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

    private async Task ExecuteCoreAsync(AcceptanceCase c, RunOptions opts, string output, Report report, CancellationToken cancellationToken)
    {
        if (opts.PostmortemOnly)
        {
            await ExecutePostmortemAsync(c, opts, output, report, cancellationToken).ConfigureAwait(false);
            return;
        }

        var checkpointing = _deps.Checkpointing;
        opts = checkpointing.CheckBreak(c, opts);
        checkpointing.CheckThrough(c, opts);
        var log = opts.Log ?? TextWriter.Null;
        if (opts.Seed.Length > 0)
        {
            // A reproduction runs the recorded world from scratch; a resumed checkpoint would be some other run's world.
            opts = opts with { Fresh = true };
        }

        ResumePlan resumed;
        if (opts.Dev)
        {
            resumed = PlanDev(c, opts, log);

            // The bundle is the iteration's fixed starting point: nothing this run does is recorded against it.
            opts = opts with { CheckpointEvery = TimeSpan.Zero };
            report["dev"] = true;
        }
        else
        {
            resumed = Wrap("checkpoint ring", () => checkpointing.PlanResume(c, opts, log));
        }

        var staged = Wrap("stage bundles", () => checkpointing.PlanStage(c, opts, resumed, log));
        if (opts.Dev && staged.Off.Length == 0)
        {
            staged = staged.WithOff("dev iteration");
        }

        var session = new CaseSession(c, report, _deps.Scenario, opts.Seed, opts.RunSeed, staged, opts.StagesDir(c), opts.Through);
        if (resumed.Resuming || staged.Staged)
        {
            // The restored store holds the earlier run's submissions.
            session.ResumeSuffix = opts.RunId();
        }

        if (opts.Dev)
        {
            session.ResumeSuffix = "dev" + CaseSession.Tag(Math.Max(opts.Attempt, 1));
        }

        if (resumed.Resuming)
        {
            session.Resumed = resumed.Entry;
        }

        var cfg = NewConfig(c, opts, output);
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
        if (resumed.Resuming)
        {
            // The bundle's save replaces the case's Start (the fixture op already ran before the capture), its store
            // lands where the service opens it, and its journal is read at a fresh launch.
            var entry = resumed.Entry!;
            var save = Wrap("stage checkpoint", () => Checkpoints.StageCheckpoint(opts.Root, entry, Path.Combine(output, "service.sqlite")));
            start = new SaveStart { Name = save };
            cfg.Resumed = true;
            if (entry.Store && entry.ServiceProfile.Length > 0)
            {
                cfg.ServiceProfile = entry.ServiceProfile;
            }

            var rev = resumed.Previous is { } previous ? previous.SourceRevision : entry.SourceRevision;
            report["resumed_from"] = new JsonObject
            {
                ["path"] = entry.Path,
                ["label"] = entry.Label,
                ["offset_ms"] = entry.OffsetMs,
                ["tick"] = (long)entry.Tick,
                ["source_revision"] = rev,
                ["save"] = save,
            };
        }
        else if (staged.Staged)
        {
            // A stage bundle opens the way a resume does: its save replaces the Start, its store is restored.
            var entry = staged.Entry!;
            var save = Wrap("stage bundle", () => Checkpoints.StageCheckpoint(opts.Root, entry, Path.Combine(output, "service.sqlite")));
            start = new SaveStart { Name = save };
            cfg.Resumed = true;
            if (entry.Store && entry.ServiceProfile.Length > 0)
            {
                cfg.ServiceProfile = entry.ServiceProfile;
            }

            report["staged_from"] = new JsonObject
            {
                ["path"] = entry.Path,
                ["stage"] = entry.Stage,
                ["offset_ms"] = entry.OffsetMs,
                ["tick"] = (long)entry.Tick,
                ["source_revision"] = entry.SourceRevision,
                ["captured_at"] = entry.At,
                ["save"] = save,
            };
        }

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

        await StopOtherProfileAsync(c, opts, report, cancellationToken).ConfigureAwait(false);

        // Session.OpenAsync is the shared preamble: stale-package check, profile, a kept process, discovery, the start,
        // pause, the fixture op, frozen needs and the identity, each on the report.
        Session lifecycle;
        try
        {
            lifecycle = await Session.OpenAsync(cfg, new RunnerHost(_host, c.NoKeep), report, start, c.Quiet, c.KeepNeeds(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (resumed.Resuming && resumed.Previous is not null)
        {
            // The game never opened on the bundle: the next run steps back one entry rather than retrying it forever.
            throw;
        }

        session.UseSession(lifecycle);
        try
        {
            await RunOpenedAsync(c, opts, output, report, cfg, session, lifecycle, resumed, staged, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await session.StopServicesAsync().ConfigureAwait(false);
            lifecycle.Close();
        }
    }

    // Everything after the game is open and prepared: crew, ring, Run, the postmortem phase, the closing checks.
    private async Task RunOpenedAsync(
        AcceptanceCase c,
        RunOptions opts,
        string output,
        Report report,
        Config cfg,
        CaseSession session,
        Session lifecycle,
        ResumePlan resumed,
        StagePlan staged,
        CancellationToken cancellationToken)
    {
        var checkpointing = _deps.Checkpointing;
        await StaffAsync(c, lifecycle.Harness, report, cancellationToken).ConfigureAwait(false);
        report["quiet_mode"] = QuietText(c.Quiet);
        if (resumed.Resuming && resumed.Entry!.Prepared is { } resumedPrepared)
        {
            session.UsePrepared(resumedPrepared);
            report["prepared"] = resumedPrepared;
        }
        else if (staged.Staged && staged.Entry!.Prepared is { } stagedPrepared)
        {
            session.UsePrepared(stagedPrepared);
            report["prepared"] = stagedPrepared;
        }

        var ring = checkpointing.NewRing(c, opts, session, cfg, output, resumed, staged, report);

        // A tripped breakpoint cuts the Run body through its token, with the BreakException as the cause.
        using var runScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Exception? cutCause = null;
        session.CutRun = cause =>
        {
            Interlocked.CompareExchange(ref cutCause, cause, null);
            runScope.Cancel();
        };
        if (ring is not null)
        {
            ring.Break = opts.Break;
            ring.OnBreak = reason => session.CutRun(new BreakException(reason));
            ring.Activate();
        }

        session.Ring = ring;
        session.Stages = checkpointing.NewStages(c, opts, session, cfg, output, staged, report);
        session.RunStarted = Stopwatch.StartNew();
        var deactivateStages = false;
        if (ring is null && session.Stages is not null)
        {
            // A staged case that opts out of the ring (NoCheckpoint) still records its stage state on the stage ring; with
            // no periodic cadence installing it only gives the state a holder.
            session.Stages.Activate();
            deactivateStages = true;
        }

        try
        {
            // The budget cuts the Run body where Finalize would fail it anyway: the -timeout safety net sits minutes past
            // it, long enough for a hung serve loop to run on unseen.
            using var body = CancellationTokenSource.CreateLinkedTokenSource(runScope.Token);
            if (report.BudgetDeadline() is { } deadline)
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                body.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
            }

            Exception? runError = null;
            try
            {
                await c.Run!(session, body.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                runError = e;
            }

            if (runError is not null && body.IsCancellationRequested && !runScope.IsCancellationRequested)
            {
                runError = new AcceptanceException("run cut at its wall-clock budget: " + ErrorText(runError), runError);
            }

            if (c.Stages.Count > 0)
            {
                report["stages"] = session.StageRows.DeepClone();
            }

            if (cutCause is BreakException broke && !cancellationToken.IsCancellationRequested)
            {
                throw await BreakRunAsync(c, opts, session, cfg, ring, resumed, broke, report).ConfigureAwait(false);
            }

            var through = cutCause as StagedThroughException;
            if (through is not null && !cancellationToken.IsCancellationRequested)
            {
                // The stage's bundle is cached: the rest of the chain, its Postmortem included, is another run's, so
                // nothing failed.
                runError = null;
            }

            if (runError is null && c.Postmortem is not null && through is null)
            {
                try
                {
                    await session.PostmortemAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    runError = e;
                }
            }

            if (runError is null)
            {
                try
                {
                    StartupRows.Check(output, cfg.Headless);
                }
                catch (AcceptanceException e)
                {
                    runError = e;
                }
            }

            await checkpointing.CloseRingAsync(
                ring,
                resumed,
                runError,
                report,
                async ct =>
                {
                    await session.StopServicesAsync().ConfigureAwait(false);
                    _ = session.Reattach();
                    await Task.CompletedTask.ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                }).ConfigureAwait(false);
            if (runError is not null)
            {
                throw runError;
            }
        }
        finally
        {
            if (deactivateStages)
            {
                session.Stages!.Deactivate();
            }
        }
    }

    // Ends a run its breakpoint cut: the case's services are stopped, the harness takes the game back, the break bundle
    // is taken (which pauses the game) and the ring is written with the bundle as its next entry, and the game is left
    // loaded and paused on the kept process (Config.KeepLoaded). The report's "break" block says where and how to go on.
    private async Task<Exception> BreakRunAsync(
        AcceptanceCase c,
        RunOptions opts,
        CaseSession session,
        Config cfg,
        CheckpointRing? ring,
        ResumePlan resumed,
        BreakException broke,
        Report report)
    {
        ring?.Deactivate();
        using var bound = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await session.StopServicesAsync().ConfigureAwait(false);
        _ = session.Reattach();
        if (ring is null)
        {
            return new RunBrokeException($"paused at breakpoint ({broke.Reason}): the run has no checkpoint ring to bundle");
        }

        try
        {
            await _deps.Checkpointing.BreakRingAsync(c, opts, ring, resumed, broke, report, bound.Token).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            return new RunBrokeException($"paused at breakpoint ({broke.Reason}): {e.Message}");
        }

        // The bundle's capture paused the game; Close keeps it loaded when the process is kept at all.
        var block = report["break"].AsMap();
        if (report["keep"].AsBool() == true)
        {
            cfg.KeepLoaded = true;
            if (block is not null)
            {
                block["game"] = "paused and loaded on the kept process";
            }
        }
        else if (block is not null)
        {
            block["game"] = $"not kept ({Game.KeepGameEnv} or NoKeep); resume relaunches it from the bundle";
        }

        return new RunBrokeException("paused at breakpoint: " + broke.Reason);
    }

    // The -postmortem-only run: the bundle From names (the ring's failed bundle by default) is staged and loaded on the
    // kept process, its store copied to <output>/service.sqlite, and only the case's Postmortem runs over it. The ring is
    // left as it was, so the next plain run still resumes from it, and the report carries postmortem_only so the landing
    // gate refuses it like a resumed run.
    private async Task ExecutePostmortemAsync(AcceptanceCase c, RunOptions opts, string output, Report report, CancellationToken cancellationToken)
    {
        if (c.Postmortem is null)
        {
            throw new AcceptanceException($"case {c.Name} declares no Postmortem phase; -postmortem-only has nothing to run (split its Run into the scenario and the reads that assert on it)");
        }

        var (entry, ring) = PostmortemBundle(c, opts);
        Fingerprint fingerprint;
        try
        {
            fingerprint = _deps.Checkpointing.Fingerprint(c, opts.ConfigDir(c));
        }
        catch (Exception e) when (e is AcceptanceException or IOException)
        {
            throw new AcceptanceException("fingerprint this root: " + e.Message, e);
        }

        if (fingerprint.Mismatch(entry) is { Length: > 0 } reason)
        {
            throw new AcceptanceException($"bundle {entry.Path} cannot be loaded under this tree: {reason}");
        }

        var session = new CaseSession(c, report, _deps.Scenario, "", 0, StagePlan.None, "", "")
        {
            Resumed = entry,
            ResumeSuffix = opts.RunId(),
        };
        var cfg = NewConfig(c, opts, output);
        session.UseConfig(cfg);
        report["keep"] = !c.NoKeep && Game.KeepGame();
        report["checkpointing"] = "off (postmortem-only)";
        if (c.Expansions.Count > 0)
        {
            cfg.Expansions = [.. c.Expansions];
        }

        var save = Wrap("stage bundle", () => Checkpoints.StageCheckpoint(opts.Root, entry, Path.Combine(output, "service.sqlite")));
        var from = new JsonObject
        {
            ["path"] = entry.Path,
            ["label"] = entry.Label,
            ["offset_ms"] = entry.OffsetMs,
            ["tick"] = (long)entry.Tick,
            ["source_revision"] = entry.SourceRevision,
            ["save"] = save,
            ["store"] = entry.Store,
        };
        if (ring is not null && ring.FailedOutput.Length > 0)
        {
            var resultPath = Path.Combine(ring.FailedOutput, "result.json");
            if (ReadResult(resultPath) is { } prior)
            {
                session.Prior = prior;
                from["prior_result"] = resultPath;
            }
        }

        report["postmortem_only"] = true;
        report["postmortem_from"] = from;
        if (opts.Log is { } log)
        {
            await log.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"postmortem-only: {c.Name} over {entry.Path} (tick {entry.Tick}); the ring is untouched")).ConfigureAwait(false);
        }

        await StopOtherProfileAsync(c, opts, report, cancellationToken).ConfigureAwait(false);
        var lifecycle = await Session.OpenAsync(cfg, new RunnerHost(_host, c.NoKeep), report, new SaveStart { Name = save }, c.Quiet, c.KeepNeeds(), cancellationToken).ConfigureAwait(false);
        try
        {
            report["quiet_mode"] = QuietText(c.Quiet);
            session.UseSession(lifecycle);
            if (entry.Prepared is { } prepared)
            {
                session.UsePrepared(prepared);
                report["prepared"] = prepared;
            }

            await c.Postmortem(session, cancellationToken).ConfigureAwait(false);
            StartupRows.Check(output, cfg.Headless);
        }
        finally
        {
            await session.StopServicesAsync().ConfigureAwait(false);
            lifecycle.Close();
        }
    }

    // The resumption of an `acceptance dev` iteration: the bundle From names, or the ring's next entry (what a plain run
    // would resume from) and failing that its failed bundle, checked against this root's fingerprint. The ring index is
    // left out of the resumption so nothing rewinds or rewrites it.
    private ResumePlan PlanDev(AcceptanceCase c, RunOptions opts, TextWriter log)
    {
        if (_deps.Checkpointing.RingExcluded(c) is { Length: > 0 } excluded)
        {
            throw new AcceptanceException($"case {c.Name} never checkpoints ({excluded}): nothing to iterate from");
        }

        var (entry, _) = NamedBundle(c, opts, ring =>
        {
            if (ring is { Next.Length: > 0 })
            {
                if (ring.Entry(ring.Next) is { } next)
                {
                    return next.Path;
                }
            }
            else if (ring?.Failed is { } failed)
            {
                return failed.Path;
            }

            throw new AcceptanceException($"no bundle of {c.Name} in {opts.RingDir(c)}: a plain run that fails leaves a ring, or name a bundle with -from");
        });
        Fingerprint fingerprint;
        try
        {
            fingerprint = _deps.Checkpointing.Fingerprint(c, opts.ConfigDir(c));
        }
        catch (Exception e) when (e is AcceptanceException or IOException)
        {
            throw new AcceptanceException("fingerprint this root: " + e.Message, e);
        }

        if (fingerprint.Mismatch(entry) is { Length: > 0 } reason)
        {
            throw new AcceptanceException($"bundle {entry.Path} cannot be loaded under this tree: {reason}");
        }

        var rev = entry.SourceRevision.Length == 0 ? "unknown" : entry.SourceRevision.Length > 8 ? entry.SourceRevision[..8] : entry.SourceRevision;
        log.WriteLine(string.Create(CultureInfo.InvariantCulture, $"dev: {c.Name} from {entry.Path} (tick {entry.Tick}, rev {rev}); the ring is untouched"));
        return new ResumePlan { Entry = entry };
    }

    // Resolves RunOptions.From to a bundle of c for a postmortem-only run: a directory holding a sidecar, a label in c's
    // ring, or the ring's failed bundle. The ring index comes along when the bundle is one of its entries.
    private static (Checkpoint Entry, Ring? Ring) PostmortemBundle(AcceptanceCase c, RunOptions opts) =>
        NamedBundle(c, opts, ring =>
        {
            if (ring?.Failed is { } failed)
            {
                return failed.Path;
            }

            if (ring?.Break is not null && ring.Entry(Breakpoint.BreakCheckpoint) is { } paused)
            {
                // A run paused at a breakpoint left its state instead.
                return paused.Path;
            }

            throw new AcceptanceException($"no failed bundle of {c.Name} in {opts.RingDir(c)}: a plain run that fails leaves one, or name a bundle with -from");
        });

    // Resolves RunOptions.From to a bundle of c: a directory holding a sidecar, a label in c's ring, or with From empty
    // the bundle the fallback picks from the ring. The ring index comes along when the bundle is one of its entries.
    private static (Checkpoint Entry, Ring? Ring) NamedBundle(AcceptanceCase c, RunOptions opts, Func<Ring?, string> fallback)
    {
        var dir = opts.RingDir(c);
        var ring = Checkpoints.ReadRing(dir);
        var from = opts.From;
        if (from.Length == 0)
        {
            from = fallback(ring);
        }
        else if (!Path.IsPathRooted(from) && from.AsSpan().IndexOfAny('/', '\\') < 0)
        {
            from = Path.Combine(dir, from);
        }

        Checkpoint entry;
        try
        {
            entry = Checkpoints.ReadCheckpoint(from);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new AcceptanceException($"-from {opts.From}: no bundle there (`ls {dir}` lists the ring)", e);
        }

        if (entry.Case.Length > 0 && entry.Case != c.Name)
        {
            throw new AcceptanceException($"-from {opts.From} is a bundle of {entry.Case}, not {c.Name}");
        }

        var relative = Path.GetRelativePath(dir, entry.Path);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            ring = null;
        }

        return (entry, ring);
    }

    // Reads a result.json as JSON; null when absent or unreadable.
    private static JsonObject? ReadResult(string path)
    {
        try
        {
            return Dyn.ParseObject(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static Config NewConfig(AcceptanceCase c, RunOptions opts, string output) => new()
    {
        Root = opts.Root,
        Output = output,
        Headless = opts.Headless && !c.Rendered,
        Graphics = c.Graphics,
        GameId = opts.GameId,
        QuietWorld = c.QuietWorld,
    };

    // Runs a checkpoint/stage planning step, naming the step in the failure like Go's fmt.Errorf("...: %w").
    private static T Wrap<T>(string what, Func<T> step)
    {
        try
        {
            return step();
        }
        catch (Exception e) when (e is AcceptanceException or IOException or UnauthorizedAccessException)
        {
            throw new AcceptanceException(what + ": " + e.Message, e);
        }
    }

    // The session host the runner opens with: the shared one, plus NoKeep: a case that ends or replaces the process has
    // nothing to keep, so the hold it gets closes by stopping the game.
    private sealed class RunnerHost(ISessionHost inner, bool noKeep) : ISessionHost
    {
        public IStartCache StartCache => inner.StartCache;

        public Task PrepareAsync(Config config, IReadOnlyList<string> saves, CancellationToken cancellationToken) =>
            inner.PrepareAsync(config, saves, cancellationToken);

        public JsonObject PackageFiles(Config config) => inner.PackageFiles(config);

        public async Task<IGameHold> OpenGameAsync(Config config, Report report, CancellationToken cancellationToken)
        {
            var hold = await inner.OpenGameAsync(config, report, cancellationToken).ConfigureAwait(false);
            if (noKeep && hold is Game game)
            {
                game.Keep = false;
            }

            return hold;
        }

        public JsonObject RecordWorld(Config config, Start start, WorldSource source) => inner.RecordWorld(config, start, source);

        public string FixtureBuildHint(string op) => inner.FixtureBuildHint(op);
    }
}
