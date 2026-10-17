using RimGovernor.Brain.Telemetry;
using RimGovernor.Brain.Telemetry.Otlp;
using RimGovernor.Contracts.Wire.Clock;

namespace RimGovernor.Brain.Acceptance;

/// <summary>The clock_step rows a recording covers.</summary>
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

/// <summary>The native-run dispatch rows the routine Worker published.</summary>
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

/// <summary>The native clock accounting: differences between two <see cref="ClockAccounting"/> reads, 0 without them.</summary>
public sealed class ClockSample
{
    public ulong NativePausedMs { get; set; }

    public ulong NativeRunningMs { get; set; }

    public double NativeProbeMs { get; set; }

    public double NativeDigestMs { get; set; }

    public ulong NativeProbes { get; set; }

    public ulong NativeDigests { get; set; }

    public long NativeMaxProbeTickGap { get; set; }

    public IReadOnlyList<HazardGap> NativeHazardGaps { get; set; } = [];

    public ulong NativePacedFrames { get; set; }

    public ulong NativePacedOverBudget { get; set; }

    public double NativeMaxPacedFrameMs { get; set; }

    /// <summary>Sets the native accounting to the difference between two reads; a counter that went backwards (a reload between them) leaves its measure unset.</summary>
    public void Account(ClockAccounting first, ClockAccounting last)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(last);
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
}

/// <summary>
/// A read-only aggregation of one log-ring timeline: the clock_step, clock_run and
/// dispatch rows, and native's clock accounting between two reads.
/// </summary>
public sealed class PhaseSummary
{
    public ClockSample Clock { get; } = new();

    public StepSample Steps { get; } = new();

    public DispatchSample Dispatch { get; } = new();

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
