#nullable enable
using System;
using System.Collections.Generic;

namespace HomeBridge.BridgeTools
{
    // The connected-roof search is independent of Verse so rectangular
    // counterfactuals can be proved on a hand-built grid. The adapter supplies
    // the installed game's radial and adjacency rules, without editing the map.
    internal static class RoofSupportGeometry
    {
        internal static string? Blocker<T>(ICollection<T> removed,
            Func<T, IEnumerable<T>> radial, Func<T, IEnumerable<T>> cardinal,
            Func<T, IEnumerable<T>> adjacentAndInside, Func<T, T, bool> withinRadius,
            Func<T, bool> inBounds, Func<T, bool> fogged, Func<T, bool> roofed,
            Func<T, bool> holdsRoof, Func<T, bool> collapsePending, out int checkedRoofs,
            ICollection<T>? structuralCells = null) where T : struct
        {
            bool Unknown(T cell) => fogged(cell) && structuralCells?.Contains(cell) != true;
            checkedRoofs = 0;
            if (removed.Count == 0) return "No occupied support cells";
            var excluded = new HashSet<T>(removed);
            var roots = new HashSet<T>();
            foreach (var cell in removed)
                foreach (var near in radial(cell)) {
                    if (!inBounds(near) || Unknown(near)) return "Unknown building support geometry";
                    if (roofed(near)) roots.Add(near);
                }
            foreach (var root in roots) {
                checkedRoofs++;
                if (collapsePending(root)) return "Roof collapse is already pending";
                var queue = new Queue<T>();
                var seen = new HashSet<T>();
                queue.Enqueue(root); seen.Add(root);
                bool supported = false, unknown = false;
                while (queue.Count > 0 && !supported) {
                    var cell = queue.Dequeue();
                    foreach (var near in adjacentAndInside(cell)) {
                        if (!inBounds(near) || !withinRadius(near, root)) continue;
                        if (Unknown(near)) { unknown = true; continue; }
                        if (!excluded.Contains(near) && holdsRoof(near)) { supported = true; break; }
                    }
                    foreach (var next in cardinal(cell)) {
                        if (!inBounds(next) || !withinRadius(next, root) || !roofed(next)) continue;
                        if (Unknown(next)) { unknown = true; continue; }
                        if (seen.Add(next)) queue.Enqueue(next);
                    }
                }
                if (!supported) return unknown ? "Alternate support geometry is unknown" : "Removing this building would leave unsupported roof";
            }
            return null;
        }
    }
}
