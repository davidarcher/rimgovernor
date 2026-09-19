#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace HomeBridge.BridgeTools
{
    // Geometry and batching are independent of Home mutation. Traversable
    // cells come only from visible enclosed roofed rooms and internal doors.
    internal static class HomeCoverageGeometry
    {
        internal const int BatchSize = 256;

        internal static HashSet<T> Connected<T>(IEnumerable<T> footprint,
            Func<T, IEnumerable<T>> neighbors, Func<T, bool> interior) where T : notnull
        {
            var result = new HashSet<T>(footprint);
            var visited = new HashSet<T>();
            var pending = new Queue<T>(result.SelectMany(neighbors).Concat(result));
            while (pending.Count > 0)
            {
                var cell = pending.Dequeue();
                if (!visited.Add(cell) || !interior(cell)) continue;
                result.Add(cell);
                foreach (var next in neighbors(cell)) pending.Enqueue(next);
            }
            return result;
        }

        // Prefer outstanding work so covered cells cannot starve the rest of
        // the base. Stable geometry ordering breaks ties; the chosen batch is
        // the shape-bound target.
        internal static List<T> Batch<T>(IEnumerable<T> ordered,
            Func<T, bool> home)
        {
            return ordered.OrderBy(c => home(c) ? 1 : 0)
                .Take(BatchSize).ToList();
        }
    }
}
