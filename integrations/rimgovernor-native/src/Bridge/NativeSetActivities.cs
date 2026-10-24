#nullable enable
using System.Collections.Generic;
using System.Linq;
using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools;

/// <summary>
/// A set step (#3613): a Work step that repeats over its <see cref="PawnActivityStep.Targets"/> (things) or its
/// <see cref="PawnActivityStep.Cells"/> (cells, #3615), the Sow step's pattern (<see cref="NativeSowingActivities"/>).
/// One job per member, nearest first; a member that is gone, already done or no longer legal is skipped; the step
/// completes when its Count is done or none is left legal (a cleared set is done, not a fault). Validate refuses only
/// when no member is legal now. Cut (Work/CutPlant through <see cref="WorkGiver_PlantsCut"/>) is the first thing family:
/// each member's job is the giver's own for a designated plant (CutPlantDesignated, or HarvestDesignated where a
/// HarvestPlant stands), and the order stands the CutPlant designation. Mine (Work/Mine through
/// <see cref="WorkGiver_Miner"/>, #3616) is the second: each member's job is the miner's own for a rock under a Mine
/// designation (MineAIUtility.JobOnThing refuses an undesignated cell), the order stands the Mine designation under the
/// mine_safety guard, and a member whose removal the excavation safety rule refuses is skipped. RemoveFloor
/// (Work/RemoveFloor through <see cref="WorkGiver_ConstructRemoveFloor"/>) is the cell family: each member's job is the
/// giver's JobOnCell, a cell whose top layer is gone is done, and the order stands the RemoveFloor designation. RemoveRoof
/// (Work/RemoveRoof through <see cref="WorkGiver_RemoveRoof"/>, #3617) is the second cell family: each member's job is the
/// giver's JobOnCell, an unroofed cell is done, and the order puts the members in the NoRoof area the giver requires.
/// </summary>
internal static class NativeSetActivities
{
    internal static bool Supported(WorkGiverDef giver, string job) =>
        job == "CutPlant" && giver.Worker is WorkGiver_PlantsCut || job == "Mine" && giver.Worker is WorkGiver_Miner
        || job == "RemoveFloor" && giver.Worker is WorkGiver_ConstructRemoveFloor
        || job == "RemoveRoof" && giver.Worker is WorkGiver_RemoveRoof;

    /// <summary>The job defs the family's giver gives a member: the cut giver's two designated-plant jobs, else the step's own job
    /// (the miner's haul-aside of a blocking item is not the member's work).</summary>
    private static bool Accepts(PawnActivityStep step, Job job) => step.Job == "CutPlant"
        ? job.def == JobDefOf.CutPlantDesignated || job.def == JobDefOf.HarvestDesignated : job.def.defName == step.Job;

    /// <summary>The members standing on the pawn's map, nearest the pawn first (then by cell, as the sow cells order).</summary>
    internal static List<Thing> Members(Pawn pawn, PawnActivityStep step) => step.Targets.Select(id => RefIndex.Thing(pawn.Map, id))
        .Where(t => t != null && t.Spawned && !t.Destroyed && t.Map == pawn.Map).Select(t => t!)
        .OrderBy(t => t.Position.DistanceToSquared(pawn.Position)).ThenBy(t => t.Position.x).ThenBy(t => t.Position.z).ToList();

    /// <summary>The member cells still to work (in bounds, a RemoveFloor cell with a removable top layer, a RemoveRoof cell still
    /// roofed), nearest the pawn first.</summary>
    internal static List<IntVec3> MemberCells(Pawn pawn, PawnActivityStep step) => step.Cells.Select(c => new IntVec3(c.X, 0, c.Z))
        .Where(c => c.InBounds(pawn.Map) && (step.Job != "RemoveFloor" || pawn.Map.terrainGrid.CanRemoveTopLayerAt(c))
            && (step.Job != "RemoveRoof" || c.Roofed(pawn.Map)))
        .OrderBy(c => c.DistanceToSquared(pawn.Position)).ThenBy(c => c.x).ThenBy(c => c.z).ToList();

    /// <summary>Whether any member still stands (a thing on the map, a cell still to work): a set with none is done.</summary>
    internal static bool Standing(Pawn pawn, PawnActivityStep step) => Members(pawn, step).Count > 0 || MemberCells(pawn, step).Count > 0;

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
        if (ExcavationSafety.Check(rock.Map, new[] { rock.Position }, out var support, throughFog: true) != ExcavationSafety.Support.Supported)
            return support ?? "roof support is unproven";
        if (rock.IsForbidden(pawn)) return "forbidden";
        if (!pawn.CanReserveAndReach(rock, PathEndMode.Touch, Danger.Some)) return "cannot be reserved or reached";
        return null;
    }

    /// <summary>Why the pawn cannot work the member cell now, independent of the designation the order stands itself; null when it can.</summary>
    internal static string? Refusal(Pawn pawn, IntVec3 cell, PawnActivityStep step, WorkGiver_Scanner giver)
    {
        if (step.Job == "RemoveFloor") return RemoveFloorRefusal(pawn, cell);
        if (step.Job == "RemoveRoof") return RemoveRoofRefusal(pawn, cell);
        return giver.HasJobOnCell(pawn, cell, false) ? null : "the work giver refuses it";
    }

    // WorkGiver_RemoveRoof.HasJobOnCell's checks less the NoRoof area (which Apply sets), plus Designator_AreaNoRoof's thick-roof
    // refusal, on the ceiling reservation layer the job takes (#3617).
    private static string? RemoveRoofRefusal(Pawn pawn, IntVec3 cell)
    {
        if (cell.Fogged(pawn.Map)) return "fogged";
        if (pawn.Map.roofGrid.RoofAt(cell) is { isThickRoof: true }) return "thick roof";
        if (!pawn.CanReserve(cell, 1, -1, ReservationLayerDefOf.Ceiling)) return "cannot be reserved";
        if (!pawn.CanReach(cell, PathEndMode.ClosestTouch, Danger.Some)) return "cannot be reached";
        return null;
    }

    // WorkGiver_ConstructRemoveFloor.HasJobOnCell's checks less the designation, on the floor reservation layer the job takes.
    private static string? RemoveFloorRefusal(Pawn pawn, IntVec3 cell)
    {
        if (cell.Fogged(pawn.Map)) return "fogged";
        if (cell.IsForbidden(pawn)) return "forbidden";
        if (WorkGiver_ConstructRemoveFloor.AnyBuildingBlockingFloorRemoval(cell, pawn.Map)) return "a building needs the floor";
        if (!pawn.CanReserveAndReach(cell, PathEndMode.Touch, Danger.Some, 1, -1, ReservationLayerDefOf.Floor)) return "cannot be reserved or reached";
        return null;
    }

    /// <summary>Validate's refusal: null while any member is legal now; else every member's reason.</summary>
    internal static string? Refusal(Pawn pawn, PawnActivityStep step, WorkGiver_Scanner giver)
    {
        var reasons = new List<string>();
        foreach (var member in Members(pawn, step))
        {
            var why = Refusal(pawn, member, step, giver);
            if (why == null) return null;
            reasons.Add(member.GetUniqueLoadID() + " " + why);
        }
        foreach (var cell in MemberCells(pawn, step))
        {
            var why = Refusal(pawn, cell, step, giver);
            if (why == null) return null;
            reasons.Add("(" + cell.x + "," + cell.z + ") " + why);
        }
        // A RemoveRoof set whose cells are all unroofed is done, not refused: a continuation re-sends it as a prior step (#3617).
        if (reasons.Count == 0 && step.Job == "RemoveRoof" && step.Cells.Count > 0) return null;
        return reasons.Count == 0 ? "no member stands on the map" : "no member is legal (" + string.Join("; ", reasons) + ")";
    }

    /// <summary>Puts a RemoveRoof set's roofed members in the NoRoof area (the area the giver and job require, through
    /// <see cref="Designator_AreaNoRoof"/>); no designation and no guard record, so Apply's own step (#3617).</summary>
    internal static void Designate(Map map, PawnActivityStep step)
    {
        if (step.Job != "RemoveRoof") return;
        var noRoof = map.areaManager.NoRoof;
        foreach (var cell in step.Cells.Select(c => new IntVec3(c.X, 0, c.Z)))
            if (cell.InBounds(map) && cell.Roofed(map) && !noRoof[cell]) NativeRemoveRoof.Designate(map, cell);
    }

    /// <summary>The members the pawn can still work, nearest first (the continuation's rebinding).</summary>
    internal static List<Thing> Legal(Pawn pawn, PawnActivityStep step, WorkGiver_Scanner giver) =>
        Members(pawn, step).Where(m => Refusal(pawn, m, step, giver) == null).ToList();

    /// <summary>The member cells the pawn can still work, nearest first (the continuation's rebinding).</summary>
    internal static List<IntVec3> LegalCells(Pawn pawn, PawnActivityStep step, WorkGiver_Scanner giver) =>
        MemberCells(pawn, step).Where(c => Refusal(pawn, c, step, giver) == null).ToList();

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
        foreach (var cell in MemberCells(pawn, step))
        {
            // The cell giver's JobOnCell makes the job without its own checks (the floor giver's); HasJobOnCell holds them.
            if (Refusal(pawn, cell, step, giver) != null || !giver.HasJobOnCell(pawn, cell, false)) continue;
            var job = giver.JobOnCell(pawn, cell, false);
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

    /// <summary>The designation a member cell needs: a RemoveFloor where none stands on a cell still to work (#3615).</summary>
    internal static DesignationDef? Needed(PawnActivityStep step, Map map, IntVec3 cell) =>
        step.Job != "RemoveFloor" || !map.terrainGrid.CanRemoveTopLayerAt(cell)
            || map.designationManager.DesignationAt(cell, DesignationDefOf.RemoveFloor) != null ? null : DesignationDefOf.RemoveFloor;
}
