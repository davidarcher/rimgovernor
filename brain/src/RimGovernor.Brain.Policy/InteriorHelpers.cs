namespace RimGovernor.Brain.Policy;

public static partial class PolicyConstants
{
    /// <summary>The lamp beside a bed or throne. It is no facility (no row links it), so the catalog has no rule that names it.</summary>
    internal const string StandingLampDef = "StandingLamp";
}

public static partial class PolicyFunctions
{
    /// <summary>How many spans with the given gap fit along a length.</summary>
    public static int RowCapacity(int length, int span, int gap)
    {
        if (span < 1 || gap < 0 || length < span)
        {
            return 0;
        }
        return (length + gap) / (span + gap);
    }

    /// <summary>Builds a piece whose footprint's lower corner is at corner.</summary>
    public static InteriorPiece NewInteriorPiece(string slot, string def, Cell size, Rotation rot, Cell corner)
    {
        var (w, h, _) = RotatedSize(size, rot);
        return new InteriorPiece { Slot = slot, Def = def, Size = size, Rot = rot, Rect = new Rectangle(corner.X, corner.Z, w, h) };
    }

    /// <summary>
    /// Reports whether a bench's interaction cell is the open floor in front of it at North (the row below
    /// its footprint), where a bench row leaves its worker row.
    /// </summary>
    internal static bool FrontInteraction(InteriorPieceDef def)
    {
        if (def.Interaction is not { } o)
        {
            return false;
        }
        var r = OccupiedRect(default, def.Size, Rotation.North);
        return o.Z == r.Z - 1 && o.X >= r.X && o.X < r.X + r.Width;
    }

    /// <summary>
    /// The rows of a double-sided room (the battery room, the tomb): a piece on each side of a 1-cell aisle
    /// straight in from the door, one every pitch cells of depth, nearest the door first.
    /// </summary>
    public static IReadOnlyList<int> AisleRows(int depth, int pitch)
    {
        var output = new List<int>();
        for (var v = 0; pitch > 0 && v < depth; v += pitch)
        {
            output.Add(v);
        }
        return output;
    }
}
