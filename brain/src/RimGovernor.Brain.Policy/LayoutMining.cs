namespace RimGovernor.Brain.Policy;

// Mine tiers order mining designations along the plan's mining zone: ore first, then the rock under dug rooms
// (walls included) and the cooler exhaust shafts so the core's footprint is cleared ahead of growth, then the
// remaining stone as demand calls for it. A dug store room (StoreRoom) ranks between ore and the other dug
// rooms: its zone waits on the dig and no stand-in store holds supplies meanwhile. A cell off the plan, or no
// plan, is MineTierStone.
public static partial class PolicyConstants
{
    public const int MineTierOre = 0;
    public const int MineTierStore = 1;
    public const int MineTierCore = 2;
    public const int MineTierStone = 3;
}

public static partial class PolicyFunctions
{
    /// <summary>Ranks cell under plan; a cell in the zone zoning marked Ore ranks first.</summary>
    public static int MineTier(this LayoutPlan p, Cell cell)
    {
        foreach (var z in p.Zones)
        {
            if (z.Kind == ZoneKind.Mining && z.Ore && ZoneHas(z, cell))
            {
                return MineTierOre;
            }
        }
        foreach (var r in p.Rooms)
        {
            if (r.Dug && IsStoreRoom(r.Role) && InRoomRing(r.Region.Bounds(), cell))
            {
                return MineTierStore;
            }
        }
        foreach (var r in p.Rooms)
        {
            var interior = r.Region.Bounds();
            if (r.Dug && cell.X >= interior.X - 1 && cell.X <= interior.X + interior.Width && cell.Z >= interior.Z - 1 && cell.Z <= interior.Z + interior.Height)
            {
                return MineTierCore;
            }
        }
        foreach (var e in p.Reservations)
        {
            if (e.Kind == ReservationKind.Exhaust && e.Area.Contains(cell))
            {
                return MineTierCore;
            }
        }
        return MineTierStone;
    }

    internal static bool InRoomRing(Rectangle interior, Cell cell) =>
        cell.X >= interior.X - 1 && cell.X <= interior.X + interior.Width && cell.Z >= interior.Z - 1 && cell.Z <= interior.Z + interior.Height;

    /// <summary>
    /// The shared rock step (RockStep) over the cells a planned room's shell waits on: its door and interior
    /// and, for a cooled room, its cooler's wall cell and exhaust shaft, mined before the ring so the ring walls
    /// the cooler cell and the room is never left open, then the cell outside the door. All of them are built on
    /// or walked, so each needs a floor; rock on the rest of the ring stays and walls the room. Dig is in that
    /// priority order.
    /// </summary>
    public static RockStepResult RoomRock(this LayoutPlan p, PlannedRoom room, IReadOnlyList<SiteCell> cells)
    {
        var planned = new List<RoleCell>();
        void Add(Cell c) => planned.Add(new RoleCell { Cell = c, Role = RockRole.NeedsFloor });

        Add(room.Door);
        foreach (var c in RectangleCells(room.Region.Bounds()))
        {
            Add(c);
        }
        var cooler = p.CoolerExhaust(room);
        if (cooler.Ok)
        {
            Add(cooler.Site.Cell);
            foreach (var c in RectangleCells(cooler.Area))
            {
                Add(c);
            }
        }
        RoomShell? shell;
        try
        {
            shell = room.Footprint().ToShell();
        }
        catch (DomainException)
        {
            shell = null;
        }
        if (shell is not null)
        {
            var from = shell.Threshold();
            // A room entered through a neighbour's wall (the butchery behind the freezer) is reached only by
            // walking that neighbour: its interior, door and the corridor from its own threshold are mined too,
            // or the miners have no way in to the link.
            var (through, ok) = p.LinkedNeighbour(room);
            if (ok)
            {
                foreach (var c in RectangleCells(through!.Region.Bounds()))
                {
                    Add(c);
                }
                Add(through.Door);
                try
                {
                    from = through.Footprint().ToShell().Threshold();
                }
                catch (DomainException)
                {
                    // the neighbour's own footprint is unusable: keep the room's threshold
                }
            }
            Add(from);
            foreach (var d in shell.ExtraDoors())
            {
                Add(d.Threshold());
            }
        }
        return RockStep(planned, cells);
    }

    /// <summary>The other room whose wall holds one of room's link doors.</summary>
    internal static (PlannedRoom? Room, bool Ok) LinkedNeighbour(this LayoutPlan p, PlannedRoom room)
    {
        foreach (var link in p.LinksOf(room))
        {
            foreach (var o in p.Rooms)
            {
                if (o.Region.Bounds() != room.Region.Bounds() && InWall(o.Region.Bounds(), link))
                {
                    return (o, true);
                }
            }
        }
        return (null, false);
    }

    /// <summary>
    /// The shared rock step over a standing room's planned cooler wall cell and exhaust shaft. The cooler cell
    /// is in Dig while the room's back wall is still rock: mining it would open the room, so the caller mines it
    /// only in the plan that places the cooler there.
    /// </summary>
    public static (RockStepResult Result, bool Ok) ExhaustRock(this LayoutPlan p, PlannedRoom room, IReadOnlyList<SiteCell> cells)
    {
        var cooler = p.CoolerExhaust(room);
        if (!cooler.Ok)
        {
            return (default!, false);
        }
        var planned = new List<RoleCell> { new() { Cell = cooler.Site.Cell, Role = RockRole.NeedsFloor } };
        foreach (var c in RectangleCells(cooler.Area))
        {
            planned.Add(new RoleCell { Cell = c, Role = RockRole.NeedsFloor });
        }
        return (RockStep(planned, cells), true);
    }

    internal static bool ZoneHas(LayoutZone z, Cell cell)
    {
        foreach (var run in z.Runs)
        {
            if (run.Z == cell.Z && cell.X >= run.X && cell.X < run.X + run.Length)
            {
                return true;
            }
        }
        return false;
    }
}
