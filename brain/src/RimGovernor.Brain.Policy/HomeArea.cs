using System.Security.Cryptography;
using System.Text;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// The diff from the current home to the target home: the base footprint (colony extent plus margin, largest
/// region and its near neighbours) with the game's auto-expand off. (Go <c>HomeAreaPlan</c>.)
/// </summary>
public sealed record HomeAreaPlan
{
    public bool AutoOff { get; init; }
    public EquatableArray<Cell> Set { get; init; }
    public EquatableArray<Cell> Clear { get; init; }

    public bool Empty() => !AutoOff && Set.Count == 0 && Clear.Count == 0;

    /// <summary>Names the diff, so an admitted edit is not proposed twice. The hash is over the plan's JSON form.</summary>
    public MethodId MethodId()
    {
        var sb = new StringBuilder();
        sb.Append("{\"AutoOff\":").Append(AutoOff ? "true" : "false").Append(",\"Set\":");
        AppendCells(sb, Set);
        sb.Append(",\"Clear\":");
        AppendCells(sb, Clear);
        sb.Append('}');
        var sum = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return new MethodId("home-" + Convert.ToHexStringLower(sum.AsSpan(0, 16)));
    }

    private static void AppendCells(StringBuilder sb, EquatableArray<Cell> cells)
    {
        sb.Append('[');
        for (var i = 0; i < cells.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{{\"X\":{cells[i].X},\"Z\":{cells[i].Z}}}");
        }
        sb.Append(']');
    }
}

public static partial class PolicyFunctions
{
    /// <summary>The Chebyshev margin the home area keeps around the colony extent.</summary>
    public const int HomeAreaMargin = 4;

    /// <summary>How far (Chebyshev) an extent region may lie from the largest one and still be home; farther regions are outposts, not base.</summary>
    public const int HomeAreaOutlier = 16;

    /// <summary>Derives the home-area diff. Unknown when the extent, the current home cells or the auto-expand setting is unknown.</summary>
    public static Fact<HomeAreaPlan> PlanHomeArea(
        Fact<Bounds> bounds,
        Fact<CurrentConstruction> construction,
        Fact<EquatableArray<ConstructionClaim>> claims,
        Fact<HomeCoverageObservation> home
    )
    {
        var unknown = Unknown<HomeAreaPlan>();
        if (!home.TryGetValue(out var census))
        {
            return unknown;
        }
        var ck = census.Home.TryGetValue(out var current);
        var ak = census.AutoHome.TryGetValue(out var auto);
        if (!ck || !ak)
        {
            return unknown;
        }
        var extent = DeriveColonyExtent(
            new ColonyExtentRequest
            {
                Bounds = bounds,
                Construction = construction,
                Claims = claims,
                Home = home,
                Margin = HomeAreaMargin,
            }
        );
        if (!extent.TryGetValue(out var value))
        {
            return unknown;
        }
        var target = HomeBase(value);
        var set = new List<Cell>();
        var clear = new List<Cell>();
        var have = new HashSet<Cell>();
        foreach (var c in current!)
        {
            if (!have.Add(c))
            {
                continue;
            }
            if (!target.Contains(c))
            {
                clear.Add(c);
            }
        }
        foreach (var c in target)
        {
            if (!have.Contains(c))
            {
                set.Add(c);
            }
        }
        var order = Comparer<Cell>.Create((a, b) => ExtentCellLess(a, b) ? -1 : ExtentCellLess(b, a) ? 1 : 0);
        set.Sort(order);
        clear.Sort(order);
        return Known(
            new HomeAreaPlan
            {
                AutoOff = auto,
                Set = EquatableArray.From(set),
                Clear = EquatableArray.From(clear),
            }
        );
    }

    /// <summary>The base footprint of an extent: its largest region and every region within HomeAreaOutlier of it.</summary>
    private static HashSet<Cell> HomeBase(ColonyExtent value)
    {
        var target = new HashSet<Cell>();
        if (value.Regions.Count == 0)
        {
            return target;
        }
        var baseIndex = 0;
        for (var i = 0; i < value.Regions.Count; i++)
        {
            if (value.Regions[i].Cells.Count > value.Regions[baseIndex].Cells.Count)
            {
                baseIndex = i;
            }
        }
        var b = value.Regions[baseIndex];
        var minX = b.Cells[0].Cell.X;
        var minZ = b.Cells[0].Cell.Z;
        var maxX = minX;
        var maxZ = minZ;
        var baseCells = new HashSet<Cell>();
        foreach (var c in b.Cells)
        {
            baseCells.Add(c.Cell);
            minX = Math.Min(minX, c.Cell.X);
            maxX = Math.Max(maxX, c.Cell.X);
            minZ = Math.Min(minZ, c.Cell.Z);
            maxZ = Math.Max(maxZ, c.Cell.Z);
        }
        // Whether any base cell lies within the cut-off of c; the bounding box rejects far cells without a scan.
        bool Near(Cell c)
        {
            const int d = HomeAreaOutlier;
            if (c.X < minX - d || c.X > maxX + d || c.Z < minZ - d || c.Z > maxZ + d)
            {
                return false;
            }
            for (var dx = -d; dx <= d; dx++)
            {
                for (var dz = -d; dz <= d; dz++)
                {
                    if (baseCells.Contains(new Cell(c.X + dx, c.Z + dz)))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        for (var i = 0; i < value.Regions.Count; i++)
        {
            var r = value.Regions[i];
            var keep = i == baseIndex;
            for (var j = 0; !keep && j < r.Cells.Count; j++)
            {
                keep = Near(r.Cells[j].Cell);
            }
            if (keep)
            {
                foreach (var c in r.Cells)
                {
                    target.Add(c.Cell);
                }
            }
        }
        return target;
    }
}
