namespace RimGovernor.Brain.Policy;

// PlannedRole.Airlock (wire name "airlock") is a vestibule child room: one walled cell outside its parent's
// door with a second door beyond it, so the parent never opens straight onto the cold. The room's Region is the
// vestibule cell, its Door the outer door on the far side and DoorRot the way both doors face; the cells to the
// vestibule's left and right are walls. The parent's wall ring owns the walls and the outer door
// (AirlockCells), so the child owes no ring of its own (Unfenced). Nothing here is specific to a shelter: any
// parent door takes one (AttachAirlock).
public static partial class PolicyFunctions
{
    /// <summary>The airlock for a room whose door is door facing rot.</summary>
    internal static PlannedRoom AttachAirlock(Cell door, Rotation rot)
    {
        var v = Outside(door, rot);
        return new PlannedRoom
        {
            Role = PlannedRole.Airlock,
            Region = RectRegion(new Rectangle(v.X, v.Z, 1, 1)),
            Door = Outside(v, rot),
            DoorRot = rot,
        };
    }

    /// <summary>The cell beyond door d, away from the room whose wall side it faces.</summary>
    internal static Cell Outside(Cell d, Rotation side) => side switch
    {
        Rotation.South => new Cell(d.X, d.Z - 1),
        Rotation.North => new Cell(d.X, d.Z + 1),
        Rotation.West => new Cell(d.X - 1, d.Z),
        Rotation.East => new Cell(d.X + 1, d.Z),
        _ => d,
    };

    /// <summary>The airlock's one floor cell.</summary>
    public static Cell Vestibule(this PlannedRoom r)
    {
        var b = r.Region.Bounds();
        return new Cell(b.X, b.Z);
    }

    /// <summary>The two wall cells beside the vestibule; empty for any room but an airlock.</summary>
    public static IReadOnlyList<Cell> AirlockWalls(this PlannedRoom r)
    {
        if (r.Role != PlannedRole.Airlock)
        {
            return [];
        }
        var v = r.Vestibule();
        if (r.DoorRot is Rotation.East or Rotation.West)
        {
            return [new Cell(v.X, v.Z - 1), new Cell(v.X, v.Z + 1)];
        }
        return [new Cell(v.X - 1, v.Z), new Cell(v.X + 1, v.Z)];
    }

    /// <summary>The rectangle the airlock occupies beyond its parent's ring: the vestibule, its two walls and the outer door. Empty for any room but an airlock.</summary>
    public static Rectangle AirlockGround(this PlannedRoom r)
    {
        if (r.Role != PlannedRole.Airlock)
        {
            return default;
        }
        var g = new Rectangle(r.Door.X, r.Door.Z, 1, 1);
        foreach (var c in r.AirlockWalls().Append(r.Vestibule()))
        {
            g = UnionRect(g, new Rectangle(c.X, c.Z, 1, 1));
        }
        return g;
    }

    /// <summary>The cells the airlock adds to its parent's ring: the two walls, then the outer door. Empty for any room but an airlock.</summary>
    internal static IReadOnlyList<Cell> AirlockCells(this PlannedRoom r) =>
        r.Role != PlannedRole.Airlock ? [] : [.. r.AirlockWalls(), r.Door];

    /// <summary>The children of room id with role.</summary>
    internal static IReadOnlyList<PlannedRoom> ChildrenOfRole(this LayoutPlan p, int id, PlannedRole role) =>
        [.. p.ChildrenOf(id).Where(c => c.Role == role)];

    /// <summary>The ring cells r's airlocks add: walls and outer doors.</summary>
    internal static IReadOnlyList<Cell> AirlockCells(this LayoutPlan p, PlannedRoom r) =>
        [.. p.ChildrenOfRole(r.Id, PlannedRole.Airlock).SelectMany(a => a.AirlockCells())];
}
