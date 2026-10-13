namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>
    /// The standing goal that keeps the colony's shelter settings current: the bot-owned "Safe" allowed area,
    /// and later sheltering pawns in it and the killbox restriction.
    /// </summary>
    public static readonly ConcernId MaintainShelter = new("MaintainShelter");

    /// <summary>The bot area key of the Safe allowed area.</summary>
    public const string SafeAreaKey = "Safe";

    /// <summary>
    /// The union of the enclosed, fully roofed rooms (every role: workshops keep sheltered pawns working)
    /// minus the killbox cells; a room with an enemy-facing door is left out unless every roofed room has one:
    /// a roof with a door toward the killbox still shelters from fallout, weather and a pack better than no
    /// Safe area at all. Sorted, deduplicated.
    /// </summary>
    public static List<Cell> SafeAreaCells(RoomObservation rooms, IReadOnlyList<Cell> killbox)
    {
        var excluded = new HashSet<Cell>(killbox);
        var inner = new HashSet<Cell>();
        var exposed = new HashSet<Cell>();
        foreach (var room in rooms.Rooms)
        {
            if (!room.Enclosed.TryGetValue(out var enclosed) || !enclosed)
            {
                continue;
            }
            if (!room.Roofed.TryGetValue(out var roofed) || !roofed)
            {
                continue;
            }
            var set = inner;
            foreach (var d in room.Doors)
            {
                if (d.EnemyFacing)
                {
                    set = exposed;
                }
            }
            foreach (var c in room.Cells)
            {
                if (!excluded.Contains(c))
                {
                    set.Add(c);
                }
            }
        }
        return SortedCells(inner.Count == 0 ? exposed : inner);
    }

    /// <summary>
    /// The AreaIntent edits that bring the Safe area from current to the planned cells, and the planned cells.
    /// With current unknown (process start or reload) it resets: delete, then create with the full set.
    /// Otherwise it sends only real diffs (set_cells for added cells, clear_cells for removed ones); none when
    /// the area is stable.
    /// </summary>
    public static (List<Area> Edits, List<Cell> Want) PlanSafeArea(
        RoomObservation rooms, IReadOnlyList<Cell> killbox, IReadOnlyList<Cell> current, bool currentKnown)
    {
        var want = SafeAreaCells(rooms, killbox);
        var edits = PlanBotArea(SafeAreaKey, want, current, currentKnown);
        return (edits, want);
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
            output.Add(new Area(AreaOperation.ClearCells, key, SortedCells(removed)));
        }
        return output;
    }
}
