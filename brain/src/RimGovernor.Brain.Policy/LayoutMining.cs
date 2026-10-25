namespace RimGovernor.Brain.Policy;

// Mine tiers order mining designations along the plan's mining zone: ore first, then the rock under dug rooms
// (walls included) so the core's footprint is cleared ahead of growth, then the remaining stone as demand calls for it. A dug store room (StoreRoom) ranks between ore and the other dug
// rooms: its zone waits on the dig and no stand-in store holds supplies meanwhile. A cell off the plan, or no
// plan, is MineTierStone.
public static partial class LayoutMiningRules
{
    public const int MineTierOre = 0;
    public const int MineTierStore = 1;
    public const int MineTierCore = 2;
    public const int MineTierStone = 3;

    internal static bool InRoomRing(Rectangle interior, Cell cell) =>
        cell.X >= interior.X - 1 && cell.X <= interior.X + interior.Width && cell.Z >= interior.Z - 1 && cell.Z <= interior.Z + interior.Height;

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

partial record LayoutPlan
{
    /// <summary>Ranks cell under plan; a cell in the zone zoning marked Ore ranks first.</summary>
    public int MineTier(Cell cell)
    {
        foreach (var z in this.Zones)
        {
            if (z.Kind == ZoneKind.Mining && z.Ore && LayoutMiningRules.ZoneHas(z, cell))
            {
                return LayoutMiningRules.MineTierOre;
            }
        }
        foreach (var r in this.Rooms)
        {
            if (r.Dug && InteriorOpenRules.IsStoreRoom(r.Role) && LayoutMiningRules.InRoomRing(r.Region.Bounds(), cell))
            {
                return LayoutMiningRules.MineTierStore;
            }
        }
        foreach (var r in this.Rooms)
        {
            var interior = r.Region.Bounds();
            if (r.Dug && cell.X >= interior.X - 1 && cell.X <= interior.X + interior.Width && cell.Z >= interior.Z - 1 && cell.Z <= interior.Z + interior.Height)
            {
                return LayoutMiningRules.MineTierCore;
            }
        }
        return LayoutMiningRules.MineTierStone;
    }

    /// <summary>
    /// The shared rock step (RockStep) over the cells a planned room's shell waits on: its door and interior,
    /// then the cell outside the door. All of them are built on or walked, so each needs a floor; rock on the
    /// rest of the ring stays and walls the room. Dig is in that priority order.
    /// </summary>
    public RockStepResult RoomRock(PlannedRoom room, IReadOnlyList<SiteCell> cells)
    {
        var planned = new List<RoleCell>();
        void Add(Cell c) => planned.Add(new RoleCell { Cell = c, Role = RockRole.NeedsFloor });

        Add(room.Door);
        foreach (var c in LayoutUtilityRules.RectangleCells(room.Region.Bounds()))
        {
            Add(c);
        }
        var shell = room.DoorSide is DoorSide.Doored ? room.Footprint() : null;

        if (shell is not null)
        {
            var from = shell.Threshold;
            // A room entered through a neighbour's wall (the butchery behind the freezer) is reached only by
            // walking that neighbour: its interior, door and the corridor from its own threshold are mined too,
            // or the miners have no way in to the link.
            var (through, ok) = this.LinkedNeighbour(room);
            if (ok)
            {
                foreach (var c in LayoutUtilityRules.RectangleCells(through!.Region.Bounds()))
                {
                    Add(c);
                }
                Add(through.Door);
                if (through.DoorSide is DoorSide.Doored)
                {
                    from = through.Footprint().Threshold;
                }
            }
            Add(from);
            foreach (var d in shell.ExtraDoors)
            {
                Add(d.Threshold);
            }
        }
        return RockStepRules.RockStep(planned, cells);
    }

    /// <summary>The other room whose wall holds one of room's link doors.</summary>
    internal (PlannedRoom? Room, bool Ok) LinkedNeighbour(PlannedRoom room)
    {
        foreach (var link in this.LinksOf(room))
        {
            foreach (var o in this.Rooms)
            {
                if (o.Region.Bounds() != room.Region.Bounds() && LayoutGeometryRules.InWall(o.Region.Bounds(), link))
                {
                    return (o, true);
                }
            }
        }
        return (null, false);
    }
}
