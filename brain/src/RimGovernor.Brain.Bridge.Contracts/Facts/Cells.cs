using RimGovernor.Contracts.Wire.Common;
using DomainCell = RimGovernor.Brain.Domain.Cell;
using WireRectangle = RimGovernor.Contracts.Wire.Observations.Rectangle;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go cells.go. cellsRead, readCells and validateCells (the observations_get_cells read and its reply check) are
// wire plumbing and not ported.
public static partial class BridgeFunctions
{
    /// <summary>
    /// Go's <c>RectCells</c>: an inclusive wire rectangle's cells, row-major; empty for a rectangle missing a
    /// corner coordinate or inverted.
    /// </summary>
    public static IReadOnlyList<DomainCell> RectCells(WireRectangle? rectangle)
    {
        var lo = rectangle?.Minimum;
        var hi = rectangle?.Maximum;
        if (lo is null || hi is null || lo.X is null || lo.Z is null || hi.X is null || hi.Z is null || hi.X.GetValueOrDefault() < lo.X.GetValueOrDefault() || hi.Z.GetValueOrDefault() < lo.Z.GetValueOrDefault())
        {
            return [];
        }
        var cells = new List<DomainCell>(checked((hi.X.GetValueOrDefault() - lo.X.GetValueOrDefault() + 1) * (hi.Z.GetValueOrDefault() - lo.Z.GetValueOrDefault() + 1)));
        for (var z = lo.Z.GetValueOrDefault(); z <= hi.Z.GetValueOrDefault(); z++)
        {
            for (var x = lo.X.GetValueOrDefault(); x <= hi.X.GetValueOrDefault(); x++)
            {
                cells.Add(new DomainCell(x, z));
            }
        }
        return cells;
    }

    /// <summary>Go's <c>WireRect</c>: the inclusive wire rectangle bounding <paramref name="cells"/>; null for none.</summary>
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
