#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using Verse.AI;
using Obs = RimGovernor.Contracts.Wire.Observations;
namespace HomeBridge.BridgeTools;

internal static class NativeResearchActivities
{
    /// <summary>
    /// ResearchManager.ResearchPerformed scales the work amount by this literal before the difficulty and cost factors
    /// (1.7 Assembly-CSharp; no named constant).
    /// </summary>
    const float PointsPerSpeedTick = 0.00825f;

    /// <summary>
    /// Every free colonist's research at every bench the current project can be researched at. The walk to a bench is
    /// not read here: its cell joins the coupled route locations, so the shared leg cache prices it.
    /// </summary>
    internal static Obs.ResearchActivityRead Read(Map map)
    {
        var project = Find.ResearchManager.GetProject();
        if (project == null) return new() { Known = true, Findings = ["research_no_current_project"] };
        // WorkGiverDefOf has no Research field: the typed route is the work type's own giver.
        var giver = WorkTypeDefOf.Research.workGiversByPriority.First(g => g.Worker is WorkGiver_Researcher);
        var scanner = (WorkGiver_Researcher)giver.Worker;
        var benches = map.listerBuildings.AllBuildingsColonistOfClass<Building_ResearchBench>()
            .Where(b => b.Spawned && project.CanBeResearchedAt(b, ignoreResearchBenchPowerStatus: false))
            .OrderBy(b => b.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
        var difficulty = Find.Storyteller.difficulty.researchSpeedFactor;
        var workers = new List<Obs.ResearchWorker>();
        foreach (var pawn in map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal))
        {
            if (pawn.Downed || pawn.Drafted || pawn.InMentalState || !NativeFoodActivity.Available(pawn) || pawn.WorkTagIsDisabled(giver.workTags)
                || (giver.workType != null && pawn.WorkTypeIsDisabled(giver.workType)) || scanner.MissingRequiredCapacity(pawn) != null) continue;
            var speed = pawn.GetStatValue(StatDefOf.ResearchSpeed);
            foreach (var bench in benches)
            {
                if (bench.IsForbidden(pawn) || !scanner.HasJobOnThing(pawn, bench, false)
                    || !pawn.CanReserveAndReach(bench, PathEndMode.InteractionCell, Danger.None)) continue;
                var cell = bench.def.hasInteractionCell ? bench.InteractionCell : bench.Position;
                var perTick = speed * bench.GetStatValue(StatDefOf.ResearchSpeedFactor) * PointsPerSpeedTick * difficulty
                    / project.CostFactor(pawn.Faction.def.techLevel);
                workers.Add(new(pawn.GetUniqueLoadID(), bench.GetUniqueLoadID(), new(cell.x, cell.z), project.defName,
                    giver.defName, JobDefOf.Research.defName, Fact.Known((double)perTick)));
            }
        }
        return new() { Known = true, Workers = EquatableArray.From(workers) };
    }
}
