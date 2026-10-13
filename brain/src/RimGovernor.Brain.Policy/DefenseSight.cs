namespace RimGovernor.Brain.Policy;

internal sealed partial class DefenseSite
{
    /// <summary>
    /// Reports whether the cell stops a line of sight, as GenGrid.CanBeSeenOver reads it: an edifice of Full
    /// fillage, an open door excepted. A door is read closed. Unknown for a cell the census lacks or fogs.
    /// </summary>
    internal Fact<bool> BlocksSight(Cell c)
    {
        if (!Cells.TryGetValue(c, out var row))
        {
            return Unknown<bool>();
        }
        if (!row.Passable.IsKnown)
        {
            return Unknown<bool>();
        }
        if (!row.CoverFill.TryGetValue(out var fill))
        {
            return Unknown<bool>();
        }
        return Known(row.Edifice != "" && fill > DefenseSightConstants.FullFillAbove);
    }

    /// <summary>
    /// GenSight.LineOfSight(from, to, map, skipFirstCell: true): the game's integer line walk, which stops short
    /// of the target and does not test the shooter's own cell. Blocked when any walked cell blocks it; clear only
    /// if every walked cell and both endpoints are known.
    /// </summary>
    internal Fact<bool> LineOfSight(Cell from, Cell to)
    {
        var b = R.Bounds;
        bool InBounds(Cell c) => c.X >= 0 && c.Z >= 0 && c.X < b.Width && c.Z < b.Height;
        if (!InBounds(from) || !InBounds(to))
        {
            return Known(false);
        }
        var unknown = false;
        foreach (var c in new[] { from, to })
        {
            if (!BlocksSight(c).IsKnown)
            {
                unknown = true;
            }
        }
        var sideOnEqual = from.X < to.X;
        if (from.X == to.X)
        {
            sideOnEqual = from.Z < to.Z;
        }
        int dx = Math.Abs(to.X - from.X), dz = Math.Abs(to.Z - from.Z);
        int x = from.X, z = from.Z;
        var stepX = to.X > from.X ? 1 : -1;
        var stepZ = to.Z > from.Z ? 1 : -1;
        // The game scales the error terms by four; only their signs matter.
        var err = dx - dz;
        dx *= 2;
        dz *= 2;
        for (var n = 1 + Math.Abs(to.X - from.X) + Math.Abs(to.Z - from.Z); n > 1; n--)
        {
            var c = new Cell(x, z);
            if (c != from)
            {
                if (!BlocksSight(c).TryGetValue(out var blocked))
                {
                    unknown = true;
                }
                else if (blocked)
                {
                    return Known(false);
                }
            }
            if (err > 0 || (err == 0 && sideOnEqual))
            {
                x += stepX;
                err -= dz;
            }
            else
            {
                z += stepZ;
                err += dx;
            }
        }
        return unknown ? Unknown<bool>() : Known(true);
    }
}

internal static class DefenseSightConstants
{
    /// <summary>ThingDef.Fillage's boundary: a def with fillPercent above it is FillCategory.Full, which blocks sight.</summary>
    internal const double FullFillAbove = 0.99;
}

public static partial class PolicyFunctions
{
    /// <summary>The game's line of sight between two cells of the request's census, for callers that hold only a request.</summary>
    public static Fact<bool> LineOfSight(this DefenseRequest r, Cell from, Cell to) => DefenseSite.Create(r).LineOfSight(from, to);
}
