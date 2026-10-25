namespace RimGovernor.Brain.BuildingRuntime;

internal static class LayoutProtected
{
    /// <summary>The persisted layout plan's core; null until a plan exists.</summary>
    public static Cell? PlanCore(ColonyReading facts)
    {
        if (facts.Facts.LayoutPlan is { } plan && plan.Core() is { } core)
        {
            return core;
        }
        return facts.Facts.LayoutPlan?.Center();
    }

    /// <summary>
    /// Where pens, barns, fields, tombs and waste start their site search: the middle of the first free
    /// field-zone run of the layout plan, else the plan's centre (no free run); null until a plan exists. The
    /// plan is read at every tech tier: gating it on Masonry stacked the pens, barn and turbines of a Camp
    /// colony on the map centre.
    /// </summary>
    public static Cell? FieldAnchor(ColonyReading facts)
    {
        if (facts.Facts.LayoutPlan is { } plan && plan.FieldAnchor(LayoutFree(facts)) is { } anchor)
        {
            return anchor;
        }
        return facts.Facts.LayoutPlan?.Center();
    }

    /// <summary>The centre of the nearest free planned room of the role to the point <paramref name="to"/> (reserve rooms when none is free), else the plan's centre; null until a plan exists.</summary>
    public static Cell? RoomAnchor(ColonyReading facts, PlannedRole role, Cell to)
    {
        if (facts.Facts.LayoutPlan is { } plan && plan.NearestAnchor(role, to, LayoutFree(facts)) is { } anchor)
        {
            return anchor;
        }
        return facts.Facts.LayoutPlan?.Center();
    }

    /// <summary>Reports whether a rectangle is observed open ground free of player buildings and zones.</summary>
    public static Func<Rectangle, bool> LayoutFree(ColonyReading facts)
    {
        var cells = new Dictionary<Cell, SiteCell>(facts.PlanningWindow.Cells.Count);
        foreach (var c in facts.PlanningWindow.Cells)
        {
            cells[c.Cell] = c;
        }
        var built = new HashSet<Cell>();
        var census = facts.Facts.CurrentConstruction;
        foreach (var b in census.Buildings)
        {
            foreach (var c in b.Cells)
            {
                built.Add(c);
            }
        }
        bool Open(Cell p)
        {
            if (!cells.TryGetValue(p, out var c) || built.Contains(p))
            {
                return false;
            }
            return c.Walkable && !c.Occupied() && !c.Zone;
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
