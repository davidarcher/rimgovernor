using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using RimWorld.QuestGen;
using Verse;
using Tick = RimGovernor.Brain.Domain.Tick;

namespace RimGovernor.Native
{
    internal static class NativeQuestSurvey
    {
        private static readonly AccessTools.FieldRef<QuestPart_ScannerDurationRemainingAlert, Site> Site = AccessTools.FieldRefAccess<QuestPart_ScannerDurationRemainingAlert, Site>("site");
        private static readonly AccessTools.FieldRef<QuestPart_ScannerDurationRemainingAlert, Thing> Scanner = AccessTools.FieldRefAccess<QuestPart_ScannerDurationRemainingAlert, Thing>("scanner");
        private static readonly AccessTools.FieldRef<QuestPart_ScannerDurationRemainingAlert, int> Duration = AccessTools.FieldRefAccess<QuestPart_ScannerDurationRemainingAlert, int>("duration");
        private static readonly AccessTools.FieldRef<QuestPart_ScannerDurationRemainingAlert, int> End = AccessTools.FieldRefAccess<QuestPart_ScannerDurationRemainingAlert, int>("endTick");

        internal static IEnumerable<RimGovernor.Brain.GameFacts.Observations.QuestObjective> Read(Quest quest)
        {
            foreach (var part in quest.PartsListForReading)
            {
                if (!(part is QuestPart_ScannerDurationRemainingAlert alert)) continue;
                var site = Site(alert);
                if (site == null) continue;
                var scanner = Scanner(alert);
                var condition = scanner == null ? RimGovernor.Brain.GameFacts.Observations.SurveyScannerCondition.Absent
                    : scanner.Destroyed ? RimGovernor.Brain.GameFacts.Observations.SurveyScannerCondition.Destroyed : RimGovernor.Brain.GameFacts.Observations.SurveyScannerCondition.Intact;
                var state = new RimGovernor.Brain.GameFacts.Observations.QuestSurveyScanner
                {
                    SiteId = site.GetUniqueLoadID(),
                    DurationTicks = Duration(alert),
                    Scanner = condition,
                    Hold = alert.State == QuestPartState.Enabled ? new RimGovernor.Brain.GameFacts.Observations.SurveyHold.Running(new Tick(End(alert))) : new RimGovernor.Brain.GameFacts.Observations.SurveyHold.Stopped(),
                    Complete = alert.State == QuestPartState.Disabled && condition == RimGovernor.Brain.GameFacts.Observations.SurveyScannerCondition.Intact && (scanner ?? throw new System.InvalidOperationException("Required native value is unavailable: scanner.")).Spawned
                        && End(alert) > 0 && GenTicks.TicksGame >= End(alert),
                };
                yield return new RimGovernor.Brain.GameFacts.Observations.QuestObjective { Kind = RimGovernor.Brain.GameFacts.Observations.QuestObjectiveKind.HoldSurveyScanner, Active = alert.State != QuestPartState.Disabled, Timing = new RimGovernor.Brain.GameFacts.Observations.QuestObjectiveTiming.Duration(Duration(alert)), SurveyScanner = state,
                    Goal = new RimGovernor.Brain.GameFacts.Observations.QuestObjectiveGoal.Uncounted(), PawnIds = [], MinimumMood = new RimGovernor.Brain.GameFacts.Observations.QuestMoodFloor.NoFloor(), LodgerMoods = [] };
            }
        }
    }
}
