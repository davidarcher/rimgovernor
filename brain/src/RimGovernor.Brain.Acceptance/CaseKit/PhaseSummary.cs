using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Telemetry;
using RimGovernor.Brain.Telemetry.Otlp;
using RimGovernor.Contracts.Wire.Clock;

namespace RimGovernor.Brain.Acceptance;

/// <summary>The nearest-rank quantiles of one phase's per-hop samples (Go <c>bridge.Quantiles</c>); Samples 0 means unknown, not zero.</summary>
public static class PhaseQuantiles
{
    /// <summary>The report row of a quantile set (json tags samples, p50_ms, p95_ms, p99_ms, max_ms, sum_ms).</summary>
    public static JsonObject ToJson(this DispatchQuantiles q)
    {
        ArgumentNullException.ThrowIfNull(q);
        return new JsonObject { ["samples"] = q.Samples, ["p50_ms"] = q.P50, ["p95_ms"] = q.P95, ["p99_ms"] = q.P99, ["max_ms"] = q.Max, ["sum_ms"] = q.Sum };
    }

    /// <summary>Reduces one phase's samples; sorts in place.</summary>
    internal static DispatchQuantiles Of(List<double> samples)
    {
        if (samples.Count == 0)
        {
            return new DispatchQuantiles();
        }

        samples.Sort();
        return new DispatchQuantiles
        {
            Samples = (ulong)samples.Count,
            Max = samples[^1],
            Sum = samples.Sum(),
            P50 = NearestRank(samples, 0.50),
            P95 = NearestRank(samples, 0.95),
            P99 = NearestRank(samples, 0.99),
        };
    }

    private static double NearestRank(List<double> sorted, double q)
    {
        var rank = Math.Clamp((int)Math.Ceiling(q * sorted.Count), 1, sorted.Count);
        return sorted[rank - 1];
    }
}

/// <summary>One requested bundle section's cost across the hops that read it (Go <c>bridge.SectionPhases</c>).</summary>
public sealed class SectionPhases
{
    public string Section { get; init; } = "";

    public ulong Hops { get; set; }

    public double Ms { get; set; }

    public double MaxMs { get; set; }

    public ulong Rows { get; set; }

    public ulong Candidates { get; set; }
}

/// <summary>The threat classifier's counters summed over the hops that ran it (Go <c>bridge.ThreatScan</c>).</summary>
public sealed class ThreatScan
{
    public ulong Hops { get; set; }

    public ulong Examined { get; set; }

    public ulong Candidates { get; set; }

    public ulong Projections { get; set; }

    public ulong ProximityChecks { get; set; }
}

/// <summary>The companion's per-hop observation account over one recording (Go <c>bridge.ObservationSample</c>); Hops 0 means no reply carried it.</summary>
public sealed class ObservationSample
{
    public ulong Hops { get; set; }

    public ulong FormatPasses { get; set; }

    public ulong PayloadBytes { get; set; }

    public double CaptureMs { get; set; }

    public double FormatMs { get; set; }

    public DispatchQuantiles Capture { get; set; } = new();

    public DispatchQuantiles Format { get; set; } = new();

    public DispatchQuantiles Queue { get; set; } = new();

    public DispatchQuantiles Execute { get; set; } = new();

    public List<SectionPhases> Sections { get; } = [];

    public SortedDictionary<string, ulong> Outcomes { get; } = new(StringComparer.Ordinal);

    public ulong EncodeHops { get; set; }

    public ulong EncodeFormatPasses { get; set; }

    public double EncodeFormatMs { get; set; }

    public DispatchQuantiles EncodeQueue { get; set; } = new();

    public DispatchQuantiles Encode { get; set; } = new();

    public ThreatScan? Threats { get; set; }

    /// <summary>The sample as its report row (the Go json tags).</summary>
    public JsonObject ToJson()
    {
        var row = new JsonObject
        {
            ["hops"] = Hops,
            ["format_passes"] = FormatPasses,
            ["payload_bytes"] = PayloadBytes,
            ["capture_ms"] = CaptureMs,
            ["format_ms"] = FormatMs,
            ["capture"] = Capture.ToJson(),
            ["format"] = Format.ToJson(),
            ["queue"] = Queue.ToJson(),
            ["execute"] = Execute.ToJson(),
        };
        if (Sections.Count > 0)
        {
            var sections = new JsonArray();
            foreach (var section in Sections)
            {
                var item = new JsonObject { ["section"] = section.Section, ["hops"] = section.Hops, ["ms"] = section.Ms, ["max_ms"] = section.MaxMs, ["rows"] = section.Rows };
                if (section.Candidates > 0)
                {
                    item["candidates"] = section.Candidates;
                }

                sections.Add(item);
            }

            row["sections"] = sections;
        }

        if (Outcomes.Count > 0)
        {
            var outcomes = new JsonObject();
            foreach (var (name, count) in Outcomes)
            {
                outcomes[name] = count;
            }

            row["outcomes"] = outcomes;
        }

        if (EncodeHops > 0)
        {
            row["encode_hops"] = EncodeHops;
        }

        if (EncodeFormatPasses > 0)
        {
            row["encode_format_passes"] = EncodeFormatPasses;
        }

        if (EncodeFormatMs != 0)
        {
            row["encode_format_ms"] = EncodeFormatMs;
        }

        row["encode_queue"] = EncodeQueue.ToJson();
        row["encode"] = Encode.ToJson();
        if (Threats is { } t)
        {
            row["threat_scan"] = new JsonObject
            {
                ["hops"] = t.Hops,
                ["examined"] = t.Examined,
                ["candidates"] = t.Candidates,
                ["projections"] = t.Projections,
                ["proximity_checks"] = t.ProximityChecks,
            };
        }

        return row;
    }
}

/// <summary>The companion's frame recorder as one recording sees it (Go <c>bridge.FrameSample</c>).</summary>
public sealed class FrameSample
{
    public bool Hooked { get; set; }

    public ulong Samples { get; set; }

    public ulong Updates { get; set; }

    public double ElapsedMs { get; set; }

    public double MaxIntervalMs { get; set; }

    public double ObservationMs { get; set; }

    public ulong Observations { get; set; }

    public ulong Cancelled { get; set; }

    public double RecorderMs { get; set; }

    public DispatchQuantiles Intervals { get; set; } = new();

    public DispatchQuantiles Observed { get; set; } = new();

    public List<(double ThresholdMs, ulong Count)> Slow { get; } = [];

    public List<JsonObject> Worst { get; } = [];

    /// <summary>Updates over the measured interval wall, or 0 without one.</summary>
    public double UpdatesPerSecond() => ElapsedMs <= 0 ? 0 : Updates * 1000 / ElapsedMs;

    /// <summary>The share of the measured update wall that ran observation work on the main thread, or 0 without a measured interval.</summary>
    public double ObservationShare() => ElapsedMs <= 0 ? 0 : ObservationMs / ElapsedMs;

    /// <summary>The sample as its report row (the Go json tags).</summary>
    public JsonObject ToJson()
    {
        var row = new JsonObject
        {
            ["hooked"] = Hooked,
            ["samples"] = Samples,
            ["updates"] = Updates,
            ["elapsed_ms"] = ElapsedMs,
            ["max_interval_ms"] = MaxIntervalMs,
            ["observation_ms"] = ObservationMs,
            ["observations"] = Observations,
            ["cancelled_hops"] = Cancelled,
            ["recorder_ms"] = RecorderMs,
            ["intervals"] = Intervals.ToJson(),
            ["observed_intervals"] = Observed.ToJson(),
        };
        if (Slow.Count > 0)
        {
            var slow = new JsonArray();
            foreach (var (threshold, count) in Slow)
            {
                slow.Add(new JsonObject { ["threshold_ms"] = threshold, ["count"] = count });
            }

            row["slow"] = slow;
        }

        if (Worst.Count > 0)
        {
            var worst = new JsonArray();
            foreach (var frame in Worst)
            {
                worst.Add(frame.DeepClone());
            }

            row["worst"] = worst;
        }

        return row;
    }
}

/// <summary>The clock_step rows a recording covers (Go <c>bridge.StepSample</c>).</summary>
public sealed class StepSample
{
    public ulong Steps { get; set; }

    public ulong Reads { get; set; }

    public ulong MaxReads { get; set; }

    public SortedDictionary<string, ulong> Reasons { get; } = new(StringComparer.Ordinal);

    /// <summary>Steps a hold woke (the step row carries the hold's stop flag).</summary>
    public ulong HoldWoken { get; set; }

    /// <summary>Runs the brain made carrying coupled orders, and the orders they carried (the clock_run rows).</summary>
    public ulong CoupledRuns { get; set; }

    public ulong CoupledOrders { get; set; }

    /// <summary>The clock_run rows by verdict (resumed, held, refused, ...).</summary>
    public SortedDictionary<string, ulong> RunVerdicts { get; } = new(StringComparer.Ordinal);

    public double ElapsedMs { get; set; }

    public double MaxElapsedMs { get; set; }

    public double GateWaitMs { get; set; }

    public double MaxGateWaitMs { get; set; }

    public ulong LiveSteps { get; set; }

    public ulong LiveReads { get; set; }

    public ulong MaxLiveReads { get; set; }

    public double LiveElapsedMs { get; set; }

    public double MaxLiveElapsedMs { get; set; }

    /// <summary>The native round trips a live step issued on average; 0 without a live step.</summary>
    public double LiveReadsPerStep() => LiveSteps == 0 ? 0 : (double)LiveReads / LiveSteps;

    /// <summary>The mean wall time of a live step; 0 without one.</summary>
    public double LiveStepMs() => LiveSteps == 0 ? 0 : LiveElapsedMs / LiveSteps;

    /// <summary>The mean wall time of a step of any reason; 0 without one.</summary>
    public double StepMs() => Steps == 0 ? 0 : ElapsedMs / Steps;
}

/// <summary>The native-run dispatch rows the routine Worker published (Go <c>bridge.DispatchSample</c>).</summary>
public sealed class DispatchSample
{
    public ulong Calls { get; set; }

    public ulong Live { get; set; }

    public ulong Refused { get; set; }

    public ulong LiveRefused { get; set; }

    public ulong StaleHolds { get; set; }

    /// <summary>The share of the Worker's native runs that native refused, or 0 without any.</summary>
    public double RefusedFraction() => Calls == 0 ? 0 : (double)Refused / Calls;
}

/// <summary>Wall TPS and the paused state the clock status samples report (Go <c>bridge.ClockSample</c>).</summary>
public sealed class ClockSample
{
    public ulong TickSamples { get; set; }

    public long TicksAdvanced { get; set; }

    public long LastTick { get; set; }

    public double WallSecs { get; set; }

    public double WallTps { get; set; }

    public ulong Resets { get; set; }

    public ulong PausedSamples { get; set; }

    public ulong ClockSamples { get; set; }

    public double PausedSecs { get; set; }

    public double SampledSecs { get; set; }

    public ulong NativePausedMs { get; set; }

    public ulong NativeRunningMs { get; set; }

    public ulong NativePauseSamples { get; set; }

    public double NativeProbeMs { get; set; }

    public double NativeDigestMs { get; set; }

    public ulong NativeProbes { get; set; }

    public ulong NativeDigests { get; set; }

    public long NativeMaxProbeTickGap { get; set; }

    public IReadOnlyList<HazardGap> NativeHazardGaps { get; set; } = [];

    public ulong NativePacedFrames { get; set; }

    public ulong NativePacedOverBudget { get; set; }

    public double NativeMaxPacedFrameMs { get; set; }

    /// <summary>
    /// Native's effective ticks per wall second (paused time included, over its last ten seconds of frames) across the
    /// status samples that reported a positive one: what the player got while the clock was on.
    /// </summary>
    public ulong EffectiveTpsSamples { get; set; }

    public double EffectiveTpsSum { get; set; }

    /// <summary>The mean positive effective ticks per second, or 0 without a sample.</summary>
    public double EffectiveTpsMean() => EffectiveTpsSamples == 0 ? 0 : EffectiveTpsSum / EffectiveTpsSamples;

    /// <summary>The digests' share of the supervisor's probe-path time, or 0 with nothing accounted.</summary>
    public double DigestShare()
    {
        var total = NativeProbeMs + NativeDigestMs;
        return total <= 0 ? 0 : NativeDigestMs / total;
    }

    /// <summary>Native's paused share of the accounted wall time, or 0 without any.</summary>
    public double PausedFractionNative() => NativePausedMs + NativeRunningMs == 0 ? 0 : (double)NativePausedMs / (NativePausedMs + NativeRunningMs);

    /// <summary>The time-weighted share of the sampled wall time the clock was paused, or the sample count ratio when no sample spans time.</summary>
    public double PausedFraction() =>
        SampledSecs > 0 ? PausedSecs / SampledSecs : ClockSamples > 0 ? (double)PausedSamples / ClockSamples : 0;
}

/// <summary>
/// A read-only aggregation of one log-ring timeline (Go <c>bridge.SummarizePhases</c>): the game-clock progress visible
/// in observation replies, the clock_step and dispatch rows, and the companion's observation and frame accounts. The
/// per-tool phase table and the snapshot-frame sample the Go summary also carries are not aggregated: the speed matrix
/// reads neither.
/// </summary>
public sealed class PhaseSummary
{
    private const string LiveStepReason = "live";

    public ClockSample Clock { get; } = new();

    public StepSample Steps { get; } = new();

    public DispatchSample Dispatch { get; } = new();

    public ObservationSample Observation { get; private set; } = new();

    public FrameSample Frames { get; private set; } = new();

    /// <summary>Aggregates the rows of a log ring.</summary>
    public static PhaseSummary Summarize(IReadOnlyList<TimelineRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var summary = new PhaseSummary();
        var clock = summary.Clock;
        long lastTick = 0;
        var haveTick = false;
        double tickWall = 0;
        double pausedWall = 0;
        var lastPaused = false;
        var havePaused = false;
        double holdWakeMs = 0;
        double maxHoldWakeMs = 0;
        ulong latencySamples = 0;
        NativePause first = default;
        NativePause last = default;
        var havePause = false;
        var observation = new ObservationAccumulator();
        var frames = new FrameAccumulator();
        foreach (var row in records)
        {
            switch (row.Kind)
            {
                case "native_call":
                    var wrapper = Text(row.Payload.GetValueOrDefault("tool"));
                    var failed = NativeReplyFailed(row);
                    if (row.Payload.GetValueOrDefault("timing") is IReadOnlyDictionary<string, object?> timing)
                    {
                        if (Number(timing.GetValueOrDefault("native_queue_ms")) is { } queue)
                        {
                            observation.Timed(queue, Field(timing, "native_execute_ms"));
                        }

                        if (ObservationRecord.Read(timing) is { } account)
                        {
                            observation.Hop(account);
                        }

                        if (FrameRecord.Read(timing) is { } frame)
                        {
                            frames.Sample(frame);
                        }
                    }

                    if (failed || wrapper != "games_call_tool")
                    {
                        continue;
                    }

                    var reply = RecordedReply(row.Payload);
                    if (reply is null)
                    {
                        continue;
                    }

                    ReplyClock(reply, out var tick, out var paused, out var hasTick, out var hasPaused, out var pause, out var hasPause);
                    if (hasPause)
                    {
                        clock.NativePauseSamples++;
                        if (!havePause)
                        {
                            first = pause;
                            havePause = true;
                        }

                        last = pause;
                        if (pause.EffectiveTps > 0)
                        {
                            clock.EffectiveTpsSamples++;
                            clock.EffectiveTpsSum += pause.EffectiveTps;
                        }
                    }

                    if (hasPaused)
                    {
                        clock.ClockSamples++;
                        if (paused)
                        {
                            clock.PausedSamples++;
                        }

                        if (havePaused && row.WallTime > pausedWall)
                        {
                            var span = row.WallTime - pausedWall;
                            clock.SampledSecs += span;
                            if (lastPaused)
                            {
                                clock.PausedSecs += span;
                            }
                        }

                        lastPaused = paused;
                        pausedWall = row.WallTime;
                        havePaused = true;
                    }

                    if (!hasTick)
                    {
                        continue;
                    }

                    clock.TickSamples++;
                    if (haveTick)
                    {
                        if (tick < lastTick)
                        {
                            clock.Resets++;
                        }
                        else
                        {
                            clock.TicksAdvanced += tick - lastTick;
                            clock.WallSecs += row.WallTime - tickWall;
                        }
                    }

                    lastTick = tick;
                    tickWall = row.WallTime;
                    haveTick = true;
                    clock.LastTick = tick;
                    break;

                case "clock_step":
                    StepRow(summary.Steps, SpeedMatrix.StepFields(row), ref holdWakeMs, ref maxHoldWakeMs, ref latencySamples);
                    break;

                case "clock_run":
                    RunRow(summary.Steps, LogRow.From(row, 0).Fields());
                    break;

                case "dispatch":
                    var payload = LogRow.From(row, 0).Fields();

                    // An outcome-change row with no native run carries no receipt: it is not one of the Worker's native dispatches.
                    if (payload.GetValueOrDefault("receipt") is not string receipt)
                    {
                        continue;
                    }

                    var d = summary.Dispatch;
                    d.Calls++;
                    var running = payload.GetValueOrDefault("running") is true;
                    if (running)
                    {
                        d.Live++;
                    }

                    if (payload.GetValueOrDefault("stale") is true)
                    {
                        d.StaleHolds++;
                    }

                    if (receipt == "refused")
                    {
                        d.Refused++;
                        if (running)
                        {
                            d.LiveRefused++;
                        }
                    }

                    break;
                default:
                    break;
            }
        }

        if (havePause && last.Paused >= first.Paused && last.Running >= first.Running)
        {
            clock.NativePausedMs = last.Paused - first.Paused;
            clock.NativeRunningMs = last.Running - first.Running;
        }

        if (havePause && last.ProbeMs >= first.ProbeMs && last.DigestMs >= first.DigestMs)
        {
            clock.NativeProbeMs = last.ProbeMs - first.ProbeMs;
            clock.NativeDigestMs = last.DigestMs - first.DigestMs;
            if (last.Probes >= first.Probes)
            {
                clock.NativeProbes = last.Probes - first.Probes;
            }

            if (last.Digests >= first.Digests)
            {
                clock.NativeDigests = last.Digests - first.Digests;
            }

            clock.NativeMaxProbeTickGap = last.MaxProbeTickGap;
            clock.NativeHazardGaps = last.HazardGaps ?? [];
        }

        if (havePause && last.PacedFrames >= first.PacedFrames && last.OverBudget >= first.OverBudget)
        {
            clock.NativePacedFrames = last.PacedFrames - first.PacedFrames;
            clock.NativePacedOverBudget = last.OverBudget - first.OverBudget;
            clock.NativeMaxPacedFrameMs = last.MaxFrameMs;
        }

        summary.Observation = observation.Result();
        summary.Frames = frames.Result();
        summary.HoldWakeSamples = latencySamples;
        summary.MeanHoldWakeMs = latencySamples > 0 ? holdWakeMs / latencySamples : 0;
        summary.MaxHoldWakeMs = maxHoldWakeMs;
        if (clock.WallSecs > 0)
        {
            clock.WallTps = (double)clock.TicksAdvanced / clock.WallSecs;
        }

        return summary;
    }

    /// <summary>The steps' hold-wake samples (the hold-to-step wall time of the holds that woke a step).</summary>
    public ulong HoldWakeSamples { get; private set; }

    public double MeanHoldWakeMs { get; private set; }

    public double MaxHoldWakeMs { get; private set; }

    // One clock_run decision row: the brain's Run, Hold or refusal. Its verdict names what it did; coupled orders ride on it.
    private static void RunRow(StepSample steps, IReadOnlyDictionary<string, object?> payload)
    {
        var verdict = Text(payload.GetValueOrDefault(Decision.VerdictKey));
        if (verdict.Length > 0)
        {
            steps.RunVerdicts[verdict] = steps.RunVerdicts.GetValueOrDefault(verdict) + 1;
        }

        if (Count(payload.GetValueOrDefault("coupled_orders")) is var orders and > 0)
        {
            steps.CoupledRuns++;
            steps.CoupledOrders += orders;
        }
    }

    private static void StepRow(StepSample steps, IReadOnlyDictionary<string, object?> payload, ref double holdWakeMs, ref double maxHoldWakeMs, ref ulong latencySamples)
    {
        steps.Steps++;
        var reads = Count(payload.GetValueOrDefault("reads"));
        steps.Reads += reads;
        steps.MaxReads = Math.Max(steps.MaxReads, reads);
        var elapsed = Field(payload, "elapsed_ms");
        steps.ElapsedMs += elapsed;
        steps.MaxElapsedMs = Math.Max(steps.MaxElapsedMs, elapsed);
        if (Field(payload, "gate_wait_ms") is var gate and > 0)
        {
            steps.GateWaitMs += gate;
            steps.MaxGateWaitMs = Math.Max(steps.MaxGateWaitMs, gate);
        }

        if (payload.GetValueOrDefault("reason") is string { Length: > 0 } reason)
        {
            steps.Reasons[reason] = steps.Reasons.GetValueOrDefault(reason) + 1;

            // The live steps carry the cost that bounds throughput at speed; keep their reads and wall apart from the
            // cold and stopped steps, which read whole families.
            if (reason == LiveStepReason)
            {
                steps.LiveSteps++;
                steps.LiveReads += reads;
                steps.MaxLiveReads = Math.Max(steps.MaxLiveReads, reads);
                steps.LiveElapsedMs += elapsed;
                steps.MaxLiveElapsedMs = Math.Max(steps.MaxLiveElapsedMs, elapsed);
            }
        }

        if (payload.GetValueOrDefault("stop") is true)
        {
            steps.HoldWoken++;
            if (Number(payload.GetValueOrDefault("stop_latency_ms")) is { } latency and >= 0)
            {
                latencySamples++;
                holdWakeMs += latency;
                maxHoldWakeMs = Math.Max(maxHoldWakeMs, latency);
            }
        }
    }

    // A native_call row records a failure: ok false or an error text.
    private static bool NativeReplyFailed(TimelineRecord row) =>
        row.Payload.GetValueOrDefault("ok") is false || Text(row.Payload.GetValueOrDefault("error")).Length > 0;

    private static string Text(object? value) => value as string ?? "";

    internal static double? Number(object? value) => value switch
    {
        double d => d,
        long l => l,
        int i => i,
        _ => null,
    };

    internal static double Field(IReadOnlyDictionary<string, object?> map, string key) => Number(map.GetValueOrDefault(key)) ?? 0;

    internal static ulong Count(object? value) => Number(value) is { } n and >= 0 ? (ulong)n : 0;

    // A recorded native_call row's reply as a JSON object: the JSON text its result.payload holds.
    private static JsonObject? RecordedReply(IReadOnlyDictionary<string, object?> payload)
    {
        if (payload.GetValueOrDefault("result") is not IReadOnlyDictionary<string, object?> result || result.GetValueOrDefault("payload") is not string text)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    // The ProtoJSON int64 encodings: a JSON string or a number.
    internal static bool TickValue(JsonNode? value, out long tick)
    {
        tick = 0;
        if (value is not JsonValue v)
        {
            return false;
        }

        if (v.TryGetValue<string>(out var text))
        {
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out tick);
        }

        if (v.TryGetValue<double>(out var number))
        {
            tick = (long)number;
            return true;
        }

        return false;
    }

    private static double? JsonNumber(JsonNode? value) =>
        value is JsonValue v && v.TryGetValue<double>(out var number) ? number : null;

    // The observation tick and, for clock status replies (or a bundle's clock status section), the actual-paused flag
    // and the native pause account out of a recorded reply.
    private static void ReplyClock(JsonObject reply, out long tick, out bool paused, out bool hasTick, out bool hasPaused, out NativePause pause, out bool hasPause)
    {
        tick = 0;
        paused = false;
        hasTick = false;
        hasPaused = false;
        pause = default;
        hasPause = false;
        long tickOut = 0;
        var pausedOut = false;
        var hasTickOut = false;
        var hasPausedOut = false;
        var pauseOut = default(NativePause);
        var hasPauseOut = false;
        void Status(JsonObject candidate)
        {
            if (StatusPaused(candidate) is { } value)
            {
                pausedOut = value;
                hasPausedOut = true;
            }

            if (StatusPause(candidate) is { } sample)
            {
                pauseOut = sample;
                hasPauseOut = true;
            }
        }

        foreach (var (_, outcome) in reply)
        {
            if (outcome is not JsonObject body)
            {
                continue;
            }

            if (body["context"] is JsonObject context && TickValue(context["tick"], out var value))
            {
                tickOut = value;
                hasTickOut = true;
            }

            Status(body);

            // A bundle carries the clock status as a section.
            if (body["clockStatus"] is JsonObject section)
            {
                Status(section);
            }
        }

        tick = tickOut;
        paused = pausedOut;
        hasTick = hasTickOut;
        hasPaused = hasPausedOut;
        pause = pauseOut;
        hasPause = hasPauseOut;
    }

    // Whether a recorded clock status shows the clock paused: its actual-paused flag when present, else its Run/Held state.
    private static bool? StatusPaused(JsonObject status)
    {
        if (status["actualPaused"] is JsonValue flag && flag.TryGetValue<bool>(out var actual))
        {
            return actual;
        }

        return status["state"]?.ToString() switch
        {
            "CLOCK_STATE_RUNNING" => false,
            "CLOCK_STATE_HELD" => true,
            _ => null,
        };
    }

    // A recorded clock status's native pause account (pausedMs / runningMs), absent on a status from a native build without it.
    private static NativePause? StatusPause(JsonObject status)
    {
        if (!TickValue(status["pausedMs"], out var pausedMs))
        {
            return null;
        }

        TickValue(status["runningMs"], out var runningMs);
        if (pausedMs < 0 || runningMs < 0)
        {
            return null;
        }

        var sample = new NativePause { Paused = (ulong)pausedMs, Running = (ulong)runningMs };
        sample.ProbeMs = JsonNumber(status["probeElapsedMs"]) ?? 0;
        sample.DigestMs = JsonNumber(status["digestElapsedMs"]) ?? 0;
        if (TickValue(status["probeTotal"], out var probes) && probes >= 0)
        {
            sample.Probes = (ulong)probes;
        }

        if (TickValue(status["digestTotal"], out var digests) && digests >= 0)
        {
            sample.Digests = (ulong)digests;
        }

        TickValue(status["sessionMaxProbeTickGap"], out var gapMax);
        sample.MaxProbeTickGap = gapMax;
        if (TickValue(status["pacedFrames"], out var frames) && frames >= 0)
        {
            sample.PacedFrames = (ulong)frames;
        }

        if (TickValue(status["pacedFramesOverBudget"], out var over) && over >= 0)
        {
            sample.OverBudget = (ulong)over;
        }

        sample.MaxFrameMs = JsonNumber(status["maxPacedFrameMs"]) ?? 0;
        sample.EffectiveTps = JsonNumber(status["effectiveTicksPerSecond"]) ?? 0;
        if (status["hazardGaps"] is JsonArray gaps)
        {
            var list = new List<HazardGap>();
            foreach (var raw in gaps)
            {
                if (raw is not JsonObject gapRow)
                {
                    continue;
                }

                TickValue(gapRow["boundTicks"], out var bound);
                TickValue(gapRow["maxTickGap"], out var maxGap);
                list.Add(new HazardGap
                {
                    HazardClass = gapRow["hazardClass"]?.ToString() ?? "",
                    BoundTicks = bound,
                    MaxTickGap = maxGap,
                    Hooked = gapRow["hooked"] is JsonValue hooked && hooked.TryGetValue<bool>(out var h) && h,
                });
            }

            sample.HazardGaps = list;
        }

        return sample;
    }

    private struct NativePause
    {
        public ulong Paused;
        public ulong Running;
        public double ProbeMs;
        public double DigestMs;
        public ulong Probes;
        public ulong Digests;
        public long MaxProbeTickGap;
        public IReadOnlyList<HazardGap> HazardGaps;
        public ulong PacedFrames;
        public ulong OverBudget;
        public double MaxFrameMs;
        public double EffectiveTps;
    }

    // ---- the companion's observation and frame accounts ----
    internal sealed class ObservationRecord
    {
        public double CaptureMs;
        public double FormatMs;
        public bool HasCapture;
        public ulong FormatPasses;
        public ulong PayloadBytes;
        public string Outcome = "";
        public List<SectionPhases> Sections = [];
        public bool Detached;
        public double EncodeQueueMs;
        public double EncodeMs;
        public double EncFormat;
        public ulong EncodeFormatPasses;
        public ThreatScan? Threats;

        // Reads the "native_observation" block; a row without one, or with a negative duration, reports nothing.
        public static ObservationRecord? Read(IReadOnlyDictionary<string, object?> timing)
        {
            if (timing.GetValueOrDefault("native_observation") is not IReadOnlyDictionary<string, object?> raw)
            {
                return null;
            }

            var capture = Number(raw.GetValueOrDefault("captureMs"));
            var format = Number(raw.GetValueOrDefault("formatMs"));
            if (capture < 0 || format < 0)
            {
                return null;
            }

            var record = new ObservationRecord
            {
                CaptureMs = capture ?? 0,
                HasCapture = capture is not null,
                FormatMs = format ?? 0,
                FormatPasses = Count(raw.GetValueOrDefault("formatPasses")),
                PayloadBytes = Count(raw.GetValueOrDefault("payloadBytes")),
                Outcome = Text(raw.GetValueOrDefault("outcome")),
            };
            if (raw.GetValueOrDefault("encode") is IReadOnlyDictionary<string, object?> encode
                && Number(encode.GetValueOrDefault("queueMs")) is { } queue and >= 0
                && Number(encode.GetValueOrDefault("ms")) is { } ms and >= 0)
            {
                record.Detached = true;
                record.EncodeQueueMs = queue;
                record.EncodeMs = ms;
                record.EncFormat = Field(encode, "formatMs");
                record.EncodeFormatPasses = Count(encode.GetValueOrDefault("formatPasses"));
            }

            if (raw.GetValueOrDefault("threatScan") is IReadOnlyDictionary<string, object?> scan)
            {
                record.Threats = new ThreatScan
                {
                    Hops = 1,
                    Examined = Count(scan.GetValueOrDefault("examined")),
                    Candidates = Count(scan.GetValueOrDefault("candidates")),
                    Projections = Count(scan.GetValueOrDefault("projections")),
                    ProximityChecks = Count(scan.GetValueOrDefault("proximityChecks")),
                };
            }

            if (raw.GetValueOrDefault("sections") is IReadOnlyDictionary<string, object?> sections)
            {
                foreach (var (name, entry) in sections)
                {
                    if (entry is not IReadOnlyDictionary<string, object?> body)
                    {
                        continue;
                    }

                    var sectionMs = Math.Max(0, Field(body, "ms"));
                    record.Sections.Add(new SectionPhases
                    {
                        Section = name,
                        Hops = 1,
                        Ms = sectionMs,
                        MaxMs = sectionMs,
                        Rows = Count(body.GetValueOrDefault("rows")),
                        Candidates = Count(body.GetValueOrDefault("candidates")),
                    });
                }
            }

            if (capture is null && format is null && !record.Detached && record.PayloadBytes == 0 && record.Sections.Count == 0)
            {
                return null;
            }

            return record;
        }
    }

    internal sealed class FrameRecord
    {
        public bool Hooked;
        public ulong Updates;
        public ulong Observations;
        public ulong Cancelled;
        public double ElapsedMs;
        public double MaxIntervalMs;
        public double ObservationMs;
        public double RecorderMs;
        public Dictionary<double, ulong>? Slow;
        public List<JsonObject> Worst = [];
        public List<double> HistEdges = [];
        public List<ulong> HistCounts = [];
        public List<ulong> HistObserved = [];

        // Reads the "native_frames" block; one without an update count reports nothing.
        public static FrameRecord? Read(IReadOnlyDictionary<string, object?> timing)
        {
            if (timing.GetValueOrDefault("native_frames") is not IReadOnlyDictionary<string, object?> raw)
            {
                return null;
            }

            if (Number(raw.GetValueOrDefault("updates")) is not { } updates || updates < 0)
            {
                return null;
            }

            var record = new FrameRecord
            {
                Updates = (ulong)updates,
                Hooked = raw.GetValueOrDefault("hooked") is true,
                ElapsedMs = Field(raw, "elapsedMs"),
                MaxIntervalMs = Field(raw, "maxUpdateMs"),
                ObservationMs = Field(raw, "observationMs"),
                RecorderMs = Field(raw, "recorderMs"),
                Observations = Count(raw.GetValueOrDefault("observations")),
                Cancelled = Count(raw.GetValueOrDefault("cancelled")),
            };
            if (raw.GetValueOrDefault("slow") is List<object?> buckets)
            {
                record.Slow = [];
                foreach (var entry in buckets)
                {
                    if (entry is IReadOnlyDictionary<string, object?> body && Number(body.GetValueOrDefault("thresholdMs")) is { } threshold)
                    {
                        record.Slow[threshold] = Count(body.GetValueOrDefault("count"));
                    }
                }
            }

            if (raw.GetValueOrDefault("histogram") is IReadOnlyDictionary<string, object?> hist
                && hist.GetValueOrDefault("edgesMs") is List<object?> edges
                && hist.GetValueOrDefault("counts") is List<object?> counts
                && counts.Count == edges.Count + 1)
            {
                record.HistEdges = [.. edges.Select(edge => Number(edge) ?? 0)];
                record.HistCounts = [.. counts.Select(Count)];
                if (hist.GetValueOrDefault("observedCounts") is List<object?> observed && observed.Count == counts.Count)
                {
                    record.HistObserved = [.. observed.Select(Count)];
                }
            }

            if (raw.GetValueOrDefault("worst") is List<object?> worst)
            {
                foreach (var entry in worst)
                {
                    if (entry is not IReadOnlyDictionary<string, object?> body)
                    {
                        continue;
                    }

                    var frame = new JsonObject
                    {
                        ["frame"] = Count(body.GetValueOrDefault("update")),
                        ["interval_ms"] = Field(body, "intervalMs"),
                        ["observation_ms"] = Field(body, "observationMs"),
                    };
                    var tick = body.GetValueOrDefault("tick");
                    frame["tick"] = tick switch
                    {
                        string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
                        double number => (long)number,
                        _ => 0L,
                    };
                    if (Text(body.GetValueOrDefault("trace")) is { Length: > 0 } trace)
                    {
                        frame["trace"] = trace;
                    }

                    record.Worst.Add(frame);
                }
            }

            return record;
        }
    }

    internal sealed class ObservationAccumulator
    {
        private readonly ObservationSample _sample = new();
        private readonly List<double> _capture = [];
        private readonly List<double> _format = [];
        private readonly List<double> _queue = [];
        private readonly List<double> _execute = [];
        private readonly List<double> _encodeQueue = [];
        private readonly List<double> _encode = [];
        private readonly Dictionary<string, SectionPhases> _sections = new(StringComparer.Ordinal);

        public void Hop(ObservationRecord record)
        {
            _sample.Hops++;
            _sample.CaptureMs += record.CaptureMs;
            _sample.FormatMs += record.FormatMs;
            _sample.FormatPasses += record.FormatPasses;
            _sample.PayloadBytes += record.PayloadBytes;
            if (record.HasCapture)
            {
                _capture.Add(record.CaptureMs);
            }

            _format.Add(record.FormatMs);
            if (record.Detached)
            {
                _sample.EncodeHops++;
                _sample.EncodeFormatPasses += record.EncodeFormatPasses;
                _sample.EncodeFormatMs += Math.Max(0, record.EncFormat);
                _encodeQueue.Add(record.EncodeQueueMs);
                _encode.Add(record.EncodeMs);
            }

            if (record.Outcome.Length > 0)
            {
                _sample.Outcomes[record.Outcome] = _sample.Outcomes.GetValueOrDefault(record.Outcome) + 1;
            }

            if (record.Threats is { } t)
            {
                var total = _sample.Threats ??= new ThreatScan();
                total.Hops += t.Hops;
                total.Examined += t.Examined;
                total.Candidates += t.Candidates;
                total.Projections += t.Projections;
                total.ProximityChecks += t.ProximityChecks;
            }

            foreach (var section in record.Sections)
            {
                if (!_sections.TryGetValue(section.Section, out var entry))
                {
                    entry = new SectionPhases { Section = section.Section };
                    _sections[section.Section] = entry;
                }

                entry.Hops++;
                entry.Ms += section.Ms;
                entry.MaxMs = Math.Max(entry.MaxMs, section.Ms);
                entry.Rows += section.Rows;
                entry.Candidates += section.Candidates;
            }
        }

        // One hop's companion queue and execute time, which every timed reply carries whether or not it also carries an observation account.
        public void Timed(double queueMs, double executeMs)
        {
            _queue.Add(queueMs);
            _execute.Add(executeMs);
        }

        public ObservationSample Result()
        {
            _sample.Capture = PhaseQuantiles.Of(_capture);
            _sample.Format = PhaseQuantiles.Of(_format);
            _sample.Queue = PhaseQuantiles.Of(_queue);
            _sample.Execute = PhaseQuantiles.Of(_execute);
            _sample.EncodeQueue = PhaseQuantiles.Of(_encodeQueue);
            _sample.Encode = PhaseQuantiles.Of(_encode);
            _sample.Sections.Clear();
            _sample.Sections.AddRange(_sections.Values
                .OrderByDescending(s => s.Ms)
                .ThenBy(s => s.Section, StringComparer.Ordinal));
            return _sample;
        }
    }

    // Differences the cumulative frame counters between the recording's first and last sample; a counter that went
    // backwards (a reloaded game reset the session) starts a new baseline rather than reporting a negative span.
    internal sealed class FrameAccumulator
    {
        private static readonly double[] SlowThresholdsMs = [16.7, 33.3, 100, 250];
        private FrameRecord _first = new();
        private FrameRecord _last = new();
        private bool _have;
        private ulong _samples;
        private double _maxInterval;

        public void Sample(FrameRecord record)
        {
            _samples++;
            _maxInterval = Math.Max(_maxInterval, record.MaxIntervalMs);
            if (!_have || record.Updates < _first.Updates)
            {
                _first = record;
                _have = true;
            }

            _last = record;
        }

        public FrameSample Result()
        {
            if (!_have)
            {
                return new FrameSample();
            }

            var output = new FrameSample { Hooked = _last.Hooked, Samples = _samples, MaxIntervalMs = _maxInterval };
            output.Worst.AddRange(_last.Worst);
            output.Updates = Span(_last.Updates, _first.Updates);
            output.Observations = Span(_last.Observations, _first.Observations);
            output.Cancelled = Span(_last.Cancelled, _first.Cancelled);
            output.ElapsedMs = SpanMs(_last.ElapsedMs, _first.ElapsedMs);
            output.ObservationMs = SpanMs(_last.ObservationMs, _first.ObservationMs);
            output.RecorderMs = SpanMs(_last.RecorderMs, _first.RecorderMs);
            foreach (var threshold in SlowThresholdsMs)
            {
                if (_last.Slow is null || !_last.Slow.TryGetValue(threshold, out var lastCount))
                {
                    continue;
                }

                var firstCount = _first.Slow is not null && _first.Slow.TryGetValue(threshold, out var f) ? f : 0;
                output.Slow.Add((threshold, Span(lastCount, firstCount)));
            }

            var sameSession = _first.Updates <= _last.Updates;
            output.Intervals = HistogramQuantiles(_first.HistCounts, _last.HistCounts, _last.HistEdges, sameSession, output.MaxIntervalMs, output.ElapsedMs);
            output.Observed = HistogramQuantiles(_first.HistObserved, _last.HistObserved, _last.HistEdges, sameSession, output.MaxIntervalMs, 0);

            // The whole recording's widest interval is exact; the observed frames' widest is their top bucket, since the ring is session-wide.
            if (output.Intervals.Samples > 0)
            {
                output.Intervals = output.Intervals with { Max = output.MaxIntervalMs };
            }

            return output;
        }

        private static ulong Span(ulong last, ulong first) => last < first ? last : last - first;

        private static double SpanMs(double last, double first) => last < first ? Math.Max(0, last) : last - first;

        // The nearest-rank p50/p95/p99 of the update intervals between two samples, differenced from the companion's
        // cumulative interval histogram; a quantile reports its bucket's upper edge.
        private static DispatchQuantiles HistogramQuantiles(List<ulong> first, List<ulong> last, List<double> edges, bool sameSession, double maxMs, double sumMs)
        {
            if (last.Count == 0)
            {
                return new DispatchQuantiles();
            }

            var counts = new ulong[last.Count];
            ulong total = 0;
            for (var i = 0; i < last.Count; i++)
            {
                var count = last[i];
                if (sameSession && first.Count == last.Count && count >= first[i])
                {
                    count -= first[i];
                }

                counts[i] = count;
                total += count;
            }

            if (total == 0)
            {
                return new DispatchQuantiles();
            }

            double Edge(double q)
            {
                var rank = Math.Max(1UL, (ulong)Math.Ceiling(q * total));
                ulong seen = 0;
                for (var i = 0; i < counts.Length; i++)
                {
                    seen += counts[i];
                    if (seen >= rank)
                    {
                        return i < edges.Count ? Math.Min(edges[i], maxMs) : maxMs;
                    }
                }

                return maxMs;
            }

            return new DispatchQuantiles { Samples = total, P50 = Edge(0.50), P95 = Edge(0.95), P99 = Edge(0.99), Max = Edge(1), Sum = sumMs };
        }
    }
}

/// <summary>
/// Aggregates the observation and frame accounts carried by raw native reply envelopes, for a caller with no OTLP
/// recording to summarize (Go <c>bridge.NativeAccount</c>): the speed matrix's governor-off row plays the save with no
/// controller attached, so its only samples are the harness's own replies.
/// </summary>
public sealed class NativeAccount
{
    private readonly PhaseSummary.ObservationAccumulator _observation = new();
    private readonly PhaseSummary.FrameAccumulator _frames = new();

    /// <summary>Reads one reply envelope's timing block and reports whether it carried anything.</summary>
    public bool Reply(JsonObject envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope["timing"] is not JsonObject timing)
        {
            return false;
        }

        // The companion's own key names; the service renames them when it copies the blocks into a row.
        var row = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["native_observation"] = ToPlain(timing["observation"]),
            ["native_frames"] = ToPlain(timing["frames"]),
        };
        var found = false;
        if (timing["queueMs"] is JsonValue queue && queue.TryGetValue<double>(out var queueMs))
        {
            var executeMs = timing["executeMs"] is JsonValue execute && execute.TryGetValue<double>(out var e) ? e : 0;
            _observation.Timed(queueMs, executeMs);
            found = true;
        }

        if (PhaseSummary.ObservationRecord.Read(row) is { } account)
        {
            _observation.Hop(account);
            found = true;
        }

        if (PhaseSummary.FrameRecord.Read(row) is { } frame)
        {
            _frames.Sample(frame);
            found = true;
        }

        return found;
    }

    /// <summary>The aggregate over the replies read so far.</summary>
    public ObservationSample Observation() => _observation.Result();

    /// <summary>The frame recorder's account differenced between the first and last reply that carried it.</summary>
    public FrameSample Frames() => _frames.Result();

    // The timeline reader's plain shape: numbers are double, arrays List of object, objects Dictionary.
    private static object? ToPlain(JsonNode? node) => node switch
    {
        JsonObject o => o.ToDictionary(p => p.Key, p => ToPlain(p.Value), StringComparer.Ordinal),
        JsonArray a => a.Select(ToPlain).ToList(),
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => null,
    };
}
