namespace RimGovernor.Brain.Policy;

public static partial class SafeAreaRules
{
    /// <summary>The bot area key of the Safe allowed area.</summary>
    public const string SafeAreaKey = "Safe";

    /// <summary>
    /// The union of the enclosed, fully roofed rooms (every role: workshops keep sheltered pawns working)
    /// minus the excluded cells; a room with a door facing the killbox is left out unless every roofed room has one:
    /// a roof with a door toward the killbox still shelters from fallout, weather and a pack better than no
    /// Safe area at all. Sorted, deduplicated.
    /// </summary>
    public static List<Cell> SafeAreaCells(RoomObservation rooms, IReadOnlyList<Cell> killbox, IReadOnlyList<Cell> excludedCells)
    {
        var excluded = new HashSet<Cell>(excludedCells);
        var inner = new HashSet<Cell>();
        var exposed = new HashSet<Cell>();
        foreach (var room in rooms.Rooms)
        {
            if (!room.Enclosed() || !room.Roofed())
            {
                continue;
            }
            var set = inner;
            foreach (var d in room.Doors)
            {
                if (d.EnemyFacing(killbox))
                {
                    set = exposed;
                }
            }
            foreach (var c in rooms.CellsOf(room))
            {
                if (!excluded.Contains(c))
                {
                    set.Add(c);
                }
            }
        }
        return PlannedRules.SortedCells(inner.Count == 0 ? exposed : inner);
    }

    /// <summary>
    /// The AreaIntent edits that bring the bot area key from current to want: with current unknown a reset
    /// (delete, then create with want), otherwise only real diffs (set_cells, clear_cells).
    /// </summary>
    public static List<Area> PlanBotArea(string key, IReadOnlyList<Cell> want, IReadOnlyList<Cell> current, bool currentKnown)
    {
        if (!currentKnown)
        {
            var del = new Area(AreaOperation.Delete, key, []);
            var create = new Area(AreaOperation.Create, key, want.ToList());
            return [del, create];
        }
        var have = new HashSet<Cell>(current);
        var wanted = new HashSet<Cell>();
        var added = new List<Cell>();
        foreach (var c in want)
        {
            wanted.Add(c);
            if (!have.Contains(c))
            {
                added.Add(c);
            }
        }
        var removed = new HashSet<Cell>();
        foreach (var c in have)
        {
            if (!wanted.Contains(c))
            {
                removed.Add(c);
            }
        }
        var output = new List<Area>();
        if (added.Count > 0)
        {
            output.Add(new Area(AreaOperation.SetCells, key, added));
        }
        if (removed.Count > 0)
        {
            output.Add(new Area(AreaOperation.ClearCells, key, PlannedRules.SortedCells(removed)));
        }
        return output;
    }
}
