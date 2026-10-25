using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Host;
using RimGovernor.Brain.GameOrders.Host;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Telemetry.Otlp;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// A throughput run: plays one staged colony at each clock speed, governed, and measures ticks per second. Rows
/// report wall TPS including holds, native paused fraction,
/// hold counts by reason, resume latency and effective speed; sampled pause ratios are diagnostic only. The outcome
/// check exists only so a governed row cannot look fast because everyone idled: its colonists must complete brain-selected
/// work (<see cref="SpeedMatrix.CheckOutcomes"/>). The throughput fixture's stage is a quiet colony with frozen needs.
/// </summary>
internal sealed partial class SpeedmatrixRun
{
    internal const string PrepareTool = "test/throughput_prepare";
    internal const string StageSave = "RimGovernor-speedmatrix-stage";

    // Ticks is the game-tick target each speed runs to after resume (the serve-side tick must advance by this much) unless
    // the stage runs out of work first, which every speed does: the controller's cadence is wall-bound, so the ticks the
    // stage's work takes grow with the pace (~2,300 at Normal).
    internal const ulong Ticks = 30000;

    // Items sizes the stage: the Cloth stacks spawned outside the roofed stockpile.
    internal const int Items = 4;

    // Tolerance is the allowed spread of the healthy colonists across speeds.
    private const int Tolerance = 1;

    // The tick wait's hang guard: an hour of game time between enabled reviews at Normal speed and reduced ticks per second.
    private static readonly TimeSpan RowStall = TimeSpan.FromMinutes(3);

    // MaxPausedFraction and MinUltrafastTpsRatio are the clock-throughput expectations (0.5 and 2); at zero they are
    // reported, not enforced.
    private const double MaxPausedFraction = 0;
    private const double MinUltrafastTpsRatio = 0;

    private readonly Session _session;
    private readonly Config _config;
    private readonly Report _report;
    private readonly IReadOnlyList<SpeedCase> _cases;
    private readonly List<SpeedOutcome> _outcomes = [];
    private readonly List<JsonObject> _rows = [];

    private SpeedmatrixRun(Session session, Config config, Report report, IReadOnlyList<SpeedCase> cases)
    {
        _session = session;
        _config = config;
        _report = report;
        _cases = cases;
    }

    /// <summary>
    /// Runs the matrix on the game <paramref name="session"/> opened on the throughput fixture's prepared colony: the
    /// rows <paramref name="spec"/> names (empty: <see cref="SpeedMatrix.DefaultSpeedMatrix"/>), each into its own
    /// directory under the run's output.
    /// </summary>
    internal static async Task RunAsync(Session session, Config config, Report report, string spec, CancellationToken cancellationToken)
    {
        if (spec.Length == 0)
        {
            spec = SpeedMatrix.DefaultSpeedMatrix;
        }

        var speedCases = SpeedMatrix.ParseSpeedCases(spec);
        var m = new SpeedmatrixRun(session, config, report, speedCases);
        m._report["speeds_spec"] = spec;
        m._report["speeds"] = OutputJson.Array(speedCases, c => c.ToJson());
        m._report["tick_target"] = Ticks;

        // What the baseline was measured on: the revision, the world and mod set, the host, the launch's resolution and
        // the measured interval's bounds. A row's own wall_seconds completes it.
        m._report["provenance"] = m.Provenance();
        m._report["tolerance"] = Tolerance;
        m._report["max_paused_fraction"] = MaxPausedFraction;
        m._report["min_ultrafast_tps_ratio"] = MinUltrafastTpsRatio;
        try
        {
            await m.StageAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is AcceptanceException or IOException)
        {
            throw new AcceptanceException("stage: " + e.Message, e);
        }

        foreach (var c in m._cases)
        {
            SpeedOutcome outcome;
            try
            {
                outcome = await m.RunCaseAsync(c, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is AcceptanceException or IOException)
            {
                throw new AcceptanceException($"{c.Name}: {e.Message}", e);
            }

            m._outcomes.Add(outcome);
            m._report["outcomes"] = OutputJson.Array(m._outcomes, o => o.ToJson());
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"DONE {c.Name}: completed_tasks={outcome.CompletedTasks} colonists={outcome.HealthyColonists} unsuccessful={outcome.UnsuccessfulStages}"));
        }

        // Every required row is present and advanced the tick before any comparator, which accept an empty matrix, sees them.
        var metrics = SpeedMetrics.FromRows(m._rows);
        var rowProblems = SpeedMatrix.SpeedRowProblems(m._cases, m._outcomes, metrics);
        if (rowProblems.Count > 0)
        {
            m._report["row_problems"] = Problems(rowProblems);
            throw new AcceptanceException("speed rows missing or empty: " + string.Join("; ", rowProblems));
        }

        var outcomeProblems = SpeedMatrix.CheckOutcomes(m._outcomes, Tolerance);
        if (outcomeProblems.Count > 0)
        {
            m._report["outcome_problems"] = Problems(outcomeProblems);
            throw new AcceptanceException("pawn outcomes: " + string.Join("; ", outcomeProblems));
        }

        var metricProblems = SpeedMatrix.CheckSpeedMetrics(metrics, MaxPausedFraction, MinUltrafastTpsRatio);
        if (metricProblems.Count > 0)
        {
            m._report["metric_problems"] = Problems(metricProblems);
            throw new AcceptanceException("clock throughput short of the thresholds: " + string.Join("; ", metricProblems));
        }

        // The run's last assertion (no native error in the game's startup log) is a no-op there.
    }

    private static JsonArray Problems(IEnumerable<string> problems) => OutputJson.Array(problems);

    // A harness over the session's game whose evidence goes under another directory.
    private Harness HarnessFor(string output)
    {
        var shared = _session.Harness;
        return new Harness(shared.Transport, output) { Tools = shared.Tools };
    }

    /// <summary>Saves the prepared colony as the stage save for every case to reload.</summary>
    private async Task StageAsync(CancellationToken cancellationToken)
    {
        var stageOutput = Path.Combine(_config.Output, "stage");
        _ = Directory.CreateDirectory(stageOutput);
        var h = HarnessFor(stageOutput);

        // Frozen needs do not survive a reload (the op is per game), so the save carries the stage only; each case
        // freezes again after loading.
        var started = DateTime.UtcNow;
        _ = await h.CallAsync("save-stage", "save_game", HostJson.Arguments(new SaveGameRequest { SaveName = StageSave }), cancellationToken).ConfigureAwait(false);
        await WaitSavedAsync(started, cancellationToken).ConfigureAwait(false);
    }

    // Waits for the game to finish writing the stage save into the active profile's Saves directory (headless-profile or profile).
    private async Task WaitSavedAsync(DateTime started, CancellationToken cancellationToken)
    {
        var root = _config.Root;
        var deadline = started.AddSeconds(90);

        // A save the game is still writing grows, so a candidate counts once its length has held for two seconds.
        var stableFor = TimeSpan.FromSeconds(2);
        var seen = new Dictionary<string, (long Length, DateTime Since)>(StringComparer.Ordinal);
        using var pace = Wait.Pace(TimeSpan.FromSeconds(1));
        while (true)
        {
            var now = DateTime.UtcNow;
            foreach (var dir in new[] { "headless-profile", "profile" })
            {
                var candidate = Path.Combine(root, dir, "Saves", StageSave + ".rws");
                var info = new FileInfo(candidate);
                if (!info.Exists || info.Length == 0 || info.LastWriteTimeUtc < started.AddSeconds(-1))
                {
                    _ = seen.Remove(candidate);
                    continue;
                }

                if (seen.TryGetValue(candidate, out var last) && last.Length == info.Length)
                {
                    if (now - last.Since >= stableFor)
                    {
                        _report["stage_save"] = candidate;
                        return;
                    }
                }
                else
                {
                    seen[candidate] = (info.Length, now);
                }
            }

            if (now > deadline)
            {
                throw new AcceptanceException($"stage save {StageSave} did not appear under {root} within 90s");
            }

            await pace.NextAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // Reloads the stage, hands the game to one serve process at the case's speed, waits out the tick target and reads
    // the outcome back.
    private async Task<SpeedOutcome> RunCaseAsync(SpeedCase c, CancellationToken cancellationToken)
    {
        var output = Path.Combine(_config.Output, c.Name);
        _ = Directory.CreateDirectory(output);
        var report = Report.Create("speed case " + c.Name, _config.Headless);
        report["case"] = c.ToJson();
        try
        {
            var outcome = await RunCaseCoreAsync(c, output, report, cancellationToken).ConfigureAwait(false);
            report["passed"] = true;
            return outcome;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            report["error"] = e.Message;
            throw;
        }
        finally
        {
            _ = report.Finalize(output);
        }
    }

    private async Task<SpeedOutcome> RunCaseCoreAsync(SpeedCase c, string output, Report report, CancellationToken cancellationToken)
    {
        ServiceProcess? service = null;
        Journal? journal = null;
        Func<Task<JsonObject>>? stopKeepAlive = null;
        var started = DateTime.UtcNow;
        try
        {
            var h = HarnessFor(output);
            _ = await h.LoadGameReadyAsync("load-stage", new() { SaveName = StageSave }, cancellationToken).ConfigureAwait(false);
            _ = await h.PauseClockAsync("pause", cancellationToken).ConfigureAwait(false);
            var identity = await ReadIdentityAsync(h, cancellationToken).ConfigureAwait(false);
            report["frozen_needs"] = (await Needs.FreezeNeedsAsync(h, null, [], cancellationToken).ConfigureAwait(false)).DeepClone();
            var startTick = await ReadTickAsync(h, "start-tick", cancellationToken).ConfigureAwait(false);

            service = await ServiceProcess.LaunchServiceAsync(
                _config,
                new ServiceLaunch
                {
                    Families = ["work"],
                    ClockSpeed = c.Speed,
                    Output = output,
                    Report = report,
                },
                _report,
                cancellationToken,
                _session.Harness).ConfigureAwait(false);
            var token = await service.SessionTokenAsync(cancellationToken).ConfigureAwait(false);
            var attached = await service.WaitAttachedAsync(identity, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
            report["service_state_attached"] = attached.DeepClone();
            var prefix = "speedmatrix-" + c.Name.ToLowerInvariant() + "-" + RandomSuffix();
            journal = await ServiceJournal.OpenWithRetryAsync(_config, cancellationToken).ConfigureAwait(false);
            var rowWorld = Harness.IdentityOf(identity);
            var rootPlanId = await service.ResumeAsync(prefix, identity, token, report, cancellationToken).ConfigureAwait(false);
            report["root_plan"] = rootPlanId;

            // The row's native clock accounting is the difference between this read and the one when the wait ends.
            var accountingStart = await ClockAccounting.ReadAsync(h, "clock-accounting-start", cancellationToken).ConfigureAwait(false);
            var resumedAt = DateTimeOffset.UtcNow;
            var keepAlive = new AuthorityKeepAlive(service, prefix, identity, token);
            stopKeepAlive = keepAlive.Start(cancellationToken);

            _ = await ServiceJournal.WaitReviewAsync(service, journal, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);

            // The target is game time: the wait ends once the tick of the newest enabled review of the row's own load has
            // advanced by the target past the reload tick. The signature is the tick itself, so a clock that stops
            // advancing stalls the wait. Enabled reviews are an hour (3000 ticks) apart when nothing else wakes one, which at
            // Normal speed is a minute or more of wall time, so the hang guard is at least RowStall.
            ulong lastTick = 0;
            Exception? waitErr = null;
            try
            {
                await new Wait { Stall = Wait.StallBudget() > RowStall ? Wait.StallBudget() : RowStall, Interval = TimeSpan.FromSeconds(2), Terminal = service.Exited }.WaitProgressAsync(
                    async ct =>
                    {
                        var review = await journal.LoadReviewAsync(ct).ConfigureAwait(false);
                        lastTick = ReviewedTick(review, rowWorld, startTick);
                        return (Wait.Signature(lastTick), lastTick >= startTick + Ticks);
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (WaitError e)
            {
                waitErr = e;
            }

            var wallSeconds = (DateTimeOffset.UtcNow - resumedAt).TotalSeconds;
            report["wait"] = OutputJson.Fields(("start_tick", startTick), ("last_tick", lastTick), ("wall_seconds", wallSeconds));
            if (waitErr is not null)
            {
                try
                {
                    var final = await journal.LoadReviewAsync(cancellationToken).ConfigureAwait(false);
                    report["rounds_review_at_failure"] = OutputJson.Fields(("revision", final.Revision), ("tick", final.Tick.Value), ("progress", final.Progress.Count));
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _ = e;
                }

                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"tick target wait (start {startTick}, last {lastTick}, want +{Ticks}): {waitErr.Message}"), waitErr);
            }

            var accountingEnd = await ClockAccounting.ReadAsync(h, "clock-accounting-end", cancellationToken).ConfigureAwait(false);
            var (unsuccessful, planCount) = await CountUnsuccessfulAsync(journal, cancellationToken).ConfigureAwait(false);
            report["plans_inspected"] = planCount;
            journal.Dispose();
            journal = null;
            report["authority_reacquisitions"] = (await stopKeepAlive().ConfigureAwait(false)).DeepClone();
            stopKeepAlive = null;
            _ = await service.StopAsync().ConfigureAwait(false);

            // The in-game brain writes its OTLP rings in the game's channel directory: the row's slice of them is its
            // timeline, the same slice a cataloged case's evidence takes.
            if (CaseEvidence.Capture(GameSessions.EndpointDirectory(_config.Configuration, _config.GameId), CaseRows.Of(_config, started), output) is { Length: > 0 } evidenceError)
            {
                throw new AcceptanceException("row evidence: " + evidenceError);
            }

            var timeline = OtlpTimeline.Read(ServiceLogs.ServiceLogsPath(output));
            var phases = PhaseSummary.Summarize(timeline, accountingStart, accountingEnd);
            var holds = StopSummaries.SummarizeHolds(timeline, resumedAt.ToUnixTimeMilliseconds());
            report["holds"] = holds.ToJson();
            var metrics = CaseMetrics(c, phases, holds, startTick, lastTick, wallSeconds);
            report["metrics"] = metrics.DeepClone();
            AppendMetrics(metrics);

            // Independent native read after the service released the slot.
            h = HarnessFor(output);
            _ = await h.PauseClockAsync("pause-after", cancellationToken).ConfigureAwait(false);

            // The service was stopped, not shut down: its authority grant lingers until its authority is released, and the
            // next reload would carry it.
            if (await Authority.ReleaseAuthorityAsync(h, identity, cancellationToken).ConfigureAwait(false) is { } revoked)
            {
                report["authority_released"] = revoked.DeepClone();
            }

            var healthy = await ClockFixture.RequireHealthyColonistsAsync(h, "pawns-after", identity, cancellationToken).ConfigureAwait(false);
            var outcome = new SpeedOutcome
            {
                Case = c.Name,
                CompletedTasks = SpeedMatrix.CompletedTasks(timeline),
                HealthyColonists = healthy,
                UnsuccessfulStages = unsuccessful,
            };
            report["outcome"] = outcome.ToJson();
            return outcome;
        }
        finally
        {
            if (stopKeepAlive is not null)
            {
                report["authority_reacquisitions"] = (await stopKeepAlive().ConfigureAwait(false)).DeepClone();
            }

            journal?.Dispose();
            if (service is not null)
            {
                _ = await service.StopAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The game tick a row has reached by its journal's <paramref name="review"/>: the review's tick once it is an enabled
    /// review of the row's own load (<paramref name="row"/>, the reload's identity), the reload's
    /// <paramref name="startTick"/> until then. The kept journal outlives a row, and its revision is no row marker:
    /// resuming writes a disabled review that carries the previous row's world and tick forward under a new revision,
    /// and a new world restarts the count.
    /// </summary>
    internal static ulong ReviewedTick(ReviewState review, RimGovernor.Brain.GameFacts.Common.Identity row, ulong startTick) =>
        review.Enabled
        && review.Snapshot.Colony.Value == row.ColonyId
        && review.Snapshot.Load.Value == row.LoadToken
        && review.Snapshot.Map.Value == row.MapId
            ? (ulong)review.Tick.Value
            : startTick;

    // Counts Unsuccessful action stages over every routine method the service dispatched.
    // LoadPlan sees retired plans; LoadPlans would hide them.
    private static async Task<(int Unsuccessful, int Plans)> CountUnsuccessfulAsync(Journal journal, CancellationToken cancellationToken)
    {
        var seen = new HashSet<PlanId>();
        var ids = new List<PlanId>();
        var review = await journal.LoadReviewAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in review.Progress)
        {
            if (review.Revision == 0 || new NeedId(row.Concern.Value) is not { IsValid: true } need)
            {
                continue;
            }

            foreach (var method in journal.LoadNeed(need, review.Snapshot, cancellationToken).Methods)
            {
                if (seen.Add(method.Plan))
                {
                    ids.Add(method.Plan);
                }
            }
        }

        var unsuccessful = 0;
        foreach (var id in ids)
        {
            PlanState state;
            try
            {
                state = await journal.LoadPlanAsync(id, cancellationToken).ConfigureAwait(false);
            }
            catch (NotFoundException)
            {
                continue;
            }

            foreach (var progress in state.Progress)
            {
                if (progress.View.Stage == Stage.Unsuccessful)
                {
                    unsuccessful++;
                }
            }
        }

        return (unsuccessful, ids.Count);
    }

    // The per-speed row the issue asks for.
    private static JsonObject CaseMetrics(SpeedCase c, PhaseSummary phases, HoldSummary holds, ulong startTick, ulong lastTick, double wallSeconds)
    {
        var steps = phases.Steps;
        var clock = phases.Clock;
        double readsPerStep = 0;
        double gateWaitMean = 0;
        if (steps.Steps > 0)
        {
            readsPerStep = (double)steps.Reads / steps.Steps;
            gateWaitMean = steps.GateWaitMs / steps.Steps;
        }

        // Wall TPS from the row's reviewed ticks; holds count as wall time.
        var wallTps = wallSeconds > 0 && lastTick > startTick ? (lastTick - startTick) / wallSeconds : 0.0;

        // Holds per 6000 ticks: how often the clock was held, however the row's length differs across speeds.
        double holdsPer6000 = 0;
        if (lastTick > startTick)
        {
            holdsPer6000 = (double)holds.Holds * 6000 / (lastTick - startTick);
        }

        var reasons = Counts(holds.Reasons.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        var runVerdicts = Counts(steps.RunVerdicts);
        var stepReasons = Counts(steps.Reasons);
        var hazardGaps = OutputJson.Array(clock.NativeHazardGaps, g => OutputJson.Fields(
            ("hazard_class", g.HazardClass),
            ("bound_ticks", g.BoundTicks),
            ("max_tick_gap", g.MaxTickGap),
            ("hooked", g.Hooked)));
        var latencies = OutputJson.Array(holds.Latencies, l => l.ToJson());
        return OutputJson.Fields(
            ("case", c.Name),
            ("speed", c.Speed),

            // The one ceiling's frame account: frames paced, those over the frame budget and the widest frame's tick work.
            ("paced_frames", clock.NativePacedFrames),
            ("paced_frames_over_budget", clock.NativePacedOverBudget),
            ("max_paced_frame_ms", clock.NativeMaxPacedFrameMs),

            ("ticks_advanced", lastTick - startTick),
            ("wall_seconds", wallSeconds),
            ("wall_tps", wallTps),

            // Effective speed: the share of the speed's own rate the row achieved, holds included (1 is a clock that was
            // never held and never slowed).
            ("effective_speed", SpeedMatrix.EffectiveSpeed(wallTps, c.Speed)),
            ("target_tps", SpeedMatrix.TargetTps(c.Speed)),
            ("paused_ms", clock.NativePausedMs),
            ("running_ms", clock.NativeRunningMs),
            ("paused_fraction_native", clock.PausedFractionNative()),

            // The supervisor's probe path split: hazard probe against the fact-change digests, session-cumulative
            // between the row's two clock accounting reads, and the detection gaps the session reported.
            ("probe_ms", clock.NativeProbeMs),
            ("digest_ms", clock.NativeDigestMs),
            ("digest_share", clock.DigestShare()),
            ("probes", clock.NativeProbes),
            ("digests", clock.NativeDigests),
            ("max_probe_tick_gap", clock.NativeMaxProbeTickGap),
            ("hazard_gaps", hazardGaps),
            ("steps", steps.Steps),
            ("reads_per_step", readsPerStep),

            // The mean and worst step wall, and the player-gate wait a step spent queued behind the Worker's dispatch step.
            ("step_ms_mean", steps.StepMs()),
            ("step_ms_max", steps.MaxElapsedMs),
            ("gate_wait_ms_mean", gateWaitMean),
            ("gate_wait_ms_max", steps.MaxGateWaitMs),

            // Hold metrics, from native's hold and resume rows: holds by reason (letter_pause, review, coupled_order,
            // ...) and the native time each hold lasted before a Run resumed it.
            ("holds", holds.Holds),
            ("holds_per_6000_ticks", holdsPer6000),
            ("hold_reasons", reasons),
            ("resume_latency_ms_mean", holds.MeanResumeMs),
            ("resume_latency_ms_max", holds.MaxResumeMs),
            ("resume_samples", holds.ResumeCount),

            // Per hold: detect_ticks, hold_ticks, resume_ms.
            ("hold_latencies", latencies),

            // The steps a hold woke and how long the wake took; the brain's own Run, Hold and refusal rows, and the
            // coupled orders that rode on them.
            ("hold_woken_steps", steps.HoldWoken),
            ("hold_wake_ms_mean", phases.MeanHoldWakeMs),
            ("hold_wake_ms_max", phases.MaxHoldWakeMs),
            ("run_verdicts", runVerdicts),
            ("coupled_runs", steps.CoupledRuns),
            ("coupled_orders", steps.CoupledOrders),

            // Live dispatch: steps by reason, the worker's native runs made under
            // a running clock and the fraction native refused.
            ("step_reasons", stepReasons),
            ("dispatches", phases.Dispatch.Calls),
            ("live_dispatches", phases.Dispatch.Live),
            ("refused_dispatches", phases.Dispatch.Refused),
            ("live_refused_dispatches", phases.Dispatch.LiveRefused),
            ("refused_fraction", phases.Dispatch.RefusedFraction()),

            // Dispatches held on stale facts before native ran them.
            ("stale_facts_holds", phases.Dispatch.StaleHolds));
    }

    // A count per key, as an object in the pairs' order.
    private static JsonObject Counts(IEnumerable<KeyValuePair<string, int>> pairs) =>
        new(pairs.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value)));

    private static JsonObject Counts(IEnumerable<KeyValuePair<string, ulong>> pairs) =>
        new(pairs.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value)));

    // Records a metrics row on the matrix report and for the comparators (the report holds its own copy).
    private void AppendMetrics(JsonObject row)
    {
        _rows.Add(row);
        _report["speed_metrics"] = OutputJson.Array(_rows, r => r.DeepClone());
    }

    private static string RandomSuffix() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

    // Reads the loaded identity the stage reload produced.
    private static Task<JsonObject> ReadIdentityAsync(Harness h, CancellationToken cancellationToken) =>
        Session.ReadIdentityAsync(h, "identity", cancellationToken);

    // The loaded tick.
    private static async Task<ulong> ReadTickAsync(Harness h, string label, CancellationToken cancellationToken) =>
        (ulong)((await h.LoadedTickAsync(label, cancellationToken).ConfigureAwait(false)).Context?.Tick
            ?? throw new AcceptanceException($"{label}: lifecycle_read_tick loaded without a context"));
}
