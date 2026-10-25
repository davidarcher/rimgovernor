using System;
using System.Collections.Generic;
using System.Linq;

namespace RimGovernor.Native
{
    // Geometry is independent of Home mutation. Traversable cells come only
    // from visible enclosed roofed rooms and internal doors.
    internal static class HomeCoverageGeometry
    {
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

    }
}
