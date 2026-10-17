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
        return new JsonObject { { "samples", q.Samples }, { "p50_ms", q.P50 }, { "p95_ms", q.P95 }, { "p99_ms", q.P99 }, { "max_ms", q.Max }, { "sum_ms", q.Sum } };
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
            { "hops", Hops },
            { "format_passes", FormatPasses },
            { "payload_bytes", PayloadBytes },
            { "capture_ms", CaptureMs },
            { "format_ms", FormatMs },
            { "capture", Capture.ToJson() },
            { "format", Format.ToJson() },
            { "queue", Queue.ToJson() },
            { "execute", Execute.ToJson() },
        };
        if (Sections.Count > 0)
        {
            row.Add("sections", OutputJson.Array(Sections, section =>
                new JsonObject { { "section", section.Section }, { "hops", section.Hops }, { "ms", section.Ms }, { "max_ms", section.MaxMs }, { "rows", section.Rows } }
                    .AddIfPresent("candidates", section.Candidates > 0 ? section.Candidates : null)));
        }

        if (Outcomes.Count > 0)
        {
            var outcomes = new JsonObject();
            foreach (var (name, count) in Outcomes)
            {
                outcomes.Add(name, count);
            }

            row.Add("outcomes", outcomes);
        }

        row.AddIfPresent("encode_hops", EncodeHops > 0 ? EncodeHops : null)
            .AddIfPresent("encode_format_passes", EncodeFormatPasses > 0 ? EncodeFormatPasses : null)
            .AddIfPresent("encode_format_ms", EncodeFormatMs != 0 ? EncodeFormatMs : null);
        row.Add("encode_queue", EncodeQueue.ToJson());
        row.Add("encode", Encode.ToJson());
        if (Threats is { } t)
        {
            row.Add("threat_scan", new JsonObject
            {
                { "hops", t.Hops },
                { "examined", t.Examined },
                { "candidates", t.Candidates },
                { "projections", t.Projections },
                { "proximity_checks", t.ProximityChecks },
            });
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
            { "hooked", Hooked },
            { "samples", Samples },
            { "updates", Updates },
            { "elapsed_ms", ElapsedMs },
            { "max_interval_ms", MaxIntervalMs },
            { "observation_ms", ObservationMs },
            { "observations", Observations },
            { "cancelled_hops", Cancelled },
            { "recorder_ms", RecorderMs },
            { "intervals", Intervals.ToJson() },
            { "observed_intervals", Observed.ToJson() },
        };
        if (Slow.Count > 0)
        {
            row.Add("slow", OutputJson.Array(Slow, s => new JsonObject { { "threshold_ms", s.ThresholdMs }, { "count", s.Count } }));
        }

        if (Worst.Count > 0)
        {
            row.Add("worst", OutputJson.Array(Worst, frame => frame.DeepClone()));
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

    /// <summary>The native accounting below: differences between two <see cref="ClockAccounting"/> reads (NativePauseSamples 2), 0 without them.</summary>
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

    /// <summary>Sets the native accounting to the difference between two reads; a counter that went backwards (a reload between them) leaves its measure unset.</summary>
    public void Account(ClockAccounting first, ClockAccounting last)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(last);
        NativePauseSamples = 2;
        if (last.PausedMs >= first.PausedMs && last.RunningMs >= first.RunningMs)
        {
            NativePausedMs = last.PausedMs - first.PausedMs;
            NativeRunningMs = last.RunningMs - first.RunningMs;
        }

        if (last.ProbeMs >= first.ProbeMs && last.DigestMs >= first.DigestMs)
        {
            NativeProbeMs = last.ProbeMs - first.ProbeMs;
            NativeDigestMs = last.DigestMs - first.DigestMs;
            if (last.Probes >= first.Probes)
            {
                NativeProbes = last.Probes - first.Probes;
            }

            if (last.Digests >= first.Digests)
            {
                NativeDigests = last.Digests - first.Digests;
            }

            NativeMaxProbeTickGap = last.SessionMaxProbeTickGap;
            NativeHazardGaps = last.HazardGaps;
        }

        if (last.PacedFrames >= first.PacedFrames && last.OverBudget >= first.OverBudget)
        {
            NativePacedFrames = last.PacedFrames - first.PacedFrames;
            NativePacedOverBudget = last.OverBudget - first.OverBudget;
            NativeMaxPacedFrameMs = last.MaxFrameMs;
        }
    }

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
    public ClockSample Clock { get; } = new();

    public StepSample Steps { get; } = new();

    public DispatchSample Dispatch { get; } = new();

    public ObservationSample Observation { get; } = new();

    public FrameSample Frames { get; } = new();

    /// <summary>
    /// Aggregates the rows of a log ring; the native clock accounting is the difference between the
    /// <paramref name="start"/> and <paramref name="end"/> reads of <see cref="ClockAccounting"/>, absent without both.
    /// </summary>
    public static PhaseSummary Summarize(IReadOnlyList<TimelineRecord> records, ClockAccounting? start = null, ClockAccounting? end = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        var summary = new PhaseSummary();
        var clock = summary.Clock;
        double holdWakeMs = 0;
        double maxHoldWakeMs = 0;
        ulong latencySamples = 0;
        foreach (var row in records)
        {
            switch (row.Kind)
            {
                case TelemetryKinds.ClockStepKind:
                    StepRow(summary.Steps, SpeedMatrix.StepFields(row), ref holdWakeMs, ref maxHoldWakeMs, ref latencySamples);
                    break;

                case TelemetryKinds.ClockRunKind:
                    RunRow(summary.Steps, LogRow.From(row, 0).Fields());
                    break;

                case TelemetryKinds.DispatchKind:
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

        if (start is not null && end is not null)
        {
            clock.Account(start, end);
        }

        summary.HoldWakeSamples = latencySamples;
        summary.MeanHoldWakeMs = latencySamples > 0 ? holdWakeMs / latencySamples : 0;
        summary.MaxHoldWakeMs = maxHoldWakeMs;
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
}
