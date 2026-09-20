using System;
using System.Collections.Generic;
using System.Linq;
using HomeBridge.BridgeTools;

internal static class NativeRoofSupportProbe
{
    // Integer cells in a 100x100 grid, well away from the boundaries.
    private static int Cell(int x, int z) => z * 100 + x;
    private static IEnumerable<int> Cardinal(int c) => new[] { c - 1, c + 1, c - 100, c + 100 };
    private static bool Near(int a, int b) => Math.Pow(a % 100 - b % 100, 2) + Math.Pow(a / 100 - b / 100, 2) <= 4;
    private static IEnumerable<int> Radial(int c) => Enumerable.Range(-2, 5).SelectMany(z => Enumerable.Range(-2, 5).Select(x => c + z * 100 + x)).Where(n => Near(n, c));

    internal static void Invoke()
    {
        var removed = new HashSet<int> { Cell(20,20), Cell(21,20), Cell(22,20), Cell(20,21), Cell(21,21), Cell(22,21) };
        var roofs = new HashSet<int>();
        var holders = new HashSet<int>(removed);
        var fog = new HashSet<int>();
        var pending = new HashSet<int>();
        Func<int, bool> inside = c => c % 100 > 0 && c % 100 < 99 && c / 100 > 0 && c / 100 < 99;
        int checkedRoofs = 0;
        Func<string> check = () => RoofSupportGeometry.Blocker(removed, Radial, Cardinal, c => Cardinal(c).Concat(new[] {c}), Near, inside, fog.Contains, roofs.Contains, holders.Contains, pending.Contains, out checkedRoofs);
        Require(check() == null, "no roof is safe");
        roofs.Add(Cell(22,21));
        Require(check() != null, "every occupied cell must be excluded as a holder");
        holders.Add(Cell(23,21));
        Require(check() == null, "adjacent alternate holder supports the roof");
        Require(checkedRoofs == 1, "overlapping radial neighborhoods must count each roof once");
        // This roof is within radius of the rectangle's far corner, but outside
        // the origin's radius. An origin-only implementation misses it.
        roofs.Clear(); roofs.Add(Cell(24,21)); holders.Remove(Cell(23,21));
        Require(check() != null, "seed roofs from the whole rectangle");
        holders.Add(Cell(25,21));
        Require(check() == null, "far-corner roof can find external support");
        pending.Add(Cell(24,21));
        Require(check() != null, "pending collapse must block even with support");
        pending.Clear(); fog.Add(Cell(24,20));
        Require(check() != null, "fog anywhere in affected geometry blocks");
        fog.Clear();
        // Disconnected roof cannot borrow a holder through unroofed ground.
        holders.Clear(); holders.Add(Cell(22,21));
        Require(check() != null, "excluded holder cannot support a disconnected roof");
        removed.Clear(); removed.Add(Cell(20,20)); roofs.Clear(); roofs.Add(Cell(20,20)); holders.Clear(); holders.Add(Cell(21,20));
        Require(check() == null, "single-cell wall keeps its prior supported behavior");
        holders.Clear(); Require(check() != null, "single-cell wall removal remains blocked");
        // The sealed shrine's interior is fogged by definition. Its bounded
        // structural survey permits roof checks, not a blanket fog bypass.
        holders.Add(Cell(21,20)); fog.Add(Cell(20,20));
        var structure = new HashSet<int> { Cell(20,20) };
        Func<string> shrineCheck = () => RoofSupportGeometry.Blocker(removed, Radial, Cardinal,
            c => Cardinal(c).Concat(new[] { c }), Near, inside, fog.Contains, roofs.Contains,
            holders.Contains, pending.Contains, out checkedRoofs, structure);
        Require(check() != null, "ordinary removal still refuses the sealed interior");
        Require(shrineCheck() == null, "surveyed sealed roof retains alternate support");
        holders.Clear();
        Require(shrineCheck() != null, "surveyed fog does not excuse unsupported roof");
        holders.Add(Cell(21,20)); pending.Add(Cell(20,20));
        Require(shrineCheck() != null, "surveyed fog does not excuse pending collapse");
        pending.Clear(); fog.Add(Cell(20,22));
        Require(shrineCheck() != null, "fog outside the shrine structure still blocks");
        Console.WriteLine("native-roof-support: rectangular exclusion, perimeter roots, fog, collapse and single-cell checks passed");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
