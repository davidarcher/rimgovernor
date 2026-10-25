using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimGovernor.Native
{
    internal static class NativeQuestSitePeople
    {
        internal static IEnumerable<RimGovernor.Brain.GameFacts.Observations.QuestObjective> Read(Quest quest)
        {
            var root = quest.root?.defName;
            if (root != "OpportunitySite_DownedRefugee" && root != "OpportunitySite_PrisonerWillingToJoin") yield break;
            // The generated pawn reward retains the exact target after the
            // site part's ThingOwner transfers it onto the destination map.
            var pawns = quest.PartsListForReading.OfType<QuestPart_Choice>().SelectMany(p => p.choices)
                .SelectMany(c => c.rewards).OfType<Reward_Pawn>().Select(r => r.pawn)
                .Where(p => p != null && !p.Destroyed && !p.Dead).Distinct().OrderBy(p => p.GetUniqueLoadID()).ToArray();
            if (pawns.Length == 0) yield break;
            yield return new RimGovernor.Brain.GameFacts.Observations.QuestObjective { Kind = RimGovernor.Brain.GameFacts.Observations.QuestObjectiveKind.RescuePawns, Goal = new RimGovernor.Brain.GameFacts.Observations.QuestObjectiveGoal.Uncounted(), Timing = new RimGovernor.Brain.GameFacts.Observations.QuestObjectiveTiming.Untimed(),
                PawnIds = [.. pawns.Select(p => new RimGovernor.Brain.Domain.PawnId(p.GetUniqueLoadID()))], Active = quest.State == QuestState.Ongoing,
                MinimumMood = new RimGovernor.Brain.GameFacts.Observations.QuestMoodFloor.NoFloor(), LodgerMoods = [] };
        }
    }
}
