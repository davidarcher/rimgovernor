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
        Console.WriteLine("native-home-coverage: connectivity and outside bounds passed");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
