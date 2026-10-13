namespace RimGovernor.Brain.BuildingRuntime;

internal static class LayoutProtected
{
    /// <summary>The persisted layout plan's core; null until a plan exists.</summary>
    public static Cell? PlanCore(ColonyProjection facts)
    {
        if (facts.LayoutPlan.TryGetValue(out var plan) && plan.Core() is { } core)
        {
            return core;
        }
        return facts.Center().TryGetValue(out var center) ? center : null;
    }

    /// <summary>
    /// Where pens, barns, fields, tombs and waste start their site search: the middle of the first free
    /// field-zone run of the layout plan, else the plan's centre (no free run); null until a plan exists. The
    /// plan is read at every tech tier: gating it on Masonry stacked the pens, barn and turbines of a Camp
    /// colony on the map centre.
    /// </summary>
    public static Cell? FieldAnchor(ColonyProjection facts)
    {
        if (facts.LayoutPlan.TryGetValue(out var plan) && plan.FieldAnchor(LayoutFree(facts)) is { } anchor)
        {
            return anchor;
        }
        return facts.Center().TryGetValue(out var center) ? center : null;
    }

    /// <summary>The centre of the nearest free planned room of the role to the point <paramref name="to"/> (reserve rooms when none is free), else the plan's centre; null until a plan exists.</summary>
    public static Cell? RoomAnchor(ColonyProjection facts, PlannedRole role, Cell to)
    {
        if (facts.LayoutPlan.TryGetValue(out var plan) && plan.NearestAnchor(role, to, LayoutFree(facts)) is { } anchor)
        {
            return anchor;
        }
        return facts.Center().TryGetValue(out var center) ? center : null;
    }

    /// <summary>Reports whether a rectangle is observed open ground free of player buildings and zones.</summary>
    public static Func<Rectangle, bool> LayoutFree(ColonyProjection facts)
    {
        var cells = new Dictionary<Cell, SiteCell>(facts.Cells.Count);
        foreach (var c in facts.Cells)
        {
            cells[c.Cell] = c;
        }
        var built = new HashSet<Cell>();
        if (facts.Facts.CurrentConstruction.TryGetValue(out var census) && census.Colony)
        {
            foreach (var b in census.Buildings)
            {
                foreach (var c in b.Cells)
                {
                    built.Add(c);
                }
            }
        }
        bool Open(Cell p)
        {
            if (!cells.TryGetValue(p, out var c) || built.Contains(p))
            {
                return false;
            }
            return c.Walkable.TryGetValue(out var walkable) && c.Zone.TryGetValue(out var zone) && walkable && !c.Occupied() && !zone;
        }
        return module =>
        {
            for (var x = module.X; x < module.X + module.Width; x++)
            {
                for (var z = module.Z; z < module.Z + module.Height; z++)
                {
                    if (!Open(new Cell(x, z)))
                    {
                        return false;
                    }
                }
            }
            return true;
        };
    }
}
