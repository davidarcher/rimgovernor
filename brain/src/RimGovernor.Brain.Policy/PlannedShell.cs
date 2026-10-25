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

/// <summary>A held native cell, retaining its published grid and exact row index without copying observations.</summary>
public sealed record SiteCell
{
    public SiteCell(RimGovernor.Brain.GameFacts.Mirror.CellGrid native, int index)
    {
        ArgumentNullException.ThrowIfNull(native);
        if (native.Width < 1 || native.Height < 1 || index < 0 || (long)index >= (long)native.Width * native.Height || index >= native.Held.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "cell index lies outside the native grid");
        }
        if (!native.Held[index])
        {
            throw new RequiredFactException("fogged native cell has no observation row");
        }
        Native = native;
        Index = index;
    }

    public RimGovernor.Brain.GameFacts.Mirror.CellGrid Native { get; }

    public int Index { get; }

    public Cell Cell => new(Native.X + (Index % Native.Width), Native.Z + (Index / Native.Width));

    public bool Walkable => Native.Walkable[Index];

    public bool Zone => ZoneId is not null;

    public bool Roofed => Roof is not null;

    public bool Indoors => Native.Indoors[Index];

    public int StorageStacks => Native.StorageStacks[Index];

    public int StorageCapacity => Native.StorageCapacity[Index];

    public bool Doorway => Native.Doorway[Index];

    public double Fertility => Native.Fertility[Index];

    public bool Polluted => Native.Polluted[Index];

    public double Glow => Native.Glow[Index];

    public string? Roof => Native.Roof[Index];

    public string? ZoneId => Native.ZoneId[Index];

    public CellRoom Room => Native.Room[Index] is { } room ? new CellRoom.InRoom(room) : CellRoom.Outside;

    public string Terrain => Native.Terrain[Index];

    public string BaseTerrain => Native.BaseTerrain[Index];

    public bool FoundationRemoval => Native.FoundationRemoval[Index];

    /// <summary>The native thing array, never modified; null on a held row means actual absence.</summary>
    public IReadOnlyList<Thing> Things => Native.Things[Index] ?? [];
}
/// <summary>The native room a cell lies in, by the grid's room key, or none.</summary>
public abstract record CellRoom
{
    private protected CellRoom() { }

    /// <summary>The cell lies in the room the grid keys by <paramref name="Key"/>.</summary>
    public sealed record InRoom(int Key) : CellRoom;

    /// <summary>The cell lies in no room (open ground outside any region room, or an impassable edifice).</summary>
    public sealed record NoRoom : CellRoom;

    /// <summary>The one <see cref="NoRoom"/> value.</summary>
    public static readonly CellRoom Outside = new NoRoom();
}

public static class SiteCellExtensions
{
    /// <summary>Reports whether the cell's top terrain affords Light.</summary>
    public static bool SupportsLight(this SiteCell c, LightTerrains light) => light[c.Terrain];
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
