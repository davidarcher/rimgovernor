#nullable enable

using RimWorld;

namespace HomeBridge.BridgeTools
{
    /// <summary>Tick and day conversions through the game's own date constants
    /// (GenDate.TicksPerDay, TicksPerHour), the same values the catalog's
    /// constants block ships. No tick literal belongs anywhere else.</summary>
    internal static class GameTime
    {
        /// <summary>A per-tick rate as a per-day rate.</summary>
        internal static float PerDay(float perTick) => perTick * GenDate.TicksPerDay;
        internal static double PerDay(double perTick) => perTick * GenDate.TicksPerDay;

        /// <summary>A day count as ticks.</summary>
        internal static double Ticks(double days) => days * GenDate.TicksPerDay;

        /// <summary>A tick count as days.</summary>
        internal static float Days(float ticks) => ticks / GenDate.TicksPerDay;
        internal static double Days(double ticks) => ticks / GenDate.TicksPerDay;

        /// <summary>A tick count as hours.</summary>
        internal static float Hours(float ticks) => ticks / GenDate.TicksPerHour;
        internal static double Hours(double ticks) => ticks / GenDate.TicksPerHour;
    }
}
