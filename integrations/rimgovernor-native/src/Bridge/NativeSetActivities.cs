#nullable enable
using System.Collections.Generic;
using System.Linq;
using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools;

/// <summary>
/// A set step (#3613): a Work step that repeats over its <see cref="PawnActivityStep.Targets"/>, the Sow step's pattern
/// (<see cref="NativeSowingActivities"/>) over things. One job per member, nearest first; a member that is gone, already
/// done or no longer legal is skipped; the step completes when its Count is done or none is left legal (a cleared set is
/// done, not a fault). Validate refuses only when no member is legal now. Cut (Work/CutPlant through
/// <see cref="WorkGiver_PlantsCut"/>) is the first family: each member's job is the giver's own for a designated plant
/// (CutPlantDesignated, or HarvestDesignated where a HarvestPlant stands), and the order stands the CutPlant designation.
/// A step whose set is cells only has no family yet.
/// </summary>
internal static class NativeSetActivities
{
    internal static bool Supported(WorkGiverDef giver, string job) => job == "CutPlant" && giver.Worker is WorkGiver_PlantsCut;

    /// <summary>The job defs the family's giver gives a member: the cut giver's two designated-plant jobs.</summary>
    private static bool Accepts(PawnActivityStep step, Job job) => step.Job == "CutPlant"
        ? job.def == JobDefOf.CutPlantDesignated || job.def == JobDefOf.HarvestDesignated : job.def.defName == step.Job;

    /// <summary>The members standing on the pawn's map, nearest the pawn first (then by cell, as the sow cells order).</summary>
    internal static List<Thing> Members(Pawn pawn, PawnActivityStep step) => step.Targets.Select(id => RefIndex.Thing(pawn.Map, id))
        .Where(t => t != null && t.Spawned && !t.Destroyed && t.Map == pawn.Map).Select(t => t!)
        .OrderBy(t => t.Position.DistanceToSquared(pawn.Position)).ThenBy(t => t.Position.x).ThenBy(t => t.Position.z).ToList();

    /// <summary>Why the pawn cannot work the member now, independent of the designation the order stands itself; null when it can.</summary>
    internal static string? Refusal(Pawn pawn, Thing member, PawnActivityStep step, WorkGiver_Scanner giver)
    {
        if (step.Job == "CutPlant") return CutRefusal(pawn, member);
        return giver.HasJobOnThing(pawn, member, false) ? null : "the work giver refuses it";
    }

    // WorkGiver_PlantsCut.JobOnThing's checks less the designation (and the reservation the job itself takes).
    private static string? CutRefusal(Pawn pawn, Thing plant)
    {
        if (plant is not Plant || plant.def.category != ThingCategory.Plant) return "not a plant";
        if (plant.IsBurning()) return "burning";
        if (plant.IsForbidden(pawn)) return "forbidden";
        if (!PlantUtility.PawnWillingToCutPlant_Job(plant, pawn)) return "the pawn is unwilling to cut it";
        if ((plant as ThingWithComps)?.GetComp<CompPlantPreventCutting>() is { PreventCutting: true }) return "cutting is prevented";
        if (!pawn.CanReserveAndReach(plant, PathEndMode.Touch, Danger.Some)) return "cannot be reserved or reached";
        return null;
    }

    /// <summary>Validate's refusal: null while any member is legal now; else every member's reason.</summary>
    internal static string? Refusal(Pawn pawn, PawnActivityStep step, WorkGiver_Scanner giver)
    {
        if (step.Targets.Count == 0) return "a cell set has no step family yet";
        var members = Members(pawn, step);
        if (members.Count == 0) return "no member stands on the map";
        var reasons = new List<string>();
        foreach (var member in members)
        {
            var why = Refusal(pawn, member, step, giver);
            if (why == null) return null;
            reasons.Add(member.GetUniqueLoadID() + " " + why);
        }
        return "no member is legal (" + string.Join("; ", reasons) + ")";
    }

    /// <summary>The members the pawn can still work, nearest first (the continuation's rebinding).</summary>
    internal static List<Thing> Legal(Pawn pawn, PawnActivityStep step, WorkGiver_Scanner giver) =>
        Members(pawn, step).Where(m => Refusal(pawn, m, step, giver) == null).ToList();

    /// <summary>The next job: the nearest member the giver gives an accepted job for, else null when none is left legal.</summary>
    internal static Job? NextJob(Pawn pawn, PawnActivityStep step, WorkGiver_Scanner giver)
    {
        foreach (var member in Members(pawn, step))
        {
            if (member.IsForbidden(pawn) || !pawn.CanReserveAndReach(member, PathEndMode.Touch, Danger.Some)) continue;
            var job = giver.JobOnThing(pawn, member, false);
            if (job == null) continue;
            if (!Accepts(step, job)) { JobMaker.ReturnToPool(job); continue; }
            job.workGiverDef = giver.def;
            return job;
        }
        return null;
    }

    /// <summary>The designation a Cut member needs: none where a CutPlant or HarvestPlant (chop wood) already stands.</summary>
    internal static DesignationDef? Needed(PawnActivityStep step, Thing member) =>
        step.Job != "CutPlant" || member.Map.designationManager.DesignationOn(member, DesignationDefOf.CutPlant) != null
            || member.Map.designationManager.DesignationOn(member, DesignationDefOf.HarvestPlant) != null ? null : DesignationDefOf.CutPlant;
}
