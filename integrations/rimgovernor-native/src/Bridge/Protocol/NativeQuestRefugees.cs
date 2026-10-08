#nullable enable
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    internal static class NativeQuestRefugees
    {
        internal static IEnumerable<Obs.QuestObjective> Read(Quest quest)
        {
            var root = quest.root?.defName;
            if (root != "RefugeePodCrash" && root != "RefugeePodCrash_Baby") yield break;
            var pawns = quest.PartsListForReading.OfType<QuestPart_DropPods>().SelectMany(p => p.Things)
                .OfType<Pawn>().Where(p => !p.Destroyed).Distinct().OrderBy(p => p.GetUniqueLoadID()).ToArray();
            if (pawns.Length == 0) yield break;
            var row = new Obs.QuestObjective { Kind = Obs.QuestObjectiveKind.RescuePawns, Active = quest.State == QuestState.Ongoing };
            row.PawnIds.Add(pawns.Select(p => p.GetUniqueLoadID()));
            yield return row;
        }
    }
}
