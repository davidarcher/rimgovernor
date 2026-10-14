namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>The rooms a plan builds a ring and contents for: the herd rooms too, not the plan's unfenced or attached rooms or its root.</summary>
    public static IReadOnlyList<PlannedRoom> PlannedBuiltRooms(LayoutPlan plan) =>
        [.. plan.Rooms.Where(r => r.Id != 0 && !r.Unfenced() && !r.Attached())];

    /// <summary>
    /// What the layout plan still owes, per room and resource: the operations the reconciler says each room owes
    /// (Reconcile's Owed, read over the plan rather than the clearance census), priced. This is the same diff the
    /// owners commit, so the price and the build cannot disagree about what is missing. Wall, door and vent
    /// cells are owed by every room that rings them but counted once, for the lowest room id. Floors are priced
    /// by floorPrice (a floor's cost list), the ring and vents by shellPrice and pieces by piecePrice (null where
    /// the def is unavailable). Pieces and ring cells with a blueprint or frame placed (placed) are owed by the census, not here. Nothing is filtered by which concern is raised or which room is built first: the supply
    /// ranker sees the whole plan and chooses what to gather. The colony-driven additions to a room (more
    /// sleepers, an upgrade) are not here: each owner derives them when it furnishes.
    /// </summary>
    public static Dictionary<int, Dictionary<Resource, long>> PlannedOwedCost(
        IEnumerable<(PlannedRoom Room, IReadOnlyList<Operation> Owed)> rooms,
        Func<string, IReadOnlyList<Amount>?> shellPrice,
        Func<string, IReadOnlyList<Amount>?> piecePrice,
        Func<string, IReadOnlyList<Amount>?> floorPrice,
        string ventDef,
        IReadOnlySet<(string Def, Cell Cell)> placed)
    {
        // A blueprint or frame already placed is owed by the construction census (its outstanding material), not
        // by the plan: counting it here too would claim the same material twice.
        var claimed = new HashSet<(string Def, Cell Cell)>(placed);
        var result = new Dictionary<int, Dictionary<Resource, long>>();
        foreach (var (room, owed) in rooms.OrderBy(r => r.Room.Id))
        {
            var (wallDef, doorDef) = room.RingDefs();
            foreach (var op in owed)
            {
                switch (op.Kind)
                {
                    case OpKind.WallIn:
                        AddCells(result, room.Id, wallDef, op.Cells, claimed, shellPrice);
                        break;
                    case OpKind.DoorIn:
                        AddCells(result, room.Id, doorDef, op.Cells, claimed, shellPrice);
                        break;
                    case OpKind.VentIn:
                        AddCells(result, room.Id, ventDef, op.Cells, claimed, shellPrice);
                        break;
                    case OpKind.FloorIn:
                        foreach (var floor in op.Floors)
                        {
                            if (floorPrice(floor.DefName) is { } costs)
                            {
                                AddCost(result, room.Id, costs, 1);
                            }
                        }
                        break;
                    case OpKind.Build:
                        foreach (var piece in op.Pieces)
                        {
                            if (!placed.Contains((piece.DefName, piece.Anchor())) && piecePrice(piece.DefName) is { } costs)
                            {
                                AddCost(result, room.Id, costs, 1);
                            }
                        }
                        break;
                }
            }
        }
        return result;
    }

    private static void AddCells(Dictionary<int, Dictionary<Resource, long>> result, int room, string def, IReadOnlyList<Cell> cells,
        HashSet<(string Def, Cell Cell)> claimed, Func<string, IReadOnlyList<Amount>?> price)
    {
        var fresh = cells.Count(c => claimed.Add((def, c)));
        if (fresh > 0 && price(def) is { } costs)
        {
            AddCost(result, room, costs, fresh);
        }
    }

    private static void AddCost(Dictionary<int, Dictionary<Resource, long>> result, int room, IReadOnlyList<Amount> costs, long times)
    {
        foreach (var cost in costs)
        {
            if (cost.Count <= 0)
            {
                continue;
            }
            if (!result.TryGetValue(room, out var row))
            {
                row = [];
                result[room] = row;
            }
            row[cost.Resource] = row.GetValueOrDefault(cost.Resource) + (cost.Count * times);
        }
    }

    /// <summary>The per-resource total of per-room requirements.</summary>
    public static Dictionary<Resource, long> PlannedTotal(IEnumerable<IReadOnlyDictionary<Resource, long>> rooms)
    {
        var total = new Dictionary<Resource, long>();
        foreach (var room in rooms)
        {
            foreach (var (resource, n) in room)
            {
                total[resource] = total.GetValueOrDefault(resource) + n;
            }
        }
        return total;
    }
}
