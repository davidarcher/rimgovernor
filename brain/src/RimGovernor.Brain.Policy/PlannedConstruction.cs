namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>
    /// The material the whole layout plan still owes its walls and doors, per resource: every ring cell of
    /// every room the ground does not yet stand on, priced by price (a def's cost list, empty or null where
    /// the def is unavailable). A wall or door two rooms share is counted once. This is the plan's
    /// declared demand: it is not filtered by which concern is raised or which room is built first, so the
    /// supply ranker sees the whole plan and chooses what to gather. Rooms a replan retired carry no ring.
    /// </summary>
    public static Dictionary<Resource, long> PlannedShellDemand(LayoutPlan plan, GroundCensus ground, Func<string, IReadOnlyList<Amount>?> price)
    {
        var cells = new Dictionary<string, HashSet<Cell>>(StringComparer.Ordinal);
        foreach (var room in plan.RoomsWithHerd())
        {
            if (room.Id == 0 || room.Unfenced() || room.Attached())
            {
                continue;
            }
            var (wallDef, doorDef) = room.RingDefs();
            var (walls, doors) = plan.Ring(room, ground);
            var shellDoors = new HashSet<Cell>(plan.ShellDoors(room));
            foreach (var c in room.Region.Walls().Cells())
            {
                var door = shellDoors.Contains(c);
                var standing = door ? doors.Contains(c) : walls.Contains(c);
                if (standing)
                {
                    continue;
                }
                var def = door ? doorDef : wallDef;
                if (!cells.TryGetValue(def, out var set))
                {
                    set = [];
                    cells[def] = set;
                }
                _ = set.Add(c);
            }
        }
        var result = new Dictionary<Resource, long>();
        foreach (var (def, set) in cells)
        {
            if (price(def) is not { } costs)
            {
                continue;
            }
            foreach (var cost in costs)
            {
                if (cost.Count > 0)
                {
                    result[cost.Resource] = result.GetValueOrDefault(cost.Resource) + (cost.Count * set.Count);
                }
            }
        }
        return result;
    }
}
