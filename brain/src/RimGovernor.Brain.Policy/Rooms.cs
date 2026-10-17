namespace RimGovernor.Brain.Policy;

/// <summary>
/// RimWorld's own RoomRoleDef defName, as the native room census reports Room.Role. The game scores every
/// role worker against a room's contents and picks one: a room with beds is a Bedroom or Barracks whatever
/// else it holds, so a role is a fact about the whole room, never something the controller assigns. Unset is
/// Go's empty string.
/// </summary>
public enum RoomRole
{
    [WireName("")]
    Unset = 0,

    [WireName("None")]
    None,

    [WireName("Room")]
    Room,

    [WireName("Bedroom")]
    Bedroom,

    [WireName("PrisonCell")]
    PrisonCell,

    [WireName("DiningRoom")]
    DiningRoom,

    [WireName("RecRoom")]
    RecRoom,

    [WireName("Hospital")]
    Hospital,

    [WireName("Laboratory")]
    Laboratory,

    [WireName("Workshop")]
    Workshop,

    [WireName("Storeroom")]
    Storeroom,

    [WireName("Barracks")]
    Barracks,

    /// <summary>A plan role only: the temporary starter room, scored by the game as whatever its furniture makes it.</summary>
    [WireName("Shelter")]
    Shelter,

    [WireName("PrisonBarracks")]
    PrisonBarracks,

    [WireName("Kitchen")]
    Kitchen,

    [WireName("Tomb")]
    Tomb,

    [WireName("Barn")]
    Barn,

    // Royalty
    [WireName("ThroneRoom")]
    ThroneRoom,

    // Ideology
    [WireName("WorshipRoom")]
    WorshipRoom,

    // Biotech
    [WireName("Nursery")]
    Nursery,

    [WireName("Playroom")]
    Playroom,

    [WireName("Classroom")]
    Classroom,

    [WireName("DeathrestChamber")]
    DeathrestChamber,

    // Anomaly
    [WireName("ContainmentCell")]
    ContainmentCell,

    [WireName("CeremonialChamber")]
    CeremonialChamber,

    /// <summary>A plan role only, no game RoomRoleDef: the game scores the furnished room a bedroom, so it has no facility row.</summary>
    [WireName("IsolationRoom")]
    IsolationRoom,

    /// <summary>A plan role only: RimWorld has no Suite RoomRoleDef, so the native census reports a furnished suite as a Bedroom.</summary>
    [WireName("Suite")]
    Suite,

    /// <summary>A RoomRoleDef this build does not name (another mod's); the observation reads it as this value instead of failing the pass.</summary>
    [WireName("Unrecognized")]
    Unrecognized,
}

/// <summary>
/// One proper indoor room from the same-tick native census. Only a selected building's admitted footprint
/// becomes a reservation; room identity is ephemeral within the current native room graph.
/// </summary>
public sealed record Room
{
    public string Id { get; init; } = "";

    public Fact<RoomRole> Role { get; init; }

    public Fact<bool> Enclosed { get; init; }

    public Fact<double> Temperature { get; init; }

    public EquatableArray<string> Beds { get; init; }

    public Fact<EquatableArray<Amount>> Contents { get; init; }

    public EquatableArray<Cell> Cells { get; init; }

    /// <summary>Every cell roofed (native open roof count zero).</summary>
    public Fact<bool> Roofed { get; init; }

    /// <summary>The doors in the room's boundary.</summary>
    public EquatableArray<RoomDoor> Doors { get; init; }

    public Fact<bool> Burning { get; init; }

    public Fact<bool> TemperatureControl { get; init; }

    public Fact<bool> PerishableContents { get; init; }

    public EquatableArray<PawnId> Pawns { get; init; }
}

/// <summary>
/// One door in a room's boundary: the door cell and the cell across it from the room. Outdoors is whether
/// that far side is outdoors; EnemyFacing is set by MarkEnemyDoors.
/// </summary>
public sealed record RoomDoor
{
    public Cell Cell { get; init; }

    public Cell Outside { get; init; }

    public Fact<bool> Outdoors { get; init; }

    public bool EnemyFacing { get; init; }

    public Fact<bool> PlayerOwned { get; init; }

    public Fact<bool> Open { get; init; }

    public Fact<bool> HoldOpen { get; init; }

    public Fact<bool> BlockedOpen { get; init; }

    public Fact<bool> Forbidden { get; init; }
}

public sealed record RoomObservation
{
    public EquatableArray<Room> Rooms { get; init; }

    public Fact<EquatableArray<string>> EligibleBeds { get; init; }

    /// <summary>The dining and recreation furniture the catalog rows name; it rides with the rooms because the interior plans read it per room.</summary>
    public DiningFurniture Dining { get; init; } = new();

    /// <summary>The plannable shapes of the catalog's buildable definitions, for the same reason.</summary>
    public PieceShapes Shapes { get; init; } = new();

    /// <summary>Returns the census row for one native room id.</summary>
    public (Room Room, bool Ok) Room(string id)
    {
        foreach (var room in Rooms)
        {
            if (room.Id == id)
            {
                return (room, true);
            }
        }
        return (new Room(), false);
    }
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

public static partial class PolicyFunctions
{
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
    /// Flags each door that opens outdoors toward the enemy side: its outward direction has a positive dot
    /// product toward the killbox centre. With no killbox every outdoor-opening door counts.
    /// </summary>
    public static RoomObservation MarkEnemyDoors(RoomObservation rooms, IReadOnlyList<Cell> killbox)
    {
        double cx = 0, cz = 0;
        foreach (var c in killbox)
        {
            cx += c.X;
            cz += c.Z;
        }
        if (killbox.Count > 0)
        {
            cx /= killbox.Count;
            cz /= killbox.Count;
        }
        var output = new List<Room>(rooms.Rooms.Count);
        foreach (var room in rooms.Rooms)
        {
            var doors = new List<RoomDoor>(room.Doors.Count);
            foreach (var door in room.Doors)
            {
                var d = door;
                var enemyFacing = d.Outdoors.TryGetValue(out var outdoors) && outdoors;
                if (enemyFacing && killbox.Count > 0)
                {
                    double dx = d.Outside.X - d.Cell.X, dz = d.Outside.Z - d.Cell.Z;
                    enemyFacing = (dx * (cx - d.Cell.X)) + (dz * (cz - d.Cell.Z)) > 0;
                }
                doors.Add(d with { EnemyFacing = enemyFacing });
            }
            output.Add(room with { Doors = EquatableArray.From(doors) });
        }
        return rooms with { Rooms = EquatableArray.From(output) };
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
            new() { Role = RoomRole.Shelter },
            // A jail is its own planned room: prisoners/recruit shells it while a prisoner is held and keeps a
            // prisoner bed per prisoner.
            new() { Role = RoomRole.PrisonCell },
            new() { Role = RoomRole.PrisonBarracks },
            // A hospital is a hosted medical bed, not a dedicated room: the game scores a room holding any
            // ordinary bed a Bedroom or Barracks, and a bed flagged medical inside it still draws patients,
            // doctors and the room's cleanliness into tending. Doctor coverage is AssignWork's standing
            // requirement; medicine is medical/reserves.
            new() { Role = RoomRole.Hospital, Compatible = Hosts(RoomRole.Bedroom, RoomRole.Barracks) },
            // A laboratory is a hosted research bench: the economy/research Need stages the simple bench in the shelter
            // (scored a Barracks once the bunks stand) when a ladder rung waits on it.
            new() { Role = RoomRole.Laboratory, Compatible = Hosts(RoomRole.Workshop, RoomRole.Barracks) },
            // A workshop shares the shelter: the ladder furnishes the first enclosed room rather than siting a
            // second ring (rounds_sleeping.go), and once the sleeping spots move indoors the game scores that
            // room a Barracks while the bench keeps working.
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
