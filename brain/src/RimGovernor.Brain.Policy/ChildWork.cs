using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>Time to a native biological-age boundary. Native aging interpolates child/adult rates over ages 11..20;
/// integration converts biological years into game days without extending the investment horizon.</summary>
internal static class ChildWorkRules
{
    internal static double DayAt(ChildWorkRead child, int years)
    {
        if (years <= child.BiologicalYears) return 0;
        DomainException.Require(double.IsFinite(child.BiologicalYears) && child.BiologicalYears >= 0
            && double.IsFinite(child.ChildAgingRate) && child.ChildAgingRate >= 0
            && double.IsFinite(child.AdultAgingRate) && child.AdultAgingRate >= 0
            && double.IsFinite(child.GeneAgingFactor) && child.GeneAgingFactor >= 0, "child aging rates");
        if (child.GeneAgingFactor == 0) return double.PositiveInfinity;
        var from = child.BiologicalYears;
        var time = 0d;
        foreach (var boundary in new[] { 11d, 20d, (double)years }.Where(a => a > from && a <= years).Distinct().Order())
        {
            var to = boundary;
            double Rate(double age) => age <= 11 ? child.ChildAgingRate : age >= 20 ? child.AdultAgingRate
                : child.ChildAgingRate + (child.AdultAgingRate - child.ChildAgingRate) * (age - 11) / 9;
            var start = Rate(from);
            var end = Rate(to);
            if (start <= 0 || end <= 0) return double.PositiveInfinity;
            time += start == end ? (to - from) / start : (to - from) * Math.Log(end / start) / (end - start);
            from = to;
        }
        return time * CalendarRules.YearDays / child.GeneAgingFactor;
    }
}
