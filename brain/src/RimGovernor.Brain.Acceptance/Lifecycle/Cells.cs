using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Bridge;
using RimGovernor.Contracts.Wire;
using Common = RimGovernor.Contracts.Wire.Common;
using Mirror = RimGovernor.Contracts.Wire.Mirror;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// One held cell of a cell grid (without the
/// per-cell thing list). A roof, zone or room the cell lacks is null.
/// </summary>
public sealed record GridCell
{
    public Cell Cell { get; init; }

    public bool Walkable { get; init; }

    public bool Zone { get; init; }

    public bool Roofed { get; init; }

    public bool Indoors { get; init; }

    public int StorageStacks { get; init; }

    public bool Doorway { get; init; }

    public double Fertility { get; init; }

    public bool Polluted { get; init; }

    public double Glow { get; init; }

    public string? Roof { get; init; }

    public string? ZoneId { get; init; }

    public int? Room { get; init; }

    public string Terrain { get; init; } = "";

    public bool InHome { get; init; }

    public string BaseTerrain { get; init; } = "";

    public double SnowDepth { get; init; }

    public bool TopLayerRemovable { get; init; }

    public bool FoundationRemoval { get; init; }

    public int StorageCapacity { get; init; }
}

/// <summary>
/// The harness's view of a cell grid an <c>observations_get_cells</c> read answers: the
/// held cells, the cells by room or zone, and the per-cell things straight from the grid's arrays.
/// </summary>
public sealed class MapCellGrid
{
    private readonly Mirror.CellGrid _grid;

    private MapCellGrid(Mirror.CellGrid grid) => _grid = grid;

    public int X => _grid.X;

    public int Z => _grid.Z;

    public int Width => _grid.Width;

    public int Height => _grid.Height;

    /// <summary>The grid a read carried, which a cells read must.</summary>
    public static MapCellGrid Of(Mirror.CellGrid? grid)
    {
        var n = grid is null ? 0 : (long)grid.Width * grid.Height;
        if (grid is null || grid.Width < 1 || grid.Height < 1 || grid.Held.Length != n || grid.Things.Length != n)
        {
            throw new AcceptanceException("cellgrid: the read carries no grid over its rect");
        }

        return new MapCellGrid(grid);
    }

    /// <summary>The held cells, row-major.</summary>
    public IReadOnlyList<GridCell> Cells()
    {
        var cells = new List<GridCell>();
        for (var j = 0; j < _grid.Held.Length; j++)
        {
            if (_grid.Held[j])
            {
                cells.Add(CellAt(j));
            }
        }

        return cells;
    }

    /// <summary>The things on each held cell, row-major like <see cref="Cells"/>; empty for none.</summary>
    public IReadOnlyList<IReadOnlyList<RimGovernor.Brain.Policy.Thing>> Things()
    {
        var things = new List<IReadOnlyList<RimGovernor.Brain.Policy.Thing>>();
        for (var j = 0; j < _grid.Held.Length; j++)
        {
            if (_grid.Held[j])
            {
                things.Add(_grid.Things[j] ?? []);
            }
        }

        return things;
    }

    /// <summary>The held cell at <paramref name="cell"/>; null outside the grid's rect or where the cell is not held (fogged).</summary>
    public GridCell? At(Cell cell) => Index(cell) is int j && _grid.Held[j] ? CellAt(j) : null;

    /// <summary>The things on <paramref name="cell"/>; empty outside the rect, on a cell not held, or for none.</summary>
    public IReadOnlyList<RimGovernor.Brain.Policy.Thing> ThingsAt(Cell cell) => Index(cell) is int j && _grid.Held[j] ? _grid.Things[j] ?? [] : [];

    /// <summary>The held cells for which <paramref name="match"/> holds, row-major.</summary>
    public IReadOnlyList<Cell> Where(Func<GridCell, bool> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        return [.. Cells().Where(match).Select(c => c.Cell)];
    }

    private int? Index(Cell cell)
    {
        var dx = cell.X - X;
        var dz = cell.Z - Z;
        return dx < 0 || dz < 0 || dx >= Width || dz >= Height ? null : (dz * Width) + dx;
    }

    /// <summary>The held cells by room key (a room row's gridRoom), row-major.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<Cell>> RoomCells() => GroupBy(j => (_grid.Room[j] is not null, _grid.Room[j].GetValueOrDefault()));

    /// <summary>The held cells by zone id, row-major.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Cell>> ZoneCells() => GroupBy(j => (_grid.ZoneId[j] is not null, _grid.ZoneId[j] ?? ""));

    private Dictionary<TKey, IReadOnlyList<Cell>> GroupBy<TKey>(Func<int, (bool Has, TKey Key)> keyOf)
        where TKey : notnull
    {
        var groups = new Dictionary<TKey, List<Cell>>();
        for (var j = 0; j < _grid.Held.Length; j++)
        {
            var (has, key) = keyOf(j);
            if (!_grid.Held[j] || !has)
            {
                continue;
            }

            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups[key] = list;
            }

            list.Add(CellOf(j));
        }

        return groups.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<Cell>)pair.Value);
    }

    private Cell CellOf(int j) => new(X + (j % Width), Z + (j / Width));

    private GridCell CellAt(int j) => new()
    {
        Cell = CellOf(j),
        Walkable = _grid.Walkable[j],
        Zone = _grid.ZoneId[j] is not null,
        Roofed = _grid.Roof[j] is not null,
        Indoors = _grid.Indoors[j],
        StorageStacks = _grid.StorageStacks[j],
        Doorway = _grid.Doorway[j],
        Fertility = _grid.Fertility[j],
        Polluted = _grid.Polluted[j],
        Glow = _grid.Glow[j],
        Roof = _grid.Roof[j],
        ZoneId = _grid.ZoneId[j],
        Room = _grid.Room[j],
        Terrain = _grid.Terrain[j],
        InHome = _grid.InHome[j],
        BaseTerrain = _grid.BaseTerrain[j],
        SnowDepth = _grid.SnowDepth[j],
        TopLayerRemovable = _grid.TopLayerRemovable[j],
        FoundationRemoval = _grid.FoundationRemoval[j],
        StorageCapacity = _grid.StorageCapacity[j],
    };
}
/// <summary>The whole-map cell read.</summary>
public static class CellReads
{
    /// <summary>The whole map's cell grid, read through <c>observations_get_cells</c>: an anchor read for the map's size, then the map.</summary>
    public static async Task<MapCellGrid> MapCellsAsync(this Harness harness, string label, Common.Identity identity, CancellationToken cancellationToken)
    {
        var anchor = await harness.ReadCellsAsync(label + "-size", identity, new Cell(0, 0), new Cell(0, 0), cancellationToken).ConfigureAwait(false);
        var width = checked((int)anchor.MapSize.Width);
        var height = checked((int)anchor.MapSize.Height);
        var map = await harness.ReadCellsAsync(label, identity, new Cell(0, 0), new Cell(width - 1, height - 1), cancellationToken).ConfigureAwait(false);
        return MapCellGrid.Of(map.Grid);
    }

    private static async Task<Observations.CellsSnapshot> ReadCellsAsync(this Harness harness, string label, Common.Identity identity, Cell min, Cell max, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);
        var request = new Observations.GetCellsRequest
        {
            Scope = TypedReads.Scope(identity),
            Rectangle = new() { Minimum = new() { X = min.X, Z = min.Z }, Maximum = new() { X = max.X, Z = max.Z } },
        };
        return await harness.CallAsync(label, NativeOps.GetCells, request, cancellationToken).ConfigureAwait(false);
    }
}
