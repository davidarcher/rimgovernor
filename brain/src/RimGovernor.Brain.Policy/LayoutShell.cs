namespace RimGovernor.Brain.Policy;

// The shell of a planned room as the game's RoomTempTracker sees it (#4045, epic #2795): for each floor cell and
// cardinal direction whose next cell is a ring wall, the cell two out is an equalize cell unless it is a room's
// floor. Those are the cells that leak to the outdoors at WallEqualizeFactor and the cells a second wall layer
// would stand on; a corner is never one, since no floor cell lies straight inside it.

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
