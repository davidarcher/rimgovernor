
namespace HomeBridge.BridgeTools
{
    // Hyperfast's hard cap (#3604, #3605). Hyperfast is TimeSpeed.Ultrafast with
    // TickManager.UltraSpeedBoost on, and vanilla's boosted multiplier is 20000x,
    // so this cap is the only thing that holds Hyperfast at 30x (1800 ticks/s).
    // Native's TickRateMultiplier postfix (SupervisedPlayClamp.cs, installed at
    // NativeStartup) applies it whenever the boost is on, with or without a run
    // segment: a supervised Run at Hyperfast, the host tools' Hyperfast, or
    // anything else that sets the boost. Inside a run segment the one ceiling
    // (ClockCeiling.cs) still applies beneath it. Pure, so the contract probe
    // compiles it directly.
    internal static class HyperfastCap
    {
        internal const int NormalTicksPerSecond = 60;
        internal const int TicksPerSecond = 30 * NormalTicksPerSecond;
        internal const float Multiplier = (float)TicksPerSecond / NormalTicksPerSecond;

        // The game's multiplier, capped while the boost is on; an unboosted multiplier is left as it is.
        internal static float Apply(bool boost, float multiplier) => boost && multiplier > Multiplier ? Multiplier : multiplier;
    }
}
