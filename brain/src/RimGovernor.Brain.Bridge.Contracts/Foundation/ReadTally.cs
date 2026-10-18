using RimGovernor.Brain.Telemetry;
using TelemetryDecision = RimGovernor.Brain.Telemetry.Decision;

namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>
/// Counts the native round trips issued under one scope. Every native call the host makes
/// while a tally is attached (<see cref="Begin"/>, <see cref="ReadTallyScope.WithReadTally"/>) is counted by the
/// native facade method it called (<see cref="ReadTallyRules.CountNativeRead"/>), errors included, and every
/// game-thread hop adds how long it waited for the game thread and how long it ran there
/// (<see cref="ReadTallyRules.CountHop"/>). A scheduler step attaches one at entry so the reads a full planner
/// composition costs are visible per step and, through <see cref="Publish"/>, in the OTLP log ring the throughput
/// profiler summarizes; each planner and each family phase nests one under it
/// (<see cref="ReadTallyScope.Nested"/>), which forwards to the step's. It is safe for concurrent use; planners
/// run in parallel under the same step scope.
/// </summary>
public sealed class ReadTally(ReadTally? parent = null)
{
    private static readonly AsyncLocal<ReadTally?> s_current = new();

    private readonly object _lock = new();
    private readonly Dictionary<string, ulong> _counts = new(StringComparer.Ordinal);
    private ulong _total;
    private double _waitMs;
    private double _runMs;

    /// <summary>The tally attached to the current async flow, or null.</summary>
    internal static ReadTally? Current => s_current.Value;

    /// <summary>The number of round trips tallied so far.</summary>
    public ulong Total
    {
        get
        {
            lock (_lock)
            {
                return _total;
            }
        }
    }

    /// <summary>
    /// Attaches this tally to the current async flow until the returned scope is disposed. A tally already attached
    /// is replaced: a step reports its own reads, and a nested tally forwards each count to its parent.
    /// </summary>
    public IDisposable Begin()
    {
        var previous = s_current.Value;
        s_current.Value = this;
        return new Restore(previous);
    }

    /// <summary>Counts one native round trip of <paramref name="tool"/> (the native facade method called).</summary>
    public void Add(string tool)
    {
        lock (_lock)
        {
            _counts[tool] = _counts.GetValueOrDefault(tool) + 1;
            _total++;
        }
        parent?.Add(tool);
    }

    /// <summary>Adds one game-thread hop: <paramref name="waitMs"/> queued for the game thread, <paramref name="runMs"/> running on it.</summary>
    public void AddHop(double waitMs, double runMs)
    {
        lock (_lock)
        {
            _waitMs += waitMs;
            _runMs += runMs;
        }
        parent?.AddHop(waitMs, runMs);
    }

    /// <summary>The tally's reads, hop wait and hop run time, and per-tool counts, as row or span attributes.</summary>
    public Dictionary<string, object?> Attrs()
    {
        var tools = new Dictionary<string, object?>(StringComparer.Ordinal);
        lock (_lock)
        {
            foreach (var (tool, n) in _counts)
            {
                tools[tool] = n;
            }
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["reads"] = _total,
                ["hop_wait_ms"] = Math.Round(_waitMs, 1),
                ["hop_run_ms"] = Math.Round(_runMs, 1),
                ["tools"] = tools,
            };
        }
    }

    /// <summary>
    /// Writes the tally as one decision row of <paramref name="d"/>'s kind (a <c>clock_step</c> or a <c>dispatch</c>):
    /// its verdict, reason, target and duration, with its attrs plus the tally's <see cref="Attrs"/>. A
    /// tally that counted no call is a no-op; the profiler (SummarizePhases) aggregates the rows.
    /// </summary>
    public void Publish(TelemetryEmitter emitter, TelemetryDecision d)
    {
        ArgumentNullException.ThrowIfNull(emitter);
        ArgumentNullException.ThrowIfNull(d);
        if (Total == 0)
        {
            return;
        }

        var attrs = new Dictionary<string, object?>(d.Attrs, StringComparer.Ordinal);
        foreach (var (key, value) in Attrs())
        {
            attrs[key] = value;
        }
        emitter.Decide(d with { Attrs = attrs });
    }

    private sealed class Restore(ReadTally? previous) : IDisposable
    {
        public void Dispose() => s_current.Value = previous;
    }
}

public static class ReadTallyRules
{

    /// <summary>The native call layer's hook: counts one round trip of <paramref name="nativeTool"/> on the attached tally, if any.</summary>
    public static void CountNativeRead(string nativeTool) => ReadTally.Current?.Add(nativeTool);

    /// <summary>The game-thread boundary's hook: adds one hop's queue wait and run time to the attached tally, if any.</summary>
    public static void CountHop(double waitMs, double runMs) => ReadTally.Current?.AddHop(waitMs, runMs);
}

/// <summary>A tally attached by <see cref="ReadTallyScope.WithReadTally"/>; disposing it restores the previous one.</summary>
public sealed class ReadTallyScope(ReadTally tally, IDisposable restore) : IDisposable
{
    public ReadTally Tally { get; } = tally;

    public void Dispose() => restore.Dispose();

    /// <summary>
    /// Attaches a fresh tally to the current async flow until the scope is disposed; one
    /// already attached is replaced, never nested.
    /// </summary>
    public static ReadTallyScope WithReadTally()
    {
        var tally = new ReadTally();
        return new ReadTallyScope(tally, tally.Begin());
    }

    /// <summary>Attaches a fresh tally that forwards every count to the one already attached (if any), until the scope is disposed.</summary>
    public static ReadTallyScope Nested()
    {
        var tally = new ReadTally(ReadTally.Current);
        return new ReadTallyScope(tally, tally.Begin());
    }
}
