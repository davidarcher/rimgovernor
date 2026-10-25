using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>The exact adult-in-group probability of the native Peaceful point-budget draw (#3946).
/// Only child branches recurse: choosing an adult already proves the event. All quantities are conditional on the
/// current native option eligibility; there is no sampled group or inferred future change in those gates.</summary>
internal static class VisitorAdultChance
{
    // IncidentWorker_VisitorGroup.PointsCurve (private): Rand.ByCurve draws from this piecewise linear density.
    private static readonly (double X, double Y)[] Points = [(45, 0), (50, 1), (100, 1), (200, 0.25), (300, 0.1), (500, 0)];

    internal static double Of(VisitorGenerationRead read)
    {
        foreach (var option in read.Groups.SelectMany(g => g.Options))
        {
            if (!(option.Cost > 0) || !double.IsFinite(option.Cost))
            {
                throw new RequiredFactException("visitor_option_cost:" + option.Kind);
            }
        }

        var minimum = read.MinimumPoints;
        if (minimum >= Points[^1].X)
        {
            return AtPoints(read, minimum);
        }

        // Membership changes only where a maker reaches its maximum, an option first fits the budget, or the
        // faction cap crosses an option's base cost. Within each such interval the group recursion splits again
        // at the remaining-budget boundaries, so every integral is analytic, never numerical quadrature.
        var cuts = new SortedSet<double>(Points.Select(p => p.X)) { Math.Max(Points[0].X, minimum) };
        foreach (var group in read.Groups)
        {
            cuts.Add(group.MaximumPoints);
            foreach (var option in group.Options)
            {
                cuts.Add(option.Cost);
                if (option.BaseCost <= minimum * 1.2)
                {
                    continue;
                }

                for (var i = 1; i < read.CostCurve.Count; i++)
                {
                    var a = read.CostCurve[i - 1];
                    var b = read.CostCurve[i];
                    if (a.Y != b.Y && option.BaseCost >= Math.Min(a.Y, b.Y) && option.BaseCost <= Math.Max(a.Y, b.Y))
                    {
                        cuts.Add(a.X + ((b.X - a.X) * (option.BaseCost - a.Y) / (b.Y - a.Y)));
                    }
                }
            }
        }

        var bounds = cuts.Where(p => p >= Math.Max(Points[0].X, minimum) && p <= Points[^1].X).ToArray();
        var adult = Mass(Points[0].X, minimum) * AtPoints(read, minimum);
        for (var i = 1; i < bounds.Length; i++)
        {
            adult += Integrate(read, bounds[i - 1], bounds[i]);
        }

        return adult / Mass(Points[0].X, Points[^1].X);
    }

    internal static double AtPoints(VisitorGenerationRead read, double points) => Integrate(read, points, points);

    private static double Integrate(VisitorGenerationRead read, double low, double high)
    {
        var points = (low + high) / 2;
        var groups = read.Groups.Where(g => g.MaximumPoints >= points && g.Options.Any(o => o.Cost <= points)).ToArray();
        var total = groups.Sum(g => Math.Max(0, g.Commonality));
        if (!(total > 0))
        {
            return 0;
        }

        if (read.CostCurve.Count == 0)
        {
            throw new RequiredFactException("visitor_pawn_cost_curve");
        }

        var cap = Math.Max(ThreatSchedule.Curve(read.CostCurve.Select(p => (p.X, p.Y)).ToArray(), points), read.MinimumPoints * 1.2);
        var result = 0.0;
        foreach (var group in groups.Where(g => g.Commonality > 0))
        {
            var options = group.Options.Where(o => o.BaseCost <= cap && o.Cost <= points).ToArray();
            var highest = options.Length == 0 ? 0 : options.Max(o => o.Cost);
            var kinds = options.Select(o => o.Kind).Distinct(StringComparer.Ordinal).ToArray();
            var counts = new int[kinds.Length];
            var indexes = options.Select(o => Array.IndexOf(kinds, o.Kind)).ToArray();
            var memo = new Dictionary<(double Low, double High, double Spent, bool Leader, string Counts), double>();
            var none = NoAdult(low, high, 0, false);
            result += group.Commonality / total * (Measure(low, high) - none);

            double NoAdult(double from, double to, double spent, bool leader)
            {
                var key = (from, to, spent, leader, string.Join(",", counts));
                if (memo.TryGetValue(key, out var known))
                {
                    return known;
                }

                // Split before choosing: affordability must be constant over the whole interval.
                var split = options.Where((o, i) => counts[indexes[i]] < o.MaximumPerGroup && !(leader && o.Leader))
                    .Select(o => spent + o.Cost).Where(p => p > from && p < to).DefaultIfEmpty(to).Min();
                if (split < to)
                {
                    return memo[key] = NoAdult(from, split, spent, leader) + NoAdult(split, to, spent, leader);
                }

                var remaining = ((from + to) / 2) - spent;
                var eligible = Enumerable.Range(0, options.Length).Where(i => options[i].Cost <= remaining
                    && counts[indexes[i]] < options[i].MaximumPerGroup && !(leader && options[i].Leader)).ToArray();
                // highestCost is never reset by native ChoosePawnGenOptionsByPoints. The first iteration's
                // maximum therefore weights every later draw, including after that expensive kind no longer fits.
                double Weight(int i) => Math.Max(0, options[i].Weight) * CostWeight(options[i].Cost / highest);
                var weight = eligible.Sum(Weight);
                if (!(weight > 0))
                {
                    return memo[key] = Measure(from, to);
                }

                var none = 0.0;
                foreach (var i in eligible.Where(i => !options[i].Adult && Weight(i) > 0))
                {
                    counts[indexes[i]]++;
                    none += Weight(i) / weight * NoAdult(from, to, spent + options[i].Cost, leader || options[i].Leader);
                    counts[indexes[i]]--;
                }

                return memo[key] = none;
            }
        }

        return result;
    }

    // PawnGroupMakerUtility.PawnWeightFactorByMostExpensivePawnCostFractionCurve (private).
    private static double CostWeight(double fraction) => ThreatSchedule.Curve([(0.2, 0.01), (0.3, 0.3), (0.5, 1)], fraction);

    private static double Measure(double low, double high) => low == high ? 1 : Mass(low, high);

    private static double Mass(double low, double high)
    {
        var sum = 0.0;
        for (var i = 1; i < Points.Length; i++)
        {
            var from = Math.Max(low, Points[i - 1].X);
            var to = Math.Min(high, Points[i].X);
            if (to > from)
            {
                sum += (to - from) * (ThreatSchedule.Curve(Points, from) + ThreatSchedule.Curve(Points, to)) / 2;
            }
        }

        return sum;
    }
}
