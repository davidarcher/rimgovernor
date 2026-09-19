using System;
using System.Collections.Generic;
using System.Linq;
using HomeBridge.BridgeTools;

internal static class NativeHomeCoverageProbe
{
    internal static void Invoke()
    {
        // Two rooms and a corridor, connected only through two internal doors.
        var interior = new HashSet<int> { 1, 2, 3, 4, 5, 6, 7 };
        Func<int, IEnumerable<int>> neighbors = c => new[] { c - 1, c + 1 };
        var cells = HomeCoverageGeometry.Connected(new[] { 1 }, neighbors, interior.Contains);
        Require(cells.SetEquals(interior), "connected rooms and corridor included");
        Require(!cells.Contains(0) && !cells.Contains(8), "outside never included");
        interior.Remove(4);
        cells = HomeCoverageGeometry.Connected(new[] { 1 }, neighbors, interior.Contains);
        Require(cells.SetEquals(new[] { 1, 2, 3 }), "blocked door or broken enclosure disconnects remote room");
        // A facility larger than the former limit must progress across batches.
        var large = Enumerable.Range(0, 700).ToList();
        var covered = new HashSet<int>();
        int batches = 0;
        while (covered.Count != large.Count)
        {
            var batch = HomeCoverageGeometry.Batch(large, covered.Contains);
            Require(batch.Count <= 256 && batch.Distinct().Count() == batch.Count, "bounded distinct batch");
            int before = covered.Count;
            covered.UnionWith(batch);
            Require(covered.Count > before, "covered prefix cannot starve missing suffix");
            Require(++batches <= 3, "all large geometry covered in three batches");
        }
        covered.Remove(1);
        Require(HomeCoverageGeometry.Batch(large, covered.Contains)[0] == 1, "later Home removal becomes work again");
        Console.WriteLine("native-home-coverage: connectivity, outside bounds, batches and restoration passed");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
