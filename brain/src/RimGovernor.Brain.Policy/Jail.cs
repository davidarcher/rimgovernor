namespace RimGovernor.Brain.Policy;

// Furnishing the jail. While a prisoner is held, prisoners/recruit keeps one bed set for prisoners in a
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
}

public static class JailRules
{
    /// <summary>Picks the next jail step from the plan, the room census, the held prisoner count, the bed census and the colony's buildings.</summary>
    public static JailStep NextJailStep(LayoutPlan plan, RoomObservation rooms, int held, IReadOnlyList<SleepingBed> beds, IReadOnlyList<CurrentBuilding> built)
    {
        var bedCount = beds.Count(b => b.Prisoners);
        if (held == 0)
        {
            return new JailStep();
        }
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
            if (actKind != JailStepKind.None)
            {
                break;
            }
            // Census: the room's id keys the beds read inside it. A room the census does not show enclosed (a
            // map-edge room is not) stands for nothing: native would force its beds back to colonist.
            var (standing, stands) = PlannedRoom.CensusRoomIn(r, rooms);
            var marks = new List<string>();
            foreach (var b in beds)
            {
                if (!stands || b.Room != standing!.Id || !b.Humanlike || b.Prisoners)
                {
                    continue;
                }
                // Native refuses a crib for prisoners, and a slave bed the plan did not put there stays as it is.
                if (!b.Crib && !b.Slaves)
                {
                    marks.Add(b.Id);
                }
            }
            if (marks.Count > 0)
            {
                marks.Sort(StringComparer.Ordinal);
                (actRoom, actKind, actMarks) = (r, JailStepKind.Mark, marks);
            }
            else if (bedCount < held)
            {
                var furnishing = JailFurnishing(r, rooms.Shapes, built);
                if (furnishing.Absent)
                {
                    (actRoom, actKind, template) = (r, JailStepKind.Reconcile, furnishing.Pieces);
                }
            }
        }
        return new JailStep { Room = actRoom, Kind = actKind, Template = template, Marks = actMarks };
    }

    /// <summary>The beds standing in the room where they stand, then the room's first template bed slot nothing stands on (the shortfall is one fresh slot).</summary>
    internal static Furnishing JailFurnishing(PlannedRoom r, PieceShapes shapes, IReadOnlyList<CurrentBuilding> built)
    {
        var (room, ok) = InteriorRules.InteriorRoomFromLayout(r, shapes);
        if (!ok)
        {
            return new Furnishing();
        }
        var (bed, bok) = room!.Piece(shapes.Furniture.PrimaryBed());
        if (!bok)
        {
            return new Furnishing();
        }
        var things = new List<string>([.. shapes.Furniture.Beds.Select(b => b.Def), bed.Def]);
        var bounds = r.Region.Bounds();
        var standing = built.Count(b => b.Cells.Count > 0 && things.Contains(b.Building!.Definition) && FurnitureRoomsRules.RectInside(bounds, PlacementSearchRules.CellsRectangle(b.Cells.Items)));
        return RoomFurnishingRules.RoomFurnishing(r, room, [new FurnishingWant { Things = things, Count = standing + 1, Piece = bed }], built);
    }
}
