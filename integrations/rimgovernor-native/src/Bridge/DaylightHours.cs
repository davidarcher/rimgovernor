#nullable enable
using System;

namespace HomeBridge.BridgeTools
{
    // Hours of daylight in one day, counted from the sun glow at each of the day's 24 hour marks. Which glow is daytime is
    // the game's (GenCelestial.IsDaytime), passed in so the count is independent of the game assemblies.
    internal static class DaylightHours
    {
        internal const int HoursPerDay = 24;
        internal const int TicksPerHour = 2500;

        // glowAtTickOffset gives the sun glow that many ticks after the start of the sampled day.
        internal static float Of(Func<int, float> glowAtTickOffset, Func<float, bool> daytime)
        {
            var hours = 0;
            for (var hour = 0; hour < HoursPerDay; hour++)
            {
                if (daytime(glowAtTickOffset(hour * TicksPerHour))) hours++;
            }
            return hours;
        }
    }
}
