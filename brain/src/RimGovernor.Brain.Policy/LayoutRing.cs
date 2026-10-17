namespace RimGovernor.Brain.Policy;

public sealed partial record LayoutPlan
{
    /// <summary>The plan's sparring rings.</summary>
    public List<PlannedRoom> RingRooms() => RoomsOf(LayoutRingRules.PlannedSparringRing);
}

/// <summary>The sparring ring, a clear square beside the training range where colonists spar.</summary>
public static partial class LayoutRingRules
{
    /// <summary>The sparring ring's plan role.</summary>
    public static PlannedRole PlannedSparringRing => PlannedRole.SparringRing;

    public const int RingMinMarkers = 4;
    public const int RingMaxMarkers = 12;

    /// <summary>The ring interior's side in cells.</summary>
    public const int RingSide = 9;

    /// <summary>The mod's marker building.</summary>
    public const string RingMarkerDef = "RimGovernor_SparringMarker";

    /// <summary>The marker count for a colony of capable adults: one each, within RingMinMarkers and RingMaxMarkers.</summary>
    public static int RingMarkersFor(int capable) => Math.Min(Math.Max(capable, RingMinMarkers), RingMaxMarkers);

    /// <summary>
    /// The cell of marker slot n on the inner edge of a ring whose interior is ins. Slots go round the four sides
    /// (south, north, west, east), one per side at a time, each side's next slot two cells further along it; the corners
    /// and the centre stay free.
    /// </summary>
    public static Cell RingSlot(Rectangle ins, int n)
    {
        var along = 2 + (2 * (n / 4));
        return (n % 4) switch
        {
            0 => new Cell(ins.X + along, ins.Z),
            1 => new Cell(ins.X + ins.Width - 1 - along, ins.Z + ins.Height - 1),
            2 => new Cell(ins.X, ins.Z + ins.Height - 1 - along),
            _ => new Cell(ins.X + ins.Width - 1, ins.Z + along),
        };
    }
}

partial record PlannedRoom
{
    /// <summary>The footprint a ring blocks: its interior with a one-cell margin.</summary>
    internal static Rectangle RingBlock(PlannedRoom r) => LayoutPerimeterRules.Pad(r.Region.Bounds(), 1);

    /// <summary>The planned ring whose blocked footprint is block.</summary>
    internal static PlannedRoom RingFromBlock(Rectangle block, int markers)
    {
        var ins = new Rectangle { X = block.X + 1, Z = block.Z + 1, Width = LayoutRingRules.RingSide, Height = LayoutRingRules.RingSide };
        return new PlannedRoom { Role = LayoutRingRules.PlannedSparringRing, Region = RegionRules.RectRegion(ins), Outdoor = true, Markers = markers };
    }

    /// <summary>The furniture template of a planned ring: one marker in each of its first Markers slots.</summary>
    public static List<WantedPiece> RingTemplate(PlannedRoom room)
    {
        var output = new List<WantedPiece>();
        for (var n = 0; n < Math.Min(room.Markers, LayoutRingRules.RingMaxMarkers); n++)
        {
            var c = LayoutRingRules.RingSlot(room.Region.Bounds(), n);
            output.Add(new WantedPiece { DefName = LayoutRingRules.RingMarkerDef, Minimum = c, Maximum = c, Slot = $"marker-{n}", Size = new Cell(1, 1), Rot = Rotation.North });
        }
        return output;
    }
}
