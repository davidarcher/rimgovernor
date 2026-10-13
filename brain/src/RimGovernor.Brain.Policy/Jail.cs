namespace RimGovernor.Brain.Policy;

// Furnishing the jail. While a prisoner is held, MaintainPopulation keeps one bed set for prisoners in a
// planned jail per held prisoner: it sets every unflagged bed standing in a jail for prisoners at once (owner
// type is room-level in the game, so a jail is never left mixed), reports the crib and slave beds as strays
// (native refuses a crib; a slave bed is not the jail's to flip), and otherwise reconciles the room
// (ReconcileRoom) to the next free template bed: the ring, the floor and the bed, installed from packed stock
// first. A fresh bed is placed plain and flagged on a later step once it stands. Wardens bring the prisoners to
// the beds; nothing here hauls.

/// <summary>The next jail step.</summary>
public enum JailStepKind
{
    /// <summary>No prisoner held, enough prisoner beds, no planned jail with room, or a fact is unknown.</summary>
    [WireName("")]
    None = 0,

    /// <summary>Reconcile Room to Template, its next free bed (ReconcileRoom): whatever the ring, floor and bed still owe.</summary>
    Reconcile,

    /// <summary>Set Marks, every markable bed standing in a planned jail, for prisoners.</summary>
    Mark,
}

/// <summary>One bounded step towards a prisoner bed per prisoner.</summary>
public sealed record JailStep
{
    public JailStepKind Kind { get; init; }

    public PlannedRoom? Room { get; init; }

    /// <summary>A JailReconcile's wanted furniture: the next free bed.</summary>
    public IReadOnlyList<WantedPiece> Template { get; init; } = [];

    /// <summary>
    /// A JailMark's beds. Bed owner type is room-level in the game (SetBedOwnerTypeByInterface flips the whole
    /// room), so every markable bed of the jail goes in one pass rather than one bed per pass.
    /// </summary>
    public IReadOnlyList<string> Marks { get; init; } = [];

    /// <summary>
    /// Humanlike beds standing in a planned jail that native refuses for prisoners (crib) or that are slave beds
    /// the plan did not put there, and so stay as they are: to remove or repurpose, never to mark. Reported on
    /// every step.
    /// </summary>
    public IReadOnlyList<string> Strays { get; init; } = [];

    /// <summary>The living prisoners.</summary>
    public int Held { get; init; }

    /// <summary>The beds set for prisoners.</summary>
    public int Beds { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>Picks the next jail step from the plan, the room census, the held prisoner count, the bed census and the colony's buildings.</summary>
    public static JailStep NextJailStep(LayoutPlan plan, RoomObservation rooms, int held, IReadOnlyList<SleepingBed> beds, IReadOnlyList<CurrentBuilding> built)
    {
        var bedCount = beds.Count(b => b.Prisoners.ValueOrDefault);
        if (held == 0)
        {
            return new JailStep();
        }
        var taken = new HashSet<Cell>();
        foreach (var b in built)
        {
            foreach (var c in b.Cells)
            {
                taken.Add(c);
            }
        }
        var strays = new List<string>();
        var actKind = JailStepKind.None;
        PlannedRoom? actRoom = null;
        IReadOnlyList<WantedPiece> template = [];
        IReadOnlyList<string> actMarks = [];
        foreach (var r in plan.Rooms)
        {
            if (r.Role != PlannedRole.Prison)
            {
                continue;
            }
            // Census: the room's id keys the beds read inside it. A room the census does not show enclosed (a
            // map-edge room is not) stands for nothing: native would force its beds back to colonist.
            var (standing, stands) = CensusRoomIn(r, rooms);
            var marks = new List<string>();
            foreach (var b in beds)
            {
                var humanlike = b.Humanlike.ValueOrDefault;
                var mk = b.Medical.IsKnown;
                var pk = b.Prisoners.TryGetValue(out var prisoners);
                if (!stands || b.Room.ValueOrDefault != standing!.Id || !humanlike || !mk || !pk || prisoners)
                {
                    continue;
                }
                if (b.Crib || b.Slaves)
                {
                    strays.Add(b.Id);
                }
                else
                {
                    marks.Add(b.Id);
                }
            }
            if (actKind != JailStepKind.None)
            {
                continue;
            }
            if (marks.Count > 0)
            {
                marks.Sort(StringComparer.Ordinal);
                (actRoom, actKind, actMarks) = (r, JailStepKind.Mark, marks);
            }
            else if (bedCount < held)
            {
                var (piece, ok) = JailSlot(r, rooms.Shapes, taken);
                if (ok)
                {
                    (actRoom, actKind, template) = (r, JailStepKind.Reconcile, [piece!.Wanted()]);
                }
            }
        }
        strays.Sort(StringComparer.Ordinal);
        return new JailStep { Held = held, Beds = bedCount, Strays = strays, Room = actRoom, Kind = actKind, Template = template, Marks = actMarks };
    }

    /// <summary>The room's first template bed slot nothing stands on.</summary>
    private static (InteriorPiece? Piece, bool Ok) JailSlot(PlannedRoom r, PieceShapes shapes, HashSet<Cell> taken)
    {
        var (room, ok) = InteriorRoomFromLayout(r, shapes);
        if (!ok)
        {
            return (null, false);
        }
        var (bed, bok) = room!.Piece(shapes.Furniture.PrimaryBed());
        if (!bok)
        {
            return (null, false);
        }
        var (interior, pok) = PlanRoomInterior(room, bed);
        if (!pok)
        {
            return (null, false);
        }
        foreach (var p in interior.Pieces)
        {
            if (!RectCells(p.Rect).Any(taken.Contains))
            {
                return (p, true);
            }
        }
        return (null, false);
    }
}
