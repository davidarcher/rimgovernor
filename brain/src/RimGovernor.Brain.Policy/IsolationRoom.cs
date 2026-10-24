namespace RimGovernor.Brain.Policy;

// The creepjoiner isolation room: one planned room holding one bed, owed while a creepjoiner whose downside has
// not shown and whose inspection the colony has not finished lives in the colony. It is staged like the
// containment cell: the layout review grows a core room sized to the bed, shelter/sleeping shells it and places
// the bed. The bed is the definition catalog's own (a humanlike sleeping bed the ThingDef mirror's
// bed_humanlike field names, the fewest-slots one the colony would sleep in), never a name here. The room is a
// plan role only: the game scores it a bedroom, which is what its furnishing makes it.
//
// A creepjoiner is held in the room by restricting its allowed area to the bot-owned Isolation area
// (IsolationAreaKey), once the room stands with its bed; the admin/creep-joiners Need releases the restriction when the
// downside shows, when the colony's record says the inspection is done, or while the pawn is hungry (a pawn
// restricted to an area treats food outside it as forbidden, decompile: ForbidUtility.InAllowedArea).
//
// The isolation room's plan role is PlannedRole.IsolationRoom (wire name "isolation-room").
/// <summary>
/// The projection's isolation inputs: the creepjoiners the colony holds apart and the bed definitions the room
/// can be furnished from. Pawns is unknown until the pawn rows are read (and without Anomaly).
/// </summary>
public sealed record IsolationPlanning
{
    public Fact<EquatableArray<PawnId>> Pawns { get; init; }

    public IReadOnlyList<string> Beds { get; init; } = [];
}

public static class IsolationRoomRules
{
    /// <summary>The bot area key of the Isolation allowed area: the plan's isolation room interior.</summary>
    public const string IsolationAreaKey = "Isolation";

    /// <summary>The native label of the Isolation area.</summary>
    public const string IsolationAreaLabel = IsolationAreaKey;

    /// <summary>
    /// The reach of a psychic agony pulse (#3844 D10): CreepJoinerWorker_PsychicAgony.DoResponse pulses every spawned pawn whose
    /// Position.InHorDistOf(creepjoiner, Range) holds, Range a private const 14.9 (ilspycmd). The layout keeps every other
    /// planned room out of it (<see cref="ApartFromIsolation"/>); the credit reads the game's own value through the catalog
    /// and fails when it outgrows this one (<see cref="ForecastIsolation"/>).
    /// </summary>
    public const double AgonyRange = 14.9;

    /// <summary>
    /// Whether two interiors stand out of a pulse's reach of each other (#3844 D10): the nearest two cells, one of each, are
    /// farther than <see cref="AgonyRange"/> apart (InHorDistOf is dx² + dz² ≤ range²), so no pawn inside one is pulsed from
    /// the other.
    /// </summary>
    public static bool ApartFromIsolation(Rectangle a, Rectangle b)
    {
        long dx = Math.Max(0, LayoutEmbedRules.AxisGap(a.X, a.Width, b.X, b.Width) + 1);
        long dz = Math.Max(0, LayoutEmbedRules.AxisGap(a.Z, a.Height, b.Z, b.Height) + 1);
        return dx * dx + dz * dz > AgonyRange * AgonyRange;
    }

    /// <summary>
    /// Whether a room of <paramref name="role"/> and one of <paramref name="other"/> must stand apart (#3844 D10): one is the
    /// isolation room and the other a planned room but a circulation corridor, which pawns only walk (passers-by are named
    /// not modelled by the credit).
    /// </summary>
    public static bool KeptApart(PlannedRole role, PlannedRole other) =>
        (role == PlannedRole.IsolationRoom) != (other == PlannedRole.IsolationRoom)
        && role != PlannedRole.Circulation && other != PlannedRole.Circulation;

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
            Role = PlanRole.IsolationRoom,
            Module = PlannedRole.IsolationRoom,
            Furniture = [new ChildFurniture { Defs = EquatableArray.From(p.Beds), Count = 1 }],
        };
        var ok = need.Resolve(furniture) is not null;
        return ok ? (need, true) : (default!, false);
    }

    /// <summary>
    /// Reports whether the plan's isolation room for need stands with every piece of its furniture: its ring
    /// stands as planned and the bed is built inside it. Only then is a creepjoiner held in it.
    /// </summary>
    public static bool IsolationRoomStanding(LayoutPlan plan, GroundCensus ground, IReadOnlyList<CurrentBuilding> built, ChildRoomNeed need, IReadOnlyList<FurnitureDefinition> defs)
    {
        var shape = need.Shape(defs);
        if (shape is null)
        {
            return false;
        }
        var rok = plan.ChildRoomFor(shape, out var room);
        if (!rok || !plan.GroundMatches(room!, ground))
        {
            return false;
        }
        var pieces = need.Resolve(defs) ?? [];
        foreach (var p in pieces)
        {
            if (ChildRoomsRules.StandingChildPieces(room!, p.Defs, built) < p.Count)
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
            foreach (var c in PlannedRules.RectCells(room.Region.Bounds()))
            {
                set.Add(c);
            }
        }
        return PlannedRules.SortedCells(set);
    }
}
