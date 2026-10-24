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

    /// <summary>A census of a map with no rooms.</summary>
    public static RoomObservation NoRooms { get; } = new() { Rooms = [], Cells = new Dictionary<string, IReadOnlyList<Cell>>(StringComparer.Ordinal) };

    public Fact<EquatableArray<string>> EligibleBeds { get; init; }

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

/// <summary>
/// What a routine planner needs before it can pursue a room role: which other roles may host the same
/// function without a dedicated room.
/// </summary>
public sealed record FacilityRequirement
{
    public RoomRole Role { get; init; }

    /// <summary>
    /// Roles whose room may host this function as a shared room. A hosting room keeps whatever role the game
    /// scores highest, so a table in a rec room leaves it a RecRoom while still seating diners.
    /// </summary>
    public EquatableArray<RoomRole> Compatible { get; init; }

    /// <summary>Whether a room of the given role can serve this function.</summary>
    public bool Hosts(RoomRole role) => role == Role || Compatible.Contains(role);
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
    /// <summary>
    /// The per-role catalog: every RoomRoleDef the installed game and expansions define, with the roles that
    /// may host its function. Content-gated roles are only pursued when their definitions are present in the
    /// planning census, never assumed from the row alone.
    /// </summary>
    public static List<FacilityRequirement> FacilityCatalog()
    {
        EquatableArray<RoomRole> Hosts(params RoomRole[] first) => [.. first, RoomRole.Room];

        return
        [
            new() { Role = RoomRole.DiningRoom, Compatible = Hosts(RoomRole.RecRoom) },
            new() { Role = RoomRole.RecRoom, Compatible = Hosts(RoomRole.DiningRoom) },
            // A bedroom is a hosted colonist bed: shelter/sleeping stages one in any room that already sleeps
            // colonists or in a generic room, then assigns it; the game scores the room Bedroom or Barracks by count.
            new() { Role = RoomRole.Bedroom, Compatible = Hosts(RoomRole.Barracks) },
            new() { Role = RoomRole.Barracks },
            // A jail is its own planned room: prisoners/recruit shells it while a prisoner is held and keeps a
            // prisoner bed per prisoner.
            new() { Role = RoomRole.PrisonCell },
            new() { Role = RoomRole.PrisonBarracks },
            // A hospital is a hosted medical bed, not a dedicated room: the game scores a room holding any
            // ordinary bed a Bedroom or Barracks, and a bed flagged medical inside it still draws patients,
            // doctors and the room's cleanliness into tending. Doctor coverage is AssignWork's standing
            // requirement; medicine is medical/reserves.
            new() { Role = RoomRole.Hospital, Compatible = Hosts(RoomRole.Bedroom, RoomRole.Barracks) },
            // A laboratory is a hosted research bench: a workshop, or a room the game scores a Barracks, hosts it.
            new() { Role = RoomRole.Laboratory, Compatible = Hosts(RoomRole.Workshop, RoomRole.Barracks) },
            // A workshop's bench keeps working in a room whose beds the game scores a Barracks.
            new() { Role = RoomRole.Workshop, Compatible = Hosts(RoomRole.Barracks) },
            new() { Role = RoomRole.Storeroom },
            // A kitchen is its own planned room: the food/cooking Need shells it at Masonry and above and holds the
            // stove to its interior.
            new() { Role = RoomRole.Kitchen },
            // A tomb is its own planned room: the admin/burial Need shells it and places a sarcophagus while a dead
            // colonist has none waiting.
            new() { Role = RoomRole.Tomb },
            // A throne room is its own planned room: shelter/sleeping shells it sized to the next title's area,
            // places a throne and furnishes it to the title's impressiveness; assigning the throne awaits an
            // action kind.
            new() { Role = RoomRole.ThroneRoom },
            // A worship room is its own planned room, staged like the child rooms: shelter/sleeping shells it
            // and places the buildings the ideoligion requires, which the game's defs name (the ideology
            // section), not this row.
            new() { Role = RoomRole.WorshipRoom },
            // The child rooms are their own planned rooms: shelter/sleeping shells each while a baby (nursery),
            // baby or child (playroom) or child (classroom) lives and places the furniture the game scores the
            // role from (ChildRoomNeeds); a nursery room holds no other bed.
            new() { Role = RoomRole.Nursery },
            new() { Role = RoomRole.Playroom },
            new() { Role = RoomRole.Classroom },
            // A deathrest chamber is its own planned room: shelter/sleeping shells it while a deathrester lives
            // and places a casket per deathrester and the accelerators its capacity allows.
            new() { Role = RoomRole.DeathrestChamber },
            // A containment cell is its own planned room: shelter/sleeping shells it while a capturable entity
            // has no platform able to hold it and places the holding platform the game's defs name, once the
            // predicted containment strength reaches what the entity needs.
            new() { Role = RoomRole.ContainmentCell },
            new() { Role = RoomRole.CeremonialChamber },
        ];
    }

    /// <summary>Looks up one catalog row.</summary>
    public static FacilityRequirement Facility(RoomRole role)
    {
        foreach (var f in FacilityCatalog())
        {
            if (f.Role == role)
            {
                return f;
            }
        }
        throw new DomainException("room role is not catalogued");
    }
}
