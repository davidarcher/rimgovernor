namespace RimGovernor.Brain.Policy;

// PlannedRole.Airlock (wire name "airlock") is a vestibule child room: one walled cell outside its parent's
// door with a second door beyond it, so the parent never opens straight onto the cold. The room's Region is the
// vestibule cell, its Door the outer door on the far side and DoorSide the way both doors face; the cells to the
// vestibule's left and right are walls. The parent's wall ring owns the walls and the outer door
// (AirlockCells), so the child owes no ring of its own (Unfenced). Any parent door takes one (AttachAirlock,
// where the tiler prices one, LayoutTileAirlock).
public static partial class LayoutAirlockRules
{
    /// <summary>The cell beyond door d, away from the room whose wall side it faces.</summary>
    internal static Cell Outside(Cell d, Rotation side)
    {
        switch (side)
        {
            case Rotation.South:
                return new Cell(d.X, d.Z - 1);
            case Rotation.North:
                return new Cell(d.X, d.Z + 1);
            case Rotation.West:
                return new Cell(d.X - 1, d.Z);
            case Rotation.East:
                return new Cell(d.X + 1, d.Z);
        }
        throw new DomainException("airlock: unnamed wall side " + (int)side);
    }
}

partial record PlannedRoom
{
    /// <summary>The airlock for a room whose door is door facing rot.</summary>
    internal static PlannedRoom AttachAirlock(Cell door, Rotation rot)
    {
        var v = LayoutAirlockRules.Outside(door, rot);
        return new PlannedRoom
        {
            Role = PlannedRole.Airlock,
            Region = RegionRules.RectRegion(new Rectangle(v.X, v.Z, 1, 1)),
            Door = LayoutAirlockRules.Outside(v, rot),
            DoorSide = new DoorSide.Doored(rot),
        };
    }

    /// <summary>The airlock's one floor cell.</summary>
    public Cell Vestibule()
    {
        var b = this.Region.Bounds();
        return new Cell(b.X, b.Z);
    }

    /// <summary>The two wall cells beside the vestibule; empty for any room but an airlock.</summary>
    public IReadOnlyList<Cell> AirlockWalls()
    {
        if (this.Role != PlannedRole.Airlock)
        {
            return [];
        }
        var v = this.Vestibule();
        if (this.DoorSide is DoorSide.Doored { Side: Rotation.East or Rotation.West })
        {
            return [new Cell(v.X, v.Z - 1), new Cell(v.X, v.Z + 1)];
        }
        return [new Cell(v.X - 1, v.Z), new Cell(v.X + 1, v.Z)];
    }

    /// <summary>The rectangle the airlock occupies beyond its parent's ring: the vestibule, its two walls and the outer door. Empty for any room but an airlock.</summary>
    public Rectangle AirlockGround()
    {
        if (this.Role != PlannedRole.Airlock)
        {
            return default;
        }
        var g = new Rectangle(this.Door.X, this.Door.Z, 1, 1);
        foreach (var c in this.AirlockWalls().Append(this.Vestibule()))
        {
            g = LayoutDefenseRules.UnionRect(g, new Rectangle(c.X, c.Z, 1, 1));
        }
        return g;
    }

    /// <summary>The cells the airlock adds to its parent's ring: the two walls, then the outer door. Empty for any room but an airlock.</summary>
    internal IReadOnlyList<Cell> AirlockCells() =>
        this.Role != PlannedRole.Airlock ? [] : [.. this.AirlockWalls(), this.Door];
}

partial record LayoutPlan
{
    /// <summary>The children of room id with role.</summary>
    internal IReadOnlyList<PlannedRoom> ChildrenOfRole(int id, PlannedRole role) =>
        [.. this.ChildrenOf(id).Where(c => c.Role == role)];

    /// <summary>The ring cells r's airlocks add: walls and outer doors.</summary>
    internal IReadOnlyList<Cell> AirlockCells(PlannedRoom r) =>
        [.. this.ChildrenOfRole(r.Id, PlannedRole.Airlock).SelectMany(a => a.AirlockCells())];
}
