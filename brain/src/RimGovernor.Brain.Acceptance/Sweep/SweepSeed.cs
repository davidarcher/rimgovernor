using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Simulation.CombatMetrics;
using RimGovernor.Brain.Telemetry;
using RimGovernor.Brain.Telemetry.Otlp;
using Fx = RimGovernor.Fixtures.Contracts;
using SimMetrics = RimGovernor.Brain.Simulation.CombatMetrics.Metrics;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// One seed of a combat fixture sweep, in this process on a worker root: open the lab, stage the fixture through the
/// stage spec, reseed the RNG with the seed, serve the routine defense planner for up to <see cref="Ticks"/> game ticks
/// and score the fight. Every seed stages afresh; the staging is deterministic (the fixture's own seed), so the seeds
/// differ only in the draws after the reseed. The bundle (reads, shots, combat rows, metrics, result.json) lands in the
/// seed's directory.
/// </summary>
internal static class SweepSeed
{
    /// <summary>A fixture run's game-tick budget.</summary>
    internal const int Ticks = 10000;

    // How far short of the budget the service is stopped: the poll interval and the stop plus the reattach, at an uncapped clock.
    private const int StopMargin = 2500;

    // How long a still served clock means the run is over. A cold service spends its first seconds in native calls that
    // the clock waits behind (#2901); 90 s is about four times the quiet-box first round (#2843).
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(90);

    // lab-open's metrics are the smoke scaffold for the clock, orders and recorder (#854), not a tactics benchmark.
    private const string SmokeOnly = " Smoke only: lab-open is an unstructured 3v3 whose outcome is not a tactics signal.";

    /// <summary>The fixture ops a metrics seed calls.</summary>
    internal static IReadOnlyList<string> Ops => [DebugGame.LabStartTool, FixtureStage.Tool, CombatFixtures.LabTool, CombatServe.PauseModeTool];

    /// <summary>
    /// Runs one seed of <paramref name="fixture"/> and returns its row: the staging digest and reseed draws it read, and
    /// the metrics when it finished. A failed seed returns what it did read beside the error.
    /// </summary>
    internal static async Task<(SeedRow Row, AcceptanceException? Error)> RunAsync(
        SweepOptions opts,
        string root,
        string fixture,
        int worker,
        int seed,
        string output,
        CancellationToken cancellationToken)
    {
        var probe = CombatFixtures.Build(fixture, 0, 0);
        var scope = string.Create(
            CultureInfo.InvariantCulture,
            $"Combat metrics (#855, #869): stage {fixture}, reseed with {seed}, serve the routine defense planner on it for up to {Ticks} game ticks, and record the reads before and after to {CombatLabBundle.ReadsFile} and the aggregate to {CombatLabBundle.MetricsFile}, with the shot tap to {CombatLabShots.ShotsFile}.");
        if (fixture == "lab-open")
        {
            scope += SmokeOnly;
        }

        var row = new SeedRow { Seed = seed, Worker = worker + 1 };
        var error = await WithGameAsync(
            opts,
            root,
            output,
            scope,
            new LabStart { Colonists = probe.Colonists },
            CombatServe.MetricsFamilies,
            async (session, cfg, report, ct) =>
            {
                var m = await BodyAsync(session, cfg, report, opts, fixture, seed, r => row = r(row), ct).ConfigureAwait(false);
                row = row with { Metrics = m };

                // The seed keeps its metrics; the flag fails it so the sweep names it (#3551).
                if (CombatLabBundle.UnarmedStops(m, Path.Combine(cfg.Output, CombatLabBundle.CombatRowsFile)) is { } unarmed)
                {
                    throw new AcceptanceException(unarmed);
                }
            },
            cancellationToken).ConfigureAwait(false);
        return (row, error);
    }

    /// <summary>
    /// Opens the worker root's game (a kept process is reused) on <paramref name="start"/> and runs
    /// <paramref name="body"/>, bounded by the sweep's per-seed timeout. The report, with passed or the error, is written
    /// to <paramref name="output"/>/result.json; the error is returned rather than thrown.
    /// </summary>
    internal static async Task<AcceptanceException?> WithGameAsync(
        SweepOptions opts,
        string root,
        string output,
        string scope,
        LabStart start,
        IReadOnlyList<string> families,
        Func<Session, Config, Report, CancellationToken, Task> body,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(output);
        var report = Report.Create(scope, headless: true);
        var cfg = new Config { Root = root, Output = output, Headless = true, GameId = opts.GameId, QuietWorld = false };
        cfg.LaunchEnv[HeadlessProfile.StateVariable] = Path.Combine(output, Checkpoints.CheckpointStoreFile);
        if (families.Count > 0)
        {
            cfg.LaunchEnv["RIMGOVERNOR_ROUTINE_FAMILIES"] = string.Join(",", families);
        }

        // The brain reads the tactic override when the game starts: a kept process launched with another value relaunches.
        cfg.LaunchEnv[CombatTuningRules.CombatTuningEnv] = opts.TuningEnv;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(opts.SeedTimeout);
        AcceptanceException? error = null;
        try
        {
            var session = await Session.OpenAsync(cfg, SessionHost.Default, report, start, QuietMode.Required, null, timeout.Token).ConfigureAwait(false);
            try
            {
                await body(session, cfg, report, timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                session.Close();
            }
        }
        catch (Exception e) when (e is AcceptanceException or IOException or UnauthorizedAccessException or JsonException
            || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            error = e as AcceptanceException ?? new AcceptanceException(
                e is OperationCanceledException ? $"seed timed out after {GoDuration.Format(opts.SeedTimeout)} (-timeout)" : e.Message,
                e);
        }

        report["passed"] = error is null;
        if (error is not null)
        {
            report["error"] = error.Message;
        }

        report.Write(output);
        return error;
    }

    // Stages the fixture, reseeds, serves the planner until the tick budget is nearly spent, reads the lab back and writes
    // the reads, shots, combat rows and metrics to the bundle.
    private static async Task<SimMetrics> BodyAsync(
        Session session,
        Config cfg,
        Report report,
        SweepOptions opts,
        string name,
        int seed,
        Action<Func<SeedRow, SeedRow>> update,
        CancellationToken cancellationToken)
    {
        var dir = cfg.Output;
        var h = session.Harness;
        var probe = CombatFixtures.Build(name, 0, 0);
        var staged = probe.Arrival.Length > 0
            ? await StagePodsAsync(h, session.Identity, name, cancellationToken).ConfigureAwait(false)
            : await CombatStage.StageAsync(h, name, cancellationToken).ConfigureAwait(false);
        report["staged_digest"] = staged.Digest;
        update(r => r with { StagedDigest = staged.Digest });
        var first = await CombatServe.RawReadAsync(h, "read", cancellationToken).ConfigureAwait(false);
        CombatLabBundle.Stamp(staged, first);
        var start = CombatServe.Typed<Fx.LabFightRead>(CombatFixtures.LabTool, first).Tick;
        if (staged.Fixture.Layout is { } layout)
        {
            report["layout"] = await CombatLabLayout.StoreAsync(h, session.Identity, layout, cancellationToken).ConfigureAwait(false);
        }

        // A MajorThreat letter mid-fight (a berserk colonist) pauses the game as the player would; the authority flip
        // then drops the fight plan (lab-open at tick 1057). The metrics measure the fight.
        await CombatServe.NeverPauseAsync(h, cancellationToken).ConfigureAwait(false);

        // The seed reseeds the RNG on the paused, staged lab, right before the planner is served: nothing draws between
        // (#2849).
        var draws = await CombatStage.ReseedAsync(h, seed, cancellationToken).ConfigureAwait(false);
        report["run_seed"] = seed;
        report["run_seed_draws"] = new JsonArray([.. draws.Select(d => (JsonNode?)JsonValue.Create(d))]);
        update(r => r with { RunSeedDraws = [.. draws] });

        var combatRows = Path.Combine(dir, CombatLabBundle.CombatRowsFile);
        var clock = new ServedClock();
        var spec = new ServeSpec
        {
            Families = CombatServe.MetricsFamilies,
            Prefix = string.Create(CultureInfo.InvariantCulture, $"combatlab-s{seed}"),
        };
        // The game's ring outlives a seed (a kept process, an appended file): this seed owns the rows from here on.
        var since = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        var service = await ServiceProcess.ServeAsync(cfg, h, session.Identity, spec, report, cancellationToken).ConfigureAwait(false);
        try
        {
            _ = await service.AcquireAsync(cancellationToken).ConfigureAwait(false);
            service.KeepAuthority(cancellationToken);
            await ServeUntilAsync(service, h, report, service.LogPath, start + Ticks - StopMargin, clock, cancellationToken).ConfigureAwait(false);

            // A quiet lab runs thousands of ticks a second: stop the clock first, then the service.
            await CombatServe.PauseAfterAsync(h, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = await service.StopAsync().ConfigureAwait(false);
            report["servedTick"] = clock.Tick;
            try
            {
                // The in-mod brain outlives the handle: park it so the reads after see a paused game.
                report["serve_park"] = await service.ParkAsync(service.Label, service.Identity!, service.Token, CancellationToken.None).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                report["serve_park_error"] = e.Message;
            }
        }

        await CombatServe.PauseAfterAsync(h, cancellationToken).ConfigureAwait(false);
        var last = await CombatServe.RawReadAsync(h, "read", cancellationToken).ConfigureAwait(false);
        await WriteReadsAsync(dir, [first, last], cancellationToken).ConfigureAwait(false);
        var shots = await CombatServe.RawReadAsync(h, "shots", cancellationToken).ConfigureAwait(false);
        await CombatLabShots.WriteShotsAsync(dir, CombatServe.Typed<Fx.LabShotsRead>(CombatFixtures.LabTool, shots), cancellationToken).ConfigureAwait(false);
        await WriteCombatRowsAsync(service.LogPath, combatRows, since, cancellationToken).ConfigureAwait(false);
        var aggregate = await CombatLabBundle.AggregateBundleAsync(dir, name, cancellationToken).ConfigureAwait(false);
        var text = CombatLabBundle.MetricsText(aggregate);
        await File.WriteAllTextAsync(Path.Combine(dir, CombatLabBundle.MetricsFile), text, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        report["metrics"] = CombatLabBundle.MetricsJson(aggregate);
        var response = ResponseEvidence.Create(name, CombatServe.MetricsSpeed, start);
        response.ReadLogs(combatRows);
        foreach (var row in CombatServe.Typed<Fx.LabFightRead>(CombatFixtures.LabTool, last).Damage)
        {
            if (row.InstigatorSide == CombatFixtures.Colonist && row.VictimSide == CombatFixtures.Hostile && row.Dealt > 0)
            {
                response.ObserveEffect(row.Tick, "colonist_damaged_hostile");
            }
        }

        report["response"] = response.ToJson();
        if (aggregate.Ticks > Ticks)
        {
            throw new AcceptanceException($"{name} ran {SweepText.Tag(aggregate.Ticks)} ticks from {SweepText.Tag(start)}, over the {SweepText.Tag(Ticks)}-tick budget");
        }

        // The row carries the metrics as the sweep file encodes them.
        return JsonSerializer.Deserialize(text, SweepJsonContext.Default.SimMetrics)
            ?? throw new AcceptanceException($"{CombatLabBundle.MetricsFile}: empty");
    }

    // Stages a drop-pod fixture and checks its arrival row is in the frame. The native records combat events once a
    // frame capture has hooked them (a served game's stream is open long before a raid), so one frame is captured before
    // staging. The served run starts with the raiders still in their pods: the pending arrival is the threat the fight
    // forms on.
    private static async Task<StagedFixture> StagePodsAsync(Harness h, JsonObject identity, string name, CancellationToken cancellationToken)
    {
        var frames = await CombatFrames.OpenAsync(h, identity, cancellationToken).ConfigureAwait(false);
        _ = await frames.NextAsync(0, cancellationToken).ConfigureAwait(false);
        var staged = await CombatStage.StageAsync(h, name, cancellationToken).ConfigureAwait(false);
        var tick = (await CombatStage.TickAsync(h, 1, cancellationToken).ConfigureAwait(false)).Tick;
        var state = await frames.NextAsync(tick, cancellationToken).ConfigureAwait(false);
        foreach (var row in state.Events)
        {
            if (row.Kind == RimGovernor.Brain.GameFacts.Mirror.CombatLogKind.HostileArrived && row.RaidStrategy == "pods" && row.Pods?.OpenTick > tick)
            {
                return staged;
            }
        }

        throw new AcceptanceException($"{name}: no pending drop-pod arrival row in {SweepText.Tag(state.Events.Count)} frame events");
    }

    // The last tick the served window saw, kept even when the wait fails.
    private sealed class ServedClock
    {
        internal int Tick { get; set; } = -1;
    }

    // Watches the served game until its tick reaches `until`, until the service records the end of the combat (a
    // combat_summary row at the log; the colony window after it would otherwise run the quiet lab to the budget), or
    // until the served clock has sat still for Idle, which ends the run without failing it.
    private static async Task ServeUntilAsync(ServiceProcess service, Harness h, Report report, string logPath, int until, ServedClock clock, CancellationToken cancellationToken)
    {
        var tail = LogTail.Create(logPath);
        var ended = false;
        var wait = new Wait { Stall = Idle, Interval = TimeSpan.FromMilliseconds(50), Terminal = service.Exited };
        try
        {
            await wait.WaitProgressAsync(
                async ct =>
                {
                    clock.Tick = await CombatServe.ServedTickAsync(h, clock.Tick, ct).ConfigureAwait(false);
                    ended = ended || tail.Next().Any(row => row.Kind == TelemetryKinds.CombatSummary.Name);
                    return (SweepText.Tag(clock.Tick), ended || clock.Tick >= until);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (WaitError e) when (e.Outcome == WaitOutcome.Stalled && clock.Tick >= 0)
        {
            report["served_idle"] = e.Message;
        }
    }

    // Copies the combat rows ScanLogs reads from the rotated segments (oldest first) and the live file of the service's
    // OTLP log ring into the bundle, one OTLP line each (the bundle is itself a log ring that ScanLogs reads back).
    private static async Task WriteCombatRowsAsync(string from, string to, double since, CancellationToken cancellationToken)
    {
        var kept = new List<byte>();
        OtlpRing.ReadLines(
            from,
            line =>
            {
                var span = line.Span;
                if (LogTail.ParseLogRow(span) is { } row && CombatLabBundle.CombatRow(row) && WallTime(span) >= since)
                {
                    kept.AddRange(span.ToArray());
                    kept.Add(0x0a);
                }
            });
        if (kept.Count == 0)
        {
            return;
        }

        await File.WriteAllBytesAsync(to, [.. kept], cancellationToken).ConfigureAwait(false);
    }

    // The Unix time in seconds of one logs.jsonl line (0 for a line that is no log record).
    private static double WallTime(ReadOnlySpan<byte> line) =>
        OtlpReader.DecodeLog(line.ToArray()) is { } log && OtlpTimeline.FromLog(log) is { } record ? record.WallTime : 0;

    private static async Task WriteReadsAsync(string dir, IReadOnlyList<JsonObject> reads, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        foreach (var read in reads)
        {
            _ = text.Append(read.ToJsonString()).Append('\n');
        }

        await File.WriteAllTextAsync(Path.Combine(dir, CombatLabBundle.ReadsFile), text.ToString(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }
}
