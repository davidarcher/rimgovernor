using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Obs = RimGovernor.Contracts.Wire.Observations;
using PawnId = RimGovernor.Brain.Domain.PawnId;

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
                yield return new Obs.QuestObjective { Kind = Obs.QuestObjectiveKind.InspectGravEngine, Active = true, Goal = new Obs.QuestObjectiveGoal.Uncounted(),
                    Timing = new Obs.QuestObjectiveTiming.Untimed(), PawnIds = [], MinimumMood = new Obs.QuestMoodFloor.NoFloor(), LodgerMoods = [] };
                yield break;
            }
            var engine = engines[0];
            var row = new Obs.QuestGravEngine
            {
                EngineId = engine.GetUniqueLoadID(),
                Inspected = Find.ResearchManager.gravEngineInspected,
                Place = Place(engine),
            };
            yield return new Obs.QuestObjective { Kind = Obs.QuestObjectiveKind.InspectGravEngine, GravEngine = row, Active = true, Goal = new Obs.QuestObjectiveGoal.Uncounted(),
                Timing = new Obs.QuestObjectiveTiming.Untimed(), PawnIds = [], MinimumMood = new Obs.QuestMoodFloor.NoFloor(), LodgerMoods = [] };
        }

        private static Obs.GravEnginePlace Place(Building_GravEngine engine)
        {
            if (!engine.Spawned) return new Obs.GravEnginePlace.Unspawned();
            var pawns = engine.Map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && !p.Drafted && !p.InMentalState).ToArray();
            return new Obs.GravEnginePlace.Spawned(engine.Map.uniqueID,
                [.. pawns.Where(p => p.CanReserveAndReach(engine, PathEndMode.Touch, Danger.None)).Select(p => new PawnId(p.GetUniqueLoadID()))],
                [.. pawns.Where(p => p.CurJob?.def == JobDefOf.InspectGravEngine && p.CurJob.targetA.Thing == engine).Select(p => new PawnId(p.GetUniqueLoadID()))]);
        }
    }
}
