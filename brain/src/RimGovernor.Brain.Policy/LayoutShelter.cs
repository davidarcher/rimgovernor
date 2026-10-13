namespace RimGovernor.Brain.Policy;

/// <summary>Ports <c>layout_shelter.go</c> and <c>layout_shelter_retire.go</c>: the temporary starter room and its retirement.</summary>
public static partial class PolicyFunctions
{
    /// <summary>The deepest shelter interior.</summary>
    private const int ShelterMaxDepth = 6;

    /// <summary>The shortest shelter interior side.</summary>
    private const int ShelterMinSide = 3;

    /// <summary>The shallowest shelter interior: the research table (3x2) and the open row in front of it that it is worked from.</summary>
    private const int ShelterMinDepth = 4;

    // Contents, in cells. A sleeping spot is 1x2, the simple research bench 3x2, the crafting spot and a campfire one cell each.
    private const int ShelterBunkCells = 2;
    private const int ShelterCampfireCell = 1;
    private const int ShelterCoolerCell = 1;

    /// <summary>The seasonal minimum below which a map is cold: a pinned constant, not the room latches' ColdEnter.</summary>
    public const double ColdMapBelowC = 0.0;

    /// <summary>How many campfires a cold map's shelter holds indoors.</summary>
    private const int ColdMapCampfires = 2;

    /// <summary>Whether a seasonal outdoor temperature curve (the twelfths' means, C) dips below ColdMapBelowC. An empty curve is not cold.</summary>
    public static bool ColdMapCurve(IEnumerable<double> curve) => curve.Any(t => t < ColdMapBelowC);

    /// <summary>How many campfires the shelter holds indoors: two on a cold map, none elsewhere, where the cooking campfire stands outside.</summary>
    public static int ShelterCampfires(bool cold) => cold ? ColdMapCampfires : 0;

    /// <summary>Whether a seasonal outdoor temperature curve peaks above the temperature planner's HotEnter. An empty curve is not hot.</summary>
    public static bool HotMapCurve(IEnumerable<double> curve)
    {
        var hot = DefaultRoundsPolicy().HotEnter;
        return curve.Any(t => t > hot);
    }

    /// <summary>How many passive coolers the shelter template holds on its floor: one on a hot map, none elsewhere.</summary>
    public static int ShelterCoolers(bool hot) => hot ? 1 : 0;

    /// <summary>The interior cells a shelter for colonists needs: its contents plus half again for the aisle and the door's approach. The research bench is counted at its 3x2 footprint.</summary>
    public static int ShelterInteriorArea(int colonists, int campfires, int coolers)
    {
        var contents = (ShelterBunkCells * Math.Max(colonists, 1)) + (ShelterCampfireCell * Math.Max(campfires, 0)) + (ShelterCoolerCell * Math.Max(coolers, 0));
        foreach (var c in ShelterCapabilities)
        {
            contents += CapabilityCells[c];
        }
        return ((contents * 3) + 1) / 2;
    }

    /// <summary>The shelter interiors (width, depth) holding at least ShelterInteriorArea cells, best first: the fewest cells, then the aspect nearest two thirds.</summary>
    public static List<(int W, int D)> ShelterSizes(int colonists, int campfires, int coolers)
    {
        var area = ShelterInteriorArea(colonists, campfires, coolers);
        var output = new List<(int W, int D)>();
        for (var d = ShelterMinDepth; d <= ShelterMaxDepth; d++)
        {
            var w = Math.Max((area + d - 1) / d, ShelterMinSide);
            if (w >= d)
            {
                output.Add((w, d));
            }
        }
        return [.. output.OrderBy(s => s.W * s.D).ThenBy(s => Abs32((2 * s.W) - (3 * s.D)))];
    }

    /// <summary>The room on interior ins with its door at the middle of the wall facing seed.</summary>
    internal static PlannedRoom ShelterRoom(Rectangle ins, Cell seed)
    {
        int cx = ins.X + (ins.Width / 2), cz = ins.Z + (ins.Height / 2);
        int dx = seed.X - cx, dz = seed.Z - cz;
        var r = new PlannedRoom { Role = PlannedShelter, Region = RectRegion(ins) };
        if (Abs32(dx) > Abs32(dz) && dx > 0)
        {
            return r with { Door = new Cell(ins.X + ins.Width, cz), DoorRot = Rotation.East };
        }
        if (Abs32(dx) > Abs32(dz))
        {
            return r with { Door = new Cell(ins.X - 1, cz), DoorRot = Rotation.West };
        }
        if (dz > 0)
        {
            return r with { Door = new Cell(cx, ins.Z + ins.Height), DoorRot = Rotation.North };
        }
        return r with { Door = new Cell(cx, ins.Z - 1), DoorRot = Rotation.South };
    }

    /// <summary>The shelter room followed by its children: an airlock outside its door when airlock is set and a battery porch on the far wall when porch is set (a cold map). The children carry no Parent yet; WithRoomIDs hands it out with their ids.</summary>
    internal static List<PlannedRoom> ShelterWithChildren(Rectangle ins, Cell seed, bool airlock, bool porch)
    {
        var r = ShelterRoom(ins, seed);
        var output = new List<PlannedRoom> { r };
        if (airlock)
        {
            output.Add(AttachAirlock(r.Door, r.DoorRot!.Value));
        }
        if (porch)
        {
            output.Add(AttachPorch(ins, r.DoorRot!.Value));
        }
        return output;
    }

    /// <summary>
    /// Whether every capability the plan's shelter hosts has a better home. Unknown or partial facts never retire it,
    /// and a colony with nothing else built keeps the shelter as its permanent home.
    /// </summary>
    public static bool ShelterRetirable(LayoutPlan plan, RoomObservation rooms, SleepingObservation sleeping, IReadOnlyList<CurrentBuilding> built)
    {
        var shelters = plan.RoomsOf(PlannedShelter);
        if (shelters.Count == 0 || sleeping.People.Count == 0 || sleeping.People.Count != sleeping.Colonists)
        {
            return false;
        }
        var inShelter = new HashSet<string>(StringComparer.Ordinal);
        var shelterRooms = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in shelters)
        {
            // Census: the shelter's beds are read off its room.
            var (room, ok) = CensusRoomIn(s, rooms);
            if (ok && room is not null)
            {
                shelterRooms.Add(room.Id);
                foreach (var b in room.Beds)
                {
                    inShelter.Add(b);
                }
            }
        }
        var bedroomOf = new Dictionary<string, Room>(StringComparer.Ordinal);
        foreach (var room in rooms.Rooms)
        {
            if (room.Role.TryGetValue(out var role) && role == RoomRole.Bedroom)
            {
                foreach (var b in room.Beds)
                {
                    if (!inShelter.Contains(b))
                    {
                        bedroomOf[b] = room;
                    }
                }
            }
        }
        foreach (var c in ShelterCapabilities)
        {
            if (!CapabilityHoused(c, plan, rooms, sleeping, built, shelterRooms, bedroomOf))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Whether a shelter-hosted capability has a standing home elsewhere. Cooking has no tested home yet, so it never holds the shelter.</summary>
    private static bool CapabilityHoused(Capability c, LayoutPlan plan, RoomObservation rooms, SleepingObservation sleeping, IReadOnlyList<CurrentBuilding> built, HashSet<string> shelterRoom, Dictionary<string, Room> bedroomOf)
    {
        if (c == Capability.Sleep)
        {
            foreach (var p in sleeping.People)
            {
                if (!p.OwnedBed.TryGetValue(out var bed) || !bedroomOf.TryGetValue(bed, out var r) || r.Id == "")
                {
                    return false;
                }
            }
        }
        else if (c == Capability.Heat)
        {
            if (!plan.Cold)
            {
                return true;
            }
            foreach (var p in sleeping.People)
            {
                p.OwnedBed.TryGetValue(out var bed);
                if (bed is null || !bedroomOf.TryGetValue(bed, out var r) || !Positive(r.TemperatureControl))
                {
                    return false;
                }
            }
        }
        else if (c == Capability.Craft)
        {
            return RoleStands(plan, PlannedWorkshop, rooms);
        }
        else if (c == Capability.Research)
        {
            return RoleStands(plan, PlannedLab, rooms) || BenchOutside(built, rooms, shelterRoom);
        }
        return true;
    }

    /// <summary>Whether there is a planned room of the role, every one standing as a roofed census room.</summary>
    private static bool RoleStands(LayoutPlan plan, PlannedRole role, RoomObservation rooms)
    {
        var planned = plan.RoomsOf(role);
        foreach (var r in planned)
        {
            if (!CensusRoomIn(r, rooms).Item2)
            {
                return false;
            }
        }
        return planned.Count > 0;
    }

    /// <summary>Whether a built research bench stands in an enclosed census room other than the shelter's.</summary>
    private static bool BenchOutside(IReadOnlyList<CurrentBuilding> built, RoomObservation rooms, HashSet<string> shelterRoom)
    {
        foreach (var b in built)
        {
            if (b.Building is null || !b.Building.Definition.Contains("ResearchBench", StringComparison.Ordinal) || b.Cells.Count == 0)
            {
                continue;
            }
            foreach (var room in rooms.Rooms)
            {
                if (!room.Enclosed.TryGetValue(out var enclosed) || !enclosed || shelterRoom.Contains(room.Id))
                {
                    continue;
                }
                if (room.Cells.Contains(b.Cells[0]))
                {
                    return true;
                }
            }
        }
        return false;
    }
}
