
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    /// <summary>The tile's seasonal crop calendar the typed colony facts'
    /// food climate reports: growing-day counts on a daily temperature sample
    /// and the native season and day of year.</summary>
    internal static class GrowingCalendar
    {
        /// <summary>One native daily seasonal-temperature walk, including all growing windows in the year.</summary>
        internal static bool[] GrowingYear(Map map)
        {
            var year = new bool[GenDate.DaysPerYear];
            for (var day = 0; day < year.Length; day++)
            {
                var temperature = GenTemperature.GetTemperatureFromSeasonAtTile(
                    GenTicks.TicksAbs + day * GenDate.TicksPerDay, map.Tile);
                year[day] = temperature >= Plant.DefaultMinOptimalGrowthTemperature
                    && temperature <= Plant.DefaultMaxOptimalGrowthTemperature;
            }
            return year;
        }

        internal static int Until(bool[] year, int start, bool growing)
        {
            for (var day = 0; day < year.Length; day++)
                if (year[(start + day) % year.Length] == growing) return day;
            return year.Length;
        }
        /// <summary>The tile's native season name and 0-based day of the year.
        /// GenDate.Season takes the Vector2 (longitude, latitude) overload; the
        /// float overload's argument order differs.</summary>
        internal static (string season, int dayOfYear) Calendar(Map map)
        {
            var longLat = Find.WorldGrid.LongLatOf(map.Tile);
            long abs = GenTicks.TicksAbs;
            return (GenDate.Season(abs, longLat).ToString(), GenDate.DayOfYear(abs, longLat.x));
        }
    }
}
