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
    private static double? ThermalLoss(string pawn, ForecastThermalExposure? exposure, ObservedPawn? seen, CapacityPenaltyTables tables,
        double movingMin, double horizon, ForecastValuePolicy policy, SortedSet<string> findings)
    {
        if (exposure is null || exposure.UnknownTicks > 0)
        {
            findings.Add("unassessed_thermal_exposure");
            return null;
        }

        if (seen is null || !seen.Pawn.HypothermiaSeverity.TryGetValue(out var cold) || !seen.Pawn.HeatstrokeSeverity.TryGetValue(out var hot) || seen.Levels is not { } levels)
        {
            findings.Add("capacities_unobserved:" + pawn);
            return null;
        }

        var spans = exposure.Spans;
        Span<int> order = spans.Count <= 64 ? stackalloc int[spans.Count] : new int[spans.Count];
        for (var i = 0; i < order.Length; i++) order[i] = i;
        order.Sort(new ThermalSpanOrder(spans));
        var coldCourse = new Course(tables.Hypothermia, levels, movingMin, cold);
        var hotCourse = new Course(tables.Heatstroke, levels, movingMin, hot);
        return Path(pawn, spans, order, true, ref coldCourse, horizon, policy, findings)
            + Path(pawn, spans, order, false, ref hotCourse, horizon, policy, findings);
    }

    /// <summary>The spans by Start, ties in span order: the stable order OrderBy ranks them in, with nothing allocated (#3468).</summary>
    private readonly struct ThermalSpanOrder(EquatableArray<ForecastThermalSpan> spans) : IComparer<int>
    {
        public int Compare(int x, int y)
        {
            var c = spans[x].Start.CompareTo(spans[y].Start);
            return c != 0 ? c : x.CompareTo(y);
        }
    }

    private static double Path(string pawn, EquatableArray<ForecastThermalSpan> spans, ReadOnlySpan<int> order, bool coldSide, ref Course course, double horizon,
        ForecastValuePolicy policy, SortedSet<string> findings)
    {
        var table = course.Table;
        double? diedAt = null;
        if (table.LethalSeverity is { } top && course.Severity >= top)
        {
            course.MarkLethal();
            diedAt = 0;
        }

        foreach (var i in order)
        {
            var span = spans[i];
            if (diedAt is not null) break;
            if (span.End <= span.Start) continue;
            diedAt = Span(ref course, span, coldSide);
        }

        course.Report(pawn, findings);
        var lost = course.LostTicks / GameTime.TicksPerDay;
        return diedAt is { } tick ? lost + Math.Max(0, horizon - tick) / GameTime.TicksPerDay + policy.DeathTerminalValue : lost;
    }

    /// <summary>One span: its temperature crossings of the giver's thresholds cut it into pieces of one regime, each walked in turn. Returns the tick the severity went lethal, or null.</summary>
    private static double? Span(ref Course course, ForecastThermalSpan span, bool coldSide)
    {
        var comfortMin = span.ComfortableMin;
        var comfortMax = span.ComfortableMax;
        var safeMin = comfortMin - SafeMargin;
        var safeMax = comfortMax + SafeMargin;
        var length = span.End - span.Start;
        var slope = (span.Final - span.Initial) / length;

        // The thresholds a crossing cuts at: three, plus the overage curve's knees on the hot side.
        Span<double> thresholds = stackalloc double[3 + OverageCurve.Length - 1];
        var thresholdCount = 3;
        if (coldSide)
        {
            thresholds[0] = safeMin; thresholds[1] = comfortMin; thresholds[2] = safeMin - HypothermiaFloor / ThermalPerDegree;
        }
        else
        {
            thresholds[0] = safeMax; thresholds[1] = comfortMax; thresholds[2] = safeMax + HeatstrokeFloor / ThermalPerDegree;
            for (var k = 1; k < OverageCurve.Length; k++) thresholds[thresholdCount++] = safeMax + OverageCurve[k].X;
        }
        Span<double> cuts = stackalloc double[2 + thresholds.Length];
        cuts[0] = 0; cuts[1] = length;
        var cutCount = 2;
        if (slope != 0)
            for (var k = 0; k < thresholdCount; k++)
            {
                var at = (thresholds[k] - span.Initial) / slope;
                if (at > 0 && at < length) cuts[cutCount++] = at;
            }

        cuts = cuts[..cutCount];
        cuts.Sort();
        for (var i = 0; i + 1 < cuts.Length; i++)
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
                        ? Gain(ref course, elapsed, ThermalPerDegree * (safeMin - start) / IntervalTicks, -ThermalPerDegree * slope / IntervalTicks)
                        : Gain(ref course, elapsed, HypothermiaFloor / IntervalTicks, 0);
                }
                else died = middle > comfortMin ? Recover(ref course, elapsed) : Hold(ref course, elapsed);
            }
            else if (middle > safeMax)
            {
                var (value, curveSlope) = Curve(middle - safeMax);
                var aboveFloor = value * ThermalPerDegree >= HeatstrokeFloor;
                died = aboveFloor
                    // The curve is linear inside this piece's segment, so the gain is linear in time: its value at the piece start, then its slope.
                    ? Gain(ref course, elapsed, ThermalPerDegree * (value + curveSlope * (start - middle)) / IntervalTicks, ThermalPerDegree * curveSlope * slope / IntervalTicks)
                    : Gain(ref course, elapsed, HeatstrokeFloor / IntervalTicks, 0);
            }
            else died = middle < comfortMax ? Recover(ref course, elapsed) : Hold(ref course, elapsed);

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

    private static double? Hold(ref Course course, double ticks)
    {
        course.Piece(ticks, course.Severity, course.Severity);
        return null;
    }

    /// <summary>Severity rising at <c>rate + slope * t</c> a tick: quadratic, cut at stage boundaries and the lethal severity. Returns the tick (from the piece start) it went lethal, or null.</summary>
    private static double? Gain(ref Course course, double ticks, double rate, double slope)
    {
        double used = 0;
        while (used < ticks)
        {
            var current = rate + slope * used;
            var remaining = ticks - used;
            var (target, reach) = Next(ref course, true, new(Clock.Rising, course.Severity, current, slope));
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
    private static double? Recover(ref Course course, double ticks)
    {
        const double upper = RecoveryMax / RecoveryFraction;
        const double lower = RecoveryMin / RecoveryFraction;
        double used = 0;
        while (used < ticks && course.Severity > 0)
        {
            var remaining = ticks - used;
            var severity = course.Severity;
            // The regime the severity is in, moving down: at a breakpoint it takes the lower regime.
            var regime = severity > upper ? Clock.FallingFast : severity > lower ? Clock.FallingExponential : Clock.FallingSlow;
            var clock = new Clock(regime, severity, 0, 0);
            var (target, reach) = Next(ref course, false, clock, regime switch { Clock.FallingFast => upper, Clock.FallingExponential => lower, _ => 0 });
            if (reach >= remaining)
            {
                var after = Math.Max(0, clock.Level(remaining));
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

    /// <summary>How a severity moves in one regime: the level after t ticks, and the ticks to a level. A struct, so a path's pieces allocate nothing (#3468).</summary>
    private readonly record struct Clock(int Regime, double Severity, double Rate, double Slope)
    {
        public const int Rising = 0, FallingFast = 1, FallingExponential = 2, FallingSlow = 3;
        private const double Decay = RecoveryFraction / IntervalTicks;

        public double Level(double t) => Regime switch
        {
            FallingFast => Severity - RecoveryMax / IntervalTicks * t,
            FallingExponential => Severity * Math.Exp(-Decay * t),
            FallingSlow => Severity - RecoveryMin / IntervalTicks * t,
            _ => throw new InvalidOperationException("a rising clock has no closed level"),
        };

        public double TimeTo(double level) => Regime switch
        {
            Rising => TimeToRise(level - Severity, Rate, Slope),
            FallingFast => (Severity - level) * IntervalTicks / RecoveryMax,
            FallingExponential => Math.Log(Severity / level) / Decay,
            _ => (Severity - level) * IntervalTicks / RecoveryMin,
        };
    }

    /// <summary>The nearest level strictly ahead in the direction of travel the path must stop at, and the ticks to it: a stage boundary, the lethal severity going up, 0 and an extra level (a rate boundary) going down.</summary>
    private static (double Target, double Reach) Next(ref Course course, bool rising, Clock clock, double? extra = null)
    {
        var severity = course.Severity;
        var target = double.NaN;
        var reach = double.PositiveInfinity;
        // The levels in the order the list held them: the stages, then the lethal severity or 0, then the extra.
        var stages = course.Table.Stages;
        var count = stages.Count + 1 + (extra is null ? 0 : 1);
        var lethal = course.Table.LethalSeverity;
        if (rising && lethal is null) count--;
        for (var i = 0; i < count; i++)
        {
            var level = i < stages.Count ? stages[i].MinSeverity : i == stages.Count && (!rising || lethal is not null) ? (rising ? lethal!.Value : 0) : extra!.Value;
            if (rising ? level <= severity : level >= severity || level < 0) continue;
            var t = clock.TimeTo(level);
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
