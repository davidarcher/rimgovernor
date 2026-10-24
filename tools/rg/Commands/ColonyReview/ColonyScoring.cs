using System.Globalization;
using System.Text.Json;

namespace RimGovernor.Tools.Commands.ColonyReview;

/// <summary>
/// The colony score and the nightly comparison. The score is a vector of components read from the run timeline plus a weighted scalar for
/// ranking, and the run's thresholds (the layout section's, #3751). The components and scalar are signal only: they gate nothing.
/// A threshold is a fail condition: one that fails fails the run (<see cref="ColonyScore.Failed"/>). A component the timeline
/// cannot supply is unknown (a null value) and is left out of the scalar, never counted as zero; a threshold whose rows the run
/// did not leave is unknown, never a pass. A run's score is compared with the previous night's stored score for the same seed;
/// one run per seed makes a delta noisy, so the delta gates nothing, and a missing baseline or a component either side could not
/// read is unknown, never a zero.
/// </summary>
internal static class ColonyScoring
{
    // Normalisation anchors: a component's Score is its Value mapped onto [0, 1] (1 best) against these, then clamped.
    // Documented in docs/developers/testing/colony-review.md.
    private const double HorizonDays = 15.0; // one in-game season (quadrum): the stage-days scale
    private const double WealthGrowthFull = 1.0; // wealth doubling over the run scores 1
    private const double FoodRunwayFull = 10.0; // a ten-day minimum runway scores 1

    // Weights of the scalar. A stage tier's weight is WeightStages split evenly over the tiers the run reached.
    private const double WeightWealth = 3.0;
    private const double WeightMood = 2.0;
    private const double WeightFood = 3.0;
    private const double WeightDeaths = 4.0;
    private const double WeightDowned = 1.0;
    private const double WeightStages = 3.0;
    private const double WeightRaid = 2.0;

    private const string DeltaOk = "ok"; // both runs have the component's score
    private const string DeltaUnknown = "unknown"; // a side has no score (null value, or the component is absent there)
    private const string DeltaNoBaseline = "no_baseline"; // no earlier run with this seed and a score

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Derives the score from the run's rows, carrying <paramref name="thresholds"/> (none when null).</summary>
    public static ColonyScore Compute(IReadOnlyList<Row> rows, IReadOnlyList<ScoreThreshold>? thresholds = null)
    {
        var read = rows.Where(r => r.Census.Error.Length == 0 && r.Census.Colonists is not null).ToList();
        double wFirst = 0;
        double wLast = 0;
        var wN = 0;
        double moodSum = 0;
        var moodN = 0;
        var minFood = double.PositiveInfinity;
        var lost = 0;
        var downedHours = 0;
        for (var i = 0; i < read.Count; i++)
        {
            var r = read[i];
            var c = r.Census;
            if (c.WealthTotal is { } wealthTotal)
            {
                if (wN == 0)
                {
                    wFirst = wealthTotal;
                }
                wLast = wealthTotal;
                wN++;
            }
            if (c.MoodMean is { } mood)
            {
                moodSum += mood;
                moodN++;
            }
            if (c.FoodRunwayDays is { } food)
            {
                minFood = Math.Min(minFood, food);
            }
            if (i > 0 && r.Colonists < read[i - 1].Colonists)
            {
                lost += read[i - 1].Colonists - r.Colonists;
            }
            if (c.Downed > 0 || (c.Pawns ?? []).Any(p => p.Downed == true))
            {
                downedHours++;
            }
        }
        var wealth = Component("wealth_growth", "ratio", WeightWealth, "last wealth over first, minus 1");
        var moodC = Component("mean_mood", "0-1", WeightMood, "mean of the hourly colony mood mean");
        var foodC = Component("min_food_runway", "days", WeightFood, "lowest food runway of the run");
        var deaths = Component("deaths", "colonists", WeightDeaths, "colonists lost between readings");
        var downed = Component("downed_time", "fraction of hours", WeightDowned, "readings with a colonist downed");
        var raid = Component("raid_damage", "", WeightRaid, "the timeline records no raid damage");
        if (wN > 1 && wFirst > 0)
        {
            var g = (wLast / wFirst) - 1;
            wealth = Set(wealth, g, g / WealthGrowthFull);
        }
        if (moodN > 0)
        {
            var m = moodSum / moodN;
            moodC = Set(moodC, m, m);
        }
        if (!double.IsPositiveInfinity(minFood))
        {
            foodC = Set(foodC, minFood, minFood / FoodRunwayFull);
        }
        if (read.Count > 0 && read[0].Colonists > 0)
        {
            deaths = Set(deaths, lost, 1 - ((double)lost / read[0].Colonists));
        }
        if (read.Count > 0)
        {
            var f = (double)downedHours / read.Count;
            downed = Set(downed, f, 1 - f);
        }
        var components = new List<ScoreComponent> { wealth, moodC, foodC, deaths, downed };
        components.AddRange(StageComponents(read));
        components.Add(raid);

        double sum = 0;
        double weight = 0;
        foreach (var c in components.Where(c => c.Score is not null))
        {
            sum += c.Weight * c.Score!.Value;
            weight += c.Weight;
        }
        return new ColonyScore { Components = components, Scalar = weight > 0 ? 100 * sum / weight : null, Thresholds = thresholds ?? [] };
    }

    /// <summary>
    /// The score of <paramref name="current"/> against <paramref name="baseline"/>. Components are listed in the current
    /// order, then any only the baseline had.
    /// </summary>
    public static ScoreDelta Compare(ColonyScore current, ColonyScore baseline)
    {
        var have = baseline.Components.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var components = new List<ComponentDelta>();
        foreach (var c in current.Components)
        {
            seen.Add(c.Name);
            var cd = new ComponentDelta { Name = c.Name, Status = DeltaUnknown };
            if (have.TryGetValue(c.Name, out var b) && Sub(c.Score, b.Score) is { } delta)
            {
                cd = cd with { Status = DeltaOk, Delta = delta, ValueDelta = Sub(c.Value, b.Value) };
            }
            components.Add(cd);
        }
        components.AddRange(baseline.Components.Where(c => !seen.Contains(c.Name)).Select(c => new ComponentDelta { Name = c.Name, Status = DeltaUnknown }));
        // Each threshold's status beside the baseline's (a threshold the baseline did not hold reads unknown there).
        var held = baseline.Thresholds.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var thresholds = current.Thresholds
            .Select(t => held.TryGetValue(t.Name, out var b)
                ? new ThresholdDelta { Name = t.Name, Status = t.Status, BaselineStatus = b.Status, ValueDelta = Sub(t.Value, b.Value) }
                : new ThresholdDelta { Name = t.Name, Status = t.Status, BaselineStatus = ScoreThreshold.Unknown })
            .ToList();
        return new ScoreDelta { Status = DeltaOk, Scalar = Sub(current.Scalar, baseline.Scalar), Components = components, Thresholds = thresholds };
    }

    /// <summary>
    /// The newest run dir under <paramref name="runs"/>, other than <paramref name="self"/>, whose run.json carries the same
    /// seed and a score. Dir names sort by date then run id, so the greatest earlier name is the previous night's.
    /// </summary>
    public static (string Name, RunSummary Summary)? FindBaseline(string runs, string self, string seed)
    {
        if (!Directory.Exists(runs))
        {
            return null;
        }
        var names = Directory.EnumerateDirectories(runs)
            .Select(d => Path.GetFileName(d))
            .Where(n => string.CompareOrdinal(n, self) < 0)
            .OrderByDescending(n => n, StringComparer.Ordinal);
        foreach (var n in names)
        {
            var path = Path.Combine(runs, n, "run.json");
            if (!File.Exists(path))
            {
                continue;
            }
            RunSummary? c;
            try
            {
                c = JsonSerializer.Deserialize<RunSummary>(File.ReadAllBytes(path), ReadOptions);
            }
            catch (JsonException)
            {
                continue;
            }
            if (c is null || c.MetaValue("seed") != seed || c.Score.Components.Count == 0)
            {
                continue;
            }
            return (n, c);
        }
        return null;
    }

    /// <summary>
    /// The summary with its delta against the previous night's run for the same seed found under
    /// <paramref name="runs"/> (<paramref name="self"/> is this run's dir name). An empty runs skips the comparison.
    /// </summary>
    public static RunSummary CompareToStore(RunSummary summary, string runs, string self)
    {
        if (runs.Length == 0)
        {
            return summary;
        }
        if (FindBaseline(runs, self, summary.MetaValue("seed")) is not { } found)
        {
            return summary with { Delta = new ScoreDelta { Status = DeltaNoBaseline } };
        }
        var delta = Compare(summary.Score, found.Summary.Score);
        return summary with { Delta = delta with { Baseline = found.Name, BaselineCommit = found.Summary.MetaValue("commit") } };
    }

    private static double Clamp01(double x) => Math.Max(0, Math.Min(1, x));

    private static double? Sub(double? a, double? b) => a is { } x && b is { } y ? x - y : null;

    private static ScoreComponent Component(string name, string unit, double weight, string note) =>
        new() { Name = name, Unit = unit, Weight = weight, Note = note };

    private static ScoreComponent Set(ScoreComponent c, double value, double score) => c with { Value = value, Score = Clamp01(score) };

    // One component per tech tier the run reached: the days from the start to its first reading. Without a tier reading it
    // is a single unknown component.
    private static List<ScoreComponent> StageComponents(List<Row> read)
    {
        var order = new List<string>();
        var first = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var r in read)
        {
            if (string.IsNullOrEmpty(r.Census.TechTier))
            {
                continue;
            }
            var t = r.Census.TechTier;
            if (first.TryAdd(t, r.Tick / 60000.0))
            {
                order.Add(t);
            }
        }
        if (order.Count == 0)
        {
            return [Component("stage_days", "days", WeightStages, "the timeline has no tech tier reading")];
        }
        var output = new List<ScoreComponent>();
        foreach (var t in order)
        {
            var c = Component(
                "stage_days:" + t,
                "days",
                WeightStages / order.Count,
                string.Create(CultureInfo.InvariantCulture, $"days until the colony first reached the {t} tech tier"));
            output.Add(Set(c, first[t], 1 - (first[t] / HorizonDays)));
        }
        return output;
    }
}
