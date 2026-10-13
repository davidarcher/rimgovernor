using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

public readonly record struct Rectangle(int X, int Z, int Width, int Height);

/// <summary>
/// Names the terrain defs whose affordances include Light (DefinitionCatalog.LightTerrains): ground a light
/// structure may stand on.
/// </summary>
public sealed record LightTerrains
{
    public LightTerrains()
    {
    }

    public LightTerrains(IReadOnlyDictionary<string, bool> values) => Values = values;

    public IReadOnlyDictionary<string, bool> Values { get; init; } = new Dictionary<string, bool>();

    public bool this[string terrain] => Values.GetValueOrDefault(terrain);
}

public sealed partial record SiteCell
{
    public Cell Cell { get; init; }

    public Fact<bool> Walkable { get; init; }

    public Fact<bool> Zone { get; init; }

    public Fact<bool> Roofed { get; init; }

    public Fact<bool> Indoors { get; init; }

    public Fact<bool> StorageEmpty { get; init; }

    /// <summary>
    /// Reports a door, or a door blueprint or frame, on the cell; indoor furnishing keeps the cells beside a
    /// doorway clear as its aisle.
    /// </summary>
    public Fact<bool> Doorway { get; init; }

    public Fact<double> Fertility { get; init; }

    public Fact<bool> Polluted { get; init; }

    public Fact<double> Glow { get; init; }

    /// <summary>Names the native roof def over the cell (rock roofs mark a mountain face for excavation).</summary>
    public Fact<string> Roof { get; init; }

    /// <summary>Names the native zone covering the cell when Zone is true.</summary>
    public Fact<string> ZoneId { get; init; }

    /// <summary>Names the native room holding the cell: growing-room kinds pick a block per room interior.</summary>
    public Fact<string> Room { get; init; }

    // The per-cell thing list and the tile columns that join it. Terrain names the terrain def; InHome is
    // inside the home area; BaseTerrain names the terrain def under a floor or bridge; its affordances are
    // what a foundation may stand on. SnowDepth and TopLayerRemovable read the ground.
    public Fact<string> Terrain { get; init; }

    public Fact<bool> InHome { get; init; }

    public Fact<string> BaseTerrain { get; init; }

    public Fact<double> SnowDepth { get; init; }

    public Fact<bool> TopLayerRemovable { get; init; }

    /// <summary>A RemoveFoundation designation standing on the cell (a bridge ordered lifted).</summary>
    public Fact<bool> FoundationRemoval { get; init; }

    /// <summary>
    /// The cell's non-pawn things in native order: a view into the grid's shared slab, never to be modified.
    /// Empty means none on a held cell; a fogged cell is not a row at all.
    /// </summary>
    public IReadOnlyList<Thing> Things { get; init; } = [];
}

public static class SiteCellExtensions
{
    /// <summary>Reports whether the cell's top terrain affords Light; a cell without a known terrain does not.</summary>
    public static bool SupportsLight(this SiteCell c, LightTerrains light) =>
        c.Terrain.TryGetValue(out var name) && light[name];
}

public static partial class PolicyFunctions
{
    internal static List<Cell> SortedCells(IReadOnlyCollection<Cell> set)
    {
        var result = set.ToList();
        result.Sort(LessComparer<Cell>(CellLess));
        return result;
    }

    /// <summary>A total-order comparer from a Go-style less function.</summary>
    internal static Comparer<T> LessComparer<T>(Func<T, T, bool> less) =>
        Comparer<T>.Create((a, b) => less(a, b) ? -1 : less(b, a) ? 1 : 0);

    /// <summary>
    /// The 3x3 indoor stockpile: the rectangle's upper-middle patch, or for any other shape the 3x3 nearest
    /// that position whose cells all lie in the interior. Default when no 3x3 fits.
    /// </summary>
    internal static Rectangle StarterStorage(RoomFootprint shell)
    {
        var b = shell.Bounds;
        var inside = new HashSet<Cell>(shell.Interior);
        bool Fits(Rectangle r) => RectCells(r).All(inside.Contains);
        var want = new Rectangle(b.X + (b.Width / 2) - 1, b.Z + (b.Height / 2) + 1, 3, 3);
        if (Fits(want))
        {
            return want;
        }
        var best = default(Rectangle);
        var found = false;
        foreach (var c in shell.Interior)
        {
            var r = new Rectangle(c.X, c.Z, 3, 3);
            if (!Fits(r))
            {
                continue;
            }
            if (!found || SquaredDistance(new Cell(r.X, r.Z), new Cell(want.X, want.Z)) < SquaredDistance(new Cell(best.X, best.Z), new Cell(want.X, want.Z)))
            {
                (best, found) = (r, true);
            }
        }
        return best;
    }

    internal static List<Cell> RectCells(Rectangle r)
    {
        var cells = new List<Cell>(r.Width * r.Height);
        for (var x = r.X; x < r.X + r.Width; x++)
        {
            for (var z = r.Z; z < r.Z + r.Height; z++)
            {
                cells.Add(new Cell(x, z));
            }
        }
        return cells;
    }

    internal static long SquaredDistance(Cell a, Cell b)
    {
        long x = (long)a.X - b.X, z = (long)a.Z - b.Z;
        return (x * x) + (z * z);
    }
}
