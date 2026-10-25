using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace RimGovernor.Brain.Runtime;

/// <summary>
/// An in-process listener on the runtime's own event source that samples allocations by type (GCAllocationTick,
/// one event per ~100 KB allocated, naming the type that crossed the mark), counts the assemblies the loader is
/// asked for and the exceptions thrown, and totals lock contentions and their wait. It runs only when <see cref="Env"/> is set, since the GC keyword at verbose level costs the whole process.
/// The runtime dispatches the events on its own thread, so they cannot be attributed to the span that allocated;
/// <see cref="Drain"/> takes everything since the previous drain, which the scheduler does once per step.
/// The process is shared with the game, so the samples include its allocations as well as the brain's.
/// </summary>
internal sealed class RuntimeProfile : EventListener
{
    public const string Env = "RIMGOVERNOR_RUNTIME_PROFILE";

    private const string RuntimeSource = "Microsoft-Windows-DotNETRuntime";
    private const EventKeywords GcKeyword = (EventKeywords)0x1;
    private const EventKeywords ContentionKeyword = (EventKeywords)0x4000;
    private const EventKeywords AssemblyLoaderKeyword = (EventKeywords)0x4;
    private const EventKeywords ExceptionKeyword = (EventKeywords)0x8000;

    /// <summary>The process's profile, or null when <see cref="Env"/> is unset.</summary>
    public static readonly RuntimeProfile? Current = string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Env)) ? null : new RuntimeProfile();

    private readonly object _gate = new();
    private Dictionary<string, long> _bytes = new(StringComparer.Ordinal);
    private Dictionary<string, long> _threadBytes = new(StringComparer.Ordinal);
    private Dictionary<string, long> _loads = new(StringComparer.Ordinal);
    private Dictionary<string, long> _exceptions = new(StringComparer.Ordinal);
    private long _contentions;
    private double _contentionNs;

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == RuntimeSource)
        {
            EnableEvents(source, EventLevel.Verbose, GcKeyword | ContentionKeyword | AssemblyLoaderKeyword | ExceptionKeyword);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        var name = e.EventName ?? "";
        if (name.StartsWith("GCAllocationTick", StringComparison.Ordinal))
        {
            var type = Payload(e, "TypeName") as string ?? "?";
            var bytes = Convert.ToInt64(Payload(e, "AllocationAmount64") ?? Payload(e, "AllocationAmount") ?? 0L);
            Count(ref _bytes, type, bytes);
            // The OS thread that allocated, so the game thread's share (its own ticks and the native reads) shows apart
            // from the brain's pool threads.
            Count(ref _threadBytes, e.OSThreadId + " " + type, bytes);
        }
        else if (name.StartsWith("AssemblyLoadStart", StringComparison.Ordinal))
        {
            Count(ref _loads, Payload(e, "AssemblyName") as string ?? "?");
        }
        else if (name.StartsWith("ExceptionThrown_", StringComparison.Ordinal))
        {
            // By type and the message's first words, digits dropped, so one throw site's rows add up.
            var message = new string([.. (Payload(e, "ExceptionMessage") as string ?? "").Where(c => !char.IsAsciiDigit(c)).Take(80)]);
            Count(ref _exceptions, (Payload(e, "ExceptionType") as string ?? "?") + ": " + message);
        }
        else if (name.StartsWith("ContentionStop", StringComparison.Ordinal))
        {
            var ns = Convert.ToDouble(Payload(e, "DurationNs") ?? 0.0);
            lock (_gate)
            {
                _contentions++;
                _contentionNs += ns;
            }
        }
    }

    // By ref so the dictionary is read under the lock, never one a drain already took.
    private void Count(ref Dictionary<string, long> counts, string key, long by = 1)
    {
        lock (_gate)
        {
            counts[key] = counts.GetValueOrDefault(key) + by;
        }
    }

    private static object? Payload(EventWrittenEventArgs e, string field)
    {
        var at = e.PayloadNames?.IndexOf(field) ?? -1;
        return at >= 0 && e.Payload is { } payload ? payload[at] : null;
    }

    /// <summary>What the listener sampled since the previous drain; every list is largest first.</summary>
    public sealed record Sample(IReadOnlyList<(string Type, long Bytes)> Allocations, IReadOnlyList<(string ThreadType, long Bytes)> ThreadAllocations, IReadOnlyList<(string Name, long Count)> Loads,
        IReadOnlyList<(string Type, long Count)> Exceptions, long Contentions, double ContentionMs);

    /// <summary>Takes and resets everything sampled since the previous drain.</summary>
    public Sample Drain()
    {
        Dictionary<string, long> bytes, threadBytes, loads, exceptions;
        long contentions;
        double ns;
        lock (_gate)
        {
            (bytes, _bytes) = (_bytes, new(StringComparer.Ordinal));
            (threadBytes, _threadBytes) = (_threadBytes, new(StringComparer.Ordinal));
            (loads, _loads) = (_loads, new(StringComparer.Ordinal));
            (exceptions, _exceptions) = (_exceptions, new(StringComparer.Ordinal));
            (contentions, _contentions) = (_contentions, 0);
            (ns, _contentionNs) = (_contentionNs, 0);
        }
        return new(Ranked(bytes), Ranked(threadBytes), Ranked(loads), Ranked(exceptions), contentions, ns / 1e6);
    }

    private static List<(string, long)> Ranked(Dictionary<string, long> counts) =>
        [.. counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).Select(c => (c.Key, c.Value))];

    /// <summary>
    /// Tags <paramref name="span"/> with a drain: total sampled KB and the top types by KB, the assemblies the loader
    /// was asked for and the exceptions thrown, by count, and contention count and wait.
    /// </summary>
    public void Tag(Activity span, int top = 15)
    {
        var sample = Drain();
        span.SetTag("rimgovernor.alloc_kb", sample.Allocations.Sum(a => a.Bytes) / 1024);
        span.SetTag("rimgovernor.alloc_top", string.Join(";", sample.Allocations.Take(top).Select(a => a.Type + "=" + a.Bytes / 1024)));
        span.SetTag("rimgovernor.alloc_thread_top", string.Join(";", sample.ThreadAllocations.Take(top * 2).Select(a => a.ThreadType + "=" + a.Bytes / 1024)));
        span.SetTag("rimgovernor.loads_top", string.Join(";", sample.Loads.Take(top).Select(l => l.Name + "=" + l.Count)));
        span.SetTag("rimgovernor.exceptions_top", string.Join(";", sample.Exceptions.Take(top).Select(x => x.Type + "=" + x.Count)));
        span.SetTag("rimgovernor.contention_events", sample.Contentions);
        span.SetTag("rimgovernor.contention_ms", Math.Round(sample.ContentionMs, 1));
    }
}
