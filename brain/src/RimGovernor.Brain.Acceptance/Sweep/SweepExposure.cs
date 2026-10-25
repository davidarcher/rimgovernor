using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
/// bootstrap range, and the down rate (down or dead) with its 95% percentile-bootstrap interval. Both resample whole
/// raids within each fixture and keep the fixture mixture fixed rather than overweighting fixtures with more seeds.
/// </summary>
public static class SweepExposure
{
    /// <summary>Bootstrap resamples per cell.</summary>
    internal const int Resamples = 2000;

    /// <summary>The bootstrap draws are seeded, so one set of rows always yields one table.</summary>
    private const int BootstrapSeed = 3541;

    private static readonly JsonSerializerOptions RowJson = new(JsonSerializerDefaults.Web);

    /// <summary>The rows of a served fight: one per seed and staged colonist. Failed seeds and runs with no clock, contact or planner orders have none.</summary>
    internal static IEnumerable<ExposureRow> Rows(Sweep sweep)
    {
        foreach (var seed in sweep.Rows)
        {
            if (!string.IsNullOrEmpty(seed.Error) || seed.Metrics is not { Ticks: > 0, FirstContactTick: >= 0, OrdersIssued: > 0, Colonists: { } colonists } m)
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
        var fixtures = trials.GroupBy(t => t.Fixture, StringComparer.Ordinal)
            .OrderBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => f.GroupBy(t => t.Seed).OrderBy(r => r.Key)
                .Select(r => new RaidSample(r.Count(), r.Sum(t => (long)t.Hits), r.Count(t => t.Downed || t.Dead), r.Any(t => t.Hits > 0)))
                .ToArray())
            .ToArray();
        // One equally sized raid cohort per fixture: its role-pawn count, not its accumulated sampling rounds,
        // determines the fixture's contribution to the per-colonist mixture. Bootstrap weights remain fixed.
        var weights = fixtures.Select(f => f.Average(r => r.Pawns)).ToArray();
        var totalWeight = weights.Sum();
        var meanHits = 0.0;
        var downRate = 0.0;
        for (var f = 0; f < fixtures.Length; f++)
        {
            var pawns = fixtures[f].Sum(r => r.Pawns);
            meanHits += weights[f] * fixtures[f].Sum(r => r.Hits) / pawns;
            downRate += weights[f] * fixtures[f].Sum(r => r.Down) / pawns;
        }

        meanHits /= totalWeight;
        downRate /= totalWeight;
        var (hitsMeans, downMeans) = Bootstrap(fixtures, weights, totalWeight);
        var (hitsLo, hitsHi) = Interval(hitsMeans, 0.05, meanHits);
        var (downLo, downHi) = Interval(downMeans, 0.025, downRate);
        return new ExposureCell(
            bin,
            role,
            trials.Length,
            Round(meanHits),
            Round(hitsLo),
            Round(hitsHi),
            Round(downRate),
            Round(downLo),
            Round(downHi),
            fixtures.Sum(f => f.Length),
            fixtures.Sum(f => f.Count(r => r.Hit)));
    }

    private sealed record RaidSample(int Pawns, long Hits, int Down, bool Hit);

    private static (double[] Hits, double[] Down) Bootstrap(RaidSample[][] fixtures, double[] weights, double totalWeight)
    {
        var random = new Random(BootstrapSeed);
        var hitsMeans = new double[Resamples];
        var downMeans = new double[Resamples];
        for (var b = 0; b < Resamples; b++)
        {
            for (var f = 0; f < fixtures.Length; f++)
            {
                var raids = fixtures[f];
                var pawns = 0;
                var hits = 0L;
                var down = 0;
                for (var i = 0; i < raids.Length; i++)
                {
                    var raid = raids[random.Next(raids.Length)];
                    pawns += raid.Pawns;
                    hits += raid.Hits;
                    down += raid.Down;
                }

                hitsMeans[b] += weights[f] * hits / pawns;
                downMeans[b] += weights[f] * down / pawns;
            }

            hitsMeans[b] /= totalWeight;
            downMeans[b] /= totalWeight;
        }

        return (hitsMeans, downMeans);
    }

    private static (double Lo, double Hi) Interval(double[] means, double tail, double observed)
    {
        Array.Sort(means);
        return (Math.Min(observed, means[(int)Math.Floor(tail * (means.Length - 1))]),
            Math.Max(observed, means[(int)Math.Ceiling((1 - tail) * (means.Length - 1))]));
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
            _ = text.Append(CultureInfo.InvariantCulture, $"  {cell.Bin}/{cell.Role}: {cell.Trials} trials, {cell.Raids} raids, {cell.HitRaids} hit raids, hits per colonist ");
            _ = text.Append(Show(hits)).Append(", down rate ").Append(Show(down)).Append(' ').Append(string.Join(' ', hits.Findings)).Append('\n');
        }

        return text.ToString();
    }

    private static string Show(ExposureReading reading) =>
        reading is ExposureReading.Calibrated { Range: var r } ? string.Create(CultureInfo.InvariantCulture, $"{r.Mean:F2} [{r.Lo:F2}, {r.Hi:F2}]") : "uncalibrated";
}
