using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimGovernor.Native
{
    internal static class NativeQuestRefugees
    {
        internal static IEnumerable<RimGovernor.Brain.GameFacts.Observations.QuestObjective> Read(Quest quest)
        {
            var root = quest.root?.defName;
            if (root != "RefugeePodCrash" && root != "RefugeePodCrash_Baby") yield break;
            var pawns = quest.PartsListForReading.OfType<QuestPart_DropPods>().SelectMany(p => p.Things)
                .OfType<Pawn>().Where(p => !p.Destroyed).Distinct().OrderBy(p => p.GetUniqueLoadID()).ToArray();
            if (pawns.Length == 0) yield break;
            yield return new RimGovernor.Brain.GameFacts.Observations.QuestObjective { Kind = RimGovernor.Brain.GameFacts.Observations.QuestObjectiveKind.RescuePawns, Goal = new RimGovernor.Brain.GameFacts.Observations.QuestObjectiveGoal.Uncounted(), Timing = new RimGovernor.Brain.GameFacts.Observations.QuestObjectiveTiming.Untimed(),
                PawnIds = [.. pawns.Select(p => new RimGovernor.Brain.Domain.PawnId(p.GetUniqueLoadID()))], Active = quest.State == QuestState.Ongoing,
                MinimumMood = new RimGovernor.Brain.GameFacts.Observations.QuestMoodFloor.NoFloor(), LodgerMoods = [] };
        }
    }
}
