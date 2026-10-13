namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>The free roofed walkable cells inside room, never on avoid: what the freezer's catch-all zone may cover.</summary>
    internal static List<Cell> RoomPool(IReadOnlyList<Cell> room, IReadOnlyList<SiteCell> cells, IReadOnlyList<Cell> avoid)
    {
        var inside = new HashSet<Cell>(room);
        var skip = new HashSet<Cell>(avoid);
        var output = new List<Cell>();
        foreach (var c in cells)
        {
            if (!inside.Contains(c.Cell) || skip.Contains(c.Cell))
            {
                continue;
            }
            c.Walkable.TryGetValue(out var walkable);
            c.Roofed.TryGetValue(out var roofed);
            c.StorageEmpty.TryGetValue(out var empty);
            if (walkable && roofed && empty && !c.Occupied())
            {
                output.Add(c.Cell);
            }
        }
        return output;
    }

    /// <summary>The planned butchery's door when it opens through a Link (into the freezer), else false.</summary>
    internal static (Cell Door, bool Ok) ButcheryDoor(LayoutPlan layout)
    {
        foreach (var r in layout.Rooms)
        {
            if (r.Role != PlannedRole.Butchery)
            {
                continue;
            }
            var linkCell = layout.LinkInto(r, PlannedRole.Freezer);
            if (linkCell is { } c)
            {
                return (c, true);
            }
        }
        return (default, false);
    }
}
