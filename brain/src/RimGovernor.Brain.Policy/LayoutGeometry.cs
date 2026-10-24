namespace RimGovernor.Brain.Policy;

public static partial class LayoutGeometryRules
{
    /// <summary>Reports c in the ring of walls round r, corners excluded.</summary>
    public static bool InWall(Rectangle r, Cell c)
    {
        var onX = c.X == r.X - 1 || c.X == r.X + r.Width;
        var onZ = c.Z == r.Z - 1 || c.Z == r.Z + r.Height;
        return (onX && c.Z >= r.Z && c.Z < r.Z + r.Height) || (onZ && c.X >= r.X && c.X < r.X + r.Width);
    }

    internal static bool RectsOverlap(Rectangle a, Rectangle b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Z < b.Z + b.Height && b.Z < a.Z + a.Height;
}
