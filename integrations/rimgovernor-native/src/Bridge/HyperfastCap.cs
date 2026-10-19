#nullable enable

namespace HomeBridge.BridgeTools
{
    // Hyperfast's hard cap (#3604). Hyperfast is TimeSpeed.Ultrafast with
    // TickManager.UltraSpeedBoost on, and vanilla's boosted multiplier is 20000x,
    // so this cap is the only thing that holds Hyperfast at 30x (1800 ticks/s).
    // It is separate from the one ceiling (ClockCeiling.cs), which still applies
    // beneath it. Anything that runs the boost applies it to TickRateMultiplier:
    // a supervised Run at Hyperfast (SupervisedPlayClamp.cs), and a caller with
    // no run segment. Pure, so the contract probe compiles it directly.
    internal static class HyperfastCap
    {
        internal const int NormalTicksPerSecond = 60;
        internal const int TicksPerSecond = 30 * NormalTicksPerSecond;
        internal const float Multiplier = (float)TicksPerSecond / NormalTicksPerSecond;

        internal static float Apply(float multiplier) => multiplier > Multiplier ? Multiplier : multiplier;
    }
}
