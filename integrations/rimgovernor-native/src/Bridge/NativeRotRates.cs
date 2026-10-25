using System;
using System.Collections.Generic;
using Verse;

namespace RimGovernor.Native;

internal static class NativeRotRates
{
    internal static double At(float temperature)
    {
        var rate = GenTemperature.RotRateAtTemperature(temperature);
        if (float.IsNaN(rate) || float.IsInfinity(rate) || rate < 0)
            throw new InvalidOperationException("GenTemperature.RotRateAtTemperature returned an invalid rate.");
        return rate;
    }

    // The supported temperature read range, sampled at each whole degree. No curve is reconstructed here.
    internal static IReadOnlyDictionary<int, double> Sample()
    {
        var rates = new Dictionary<int, double>();
        for (var temperature = -273; temperature <= 1000; temperature++) rates.Add(temperature, At(temperature));
        return rates;
    }
}
