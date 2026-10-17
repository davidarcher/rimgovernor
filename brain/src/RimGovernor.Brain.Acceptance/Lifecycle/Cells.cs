using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Contracts.Wire;
using Mirror = RimGovernor.Contracts.Wire.Mirror;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// One held cell of a cell grid (Go <c>policy.SiteCell</c> as <c>cellgrid.Grid.Cells()</c> returns it, without the
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
/// The harness's view of a cell grid an <c>observations_get_cells</c> read answers (Go <c>cellgrid.Grid</c>): the
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

    /// <summary>The held cells by room key (a room row's gridRoom), row-major (Go <c>RoomCells</c>).</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<Cell>> RoomCells() => GroupBy(j => (_grid.Room[j] is not null, _grid.Room[j].GetValueOrDefault()));

    /// <summary>The held cells by zone id, row-major (Go <c>ZoneCells</c>).</summary>
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
/// <summary>The whole-map cell read (Go <c>cells.go</c>).</summary>
public static class CellReads
{
    /// <summary>The snapshot an <c>observations_get_cells</c> reply's <c>observed</c> arm holds (as Outcome returns it) and its grid decoded.</summary>
    public static (Observations.CellsSnapshot Snapshot, MapCellGrid Grid) DecodeCells(JsonObject observed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        Observations.CellsSnapshot snapshot;
        try
        {
            snapshot = WireCodec.ToRecord<Observations.CellsSnapshot>(observed);
        }
        catch (JsonException e)
        {
            throw new AcceptanceException($"cells snapshot: {e.Message}", e);
        }

        return (snapshot, MapCellGrid.Of(snapshot.Grid));
    }

    /// <summary>The whole map's cell grid, read through <c>observations_get_cells</c>: an anchor read for the map's size, then the map.</summary>
    public static async Task<MapCellGrid> MapCellsAsync(this Harness harness, string label, JsonNode identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(identity);

        async Task<(Observations.CellsSnapshot Snapshot, MapCellGrid Grid)> Read(string suffix, long maxX, long maxZ)
        {
            var reply = await harness.WireAsync(
                label + suffix,
                "observations_get_cells",
                new JsonObject
                {
                    ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() },
                    ["rectangle"] = new JsonObject
                    {
                        ["minimum"] = new JsonObject { ["x"] = 0, ["z"] = 0 },
                        ["maximum"] = new JsonObject { ["x"] = maxX, ["z"] = maxZ },
                    },
                },
                cancellationToken).ConfigureAwait(false);
            var (_, observed) = Harness.Outcome(reply, "observed");
            return DecodeCells(observed);
        }

        var (anchor, _) = await Read("-size", 0, 0).ConfigureAwait(false);
        var width = (long)(anchor.MapSize?.Width ?? 0);
        var height = (long)(anchor.MapSize?.Height ?? 0);
        var (_, grid) = await Read(string.Empty, width - 1, height - 1).ConfigureAwait(false);
        return grid;
    }

    /// <summary>A ProtoJSON inclusive rectangle's cells, row-major; empty when the rectangle lacks a corner.</summary>
    public static IReadOnlyList<Cell> RectCells(JsonNode? raw)
    {
        var low = raw.Member("minimum").AsMap();
        var high = raw.Member("maximum").AsMap();
        var cells = new List<Cell>();
        if (low is null || high is null)
        {
            return cells;
        }

        var x0 = (int)low["x"].AsNumber();
        var z0 = (int)low["z"].AsNumber();
        var x1 = (int)high["x"].AsNumber();
        var z1 = (int)high["z"].AsNumber();
        for (var z = z0; z <= z1; z++)
        {
            for (var x = x0; x <= x1; x++)
            {
                cells.Add(new Cell(x, z));
            }
        }

        return cells;
    }
}
