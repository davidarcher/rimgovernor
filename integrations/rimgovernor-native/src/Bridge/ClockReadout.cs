using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using RimGovernor.Host.Sdk;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimGovernor.Native
{
    /// <summary>
    /// The clock in the bottom-right date readout reads as a time with seconds in place of vanilla's hour: "3:45:12 PM"
    /// or "15:45:12" for "3 PM" or "15h", following the game's 12/24-hour preference.
    /// <c>DateReadout.DateOnGUI</c> draws its hour from two 24-entry string caches indexed by the
    /// local hour; the prefix writes the current time into the entry about to be drawn in each cache,
    /// in that cache's form, so the game's own layout, width and tooltip are untouched. The time is local to the
    /// viewed tile, as vanilla's hour is. Installed from <see cref="NativeStartup"/> and skipped in
    /// the headless acceptance build.
    /// </summary>
    internal static class ClockReadout
    {
        private static int installed;
        private static List<string>? hours24;
        private static List<string>? hours12;

        internal static void Install()
        {
            if (HeadlessRim.HeadlessMode.Active) return;
            if (Interlocked.CompareExchange(ref installed, 1, 0) != 0) return;
            try
            {
                // Private caches, so no typed route: the names are DateReadout's own. Both lists are
                // readonly and only cleared and refilled by DateReadout.Reset, so the references hold.
                hours24 = AccessTools.Field(typeof(DateReadout), "fastHourStrings24h")?.GetValue(null) as List<string>;
                hours12 = AccessTools.Field(typeof(DateReadout), "fastHourStrings12h")?.GetValue(null) as List<string>;
                if (hours24 == null || hours12 == null) throw new MissingFieldException("DateReadout.fastHourStrings24h/12h");
                new Harmony("rimgovernor.clock-readout").Patch(
                    AccessTools.Method(typeof(DateReadout), nameof(DateReadout.DateOnGUI))
                        ?? throw new MissingMethodException("DateReadout.DateOnGUI"),
                    prefix: new HarmonyMethod(typeof(ClockReadout), nameof(BeforeDate)));
            }
            catch (Exception ex)
            {
                ModLog.Error("startup", "Clock readout installation failed: " + ex);
            }
        }

        // The viewed location is chosen as DateOnGUI chooses it: the selected world tile or object,
        // else the current map; with neither the original draws nothing.
        private static void BeforeDate()
        {
            Vector2 longLat;
            if (WorldRendererUtility.WorldSelected && Find.WorldSelector.SelectedTile.Valid) longLat = Find.WorldGrid.LongLatOf(Find.WorldSelector.SelectedTile);
            else if (WorldRendererUtility.WorldSelected && Find.WorldSelector.NumSelectedObjects > 0) longLat = Find.WorldGrid.LongLatOf(Find.WorldSelector.FirstSelectedObject.Tile);
            else if (Find.CurrentMap != null) longLat = Find.WorldGrid.LongLatOf(Find.CurrentMap.Tile);
            else return;
            var dayTick = GenDate.DayTick(Find.TickManager.TicksAbs, longLat.x);
            var hour = dayTick / GenDate.TicksPerHour;
            if (hours24 == null || hours12 == null) throw new InvalidOperationException("The clock readout caches are unavailable.");
            if (hours24.Count != GenDate.HoursPerDay || hours12.Count != GenDate.HoursPerDay) return;
            hours24[hour] = ClockFace.Format(dayTick, twelveHour: false);
            hours12[hour] = ClockFace.Format(dayTick, twelveHour: true);
        }
    }
}
