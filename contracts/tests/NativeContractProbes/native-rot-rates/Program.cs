using System;
using RimGovernor.Native;

internal static class NativeRotRatesProbe
{
    internal static void Invoke()
    {
        Verse.GenTemperature.Rate = c => c * c + .125f;
        var rates = NativeRotRates.Sample();
        Require(rates.Count == 1274, "every whole degree in the declared range is sampled");
        foreach (var pair in rates) Require(pair.Value == pair.Key * pair.Key + .125, "native game function supplies each sample");
        Require(NativeRotRates.At(3.25f) == 3.25 * 3.25 + .125, "stack rate uses its unrounded ambient temperature");
        Verse.GenTemperature.Rate = _ => float.NaN;
        var refused = false;
        try { NativeRotRates.Sample(); } catch (InvalidOperationException) { refused = true; }
        Require(refused, "a failed native rate aborts the whole read");
        Console.WriteLine("native-rot-rates: native function delegation, whole-degree census and missing fact refusal passed");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}

namespace Verse
{
    internal static class GenTemperature
    {
        internal static Func<float, float> Rate = null!;
        internal static float RotRateAtTemperature(float temperature) => Rate(temperature);
    }
}
