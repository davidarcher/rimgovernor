#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    internal static class RoofSupportSafety
    {
        internal static bool GeometryKnown(Map map, IntVec3 cell) =>
            GenRadial.RadialCellsAround(cell, RoofCollapseUtility.RoofMaxSupportDistance, true)
                .All(c => c.InBounds(map) && !c.Fogged(map));
        // Counterfactual version of the installed RoofCollapseUtility's connected
        // roof/radius rule. The map is never edited to preview removal.
        internal static string? Blocker(Building building, out int checkedRoofs) => Blocker(building, null, out checkedRoofs);

        // assumedHolders are open cells counted as roof holders the removal
        // would find standing: the stone-shell census lists a fresh candidate
        // only when its backups, once built, keep every roof up (#293).
        internal static string? Blocker(Building building, ICollection<IntVec3>? assumedHolders, out int checkedRoofs,
            ICollection<IntVec3>? structuralCells = null)
        {
            checkedRoofs = 0;
            if (building?.Map == null || !building.Spawned) return "Observed building is unavailable";
            // Removing a non-holder cannot change support, even under a roof.
            if (!building.def.holdsRoof) return null;
            var map = building.Map;
            var radius = RoofCollapseUtility.RoofMaxSupportDistance;
            return RoofSupportGeometry.Blocker(building.OccupiedRect().ToList(),
                c => GenRadial.RadialCellsAround(c, radius, true),
                c => GenAdj.CardinalDirections.Select(offset => c + offset),
                c => GenAdj.CardinalDirectionsAndInside.Select(offset => c + offset),
                (near, root) => near.InHorDistOf(root, radius),
                c => c.InBounds(map), c => c.Fogged(map), c => c.Roofed(map),
                c => c.GetEdifice(map)?.def.holdsRoof == true || assumedHolders?.Contains(c) == true,
                c => map.roofCollapseBuffer.IsMarkedToCollapse(c), out checkedRoofs, structuralCells);
        }
    }

    public sealed class RoofSupportTools
    {
        [Tool("home/roof_support", Description = "Read-only counterfactual support check for one exact native building. Follows installed roof connectivity and radius while excluding its whole occupied rectangle. Does not prove enclosure, escape access, ownership or authorize deconstruction.")]
        public async Task<object> Read(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Exact observed building Thing ID.")] string target)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var wall = map?.listerThings.AllThings.OfType<Building>().SingleOrDefault(b => b.GetUniqueLoadID() == target);
                if (wall == null) return new { success = false, error = "Observed building is unavailable" };
                var blocker = RoofSupportSafety.Blocker(wall, out var roofs);
                return new { success = true, target, tick = Find.TickManager.TicksGame,
                    supportWithoutTarget = blocker == null, checkedRoofs = roofs, blocker,
                    scope = "Roof support only; enclosure, escape routes and construction ownership require independent checks" };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
