namespace RimGovernor.Brain.Policy;

public sealed partial record PlannedRoom
{
    /// <summary>
    /// Whether the room has no ring: a training range, sparring ring or yard stands in open air with nothing
    /// built round it, so it owes no wall, fence, gate, roof or floor and has no Door. Only the animal pen keeps a fence.
    /// </summary>
    public bool Unfenced() =>
        Role == LayoutRangeRules.PlannedTrainingRange
        || Role == LayoutRingRules.PlannedSparringRing
        || Role == LayoutYardRules.PlannedYard
        || Role == PlannedRole.Airlock;
}

public sealed partial record LayoutPlan
{
    /// <summary>The plan's training ranges.</summary>
    public List<PlannedRoom> RangeRooms() => RoomsOf(LayoutRangeRules.PlannedTrainingRange);
}

/// <summary>The training range, a planned Outdoor room with no ring at all.</summary>
public static partial class LayoutRangeRules
{
    /// <summary>The roles planned as an Outdoor room (PlannedRoom.Outdoor): no ring, no roof, no floor owed.</summary>
    internal static readonly IReadOnlySet<PlannedRole> OutdoorRoles = new HashSet<PlannedRole>
    {
        PlannedRole.Pen,
        PlannedRole.Yard,
        PlannedRole.TrainingRange,
        PlannedRole.SparringRing,
    };

    /// <summary>Reports whether a room of role is planned as an Outdoor room.</summary>
    public static bool IsOutdoor(this PlannedRole role) => OutdoorRoles.Contains(role);

    /// <summary>The training range's plan role.</summary>
    public static PlannedRole PlannedTrainingRange => PlannedRole.TrainingRange;

    public const int RangeMinStands = 2;
    public const int RangeMaxStands = 8;

    /// <summary>The stand count for a colony of capable adults: one per two, within RangeMinStands and RangeMaxStands.</summary>
    public static int RangeStandsFor(int capable) => Math.Min(Math.Max((capable + 1) / 2, RangeMinStands), RangeMaxStands);

    /// <summary>The footprint of a range of stands stands in the given orientation.</summary>
    internal static (int W, int H) RangeBlockSize(int stands, bool horizontal)
    {
        var (w, h) = (stands + 2, TrainingRules.RangeDistance + 1 + 2);
        return horizontal ? (w, h) : (h, w);
    }

    /// <summary>
    /// The direction the dummies point for a site on side of the core extent ext, rows running along X when horizontal:
    /// the side itself when it lies along the dummy axis, else the end of that axis farther from the core.
    /// </summary>
    internal static Rotation RangeFacing(Rotation side, bool horizontal, Rectangle block, Rectangle ext)
    {
        if (horizontal)
        {
            if (side == Rotation.North || side == Rotation.South)
            {
                return side;
            }
            return block.Z + (block.Height / 2) >= ext.Z + (ext.Height / 2) ? Rotation.North : Rotation.South;
        }
        if (side == Rotation.East || side == Rotation.West)
        {
            return side;
        }
        return block.X + (block.Width / 2) >= ext.X + (ext.Width / 2) ? Rotation.East : Rotation.West;
    }
}

partial record PlannedRoom
{
    /// <summary>The stands a planned range holds: the length of its rows.</summary>
    public static int RangeStandCount(PlannedRoom r) =>
        r.Facing is Rotation.North or Rotation.South ? r.Region.Bounds().Width
        : r.Facing is Rotation.East or Rotation.West ? r.Region.Bounds().Height
        : 0; // a room without a facing is no range

    /// <summary>The footprint an unfenced range blocks: its interior with a one-cell margin. A stray practice shot cannot hit a pawn, so nothing behind the dummies is kept free.</summary>
    internal static Rectangle RangeBlock(PlannedRoom r) => LayoutPerimeterRules.Pad(r.Region.Bounds(), 1);

    /// <summary>The planned range whose blocked footprint is block.</summary>
    internal static PlannedRoom RangeFromBlock(Rectangle block, Rotation facing, int stands)
    {
        var ins = new Rectangle { X = block.X + 1, Z = block.Z + 1, Width = stands, Height = TrainingRules.RangeDistance + 1 };
        if (facing == Rotation.East || facing == Rotation.West)
        {
            ins = ins with { Width = TrainingRules.RangeDistance + 1, Height = stands };
        }
        return new PlannedRoom { Role = LayoutRangeRules.PlannedTrainingRange, Region = RegionRules.RectRegion(ins), Outdoor = true, Facing = facing };
    }

    /// <summary>The furniture template of a planned range: RangeLayout, one WantedPiece per building.</summary>
    public static List<WantedPiece> RangeTemplate(PlannedRoom room)
    {
        var layout = TrainingRules.RangeLayout(room);
        var output = new List<WantedPiece>(layout.Count);
        foreach (var p in layout)
        {
            output.Add(new WantedPiece { DefName = TrainingRules.RangeDefNames[p.Kind], Minimum = p.Cell, Maximum = p.Cell, Size = new Cell(1, 1), Rot = Rotation.North });
        }
        return output;
    }
}
