namespace RimGovernor.Brain.Policy;

// The creepjoiner isolation room: one planned room holding one bed, owed while a creepjoiner whose downside has
// not shown and whose inspection the colony has not finished lives in the colony. It is staged like the
// containment cell: the layout review grows a core room sized to the bed, MaintainHousing shells it and places
// the bed. The bed is the definition catalog's own (a humanlike sleeping bed the ThingDef mirror's
// bed_humanlike field names, the fewest-slots one the colony would sleep in), never a name here. The room is a
// plan role only: the game scores it a bedroom, which is what its furnishing makes it.
//
// A creepjoiner is held in the room by restricting its allowed area to the bot-owned Isolation area
// (IsolationAreaKey), once the room stands with its bed; ManageCreepJoiners releases the restriction when the
// downside shows, when the colony's record says the inspection is done, or while the pawn is hungry (a pawn
// restricted to an area treats food outside it as forbidden, decompile: ForbidUtility.InAllowedArea).
//
// The isolation room's plan role is PlannedRole.IsolationRoom (wire name "isolation-room").
public static partial class PolicyConstants
{
    /// <summary>The bot area key of the Isolation allowed area: the plan's isolation room interior.</summary>
    public const string IsolationAreaKey = "Isolation";

    /// <summary>The native label of the Isolation area.</summary>
    public const string IsolationAreaLabel = IsolationAreaKey;
}

/// <summary>
/// The projection's isolation inputs: the creepjoiners the colony holds apart and the bed definitions the room
/// can be furnished from. Pawns is unknown until the pawn rows are read (and without Anomaly).
/// </summary>
public sealed record IsolationPlanning
{
    public Fact<IReadOnlyList<PawnId>> Pawns { get; init; }

    public IReadOnlyList<string> Beds { get; init; } = [];
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// The room the isolated creepjoiners owe: one bed in its own room (one bed however many creepjoiners; they
    /// are rare). No isolated creepjoiner, an unread census, or a bed that is not buildable yet or has no known
    /// size owes nothing.
    /// </summary>
    public static (ChildRoomNeed Need, bool Ok) IsolationRoomNeed(IsolationPlanning p, IReadOnlyList<FurnitureDefinition> furniture)
    {
        if (!p.Pawns.TryGetValue(out var pawns) || pawns.Count == 0 || p.Beds.Count == 0)
        {
            return (default!, false);
        }
        var need = new ChildRoomNeed
        {
            Role = RoomRole.IsolationRoom,
            Module = PlannedRole.IsolationRoom,
            Furniture = [new ChildFurniture(p.Beds, 1)],
        };
        var (_, ok) = need.Resolve(furniture);
        return ok ? (need, true) : (default!, false);
    }

    /// <summary>
    /// Reports whether the plan's isolation room for need stands with every piece of its furniture: its ring
    /// stands as planned and the bed is built inside it. Only then is a creepjoiner held in it.
    /// </summary>
    public static bool IsolationRoomStanding(LayoutPlan plan, GroundCensus ground, IReadOnlyList<CurrentBuilding> built, ChildRoomNeed need, IReadOnlyList<FurnitureDefinition> defs)
    {
        var (shape, ok) = need.Shape(defs);
        if (!ok)
        {
            return false;
        }
        var (room, rok) = plan.ChildRoomFor(shape);
        if (!rok || !plan.GroundMatches(room, ground))
        {
            return false;
        }
        var (pieces, _) = need.Resolve(defs);
        foreach (var p in pieces)
        {
            if (StandingChildPieces(room, p.Defs, built) < p.Count)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The interior cells of the plan's isolation rooms, sorted.</summary>
    public static IReadOnlyList<Cell> IsolationRoomCells(this LayoutPlan p)
    {
        var set = new HashSet<Cell>();
        foreach (var room in p.Rooms)
        {
            if (room.Role != PlannedRole.IsolationRoom)
            {
                continue;
            }
            foreach (var c in RectCells(room.Region.Bounds()))
            {
                set.Add(c);
            }
        }
        return SortedCells(set);
    }
}
