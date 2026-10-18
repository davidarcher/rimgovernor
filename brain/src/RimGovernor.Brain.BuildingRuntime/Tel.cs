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

    /// <summary>
    /// Tags <paramref name="span"/>, when the returned scope is disposed, with the runtime counters' deltas over the
    /// scope (<see cref="RuntimeSample"/>) and the GC mode and processor count they are read against.
    /// </summary>
    public static IDisposable RuntimeTags(Activity? span)
    {
        span?.SetTag("rimgovernor.gc_mode", (System.Runtime.GCSettings.IsServerGC ? "server/" : "workstation/") + System.Runtime.GCSettings.LatencyMode);
        span?.SetTag("rimgovernor.processors", Environment.ProcessorCount);
        return new RuntimeScope(span, RuntimeSample.Take());
    }

    /// <summary>
    /// When the runtime profile runs (<see cref="RuntimeProfile.Env"/>), drops what it sampled before the scope and
    /// tags <paramref name="span"/> on dispose with the allocations by type and the contentions sampled during it.
    /// </summary>
    public static IDisposable? ProfileTags(Activity? span)
    {
        if (span is null || RuntimeProfile.Current is not { } profile)
        {
            return null;
        }
        profile.Drain();
        return new ProfileScope(span, profile);
    }

    private sealed class ProfileScope(Activity span, RuntimeProfile profile) : IDisposable
    {
        public void Dispose() => profile.Tag(span);
    }

    private sealed class RuntimeScope(Activity? span, RuntimeSample began) : IDisposable
    {
        public void Dispose()
        {
            if (span is not null)
            {
                RuntimeSample.Take().Tag(span, began);
            }
        }
    }

    private sealed class PhaseScope(Activity? span, ReadTallyScope reads) : IDisposable
    {
        private readonly RuntimeSample _began = RuntimeSample.Take();

        public void Dispose()
        {
            reads.Dispose();
            TagReads(span, reads.Tally);
            if (span is not null)
            {
                RuntimeSample.Take().Tag(span, _began);
            }
            EndSpan(span);
        }
    }

    /// <summary>
    /// The process-wide runtime counters a phase span is tagged with, as deltas over the phase: GC pause time and
    /// collections by generation, lock contentions, process CPU (the game's included, which shares the process), the
    /// thread pool's queue and threads, and the phase's own thread CPU when it began and ended on one thread.
    /// </summary>
    private readonly record struct RuntimeSample(TimeSpan GcPause, int Gen0, int Gen1, int Gen2, long Contentions, TimeSpan ProcessCpu, int Thread, long ThreadCpuTicks, long Pending, int Threads)
    {
        public static RuntimeSample Take() => new(
            GC.GetTotalPauseDuration(), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), Monitor.LockContentionCount,
            Environment.CpuUsage.TotalTime, Environment.CurrentManagedThreadId, ThreadCpu(), ThreadPool.PendingWorkItemCount, ThreadPool.ThreadCount);

        public void Tag(Activity span, RuntimeSample began)
        {
            span.SetTag("rimgovernor.gc_pause_ms", Math.Round((GcPause - began.GcPause).TotalMilliseconds, 1));
            span.SetTag("rimgovernor.gc", $"{Gen0 - began.Gen0}/{Gen1 - began.Gen1}/{Gen2 - began.Gen2}");
            span.SetTag("rimgovernor.lock_contentions", Contentions - began.Contentions);
            span.SetTag("rimgovernor.process_cpu_ms", Math.Round((ProcessCpu - began.ProcessCpu).TotalMilliseconds));
            span.SetTag("rimgovernor.pool_pending", began.Pending);
            span.SetTag("rimgovernor.pool_threads", began.Threads);
            if (Thread == began.Thread && ThreadCpuTicks >= 0 && began.ThreadCpuTicks >= 0)
            {
                span.SetTag("rimgovernor.thread_cpu_ms", Math.Round((ThreadCpuTicks - began.ThreadCpuTicks) / 10_000.0, 1));
            }
        }

        // The current OS thread's kernel plus user time in 100 ns ticks; -1 off Windows.
        private static long ThreadCpu() =>
            OperatingSystem.IsWindows() && GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user) ? kernel + user : -1;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

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
