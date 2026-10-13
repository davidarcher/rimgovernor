namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>Reports c in the ring of walls round r, corners excluded.</summary>
    public static bool InWall(Rectangle r, Cell c)
    {
        var onX = c.X == r.X - 1 || c.X == r.X + r.Width;
        var onZ = c.Z == r.Z - 1 || c.Z == r.Z + r.Height;
        return (onX && c.Z >= r.Z && c.Z < r.Z + r.Height) || (onZ && c.X >= r.X && c.X < r.X + r.Width);
    }

    internal static Cell TransposeCell(Cell c) => new(c.Z, c.X);

    internal static Rotation TransposeRot(Rotation rot) => rot switch
    {
        Rotation.North => Rotation.East,
        Rotation.East => Rotation.North,
        Rotation.South => Rotation.West,
        Rotation.West => Rotation.South,
        _ => rot,
    };

    /// <summary>Swaps X and Z; a door facing south faces west once transposed, north east, and back.</summary>
    internal static PlannedRoom TransposeRoom(PlannedRoom r)
    {
        var bounds = r.Region.Bounds();
        return r with
        {
            Region = RectRegion(new Rectangle(bounds.Z, bounds.X, bounds.Height, bounds.Width)),
            Door = TransposeCell(r.Door),
            DoorRot = r.DoorRot is { } dr ? TransposeRot(dr) : null,
            Facing = r.Facing is { } fc ? TransposeRot(fc) : null,
        };
    }

    internal static bool RectsOverlap(Rectangle a, Rectangle b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Z < b.Z + b.Height && b.Z < a.Z + a.Height;
}
