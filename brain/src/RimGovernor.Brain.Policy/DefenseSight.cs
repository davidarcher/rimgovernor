namespace RimGovernor.Brain.Policy;

/// <summary>How a census cell stands to a line of sight: open, blocking, or unseen (fogged or outside the census).</summary>
public enum SightCell
{
    Open,
    Blocks,
    Unseen,
}

/// <summary>
/// A line of sight over the census: clear when every walked cell is seen and open, blocked at the first seen
/// blocker or off the map, unseen when no seen cell blocks it but a fogged or uncensused cell lies on it.
/// </summary>
public enum Sightline
{
    Clear,
    Blocked,
    Unseen,
}

internal sealed partial class DefenseSite
{
    /// <summary>
    /// How the cell stands to a line of sight, as GenGrid.CanBeSeenOver reads it: an edifice of Full fillage
    /// blocks, an open door excepted (a door is read closed). A cell the census lacks or fogs is unseen.
    /// </summary>
    internal SightCell BlocksSight(Cell c)
    {
        if (!Cells.TryGetValue(c, out var row))
        {
            return SightCell.Unseen;
        }
        return row.Edifice != "" && row.CoverFill > DefenseSightConstants.FullFillAbove ? SightCell.Blocks : SightCell.Open;
    }

    /// <summary>
    /// GenSight.LineOfSight(from, to, map, skipFirstCell: true): the game's integer line walk, which stops short
    /// of the target and does not test the shooter's own cell. Blocked when any walked cell blocks it; clear only
    /// if every walked cell and both endpoints are seen.
    /// </summary>
    internal Sightline LineOfSight(Cell from, Cell to)
    {
        var b = R.Bounds;
        bool InBounds(Cell c) => c.X >= 0 && c.Z >= 0 && c.X < b.Width && c.Z < b.Height;
        if (!InBounds(from) || !InBounds(to))
        {
            return Sightline.Blocked;
        }
        var unseen = BlocksSight(from) == SightCell.Unseen || BlocksSight(to) == SightCell.Unseen;
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
                switch (BlocksSight(c))
                {
                    case SightCell.Blocks:
                        return Sightline.Blocked;
                    case SightCell.Unseen:
                        unseen = true;
                        break;
                    case SightCell.Open:
                        break;
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
        return unseen ? Sightline.Unseen : Sightline.Clear;
    }
}

internal static class DefenseSightConstants
{
    /// <summary>ThingDef.Fillage's boundary: a def with fillPercent above it is FillCategory.Full, which blocks sight.</summary>
    internal const double FullFillAbove = 0.99;
}
