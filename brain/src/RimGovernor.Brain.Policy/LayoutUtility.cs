namespace RimGovernor.Brain.Policy;

/// <summary>
/// One planned generator placement: the native centre and rotation, and the footprint the preview must match.
/// </summary>
public sealed record PlannedPowerSite
{
    public Cell Cell { get; init; }
    public Rotation Rotation { get; init; }
    public Rectangle Area { get; init; }
}

/// <summary>Marks the cells a utility site may not take.</summary>
internal sealed partial class UtilityGrid
{
    private int w;
    private int h;
    private bool[] ok = [];
    private bool[] rock = [];
    private bool[] used = [];

    public UtilityGrid(LayoutPlan plan)
    {
        var u = this;
        foreach (var z in plan.Zones)
        {
            foreach (var r in z.Runs)
            {
                (u.w, u.h) = (Math.Max(u.w, r.X + r.Length), Math.Max(u.h, r.Z + 1));
            }
        }
        var n = u.w * u.h;
        (u.ok, u.rock, u.used) = (new bool[n], new bool[n], new bool[n]);
        foreach (var z in plan.Zones)
        {
            bool[] set;
            if (z.Kind == LayoutZoningRules.ZoneCore)
            {
                set = u.ok;
            }
            else if (z.Kind == LayoutPlanRules.ZoneMining)
            {
                set = u.rock;
            }
            else
            {
                continue;
            }
            foreach (var r in z.Runs)
            {
                for (var x = r.X; x < r.X + r.Length; x++)
                {
                    set[(r.Z * u.w) + x] = true;
                }
            }
        }
        foreach (var r in plan.Rooms)
        {
            u.Mark(PlannedRoom.RoomWalls(r));
            if (r.Attached())
            {
                u.Mark(r.AttachedGround());
            }
        }
        foreach (var r in plan.Reserved())
        {
            u.MarkRegion(r.Area);
        }
    }

    private bool In(int x, int z) => x >= 0 && z >= 0 && x < w && z < h;

    public void Mark(Rectangle r)
    {
        for (var z = r.Z; z < r.Z + r.Height; z++)
        {
            for (var x = r.X; x < r.X + r.Width; x++)
            {
                if (In(x, z))
                {
                    used[(z * w) + x] = true;
                }
            }
        }
    }

    public void MarkRegion(Region g)
    {
        foreach (var c in g.Cells())
        {
            if (In(c.X, c.Z))
            {
                used[(c.Z * w) + c.X] = true;
            }
        }
    }

    /// <summary>Every cell of r on unplanned core candidates; rockOK lets it cross natural rock.</summary>
    public bool Free(Rectangle r, bool rockOK)
    {
        for (var z = r.Z; z < r.Z + r.Height; z++)
        {
            for (var x = r.X; x < r.X + r.Width; x++)
            {
                if (!In(x, z))
                {
                    return false;
                }
                var i = (z * w) + x;
                if (!ok[i] || used[i] || (rock[i] && !rockOK))
                {
                    return false;
                }
            }
        }
        return true;
    }

}

/// <summary>
/// Utility reservations. The plan holds wind turbines and solar plots as stored site nodes and a stone enclosure
/// around each geyser's generator. The generator never builds over a reservation. Batteries have no planned site: the
/// store method places each on a roofed cell (power-contracts.md).
/// </summary>
public static partial class LayoutUtilityRules
{
    // A turbine's long side is the wind path's width.
    internal const int TurbineWidth = 7;
    internal const int SolarSide = 4;
    internal const int GeothermalSide = 6;

    /// <summary>The enclosure's thickness.</summary>
    internal const int GeothermalShell = 2;

    /// <summary>The solar generator, planned on its 4x4 plots.</summary>
    public const string SolarDefinition = "SolarGenerator";

    /// <summary>
    /// A turbine's native centre and rotation on its 7x2 pad (2x7 turned): lowRow puts it on the pad's low row (z, or x
    /// turned), facing north (east turned); otherwise it stands on the high row facing the other way.
    /// </summary>
    public static (Cell Center, Rotation Rot) TurbinePlacement(Rectangle area, bool lowRow)
    {
        if (area.Width == 2 && area.Height == TurbineWidth) // turned: the low column faces east
        {
            return lowRow ? (new Cell(area.X, area.Z + 3), Rotation.East) : (new Cell(area.X + 1, area.Z + 3), Rotation.West);
        }
        return lowRow ? (new Cell(area.X + 3, area.Z), Rotation.North) : (new Cell(area.X + 3, area.Z + 1), Rotation.South);
    }

    /// <summary>Lists r's cells row by row.</summary>
    public static List<Cell> RectangleCells(Rectangle r)
    {
        var output = new List<Cell>();
        for (var z = r.Z; z < r.Z + r.Height; z++)
        {
            for (var x = r.X; x < r.X + r.Width; x++)
            {
                output.Add(new Cell(x, z));
            }
        }
        return output;
    }

    /// <summary>
    /// CompPowerPlantWind's wind path for a turbine centred on centre (WindTurbineUtility.CalculateWindCells): 7 wide,
    /// 10 rows in front starting two out and 6 behind. A roof of any kind, or a wind-blocking thing, on a cell cuts the
    /// output by 20 percent; the turbine's size does not enter.
    /// </summary>
    public static List<Cell> TurbineWindCells(Cell center, Rotation rot)
    {
        int off = 0, front = LayoutPowerNodesRules.WindPathAhead - 1, back = LayoutPowerNodesRules.WindPathBehind - 1;
        if (rot != Rotation.North && rot != Rotation.East)
        {
            (off, front, back) = (-1, LayoutPowerNodesRules.WindPathBehind - 1, LayoutPowerNodesRules.WindPathAhead - 1);
        }
        const int half = LayoutPowerNodesRules.WindPathWidth / 2;
        Rectangle a, b;
        if (rot == Rotation.East || rot == Rotation.West)
        {
            a = new Rectangle { X = center.X + LayoutPowerNodesRules.WindPathStart + off, Z = center.Z - half, Width = front + 1, Height = LayoutPowerNodesRules.WindPathWidth };
            b = new Rectangle { X = center.X - 1 - back + off, Z = center.Z - half, Width = back + 1, Height = LayoutPowerNodesRules.WindPathWidth };
        }
        else
        {
            a = new Rectangle { X = center.X - half, Z = center.Z + LayoutPowerNodesRules.WindPathStart + off, Width = LayoutPowerNodesRules.WindPathWidth, Height = front + 1 };
            b = new Rectangle { X = center.X - half, Z = center.Z - 1 - back + off, Width = LayoutPowerNodesRules.WindPathWidth, Height = back + 1 };
        }
        var output = new List<Cell>();
        foreach (var r in new[] { a, b })
        {
            output.AddRange(RectangleCells(r));
        }
        return output;
    }
}

partial record PlannedRoom
{
    /// <summary>The cells a room's walls and interior take. A ring is its block, a range its range block; any other unfenced room blocks its interior.</summary>
    /// <summary>A barn's or pen's outline: its interior with the ring.</summary>
    internal Rectangle Outline() => LayoutPerimeterRules.Pad(this.Region.Bounds(), 1);

    internal static Rectangle RoomWalls(PlannedRoom r)
    {
        if (r.Role == LayoutRingRules.PlannedSparringRing)
        {
            return RingBlock(r);
        }
        if (r.Unfenced())
        {
            return r.Role == LayoutRangeRules.PlannedTrainingRange ? RangeBlock(r) : r.Region.Bounds();
        }
        var inner = r.Region.Bounds();
        return new Rectangle { X = inner.X - 1, Z = inner.Z - 1, Width = inner.Width + 2, Height = inner.Height + 2 };
    }
}

partial record LayoutPlan
{
    /// <summary>
    /// The plan's sites for definition: wind turbines on their pads (TurbinePlacement), solar on its plots, the
    /// geothermal generator in its enclosure. Empty for any other definition or a plan with no such site.
    /// </summary>
    public static List<PlannedPowerSite> PlannedPowerSites(LayoutPlan plan, string definition)
    {
        var output = new List<PlannedPowerSite>();
        if (definition == PowerRules.WindTurbineDefinition)
        {
            foreach (var s in plan.LiveSites(LayoutPowerNodesRules.NodeTurbine))
            {
                var turn = LayoutPowerNodesRules.TurbineTurns.FirstOrDefault(t => t.Rot == s.Rotation);
                if (turn == default)
                {
                    continue;
                }
                var area = s.Footprint.Bounds();
                var (c, rot) = LayoutUtilityRules.TurbinePlacement(area, turn.LowRow);
                output.Add(new PlannedPowerSite { Cell = c, Rotation = rot, Area = area });
            }
        }
        else if (definition == LayoutUtilityRules.SolarDefinition)
        {
            foreach (var s in plan.LiveSites(LayoutPowerNodesRules.NodeSolar))
            {
                var a = s.Footprint.Bounds();
                output.Add(new PlannedPowerSite { Cell = new Cell(a.X + 1, a.Z + 1), Rotation = Rotation.North, Area = a });
            }
        }
        else if (definition == PowerRules.GeothermalDefinition)
        {
            // The 6x6 generator inside the shell, on the geyser's position: a 6-wide footprint spans its centre -2..+3.
            foreach (var r in plan.Reservations)
            {
                if (r.Kind == LayoutPlanRules.ReserveGeothermal)
                {
                    var gen = LayoutPerimeterRules.Pad(r.Area.Bounds(), -LayoutUtilityRules.GeothermalShell);
                    output.Add(new PlannedPowerSite { Cell = new Cell(gen.X + 2, gen.Z + 2), Rotation = Rotation.North, Area = gen });
                }
            }
        }
        return output;
    }

    internal static UtilityGrid NewUtilityGrid(LayoutPlan plan) => new UtilityGrid(plan);

    /// <summary>The cells the turbine standing on area must keep clear: its own wind path, not the pair's whole lane area. Empty for a site that is not a turbine (a solar plot).</summary>
    public static List<Cell> TurbineCatchZone(LayoutPlan plan, Rectangle area)
    {
        foreach (var site in PlannedPowerSites(plan, PowerRules.WindTurbineDefinition))
        {
            if (site.Area == area)
            {
                return LayoutUtilityRules.TurbineWindCells(site.Cell, site.Rotation);
            }
        }
        return [];
    }
}
