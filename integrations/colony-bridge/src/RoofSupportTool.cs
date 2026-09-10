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
        // Counterfactual version of the installed RoofCollapseUtility's connected
        // roof/radius rule. The map is never edited to preview removal.
        internal static string Blocker(Building wall, out int checkedRoofs)
        {
            checkedRoofs = 0;
            if (wall?.Map == null || wall.def != ThingDefOf.Wall || wall.OccupiedRect().Area != 1)
                return "Only one native wall can be checked";
            var map = wall.Map;
            var radius = RoofCollapseUtility.RoofMaxSupportDistance;
            var affected = GenRadial.RadialCellsAround(wall.Position, radius, true).ToList();
            if (affected.Any(c => !c.InBounds(map) || c.Fogged(map))) return "Unknown wall support geometry";
            foreach (var root in affected.Where(c => c.Roofed(map))) {
                checkedRoofs++;
                if (map.roofCollapseBuffer.IsMarkedToCollapse(root)) return "Roof collapse is already pending";
                var queue = new Queue<IntVec3>();
                var seen = new HashSet<IntVec3>();
                queue.Enqueue(root); seen.Add(root);
                bool supported = false, unknown = false;
                while (queue.Count > 0 && !supported) {
                    var c = queue.Dequeue();
                    foreach (var offset in GenAdj.CardinalDirectionsAndInside) {
                        var near = c + offset;
                        if (!near.InBounds(map) || !near.InHorDistOf(root, radius)) continue;
                        if (near.Fogged(map)) { unknown = true; continue; }
                        var holder = near.GetEdifice(map);
                        if (holder != null && holder != wall && holder.def.holdsRoof) { supported = true; break; }
                    }
                    foreach (var offset in GenAdj.CardinalDirections) {
                        var next = c + offset;
                        if (!next.InBounds(map) || !next.InHorDistOf(root, radius) || !next.Roofed(map)) continue;
                        if (next.Fogged(map)) { unknown = true; continue; }
                        if (seen.Add(next)) queue.Enqueue(next);
                    }
                }
                if (!supported) return unknown ? "Alternate support geometry is unknown" : "Removing this wall would leave unsupported roof";
            }
            return null;
        }
    }

    public sealed class RoofSupportTools
    {
        [Tool("home/roof_support", Description = "Read-only counterfactual support check for one exact native wall. Follows installed roof connectivity and radius while excluding that holder. Does not prove enclosure, escape access, ownership or authorize deconstruction.")]
        public async Task<object> Read(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Exact observed wall Thing ID.")] string target)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var wall = map?.listerThings.AllThings.OfType<Building>().SingleOrDefault(b => b.GetUniqueLoadID() == target);
                if (wall == null) return new { success = false, error = "Observed wall is unavailable" };
                var blocker = RoofSupportSafety.Blocker(wall, out var roofs);
                return new { success = true, target, tick = Find.TickManager.TicksGame,
                    supportWithoutTarget = blocker == null, checkedRoofs = roofs, blocker,
                    scope = "Roof support only; enclosure, escape routes and construction ownership require independent checks" };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
