using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

// The thermal term of the capability-days ledger (#3521 of #3469): hypothermia and heatstroke severity integrated exactly over a
// pawn's stationary thermal spans, then priced through the hediff's stage table like any other course. The rates mirror the game's
// HediffGiver_Hypothermia and HediffGiver_Heat (kept-constants, "Mirrored game literals: capacity penalties"). The giver steps
// every 60 ticks; the integral here is its continuous limit, with the temperature linear inside each span, so every piece of the
// path is a closed form: linear, quadratic (gain under a linear temperature) or exponential (recovery), cut where the severity
// meets a stage boundary, a rate boundary, zero or the lethal severity.

public static partial class ForecastValue
{
    // Both givers: severity gained per degree beyond the safe range, per 60-tick interval, with a floor under the gain.
    private const double ThermalPerDegree = 6.45E-05;
    private const double HypothermiaFloor = 0.00075;
    private const double HeatstrokeFloor = 0.000375;

    // Both givers: inside the comfort range the severity falls by Clamp(severity * 0.027, 0.0015, 0.015) per interval.
    private const double RecoveryFraction = 0.027;
    private const double RecoveryMin = 0.0015;
    private const double RecoveryMax = 0.015;

    // GenTemperature.SafeTemperatureRange: the comfortable range widened by this on each side.
    private const double SafeMargin = 10;

    // HediffGiver_Heat.TemperatureOverageAdjustmentCurve: (degrees over the safe maximum, adjusted overage).
    private static readonly (double X, double Y)[] OverageCurve = [(0, 0), (25, 25), (50, 40), (100, 60), (200, 80), (400, 100)];

    // Past the last listed point the game's curve runs on to (4000, 1000): a straight line of this slope, far above any temperature a map reaches.
    private const double OverageTailSlope = 0.25;

    private const double IntervalTicks = 60;

    /// <summary>
    /// A pawn's capability-days lost to hypothermia and heatstroke over its thermal spans, from the severities it has now. Null
    /// (unassessed) when any occupancy is unknown, so a pawn is never free by omission, or its capacities are unobserved.
    /// A severity that reaches the def's lethal value charges the remaining horizon and the policy's terminal value.
    /// </summary>
    private static double? ThermalLoss(string pawn, ForecastThermalExposure? exposure, NeedsForecastPawn? seen, CapacityPenaltyTables tables,
        double movingMin, double horizon, ForecastValuePolicy policy, SortedSet<string> findings)
    {
        if (exposure is null || exposure.UnknownTicks > 0)
        {
            findings.Add("unassessed_thermal_exposure");
            return null;
        }

        if (seen is null || !seen.HypothermiaSeverity.TryGetValue(out var cold) || !seen.HeatstrokeSeverity.TryGetValue(out var hot) || Levels(seen) is not { } levels)
        {
            findings.Add("capacities_unobserved:" + pawn);
            return null;
        }

        return Path(pawn, exposure, true, new Course(tables.Hypothermia, levels, movingMin, cold), horizon, policy, findings)
            + Path(pawn, exposure, false, new Course(tables.Heatstroke, levels, movingMin, hot), horizon, policy, findings);
    }

    private static double Path(string pawn, ForecastThermalExposure exposure, bool coldSide, Course course, double horizon, ForecastValuePolicy policy,
        SortedSet<string> findings)
    {
        var table = course.Table;
        double? diedAt = null;
        if (table.LethalSeverity is { } top && course.Severity >= top)
        {
            course.MarkLethal();
            diedAt = 0;
        }

        foreach (var span in exposure.Spans.OrderBy(s => s.Start))
        {
            if (diedAt is not null) break;
            if (span.End <= span.Start) continue;
            diedAt = Span(course, span, coldSide);
        }

        course.Report(pawn, findings);
        var lost = course.LostTicks / GameTime.TicksPerDay;
        return diedAt is { } tick ? lost + Math.Max(0, horizon - tick) / GameTime.TicksPerDay + policy.DeathTerminalValue : lost;
    }

    /// <summary>One span: its temperature crossings of the giver's thresholds cut it into pieces of one regime, each walked in turn. Returns the tick the severity went lethal, or null.</summary>
    private static double? Span(Course course, ForecastThermalSpan span, bool coldSide)
    {
        var comfortMin = span.ComfortableMin;
        var comfortMax = span.ComfortableMax;
        var safeMin = comfortMin - SafeMargin;
        var safeMax = comfortMax + SafeMargin;
        var length = span.End - span.Start;
        var slope = (span.Final - span.Initial) / length;

        var thresholds = new List<double>(coldSide
            ? [safeMin, comfortMin, safeMin - HypothermiaFloor / ThermalPerDegree]
            : [safeMax, comfortMax, safeMax + HeatstrokeFloor / ThermalPerDegree, .. OverageCurve.Skip(1).Select(p => safeMax + p.X)]);
        var cuts = new List<double> { 0, length };
        if (slope != 0)
            foreach (var threshold in thresholds)
            {
                var at = (threshold - span.Initial) / slope;
                if (at > 0 && at < length) cuts.Add(at);
            }

        cuts.Sort();
        for (var i = 0; i + 1 < cuts.Count; i++)
        {
            double from = cuts[i], to = cuts[i + 1];
            if (to <= from) continue;
            var start = span.Initial + slope * from;
            var middle = span.Initial + slope * (from + to) / 2;
            var elapsed = to - from;
            double? died;
            if (coldSide)
            {
                if (middle < safeMin)
                {
                    // Depth below the safe minimum, deeper than the floor's depth, gains linearly in the depth.
                    var depth = HypothermiaFloor / ThermalPerDegree;
                    died = middle < safeMin - depth
                        ? Gain(course, elapsed, ThermalPerDegree * (safeMin - start) / IntervalTicks, -ThermalPerDegree * slope / IntervalTicks)
                        : Gain(course, elapsed, HypothermiaFloor / IntervalTicks, 0);
                }
                else died = middle > comfortMin ? Recover(course, elapsed) : Hold(course, elapsed);
            }
            else if (middle > safeMax)
            {
                var (value, curveSlope) = Curve(middle - safeMax);
                var aboveFloor = value * ThermalPerDegree >= HeatstrokeFloor;
                died = aboveFloor
                    // The curve is linear inside this piece's segment, so the gain is linear in time: its value at the piece start, then its slope.
                    ? Gain(course, elapsed, ThermalPerDegree * (value + curveSlope * (start - middle)) / IntervalTicks, ThermalPerDegree * curveSlope * slope / IntervalTicks)
                    : Gain(course, elapsed, HeatstrokeFloor / IntervalTicks, 0);
            }
            else died = middle < comfortMax ? Recover(course, elapsed) : Hold(course, elapsed);

            if (died is { } within) return span.Start + from + within;
        }

        return null;
    }

    /// <summary>The adjusted overage at the given degrees over the safe maximum, and the curve's slope in that segment.</summary>
    private static (double Value, double Slope) Curve(double overage)
    {
        for (var i = 1; i < OverageCurve.Length; i++)
        {
            if (overage <= OverageCurve[i].X)
            {
                var (x0, y0) = OverageCurve[i - 1];
                var (x1, y1) = OverageCurve[i];
                var slope = (y1 - y0) / (x1 - x0);
                return (y0 + slope * (overage - x0), slope);
            }
        }

        var (lastX, lastY) = OverageCurve[^1];
        return (lastY + OverageTailSlope * (overage - lastX), OverageTailSlope);
    }

    private static double? Hold(Course course, double ticks)
    {
        course.Piece(ticks, course.Severity, course.Severity);
        return null;
    }

    /// <summary>Severity rising at <c>rate + slope * t</c> a tick: quadratic, cut at stage boundaries and the lethal severity. Returns the tick (from the piece start) it went lethal, or null.</summary>
    private static double? Gain(Course course, double ticks, double rate, double slope)
    {
        double used = 0;
        while (used < ticks)
        {
            var current = rate + slope * used;
            var remaining = ticks - used;
            var (target, reach) = Next(course, true, level => TimeToRise(level - course.Severity, current, slope));
            if (reach >= remaining)
            {
                var after = course.Severity + current * remaining + slope * remaining * remaining / 2;
                course.Piece(remaining, course.Severity, after);
                course.Severity = after;
                return null;
            }

            course.Piece(reach, course.Severity, target);
            course.Severity = target;
            used += reach;
            if (course.Table.LethalSeverity is { } lethal && target >= lethal)
            {
                course.MarkLethal();
                return used;
            }
        }

        return null;
    }

    /// <summary>
    /// Severity falling by Clamp(severity * 0.027, 0.0015, 0.015) per interval: linear at the top rate above its breakpoint,
    /// exponential between the two, linear at the bottom rate under it, and gone at 0.
    /// </summary>
    private static double? Recover(Course course, double ticks)
    {
        const double decay = RecoveryFraction / IntervalTicks;
        const double upper = RecoveryMax / RecoveryFraction;
        const double lower = RecoveryMin / RecoveryFraction;
        double used = 0;
        while (used < ticks && course.Severity > 0)
        {
            var remaining = ticks - used;
            var severity = course.Severity;
            // The regime the severity is in, moving down: at a breakpoint it takes the lower regime.
            var regime = severity > upper ? 0 : severity > lower ? 1 : 2;
            double Level(double t) => regime switch
            {
                0 => severity - RecoveryMax / IntervalTicks * t,
                1 => severity * Math.Exp(-decay * t),
                _ => severity - RecoveryMin / IntervalTicks * t,
            };
            double TimeTo(double level) => regime switch
            {
                0 => (severity - level) * IntervalTicks / RecoveryMax,
                1 => Math.Log(severity / level) / decay,
                _ => (severity - level) * IntervalTicks / RecoveryMin,
            };

            var (target, reach) = Next(course, false, TimeTo, regime switch { 0 => upper, 1 => lower, _ => 0 });
            if (reach >= remaining)
            {
                var after = Math.Max(0, Level(remaining));
                course.Piece(remaining, severity, after);
                course.Severity = after;
                return null;
            }

            course.Piece(reach, severity, target);
            course.Severity = target;
            used += reach;
        }

        if (used < ticks) course.Severity = 0;
        return null;
    }

    /// <summary>The nearest level strictly ahead in the direction of travel the path must stop at, and the ticks to it: a stage boundary, the lethal severity going up, 0 and an extra level (a rate boundary) going down.</summary>
    private static (double Target, double Reach) Next(Course course, bool rising, Func<double, double> timeTo, double? extra = null)
    {
        var severity = course.Severity;
        var levels = course.Table.Stages.Select(s => s.MinSeverity).ToList();
        if (rising && course.Table.LethalSeverity is { } lethal) levels.Add(lethal);
        if (!rising) levels.Add(0);
        if (extra is { } more) levels.Add(more);
        var target = double.NaN;
        var reach = double.PositiveInfinity;
        foreach (var level in levels)
        {
            if (rising ? level <= severity : level >= severity || level < 0) continue;
            var t = timeTo(level);
            if (t < reach) { reach = t; target = level; }
        }

        return (target, reach);
    }

    /// <summary>Ticks for severity to rise by <paramref name="rise"/> at <c>rate + slope * t</c> a tick; infinite when it never does.</summary>
    private static double TimeToRise(double rise, double rate, double slope)
    {
        if (Math.Abs(slope) < 1e-18) return rate > 0 ? rise / rate : double.PositiveInfinity;
        var discriminant = rate * rate + 2 * slope * rise;
        if (discriminant < 0) return double.PositiveInfinity;
        var root = (-rate + Math.Sqrt(discriminant)) / slope;
        if (root > 0) return root;
        var other = (-rate - Math.Sqrt(discriminant)) / slope;
        return other > 0 ? other : double.PositiveInfinity;
    }
}
