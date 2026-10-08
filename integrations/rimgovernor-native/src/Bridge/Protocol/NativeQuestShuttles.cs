#nullable enable
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    internal static class NativeQuestShuttles
    {
        internal static List<Obs.QuestShuttleState> Read(Quest quest)
        {
            var rows = new List<Obs.QuestShuttleState>();
            foreach (var shuttle in Find.Maps.SelectMany(m => m.listerThings.AllThings).Select(t => t.TryGetComp<CompShuttle>()))
            {
                if (shuttle == null || shuttle.shipParent == null || !quest.PartsListForReading.Any(p => p.QuestPartReserves(shuttle.shipParent))) continue;
                var gizmos = shuttle.CompGetGizmosExtra().ToArray();
                var row = new Obs.QuestShuttleState { ShuttleId = shuttle.parent.GetUniqueLoadID(),
                    AutoloadAvailable = gizmos.OfType<Command_Toggle>().Any(g => !g.Disabled && g.defaultLabel == "CommandAutoloadTransporters".Translate().ToString()),
                    Autoload = shuttle.Autoload, Loading = shuttle.Transporter.LoadingInProgressOrReadyToLaunch,
                    AllRequiredLoaded = shuttle.AllRequiredThingsLoaded,
                    ManualLaunchAvailable = shuttle.CanLaunch.Accepted && shuttle.AllRequiredThingsLoaded && gizmos.OfType<Command_Action>().Any(g => !g.Disabled && g.defaultLabel == "CommandSendShuttle".Translate().ToString()),
                    RequiredColonistCount = shuttle.requiredColonistCount };
                row.PawnIds.Add(shuttle.RequiredPawns.Select(p => p.GetUniqueLoadID()));
                row.LoadedPawnIds.Add(shuttle.Transporter.innerContainer.OfType<Pawn>().Select(p => p.GetUniqueLoadID()));
                rows.Add(row);
            }
            return rows;
        }

        internal static void LodgerMood(Quest quest, Obs.QuestObjective row, IEnumerable<Pawn> pawns)
        {
            var guests = pawns.ToArray();
            var thresholds = quest.PartsListForReading.OfType<QuestPart_MoodBelow>().Where(p => p.pawns.Any(guests.Contains)).ToArray();
            if (thresholds.Length > 0) row.MinimumMood = thresholds.Max(p => p.threshold);
            foreach (var pawn in guests)
            {
                var state = new Obs.QuestLodgerMood { PawnId = pawn.GetUniqueLoadID() };
                if (pawn.Spawned && !pawn.Dead && pawn.needs?.mood != null) state.Mood = pawn.needs.mood.CurLevel;
                row.LodgerMoods.Add(state);
            }
        }
    }
}
