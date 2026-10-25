using RimGovernor.Brain.GameFacts.Common;
using DomainCell = RimGovernor.Brain.Domain.Cell;
using WireRectangle = RimGovernor.Brain.GameFacts.Observations.Rectangle;

namespace RimGovernor.Brain.Policy;

// cellsRead, readCells and validateCells (the observations_get_cells read and its reply check) are
// wire plumbing and not ported.
public static class CellsRules
{
    /// <summary>
    /// An inclusive wire rectangle's cells, row-major; empty for a rectangle missing a
    /// corner coordinate or inverted.
    /// </summary>
    public static IReadOnlyList<DomainCell> RectCells(WireRectangle? rectangle)
    {
        if (rectangle is null) throw new DomainException("Rectangle missing.");
        var lo = rectangle.Minimum;
        var hi = rectangle.Maximum;
        if (hi.X < lo.X || hi.Z < lo.Z)
        {
            throw new DomainException("Rectangle is inverted.");
        }
        var cells = new List<DomainCell>(checked((hi.X - lo.X + 1) * (hi.Z - lo.Z + 1)));
        for (var z = lo.Z; z <= hi.Z; z++)
        {
            for (var x = lo.X; x <= hi.X; x++)
            {
                cells.Add(new DomainCell(x, z));
            }
        }
        return cells;
    }

    /// <summary>The inclusive wire rectangle bounding <paramref name="cells"/>; null for none.</summary>
    public static WireRectangle? WireRect(IReadOnlyList<DomainCell> cells)
    {
        if (cells.Count == 0)
        {
            return null;
        }
        int loX = cells[0].X, loZ = cells[0].Z, hiX = cells[0].X, hiZ = cells[0].Z;
        foreach (var cell in cells.Skip(1))
        {
            loX = Math.Min(loX, cell.X);
            loZ = Math.Min(loZ, cell.Z);
            hiX = Math.Max(hiX, cell.X);
            hiZ = Math.Max(hiZ, cell.Z);
        }
        return new WireRectangle { Minimum = new Cell { X = loX, Z = loZ }, Maximum = new Cell { X = hiX, Z = hiZ } };
    }
}
