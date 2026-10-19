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
/// Mine (Work/Mine through <see cref="WorkGiver_Miner"/>, #3616) is the second: each member's job is the miner's own for
/// a rock under a Mine designation (MineAIUtility.JobOnThing refuses an undesignated cell), the order stands the Mine
/// designation under the mine_safety guard, and a member whose removal the excavation safety rule refuses is skipped.
/// A step whose set is cells only has no family yet.
/// </summary>
internal static class NativeSetActivities
{
    internal static bool Supported(WorkGiverDef giver, string job) =>
        job == "CutPlant" && giver.Worker is WorkGiver_PlantsCut || job == "Mine" && giver.Worker is WorkGiver_Miner;

    /// <summary>The job defs the family's giver gives a member: the cut giver's two designated-plant jobs, else the step's own job
    /// (the miner's haul-aside of a blocking item is not the member's work).</summary>
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
        if (step.Job == "Mine") return MineRefusal(pawn, member);
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

    // MineAIUtility.JobOnThing's checks less the designation (and the reservation the job itself takes), plus the mine_safety
    // guard's permanent blockers (the cell rule and the excavation safety rule): a rock whose removal would leave a roof
    // unsupported is skipped here, since the guard would only cancel the job (#3616).
    private static string? MineRefusal(Pawn pawn, Thing rock)
    {
        if (rock is not Mineable || !rock.def.mineable) return "not mineable rock";
        if (ExcavationTools.CellBlocker(rock.Position, rock.Map) is { } cell) return cell;
        if (ExcavationSafety.Check(rock.Map, new[] { rock.Position }, out _, out var support, throughFog: true) != ExcavationSafety.Support.Supported)
            return support ?? "roof support is unproven";
        if (rock.IsForbidden(pawn)) return "forbidden";
        if (!pawn.CanReserveAndReach(rock, PathEndMode.Touch, Danger.Some)) return "cannot be reserved or reached";
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

    /// <summary>The next job: the nearest legal member the giver gives an accepted job for, else null when none is left legal.</summary>
    internal static Job? NextJob(Pawn pawn, PawnActivityStep step, WorkGiver_Scanner giver)
    {
        foreach (var member in Legal(pawn, step, giver))
        {
            var job = giver.JobOnThing(pawn, member, false);
            if (job == null) continue;
            if (!Accepts(step, job)) { JobMaker.ReturnToPool(job); continue; }
            job.workGiverDef = giver.def;
            return job;
        }
        return null;
    }

    /// <summary>The designation the order stands for a member (StandDesignation places it where missing and marks its guard
    /// record Ordered): a Cut member's CutPlant unless a CutPlant or HarvestPlant (chop wood) already stands on it; a Mine
    /// member's Mine, standing or not (a Designate-stood mine_safety record waits for supervised play until it is Ordered),
    /// unless a MineVein stands at its cell (#3616).</summary>
    internal static DesignationDef? Needed(PawnActivityStep step, Thing member)
    {
        var manager = member.Map.designationManager;
        if (step.Job == "CutPlant")
            return manager.DesignationOn(member, DesignationDefOf.CutPlant) != null || manager.DesignationOn(member, DesignationDefOf.HarvestPlant) != null ? null : DesignationDefOf.CutPlant;
        if (step.Job == "Mine") return manager.DesignationAt(member.Position, DesignationDefOf.MineVein) != null ? null : DesignationDefOf.Mine;
        return null;
    }
}
