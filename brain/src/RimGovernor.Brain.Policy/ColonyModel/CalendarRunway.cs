namespace RimGovernor.Brain.Policy;

/// <summary>Exact crossings of the piecewise-linear seasonal food reserve over the native daily calendar.</summary>
internal static class CalendarRunway
{
    internal static double? Crossing(RimGovernor.Brain.GameFacts.Observations.FoodClimate calendar, double from, double until, double stock, double net, double consumption)
    {
        if (consumption <= 0) return null;
        var now = from;
        while (now <= until)
        {
            var (gap, slope, boundary) = Segment(calendar.GrowingYear, now);
            var surplus = stock + net * (now - from) - consumption * gap;
            if (surplus <= 0) return now;
            var end = Math.Min(until, boundary);
            var fall = consumption * slope - net;
            if (fall > 0 && surplus / fall <= end - now) return now + surplus / fall;
            if (end == until)
            {
                var endpoint = Segment(calendar.GrowingYear, until).Gap;
                return stock + net * (until - from) <= consumption * endpoint ? until : null;
            }
            now = end;
        }
        return null;
    }

    private static (double Gap, double Slope, double Boundary) Segment(EquatableArray<bool> year, double day)
    {
        var whole = Math.Floor(day);
        var fraction = day - whole;
        var index = (int)(whole % CalendarRules.YearDays);
        int Until(int start, bool growing)
        {
            for (var i = 0; i < year.Count; i++)
                if (year[(start + i) % year.Count] == growing) return i;
            return year.Count;
        }
        var boundary = whole + 1;
        if (!year[index])
        {
            var wait = Until(index, true);
            if (wait == year.Count) return (CalendarRules.YearDays, 0, boundary);
            var gap = wait - fraction + CalendarRules.FirstHarvestDays;
            var cappedUntil = whole + wait + CalendarRules.FirstHarvestDays - CalendarRules.YearDays;
            if (day < cappedUntil)
                return (CalendarRules.YearDays, 0, Math.Min(boundary, cappedUntil));
            return (gap, -1, boundary);
        }
        var remaining = Until(index, false);
        if (remaining == year.Count) return (0, 0, boundary);
        var dormant = Until(index + remaining, true);
        var reserve = Math.Min(CalendarRules.YearDays, dormant + CalendarRules.FirstHarvestDays);
        var lead = reserve + CalendarRules.FieldCycles * CalendarRules.FirstHarvestDays;
        var rampEnd = whole + remaining - 1;
        var rampStart = rampEnd - lead;
        if (day < rampStart) return (0, 0, Math.Min(boundary, rampStart));
        if (day >= rampEnd) return (reserve, 0, boundary);
        return (reserve * (day - rampStart) / lead, reserve / lead, Math.Min(boundary, rampEnd));
    }
}
