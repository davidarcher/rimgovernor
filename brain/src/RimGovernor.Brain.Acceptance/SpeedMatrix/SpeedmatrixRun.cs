using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Contracts.Host;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Telemetry.Otlp;
using Fx = RimGovernor.Fixtures.Contracts;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// What the matrix profiles differ by: the stage the fixture prepares, the save it is kept
/// in, the tick target each row runs to and the rows compared. <c>acceptance speedmatrix</c> (plain) is the whole speed
/// sweep on a quiet flat debug colony; <c>-profile observations</c> is the rendered observation baseline on the tribal8 colony.
/// </summary>
internal sealed record SpeedmatrixProfile
{
    public int Items { get; init; }

    public int Segments { get; init; }

    public ulong Ticks { get; init; }

    public string Save { get; init; } = "";

    public string Speeds { get; init; } = "";
}

/// <summary>
/// Runs identical staged work at each clock speed and checks equal game outcomes.
/// Governor-off measures the simulation ceiling outside that outcome comparison. Governed rows report wall TPS
/// including holds, native paused fraction, hold counts by reason, resume latency and effective speed; sampled pause ratios are diagnostic only. The throughput
/// fixture provides construction and hauling work in a quiet colony with frozen needs.
/// </summary>
internal sealed partial class SpeedmatrixRun
{
    internal const string PrepareTool = "test/throughput_prepare";
    internal const string ControlTool = "test/throughput_control";
    internal const string StageSave = "RimGovernor-speedmatrix-stage";

    // Ticks is the game-tick target each speed runs to after resume (the serve-side tick must advance by this much) unless
    // the stage runs out of work first, which every speed does: the controller's cadence is wall-bound, so the ticks the
    // stage's work takes grow with the pace, from ~2,300 at Normal to ~20,000 at boosted Ultrafast.
    internal const ulong Ticks = 30000;

    // Items and Segments size the stage: Steel stacks spawned and wall segments laid out.
    internal const int Items = 4;
    internal const int Segments = 6;

    // Tolerance is the allowed spread of each pawn-outcome postcondition across speeds.
    private const int Tolerance = 1;

    // MaxPausedFraction and MinUltrafastTpsRatio are the clock-throughput expectations (0.5 and 2); at zero they are
    // reported, not enforced.
    private const double MaxPausedFraction = 0;
    private const double MinUltrafastTpsRatio = 0;

    private readonly SpeedmatrixProfile _profile;
    private readonly Session _session;
    private readonly Config _config;
    private readonly Report _report;
    private readonly IReadOnlyList<SpeedCase> _cases;
    private readonly List<SpeedOutcome> _outcomes = [];
    private readonly List<JsonObject> _rows = [];

    // The fixture's cell lists, in the "x:z" form test/throughput_control reads back.
    private string _storage = "";
    private string _walls = "";

    private SpeedmatrixRun(SpeedmatrixProfile profile, Session session, Config config, Report report, IReadOnlyList<SpeedCase> cases)
    {
        _profile = profile;
        _session = session;
        _config = config;
        _report = report;
        _cases = cases;
    }

    /// <summary>The whole speed sweep on a quiet flat debug colony.</summary>
    internal static SpeedmatrixProfile PlainProfile { get; } = new()
    {
        Items = Items,
        Segments = Segments,
        Ticks = Ticks,
        Save = StageSave,
        Speeds = SpeedMatrix.DefaultSpeedMatrix,
    };

    /// <summary>
    /// Runs the matrix on the game <paramref name="session"/> opened on the throughput fixture's prepared colony: the
    /// rows <paramref name="spec"/> names (empty: the profile's), each into its own directory under the run's output.
    /// </summary>
    internal static async Task RunAsync(Session session, Config config, Report report, SpeedmatrixProfile p, string spec, CancellationToken cancellationToken)
    {
        if (spec.Length == 0)
        {
            spec = p.Speeds;
        }

        var speedCases = SpeedMatrix.ParseSpeedCases(spec);
        var m = new SpeedmatrixRun(p, session, config, report, speedCases);
        m._report["speeds_spec"] = spec;
        m._report["speeds"] = OutputJson.Array(speedCases, c => c.ToJson());
        m._report["tick_target"] = p.Ticks;

        // What the baseline was measured on: the revision, the world and mod set, the host, the launch's resolution and
        // the measured interval's bounds. A row's own wall_seconds completes it.
        m._report["provenance"] = m.Provenance();
        m._report["tolerance"] = Tolerance;
        m._report["max_paused_fraction"] = MaxPausedFraction;
        m._report["min_ultrafast_tps_ratio"] = MinUltrafastTpsRatio;
        if (!Dyn.Contains(session.Names, ControlTool))
        {
            throw new AcceptanceException($"missing {ControlTool} in discovery; rebuild the native mod with -Fixture ThroughputFixture");
        }

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
            SpeedOutcome? outcome = null;
            try
            {
                if (c.GovernorOff)
                {
                    await m.RunGovernorOffAsync(c, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    outcome = await m.RunCaseAsync(c, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is AcceptanceException or IOException)
            {
                throw new AcceptanceException($"{c.Name}: {e.Message}", e);
            }

            if (!c.Compared || outcome is null)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"DONE {c.Name}: no controller"));
                continue;
            }

            m._outcomes.Add(outcome);
            m._report["outcomes"] = OutputJson.Array(m._outcomes, o => o.ToJson());
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"DONE {c.Name}: stored={outcome.StoredUnits}/{outcome.StoredUnits + outcome.LooseUnits} walls={outcome.WallsBuilt} colonists={outcome.HealthyColonists} unsuccessful={outcome.UnsuccessfulStages}"));
        }

        // Every required row is present and advanced the tick before any comparator, which accept an empty matrix, sees them.
        var metrics = SpeedMetrics.FromRows(m._rows);
        var rowProblems = SpeedMatrix.SpeedRowProblems(m._cases, m._outcomes, metrics);
        if (rowProblems.Count > 0)
        {
            m._report["row_problems"] = Problems(rowProblems);
            throw new AcceptanceException("speed rows missing or empty: " + string.Join("; ", rowProblems));
        }

        var ratios = SpeedMatrix.CeilingRatios(metrics);
        if (ratios.Count > 0)
        {
            m._report["ceiling_ratios"] = OutputJson.Array(ratios, r => r.ToJson());
        }

        var outcomeProblems = SpeedMatrix.CompareOutcomes(m._outcomes, Tolerance);
        if (outcomeProblems.Count > 0)
        {
            m._report["outcome_problems"] = Problems(outcomeProblems);
            throw new AcceptanceException("pawn outcomes differ across speeds: " + string.Join("; ", outcomeProblems));
        }

        // The stage must have been reachable: a matrix where nothing was hauled or built agrees trivially and proves nothing.
        foreach (var o in m._outcomes)
        {
            if (o.StoredUnits == 0 && o.WallsBuilt == 0)
            {
                throw new AcceptanceException($"{o.Case}: nothing was hauled or built within the tick target; raise ticks or check the stage");
            }
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

    /// <summary>Takes the fixture's layout from the prepared colony and saves it as the stage save for every case to reload.</summary>
    private async Task StageAsync(CancellationToken cancellationToken)
    {
        var prepared = Harness.FixtureReply<Fx.ThroughputPrepared>(
            PrepareTool, _session.Prepared ?? throw new AcceptanceException($"prepared: {PrepareTool} replied nothing"));
        var stageOutput = Path.Combine(_config.Output, "stage");
        _ = Directory.CreateDirectory(stageOutput);
        var h = HarnessFor(stageOutput);
        _storage = CellList(prepared.StorageCells.Select(c => (c.X, c.Z)));
        _walls = CellList(prepared.Sites.Select(s => (s.X, s.Z)));

        if (prepared.Sites.Count != _profile.Segments)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"stage laid out {prepared.Sites.Count} wall sites, want {_profile.Segments}"));
        }

        // Frozen needs do not survive a reload (the op is per game), so the save carries the stage only; each case
        // freezes again after loading.
        var started = DateTime.UtcNow;
        _ = await h.CallAsync("save-stage", "rimgovernor/save_game", HostJson.Arguments(new SaveGameRequest { SaveName = _profile.Save }), cancellationToken).ConfigureAwait(false);
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
                var candidate = Path.Combine(root, dir, "Saves", _profile.Save + ".rws");
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
                throw new AcceptanceException($"stage save {_profile.Save} did not appear under {root} within 90s");
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
        ObservationReaders? readers = null;
        Func<Task<JsonObject>>? stopKeepAlive = null;
        try
        {
            var h = HarnessFor(output);
            _ = await h.LoadGameReadyAsync("load-stage", new() { SaveName = _profile.Save }, cancellationToken).ConfigureAwait(false);
            _ = await h.PauseClockAsync("pause", cancellationToken).ConfigureAwait(false);
            var identity = await ReadIdentityAsync(h, cancellationToken).ConfigureAwait(false);
            report["frozen_needs"] = (await Needs.FreezeNeedsAsync(h, null, [], cancellationToken).ConfigureAwait(false)).DeepClone();
            var startTick = await FreshStageTickAsync(h, identity, report, cancellationToken).ConfigureAwait(false);

            // The brain runs in the game process, so the serve flag the case adds (--clock-test-acceleration)
            // cannot be applied; the launch records them under its env entries.
            var serveArgs = c.ServeArgs();
            service = await ServiceProcess.LaunchServiceAsync(
                _config,
                new ServiceLaunch
                {
                    Families = ["work"],
                    Env = serveArgs.Count == 0 ? [] : ["serve_args=" + string.Join(' ', serveArgs)],
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

            // The kept game's journal outlives a row, so the previous row's review (and its tick) is still in it.
            var staleRevision = (await journal.LoadRoundsAsync(cancellationToken).ConfigureAwait(false)).Revision;
            var rootPlanId = await service.ResumeAsync(prefix, identity, token, report, cancellationToken).ConfigureAwait(false);
            report["root_plan"] = rootPlanId;

            // The observation-load row: concurrent state readers.
            if (c.ObservationLoad)
            {
                readers = ObservationReaders.Start(service, SpeedMatrix.ObservationLoadReaders, SpeedMatrix.ObservationLoadInterval, cancellationToken);
            }

            // The row's native clock accounting is the difference between this read and the one when the wait ends.
            var accountingStart = await ClockAccounting.ReadAsync(h, "clock-accounting-start", cancellationToken).ConfigureAwait(false);
            var resumedAt = DateTimeOffset.UtcNow;
            var keepAlive = new AuthorityKeepAlive(service, prefix, identity, token);
            stopKeepAlive = keepAlive.Start(cancellationToken);

            _ = await ServiceJournal.WaitRoundsAsync(service, journal, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);

            // The target is game time: the wait ends once the tick the service last observed (its OTLP log ring's newest
            // clock sample; the routine review's tick only moves once per full step) has
            // advanced by the target past the reload tick. The signature is the tick itself, so a clock that stops advancing
            // stalls the wait.
            ulong lastTick = 0;
            Exception? waitErr = null;
            try
            {
                await new Wait { Stall = Wait.StallBudget(), Interval = TimeSpan.FromSeconds(2), Terminal = service.Exited }.WaitProgressAsync(
                    async ct =>
                    {
                        var review = await journal.LoadRoundsAsync(ct).ConfigureAwait(false);
                        lastTick = review.Revision > staleRevision ? (ulong)review.Tick.Value : startTick;
                        return (Wait.Signature(lastTick), lastTick >= startTick + _profile.Ticks);
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
                    var final = await journal.LoadRoundsAsync(cancellationToken).ConfigureAwait(false);
                    report["rounds_review_at_failure"] = OutputJson.Fields(("revision", final.Revision), ("tick", final.Tick.Value), ("progress", final.Progress.Count));
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _ = e;
                }

                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"tick target wait (start {startTick}, last {lastTick}, want +{_profile.Ticks}): {waitErr.Message}"), waitErr);
            }

            var accountingEnd = await ClockAccounting.ReadAsync(h, "clock-accounting-end", cancellationToken).ConfigureAwait(false);
            var (unsuccessful, planCount) = await CountUnsuccessfulAsync(journal, cancellationToken).ConfigureAwait(false);
            report["plans_inspected"] = planCount;
            journal.Dispose();
            journal = null;
            JsonObject? readerSummary = null;
            if (readers is not null)
            {
                readerSummary = await readers.StopAsync().ConfigureAwait(false);
                report["readers"] = readerSummary.DeepClone();
                readers = null;
            }

            report["authority_reacquisitions"] = (await stopKeepAlive().ConfigureAwait(false)).DeepClone();
            stopKeepAlive = null;
            _ = await service.StopAsync().ConfigureAwait(false);
            var timeline = OtlpTimeline.Read(ServiceLogs.ServiceLogsPath(output));
            var phases = PhaseSummary.Summarize(timeline, accountingStart, accountingEnd);
            var holds = StopSummaries.SummarizeHolds(timeline, resumedAt.ToUnixTimeMilliseconds());
            report["holds"] = holds.ToJson();
            var metrics = CaseMetrics(c, phases, holds, startTick, lastTick, wallSeconds);
            if (readerSummary is not null)
            {
                metrics.Add("readers", readerSummary.DeepClone());
                var readerProblems = ObservationReaders.ReaderProblems(readerSummary);
                if (readerProblems.Count > 0)
                {
                    throw new AcceptanceException("observation-load row: " + string.Join("; ", readerProblems));
                }
            }

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

            var after = await ControlAsync(h, identity, "control-after", cancellationToken).ConfigureAwait(false);
            report["control_after"] = after.DeepClone();
            var healthy = await ClockFixture.RequireHealthyColonistsAsync(h, "pawns-after", identity, cancellationToken).ConfigureAwait(false);
            var outcome = SpeedMatrix.OutcomeFromControl(c.Name, after) with
            {
                HealthyColonists = healthy,
                UnsuccessfulStages = unsuccessful,
            };
            report["outcome"] = outcome.ToJson();
            if (c.Player)
            {
                // Player acceleration's bounds, checked on the row's own recording once the outcome above has been read.
                var problems = SpeedMatrix.PlayerRowProblems(new PlayerRow
                {
                    HazardGaps = phases.Clock.NativeHazardGaps,
                    PacedFrames = phases.Clock.NativePacedFrames,
                    OverBudget = phases.Clock.NativePacedOverBudget,
                    LastPacingReason = SpeedMatrix.LastPacingReason(timeline),
                });
                if (problems.Count > 0)
                {
                    throw new AcceptanceException("player row: " + string.Join("; ", problems));
                }
            }

            return outcome;
        }
        finally
        {
            if (readers is not null)
            {
                report["readers"] = (await readers.StopAsync().ConfigureAwait(false)).DeepClone();
            }

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

    // Reads the stage's counters through test/throughput_control: the reply as recorded (the report's control blocks and
    // the outcome read it whole) and as its record.
    private async Task<JsonObject> ControlAsync(Harness h, JsonObject identity, string label, CancellationToken cancellationToken) =>
        (await ControlReadAsync(h, identity, label, cancellationToken).ConfigureAwait(false)).Raw;

    private async Task<(JsonObject Raw, Fx.ThroughputControlRead Read)> ControlReadAsync(Harness h, JsonObject identity, string label, CancellationToken cancellationToken)
    {
        var id = Harness.IdentityOf(identity);
        var request = new Fx.ThroughputControlRequest { ColonyId = id.ColonyId, LoadToken = id.LoadToken, MapId = id.MapId, StorageCells = _storage, WallCells = _walls };
        var reply = await h.CallAsync(label, ControlTool, FixtureRecordJson.Args(request), cancellationToken).ConfigureAwait(false);
        var read = Harness.FixtureReply<Fx.ThroughputControlRead>(ControlTool, reply);
        return read.Success ? (reply, read) : throw new AcceptanceException($"{ControlTool} refused: {reply.ToJson()}");
    }

    // Reads the reloaded stage's counters into the report (control_before), requires them fresh (nothing stored, built or
    // laid out) and returns the reload's tick.
    private async Task<ulong> FreshStageTickAsync(Harness h, JsonObject identity, Report report, CancellationToken cancellationToken)
    {
        var (before, read) = await ControlReadAsync(h, identity, "control-before", cancellationToken).ConfigureAwait(false);
        report["control_before"] = before.DeepClone();
        if (read.StoredUnits != 0 || read.WallsBuilt != 0 || read.WallBlueprints != 0)
        {
            throw new AcceptanceException("stage is not fresh after reload: " + before.ToJson());
        }

        return (ulong)read.Tick;
    }

    // Counts Unsuccessful action stages over every routine method the service dispatched.
    // LoadPlan sees retired plans; LoadPlans would hide them.
    private static async Task<(int Unsuccessful, int Plans)> CountUnsuccessfulAsync(Journal journal, CancellationToken cancellationToken)
    {
        var seen = new HashSet<PlanId>();
        var ids = new List<PlanId>();
        var review = await journal.LoadRoundsAsync(cancellationToken).ConfigureAwait(false);
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

        // Wall TPS from the row's own tick reads, as the governor-off row computes it; holds count as wall time.
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
            ("test_acceleration", c.TestAcceleration),
            ("player", c.Player),

            // Player acceleration's frame account: frames paced, those over the frame budget and the widest frame's tick work.
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

    // Renders fixture cells as the "x:z,x:z" list test/throughput_control parses.
    private static string CellList(IEnumerable<(int X, int Z)> cells) =>
        string.Join(',', cells.Select(cell => string.Create(CultureInfo.InvariantCulture, $"{cell.X}:{cell.Z}")));

    private static string RandomSuffix() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
}
