using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lifecycle/headless-soak</c> (Go <c>soak.go</c>): a diagnostic for a game that vanishes or stops answering
/// mid-run. It starts a fresh debug game, then drives the clock with one of several call patterns (<see cref="ModeEnv"/>)
/// while recording a timeline of the game process, its memory and the game tick, so the moment the game connection drops
/// is visible.
/// <para>
/// Adaptation (#3090): the Go case ran its own bridge client and recorded <c>games_status</c>, the bridge tool schemas
/// and a reconnect attempt after a failure. There is no bridge now: the case opens its own session on the game's HTTP
/// channel (<see cref="GameSessions.OpenBridgeSessionAsync"/>), stops the game with <see cref="GameProcess.StopAsync(string, string, CancellationToken)"/>,
/// records the launcher's process record (running, pid, generation) in place of <c>games_status</c>, and drops the tool
/// schema dump, the native-name listing and the reconnect probe, which have no counterpart. The post-failure tick read
/// is kept.
/// </para>
/// </summary>
internal static class LifecycleHeadlessSoak
{
    /// <summary>Overrides the registered soak's call pattern for a diagnostic chase.</summary>
    internal const string ModeEnv = "RIMGOVERNOR_ACCEPT_SOAK_MODE";

    /// <summary>Overrides the registered soak's length for a diagnostic chase.</summary>
    internal const string DurationEnv = "RIMGOVERNOR_ACCEPT_SOAK_DURATION";

    internal static AcceptanceCase Case() => new()
    {
        Name = "lifecycle/headless-soak",
        Tier = CaseTier.Off,
        Scope = "Diagnostic soak: fresh debug game driven by the soak mode (poll by default) while recording game status, process memory and tick.",
        Start = new OwnedStart(),
        Reason = "the diagnostic opens its own bridge session and stops the game it soaked",
        NoKeep = true,
        Budget = TimeSpan.FromMinutes(6),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    /// <summary>The soak's shape; the environment overrides the registered defaults for a diagnostic run.</summary>
    private sealed record SoakConfig(string Mode, string Speed, bool Ultra, TimeSpan Duration, TimeSpan Poll, TimeSpan Chunk, int StepTicks);

    /// <summary>
    /// The registered soak: two minutes of Superfast polling, inside a minute-scale budget. A diagnostic chase sets
    /// <see cref="DurationEnv"/> (e.g. 12m) and <see cref="ModeEnv"/> (poll | idle | pausedpoll | playfor | stepticks).
    /// </summary>
    private static SoakConfig Defaults()
    {
        var mode = Environment.GetEnvironmentVariable(ModeEnv);
        var duration = TimeSpan.FromMinutes(2);
        if (Environment.GetEnvironmentVariable(DurationEnv) is { Length: > 0 } raw)
        {
            duration = ParseDuration(raw) ?? throw new AcceptanceException($"{DurationEnv}: invalid duration \"{raw}\"");
        }

        return new SoakConfig(string.IsNullOrEmpty(mode) ? "poll" : mode, "Superfast", true, duration, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60), 2500);
    }

    /// <summary>Parses a Go-style duration of one number and one unit (ms, s, m, h).</summary>
    private static TimeSpan? ParseDuration(string raw)
    {
        foreach (var (suffix, scale) in new[] { ("ms", 1.0 / 1000), ("h", 3600.0), ("m", 60.0), ("s", 1.0) })
        {
            if (raw.EndsWith(suffix, StringComparison.Ordinal) && double.TryParse(raw[..^suffix.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n >= 0)
            {
                return TimeSpan.FromSeconds(n * scale);
            }
        }

        return null;
    }

    private sealed class Timeline(string path) : IDisposable
    {
        private readonly StreamWriter _writer = new(path, append: false, new UTF8Encoding(false));
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        internal void Row(string kind, JsonObject fields)
        {
            fields["kind"] = kind;
            fields["at"] = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);
            fields["elapsed_s"] = (int)_clock.Elapsed.TotalSeconds;
            var text = fields.ToJsonString();
            _writer.WriteLine(text);
            _writer.Flush();
            Console.Error.WriteLine(text);
        }

        public void Dispose() => _writer.Dispose();
    }

    /// <summary>Samples RimWorld process memory; diagnostic only and never fails the run.</summary>
    private static JsonObject ProcessSample()
    {
        var rows = new JsonArray();
        try
        {
            foreach (var process in Process.GetProcessesByName("RimWorld"))
            {
                using (process)
                {
                    rows.Add($"{process.ProcessName} pid={process.Id} workingSetKb={process.WorkingSet64 / 1024}");
                }
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return new JsonObject { ["error"] = e.Message };
        }

        return new JsonObject { ["count"] = rows.Count, ["rows"] = rows };
    }

    /// <summary>The launcher's record of the game process (the stand-in for the bridge's games_status).</summary>
    private static JsonObject GameStatus(string stateDir, string gameId)
    {
        try
        {
            return GameProcess.Attach(stateDir, gameId) is { } game
                ? new JsonObject { ["running"] = game.Alive, ["pid"] = game.Pid, ["generation"] = game.Generation }
                : new JsonObject { ["running"] = false };
        }
        catch (AcceptanceException e)
        {
            return new JsonObject { ["error"] = e.Message };
        }
    }

    /// <summary>Owns the process (an Owned start): it opens its own session, starts the game, drives the clock and stops the game at the end.</summary>
    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var naCfg = s.Config;
        var report = s.Report;
        var cfg = Defaults();
        report["mode"] = cfg.Mode;
        report["duration_ms"] = (long)cfg.Duration.TotalMilliseconds;
        var output = naCfg.Output;
        var gameId = naCfg.GameId.Length > 0 ? naCfg.GameId : HeadlessProfile.TrialGameId;
        var stateDir = Path.GetFullPath(naCfg.Configuration);
        INativeTransport transport;
        try
        {
            transport = await GameSessions.OpenBridgeSessionAsync(naCfg.Configuration, gameId, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"open bridge session: {e.Message}", e);
        }

        var h = new Harness(transport, output);
        try
        {
            await SoakAsync(h, cfg, output, stateDir, gameId, report, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                report["stop"] = await GameProcess.StopAsync(stateDir, gameId, stop.Token).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                report["stop_error"] = e.Message;
            }
            catch (OperationCanceledException e)
            {
                report["stop_error"] = e.Message;
            }

            await h.CloseAsync().ConfigureAwait(false);
        }
    }

    private static async Task SoakAsync(Harness h, SoakConfig cfg, string output, string stateDir, string gameId, Report report, CancellationToken cancellationToken)
    {
        using var tl = new Timeline(Path.Combine(output, "timeline.jsonl"));
        tl.Row("game-status", new JsonObject { ["status"] = GameStatus(stateDir, gameId), ["proc"] = ProcessSample() });
        _ = await DebugGame.StartDebugGameAsync(h, ProfileStartCache.Current, null, QuietMode.IfAvailable, cancellationToken).ConfigureAwait(false);
        _ = await h.CallAsync("pause", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);
        tl.Row("game-ready", new JsonObject { ["status"] = GameStatus(stateDir, gameId), ["proc"] = ProcessSample() });

        async Task<double> Tick(string label)
        {
            var reply = await h.WireAsync(label, "lifecycle_read_tick", new JsonObject(), cancellationToken).ConfigureAwait(false);
            var (_, loaded) = Harness.Outcome(reply, "loaded");
            return loaded["context"].Member("tick").AsNumber();
        }

        void Sample(string label, Exception? nativeError, JsonObject? extra)
        {
            var fields = new JsonObject { ["label"] = label, ["status"] = GameStatus(stateDir, gameId), ["proc"] = ProcessSample() };
            if (nativeError is not null)
            {
                fields["native_error"] = nativeError.Message;
            }

            foreach (var (key, value) in extra ?? [])
            {
                fields[key] = value?.DeepClone();
            }

            tl.Row("sample", fields);
        }

        // onFailure gathers everything observable once the game has stopped answering.
        async Task<AcceptanceException> OnFailure(string label, Exception cause)
        {
            tl.Row("failure", new JsonObject { ["label"] = label, ["error"] = cause.Message });
            var post = new JsonObject { ["status"] = GameStatus(stateDir, gameId), ["proc"] = ProcessSample() };
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            post["status_after_wait"] = GameStatus(stateDir, gameId);
            try
            {
                _ = await Tick("post-failure-tick").ConfigureAwait(false);
                post["post_failure_tick_ok"] = true;
            }
            catch (AcceptanceException e)
            {
                post["post_failure_tick_error"] = e.Message;
            }

            tl.Row("post-failure", post);
            report["failure"] = cause.Message;
            report["failure_label"] = label;
            return new AcceptanceException($"{label}: {cause.Message}", cause);
        }

        var start = await Tick("tick-start").ConfigureAwait(false);
        report["tick_start"] = start;
        var deadline = DateTimeOffset.UtcNow + cfg.Duration;
        var last = start;
        var i = 0;
        switch (cfg.Mode)
        {
            case "poll" or "idle" or "pausedpoll":
                if (cfg.Mode != "pausedpoll")
                {
                    _ = await h.CallAsync("resume", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = cfg.Speed, ["ultraSpeedBoost"] = cfg.Ultra }, cancellationToken).ConfigureAwait(false);
                }

                while (DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(cfg.Poll, cancellationToken).ConfigureAwait(false);
                    i++;
                    var label = string.Create(CultureInfo.InvariantCulture, $"poll-{i:D3}");
                    if (cfg.Mode == "idle")
                    {
                        Sample(label, null, null);
                        continue;
                    }

                    try
                    {
                        last = await Tick(label).ConfigureAwait(false);
                    }
                    catch (AcceptanceException e)
                    {
                        Sample(label, e, null);
                        throw await OnFailure(label, e).ConfigureAwait(false);
                    }

                    Sample(label, null, new JsonObject { ["tick"] = last });
                }

                if (cfg.Mode == "idle")
                {
                    try
                    {
                        last = await Tick("idle-final-tick").ConfigureAwait(false);
                    }
                    catch (AcceptanceException e)
                    {
                        throw await OnFailure("idle-final-tick", e).ConfigureAwait(false);
                    }
                }

                break;
            case "playfor":
                while (DateTimeOffset.UtcNow < deadline)
                {
                    i++;
                    var label = string.Create(CultureInfo.InvariantCulture, $"playfor-{i:D3}");
                    JsonObject res;
                    try
                    {
                        res = await h.CallAsync(label, "rimgovernor/play_for", new JsonObject { ["speed"] = cfg.Speed, ["durationMs"] = (long)cfg.Chunk.TotalMilliseconds }, cancellationToken).ConfigureAwait(false);
                    }
                    catch (AcceptanceException e)
                    {
                        Sample(label, e, null);
                        throw await OnFailure(label, e).ConfigureAwait(false);
                    }

                    try
                    {
                        last = await Tick(label + "-tick").ConfigureAwait(false);
                    }
                    catch (AcceptanceException e)
                    {
                        Sample(label, e, null);
                        throw await OnFailure(label + "-tick", e).ConfigureAwait(false);
                    }

                    Sample(label, null, new JsonObject { ["tick"] = last, ["play_for_keys"] = NewColonyKit.Strings(res.Select(pair => pair.Key)) });
                }

                break;
            case "stepticks":
                while (DateTimeOffset.UtcNow < deadline)
                {
                    i++;
                    var label = string.Create(CultureInfo.InvariantCulture, $"step-{i:D3}");
                    try
                    {
                        _ = await h.CallAsync(label, "rimgovernor/step_game_ticks", new JsonObject { ["ticks"] = cfg.StepTicks }, cancellationToken).ConfigureAwait(false);
                    }
                    catch (AcceptanceException e)
                    {
                        Sample(label, e, null);
                        throw await OnFailure(label, e).ConfigureAwait(false);
                    }

                    try
                    {
                        last = await Tick(label + "-tick").ConfigureAwait(false);
                    }
                    catch (AcceptanceException e)
                    {
                        Sample(label, e, null);
                        throw await OnFailure(label + "-tick", e).ConfigureAwait(false);
                    }

                    Sample(label, null, new JsonObject { ["tick"] = last });
                }

                break;
            default:
                throw new AcceptanceException($"unknown mode \"{cfg.Mode}\"");
        }

        try
        {
            _ = await h.CallAsync("re-pause", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw await OnFailure("re-pause", e).ConfigureAwait(false);
        }

        try
        {
            var logs = await h.CallAsync("logs", "rimgovernor/list_logs", new JsonObject(), cancellationToken).ConfigureAwait(false);
            report["log_keys"] = NewColonyKit.Strings(logs.Select(pair => pair.Key));
        }
        catch (AcceptanceException tolerated)
        {
            // The log listing is optional evidence.
            Tolerated.Note("log listing", tolerated);
        }

        report["tick_end"] = last;
        report["ticks_advanced"] = last - start;
        report["samples"] = i;
    }
}
