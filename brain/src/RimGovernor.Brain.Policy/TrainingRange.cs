namespace RimGovernor.Brain.Policy;

// Go: training_range.go. The training range is a template of the mod's own buildings
// (integrations/rimgovernor-native/Defs/ThingDefs/TrainingRange.xml) in open air: one row of stands side by side
// with no gaps and, RangeDistance cells away, one row of dummies directly facing them. The room's Interior is the
// bounding rectangle of the two rows, its Facing the direction from the stands to the dummies (the range layout
// sites it and sizes it by stand count).

/// <summary>One building of the range template.</summary>
public enum RangePieceKind
{
    [WireName("stand")]
    Stand,

    [WireName("dummy")]
    Dummy,
}

/// <summary>One building and the cell it stands on. Column is the zero-based position along the row of a stand or dummy; a stand and the dummy facing it share one.</summary>
public sealed record RangePiece(RangePieceKind Kind, Cell Cell, int Column);

public static partial class PolicyFunctions
{
    /// <summary>The cells from a stand to the dummy facing it. It keeps the shot inside the bow's short-accuracy band.</summary>
    public const int RangeDistance = 8;

    /// <summary>Maps each piece to the mod's building def.</summary>
    public static IReadOnlyDictionary<RangePieceKind, string> RangeDefNames { get; } =
        new Dictionary<RangePieceKind, string>
        {
            [RangePieceKind.Stand] = "RimGovernor_TrainingBowStand",
            [RangePieceKind.Dummy] = "RimGovernor_TrainingDummy",
        };

    /// <summary>The lane bow the training job issues; no recipe makes it.</summary>
    public const string RangeWeaponDef = "Bow_Training";

    /// <summary>
    /// Places the range template inside room's Interior: stands side by side on the row at the stand end and a
    /// dummy directly across from each, RangeDistance cells away at the dummy end (the room's Facing). Pieces come
    /// column by column (stand, dummy). A room whose Facing is not a direction has none.
    /// </summary>
    public static IReadOnlyList<RangePiece> RangeLayout(PlannedRoom room)
    {
        var interior = room.Region.Bounds();
        var n = RangeStandCount(room);
        var pieces = new List<RangePiece>(2 * n);
        for (var i = 0; i < n; i++)
        {
            Cell stand;
            Cell dummy;
            switch (room.Facing)
            {
                case Rotation.North:
                    stand = new Cell(interior.X + i, interior.Z);
                    dummy = new Cell(stand.X, interior.Z + RangeDistance);
                    break;
                case Rotation.South:
                    stand = new Cell(interior.X + i, interior.Z + RangeDistance);
                    dummy = new Cell(stand.X, interior.Z);
                    break;
                case Rotation.East:
                    stand = new Cell(interior.X, interior.Z + i);
                    dummy = new Cell(interior.X + RangeDistance, stand.Z);
                    break;
                case Rotation.West:
                    stand = new Cell(interior.X + RangeDistance, interior.Z + i);
                    dummy = new Cell(interior.X, stand.Z);
                    break;
                default:
                    return [];
            }
            pieces.Add(new RangePiece(RangePieceKind.Stand, stand, i));
            pieces.Add(new RangePiece(RangePieceKind.Dummy, dummy, i));
        }
        return pieces;
    }
}
