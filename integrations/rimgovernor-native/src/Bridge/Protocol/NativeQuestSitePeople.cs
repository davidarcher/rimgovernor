#nullable enable
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    internal static class NativeQuestSitePeople
    {
        internal static IEnumerable<Obs.QuestObjective> Read(Quest quest)
        {
            var root = quest.root?.defName;
            if (root != "OpportunitySite_DownedRefugee" && root != "OpportunitySite_PrisonerWillingToJoin") yield break;
            // The generated pawn reward retains the exact target after the
            // site part's ThingOwner transfers it onto the destination map.
            var pawns = quest.PartsListForReading.OfType<QuestPart_Choice>().SelectMany(p => p.choices)
                .SelectMany(c => c.rewards).OfType<Reward_Pawn>().Select(r => r.pawn)
                .Where(p => p != null && !p.Destroyed && !p.Dead).Distinct().OrderBy(p => p.GetUniqueLoadID()).ToArray();
            if (pawns.Length == 0) yield break;
            var row = new Obs.QuestObjective { Kind = Obs.QuestObjectiveKind.RescuePawns, Active = quest.State == QuestState.Ongoing };
            row.PawnIds.Add(pawns.Select(p => p.GetUniqueLoadID()));
            yield return row;
        }
    }
}
