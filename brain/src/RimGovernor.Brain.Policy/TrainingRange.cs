namespace RimGovernor.Brain.Policy;

// The training range is a template of the mod's own buildings
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

/// <summary>One building and the cell it stands on.</summary>
public sealed record RangePiece(RangePieceKind Kind, Cell Cell);

public static partial class TrainingRules
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

    /// <summary>
    /// Places the range template inside room's Interior: stands side by side on the row at the stand end and a
    /// dummy directly across from each, RangeDistance cells away at the dummy end (the room's Facing). Pieces come
    /// column by column (stand, dummy). An Unturned room has none.
    /// </summary>
    public static IReadOnlyList<RangePiece> RangeLayout(PlannedRoom room)
    {
        if (room.Facing is not Heading.Toward { Side: var side })
        {
            return [];
        }
        var interior = room.Region.Bounds();
        var n = PlannedRoom.RangeStandCount(room);
        var pieces = new List<RangePiece>(2 * n);
        for (var i = 0; i < n; i++)
        {
            var (stand, dummy) = side == Rotation.North ? (new Cell(interior.X + i, interior.Z), new Cell(interior.X + i, interior.Z + RangeDistance))
                : side == Rotation.South ? (new Cell(interior.X + i, interior.Z + RangeDistance), new Cell(interior.X + i, interior.Z))
                : side == Rotation.East ? (new Cell(interior.X, interior.Z + i), new Cell(interior.X + RangeDistance, interior.Z + i))
                : (new Cell(interior.X + RangeDistance, interior.Z + i), new Cell(interior.X, interior.Z + i));
            pieces.Add(new RangePiece(RangePieceKind.Stand, stand));
            pieces.Add(new RangePiece(RangePieceKind.Dummy, dummy));
        }
        return pieces;
    }
}
