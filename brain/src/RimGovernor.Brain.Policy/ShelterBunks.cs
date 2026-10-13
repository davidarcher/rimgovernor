namespace RimGovernor.Brain.Policy;

// The sleeping layout of a starter shell before its ring goes up: beds are the first construction on the
// site and the sleeping spots the interim, so the ring is raised around colonists who already have somewhere
// to lie down. The packing is the shelter interior template's (interior_shelter.go): one bunk slot per
// colonist, shared by the spot rung and the bed rung (a bed replaces the spot on the same cells), each a 1x2
// footprint at the slot's rotation, off the cell inside the door, the starter storage patch and the cells the
// plan digs. Fewer bunks than asked for is not an error: the shell is small, and whatever fits is placed.

public static partial class PolicyFunctions
{
    /// <summary>The footprint a bunk anchored at anchor occupies at a rotation.</summary>
    public static Rectangle BunkRect(Cell anchor, Rotation rot) => OccupiedRect(anchor, ShelterBunkSize, rot);

    /// <summary>The two cells of BunkRect.</summary>
    public static IReadOnlyList<Cell> BunkCells(Cell anchor, Rotation rot) => RectCells(BunkRect(anchor, rot));

    /// <summary>
    /// Reads a planned shelter room as the shelter template's input: its interior, the door, and the cells
    /// the template keeps clear (the starter storage patch, the cells plan dig mines and reserved). False when
    /// the interior is not a rectangle the door opens onto.
    /// </summary>
    public static (InteriorRoom Room, bool Ok) ShelterRoom(
        PlannedRoom room,
        IReadOnlyList<Cell> mined,
        PieceShapes shapes,
        int occupants,
        int campfires,
        int coolers,
        IReadOnlyList<Cell> reserved)
    {
        RoomFootprint shell;
        try
        {
            shell = room.Footprint();
        }
        catch (DomainException)
        {
            return (new InteriorRoom(), false);
        }
        var bounds = room.Region.Bounds();
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return (new InteriorRoom(), false);
        }
        var interior = new InteriorRoom
        {
            Role = RoomRole.Shelter,
            Interior = bounds,
            Doors = EquatableArray.From(new[] { room.Door }),
            Shapes = shapes,
            Occupants = occupants,
            Campfires = campfires,
            Coolers = coolers,
            Capabilities = CapabilitiesOf(PlannedRole.Shelter),
        };
        var (_, ok) = DoorSide(interior.Interior, room.Door);
        if (!ok)
        {
            return (new InteriorRoom(), false);
        }
        var keep = new List<Cell>(RectCells(StarterStorage(shell)));
        keep.AddRange(mined);
        keep.AddRange(reserved);
        return (interior with { Reserved = EquatableArray.From(keep) }, true);
    }

    /// <summary>
    /// The bunk slots of the room's interior for up to occupants sleepers (0 fills every bunk that fits), in
    /// world cells, best first; mined are the cells plan dig still mines and reserved further cells to keep clear.
    /// </summary>
    public static List<InteriorPiece> PlanShelterBunks(
        PlannedRoom room,
        IReadOnlyList<Cell> mined,
        PieceShapes shapes,
        int occupants,
        int campfires,
        int coolers,
        IReadOnlyList<Cell> reserved)
    {
        var (interior, ok) = ShelterRoom(room, mined, shapes, occupants, campfires, coolers, reserved);
        if (!ok)
        {
            return [];
        }
        var (plan, planned) = PlanRoomInterior(interior, new InteriorPieceDef());
        if (!planned)
        {
            return [];
        }
        return plan.Pieces.Where(p => p.IsBunk()).ToList();
    }
}
