namespace RimGovernor.Brain.Policy;

/// <summary>
/// The same-tick native room census the planners read: every proper indoor room (the Contracts RoomState row) and
/// its cells from the frame cell grid. Room identity is ephemeral within the current native room graph.
/// </summary>
public sealed record RoomObservation
{
    public required EquatableArray<RoomState> Rooms { get; init; }

    /// <summary>Each room's cells in the frame cell grid, by room id. A room whose every cell is fogged has none.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<Cell>> Cells { get; init; }

    public EquatableArray<string> EligibleBeds { get; init; }

    /// <summary>The dining and recreation furniture the catalog rows name; it rides with the rooms because the interior plans read it per room.</summary>
    public DiningFurniture Dining { get; init; } = new();

    /// <summary>The plannable shapes of the catalog's buildable definitions, for the same reason.</summary>
    public PieceShapes Shapes { get; init; } = new();

    /// <summary>Returns the census row for one native room id.</summary>
    public (RoomState? Room, bool Ok) Room(string id)
    {
        foreach (var room in Rooms)
        {
            if (room.Id == id)
            {
                return (room, true);
            }
        }
        return (null, false);
    }

    /// <summary>The room's cells in the frame cell grid.</summary>
    public IReadOnlyList<Cell> CellsOf(RoomState room) => Cells.TryGetValue(room.Id, out var cells) ? cells : [];
}

public static partial class RoomsRules
{
    /// <summary>
    /// A closed indoor room: a proper room, not a doorway, not outdoors (by temperature or psychologically), off
    /// the map edge and fully roofed.
    /// </summary>
    public static bool Enclosed(this RoomState room) =>
        room.ProperRoom && !room.Doorway && !room.Outdoors && !room.PsychologicallyOutdoors && !room.TouchesMapEdge && room.OpenRoofCount == 0;

    /// <summary>Every cell roofed (native open roof count zero).</summary>
    public static bool Roofed(this RoomState room) => room.OpenRoofCount == 0;

    /// <summary>The definitions standing in the room, as amounts.</summary>
    public static IEnumerable<Amount> ContentAmounts(this RoomState room) =>
        room.Contents.Select(q => new Amount(new Resource(q.DefName), q.Units));

    /// <summary>The ids of the beds in the room.</summary>
    public static IEnumerable<string> BedIds(this RoomState room) => room.Beds.Select(b => b.Id);

    /// <summary>The pawns in the room.</summary>
    public static IEnumerable<PawnId> PawnIds(this RoomState room) => room.Pawns.Select(p => new PawnId(p.Id));

    /// <summary>
    /// A door that opens outdoors toward the enemy side: its outward direction has a positive dot product toward
    /// the killbox centre. With no killbox every outdoor-opening door counts.
    /// </summary>
    public static bool EnemyFacing(this RoomDoor door, IReadOnlyList<Cell> killbox)
    {
        if (!door.Outdoors || killbox.Count == 0)
        {
            return door.Outdoors;
        }
        double cx = 0, cz = 0;
        foreach (var c in killbox)
        {
            cx += c.X;
            cz += c.Z;
        }
        cx /= killbox.Count;
        cz /= killbox.Count;
        double dx = door.Outside.X - door.Cell.X, dz = door.Outside.Z - door.Cell.Z;
        return (dx * (cx - door.Cell.X)) + (dz * (cz - door.Cell.Z)) > 0;
    }

    /// <summary>The cells of the plan's killbox reservation; empty when the plan reserves none.</summary>
    public static List<Cell> KillboxCells(this LayoutPlan p)
    {
        var output = new List<Cell>();
        var (k, ok) = p.Killbox();
        if (ok)
        {
            output.AddRange(k.Footprint.Cells());
        }
        return output;
    }
}
