using System.Globalization;
using System.Text;
using RimGovernor.Brain.Telemetry;
using TelemetryDecision = RimGovernor.Brain.Telemetry.Decision;

namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>
/// Go's <c>ReadTally</c>: counts the native round trips issued under one scope. Every native call the host makes
/// while a tally is attached (<see cref="Begin"/>, <see cref="BridgeFunctions.WithReadTally"/>) is counted by the
/// native tool it called (<see cref="BridgeFunctions.CountNativeRead"/>), errors included. A scheduler step attaches
/// one at entry so the reads a full planner composition costs are visible per step and, through
/// <see cref="Publish"/>, in the OTLP log ring the throughput profiler summarizes. It is safe for concurrent use;
/// planners run in parallel under the same step scope.
/// </summary>
/// <remarks>
/// The one-off schema fetches (<see cref="SchemaTool"/>) a host issues before a tool's first call are counted apart,
/// as <see cref="Schema"/>: they are a session's startup cost, not a step's reads, and would otherwise make the first
/// full step look several reads heavier than any other.
/// </remarks>
public sealed class ReadTally
{
    /// <summary>The wrapper call a native tool's schema is fetched with.</summary>
    public const string SchemaTool = "games_tool_detail";

    private static readonly AsyncLocal<ReadTally?> s_current = new();

    private readonly object _lock = new();
    private readonly Dictionary<string, ulong> _counts = new(StringComparer.Ordinal);
    private ulong _total;
    private ulong _schema;
    private bool _used;

    /// <summary>The tally attached to the current async flow, or null.</summary>
    internal static ReadTally? Current => s_current.Value;

    /// <summary>The number of schema fetches tallied so far, apart from <see cref="Total"/>.</summary>
    public ulong Schema
    {
        get
        {
            lock (_lock)
            {
                return _schema;
            }
        }
    }

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

    /// <summary>Counts one native round trip of <paramref name="tool"/> (the inner <c>rimgovernor/*</c> method, or the wrapper name otherwise).</summary>
    public void Add(string tool)
    {
        lock (_lock)
        {
            if (tool == SchemaTool)
            {
                _schema++;
            }
            else
            {
                _counts[tool] = _counts.GetValueOrDefault(tool) + 1;
                _total++;
            }

            _used = true;
        }
    }

    /// <summary>A copy of the per-tool tally.</summary>
    public Dictionary<string, ulong> Counts()
    {
        lock (_lock)
        {
            return new Dictionary<string, ulong>(_counts, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// The tally as <c>total=N tool=n ...</c> with tools in descending count order, the one-line form the clock
    /// debug log prints per step.
    /// </summary>
    public override string ToString()
    {
        var counts = Counts();
        var names = counts.Keys.ToList();
        names.Sort((a, b) =>
        {
            var byCount = counts[b].CompareTo(counts[a]);
            return byCount != 0 ? byCount : string.CompareOrdinal(a, b);
        });
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"total={Total}");
        foreach (var tool in names)
        {
            var shown = tool.StartsWith("rimgovernor/", StringComparison.Ordinal) ? tool["rimgovernor/".Length..] : tool;
            builder.Append(CultureInfo.InvariantCulture, $" {shown}={counts[tool]}");
        }

        var schema = Schema;
        if (schema > 0)
        {
            builder.Append(CultureInfo.InvariantCulture, $" schema={schema}");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Writes the tally as one decision row of <paramref name="d"/>'s kind (a <c>clock_step</c> or a <c>dispatch</c>):
    /// its verdict, reason, target and duration, with its attrs plus the tally's <c>reads</c>, <c>tools</c> and
    /// <c>schema_fetches</c>. A tally that counted no call is a no-op; the profiler (SummarizePhases) aggregates the rows.
    /// </summary>
    public void Publish(TelemetryEmitter emitter, TelemetryDecision d)
    {
        ArgumentNullException.ThrowIfNull(emitter);
        ArgumentNullException.ThrowIfNull(d);
        bool used;
        lock (_lock)
        {
            used = _used;
        }

        if (!used)
        {
            return;
        }

        var tools = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (tool, n) in Counts())
        {
            tools[tool] = n;
        }

        var attrs = new Dictionary<string, object?>(d.Attrs, StringComparer.Ordinal)
        {
            ["reads"] = Total,
            ["tools"] = tools,
            ["schema_fetches"] = Schema,
        };
        emitter.Decide(d with { Attrs = attrs });
    }

    private sealed class Restore(ReadTally? previous) : IDisposable
    {
        public void Dispose() => s_current.Value = previous;
    }
}

public static partial class BridgeFunctions
{
    /// <summary>
    /// Go's <c>WithReadTally</c>: attaches a fresh tally to the current async flow until the scope is disposed; one
    /// already attached is replaced, never nested.
    /// </summary>
    public static ReadTallyScope WithReadTally()
    {
        var tally = new ReadTally();
        return new ReadTallyScope(tally, tally.Begin());
    }

    /// <summary>The native call layer's hook: counts one round trip of <paramref name="nativeTool"/> on the attached tally, if any.</summary>
    public static void CountNativeRead(string nativeTool) => ReadTally.Current?.Add(nativeTool);
}

/// <summary>A tally attached by <see cref="BridgeFunctions.WithReadTally"/>; disposing it restores the previous one.</summary>
public sealed class ReadTallyScope(ReadTally tally, IDisposable restore) : IDisposable
{
    public ReadTally Tally { get; } = tally;

    public void Dispose() => restore.Dispose();
}
