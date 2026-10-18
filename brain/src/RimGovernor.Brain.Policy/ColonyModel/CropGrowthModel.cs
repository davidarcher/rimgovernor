using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Policy;

// The tier-2 colony model's crop growth (#3530 of #3470), integrated per twelfth. The game's rate is
// 1 / (60,000 x GrowDays) per tick while the plant is not resting, times the fertility, temperature and light factors
// (Plant.GrowthPerTick). Light is taken as outdoor daylight: the resting window is the daylight; an indoor zone under grow lights is
// not modelled (finding). The two factors below mirror PlantUtility (kept constants).

/// <summary>Growth of one crop, as a fraction of full growth per calendar day.</summary>
public static class CropGrowthModel
{
    /// <summary>
    /// <c>PlantUtility.GrowthRateFactorFor_Temperature</c>: a ramp from zero at the crop's minimum to one at its minimum optimum, one
    /// between the optima, a ramp down to zero at its maximum. Zero outside the band.
    /// </summary>
    public static double TemperatureFactor(double minimum, double minimumOptimal, double maximumOptimal, double maximum, double temperature)
    {
        if (temperature < minimumOptimal)
        {
            return InverseLerp(minimum, minimumOptimal, temperature);
        }

        return temperature > maximumOptimal ? InverseLerp(maximum, maximumOptimal, temperature) : 1;
    }

    /// <summary>The crop's temperature factor at the temperature; unknown while its band is unread.</summary>
    public static Fact<double> TemperatureFactor(CropChoice crop, double temperature) =>
        crop.MinGrowthTemperature.TryGetValue(out var lo) && crop.MinOptimalGrowthTemperature.TryGetValue(out var lo2)
            && crop.MaxOptimalGrowthTemperature.TryGetValue(out var hi2) && crop.MaxGrowthTemperature.TryGetValue(out var hi)
            ? Known(TemperatureFactor(lo, lo2, hi2, hi, temperature))
            : default;

    /// <summary><c>PlantUtility.GrowthRateFactorFor_Fertility</c>: <c>fertility x sensitivity + (1 - sensitivity)</c>, never below zero.</summary>
    public static Fact<double> FertilityFactor(CropChoice crop, double fertility) =>
        crop.FertilitySensitivity.TryGetValue(out var sensitivity) ? Known(Math.Max(0, (fertility * sensitivity) + (1 - sensitivity))) : default;

    /// <summary>
    /// Growth a calendar day at the field's mean fertility and the twelfth's temperature: the base rate (1 / GrowDays) times both
    /// factors times the fraction of the day the plant does not rest. Unknown while any of the crop's growth facts is unread.
    /// </summary>
    public static Fact<double> DailyGrowth(CropChoice crop, double meanFertility, double temperature)
    {
        if (!crop.GrowDays.TryGetValue(out var days) || !(days > 0) || !crop.RestingDayFraction.TryGetValue(out var resting)
            || !TemperatureFactor(crop, temperature).TryGetValue(out var heat) || !FertilityFactor(crop, meanFertility).TryGetValue(out var soil))
        {
            return default;
        }

        return Known(heat * soil * (1 - resting) / days);
    }

    /// <summary>
    /// Whether growth reaches <paramref name="needed"/> before the growing season ends: the daily growth of each twelfth from
    /// <paramref name="twelfth"/> on (the first only for <paramref name="firstSpan"/> days, the rest for a twelfth each), over at most
    /// <paramref name="horizonDays"/>. A twelfth with no growth before the need is met ends the season, so the crop will not mature;
    /// a horizon reached first is not a failure.
    /// </summary>
    public static bool Matures(IReadOnlyList<double> dailyByTwelfth, int twelfth, double firstSpan, double needed, double horizonDays)
    {
        var span = firstSpan;
        var elapsed = 0.0;
        while (needed > 0 && elapsed < horizonDays)
        {
            var rate = dailyByTwelfth[twelfth];
            if (!(rate > 0))
            {
                return false;
            }

            var days = Math.Min(span, horizonDays - elapsed);
            needed -= rate * days;
            elapsed += days;
            twelfth = (twelfth + 1) % dailyByTwelfth.Count;
            span = ColonyModel.TwelfthDays;
        }

        return true;
    }

    private static double InverseLerp(double a, double b, double value) => a == b ? 0 : Math.Clamp((value - a) / (b - a), 0, 1);
}
