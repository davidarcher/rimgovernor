namespace RimGovernor.Brain.Policy;

public sealed partial record PlannedRoom
{
    /// <summary>The rectangle the porch occupies beyond its parent's ring; empty for any room but a porch.</summary>
    public Rectangle PorchGround() => Role != LayoutPorchRules.PlannedPorch ? new Rectangle() : Region.Bounds();

    /// <summary>The porch's battery footprints, nearest the interior's near corner first: 1x2 turned east across a strip along x, facing north along one along z.</summary>
    public List<Rectangle> PorchSlots()
    {
        var output = new List<Rectangle>();
        if (Role != LayoutPorchRules.PlannedPorch)
        {
            return output;
        }
        var s = Region.Bounds();
        if (s.Width > s.Height)
        {
            for (var x = s.X; x + 1 < s.X + s.Width; x += 2)
            {
                output.Add(new Rectangle { X = x, Z = s.Z, Width = 2, Height = 1 });
            }
            return output;
        }
        for (var z = s.Z; z + 1 < s.Z + s.Height; z += 2)
        {
            output.Add(new Rectangle { X = s.X, Z = z, Width = 1, Height = 2 });
        }
        return output;
    }
}

/// <summary>A cold-map shelter's open lean-to child room.</summary>
public static partial class LayoutPorchRules
{
    /// <summary>
    /// A cold-map shelter's open lean-to child room: a one-cell strip on the exterior face of the wall opposite the
    /// parent's door, roofed and holding the first batteries until the battery room stands. It has no walls and no door,
    /// so the heated room never opens onto it (Unfenced). The room's Region is the strip; a strip longer along x holds
    /// 1x2 batteries turned east, one longer along z batteries facing north.
    /// </summary>
    public static PlannedRole PlannedPorch => PlannedRole.Porch;
}

partial record LayoutPlan
{
    /// <summary>The cells to designate in the Build Roof area for every porch in the plan, in plan order.</summary>
    public static List<Cell> PorchRoofCells(LayoutPlan plan)
    {
        var output = new List<Cell>();
        foreach (var r in plan.RoomsOf(LayoutPorchRules.PlannedPorch))
        {
            output.AddRange(LayoutUtilityRules.RectangleCells(r.Region.Bounds()));
        }
        return output;
    }

    /// <summary>The battery sites on the plan's porches, in plan order.</summary>
    internal static List<PlannedPowerSite> PorchSites(LayoutPlan plan)
    {
        var output = new List<PlannedPowerSite>();
        foreach (var r in plan.RoomsOf(LayoutPorchRules.PlannedPorch))
        {
            foreach (var s in r.PorchSlots())
            {
                var rot = s.Height == 2 ? Rotation.North : Rotation.East;
                output.Add(new PlannedPowerSite { Cell = new Cell(s.X, s.Z), Rotation = rot, Area = s });
            }
        }
        return output;
    }
}
