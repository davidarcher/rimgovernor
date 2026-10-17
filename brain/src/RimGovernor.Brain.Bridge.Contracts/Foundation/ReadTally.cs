using RimGovernor.Brain.Telemetry;
using TelemetryDecision = RimGovernor.Brain.Telemetry.Decision;

namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>
/// Counts the native round trips issued under one scope. Every native call the host makes
/// while a tally is attached (<see cref="Begin"/>, <see cref="ReadTallyScope.WithReadTally"/>) is counted by the
/// native facade method it called (<see cref="ReadTallyRules.CountNativeRead"/>), errors included. A scheduler step
/// attaches one at entry so the reads a full planner composition costs are visible per step and, through
/// <see cref="Publish"/>, in the OTLP log ring the throughput profiler summarizes. It is safe for concurrent use;
/// planners run in parallel under the same step scope.
/// </summary>
public sealed class ReadTally
{
    private static readonly AsyncLocal<ReadTally?> s_current = new();

    private readonly object _lock = new();
    private readonly Dictionary<string, ulong> _counts = new(StringComparer.Ordinal);
    private ulong _total;

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
    /// is replaced, never nested: each step reports its own reads.
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
    }

    /// <summary>
    /// Writes the tally as one decision row of <paramref name="d"/>'s kind (a <c>clock_step</c> or a <c>dispatch</c>):
    /// its verdict, reason, target and duration, with its attrs plus the tally's <c>reads</c> and <c>tools</c>. A
    /// tally that counted no call is a no-op; the profiler (SummarizePhases) aggregates the rows.
    /// </summary>
    public void Publish(TelemetryEmitter emitter, TelemetryDecision d)
    {
        ArgumentNullException.ThrowIfNull(emitter);
        ArgumentNullException.ThrowIfNull(d);
        ulong total;
        var tools = new Dictionary<string, object?>(StringComparer.Ordinal);
        lock (_lock)
        {
            total = _total;
            foreach (var (tool, n) in _counts)
            {
                tools[tool] = n;
            }
        }

        if (total == 0)
        {
            return;
        }

        var attrs = new Dictionary<string, object?>(d.Attrs, StringComparer.Ordinal)
        {
            ["reads"] = total,
            ["tools"] = tools,
        };
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
}
