#nullable enable
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    internal static class NativeQuestGravEngine
    {
        internal static IEnumerable<Obs.QuestObjective> Read(Quest quest)
        {
            if (quest.root?.defName != "MechanoidSignal") yield break;
            var engines = quest.QuestLookTargets.Select(t => t.Thing).OfType<Building_GravEngine>().Distinct().ToArray();
            if (engines.Length != 1)
            {
                yield return new Obs.QuestObjective { Kind = Obs.QuestObjectiveKind.InspectGravEngine };
                yield break;
            }
            var engine = engines[0];
            var row = new Obs.QuestGravEngine { EngineId = engine.GetUniqueLoadID(), Spawned = engine.Spawned,
                Inspected = Find.ResearchManager.gravEngineInspected };
            if (engine.Spawned)
            {
                row.MapId = engine.Map.uniqueID;
                row.Cell = new Common.Cell { X = engine.Position.x, Z = engine.Position.z };
                var pawns = engine.Map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && !p.Drafted && !p.InMentalState).ToArray();
                row.EligiblePawnIds.Add(pawns.Where(p => p.CanReserveAndReach(engine, PathEndMode.Touch, Danger.None)).Select(p => p.GetUniqueLoadID()));
                row.InspectingPawnIds.Add(pawns.Where(p => p.CurJob?.def == JobDefOf.InspectGravEngine && p.CurJob.targetA.Thing == engine).Select(p => p.GetUniqueLoadID()));
            }
            yield return new Obs.QuestObjective { Kind = Obs.QuestObjectiveKind.InspectGravEngine, GravEngine = row };
        }
    }
}
