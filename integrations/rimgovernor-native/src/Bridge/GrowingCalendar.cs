#nullable enable

using System;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    /// <summary>The tile's seasonal crop calendar the typed colony facts'
    /// food climate reports: growing-day counts on a daily temperature sample
    /// and the native season and day of year.</summary>
    internal static class GrowingCalendar
    {
        internal static int GrowingDaysRemaining(Map map)
        {
            for (var days = 0; days < GenDate.DaysPerYear; days++)
            {
                var temperature = GenTemperature.GetTemperatureFromSeasonAtTile(
                    GenTicks.TicksAbs + days * GenDate.TicksPerDay, map.Tile);
                if (temperature < Plant.DefaultMinOptimalGrowthTemperature
                    || temperature > Plant.DefaultMaxOptimalGrowthTemperature) return days;
            }
            return GenDate.DaysPerYear;
        }

        /// <summary>Days until the tile's seasonal temperature next enters the
        /// crop growth range: 0 while it is inside it now, DaysPerYear when it
        /// never is. The same daily sample GrowingDaysRemaining walks.</summary>
        internal static int GrowingDaysUntil(Map map)
        {
            for (var days = 0; days < GenDate.DaysPerYear; days++)
            {
                var temperature = GenTemperature.GetTemperatureFromSeasonAtTile(
                    GenTicks.TicksAbs + days * GenDate.TicksPerDay, map.Tile);
                if (temperature >= Plant.DefaultMinOptimalGrowthTemperature
                    && temperature <= Plant.DefaultMaxOptimalGrowthTemperature) return days;
            }
            return GenDate.DaysPerYear;
        }

        /// <summary>Length of the current or coming non-growing stretch on the
        /// same daily sample: days from the first out-of-range day to the next
        /// in-range one. 0 while the tile never leaves the crop range,
        /// DaysPerYear when it never enters it; equal to GrowingDaysUntil while
        /// crops do not grow now, so the seasonal harvest gap a colony holds
        /// stored food against is the same figure on both sides of the
        /// frost.</summary>
        internal static int NonGrowingDays(Map map)
        {
            Func<int, bool> growing = day => {
                var temperature = GenTemperature.GetTemperatureFromSeasonAtTile(
                    GenTicks.TicksAbs + day * GenDate.TicksPerDay, map.Tile);
                return temperature >= Plant.DefaultMinOptimalGrowthTemperature
                    && temperature <= Plant.DefaultMaxOptimalGrowthTemperature;
            };
            var first = 0;
            while (first < GenDate.DaysPerYear && growing(first)) first++;
            if (first == GenDate.DaysPerYear) return 0;
            for (var day = first + 1; day < GenDate.DaysPerYear; day++)
                if (growing(day)) return day - first;
            // The sample repeats yearly: with no re-entry inside the year the
            // stretch runs to the growing day 0 a year on.
            return first == 0 ? GenDate.DaysPerYear : GenDate.DaysPerYear - first;
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
