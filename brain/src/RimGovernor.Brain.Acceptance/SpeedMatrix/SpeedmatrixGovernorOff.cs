using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

internal sealed partial class SpeedmatrixRun
{
    // The governor-off row: the same stage reloaded and frozen exactly as a governed row, then played natively at the
    // row's speed (the test acceleration where the row asks for it) with no controller attached, until the tick has
    // advanced by the budget. The harness's own tick reads pace the wait; there is nothing else to wait on. Its metrics
    // row carries wall TPS and the tick budget under the same keys as a governed row, zeros for the governor's counters,
    // and governor_off so a reader tells the rows apart. Nothing submits plans, so the row records its native counters
    // but takes no part in the outcome comparison; it still requires healthy colonists and a fresh stage.
    private async Task RunGovernorOffAsync(SpeedCase c, CancellationToken cancellationToken)
    {
        var output = Path.Combine(_config.Output, c.Name);
        _ = Directory.CreateDirectory(output);
        var report = Report.Create("speed case " + c.Name, _config.Headless);
        report["case"] = c.ToJson();
        try
        {
            await RunGovernorOffCoreAsync(c, output, report, cancellationToken).ConfigureAwait(false);
            report["passed"] = true;
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

    private async Task RunGovernorOffCoreAsync(SpeedCase c, string output, Report report, CancellationToken cancellationToken)
    {
        var h = HarnessFor(output);
        _ = await h.CallAsync(
            "load-stage",
            "rimgovernor/load_game_ready",
            new JsonObject { ["saveName"] = _profile.Save, ["readiness"] = "visual", ["timeoutMs"] = 90000, ["ignoreModCompatibility"] = false },
            cancellationToken).ConfigureAwait(false);
        _ = await h.CallAsync("pause", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);
        var identity = await ReadIdentityAsync(h, cancellationToken).ConfigureAwait(false);
        report["frozen_needs"] = (await Needs.FreezeNeedsAsync(h, null, [], cancellationToken).ConfigureAwait(false)).DeepClone();
        var before = await ControlAsync(h, identity, "control-before", cancellationToken).ConfigureAwait(false);
        report["control_before"] = before.DeepClone();
        if (before["storedUnits"].AsNumber() != 0 || before["wallsBuilt"].AsNumber() != 0 || before["wallBlueprints"].AsNumber() != 0)
        {
            throw new AcceptanceException("stage is not fresh after reload: " + before.ToJson());
        }

        var startTick = (ulong)before["tick"].AsNumber();
        var accountingStart = await ClockAccounting.ReadAsync(h, "clock-accounting-start", cancellationToken).ConfigureAwait(false);
        _ = await h.CallAsync("play", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = c.Speed, ["ultraSpeedBoost"] = c.TestAcceleration }, cancellationToken).ConfigureAwait(false);
        var resumedAt = DateTimeOffset.UtcNow;
        var lastTick = startTick;
        var reads = 0;

        Exception? waitErr = null;
        try
        {
            await new Wait { Stall = Wait.StallBudget(), Interval = TimeSpan.FromSeconds(2) }.WaitProgressAsync(
                async ct =>
                {
                    var tick = await ReadTickAsync(h, string.Create(CultureInfo.InvariantCulture, $"tick-{reads}"), ct).ConfigureAwait(false);
                    reads++;
                    lastTick = tick;
                    return (Wait.Signature(lastTick), lastTick >= startTick + _profile.Ticks);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (WaitError e)
        {
            waitErr = e;
        }

        var wallSeconds = (DateTimeOffset.UtcNow - resumedAt).TotalSeconds;
        try
        {
            _ = await h.CallAsync("pause-after", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e) when (waitErr is null)
        {
            waitErr = e;
        }

        report["wait"] = new JsonObject { ["start_tick"] = startTick, ["last_tick"] = lastTick, ["wall_seconds"] = wallSeconds, ["tick_reads"] = reads };
        if (waitErr is not null)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"tick target wait (start {startTick}, last {lastTick}, want +{_profile.Ticks}): {waitErr.Message}"), waitErr);
        }

        // No supervisor runs on this row, so native's pause account holds whatever its own transitions recorded (none).
        var clock = new ClockSample();
        clock.Account(accountingStart, await ClockAccounting.ReadAsync(h, "clock-accounting-end", cancellationToken).ConfigureAwait(false));
        var after = await ControlAsync(h, identity, "control-after", cancellationToken).ConfigureAwait(false);
        report["control_after"] = after.DeepClone();
        var healthy = await ClockFixture.RequireHealthyColonistsAsync(h, "pawns-after", identity, cancellationToken).ConfigureAwait(false);
        var outcome = SpeedMatrix.OutcomeFromControl(c.Name, after) with { HealthyColonists = healthy };
        report["outcome"] = outcome.ToJson();
        var wallTps = wallSeconds > 0 && lastTick > startTick ? (lastTick - startTick) / wallSeconds : 0.0;
        var metrics = new JsonObject
        {
            ["case"] = c.Name,
            ["speed"] = c.Speed,
            ["test_acceleration"] = c.TestAcceleration,
            ["governor_off"] = true,
            ["ticks_advanced"] = lastTick - startTick,
            ["wall_seconds"] = wallSeconds,
            ["wall_tps"] = wallTps,
            ["effective_speed"] = SpeedMatrix.EffectiveSpeed(wallTps, c.Speed),
            ["paused_ms"] = clock.NativePausedMs,
            ["running_ms"] = clock.NativeRunningMs,
            ["paused_fraction_native"] = clock.PausedFractionNative(),
            ["native_pause_samples"] = clock.NativePauseSamples,
            ["paused_fraction"] = 0.0,
            ["paused_samples"] = 0,
            ["clock_samples"] = 0,
            ["paused_sampled_seconds"] = 0.0,
            ["steps"] = 0,
            ["reads_per_step"] = 0.0,
            ["holds"] = 0,
            ["resume_latency_ms_mean"] = 0.0,
            ["tick_reads"] = reads,
        };
        // Nothing records an OTLP timeline here, so the row's observation and frame accounts are empty (unknown, not zero).
        foreach (var (key, value) in ObservationRow(new ObservationSample(), new FrameSample()))
        {
            metrics[key] = value?.DeepClone();
        }

        report["metrics"] = metrics.DeepClone();
        AppendMetrics(metrics);
    }

    // Reads the loaded identity the stage reload produced.
    private static Task<JsonObject> ReadIdentityAsync(Harness h, CancellationToken cancellationToken) =>
        Session.ReadIdentityAsync(h, "identity", cancellationToken);

    // The loaded tick.
    private static async Task<ulong> ReadTickAsync(Harness h, string label, CancellationToken cancellationToken) =>
        (ulong)((await h.LoadedTickAsync(label, cancellationToken).ConfigureAwait(false)).Context?.Tick
            ?? throw new AcceptanceException($"{label}: lifecycle_read_tick loaded without a context"));
}
