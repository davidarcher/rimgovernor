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
public readonly record struct Calendar
{
    public Calendar() { }
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
    public required double Temperature { get; init; }
    public double MinGrowth { get; init; }
    public double MinOptimal { get; init; }
    public double MaxOptimal { get; init; }
    public double MaxGrowth { get; init; }

    public double TemperatureFactor()
    {
        if (!double.IsFinite(Temperature) || !double.IsFinite(MinGrowth) || !double.IsFinite(MaxGrowth)
            || !(MinGrowth <= MinOptimal && MinOptimal <= MaxOptimal && MaxOptimal <= MaxGrowth))
            throw new RequiredFactException("crop temperature band");
        return CropGrowthModel.TemperatureFactor(MinGrowth, MinOptimal, MaxOptimal, MaxGrowth, Temperature);
    }

    public long GrowingCells() => Stand is CropStand.Sown sown && TemperatureFactor() > 0 ? sown.FertilePlanted : 0;
}

/// <summary>A field's observed harvest timing: a live crop with a lead, an unsown zone, or a crop with no growing season ahead.</summary>
public abstract record HarvestTiming
{
    private HarvestTiming() { }
    public sealed record Expected(double Days) : HarvestTiming;
    public sealed record Fallow : HarvestTiming;
    public sealed record Dormant : HarvestTiming;
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
    /// at one year. An invalid calendar fails the policy contract.
    ///
    /// An observed growth pause (CropPauseDays: an eclipse, volcanic winter or cold snap with a
    /// remaining-duration read) extends the gap. While crops grow it is added outright, since the next harvest
    /// waits for the pause to lift; while they do not, the pause plus the first harvest cycle stands in for a
    /// shorter seasonal wait.
    /// </summary>
    public static double HarvestGapDays(Calendar calendar, EquatableArray<DisasterCondition> conditions)
    {
        var pause = CropRiskRules.CropPauseDays(conditions);
        var c = calendar;
        if (!c.Valid()) throw new DomainException("invalid calendar");

        if (c.GrowingDaysUntil > 0)
        {
            return Math.Min(YearDays, Math.Max(c.GrowingDaysUntil, pause) + FirstHarvestDays);
        }

        var gap = 0.0;
        if (c.NonGrowingDays > 0)
        {
            gap = Math.Min(YearDays, c.NonGrowingDays + FirstHarvestDays);
            var lead = gap + (FieldCycles * FirstHarvestDays);
            gap *= Math.Max(0, Math.Min(1, 1 - ((c.GrowingDaysRemaining - 1) / lead)));
        }

        return Math.Min(YearDays, gap + pause);
    }

    /// <summary>
    /// Returns the policy with its stored-food thresholds widened by the harvest gap the calendar
    /// reports: FoodMinDays and FoodTargetDays both grow by the gap, so a colony fills its fields and larder against the coming winter
    /// instead of holding the flat summer thresholds into the first frost. FootholdFoodDays and every other
    /// field are unchanged, a calendar with no harvest gap leaves the policy as
    /// configured, and the result still satisfies Validate.
    /// </summary>
    public static RoundsPolicy Seasonal(this RoundsPolicy p, Calendar calendar, EquatableArray<DisasterCondition> conditions)
    {
        var gap = HarvestGapDays(calendar, conditions);
        if (gap <= 0 || p.FoodTargetDays <= 0)
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
    /// the non-growing stretch. It is capped at one year. Unsown fields are fallow; a field with no growing window ahead is dormant.
    /// Invalid required growth inputs raise.
    /// </summary>
    public static HarvestTiming HarvestLeadDays(CropGrowth g, double growDays, Calendar calendar)
    {
        if (!calendar.Valid() || !double.IsFinite(growDays) || growDays <= 0) throw new RequiredFactException("crop harvest cycle");
        if (g.Stand is CropStand.Fallow) return new HarvestTiming.Fallow();
        var sown = (CropStand.Sown)g.Stand;
        var factor = g.TemperatureFactor();
        var need = Math.Max(0, 1 - sown.GrowthMean) * growDays / Math.Max(MinFertilityFactor, sown.FertilityMean);
        if (factor <= 0)
            return calendar.GrowingDaysUntil <= 0 ? new HarvestTiming.Dormant() : new HarvestTiming.Expected(Math.Min(YearDays, calendar.GrowingDaysUntil + need));
        var lead = need / factor;
        if (lead > calendar.GrowingDaysRemaining) lead += calendar.NonGrowingDays;
        return new HarvestTiming.Expected(Math.Min(YearDays, lead));
    }
}