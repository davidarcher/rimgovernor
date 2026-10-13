using System.Globalization;
using System.Text.Json;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// One case's baseline price (Go <c>cost.Row</c>): the wall time of its run and the boot time inside it (zero when the
/// run attached to a kept process).
/// </summary>
public readonly record struct CostRow(TimeSpan Wall, TimeSpan Boot);

/// <summary>
/// Totals a name set (Go <c>cost.Summary</c>): the timed cases' wall and boot and how many names the baseline does not
/// time.
/// </summary>
public readonly record struct CostSummary(int Timed, int Untimed, TimeSpan Wall, TimeSpan Boot)
{
    /// <summary>Renders the total on one line: "12 cases, 41m10s wall (boot 2m3s), 3 untimed".</summary>
    public override string ToString()
    {
        var count = Timed + Untimed;
        var parts = new List<string> { string.Create(CultureInfo.InvariantCulture, $"{count} case{(count == 1 ? "" : "s")}") };
        if (Timed > 0)
        {
            parts.Add($"{CostTable.Format(Wall)} wall (boot {CostTable.Format(Boot)})");
        }

        if (Untimed > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{Untimed} untimed"));
        }

        return string.Join(", ", parts);
    }
}

/// <summary>
/// Prices acceptance cases from an earlier run's timings (Go package <c>cost</c>) so an agent choosing between the
/// cases a change owes can see that one costs 4 minutes and another 18; <c>acceptance list -cost</c> uses it.
/// A baseline is either a suite result.json (one wall_ms/boot_ms row per case, the file <c>acceptance suite
/// -baseline</c> also takes) or a metrics series (metrics.jsonl, <see cref="SeriesRow"/> lines), where a case's price is
/// the median of its last <see cref="MetricSeries.DriftWindow"/> passing rows. Cases the baseline does not time are
/// untimed and priced as unknown, never zero.
/// </summary>
public sealed class CostTable
{
    private readonly Dictionary<string, CostRow> _rows = new(StringComparer.Ordinal);

    private CostTable(string path)
    {
        Path = path;
    }

    /// <summary>The baseline file the table was read from.</summary>
    public string Path { get; }

    /// <summary>Reads a baseline: a metrics series when <paramref name="path"/> ends in .jsonl, a suite result.json otherwise.</summary>
    public static CostTable Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ? LoadSeries(path) : LoadSuite(path);
    }

    private static CostTable LoadSuite(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new AcceptanceException($"baseline: {e.Message}", e);
        }

        var table = new CostTable(path);
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("the document is not an object");
            }

            if (doc.RootElement.TryGetProperty("cases", out var cases) && cases.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in cases.EnumerateArray())
                {
                    var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                    var wall = Number(item, "wall_ms");
                    if (name.Length == 0 || wall <= 0)
                    {
                        continue;
                    }

                    table._rows[name] = new CostRow(Ms(wall), Ms(Number(item, "boot_ms")));
                }
            }
        }
        catch (JsonException e)
        {
            throw new AcceptanceException($"baseline: {path} is not a suite result.json: {e.Message}", e);
        }

        return table;
    }

    private static double Number(JsonElement item, string key) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : 0;

    private static CostTable LoadSeries(string path)
    {
        if (!File.Exists(path))
        {
            throw new AcceptanceException($"baseline: {path}: no such file");
        }

        IReadOnlyList<SeriesRow> rows;
        try
        {
            rows = MetricSeries.ReadSeries(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new AcceptanceException($"baseline: {e.Message}", e);
        }

        var walls = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        var boots = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!row.Passed)
            {
                continue;
            }

            if (!row.Metrics.Values.TryGetValue("wall_ms", out var wall) || wall <= 0)
            {
                continue;
            }

            if (!walls.TryGetValue(row.Case, out var w))
            {
                walls[row.Case] = w = [];
                boots[row.Case] = [];
            }

            w.Add(wall);
            boots[row.Case].Add(row.Metrics.Values.GetValueOrDefault("boot_ms"));
        }

        var table = new CostTable(path);
        foreach (var (name, w) in walls)
        {
            var b = boots[name];
            if (w.Count > MetricSeries.DriftWindow)
            {
                w.RemoveRange(0, w.Count - MetricSeries.DriftWindow);
                b.RemoveRange(0, b.Count - MetricSeries.DriftWindow);
            }

            table._rows[name] = new CostRow(Ms(Median(w)), Ms(Median(b)));
        }

        return table;
    }

    private static TimeSpan Ms(double v) => TimeSpan.FromTicks((long)(v * TimeSpan.TicksPerMillisecond));

    private static double Median(List<double> values)
    {
        var sorted = values.ToArray();
        Array.Sort(sorted);
        var n = sorted.Length;
        if (n == 0)
        {
            return 0;
        }

        return n % 2 == 1 ? sorted[n / 2] : (sorted[(n / 2) - 1] + sorted[n / 2]) / 2;
    }

    /// <summary>The price of <paramref name="name"/> and whether the baseline times it. A null table times nothing.</summary>
    public static bool Of(CostTable? table, string name, out CostRow row)
    {
        row = default;
        return table is not null && table._rows.TryGetValue(name, out row);
    }

    /// <summary>The price of <paramref name="name"/> and whether the baseline times it.</summary>
    public bool Of(string name, out CostRow row) => _rows.TryGetValue(name, out row);

    /// <summary>The cases the baseline times, sorted.</summary>
    public IReadOnlyList<string> Names()
    {
        var names = new List<string>(_rows.Keys);
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    /// <summary>Prices <paramref name="names"/> against <paramref name="table"/> (null times nothing).</summary>
    public static CostSummary Summarize(CostTable? table, IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        int timed = 0, untimed = 0;
        TimeSpan wall = TimeSpan.Zero, boot = TimeSpan.Zero;
        foreach (var name in names)
        {
            if (!Of(table, name, out var row))
            {
                untimed++;
                continue;
            }

            timed++;
            wall += row.Wall;
            boot += row.Boot;
        }

        return new CostSummary(timed, untimed, wall, boot);
    }

    /// <summary>Renders a duration to the second ("4m12s", "18s").</summary>
    public static string Format(TimeSpan d)
    {
        var total = (long)Math.Round(d.TotalSeconds, MidpointRounding.AwayFromZero);
        var sign = total < 0 ? "-" : "";
        total = Math.Abs(total);
        var h = total / 3600;
        var m = total % 3600 / 60;
        var s = total % 60;
        if (h > 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{sign}{h}h{m}m{s}s");
        }

        return m > 0 ? string.Create(CultureInfo.InvariantCulture, $"{sign}{m}m{s}s") : string.Create(CultureInfo.InvariantCulture, $"{sign}{s}s");
    }
}
