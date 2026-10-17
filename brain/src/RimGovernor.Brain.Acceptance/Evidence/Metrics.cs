using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// Every result.json carries a flat "metrics" block of numbers under stable names, so a run's cost is comparable
/// across runs without reading the nested report: the timing fields, the wait statistics, the native round trips
/// and pause costs (from the exported OTEL metrics rings, see <c>MetricsOtel.cs</c>), the service launches and the
/// evidence size. Finalize computes it from the report and the output directory; cases add nothing.
/// <c>acceptance run</c> and <c>acceptance suite</c> append one row per case to an append-only series
/// (<see cref="MetricSeries"/>) and flag the metrics that drifted past their rule against the series' trailing median.
/// </summary>
public sealed class Metrics
{
    /// <summary>The report field holding the block.</summary>
    public const string MetricsKey = "metrics";

    /// <summary>The block's keys in report order; every block carries all of them (0 when the source is absent, e.g. no service ran).</summary>
    public static readonly IReadOnlyList<string> MetricNames =
    [
        "wall_ms", "boot_ms", "ticks_advanced", "wall_tps",
        "waits", "waits_stalled", "max_quiet_ms",
        "native_calls", "native_errors",
        "native_queue_ms_mean", "native_exec_ms_mean",
        "service_launches", "evidence_bytes",
    ];

    /// <summary>The block: metric name to value.</summary>
    public Dictionary<string, double> Values { get; } = [];

    /// <summary>
    /// Derives the block from a finalized report (its timing fields and wait_stats) and the output directory's
    /// recordings and evidence. It is total: a missing recording or unreadable directory leaves that part at zero.
    /// </summary>
    public static Metrics Compute(Report report, string output)
    {
        ArgumentNullException.ThrowIfNull(report);
        var m = new Metrics();
        foreach (var name in MetricNames)
        {
            m.Values[name] = 0;
        }

        var result = ReportTiming.Of(report);
        m.Values["wall_ms"] = result.WallMs ?? 0;
        m.Values["boot_ms"] = result.BootMs ?? 0;
        m.Values["ticks_advanced"] = result.TicksAdvanced ?? 0;
        m.Values["wall_tps"] = result.WallTps ?? 0;
        if (result.WaitStats is { } stats)
        {
            m.Values["waits"] = stats.Waits;
            m.Values["waits_stalled"] = stats.Stalled;
            m.Values["max_quiet_ms"] = stats.MaxQuietMs;
        }

        var launches = 0;
        foreach (var (key, value) in report.Data)
        {
            if ((key == "service" || key.StartsWith("service_", StringComparison.Ordinal)) && value is JsonObject)
            {
                launches++;
            }
        }

        m.Values["service_launches"] = launches;
        OtelTotals.Read(output).Fill(m);
        m.Values["evidence_bytes"] = EvidenceBytes(output);
        return m;
    }

    /// <summary>Reads a report's block as Metrics; null when the report has none.</summary>
    internal static Metrics? Of(Report report) => Of(CaseResult.Of(report).Metrics);

    /// <summary>Reads a block object (a report decoded from JSON holds it so) as Metrics; null for none.</summary>
    internal static Metrics? Of(JsonObject? block)
    {
        if (block is null)
        {
            return null;
        }

        var m = new Metrics();
        foreach (var (name, value) in block)
        {
            m.Values[name] = MetricValue(value);
        }

        return m;
    }

    /// <summary>A block number as decoded JSON holds it (a number or a numeric string); an absent or non-numeric value is 0.</summary>
    private static double MetricValue(JsonNode? value)
    {
        if (value is not JsonValue json)
        {
            return 0;
        }

        if (json.TryGetValue<double>(out var d))
        {
            return d;
        }

        if (json.TryGetValue<long>(out var l))
        {
            return l;
        }

        return json.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    /// <summary>The block as a JSON object, member order as in <see cref="MetricNames"/> then any others by name.</summary>
    public JsonNode ToJson()
    {
        var block = new JsonObject();
        foreach (var name in MetricNames)
        {
            if (Values.TryGetValue(name, out var value))
            {
                block.Add(name, value);
            }
        }

        foreach (var (name, value) in Values.Where(p => !MetricNames.Contains(p.Key, StringComparer.Ordinal)).OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            block.Add(name, value);
        }

        return block;
    }

    // The size of every file under output; result.json is written after the block, so it never counts.
    private static long EvidenceBytes(string output)
    {
        long total = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(path).Length;
                }
                catch (IOException)
                {
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return total;
    }
}
