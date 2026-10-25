using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using PawnId = RimGovernor.Brain.Domain.PawnId;

namespace RimGovernor.Native
{
    internal static class NativeQuestGravEngine
    {
        internal static IEnumerable<RimGovernor.Brain.GameFacts.Observations.QuestObjective> Read(Quest quest)
        {
            if (quest.root?.defName != "MechanoidSignal") yield break;
            var engines = quest.QuestLookTargets.Select(t => t.Thing).OfType<Building_GravEngine>().Distinct().ToArray();
            if (engines.Length != 1)
            {
                yield return new RimGovernor.Brain.GameFacts.Observations.QuestObjective { Kind = RimGovernor.Brain.GameFacts.Observations.QuestObjectiveKind.InspectGravEngine, Active = true, Goal = new RimGovernor.Brain.GameFacts.Observations.QuestObjectiveGoal.Uncounted(),
                    Timing = new RimGovernor.Brain.GameFacts.Observations.QuestObjectiveTiming.Untimed(), PawnIds = [], MinimumMood = new RimGovernor.Brain.GameFacts.Observations.QuestMoodFloor.NoFloor(), LodgerMoods = [] };
                yield break;
            }
            var engine = engines[0];
            var row = new RimGovernor.Brain.GameFacts.Observations.QuestGravEngine
            {
                EngineId = engine.GetUniqueLoadID(),
                Inspected = Find.ResearchManager.gravEngineInspected,
                Place = Place(engine),
            };
            yield return new RimGovernor.Brain.GameFacts.Observations.QuestObjective { Kind = RimGovernor.Brain.GameFacts.Observations.QuestObjectiveKind.InspectGravEngine, GravEngine = row, Active = true, Goal = new RimGovernor.Brain.GameFacts.Observations.QuestObjectiveGoal.Uncounted(),
                Timing = new RimGovernor.Brain.GameFacts.Observations.QuestObjectiveTiming.Untimed(), PawnIds = [], MinimumMood = new RimGovernor.Brain.GameFacts.Observations.QuestMoodFloor.NoFloor(), LodgerMoods = [] };
        }

        private static RimGovernor.Brain.GameFacts.Observations.GravEnginePlace Place(Building_GravEngine engine)
        {
            if (!engine.Spawned) return new RimGovernor.Brain.GameFacts.Observations.GravEnginePlace.Unspawned();
            var pawns = engine.Map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && !p.Drafted && !p.InMentalState).ToArray();
            return new RimGovernor.Brain.GameFacts.Observations.GravEnginePlace.Spawned(engine.Map.uniqueID,
                [.. pawns.Where(p => p.CanReserveAndReach(engine, PathEndMode.Touch, Danger.None)).Select(p => new PawnId(p.GetUniqueLoadID()))],
                [.. pawns.Where(p => p.CurJob?.def == JobDefOf.InspectGravEngine && p.CurJob.targetA.Thing == engine).Select(p => new PawnId(p.GetUniqueLoadID()))]);
        }
    }
}
