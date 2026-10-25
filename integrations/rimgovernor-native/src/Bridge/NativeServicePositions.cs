using System;
using System.Collections.Generic;
using System.Linq;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Observations;
using RimWorld;
using Verse;
using Verse.AI;
using Thing = Verse.Thing;

namespace RimGovernor.Native;

/// <summary>Native service geometry, without running a job or advancing the game's random state.</summary>
internal static class NativeServicePositions
{
    internal static ServicePositionRead Meal(Pawn pawn, Thing food, NativePathLegs legs)
    {
        if (legs.ServicePosition(pawn, pawn.Position, food, PathEndMode.Touch) is not ServicePositionRead.Located pickup)
            return new ServicePositionRead.Unprojected("ingest_pickup_path_not_read");
        var root = new IntVec3(pickup.Cell.X, 0, pickup.Cell.Z);
        return MealAt(pawn, food.def, [root]);
    }

    internal static ServicePositionRead Meal(Pawn pawn, ThingDef food, Cell destination) =>
        MealAt(pawn, food, GenAdj.CellsAdjacent8Way(new IntVec3(destination.X, 0, destination.Z), Rot4.North, IntVec2.One)
            .Append(new(destination.X, 0, destination.Z)).Where(c => c.InBounds(pawn.Map) && c.Standable(pawn.Map)));

    private static ServicePositionRead MealAt(Pawn pawn, ThingDef food, IEnumerable<IntVec3> pickups)
    {
        var map = pawn.Map;
        var roots = pickups.Where(c => c.InBounds(map)).Distinct().ToArray();
        // Toils_Ingest searches from the post-pickup position. RCellFinder's random standing search
        // expands to distance 15, with a final origin-region RandomCell and pickup-cell fallback.
        // Bound every random outcome without running the selector or assigning a distribution.
        var cells = roots.SelectMany(root => (root.GetRegion(map)?.Cells ?? Enumerable.Empty<IntVec3>())
            .Concat(GenRadial.RadialCellsAround(root, 15f, true).Where(c => c.InBounds(map) && c.GetRegion(map) != null))
            .Concat(map.listerBuildings.allBuildingsColonist.Where(b => b.def.building?.isSittable == true)
                .Where(b => b.Position.DistanceToSquared(root) <= food.ingestible.chairSearchRadius * food.ingestible.chairSearchRadius)
                .SelectMany(b => b.OccupiedRect())).Append(root));
        return Possible(cells);
    }
    internal static ServicePositionRead Recreation(Pawn pawn, Thing target, JobDef job, NativePathLegs legs)
    {
        var driver = job.driverClass;
        if (typeof(JobDriver_VisitJoyThing).IsAssignableFrom(driver))
            return legs.ServicePosition(pawn, pawn.Position, target, PathEndMode.Touch);
        if (typeof(JobDriver_WatchBuilding).IsAssignableFrom(driver))
            return Possible(WatchBuildingUtility.CalculateWatchCells(target.def, target.Position, target.Rotation, pawn.Map));
        if (typeof(JobDriver_SitFacingBuilding).IsAssignableFrom(driver) || driver == typeof(JobDriver_PlayBilliards))
            return Possible(GenAdj.CellsAdjacent8Way(target).Where(c => c.InBounds(pawn.Map) && c.Standable(pawn.Map)));
        return new ServicePositionRead.Unprojected("unsupported_recreation_driver:" + driver.FullName);
    }

    private static ServicePositionRead Possible(IEnumerable<IntVec3> cells)
    {
        var positions = cells.Select(c => new Cell(c.x, c.z)).Distinct().OrderBy(c => c.X).ThenBy(c => c.Z).ToArray();
        return positions.Length == 0 ? new ServicePositionRead.Unprojected("native_service_cells_empty")
            : positions.Length == 1 ? new ServicePositionRead.Located(positions[0])
            : new ServicePositionRead.Possible(EquatableArray.From(positions));
    }
}
