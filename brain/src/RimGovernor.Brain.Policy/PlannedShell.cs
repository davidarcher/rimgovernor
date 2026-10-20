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

    /// <summary>The item stacks on the cell (0 for none); unknown is never 0.</summary>
    public Fact<int> StorageStacks { get; init; }

    /// <summary>
    /// The stacks the cell can hold: a storage building's maxItemsInCell (3 on a shelf), 0 under any other edifice,
    /// 1 on open ground.
    /// </summary>
    public Fact<int> StorageCapacity { get; init; }

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

    /// <summary>
    /// Keys the native room holding the cell (the grid's room key): growing-room kinds pick a block per room
    /// interior. Unknown outside any room.
    /// </summary>
    public Fact<int> Room { get; init; }

    // The per-cell thing list and the tile columns that join it. Terrain names the terrain def; InHome is
    // inside the home area; BaseTerrain names the terrain def under a floor or bridge; its affordances are
    // what a foundation may stand on.
    public Fact<string> Terrain { get; init; }

    public Fact<bool> InHome { get; init; }

    public Fact<string> BaseTerrain { get; init; }

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

public static partial class PlannedRules
{
    internal static List<Cell> SortedCells(IReadOnlyCollection<Cell> set)
    {
        var result = set.ToList();
        result.Sort(LessComparer<Cell>(CombatManhunterRules.CellLess));
        return result;
    }

    /// <summary>A total-order comparer from a less function.</summary>
    internal static Comparer<T> LessComparer<T>(Func<T, T, bool> less) =>
        Comparer<T>.Create((a, b) => less(a, b) ? -1 : less(b, a) ? 1 : 0);

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
