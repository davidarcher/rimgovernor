using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

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
        var output = Path.Combine(_session.Config.Output, c.Name);
        _ = Directory.CreateDirectory(output);
        var report = Report.Create("speed case " + c.Name, _session.Config.Headless);
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
        _ = _session.Reattach();
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
        _ = await h.CallAsync("play", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = c.Speed, ["ultraSpeedBoost"] = c.TestAcceleration }, cancellationToken).ConfigureAwait(false);
        var resumedAt = DateTimeOffset.UtcNow;
        var lastTick = startTick;
        var reads = 0;

        // Nothing records an OTLP timeline here, so the row's observation and frame accounts come off its own tick
        // reads' reply envelopes: the ungoverned update intervals on the same save and renderer.
        var account = new NativeAccount();
        Exception? waitErr = null;
        try
        {
            await new Wait { Stall = Wait.StallBudget(), Interval = TimeSpan.FromSeconds(2) }.WaitProgressAsync(
                async ct =>
                {
                    var tick = await ReadTickAccountedAsync(h, string.Create(CultureInfo.InvariantCulture, $"tick-{reads}"), account, ct).ConfigureAwait(false);
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
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"tick budget wait (start {startTick}, last {lastTick}, want +{_profile.Ticks}): {waitErr.Message}"), waitErr);
        }

        var after = await ControlAsync(h, identity, "control-after", cancellationToken).ConfigureAwait(false);
        report["control_after"] = after.DeepClone();
        var pawnsReply = await h.WireAsync(
            "pawns-after",
            "observations_list_pawns",
            new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() }, ["filter"] = new JsonObject { ["colonist"] = true } },
            cancellationToken).ConfigureAwait(false);
        ClockFixture.RequireHealthyColonists(pawnsReply);
        var (_, observed) = Harness.Outcome(pawnsReply, "observed");
        var outcome = SpeedMatrix.OutcomeFromControl(c.Name, after) with { HealthyColonists = observed["pawns"].AsSlice().Count };
        report["outcome"] = outcome.ToJson();
        var wallTps = wallSeconds > 0 && lastTick > startTick ? (lastTick - startTick) / wallSeconds : 0.0;
        var metrics = new JsonObject
        {
            ["case"] = c.Name,
            ["speed"] = c.Speed,
            ["test_acceleration"] = c.TestAcceleration,
            ["blind_ticks"] = c.BlindTicks,
            ["governor_off"] = true,
            ["ticks_advanced"] = lastTick - startTick,
            ["wall_seconds"] = wallSeconds,
            ["budget_wall_tps"] = wallTps,
            ["wall_tps"] = wallTps,
            ["paused_ms"] = 0,
            ["running_ms"] = (ulong)(wallSeconds * 1000),
            ["paused_fraction_native"] = 0.0,
            ["native_pause_samples"] = 0,
            ["paused_fraction"] = 0.0,
            ["paused_samples"] = 0,
            ["clock_samples"] = 0,
            ["paused_sampled_seconds"] = 0.0,
            ["steps"] = 0,
            ["reads_per_step"] = 0.0,
            ["stops"] = 0,
            ["budget_stops"] = 0,
            ["reactive_stops"] = 0,
            ["tick_reads"] = reads,
        };
        foreach (var (key, value) in ObservationRow(account.Observation(), account.Frames()))
        {
            metrics[key] = value?.DeepClone();
        }

        report["metrics"] = metrics.DeepClone();
        AppendMetrics(metrics);
    }

    // Reads the loaded identity the stage reload produced.
    private static async Task<JsonObject> ReadIdentityAsync(Harness h, CancellationToken cancellationToken)
    {
        var reply = await h.WireAsync("identity", "lifecycle_read_identity", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var (_, loaded) = Harness.Outcome(reply, "loaded");
        return loaded["context"].AsMap()?["identity"].AsMap() ?? [];
    }

    // ReadTick over the raw reply envelope, so the reply's companion timing block feeds the row's observation and frame
    // accounts. Harness.WireAsync unwraps the ProtoJSON payload and drops the block.
    private static async Task<ulong> ReadTickAccountedAsync(Harness h, string label, NativeAccount account, CancellationToken cancellationToken)
    {
        var envelope = await h.CallAsync(label, "rimgovernor/lifecycle_read_tick", new JsonObject { ["request"] = "{}" }, cancellationToken).ConfigureAwait(false);
        _ = account.Reply(envelope);
        if (envelope["payload"] is not JsonValue value || !value.TryGetValue<string>(out var payload))
        {
            throw new AcceptanceException("lifecycle_read_tick: reply did not preserve the ProtoJSON string envelope");
        }

        JsonObject message;
        try
        {
            message = JsonNode.Parse(payload) as JsonObject ?? throw new AcceptanceException("lifecycle_read_tick: invalid ProtoJSON reply: not an object");
        }
        catch (System.Text.Json.JsonException e)
        {
            throw new AcceptanceException("lifecycle_read_tick: invalid ProtoJSON reply: " + e.Message, e);
        }

        var (_, loaded) = Harness.Outcome(message, "loaded");
        return (ulong)loaded["context"].AsMap()?["tick"].AsNumber()!;
    }
}
