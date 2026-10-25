using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Obs = RimGovernor.Contracts.Wire.Observations;
using PawnId = RimGovernor.Brain.Domain.PawnId;

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
                // leftToLoad is null until loading starts: nothing is pending then.
                var pending = shuttle.Transporter.leftToLoad?.Where(t => t.CountToTransfer > 0).SelectMany(t => t.things).OfType<Pawn>()
                    .Select(p => p.GetUniqueLoadID()).Distinct() ?? Enumerable.Empty<string>();
                rows.Add(new Obs.QuestShuttleState
                {
                    AutoloadAvailable = gizmos.OfType<Command_Toggle>().Any(g => !g.Disabled && g.defaultLabel == "CommandAutoloadTransporters".Translate().ToString()),
                    Autoload = shuttle.Autoload,
                    Loading = shuttle.Transporter.LoadingInProgressOrReadyToLaunch,
                    AllRequiredLoaded = shuttle.AllRequiredThingsLoaded,
                    ManualLaunchAvailable = shuttle.CanLaunch.Accepted && shuttle.AllRequiredThingsLoaded && gizmos.OfType<Command_Action>().Any(g => !g.Disabled && g.defaultLabel == "CommandSendShuttle".Translate().ToString()),
                    LoadedPawnIds = [.. shuttle.Transporter.innerContainer.OfType<Pawn>().Select(p => new PawnId(p.GetUniqueLoadID()))],
                    PendingPawnIds = [.. pending.Select(id => new PawnId(id))],
                });
            }
            return rows;
        }

        // The lodgers' mood floor (the highest QuestPart_MoodBelow threshold over them) and the mood of every lodger the
        // game tracks one for.
        internal static (Obs.QuestMoodFloor Floor, RimGovernor.Brain.Domain.EquatableArray<Obs.QuestLodgerMood> Moods) LodgerMood(Quest quest, Pawn[] guests)
        {
            var thresholds = quest.PartsListForReading.OfType<QuestPart_MoodBelow>().Where(p => p.pawns.Any(guests.Contains)).ToArray();
            Obs.QuestMoodFloor floor = thresholds.Length > 0 ? new Obs.QuestMoodFloor.Floor(thresholds.Max(p => p.threshold)) : new Obs.QuestMoodFloor.NoFloor();
            RimGovernor.Brain.Domain.EquatableArray<Obs.QuestLodgerMood> moods = [.. guests.Where(p => p.Spawned && !p.Dead && p.needs?.mood != null).Select(p => new Obs.QuestLodgerMood(p.needs.mood.CurLevel))];
            return (floor, moods);
        }
    }
}
