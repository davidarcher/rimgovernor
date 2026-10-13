namespace RimGovernor.Brain.Policy;

/// <summary>
/// FreeSites' free-cell census: only walkable, unoccupied, unzoned, light-supporting cells outside any
/// protected footprint may host a footprint. (Go <c>FreeSiteRequest</c>.)
/// </summary>
public sealed record FreeSiteRequest
{
    public Bounds Bounds { get; init; }

    public Cell Anchor { get; init; }

    public IReadOnlyList<SiteCell> Cells { get; init; } = [];

    public IReadOnlyList<Cell> Protected { get; init; } = [];

    /// <summary>The terrain defs that afford Light (<c>SiteCell.SupportsLight</c>).</summary>
    public LightTerrains Light { get; init; } = new();
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Go <c>FreeSites</c>: returns a bounded, deterministically ordered list of width x height candidate
    /// rectangles nearest the colony anchor (a plain grave's 1x2). It is a proposal only: native placement
    /// previews still decide legality. Missing or unknown cells are never treated as free. Throws
    /// <see cref="DomainException"/> where Go returned an error.
    /// </summary>
    public static IReadOnlyList<Rectangle> FreeSites(FreeSiteRequest r, int width, int height)
    {
        DomainException.Require(r.Bounds.Width > 0 && r.Bounds.Height > 0 && r.Bounds.Width <= 4096 && r.Bounds.Height <= 4096, "invalid free site bounds");
        bool InBounds(Cell c) => c.X >= 0 && c.Z >= 0 && c.X < r.Bounds.Width && c.Z < r.Bounds.Height;
        DomainException.Require(InBounds(r.Anchor), "invalid free site anchor");
        var cells = new Dictionary<Cell, SiteCell>(r.Cells.Count);
        var ordered = new List<Cell>(r.Cells.Count);
        foreach (var c in r.Cells)
        {
            DomainException.Require(InBounds(c.Cell), "free site cell out of bounds");
            DomainException.Require(cells.TryAdd(c.Cell, c), "duplicate free site cell");
            ordered.Add(c.Cell);
        }
        ordered.Sort((a, b) => CellLess(a, b) ? -1 : CellLess(b, a) ? 1 : 0);
        var protectedCells = new HashSet<Cell>();
        foreach (var c in r.Protected)
        {
            DomainException.Require(InBounds(c), "protected free site cell out of bounds");
            protectedCells.Add(c);
        }
        bool Free(Cell p)
        {
            if (!cells.TryGetValue(p, out var c) || protectedCells.Contains(p))
            {
                return false;
            }
            return Positive(c.Walkable) && !c.Occupied() && Positive(Measured(c.Zone, v => !v)) && c.SupportsLight(r.Light);
        }
        var sites = new List<(long Score, Cell Cell)>();
        foreach (var c in ordered)
        {
            if (c.X + width > r.Bounds.Width || c.Z + height > r.Bounds.Height)
            {
                continue;
            }
            var rect = new Rectangle { X = c.X, Z = c.Z, Width = width, Height = height };
            if (!RectCells(rect).All(Free))
            {
                continue;
            }
            sites.Add((SquaredDistance(new Cell(c.X + (width / 2), c.Z + (height / 2)), r.Anchor), c));
        }
        // Stable sort: ties keep the cell order established above.
        return
        [
            .. sites
                .OrderBy(s => s.Score)
                .Select(s => s.Cell)
                .Take(24)
                .Select(cell => new Rectangle { X = cell.X, Z = cell.Z, Width = width, Height = height }),
        ];
    }
}
