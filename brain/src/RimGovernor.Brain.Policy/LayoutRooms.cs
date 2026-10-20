namespace RimGovernor.Brain.Policy;

public sealed partial record LayoutPlan
{
    /// <summary>The plan's first open-ground room of role with no ring standing yet: the kitchen, freezer or jail its owning goal shells before furnishing. A dug room is mined out first. Null when none.</summary>
    public PlannedRoom? NextPlannedRoom(PlannedRole role, GroundCensus ground)
    {
        foreach (var r in Rooms)
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

    /// <summary>The rooms attached to room id (an airlock), in plan order; empty for a room with none.</summary>
    public List<PlannedRoom> ChildrenOf(int id) => [.. Rooms.Where(r => id != 0 && r.Parent == id)];

    public List<PlannedRoom> RoomsOf(PlannedRole role) => [.. Rooms.Where(r => r.Role == role)];
}

/// <summary>Room construction to plan shapes and room queries over a LayoutPlan.</summary>
public static partial class LayoutRoomsRules
{
    internal static List<PlannedRoom> WithoutRooms(IEnumerable<PlannedRoom> rooms, ISet<int> drop) => [.. rooms.Where(r => !drop.Contains(r.Id))];

    /// <summary>
    /// The add-on roles whose need is known to be gone: every pawn's stage (and, for the deathrest chamber, deathrest)
    /// is known and none owes the room; the ideoligion is known and requires no building; the containment demand is
    /// known to be zero entities. needs are the rooms still owed.
    /// </summary>
    public static List<PlannedRole> EndedRoomRoles(Fact<EquatableArray<WorkPawn>> pawns, Fact<Ideoligion> ideology, ContainmentPlanning containment, IsolationPlanning isolation, IReadOnlyList<ChildRoomNeed> needs)
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
}

partial record LayoutPlan
{
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
            if (!fixedRooms.Contains(r.Id) && LayoutDeriveRules.RectHits(PlannedRoom.RoomWalls(r), occupied))
            {
                fixedRooms.Add(r.Id);
            }
        }
        return fixedRooms;
    }

    /// <summary>Every room of plan.</summary>
    internal static HashSet<int> AllPins(LayoutPlan plan) => [.. plan.Rooms.Select(r => r.Id)];

    internal static int BedroomCount(LayoutPlan p) => p.Rooms.Count(r => r.Role == LayoutSurveyRules.PlannedBedroom);

    /// <summary>The walls of the plan's rooms an open journal plan is working on, keyed by the room's interior origin as the plan's method ids are: added to the occupied cells, they make such a room fixed before any building of it is accepted.</summary>
    public static HashSet<Cell> InFlightRoomCells(LayoutPlan plan, IReadOnlySet<Cell> origins, IReadOnlySet<int>? ids = null)
    {
        var output = new HashSet<Cell>();
        foreach (var r in plan.Rooms)
        {
            if (origins.Contains(new Cell(r.Region.Bounds().X, r.Region.Bounds().Z)) || (ids?.Contains(r.Id) ?? false))
            {
                output.UnionWith(PlannedRules.RectCells(PlannedRoom.RoomWalls(r)));
            }
        }
        return output;
    }

    /// <summary>The ids of the plan's rooms an open journal plan is working on: those a shell method names by id, and those the origin-keyed methods name by their interior origin.</summary>
    public static HashSet<int> InFlightRooms(LayoutPlan plan, IReadOnlySet<Cell> origins, IReadOnlySet<int>? ids = null) =>
        [.. plan.Rooms.Where(r => origins.Contains(new Cell(r.Region.Bounds().X, r.Region.Bounds().Z)) || (ids?.Contains(r.Id) ?? false)).Select(r => r.Id)];

    /// <summary>How many planned rooms hold one of roles.</summary>
    public static int RoomsOfRoles(LayoutPlan plan, IEnumerable<PlannedRole> roles) => roles.Sum(role => plan.RoomsOf(role).Count);

    /// <summary>The ids of the plan's rooms that stand as planned or hold a building of furniture's definitions.</summary>
    public static HashSet<int> RoomsInUse(LayoutPlan plan, IReadOnlyList<CurrentBuilding> built, IReadOnlyList<FurnitureDefinition> furniture)
    {
        var inUse = BuiltRooms(plan, LayoutGroundRules.GroundOf(built));
        var names = furniture.Select(d => d.Name).ToList();
        foreach (var r in plan.Rooms)
        {
            if (!inUse.Contains(r.Id) && ChildRoomsRules.StandingChildPieces(r, names, built) > 0)
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
        return (plan with { Rooms = LayoutRoomsRules.WithoutRooms(plan.Rooms, drop), Links = [.. plan.Links.Where(l => !drop.Contains(l.A) && !drop.Contains(l.B))] }, true);
    }
}
