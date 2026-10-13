using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimGovernor.Brain.Simulation.CombatMetrics;

// Port of Go internal/combatmetrics (sweep.go). Sweep files are the committed combat lab baselines
// (#2843), so they stay JSON with the Go field names.

/// <summary>One seed's run: its scored metrics and the evidence that the run started where its pair did.</summary>
public sealed record SeedRow
{
    [JsonPropertyName("seed")]
    public int Seed { get; init; }

    [JsonPropertyName("worker")]
    public int Worker { get; init; }

    /// <summary>The digest the staging read back; the same checkpoint reads the same digest for every seed.</summary>
    [JsonPropertyName("stagedDigest")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StagedDigest { get; init; }

    /// <summary>The next draws read right after the reseed: the same seed on the same checkpoint reads the same draws.</summary>
    [JsonPropertyName("runSeedDraws")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? RunSeedDraws { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }

    [JsonPropertyName("metrics")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Metrics? Metrics { get; init; }
}

/// <summary>
/// The mean of a measure over N runs with a 95% t interval. The interval is undefined (Lo and Hi equal the
/// mean) below two runs; a measure with no variance has a zero-width interval.
/// </summary>
public sealed record Stat
{
    // The two-sided 95% Student t critical value by degrees of freedom 1..30; beyond that the normal 1.96 is
    // within 2%.
    private static readonly double[] _tCrit95 =
    [
        12.706, 4.303, 3.182, 2.776, 2.571, 2.447, 2.365, 2.306, 2.262, 2.228,
        2.201, 2.179, 2.160, 2.145, 2.131, 2.120, 2.110, 2.101, 2.093, 2.086,
        2.080, 2.074, 2.069, 2.064, 2.060, 2.056, 2.052, 2.048, 2.045, 2.042,
    ];

    [JsonPropertyName("n")]
    public int N { get; init; }

    [JsonPropertyName("mean")]
    public double Mean { get; init; }

    [JsonPropertyName("sd")]
    public double SD { get; init; }

    [JsonPropertyName("lo")]
    public double Lo { get; init; }

    [JsonPropertyName("hi")]
    public double Hi { get; init; }

    /// <summary>The mean and 95% t interval of xs (Go <c>NewStat</c>).</summary>
    public static Stat NewStat(IReadOnlyList<double> xs)
    {
        if (xs.Count == 0)
        {
            return new Stat();
        }
        var mean = 0.0;
        foreach (var x in xs)
        {
            mean += x;
        }
        mean /= xs.Count;
        if (xs.Count < 2)
        {
            return new Stat { N = xs.Count, Mean = mean, Lo = mean, Hi = mean };
        }
        var ss = 0.0;
        foreach (var x in xs)
        {
            ss += (x - mean) * (x - mean);
        }
        var sd = Math.Sqrt(ss / (xs.Count - 1));
        var t = 1.96;
        var df = xs.Count - 1;
        if (df <= _tCrit95.Length)
        {
            t = _tCrit95[df - 1];
        }
        var half = t * sd / Math.Sqrt(xs.Count);
        return new Stat { N = xs.Count, Mean = mean, SD = sd, Lo = mean - half, Hi = mean + half };
    }

    public override string ToString() =>
        N == 0 ? "n/a" : string.Create(CultureInfo.InvariantCulture, $"{Mean:F2} [{Lo:F2}, {Hi:F2}] n={N}");
}

/// <summary>The distribution a Sweep reports. The survival counts come first: deaths and downs are never reduced to one scalar.</summary>
public sealed record Summary
{
    [JsonPropertyName("runs")]
    public int Runs { get; init; }

    [JsonPropertyName("seedsWithColonistDeath")]
    public int SeedsWithColonistDeath { get; init; }

    [JsonPropertyName("seedsWithColonistDown")]
    public int SeedsWithColonistDown { get; init; }

    [JsonPropertyName("unresolved")]
    public int Unresolved { get; init; }

    [JsonPropertyName("colonistDeaths")]
    public Stat ColonistDeaths { get; init; } = new();

    [JsonPropertyName("colonistDowns")]
    public Stat ColonistDowns { get; init; } = new();

    [JsonPropertyName("colonistDamage")]
    public Stat ColonistDamage { get; init; } = new();

    [JsonPropertyName("enemyDowns")]
    public Stat EnemyDowns { get; init; } = new();

    [JsonPropertyName("enemyDeaths")]
    public Stat EnemyDeaths { get; init; } = new();

    /// <summary>Resolved runs only.</summary>
    [JsonPropertyName("contactToResolutionTicks")]
    public Stat ContactToResolution { get; init; } = new();

    // A named per-run number; null from get where the run has none.
    private sealed record Measure(string Name, Func<Metrics, double?> Get);

    // Deaths and downs first; Compare gates on the first two.
    private static readonly Measure[] _measures =
    [
        new("colonistDeaths", m => m.ColonistDeaths),
        new("colonistDowns", m => m.ColonistDowns),
        new("colonistDamage", m => m.ColonistDamage),
        new("enemyDowns", m => m.EnemyDowns),
        new("enemyDeaths", m => m.EnemyDeaths),
        new("contactToResolutionTicks", m => m.ContactToResolution >= 0 ? m.ContactToResolution : null),
    ];

    internal static IReadOnlyList<string> MeasureNames { get; } = _measures.Select(m => m.Name).ToArray();

    internal static double? MeasureOf(int index, Metrics m) => _measures[index].Get(m);

    /// <summary>Derives a Sweep's summary from its rows; a row that failed has no metrics and counts in no measure (Go <c>Summarize</c>).</summary>
    public static Summary Summarize(IReadOnlyList<SeedRow> rows)
    {
        int runs = 0, deaths = 0, downs = 0, unresolved = 0;
        var series = _measures.Select(_ => new List<double>()).ToArray();
        foreach (var row in rows)
        {
            if (row.Metrics is not { } m)
            {
                continue;
            }
            runs++;
            if (m.ColonistDeaths > 0)
            {
                deaths++;
            }
            if (m.ColonistDowns > 0)
            {
                downs++;
            }
            if (m.Winner.Length == 0)
            {
                unresolved++;
            }
            for (var i = 0; i < _measures.Length; i++)
            {
                if (_measures[i].Get(m) is { } v)
                {
                    series[i].Add(v);
                }
            }
        }
        return new Summary
        {
            Runs = runs,
            SeedsWithColonistDeath = deaths,
            SeedsWithColonistDown = downs,
            Unresolved = unresolved,
            ColonistDeaths = Stat.NewStat(series[0]),
            ColonistDowns = Stat.NewStat(series[1]),
            ColonistDamage = Stat.NewStat(series[2]),
            EnemyDowns = Stat.NewStat(series[3]),
            EnemyDeaths = Stat.NewStat(series[4]),
            ContactToResolution = Stat.NewStat(series[5]),
        };
    }
}

/// <summary>
/// One fixture run under N seeds: a row per seed and the derived summary. It is the file
/// <c>acceptance sweep</c> writes and <c>-against</c> reads.
/// </summary>
public sealed record Sweep
{
    /// <summary>
    /// Bump when a field changes meaning. The baselines (#2843) are Sweep files, so a reader refuses one of
    /// another version instead of comparing it.
    /// </summary>
    public const int SweepVersion = 2;

    private static readonly JsonSerializerOptions _readOptions = new() { PropertyNameCaseInsensitive = true };

    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("fixture")]
    public string Fixture { get; init; } = "";

    /// <summary>The concurrency the runs shared; WallMs depends on it and on Load and is excluded from comparisons.</summary>
    [JsonPropertyName("workers")]
    public int Workers { get; init; }

    /// <summary>The box-load note the runs shared.</summary>
    [JsonPropertyName("load")]
    public string Load { get; init; } = "";

    [JsonPropertyName("wallMs")]
    public long WallMs { get; init; }

    [JsonPropertyName("rows")]
    public IReadOnlyList<SeedRow> Rows { get; init; } = [];

    [JsonPropertyName("summary")]
    public Summary Summary { get; init; } = new();

    /// <summary>Assembles a Sweep from its rows, sorted by seed (Go <c>NewSweep</c>).</summary>
    public static Sweep NewSweep(string fixture, int workers, string load, long wallMs, IReadOnlyList<SeedRow> rows)
    {
        var sorted = rows.OrderBy(r => r.Seed).ToArray();
        return new Sweep
        {
            Version = SweepVersion,
            Fixture = fixture,
            Workers = workers,
            Load = load,
            WallMs = wallMs,
            Rows = sorted,
            Summary = Summary.Summarize(sorted),
        };
    }

    /// <summary>Reads a Sweep file, refusing another schema version (Go <c>ParseSweep</c>); invalid JSON throws <see cref="JsonException"/>.</summary>
    public static Sweep ParseSweep(string json)
    {
        var s = JsonSerializer.Deserialize<Sweep>(json, _readOptions) ?? throw new JsonException("sweep file is empty");
        if (s.Version != SweepVersion)
        {
            throw new InvalidDataException(
                $"sweep is schema version {s.Version}, this build reads version {SweepVersion}: re-run the sweep");
        }
        return s;
    }

    /// <summary>
    /// Lists why a sweep cannot serve as a baseline: a failed seed, a seed that saw no contact or issued no
    /// orders (the planner never fought), or a summary that is not the one its rows derive.
    /// </summary>
    public IReadOnlyList<string> Defects()
    {
        var output = new List<string>();
        foreach (var r in Rows)
        {
            if (!string.IsNullOrEmpty(r.Error) || r.Metrics is null)
            {
                output.Add($"seed {r.Seed} failed: {r.Error}");
            }
            else if (r.Metrics.FirstContactTick < 0)
            {
                output.Add($"seed {r.Seed} saw no contact");
            }
            else if (r.Metrics.OrdersIssued == 0)
            {
                output.Add($"seed {r.Seed} issued no orders");
            }
        }
        if (Summary != Summary.Summarize(Rows))
        {
            output.Add("summary is not derived from the rows");
        }
        return output;
    }

    /// <summary>
    /// Prints the sweep with survival first: the seeds with a colonist death or down, the deaths and downs
    /// means with intervals, then the rest.
    /// </summary>
    public string Report()
    {
        var b = new StringBuilder();
        var sum = Summary;
        b.Append(CultureInfo.InvariantCulture, $"sweep {Fixture}: {sum.Runs} of {Rows.Count} seeds scored, {Workers} workers, {Load}\n");
        b.Append(CultureInfo.InvariantCulture, $"  seeds with a colonist death: {sum.SeedsWithColonistDeath} of {sum.Runs}\n");
        b.Append(CultureInfo.InvariantCulture, $"  seeds with a colonist down:  {sum.SeedsWithColonistDown} of {sum.Runs}\n");
        b.Append(CultureInfo.InvariantCulture, $"  unresolved:                  {sum.Unresolved} of {sum.Runs}\n");
        b.Append(CultureInfo.InvariantCulture, $"  colonist deaths: {sum.ColonistDeaths}\n  colonist downs:  {sum.ColonistDowns}\n");
        b.Append(CultureInfo.InvariantCulture, $"  colonist damage: {sum.ColonistDamage}\n  enemy downs:     {sum.EnemyDowns}\n  enemy deaths:    {sum.EnemyDeaths}\n  contact to resolution (ticks): {sum.ContactToResolution}\n");
        foreach (var r in Rows)
        {
            if (!string.IsNullOrEmpty(r.Error))
            {
                b.Append(CultureInfo.InvariantCulture, $"  seed {r.Seed} FAILED: {r.Error}\n");
            }
        }
        return b.ToString();
    }
}

/// <summary>A paired difference (candidate minus baseline) over the seeds both sweeps ran.</summary>
public sealed record Delta
{
    [JsonPropertyName("measure")]
    public string Measure { get; init; } = "";

    [JsonPropertyName("delta")]
    public Stat Stat { get; init; } = new();
}

/// <summary>A candidate sweep against a baseline sweep, seed by seed.</summary>
public sealed record Comparison
{
    [JsonPropertyName("fixture")]
    public string Fixture { get; init; } = "";

    [JsonPropertyName("pairedSeeds")]
    public IReadOnlyList<int> Paired { get; init; } = [];

    /// <summary>In one sweep only, or failed in either.</summary>
    [JsonPropertyName("unpairedSeeds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyList<int> Unpaired { get; init; } = [];

    [JsonPropertyName("deltas")]
    public IReadOnlyList<Delta> Deltas { get; init; } = [];

    /// <summary>The seeds where the candidate lost more colonists than the baseline did.</summary>
    [JsonPropertyName("deathGainSeeds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyList<int> DeathGainSeeds { get; init; } = [];

    /// <summary>Names why the verdict is a regression; empty is a pass.</summary>
    [JsonPropertyName("regressions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyList<string> Regressions { get; init; } = [];

    /// <summary>The exit verdict: a survival regression.</summary>
    public bool Regressed() => Regressions.Count > 0;

    /// <summary>
    /// Pairs the candidate against the baseline by seed. It regresses when a seed gains a colonist death, or
    /// when the paired delta interval of colonist deaths or downs lies wholly above zero (a zero-variance
    /// positive delta included; one pair has no interval). Damage, enemy outcomes and time are reported,
    /// never gated. Fixtures that differ, or no shared completed seed, throw <see cref="InvalidOperationException"/>.
    /// </summary>
    public static Comparison Compare(Sweep baseline, Sweep candidate)
    {
        if (baseline.Fixture != candidate.Fixture)
        {
            throw new InvalidOperationException($"baseline is fixture \"{baseline.Fixture}\", candidate \"{candidate.Fixture}\"");
        }
        var bySeed = new Dictionary<int, SeedRow>();
        foreach (var r in baseline.Rows)
        {
            bySeed[r.Seed] = r;
        }
        var seen = new HashSet<int>();
        var paired = new List<int>();
        var unpaired = new List<int>();
        var deathGain = new List<int>();
        var pairs = new List<(Metrics Base, Metrics Cand)>();
        foreach (var r in candidate.Rows)
        {
            seen.Add(r.Seed);
            if (!bySeed.TryGetValue(r.Seed, out var b) || b.Metrics is null || r.Metrics is null)
            {
                unpaired.Add(r.Seed);
                continue;
            }
            paired.Add(r.Seed);
            pairs.Add((b.Metrics, r.Metrics));
            if (r.Metrics.ColonistDeaths > b.Metrics.ColonistDeaths)
            {
                deathGain.Add(r.Seed);
            }
        }
        foreach (var r in baseline.Rows)
        {
            if (!seen.Contains(r.Seed))
            {
                unpaired.Add(r.Seed);
            }
        }
        unpaired.Sort();
        if (pairs.Count == 0)
        {
            throw new InvalidOperationException("baseline and candidate share no completed seed");
        }
        var deltas = new List<Delta>();
        for (var i = 0; i < Summary.MeasureNames.Count; i++)
        {
            var diffs = new List<double>();
            foreach (var (bm, cm) in pairs)
            {
                if (Summary.MeasureOf(i, bm) is { } bv && Summary.MeasureOf(i, cm) is { } cv)
                {
                    diffs.Add(cv - bv);
                }
            }
            deltas.Add(new Delta { Measure = Summary.MeasureNames[i], Stat = Stat.NewStat(diffs) });
        }
        var regressions = new List<string>();
        if (deathGain.Count > 0)
        {
            regressions.Add($"colonist deaths rose on seeds {GoList(deathGain)}");
        }
        foreach (var d in deltas.Take(2)) // deaths, downs
        {
            if (d.Stat.N >= 2 && d.Stat.Lo > 0)
            {
                regressions.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{d.Measure} paired delta {Signed(d.Stat.Mean)}, 95% interval [{Signed(d.Stat.Lo)}, {Signed(d.Stat.Hi)}] above zero"));
            }
        }
        return new Comparison
        {
            Fixture = candidate.Fixture,
            Paired = paired,
            Unpaired = unpaired,
            Deltas = deltas,
            DeathGainSeeds = deathGain,
            Regressions = regressions,
        };
    }

    /// <summary>Prints the paired comparison, deaths and downs first, and the verdict.</summary>
    public string Report()
    {
        var b = new StringBuilder();
        b.Append(CultureInfo.InvariantCulture, $"paired against baseline, {Fixture}: {Paired.Count} seeds paired");
        if (Unpaired.Count > 0)
        {
            b.Append(CultureInfo.InvariantCulture, $", unpaired {GoList(Unpaired)}");
        }
        b.Append('\n');
        foreach (var d in Deltas)
        {
            b.Append(CultureInfo.InvariantCulture, $"  delta {d.Measure,-26} {Signed(d.Stat.Mean)} [{Signed(d.Stat.Lo)}, {Signed(d.Stat.Hi)}] n={d.Stat.N}\n");
        }
        if (Regressed())
        {
            foreach (var r in Regressions)
            {
                b.Append(CultureInfo.InvariantCulture, $"  REGRESSION: {r}\n");
            }
        }
        else
        {
            b.Append("  no survival regression\n");
        }
        return b.ToString();
    }

    // Go's %+.2f.
    private static string Signed(double v) => v.ToString("+0.00;-0.00;+0.00", CultureInfo.InvariantCulture);

    // Go's %v of a []int: "[1 2 3]".
    private static string GoList(IReadOnlyList<int> values) => "[" + string.Join(' ', values) + "]";
}
