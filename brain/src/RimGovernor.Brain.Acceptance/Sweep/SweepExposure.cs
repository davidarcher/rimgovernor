using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Simulation.CombatMetrics;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// One colonist of one seed's raid, the row <c>acceptance sweep -exposure</c> appends and <c>-exposure-table</c> reads:
/// Ratio is raid points over defense capacity (null with no capacity, which lands in the top bin).
/// </summary>
public sealed record ExposureRow(
    [property: JsonPropertyName("fixture")] string Fixture,
    [property: JsonPropertyName("seed")] int Seed,
    [property: JsonPropertyName("ratio")] double? Ratio,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("hits")] int Hits,
    [property: JsonPropertyName("downed")] bool Downed,
    [property: JsonPropertyName("dead")] bool Dead);

/// <summary>
/// The exposure calibration (harm model, step 3): the per-colonist rows of a sweep and the table built from them. The cells are
/// <see cref="RaidExposure"/>'s ratio bins by role; each holds the mean hits per colonist per raid with a 90% percentile
/// bootstrap range, and the down rate (down or dead) with its 95% Wilson interval.
/// </summary>
public static class SweepExposure
{
    /// <summary>Bootstrap resamples per cell.</summary>
    internal const int Resamples = 2000;

    /// <summary>The bootstrap draws are seeded, so one set of rows always yields one table.</summary>
    private const int BootstrapSeed = 3541;

    private static readonly JsonSerializerOptions RowJson = new(JsonSerializerDefaults.Web);

    /// <summary>The rows of a sweep: one per seed and staged colonist. A seed that failed, or a run scored without a stamped staging, has none.</summary>
    internal static IEnumerable<ExposureRow> Rows(Sweep sweep)
    {
        foreach (var seed in sweep.Rows)
        {
            if (seed.Metrics is not { Colonists: { } colonists } m)
            {
                continue;
            }

            double? ratio = m.DefenseCapacity > 0 ? m.RaidPoints / m.DefenseCapacity : null;
            foreach (var c in colonists)
            {
                yield return new ExposureRow(sweep.Fixture, seed.Seed, ratio, c.Role, c.Hits, c.Downed, c.Dead);
            }
        }
    }

    /// <summary>Appends the rows, one JSON object per line.</summary>
    internal static void Append(string path, IEnumerable<ExposureRow> rows)
    {
        var text = new StringBuilder();
        foreach (var row in rows)
        {
            _ = text.Append(JsonSerializer.Serialize(row, RowJson)).Append('\n');
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, text.ToString());
    }

    internal static List<ExposureRow> ReadRows(string path)
    {
        var rows = new List<ExposureRow>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length > 0)
            {
                rows.Add(JsonSerializer.Deserialize<ExposureRow>(line, RowJson) ?? throw new InvalidDataException($"{path}: empty row"));
            }
        }

        return rows;
    }

    /// <summary>The table the rows calibrate: a cell for every bin and role that has a trial, in bin then role order.</summary>
    public static ExposureTable Build(IReadOnlyList<ExposureRow> rows)
    {
        var cells = new List<ExposureCell>();
        foreach (var bin in RaidExposure.Bins)
        {
            foreach (var role in ExposureRoles.All)
            {
                var trials = rows.Where(r => RaidExposure.BinOf(r.Ratio ?? double.PositiveInfinity) == bin && r.Role == role).ToArray();
                if (trials.Length > 0)
                {
                    cells.Add(Cell(bin, role, trials));
                }
            }
        }

        return new ExposureTable(RaidExposure.Schema, cells);
    }

    private static ExposureCell Cell(string bin, string role, ExposureRow[] trials)
    {
        var hits = trials.Select(t => t.Hits).ToArray();
        var down = trials.Count(t => t.Downed || t.Dead);
        var (hitsLo, hitsHi) = Bootstrap(hits);
        var (downLo, downHi) = HuntingQualification.Wilson(down, trials.Length);
        return new ExposureCell(
            bin,
            role,
            trials.Length,
            Round(hits.Average()),
            Round(hitsLo),
            Round(hitsHi),
            Round((double)down / trials.Length),
            Round(Math.Max(0, downLo)),
            Round(Math.Min(1, downHi)));
    }

    // The 5th and 95th percentiles of the resampled mean: a 90% range.
    private static (double Lo, double Hi) Bootstrap(int[] hits)
    {
        var random = new Random(BootstrapSeed);
        var means = new double[Resamples];
        for (var b = 0; b < means.Length; b++)
        {
            var sum = 0L;
            for (var i = 0; i < hits.Length; i++)
            {
                sum += hits[random.Next(hits.Length)];
            }

            means[b] = (double)sum / hits.Length;
        }

        Array.Sort(means);
        return (means[(int)Math.Floor(0.05 * (means.Length - 1))], means[(int)Math.Ceiling(0.95 * (means.Length - 1))]);
    }

    private static double Round(double v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);

    /// <summary>
    /// <c>acceptance sweep -exposure-table &lt;rows.jsonl&gt; &lt;exposure.json&gt;</c>: builds the table from the rows, writes it, and
    /// reads the written file back through <see cref="RaidExposure"/>, printing what each cell answers.
    /// </summary>
    internal static int Table(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length != 2)
        {
            stderr.WriteLine("usage: acceptance sweep -exposure-table <rows.jsonl> <exposure.json>");
            return 2;
        }

        List<ExposureRow> rows;
        try
        {
            rows = ReadRows(Path.GetFullPath(args[0]));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            stderr.WriteLine(e.Message);
            return 2;
        }

        if (rows.Count == 0)
        {
            stderr.WriteLine($"{args[0]}: no exposure rows");
            return 2;
        }

        var path = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, RaidExposure.Serialize(Build(rows)));
        using var stream = File.OpenRead(path);
        var table = RaidExposure.Parse(stream);
        stdout.Write(Report(table));
        stdout.WriteLine($"  written {path}");
        return 0;
    }

    /// <summary>What the table answers for each of its cells, at the lower edge of the cell's ratio bin.</summary>
    internal static string Report(ExposureTable table)
    {
        var text = new StringBuilder();
        foreach (var cell in table.Cells)
        {
            var bin = RaidExposure.Bins.ToList().IndexOf(cell.Bin);
            var ratio = bin == 0 ? 0 : RaidExposure.Edges[bin - 1];
            var hits = RaidExposure.HitsPerColonist(table, ratio, cell.Role);
            var down = RaidExposure.DownRate(table, ratio, cell.Role);
            _ = text.Append(CultureInfo.InvariantCulture, $"  {cell.Bin}/{cell.Role}: {cell.Trials} trials, hits per colonist ");
            _ = text.Append(Show(hits)).Append(", down rate ").Append(Show(down)).Append(' ').Append(string.Join(' ', hits.Findings)).Append('\n');
        }

        return text.ToString();
    }

    private static string Show(ExposureReading reading) =>
        reading is ExposureReading.Calibrated { Range: var r } ? string.Create(CultureInfo.InvariantCulture, $"{r.Mean:F2} [{r.Lo:F2}, {r.Hi:F2}]") : "uncalibrated";
}
