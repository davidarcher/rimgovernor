namespace RimGovernor.Brain.Policy;

/// <summary>
/// One planned battery or generator placement: the native centre and rotation, and the footprint the preview must
/// match. Block is the stone block between a battery and the one before it on its side (zero for the first row and for
/// generators).
/// </summary>
public sealed record PlannedPowerSite
{
    public Cell Cell { get; init; }
    public Rotation Rotation { get; init; }
    public Rectangle Area { get; init; }
    public Rectangle Block { get; init; }
}

/// <summary>A cooled room's planned cooler: the cell in its back wall in front of the exhaust reservation, turned so the hot side faces the exhaust and the cold side the room.</summary>
public sealed record PlannedCoolerSite
{
    public Cell Cell { get; init; }
    public Rotation Rotation { get; init; }
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
/// Utility reservations. The plan holds a battery room (a room like any other), wind
/// turbines and solar plots as stored site nodes, a stone enclosure around each geyser's generator and an exhaust cell
/// (or dug shaft) behind each room that needs cooling. The generator never builds over a reservation.
/// </summary>
public static partial class LayoutUtilityRules
{
    /// <summary>The battery room: 5 wide, a 1-cell walkway from the door down the middle, batteries (1x2) along both sides with a stone block between each, roofed, stone door.</summary>
    public static PlannedRole PlannedBattery => PlannedRole.Battery;

    /// <summary>The cells behind a cooled room's back wall the cooler dumps heat into: one open cell, or a shaft dug through rock to open ground.</summary>
    public static ReservationKind ReserveExhaust => ReservationKind.Exhaust;

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

    /// <summary>The rooms that need a cooler, most important first.</summary>
    internal static PlannedRole[] CoolingRoles => [LayoutSurveyRules.PlannedFreezer, LayoutSurveyRules.PlannedTomb, LayoutSurveyRules.PlannedMorgue, PlannedRole.MealCloset];

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
    /// <summary>
    /// A battery room's battery footprints (1x2 across the walkway on each side), nearest the door first; the rows
    /// between them hold the stone blocks. A room on a crossing (east or west door) is the same room transposed: 1x2
    /// slots along z, rows stepping along x.
    /// </summary>
    public static List<Rectangle> BatterySlots(PlannedRoom r)
    {
        if (r.DoorRot == Rotation.East || r.DoorRot == Rotation.West)
        {
            return [.. BatterySlots(TransposeRoom(r)).Select(s => new Rectangle { X = s.Z, Z = s.X, Width = s.Height, Height = s.Width })];
        }
        var inner = r.Region.Bounds();
        var output = new List<Rectangle>();
        foreach (var i in InteriorHelpersRules.AisleRows(inner.Height, 2))
        {
            var z = inner.Z + i;
            if (r.DoorRot == Rotation.North) // door on the north wall
            {
                z = inner.Z + inner.Height - 1 - i;
            }
            output.Add(new Rectangle { X = inner.X, Z = z, Width = 2, Height = 1 });
            output.Add(new Rectangle { X = inner.X + inner.Width - 2, Z = z, Width = 2, Height = 1 });
        }
        return output;
    }

    /// <summary>A room's back wall cell (the middle of the wall facing away from its hallway), the step pointing out through it, and that direction.</summary>
    internal static (Cell Wall, Cell Step, Rotation Rot) BackWall(PlannedRoom room)
    {
        var inner = room.Region.Bounds();
        if (room.DoorRot == Rotation.North) // room south of an east-west hallway
        {
            return (new Cell(inner.X + (inner.Width / 2), inner.Z - 1), new Cell(0, -1), Rotation.South);
        }
        if (room.DoorRot == Rotation.East) // room west of a crossing
        {
            return (new Cell(inner.X - 1, inner.Z + (inner.Height / 2)), new Cell(-1, 0), Rotation.West);
        }
        if (room.DoorRot == Rotation.West)
        {
            return (new Cell(inner.X + inner.Width, inner.Z + (inner.Height / 2)), new Cell(1, 0), Rotation.East);
        }
        return (new Cell(inner.X + (inner.Width / 2), inner.Z + inner.Height), new Cell(0, 1), Rotation.North);
    }

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
    /// The plan's sites for definition: batteries in the battery room's slots (1x2 turned east, or facing north in a
    /// room on a crossing), wind turbines on their pads (TurbinePlacement), solar on its plots. Empty for any other
    /// definition or a plan with no such site.
    /// </summary>
    public static List<PlannedPowerSite> PlannedPowerSites(LayoutPlan plan, string definition)
    {
        var output = new List<PlannedPowerSite>();
        if (definition == PowerRules.BatteryDefinition)
        {
            // The shelter's porch holds the batteries until the battery room is planned.
            if (plan.RoomsOf(LayoutUtilityRules.PlannedBattery).Count == 0)
            {
                output.AddRange(PorchSites(plan));
            }
            foreach (var r in plan.Rooms)
            {
                if (r.Role != LayoutUtilityRules.PlannedBattery)
                {
                    continue;
                }
                var slots = PlannedRoom.BatterySlots(r);
                for (var i = 0; i < slots.Count; i++)
                {
                    var s = slots[i];

                    // A 1x2 battery turned east spans x..x+1; facing north z..z+1.
                    var rot = s.Height == 2 ? Rotation.North : Rotation.East;
                    var site = new PlannedPowerSite { Cell = new Cell(s.X, s.Z), Rotation = rot, Area = s };
                    if (i >= 2)
                    {
                        var prev = slots[i - 2];
                        site = site with
                        {
                            Block = rot == Rotation.North
                                ? new Rectangle { X = (s.X + prev.X) / 2, Z = s.Z, Width = 1, Height = s.Height }
                                : new Rectangle { X = s.X, Z = (s.Z + prev.Z) / 2, Width = s.Width, Height = 1 },
                        };
                    }
                    output.Add(site);
                }
            }
        }
        else if (definition == PowerRules.WindTurbineDefinition)
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

    /// <summary>A cooler site per cooling room whose exhaust the plan reserved, in plan order.</summary>
    public static List<PlannedCoolerSite> PlannedCoolerSites(LayoutPlan plan)
    {
        var output = new List<PlannedCoolerSite>();
        foreach (var role in LayoutUtilityRules.CoolingRoles)
        {
            foreach (var r in plan.Rooms)
            {
                if (r.Role != role)
                {
                    continue;
                }
                if (plan.CoolerExhaust(r) is { } found)
                {
                    output.Add(found.Site);
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

public sealed partial record LayoutPlan
{
    /// <summary>The cooler site in room's back wall and the exhaust the plan reserved behind it; null when the plan reserved none.</summary>
    public (PlannedCoolerSite Site, Rectangle Exhaust)? CoolerExhaust(PlannedRoom room)
    {
        var (wall, step, rot) = PlannedRoom.BackWall(room);
        var first = new Cell(wall.X + step.X, wall.Z + step.Z);
        foreach (var e in Reservations)
        {
            var area = e.Area.Bounds();
            if (e.Kind == LayoutUtilityRules.ReserveExhaust && (area.Width == 1 || area.Height == 1) && e.Area.Contains(first))
            {
                return (new PlannedCoolerSite { Cell = wall, Rotation = rot }, area);
            }
        }
        return null;
    }
}
