using System;
using System.Collections.Generic;
using System.Linq;
using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using Verse.AI;
namespace RimGovernor.Native;

/// <summary>
/// The sowing observation (#3528): the cells native sowing accepts now, per free colonist and grower. Legality is the game's own
/// <see cref="WorkGiver_GrowerSow"/> (so <see cref="TreeLatticeSowing"/>'s patch applies), read through a private subclass that
/// sets the giver's shared wanted-plant cache around each call and accepts only a Sow job: a cut-plant or haul-aside job the giver
/// would return for a blocked cell is not sowing work. Sowing itself is native work (task 5.6): no exact order sows.
/// </summary>
internal static class NativeSowingActivities
{
    /// <summary>The grower sow giver def, reached through its giver class (the game's DefOf lists no sow giver).</summary>
    internal static WorkGiverDef? Giver => DefDatabase<WorkGiverDef>.AllDefsListForReading.FirstOrDefault(d => d.giverClass == typeof(WorkGiver_GrowerSow));

    private sealed class SowGiver : WorkGiver_GrowerSow
    {
        /// <summary>The Sow job at <paramref name="cell"/> for <paramref name="crop"/>, else null.</summary>
        internal Job? Sow(Pawn pawn, IntVec3 cell, ThingDef crop)
        {
            wantedPlantDef = crop;
            try
            {
                var job = JobOnCell(pawn, cell, false);
                if (job == null || job.def == JobDefOf.Sow) return job;
                JobMaker.ReturnToPool(job);
                return null;
            }
            finally { wantedPlantDef = null; }
        }

        /// <summary>The giver's own per-grower requirement (sowing allowed, a crop set, a settable that can take seed now).</summary>
        internal bool Accepts(IPlantToGrowSettable grower, Pawn pawn)
        {
            try { return ExtraRequirements(grower, pawn); }
            finally { wantedPlantDef = null; }
        }
    }

    private static SowGiver? giver;

    private static SowGiver Probe()
    {
        if (giver != null) return giver;
        return giver = new SowGiver { def = Giver ?? throw new InvalidOperationException("sow_giver_def_missing") };
    }

    private static IEnumerable<IntVec3> CellsOf(IPlantToGrowSettable grower)
        => grower is Zone_Growing zone ? zone.Cells : ((Thing)grower).OccupiedRect();

    private static string IdOf(IPlantToGrowSettable grower) => grower is Zone_Growing zone ? zone.GetUniqueLoadID() : ((Thing)grower).GetUniqueLoadID();

    /// <summary>True when the pawn may do sowing work at all: the giver's work type, tags and capacities.</summary>
    private static bool CanWork(Pawn pawn)
    {
        var def = Probe().def;
        return !pawn.WorkTagIsDisabled(def.workTags) && (def.workType == null || !pawn.WorkTypeIsDisabled(def.workType))
            && Probe().MissingRequiredCapacity(pawn) == null;
    }

    /// <summary>
    /// The cells the pawn could sow now in this grower, nearest the pawn first: no plant or blueprint in the way, fertility and season
    /// right, the pawn skilled enough, the cell reservable and reachable. Empty when the grower takes no seed now or the pawn cannot work.
    /// </summary>
    internal static List<IntVec3> LegalCells(Pawn pawn, IPlantToGrowSettable grower)
    {
        var cells = new List<IntVec3>();
        var crop = grower.GetPlantDefToGrow();
        if (crop == null || !CanWork(pawn) || !Probe().Accepts(grower, pawn)) return cells;
        foreach (var cell in CellsOf(grower))
            if (cell.InBounds(pawn.Map) && Probe().Sow(pawn, cell, crop) is { } job)
            {
                JobMaker.ReturnToPool(job);
                if (pawn.CanReach(cell, PathEndMode.Touch, Danger.None)) cells.Add(cell);
            }
        return cells.OrderBy(c => c.DistanceToSquared(pawn.Position)).ThenBy(c => c.x).ThenBy(c => c.z).ToList();
    }

    /// <summary>Every free colonist's sowing of every growing zone or planter that has a crop set (#3528).</summary>
    internal static RimGovernor.Brain.GameFacts.Observations.SowingActivityRead Read(Map map, NativePathLegs? legs = null)
    {
        legs ??= new(map);
        var workers = new List<RimGovernor.Brain.GameFacts.Observations.SowingWorker>();
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var growers = map.zoneManager.AllZones.OfType<Zone_Growing>().Cast<IPlantToGrowSettable>()
            .Concat(map.listerBuildings.allBuildingsColonist.OfType<Building_PlantGrower>())
            .OrderBy(IdOf, StringComparer.Ordinal).ToArray();
        var giverName = Probe().def.defName;
        foreach (var grower in growers)
            if (grower.GetPlantDefToGrow() == null) findings.Add("sowing_zone_without_crop:" + IdOf(grower));
        foreach (var pawn in map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal))
        {
            if (!NativeFoodActivity.Available(pawn)) continue;
            // A pawn with no plant work speed, or no Plants skill to learn, cannot sow: it has no row.
            var speed = pawn.GetStatValue(StatDefOf.PlantWorkSpeed);
            if (speed <= 0) continue;
            foreach (var grower in growers)
            {
                var crop = grower.GetPlantDefToGrow();
                if (crop?.plant == null) continue;
                var cells = LegalCells(pawn, grower);
                if (cells.Count == 0 || legs.Cost(pawn, pawn.Position, cells[0], PathEndMode.OnCell) is not { } path) continue;
                if (NativeProductiveSkill.ReadWork(pawn, SkillDefOf.Plants, StatDefOf.PlantWorkSpeed, cells.Count,
                    1d / crop.plant.sowWork, 0.085, true, false, "JobDriver_PlantSow: Plants 0.085 XP per callback tick before sow work") is not { } skillWork) continue;
                workers.Add(new(pawn.GetUniqueLoadID(), IdOf(grower), giverName, path, (double)crop.plant.sowWork / speed,
                    EquatableArray.From(cells.Select(c => new RimGovernor.Brain.Domain.Cell(c.x, c.z))))
                { SkillWork = skillWork });
            }
        }
        return new() { Workers = EquatableArray.From(workers), Findings = EquatableArray.From(findings) };
    }
}
