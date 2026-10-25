namespace RimGovernor.Brain.Policy;


/// <summary>
/// The map tile's native growing calendar, read with the other routine facts. <see cref="GrowingDays"/> is the
/// tile's growing period per year; <see cref="GrowingDaysRemaining"/> the days until the seasonal temperature
/// next leaves the crop growth range (YearDays while it never does); <see cref="GrowingDaysUntil"/> the days
/// until it next re-enters it (0 while crops grow now, YearDays when the tile never grows);
/// <see cref="NonGrowingDays"/> the length of the current or coming non-growing stretch on the same walk (0
/// while the tile never leaves the range, equal to GrowingDaysUntil while crops do not grow).
/// GrowingDays, Season and DayOfYear are informational.
/// </summary>
public sealed record Calendar
{
    /// <summary>Native growing eligibility sampled daily from this review origin; preserves every seasonal window.</summary>
    public EquatableArray<bool> GrowingYear { get; init; }

    public string Season { get; init; } = "";

    public long DayOfYear { get; init; }

    public double GrowingDays { get; init; }

    public double GrowingDaysRemaining { get; init; }

    public double GrowingDaysUntil { get; init; }

    public double NonGrowingDays { get; init; }

    /// <summary>
    /// Whether every day count is a finite value inside one year and the day of year is inside it.
    /// </summary>
    public bool Valid()
    {
        foreach (var days in new[] { GrowingDays, GrowingDaysRemaining, GrowingDaysUntil, NonGrowingDays })
        {
            if (double.IsNaN(days) || double.IsInfinity(days) || days < 0 || days > CalendarRules.YearDays)
            {
                return false;
            }
        }

        return DayOfYear >= 0 && DayOfYear < CalendarRules.YearDays;
    }
}

/// <summary>What stands in a growing zone: plants with their means, or nothing planted.</summary>
public abstract record CropStand
{
    private CropStand() { }

    /// <summary>
    /// <paramref name="FertilePlanted"/> cells hold plants on fertile soil (at least one cell is planted);
    /// <paramref name="GrowthMean"/> and <paramref name="FertilityMean"/> are the plants' mean growth fraction and fertility factor.
    /// </summary>
    public sealed record Sown(long FertilePlanted, double GrowthMean, double FertilityMean) : CropStand;

    /// <summary>No plant is sown: the zone grows nothing and has no harvest to wait for.</summary>
    public sealed record Fallow : CropStand;
}

/// <summary>
/// The raw state of one growing zone's crop as native mirrors it: what stands in the zone, the outdoor temperature and
/// the crop's native growth range. Light is not part of it: the
/// factor is an instantaneous reading that drops to zero every night, so it says nothing about days to harvest.
/// </summary>
public sealed record CropGrowth
{
    public required CropStand Stand { get; init; }

    /// <summary>The outdoor temperature at the zone, finite.</summary>
    public required double Temperature { get; init; }

    public Fact<double> MinGrowth { get; init; }

    public Fact<double> MinOptimal { get; init; }

    public Fact<double> MaxOptimal { get; init; }

    public Fact<double> MaxGrowth { get; init; }

    /// <summary>
    /// The crop's growth-rate factor at the current outdoor temperature: 1 inside the optimal range, falling
    /// linearly to 0 at the growth limits. Unknown while a range fact is missing or the range is not
    /// ordered.
    /// </summary>
    public Fact<double> TemperatureFactor()
    {
        var t = Temperature;
        var lk = MinGrowth.TryGetValue(out var lo);
        var l2k = MinOptimal.TryGetValue(out var lo2);
        var h2k = MaxOptimal.TryGetValue(out var hi2);
        var hk = MaxGrowth.TryGetValue(out var hi);
        if (!lk || !l2k || !h2k || !hk || !(lo <= lo2 && lo2 <= hi2 && hi2 <= hi))
        {
            return Unknown<double>();
        }

        if (t <= lo || t >= hi)
        {
            return Known(0.0);
        }

        if (t < lo2)
        {
            return Known((t - lo) / (lo2 - lo));
        }

        if (t > hi2)
        {
            return Known((hi - t) / (hi - hi2));
        }

        return Known(1.0);
    }

    /// <summary>The planted cells that grow right now: fertile and inside the crop's growth temperature range. Unknown while a fact is missing.</summary>
    public Fact<long> GrowingCells()
    {
        if (!TemperatureFactor().TryGetValue(out var factor))
        {
            return Unknown<long>();
        }

        return factor <= 0 || Stand is not CropStand.Sown sown ? Known(0L) : Known(sown.FertilePlanted);
    }
}

public static class CalendarRules
{
    /// <summary>The native 60-day year every calendar fact is bounded by.</summary>
    public const int YearDays = 60;

    // The grow cycle the harvest gap adds past the first growing day: the fastest starter crop (rice) at its
    // native growth rate on ordinary soil. FieldCycles is the field budget's cycles per harvest
    // (FieldCoverage), the lead a sown field needs before the frost.
    internal const double FirstHarvestDays = 3;
    internal const double FieldCycles = 2.5;

    /// <summary>
    /// A bound on the fertility divisor so a barren plant reads as a very long wait instead of an infinite one.
    /// </summary>
    internal const double MinFertilityFactor = 0.01;

    /// <summary>
    /// The stretch without a field harvest the colony must hold stored food against right now. While crops do
    /// not grow it is the wait until growth resumes; while they do it is the coming non-growing stretch the
    /// same daily walk reports (zero on a tile that grows all year), phased in linearly over the gap plus one
    /// field cycle before the frost so the larder fills as the frost nears instead of the whole summer
    /// reading as a deficit. The phase-in is complete on the last growing day (one day remaining), so the
    /// thresholds the frost brings are the ones the colony already held. Either way one grow cycle of the
    /// fastest starter crop is added, because a harvest lags the first growing day, and the whole is capped
    /// at one year. Unknown while the calendar is unknown or invalid.
    ///
    /// An observed growth pause (CropPauseDays: an eclipse, volcanic winter or cold snap with a
    /// remaining-duration read) extends the gap. While crops grow it is added outright, since the next harvest
    /// waits for the pause to lift; while they do not, the pause plus the first harvest cycle stands in for a
    /// shorter seasonal wait. A pause on an unknown calendar is the whole gap.
    /// </summary>
    public static Fact<double> HarvestGapDays(Fact<Calendar> calendar, EquatableArray<DisasterCondition> conditions)
    {
        var pause = CropRiskRules.CropPauseDays(conditions);
        if (!calendar.TryGetValue(out var c) || !c.Valid())
        {
            if (pause <= 0)
            {
                return Unknown<double>();
            }

            return Known(Math.Min(YearDays, pause + FirstHarvestDays));
        }

        if (c.GrowingDaysUntil > 0)
        {
            return Known(Math.Min(YearDays, Math.Max(c.GrowingDaysUntil, pause) + FirstHarvestDays));
        }

        var gap = 0.0;
        if (c.NonGrowingDays > 0)
        {
            gap = Math.Min(YearDays, c.NonGrowingDays + FirstHarvestDays);
            var lead = gap + (FieldCycles * FirstHarvestDays);
            gap *= Math.Max(0, Math.Min(1, 1 - ((c.GrowingDaysRemaining - 1) / lead)));
        }

        return Known(Math.Min(YearDays, gap + pause));
    }

    /// <summary>
    /// Returns the policy with its stored-food thresholds widened by the harvest gap the calendar
    /// reports: FoodMinDays and FoodTargetDays both grow by the gap, so a colony fills its fields and larder against the coming winter
    /// instead of holding the flat summer thresholds into the first frost. FootholdFoodDays and every other
    /// field are unchanged, an unknown calendar with no observed growth pause leaves the policy as
    /// configured, and the result still satisfies Validate.
    /// </summary>
    public static RoundsPolicy Seasonal(this RoundsPolicy p, Fact<Calendar> calendar, EquatableArray<DisasterCondition> conditions)
    {
        if (!HarvestGapDays(calendar, conditions).TryGetValue(out var gap) || gap <= 0 || p.FoodTargetDays <= 0)
        {
            return p;
        }

        var target = Math.Min(YearDays, p.FoodTargetDays + gap);
        if (target <= p.FoodTargetDays)
        {
            return p;
        }

        return p with
        {
            FoodMinDays = Math.Min(p.FoodMinDays + gap, target - (p.FoodTargetDays - p.FoodMinDays)),
            FoodTargetDays = target,
        };
    }

    /// <summary>
    /// The days until the field's plants finish growing, counted from the mean planted growth at the current
    /// temperature and fertility, and extended by the calendar: crops that do not grow now wait
    /// GrowingDaysUntil first, and growth that outlasts the growing days left before the frost resumes after
    /// the non-growing stretch. It is capped at one year. Unknown (never zero) while a fact the walk needs is
    /// missing, no plant is sown, or the crop cannot grow at the current temperature with no growing day
    /// ahead on the calendar.
    /// </summary>
    public static Fact<double> HarvestLeadDays(CropGrowth g, Fact<double> growDays, Fact<Calendar> calendar)
    {
        var dk = growDays.TryGetValue(out var days);
        var tk = g.TemperatureFactor().TryGetValue(out var factor);
        var ck = calendar.TryGetValue(out var c);
        if (
            g.Stand is not CropStand.Sown sown
            || !dk
            || !tk
            || !ck
            || !c!.Valid()
            || double.IsNaN(days)
            || double.IsInfinity(days)
            || days <= 0
        )
        {
            return Unknown<double>();
        }

        var need = Math.Max(0, 1 - sown.GrowthMean) * days / Math.Max(MinFertilityFactor, sown.FertilityMean);
        if (factor <= 0)
        {
            if (c.GrowingDaysUntil <= 0)
            {
                return Unknown<double>();
            }

            return Known(Math.Min(YearDays, c.GrowingDaysUntil + need));
        }

        var lead = need / factor;
        if (lead > c.GrowingDaysRemaining)
        {
            lead += c.NonGrowingDays;
        }

        return Known(Math.Min(YearDays, lead));
    }
}
