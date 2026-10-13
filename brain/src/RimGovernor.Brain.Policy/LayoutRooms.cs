namespace RimGovernor.Brain.Policy;

public sealed partial record LayoutPlan
{
    /// <summary>The plan's rooms for role as shells, in plan order; a room whose shell is invalid is skipped.</summary>
    public List<RoomFootprint> PlannedShells(RoomRole role)
    {
        var shells = new List<RoomFootprint>();
        if (PolicyFunctions.PlannedRoleFor(role) is not { } want)
        {
            return shells;
        }
        foreach (var r in Rooms)
        {
            if (r.Role != want)
            {
                continue;
            }
            try
            {
                shells.Add(r.Footprint());
            }
            catch (DomainException)
            {
                // a room whose shell is invalid is skipped
            }
        }
        return shells;
    }

    /// <summary>The plan's first open-ground room of role with no ring standing yet: the kitchen, freezer or jail its owning goal shells before furnishing. A dug room is mined out first. Null when none.</summary>
    public PlannedRoom? NextPlannedRoom(PlannedRole role, GroundCensus ground)
    {
        IReadOnlyList<PlannedRoom> rooms = Rooms;
        if (role == PolicyFunctions.PlannedYard)
        {
            // Yards are reservations viewed as rooms, not plan rooms.
            rooms = YardRooms();
        }
        foreach (var r in rooms)
        {
            if (r.Role != role)
            {
                continue;
            }
            if (!this.GroundMatches(r, ground))
            {
                return r;
            }
        }
        return null;
    }

    /// <summary>The rooms attached to room id (an airlock, a porch), in plan order; empty for a room with none.</summary>
    public List<PlannedRoom> ChildrenOf(int id) => [.. Rooms.Where(r => id != 0 && r.Parent == id)];

    internal List<PlannedRoom> RoomsOf(PlannedRole role) => [.. Rooms.Where(r => r.Role == role)];
}

/// <summary>Ports <c>layout_rooms.go</c> and <c>layout_plan_rooms.go</c>: room construction to plan shapes and room queries over a LayoutPlan.</summary>
public static partial class PolicyFunctions
{
    /// <summary>The v2 plan role a builder's room role fills; null for a role the core plans no room for.</summary>
    public static PlannedRole? PlannedRoleFor(RoomRole role)
    {
        if (role == RoomRole.Bedroom)
        {
            return PlannedBedroom;
        }
        if (role == RoomRoleSuite)
        {
            return PlannedSuite;
        }
        if (role == RoomRole.Shelter)
        {
            return PlannedShelter;
        }
        if (role == RoomRole.Kitchen)
        {
            return PlannedKitchen;
        }
        if (role == RoomRole.DiningRoom)
        {
            return PlannedDining;
        }
        if (role == RoomRole.RecRoom)
        {
            return PlannedRec;
        }
        if (role == RoomRole.Hospital)
        {
            return PlannedHospital;
        }
        if (role == RoomRole.PrisonCell || role == RoomRole.PrisonBarracks)
        {
            return PlannedPrison;
        }
        if (role == RoomRole.Workshop)
        {
            return PlannedWorkshop;
        }
        if (role == RoomRole.Storeroom)
        {
            return PlannedStorage;
        }
        if (role == RoomRole.Laboratory)
        {
            return PlannedLab;
        }
        if (role == RoomRole.Tomb)
        {
            return PlannedTomb;
        }
        if (role == RoomRole.ThroneRoom)
        {
            return PlannedThrone;
        }
        if (role == RoomRole.Nursery)
        {
            return PlannedRole.Nursery;
        }
        if (role == RoomRole.Playroom)
        {
            return PlannedRole.Playroom;
        }
        if (role == RoomRole.Classroom)
        {
            return PlannedRole.Classroom;
        }
        if (role == RoomRole.WorshipRoom)
        {
            return PlannedRole.Worship;
        }
        if (role == RoomRole.DeathrestChamber)
        {
            return PlannedRole.DeathrestChamber;
        }
        if (role == RoomRole.ContainmentCell)
        {
            return PlannedRole.ContainmentCell;
        }
        if (role == RoomRole.IsolationRoom)
        {
            return PlannedRole.IsolationRoom;
        }
        return null;
    }

    /// <summary>The ids of the plan's rooms whose ring stands as planned (GroundMatches).</summary>
    public static HashSet<int> BuiltRooms(LayoutPlan plan, GroundCensus ground) => [.. plan.Rooms.Where(r => plan.GroundMatches(r, ground)).Select(r => r.Id)];

    /// <summary>
    /// The ids of the plan's rooms with anything of ours on them: its ring standing as planned, or a cell of occupied (a
    /// building, blueprint, frame, edifice, doorway or journal claim) on its walls or floor. A replan must not move a
    /// fixed room; an untouched planned room is not fixed.
    /// </summary>
    public static HashSet<int> FixedRooms(LayoutPlan plan, GroundCensus ground, IReadOnlySet<Cell> occupied)
    {
        var fixedRooms = BuiltRooms(plan, ground);
        foreach (var r in plan.Rooms)
        {
            if (!fixedRooms.Contains(r.Id) && RectHits(RoomWalls(r), occupied))
            {
                fixedRooms.Add(r.Id);
            }
        }
        return fixedRooms;
    }

    /// <summary>Every room of plan.</summary>
    internal static HashSet<int> AllPins(LayoutPlan plan) => [.. plan.Rooms.Select(r => r.Id)];

    internal static int BedroomCount(LayoutPlan p) => p.Rooms.Count(r => r.Role == PlannedBedroom);

    /// <summary>The walls of the plan's rooms an open journal plan is working on, keyed by the room's interior origin as the plan's method ids are: added to the occupied cells, they make such a room fixed before any building of it is accepted.</summary>
    public static HashSet<Cell> InFlightRoomCells(LayoutPlan plan, IReadOnlySet<Cell> origins)
    {
        var output = new HashSet<Cell>();
        foreach (var r in plan.Rooms)
        {
            if (origins.Contains(new Cell(r.Region.Bounds().X, r.Region.Bounds().Z)))
            {
                output.UnionWith(RectCells(RoomWalls(r)));
            }
        }
        return output;
    }

    /// <summary>The ids of the plan's rooms an open journal plan is working on, keyed by origin as InFlightRoomCells is.</summary>
    public static HashSet<int> InFlightRooms(LayoutPlan plan, IReadOnlySet<Cell> origins) =>
        [.. plan.Rooms.Where(r => origins.Contains(new Cell(r.Region.Bounds().X, r.Region.Bounds().Z))).Select(r => r.Id)];

    /// <summary>How many planned rooms hold one of roles.</summary>
    public static int RoomsOfRoles(LayoutPlan plan, IEnumerable<PlannedRole> roles) => roles.Sum(role => plan.RoomsOf(role).Count);

    internal static List<PlannedRoom> WithoutRooms(IEnumerable<PlannedRoom> rooms, ISet<int> drop) => [.. rooms.Where(r => !drop.Contains(r.Id))];

    /// <summary>
    /// The add-on roles whose need is known to be gone: every pawn's stage (and, for the deathrest chamber, deathrest)
    /// is known and none owes the room; the ideoligion is known and requires no building; the containment demand is
    /// known to be zero entities. needs are the rooms still owed.
    /// </summary>
    public static List<PlannedRole> EndedRoomRoles(Fact<IReadOnlyList<WorkPawn>> pawns, Fact<Ideoligion> ideology, ContainmentPlanning containment, IsolationPlanning isolation, IReadOnlyList<ChildRoomNeed> needs)
    {
        var owed = needs.Select(n => n.Module).ToHashSet();
        var ended = new List<PlannedRole>();
        void Add(bool known, params PlannedRole[] roles)
        {
            foreach (var role in roles)
            {
                if (known && !owed.Contains(role))
                {
                    ended.Add(role);
                }
            }
        }
        var listKnown = pawns.TryGetValue(out var list);
        list ??= [];
        var stages = listKnown;
        var deathrests = listKnown;
        foreach (var p in list)
        {
            if (!p.Biotech.TryGetValue(out var bt))
            {
                stages = false;
                deathrests = false;
                break;
            }
            if (!bt.DevelopmentalStage.TryGetValue(out _))
            {
                stages = false;
            }
            if (!bt.Deathrest.TryGetValue(out _))
            {
                deathrests = false;
            }
        }
        Add(stages && list.Count > 0, PlannedRole.Nursery, PlannedRole.Playroom, PlannedRole.Classroom);
        Add(deathrests && list.Count > 0, PlannedRole.DeathrestChamber);
        Add(ideology.TryGetValue(out _), PlannedRole.Worship);
        var demandKnown = containment.Demand.TryGetValue(out var demand);
        Add(demandKnown && demand!.Entities == 0, PlannedRole.ContainmentCell);
        var isolatedKnown = isolation.Pawns.TryGetValue(out var isolated);
        Add(isolatedKnown && isolated!.Count == 0, PlannedRole.IsolationRoom);
        return ended;
    }

    /// <summary>The ids of the plan's rooms that stand as planned or hold a building of furniture's definitions.</summary>
    public static HashSet<int> RoomsInUse(LayoutPlan plan, IReadOnlyList<CurrentBuilding> built, IReadOnlyList<FurnitureDefinition> furniture)
    {
        var inUse = BuiltRooms(plan, GroundOf(built));
        var names = furniture.Select(d => d.Name).ToList();
        foreach (var r in plan.Rooms)
        {
            if (!inUse.Contains(r.Id) && StandingChildPieces(r, names, built) > 0)
            {
                inUse.Add(r.Id);
            }
        }
        return inUse;
    }

    /// <summary>Removes the rooms with the interiors in drop from plan. Reports whether there was any. A child leaves with its parent: drop gains its id.</summary>
    internal static (LayoutPlan Plan, bool Dropped) DropRooms(LayoutPlan plan, HashSet<int> drop)
    {
        if (drop.Count == 0)
        {
            return (plan, false);
        }
        foreach (var r in plan.Rooms)
        {
            if (r.Parent != 0 && drop.Contains(r.Parent))
            {
                drop.Add(r.Id);
            }
        }

        // A door joins two rooms: one that leaves takes its Links entries with it.
        return (plan with { Rooms = WithoutRooms(plan.Rooms, drop), Links = [.. plan.Links.Where(l => !drop.Contains(l.A) && !drop.Contains(l.B))] }, true);
    }
}
