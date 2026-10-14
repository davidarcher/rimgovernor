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

    /// <summary>
    /// The material the whole plan still owes its floors, per resource: each fenced room's interior cells that
    /// do not yet carry the floor the flooring review wants for them (WantedFloors, the choice the selector
    /// lays) and have none ordered, priced by that floor's cost list. Affordability is not asked: the stock is
    /// read as unknown so a floor the colony cannot pay for yet is still demanded, which is the point.
    /// </summary>
    public static Dictionary<Resource, long> PlannedFloorDemand(LayoutPlan plan, FlooringFacts facts, FlooringPolicy policy)
    {
        var priced = facts with { Stock = Unknown<IReadOnlyDictionary<Resource, long>>() };
        var laid = new Dictionary<Cell, FloorCell>();
        if (facts.Observation.TryGetValue(out var observed))
        {
            foreach (var room in observed.Rooms)
            {
                foreach (var cell in room.Cells)
                {
                    laid[cell.Cell] = cell;
                }
            }
        }
        var result = new Dictionary<Resource, long>();
        foreach (var room in plan.RoomsWithHerd())
        {
            if (room.Id == 0 || room.Unfenced() || room.Attached())
            {
                continue;
            }
            var wanted = WantedFloors(room, [], priced, policy);
            foreach (var cell in room.Region.Cells())
            {
                var name = wanted(cell);
                if (name.Length == 0 || !priced.Definitions.TryGetValue(name, out var def) || !def.Costs.TryGetValue(out var costs))
                {
                    continue;
                }
                if (laid.TryGetValue(cell, out var at) && (at.Terrain == name || at.Pending.Length > 0))
                {
                    continue;
                }
                foreach (var cost in costs)
                {
                    if (cost.Count > 0)
                    {
                        result[cost.Resource] = result.GetValueOrDefault(cost.Resource) + cost.Count;
                    }
                }
            }
        }
        return result;
    }
}
