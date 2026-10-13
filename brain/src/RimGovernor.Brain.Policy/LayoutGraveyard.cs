namespace RimGovernor.Brain.Policy;

// The graveyard: a planned Outdoor room in the outskirts cluster. It is unfenced open ground
// (PlannedRoom.Unfenced: no ring, gate, roof or floor; graves need diggable soil, not a constructed floor), and
// it never grows: a full graveyard asks for a further one.
//
// The interior template packs GraveyardGraves plain graves (GraveDefinition, 1x2) in two bands of
// GraveyardColumns columns. Graves stand on even columns, so every grave's long sides face an aisle column (or
// the room's edge on one side), and three aisle rows (the south row, the row between the bands and the top row)
// join the aisle columns.
//
//   z=6  . . . . . . . . . . .   aisle
//   z=5  G . G . G . G . G . G
//   z=4  G . G . G . G . G . G   band 1
//   z=3  . . . . . . . . . . .   aisle
//   z=2  G . G . G . G . G . G
//   z=1  G . G . G . G . G . G   band 0
//   z=0  . . . . . . . . . . .   aisle
public static partial class PolicyConstants
{
    /// <summary>The graves the graveyard is sized for.</summary>
    public const int GraveyardGraves = 12;

    /// <summary>The grave columns in each of the two bands.</summary>
    public const int GraveyardColumns = GraveyardGraves / 2;

    /// <summary>The interior rows (from the south) the two bands start at.</summary>
    internal static readonly int[] GraveBandRows = [1, 4];

    /// <summary>The roles planned as an Outdoor room (PlannedRoom.Outdoor): no ring, no roof, no floor owed.</summary>
    internal static readonly IReadOnlySet<PlannedRole> OutdoorRoles = new HashSet<PlannedRole>
    {
        PlannedRole.Pen,
        PlannedRole.Graveyard,
        PlannedRole.WasteYard,
        PlannedRole.Yard,
        PlannedRole.TrainingRange,
        PlannedRole.SparringRing,
    };
}

public static partial class PolicyFunctions
{
    /// <summary>The grave rectangles (1 wide, 2 deep) of a graveyard whose interior is interior, in plan order; empty when interior is too small for the template.</summary>
    public static IReadOnlyList<Rectangle> GraveyardSlots(Rectangle interior)
    {
        if (interior.Width < (2 * GraveyardColumns) - 1 || interior.Height < GraveBandRows[1] + 3)
        {
            return [];
        }
        var output = new List<Rectangle>();
        foreach (var row in GraveBandRows)
        {
            for (var col = 0; col < GraveyardColumns; col++)
            {
                output.Add(new Rectangle(interior.X + (2 * col), interior.Z + row, 1, 2));
            }
        }
        return output;
    }

    /// <summary>Reports whether a room of role is planned as an Outdoor room.</summary>
    public static bool IsOutdoor(this PlannedRole role) => OutdoorRoles.Contains(role);

    /// <summary>The graveyard's first template slot no building stands on.</summary>
    internal static (InteriorPiece? Piece, bool Ok) GraveSlot(PlannedRoom r, IReadOnlySet<Cell> taken)
    {
        var slots = GraveyardSlots(r.Region.Bounds());
        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (RectCells(slot).Any(taken.Contains))
            {
                continue;
            }
            return (NewInteriorPiece($"grave-{i}", GraveDefinition, new Cell(1, 2), Rotation.North, new Cell(slot.X, slot.Z)), true);
        }
        return (null, false);
    }

    /// <summary>Counts the graves standing in the planned graveyards and the slots those graveyards hold: the capacity the further-graveyard request reads (GraveyardsWanted).</summary>
    public static (int Standing, int Slots) GravesStanding(LayoutPlan plan, IReadOnlyList<CurrentBuilding> built)
    {
        var rooms = plan.RoomsOf(PlannedRole.Graveyard);
        var standing = 0;
        foreach (var b in built)
        {
            if (b.Building!.Definition != GraveDefinition)
            {
                continue;
            }
            foreach (var r in rooms)
            {
                if (b.Cells.Count > 0 && RectContains(r.Region.Bounds(), b.Cells[0]))
                {
                    standing++;
                    break;
                }
            }
        }
        var slots = 0;
        foreach (var r in rooms)
        {
            slots += GraveyardSlots(r.Region.Bounds()).Count;
        }
        return (standing, slots);
    }
}
