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
        if (!double.IsFinite(temperature) || !double.IsFinite(minimum) || !double.IsFinite(minimumOptimal) || !double.IsFinite(maximumOptimal) || !double.IsFinite(maximum) || !(minimum <= minimumOptimal && minimumOptimal <= maximumOptimal && maximumOptimal <= maximum))
            throw new RequiredFactException("crop growth temperature band");
        if (temperature < minimumOptimal)
        {
            return InverseLerp(minimum, minimumOptimal, temperature);
        }

        return temperature > maximumOptimal ? InverseLerp(maximum, maximumOptimal, temperature) : 1;
    }

    /// <summary>The crop's native temperature factor at the given temperature.</summary>
    public static double TemperatureFactor(CropChoice crop, double temperature) =>
        TemperatureFactor(crop.MinGrowthTemperature, crop.MinOptimalGrowthTemperature, crop.MaxOptimalGrowthTemperature, crop.MaxGrowthTemperature, temperature);

    /// <summary>The native fertility factor, bounded below by zero.</summary>
    public static double FertilityFactor(CropChoice crop, double fertility)
    {
        if (!double.IsFinite(fertility) || fertility < 0 || !double.IsFinite(crop.FertilitySensitivity))
            throw new RequiredFactException("crop growth fertility");
        return Math.Max(0, (fertility * crop.FertilitySensitivity) + (1 - crop.FertilitySensitivity));
    }

    /// <summary>The growth fraction per calendar day from the crop's observed native growth inputs.</summary>
    public static double DailyGrowth(CropChoice crop, double meanFertility, double temperature)
    {
        if (!double.IsFinite(crop.GrowDays) || crop.GrowDays <= 0 || !double.IsFinite(crop.RestingDayFraction) || crop.RestingDayFraction < 0 || crop.RestingDayFraction >= 1)
        {
            throw new RequiredFactException("crop growth cycle");
        }
        return TemperatureFactor(crop, temperature) * FertilityFactor(crop, meanFertility) * (1 - crop.RestingDayFraction) / crop.GrowDays;
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
