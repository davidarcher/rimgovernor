#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using Verse.AI;
using Obs = RimGovernor.Contracts.Wire.Observations;
namespace HomeBridge.BridgeTools;

internal static class NativeResearchActivities
{
    // TickManager/TickList expose no public tick-order view. The shared-project final callback must
    // use native order: later pawns fail the research toil once the earlier pawn finishes its project.
    private static readonly AccessTools.FieldRef<TickManager, TickList> NormalTicks = AccessTools.FieldRefAccess<TickManager, TickList>("tickListNormal");
    private static readonly AccessTools.FieldRef<TickList, List<List<Thing>>> TickBuckets = AccessTools.FieldRefAccess<TickList, List<List<Thing>>>("thingLists");
    private static readonly AccessTools.FieldRef<TickList, List<Thing>> Registering = AccessTools.FieldRefAccess<TickList, List<Thing>>("thingsToRegister");
    private static readonly AccessTools.FieldRef<TickList, List<Thing>> Deregistering = AccessTools.FieldRefAccess<TickList, List<Thing>>("thingsToDeregister");
    private static Dictionary<Pawn, int> CallbackOrder()
    {
        var ticks = NormalTicks(Find.TickManager);
        var things = new List<Thing>(TickBuckets(ticks)[0]);
        things.AddRange(Registering(ticks));
        foreach (var removed in Deregistering(ticks)) things.Remove(removed);
        return things.Select((thing, index) => (thing, index)).Where(x => x.thing is Pawn).ToDictionary(x => (Pawn)x.thing, x => x.index);
    }
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
        var order = CallbackOrder();
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
                    giver.defName, JobDefOf.Research.defName, Fact.Known((double)perTick))
                { SkillWork = NativeProductiveSkill.ReadWork(pawn, SkillDefOf.Intellectual, StatDefOf.ResearchSpeed, project.Cost - project.ProgressReal,
                    bench.GetStatValue(StatDefOf.ResearchSpeedFactor) * PointsPerSpeedTick * difficulty / project.CostFactor(pawn.Faction.def.techLevel),
                    0.1, false, true, "JobDriver_Research: points then Intellectual 0.1 XP per callback tick") is { } learning
                    ? learning with { CallbackOrder = order.TryGetValue(pawn, out var at) ? Fact.Known(at) : default } : null });
            }
        }
        return new() { Known = true, Workers = EquatableArray.From(workers) };
    }
}
