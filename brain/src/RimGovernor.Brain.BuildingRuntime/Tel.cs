using System.Diagnostics;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// Package-level Decide/Event/StartSpan/... helpers over the Telemetry project's facade.
/// The facade takes its <see cref="TelemetryEmitter"/> by
/// instance, so the controller session installs its emitter here once and the planners' static helpers, which
/// have no session in reach, emit through it. Until one is installed every row is dropped (the null sink).
/// </summary>
internal static class Tel
{
    private static TelemetryEmitter s_emitter = new(NullTelemetrySink.Instance);

    /// <summary>The emitter rows go to; the session replaces it with the one over its sink.</summary>
    public static TelemetryEmitter Emitter
    {
        get => Volatile.Read(ref s_emitter);
        set => Volatile.Write(ref s_emitter, value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>The label of an outcome with no refusal reason.</summary>
    public static Label NoReason => TelemetryMetrics.NoReason;

    public static void Decide(Decision d) => Emitter.Decide(d);

    public static void Event(TelemetryLevel level, string component, string kind, string message, IEnumerable<KeyValuePair<string, object?>>? attrs = null) =>
        Emitter.Event(level, component, kind, message, attrs);

    /// <summary><see cref="Event(TelemetryLevel, string, string, string, IEnumerable{KeyValuePair{string, object?}}?)"/> with attributes as (key, value) tuples.</summary>
    public static void Event(TelemetryLevel level, string component, string kind, string message, IEnumerable<(string Key, object? Value)> attrs) =>
        Emitter.Event(level, component, kind, message, attrs.Select(a => new KeyValuePair<string, object?>(a.Key, a.Value)));

    /// <summary>Records the game tick the service last read (feeds each row's tick stamp).</summary>
    public static void ObserveTick(long tick) => Emitter.Ticks.Observe(tick);

    /// <summary>Starts a span under the current activity, or under <paramref name="parent"/> when one is given, with tags.</summary>
    public static Activity? StartSpan(string component, string name, params (string Key, object? Value)[] attrs) =>
        StartSpan(component, name, default, attrs);

    public static Activity? StartSpan(string component, string name, ActivityContext parent, params (string Key, object? Value)[] attrs)
    {
        var span = Spans.StartSpan(component, name, parent);
        if (span is not null)
        {
            foreach (var (key, value) in attrs)
            {
                span.SetTag(key, value);
            }
        }
        return span;
    }

    public static Activity? StartSpan(string component, string name, IReadOnlyDictionary<string, object?> attrs)
    {
        var span = Spans.StartSpan(component, name);
        if (span is not null)
        {
            foreach (var (key, value) in attrs)
            {
                span.SetTag(key, value);
            }
        }
        return span;
    }

    public static void EndSpan(Activity? span, Exception? error = null) => Spans.EndSpan(span, error);

    /// <summary>Tags <paramref name="span"/> with the reads <paramref name="tally"/> counted, their game-thread wait and run time, and the reads per native tool.</summary>
    public static void TagReads(Activity? span, ReadTally tally)
    {
        if (span is null)
        {
            return;
        }
        foreach (var (key, value) in tally.Attrs())
        {
            span.SetTag("rimgovernor." + key, value is Dictionary<string, object?> tools
                ? string.Join(",", tools.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => t.Key + "=" + t.Value))
                : value);
        }
    }

    /// <summary>
    /// Opens a child span named <paramref name="name"/> over one phase of a planner run, with its own read tally
    /// nested under the planner's; disposing it tags the span with the phase's reads (<see cref="TagReads"/>) and
    /// ends it.
    /// </summary>
    public static IDisposable Phase(string name, params (string Key, object? Value)[] attrs) =>
        new PhaseScope(StartSpan("buildingruntime", name, attrs), ReadTallyScope.Nested());

    private sealed class PhaseScope(Activity? span, ReadTallyScope reads) : IDisposable
    {
        public void Dispose()
        {
            reads.Dispose();
            TagReads(span, reads.Tally);
            EndSpan(span);
        }
    }

    public static void Refuse(Activity? span, params string[] reasons) => Spans.Refuse(span, reasons);

    /// <summary>The remote parent a wire traceparent names; the default context (a new root) for a malformed one.</summary>
    public static ActivityContext Remote(string wire) =>
        Spans.TryParseWireTrace(wire, out var trace, out var span) ? Spans.RemoteHex(trace, span) : default;

    public static void Admission(string verdict, Label reason) => TelemetryMetrics.Admission(verdict, reason);

    public static void ActionOutcome(Label kind, string verdict, Label reason) => TelemetryMetrics.ActionOutcome(kind, verdict, reason);

    public static void SchedulerStep(Label cause, double ms) => TelemetryMetrics.SchedulerStep(cause, ms);

    public static void StopLatency(double ms) => TelemetryMetrics.StopLatency(ms);

    public static void CriticalWave(double ms) => TelemetryMetrics.CriticalWave(ms);

    public static void TicksPerSecond(double tps) => TelemetryMetrics.TicksPerSecond(tps);
}
