using System.Diagnostics.Metrics;

namespace RimGovernor.Brain.Telemetry;

/// <summary>An attribute value that came from a <see cref="Vocabulary"/>.</summary>
public readonly record struct Label
{
    internal Label(string value) => Value = value;

    public string Value { get; }

    public override string ToString() => Value ?? "";
}

/// <summary>A closed set of attribute values, so a metric's series count is bounded by construction.</summary>
public sealed class Vocabulary
{
    /// <summary>The label a value outside its vocabulary collapses to.</summary>
    public const string Other = "other";

    private readonly HashSet<string> _set;
    private readonly Func<string, bool>? _ok;

    private Vocabulary(HashSet<string> set, Func<string, bool>? ok)
    {
        _set = set;
        _ok = ok;
    }

    /// <summary>A vocabulary of exactly the given values.</summary>
    public static Vocabulary Words(params string[] values) => new(new HashSet<string>(values, StringComparer.Ordinal), null);

    /// <summary>A vocabulary of the values <paramref name="ok"/> accepts; it must accept a finite set (an enum's membership test), never a pattern.</summary>
    public static Vocabulary Where(Func<string, bool> ok) => new([], ok);

    /// <summary>The value when it belongs to the vocabulary, else <see cref="Other"/>.</summary>
    public Label Label(string v) => _set.Contains(v) || (_ok is not null && _ok(v)) ? new Label(v) : new Label(Other);
}

/// <summary>
/// The controller's counters and histograms, recorded from explicit measurements at the call site
/// (docs/developers/contracts/otel-metrics.md). Every attribute is a <see cref="Label"/> from a closed
/// <see cref="Vocabulary"/>, so pawns, request ids and free-form targets cannot be passed. The instruments live
/// on one process-wide <see cref="Meter"/>; with no listener attached a measurement costs nothing.
/// </summary>
public static class TelemetryMetrics
{
    // Native-call phases (the phase attribute of NativePhase).
    public const string PhaseGateWait = "gate_wait";
    public const string PhaseCall = "call";
    public const string PhaseDecode = "decode";
    public const string PhaseProtoDecode = "proto_decode";
    public const string PhaseNativeQueue = "native_queue";
    public const string PhaseNativeExecute = "native_execute";

    // Instrument names, the metrics contract's table.
    public const string NativePhaseMetric = "rimgovernor.native.phase.duration";
    public const string NativeCallsMetric = "rimgovernor.native.calls";
    public const string StepMetric = "rimgovernor.scheduler.step.duration";
    public const string StopLatencyMetric = "rimgovernor.scheduler.stop_latency";
    public const string ControllerPauseMetric = "rimgovernor.scheduler.controller_pause";
    public const string StopPauseMetric = "rimgovernor.scheduler.stop_pause";
    public const string CriticalWaveMetric = "rimgovernor.scheduler.critical_wave.duration";
    public const string TicksPerSecondMetric = "rimgovernor.game.ticks_per_second";
    public const string OutcomesMetric = "rimgovernor.action.outcomes";
    public const string AdmissionsMetric = "rimgovernor.admission.decisions";

    /// <summary>The label of an outcome that has no refusal reason.</summary>
    public static readonly Label NoReason = new("none");

    // Histogram boundaries, explicit because the file sink carries only explicit buckets. Milliseconds throughout.

    /// <summary>Spans a cached read (sub-millisecond) to a call that hit its timeout (10 s).</summary>
    public static readonly IReadOnlyList<double> NativeBuckets =
        [0.5, 1, 2, 5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000];

    /// <summary>Spans a quiet admission tail to a step that waited out a long pause (5 min).</summary>
    public static readonly IReadOnlyList<double> SchedulerBuckets =
        [1, 5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000, 30000, 60000, 120000, 300000];

    private static readonly Vocabulary s_nativePhases = Vocabulary.Words(PhaseGateWait, PhaseCall, PhaseDecode, PhaseProtoDecode, PhaseNativeQueue, PhaseNativeExecute);
    private static readonly Vocabulary s_nativeOutcomes = Vocabulary.Words("ok", "refused", "error");
    private static readonly Vocabulary s_verdicts = Vocabulary.Words("applied", "refused", "failed");
    private static readonly Vocabulary s_admissions = Vocabulary.Words("admitted", "refused");

    private static readonly Meter s_meter = new(Spans.ScopePrefix + "telemetry");

    private static readonly Histogram<double> s_nativePhase = Hist(NativePhaseMetric, "Native call time by phase and native tool.", NativeBuckets);
    private static readonly Histogram<double> s_step = Hist(StepMetric, "Scheduler step wall time by cause.", SchedulerBuckets);
    private static readonly Histogram<double> s_stopLatency = Hist(StopLatencyMetric, "Native clock stop to the step it woke.", SchedulerBuckets);
    private static readonly Histogram<double> s_controllerPause = Hist(ControllerPauseMetric, "Wall time the controller held the game paused in a step.", SchedulerBuckets);
    private static readonly Histogram<double> s_stopPause = Hist(StopPauseMetric, "Window stop to the admission that resumed the game.", SchedulerBuckets);
    private static readonly Histogram<double> s_criticalWave = Hist(CriticalWaveMetric, "Critical planner wave wall time, the controller's leg of hazard to protective order.", SchedulerBuckets);
    private static readonly Counter<long> s_nativeCalls = s_meter.CreateCounter<long>(NativeCallsMetric, "{call}", "Completed native calls by native tool and outcome.");
    private static readonly Gauge<double> s_ticksPerSecond = s_meter.CreateGauge<double>(TicksPerSecondMetric, "{tick}/s", "Game ticks per wall second the clock status last reported.");
    private static readonly Counter<long> s_outcomes = s_meter.CreateCounter<long>(OutcomesMetric, "{action}", "Dispatched actions by kind, verdict and refusal reason.");
    private static readonly Counter<long> s_admissionCount = s_meter.CreateCounter<long>(AdmissionsMetric, "{decision}", "Method admissions by verdict and refusal reason.");

    private static Histogram<double> Hist(string name, string description, IReadOnlyList<double> bounds) =>
        s_meter.CreateHistogram<double>(name, "ms", description, advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = bounds });

    private static KeyValuePair<string, object?> Tag(string key, Label l) => new(key, l.Value ?? "");

    /// <summary>Records one phase of a native call. <paramref name="tool"/> must come from a vocabulary of native tool names.</summary>
    public static void NativePhase(Label tool, string phase, double ms) =>
        s_nativePhase.Record(ms, Tag("native_tool", tool), Tag("phase", s_nativePhases.Label(phase)));

    /// <summary>Counts one completed native call; <paramref name="outcome"/> is ok, refused or error.</summary>
    public static void NativeCall(Label tool, string outcome) =>
        s_nativeCalls.Add(1, Tag("native_tool", tool), Tag("outcome", s_nativeOutcomes.Label(outcome)));

    /// <summary>Records one step's wall time under its cause.</summary>
    public static void SchedulerStep(Label cause, double ms) => s_step.Record(ms, Tag("step_cause", cause));

    /// <summary>Records the native stop stamp to the step it woke.</summary>
    public static void StopLatency(double ms) => s_stopLatency.Record(ms);

    /// <summary>Records the wall time the controller held the game paused.</summary>
    public static void ControllerPause(double ms) => s_controllerPause.Record(ms);

    /// <summary>Records the window stop to readmit pause.</summary>
    public static void StopPause(double ms) => s_stopPause.Record(ms);

    /// <summary>Records one critical planner wave's wall time.</summary>
    public static void CriticalWave(double ms) => s_criticalWave.Record(ms);

    /// <summary>Sets the game ticks per wall second gauge.</summary>
    public static void TicksPerSecond(double tps) => s_ticksPerSecond.Record(tps);

    /// <summary>Counts one dispatched action: verdict applied, refused or failed, and for a refusal its reason (<see cref="NoReason"/> otherwise).</summary>
    public static void ActionOutcome(Label kind, string verdict, Label reason) =>
        s_outcomes.Add(1, Tag("action_kind", kind), Tag("verdict", s_verdicts.Label(verdict)), Tag("reason", reason));

    /// <summary>Counts one method admission: verdict admitted or refused, and for a refusal its reason (<see cref="NoReason"/> otherwise).</summary>
    public static void Admission(string verdict, Label reason) =>
        s_admissionCount.Add(1, Tag("verdict", s_admissions.Label(verdict)), Tag("reason", reason));
}
