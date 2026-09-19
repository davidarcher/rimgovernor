#nullable enable

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
        internal static string? Blocker(Building building, out int checkedRoofs)
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
                c => c.GetEdifice(map)?.def.holdsRoof == true,
                c => map.roofCollapseBuffer.IsMarkedToCollapse(c), out checkedRoofs);
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
