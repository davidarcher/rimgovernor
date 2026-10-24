namespace RimGovernor.Brain.Policy;

/// <summary>
/// The shared sky a crop channel is priced under: the growing calendar and the active game conditions.
/// Unknown facts price no risk and no pause, never a guessed one.
/// </summary>
public sealed record CropSeason
{
    public Fact<Calendar> Calendar { get; init; }

    public EquatableArray<DisasterCondition> Conditions { get; init; }
}

/// <summary>
/// What a planted field's risks derive from: the held cells of its zone and the share under open sky, and its
/// planted cells and the blighted plants standing in them. The zone cell counts are unknown until observed.
/// </summary>
public sealed record FieldExposure
{
    public Fact<long> ZoneCells { get; init; }

    public Fact<long> UnroofedCells { get; init; }

    public long Planted { get; init; }

    public long Blighted { get; init; }
}

public static class CropRiskRules
{
    /// <summary>
    /// The observed remaining duration, in game days, of the conditions that stop crops growing: an eclipse, a
    /// volcanic winter or a cold snap with a native remaining-duration read. Zero when none is active or the
    /// census is unknown. A crop's harvest lead extends by it.
    /// </summary>
    public static double CropPauseDays(EquatableArray<DisasterCondition> conditions)
    {
        var ticks = DisasterRules.ConditionRemainingTicks(conditions, DisasterRules.ConditionEclipse, DisasterRules.ConditionVolcanicWinter, DisasterRules.ConditionColdSnap);
        return ticks <= 0 ? 0 : (double)ticks / GameTime.TicksPerDay;
    }

    /// <summary>Whether the toxic-fallout condition is on the map.</summary>
    internal static bool ToxicFalloutActive(EquatableArray<DisasterCondition> conditions) =>
        conditions.Any(c => c.Definition == DisasterRules.ConditionToxicFallout);

    /// <summary>Part/whole as a fact in [0, 1]; unknown while either count is, the whole is not positive or the part exceeds it.</summary>
    internal static Fact<double> Share(Fact<long> part, Fact<long> whole)
    {
        var pk = part.TryGetValue(out var p);
        var wk = whole.TryGetValue(out var w);
        if (!pk || !wk || w <= 0 || p < 0 || p > w)
        {
            return Unknown<double>();
        }
        return Known((double)p / w);
    }

    /// <summary>
    /// The expected fraction of a field's delivery toxic fallout takes: while the condition is on, the plants
    /// under open sky die, so the weight is the unroofed share of the field's cells; zero without the
    /// condition. Unknown while the census or the roof data is.
    /// </summary>
    public static Fact<double> FalloutRisk(EquatableArray<DisasterCondition> conditions, FieldExposure e) =>
        ToxicFalloutActive(conditions) ? Share(e.UnroofedCells, e.ZoneCells) : Known(0.0);

    /// <summary>The expected fraction of a field's delivery blight takes: the blighted share of its planted cells. Unknown with nothing planted.</summary>
    public static Fact<double> BlightRisk(FieldExposure e) => Share(Known(e.Blighted), Known(e.Planted));

    /// <summary>
    /// The expected fraction of a field's delivery frost takes: when the harvest lead outlasts the growing
    /// days left, the share of the lead window spent in the non-growing stretch, for the part of the field
    /// under open sky. Zero while the harvest lands before the frost. Unknown while the lead, the calendar or
    /// the roof data is.
    /// </summary>
    public static Fact<double> FrostRisk(Fact<double> lead, Fact<Calendar> calendar, FieldExposure e)
    {
        var dk = lead.TryGetValue(out var days);
        var ck = calendar.TryGetValue(out var c);
        var ok = Share(e.UnroofedCells, e.ZoneCells).TryGetValue(out var open);
        if (!dk || !ck || !ok || !c!.Valid() || double.IsNaN(days) || days <= 0)
        {
            return Unknown<double>();
        }
        if (days <= c.GrowingDaysRemaining)
        {
            return Known(0.0);
        }
        return Known(open * Math.Min(1, c.NonGrowingDays / days));
    }

    /// <summary>
    /// Lists the known, positive risks of a field's expected fallout, blight and frost; an unknown weight is
    /// left out, never priced as zero or as a loss.
    /// </summary>
    internal static List<CandidateRisk> CropRisks(CropSeason season, Fact<double> lead, FieldExposure e)
    {
        var result = new List<CandidateRisk>();
        (CandidateRiskKind Kind, Fact<double> Weight)[] risks =
        [
            (CandidateRiskKind.Fallout, FalloutRisk(season.Conditions, e)),
            (CandidateRiskKind.Blight, BlightRisk(e)),
            (CandidateRiskKind.Frost, FrostRisk(lead, season.Calendar, e)),
        ];
        foreach (var (kind, weight) in risks)
        {
            if (weight.TryGetValue(out var w) && w > 0)
            {
                result.Add(new CandidateRisk(kind, Math.Min(1, w)));
            }
        }
        return result;
    }

    /// <summary>A harvest lead pushed back by the crop pause, capped at a year; an unknown lead stays unknown.</summary>
    internal static Fact<double> WithCropPause(Fact<double> lead, EquatableArray<DisasterCondition> conditions)
    {
        if (!lead.TryGetValue(out var days) || !FoodForecastRules.FoodNumber(days))
        {
            return lead;
        }
        return Known(Math.Min(CalendarRules.YearDays, days + CropPauseDays(conditions)));
    }
}
