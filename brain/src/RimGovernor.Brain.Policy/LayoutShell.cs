namespace RimGovernor.Brain.Policy;

// The shell of a planned room as the game's RoomTempTracker sees it (#4045, epic #2795): for each floor cell and
// cardinal direction whose next cell is a ring wall, the cell two out is an equalize cell unless it is a room's
// floor. Those are the cells that leak to the outdoors at WallEqualizeFactor and the cells a second wall layer
// stands on; a corner is never one, since no floor cell lies straight inside it.
//
// The second wall layer (#4046): the tiler places it where it beats heating (RG-HABITAT-008), as the plan-level
// derived region LayoutPlan.OuterWalls, the way Roofed is derived at tile time. After the airlocks (so the
// vestibule, its walls, the outer door and the cell beyond it are known) and before the lamps and conduits, each
// fenced room in the roofed set is priced as placed (its exterior opening as chosen, door or airlock, its link
// doors closed and its vents) with the ring alone and with a layer on the cells it can have, the layer's cells
// bought at the catalog's wall at the layout's own rate (BuildW); the layer is placed when it is the cheaper total.
// The layer is per cell, so a room whose candidates are partly blocked is priced on the cells it can have.

partial record LayoutPlan
{
    private static readonly (int X, int Z)[] Cardinal = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    /// <summary>
    /// The cells the game samples for the room's outdoor-facing walls: for each ring cell with a floor cell straight
    /// inside it, the door excluded, the cell two out when it is no room's floor in this plan; one entry per sampled
    /// cell, so the count is the room's outdoor wall count and the cells are where a second layer stands. Empty for
    /// an unfenced room (an airlock's vestibule is priced through its parent's link) and for a room with no floor.
    /// </summary>
    public IReadOnlyList<Cell> OutdoorWallCells(PlannedRoom room)
    {
        ArgumentNullException.ThrowIfNull(room);
        if (room.Unfenced() || room.Region.Empty())
        {
            return [];
        }

        var door = room.DoorSide is DoorSide.Doored ? room.Door : (Cell?)null;
        var output = new List<Cell>();
        foreach (var c in room.Region.Cells())
        {
            foreach (var (dx, dz) in Cardinal)
            {
                var wall = new Cell(c.X + dx, c.Z + dz);
                if (room.Region.Contains(wall) || wall == door)
                {
                    continue;
                }

                var beyond = new Cell(c.X + (2 * dx), c.Z + (2 * dz));
                if (Rooms.Any(o => o.Region.Contains(beyond)) || output.Contains(beyond))
                {
                    continue;
                }

                output.Add(beyond);
            }
        }

        return output;
    }
}

public static partial class LayoutTileRules
{
    public const string ReasonTileShell = "tile_shell";
    public const string ReasonTileShellNotPaid = "tile_shell_not_paid";
    public const string ReasonTileShellNoGround = "tile_shell_no_ground";
    public const string ReasonTileShellUnpriced = "tile_shell_unpriced";
    public const string ReasonTileShellTemperate = "tile_shell_temperate";
}

partial record LayoutPlan
{
    /// <summary>Whether the outer wall layer stands clear of every room's floor and ring, every airlock's cells and every planned site (Valid rejects an overlap).</summary>
    private bool ValidOuterWalls()
    {
        if (OuterWalls.Empty())
        {
            return true;
        }
        foreach (var r in Rooms)
        {
            var block = r.Unfenced() ? r.Region.Bounds() : PlannedRoom.RoomWalls(r);
            if (OuterWalls.OverlapsRect(block) || r.AirlockCells().Any(OuterWalls.Contains))
            {
                return false;
            }
        }
        foreach (var s in Sites)
        {
            if (OuterWalls.Overlaps(s.Footprint))
            {
                return false;
            }
        }
        return true;
    }
}

internal sealed partial class Tiler
{
    /// <summary>
    /// Places the second wall layer on every roofed room whose layer is the cheaper total in sustained watts, as
    /// LayoutPlan.OuterWalls, and says per room why it has none. A room's candidate cells are its sampled cells
    /// (LayoutPlan.OutdoorWallCells) that are open ground no other layer, door, vent, cooler, airlock or kept route
    /// holds (ShellBlocked); a room with none says so. The room is priced as placed, its outdoor wall count the walls
    /// a candidate backs, with the ring alone and with the layer, the layer's cells at the catalog's wall (BuildW).
    /// </summary>
    internal void PlaceShell()
    {
        var p = Out.Plan;
        if (p.Roofed.Empty())
        {
            NoteShell(-1, LayoutTileRules.ReasonTileShellTemperate, LayoutTileRules.TilePlace);
            return;
        }
        var m = In.Catalog.Climate;
        if (!m.Valid() || !QuestCapacityForecast.FinitePositive(In.SteelValue))
        {
            NoteShell(-1, LayoutTileRules.ReasonTileShellUnpriced, LayoutTileRules.TileUnsatisfied);
            return;
        }
        var wallW = ClimateRules.BuildW(SiteRules.DefaultSiteTypeWeights(), m.Build.Wall, In.SteelValue);
        var blocked = ShellBlocked(p);
        var movements = LayoutPlan.ExteriorDoorMovements(p);
        var layer = new List<Cell>();
        for (var i = 0; i < Rooms.Count; i++)
        {
            var room = p.Rooms[i];
            if (room.Unfenced() || !LayoutTileAirlockRules.RoofedRoom(p.Roofed, room))
            {
                continue;
            }
            var g = In.Groups[Rooms[i].Group];
            var candidates = p.OutdoorWallCells(room).Where(c => Free.Contains(c) && !blocked.Contains(c) && !layer.Contains(c)).ToList();
            if (candidates.Count == 0)
            {
                NoteShell(g.Id, LayoutTileRules.ReasonTileShellNoGround, LayoutTileRules.TileUnsatisfied);
                continue;
            }
            double lone, doubled;
            try
            {
                var passages = movements.GetValueOrDefault(room.Id);
                lone = ShellCost(i, candidates.Count, ShellLayers.Lone, passages);
                doubled = ShellCost(i, candidates.Count, ShellLayers.Doubled, passages) + (candidates.Count * wallW);
            }
            catch (DomainException)
            {
                NoteShell(g.Id, LayoutTileRules.ReasonTileShellUnpriced, LayoutTileRules.TileUnsatisfied);
                continue;
            }
            if (doubled >= lone)
            {
                NoteShell(g.Id, LayoutTileRules.ReasonTileShellNotPaid, LayoutTileRules.TileUnsatisfied);
                continue;
            }
            layer.AddRange(candidates);
            NoteShell(g.Id, LayoutTileRules.ReasonTileShell, LayoutTileRules.TilePlace);
        }
        if (layer.Count > 0)
        {
            Out.Plan = Out.Plan with { OuterWalls = RegionRules.RegionOf(layer.SortedBy(CombatManhunterRules.CellLess)) };
        }
    }

    /// <summary>
    /// Room i's cost a year in sustained watts as placed, with its heaters and coolers built (PlantsW): its exterior
    /// opening (an airlock where one is attached, else its door at its traffic), its link doors closed on their
    /// neighbours held where their bands hold them, its vents, and walls outdoor-facing walls behind a shell of
    /// shell layers.
    /// </summary>
    private double ShellCost(int i, int walls, ShellLayers shell, double passages)
    {
        var p = Out.Plan;
        var m = In.Catalog.Climate;
        var room = p.Rooms[i];
        var g = In.Groups[Rooms[i].Group];
        var band = LayoutRoofRules.ComfortBand();
        if (LayoutTileRules.GroupCold(g) && In.FullRotRateC > 0)
        {
            band = new Band { MinC = double.NegativeInfinity, MaxC = In.FullRotRateC };
        }
        var outdoors = new ClimateSide { Outdoors = true };
        var links = new List<ClimateLink>();
        if (ExtDoor[i] is not null)
        {
            var opening = p.ChildrenOfRole(room.Id, PlannedRole.Airlock).Count > 0 ? Opening.Airlock : Opening.Door;
            links.Add(new ClimateLink { Opening = opening, Side = outdoors });
        }
        ClimateSide Side(int j)
        {
            var b = VentBand(In.Groups[Rooms[j].Group]) ?? m.Living;
            return new ClimateSide { HeldC = HeldAt(b), Cells = RoomCells(j) };
        }
        foreach (var j in Linked(i))
        {
            links.Add(new ClimateLink { Opening = Opening.DoorClosed, Side = Side(j) });
        }
        foreach (var v in Vents)
        {
            if (v.Room == i)
            {
                links.Add(new ClimateLink { Opening = Opening.Vent, Side = v.Other < 0 ? outdoors : Side(v.Other) });
            }
            else if (v.Other == i)
            {
                links.Add(new ClimateLink { Opening = Opening.Vent, Side = Side(v.Room) });
            }
        }
        var r = new ClimateRoom
        {
            Cells = RoomCells(i),
            Band = band,
            OutdoorWalls = walls,
            Shell = shell,
            Heat = EquatableArray.From(In.Catalog.GroupHeat(g)),
            Links = EquatableArray.From(links),
        };
        var w = ClimateRules.ClimateLoad(m, In.OutdoorCurve, r, passages);
        return w.MeanW + m.PlantsW(SiteRules.DefaultSiteTypeWeights(), In.SteelValue, w);
    }

    /// <summary>
    /// The cells no layer stands on: the cell beyond each exterior door, the outside cell of each placed vent and of
    /// each wall cell ChooseVents left for the group's wall stations, every airlock's ground and the cell beyond its
    /// outer door, the entrances, and every cell on a trip's walk (the routes KeepRoutes kept, TripCellMovements).
    /// </summary>
    private HashSet<Cell> ShellBlocked(LayoutPlan p)
    {
        var blocked = new HashSet<Cell>(p.Entrances);
        for (var i = 0; i < Rooms.Count; i++)
        {
            if (ExtDoor[i] is not null)
            {
                blocked.Add(ExtOut[i]);
            }
        }
        Cell? OutsideOf(int i, Cell wall)
        {
            var (rot, ok) = InteriorRules.DoorSide(Rooms[i].Rect, wall);
            return ok ? LayoutAirlockRules.Outside(wall, rot) : null;
        }
        var used = new HashSet<Cell>(Vents.Select(v => v.Cell));
        foreach (var v in Vents)
        {
            if (v.Other < 0 && OutsideOf(v.Room, v.Cell) is { } outside)
            {
                blocked.Add(outside);
            }
        }
        for (var i = 0; i < Rooms.Count; i++)
        {
            var demand = WallDemand(In.Groups[Rooms[i].Group]);
            if (demand == 0)
            {
                continue;
            }
            var candidates = VentCandidates(i, used);
            foreach (var wall in candidates.Skip(Math.Max(0, candidates.Count - demand)))
            {
                if (OutsideOf(i, wall) is { } outside)
                {
                    blocked.Add(outside);
                }
            }
        }
        foreach (var a in p.Rooms)
        {
            if (a.Role != PlannedRole.Airlock || a.DoorSide is not DoorSide.Doored doored)
            {
                continue;
            }
            blocked.UnionWith(a.AirlockCells());
            blocked.Add(a.Vestibule());
            blocked.Add(LayoutAirlockRules.Outside(a.Door, doored.Side));
        }
        blocked.UnionWith(LayoutPlan.TripCellMovements(p).Keys);
        return blocked;
    }

    internal void NoteShell(int group, string reason, string verdict) =>
        Out.Decisions.Add(new TileDecision { Group = group, Verdict = verdict, Reason = reason });
}
