#nullable enable
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using RimWorld.QuestGen;
using Verse;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    internal static class NativeQuestSurvey
    {
        private static readonly AccessTools.FieldRef<QuestPart_ScannerDurationRemainingAlert, Site> Site = AccessTools.FieldRefAccess<QuestPart_ScannerDurationRemainingAlert, Site>("site");
        private static readonly AccessTools.FieldRef<QuestPart_ScannerDurationRemainingAlert, Thing> Scanner = AccessTools.FieldRefAccess<QuestPart_ScannerDurationRemainingAlert, Thing>("scanner");
        private static readonly AccessTools.FieldRef<QuestPart_ScannerDurationRemainingAlert, int> Duration = AccessTools.FieldRefAccess<QuestPart_ScannerDurationRemainingAlert, int>("duration");
        private static readonly AccessTools.FieldRef<QuestPart_ScannerDurationRemainingAlert, int> End = AccessTools.FieldRefAccess<QuestPart_ScannerDurationRemainingAlert, int>("endTick");
        private static readonly AccessTools.FieldRef<QuestPart_ScannerDurationRemainingAlert, int> Raid = AccessTools.FieldRefAccess<QuestPart_ScannerDurationRemainingAlert, int>("fireRaidTick");

        internal static IEnumerable<Obs.QuestObjective> Read(Quest quest)
        {
            foreach (var part in quest.PartsListForReading)
            {
                if (!(part is QuestPart_ScannerDurationRemainingAlert alert)) continue;
                var site = Site(alert);
                if (site == null) continue;
                var state = new Obs.QuestSurveyScanner { SiteId = site.GetUniqueLoadID(), DurationTicks = Duration(alert) };
                var scanner = Scanner(alert);
                if (scanner != null) { state.ScannerId = scanner.GetUniqueLoadID(); state.Alive = !scanner.Destroyed; }
                state.Complete = alert.State == QuestPartState.Disabled && scanner?.Spawned == true && state.HasAlive && state.Alive && End(alert) > 0 && GenTicks.TicksGame >= End(alert);
                if (alert.State == QuestPartState.Enabled) { state.EndTick = End(alert); if (Raid(alert) > 0) state.RaidTick = Raid(alert); }
                yield return new Obs.QuestObjective { Kind = Obs.QuestObjectiveKind.HoldSurveyScanner, Active = alert.State != QuestPartState.Disabled, DurationTicks = Duration(alert), SurveyScanner = state };
            }
        }
    }
}
