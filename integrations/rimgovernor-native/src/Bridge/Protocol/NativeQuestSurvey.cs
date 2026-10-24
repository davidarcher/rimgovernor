#nullable enable
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using RimWorld.QuestGen;
using Verse;
using Obs = RimGovernor.Contracts.Wire.Observations;
using Tick = RimGovernor.Brain.Domain.Tick;

namespace HomeBridge.BridgeTools
{
    internal static class NativeQuestSurvey
    {
        private static readonly AccessTools.FieldRef<QuestPart_ScannerDurationRemainingAlert, Site> Site = AccessTools.FieldRefAccess<QuestPart_ScannerDurationRemainingAlert, Site>("site");
        private static readonly AccessTools.FieldRef<QuestPart_ScannerDurationRemainingAlert, Thing> Scanner = AccessTools.FieldRefAccess<QuestPart_ScannerDurationRemainingAlert, Thing>("scanner");
        private static readonly AccessTools.FieldRef<QuestPart_ScannerDurationRemainingAlert, int> Duration = AccessTools.FieldRefAccess<QuestPart_ScannerDurationRemainingAlert, int>("duration");
        private static readonly AccessTools.FieldRef<QuestPart_ScannerDurationRemainingAlert, int> End = AccessTools.FieldRefAccess<QuestPart_ScannerDurationRemainingAlert, int>("endTick");

        internal static IEnumerable<Obs.QuestObjective> Read(Quest quest)
        {
            foreach (var part in quest.PartsListForReading)
            {
                if (!(part is QuestPart_ScannerDurationRemainingAlert alert)) continue;
                var site = Site(alert);
                if (site == null) continue;
                var scanner = Scanner(alert);
                var condition = scanner == null ? Obs.SurveyScannerCondition.Absent
                    : scanner.Destroyed ? Obs.SurveyScannerCondition.Destroyed : Obs.SurveyScannerCondition.Intact;
                var state = new Obs.QuestSurveyScanner
                {
                    SiteId = site.GetUniqueLoadID(),
                    DurationTicks = Duration(alert),
                    Scanner = condition,
                    Hold = alert.State == QuestPartState.Enabled ? new Obs.SurveyHold.Running(new Tick(End(alert))) : new Obs.SurveyHold.Stopped(),
                    Complete = alert.State == QuestPartState.Disabled && condition == Obs.SurveyScannerCondition.Intact && scanner!.Spawned
                        && End(alert) > 0 && GenTicks.TicksGame >= End(alert),
                };
                yield return new Obs.QuestObjective { Kind = Obs.QuestObjectiveKind.HoldSurveyScanner, Active = alert.State != QuestPartState.Disabled, Timing = new Obs.QuestObjectiveTiming.Duration(Duration(alert)), SurveyScanner = state,
                    Goal = new Obs.QuestObjectiveGoal.Uncounted(), PawnIds = [], MinimumMood = new Obs.QuestMoodFloor.NoFloor(), LodgerMoods = [] };
            }
        }
    }
}
