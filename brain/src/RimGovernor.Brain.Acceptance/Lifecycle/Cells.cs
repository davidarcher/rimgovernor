using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Contracts.Wire;
using Mirror = RimGovernor.Contracts.Wire.Mirror;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// One held cell of a decoded whole-map grid (Go <c>policy.SiteCell</c> as <c>cellgrid.Grid.Cells()</c> returns it,
/// without the per-cell thing list). A member is null where the wire marks the fact unknown.
/// </summary>
public sealed record GridCell
{
    public Cell Cell { get; init; }

    public bool? Walkable { get; init; }

    public bool? Zone { get; init; }

    public bool? Roofed { get; init; }

    public bool? Indoors { get; init; }

    public bool? StorageEmpty { get; init; }

    public bool? Doorway { get; init; }

    public double? Fertility { get; init; }

    public bool? Polluted { get; init; }

    public double? Glow { get; init; }

    public string? Roof { get; init; }

    public string? ZoneId { get; init; }

    public string? Room { get; init; }

    public string? Terrain { get; init; }

    public bool? InHome { get; init; }

    public string? BaseTerrain { get; init; }

    public double? SnowDepth { get; init; }

    public bool? TopLayerRemovable { get; init; }

    public bool? FoundationRemoval { get; init; }
}

/// <summary>
/// A decoded whole-map grid (Go <c>cellgrid.Grid</c>): one array per cell field over a rectangle, with the wire's
/// sentinels (cell 0 not held, 1 held; bools 0 unknown, 1 false, 2 true; floats NaN unknown; strings 0 unknown, k for
/// strings[k-1]). Only a keyframe decodes, which is what an <c>observations_get_cells</c> read answers; the harness
/// has no use for the thing list, so it is not decoded.
/// </summary>
public sealed class MapCellGrid
{
    private const long MaxCells = 1 << 20;

    private const int PresenceArray = 0;

    private enum ArrayKind : byte
    {
        Code,
        Number,
        Index,
    }

    // The arrays in the wire's field order (presence first).
    private static readonly ArrayKind[] Kinds =
    [
        ArrayKind.Code, ArrayKind.Code, ArrayKind.Code, ArrayKind.Code, ArrayKind.Code, ArrayKind.Code, ArrayKind.Code,
        ArrayKind.Number, ArrayKind.Code, ArrayKind.Number, ArrayKind.Index, ArrayKind.Index, ArrayKind.Index, ArrayKind.Index,
        ArrayKind.Code, ArrayKind.Index, ArrayKind.Number, ArrayKind.Code, ArrayKind.Code,
    ];

    private readonly byte[][] _codes = new byte[Kinds.Length][];
    private readonly double[][] _numbers = new double[Kinds.Length][];
    private readonly uint[][] _indexes = new uint[Kinds.Length][];
    private readonly string[] _strings;

    private MapCellGrid(int x, int z, int width, int height, string[] strings)
    {
        X = x;
        Z = z;
        Width = width;
        Height = height;
        _strings = strings;
    }

    public int X { get; }

    public int Z { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Decodes a keyframe grid.</summary>
    public static MapCellGrid Apply(Mirror.CellGrid? grid)
    {
        if (grid?.Rect is not { X: >= 0, Z: >= 0, Width: >= 1, Height: >= 1 } rect || (long)rect.Width.GetValueOrDefault() * rect.Height.GetValueOrDefault() > MaxCells)
        {
            throw Failure("rect");
        }

        var width = rect.Width.GetValueOrDefault();
        var height = rect.Height.GetValueOrDefault();
        var strings = new string[grid.Strings.Count];
        for (var i = 0; i < strings.Length; i++)
        {
            strings[i] = grid.Strings[i];
        }

        var result = new MapCellGrid(rect.X.GetValueOrDefault(), rect.Z.GetValueOrDefault(), width, height, strings);
        Mirror.FieldArray?[] slots =
        [
            grid.Cell, grid.Walkable, grid.Zone, grid.Roofed, grid.Indoors, grid.StorageEmpty, grid.Doorway,
            grid.Fertility, grid.Polluted, grid.Glow, grid.Roof, grid.ZoneId, grid.Room, grid.Terrain,
            grid.InHome, grid.BaseTerrain, grid.SnowDepth, grid.TopLayerRemovable, grid.FoundationRemoval,
        ];
        for (var i = 0; i < Kinds.Length; i++)
        {
            result.DecodeColumn(i, slots[i] ?? throw Failure("a keyframe lacks an array"), width * height);
        }

        return result;
    }

    /// <summary>The held cells, row-major.</summary>
    public IReadOnlyList<GridCell> Cells()
    {
        var presence = _codes[PresenceArray];
        var cells = new List<GridCell>();
        for (var j = 0; j < presence.Length; j++)
        {
            if (presence[j] == 1)
            {
                cells.Add(CellAt(j));
            }
        }

        return cells;
    }

    /// <summary>The held cells by room key (a room row's gridRoom), row-major (Go <c>RoomCells</c>).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Cell>> RoomCells() => GroupBy(12);

    /// <summary>The held cells by zone id, row-major (Go <c>ZoneCells</c>).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Cell>> ZoneCells() => GroupBy(11);

    private static AcceptanceException Failure(string message) => new("cellgrid: " + message);

    private Dictionary<string, IReadOnlyList<Cell>> GroupBy(int array)
    {
        var groups = new Dictionary<string, List<Cell>>(StringComparer.Ordinal);
        var presence = _codes[PresenceArray];
        var keys = _indexes[array];
        for (var j = 0; j < presence.Length; j++)
        {
            if (presence[j] != 1 || keys[j] == 0)
            {
                continue;
            }

            var key = _strings[keys[j] - 1];
            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups[key] = list;
            }

            list.Add(CellOf(j));
        }

        return groups.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<Cell>)pair.Value, StringComparer.Ordinal);
    }

    private Cell CellOf(int j) => new(X + (j % Width), Z + (j / Width));

    private GridCell CellAt(int j) => new()
    {
        Cell = CellOf(j),
        Walkable = BoolAt(1, j),
        Zone = BoolAt(2, j),
        Roofed = BoolAt(3, j),
        Indoors = BoolAt(4, j),
        StorageEmpty = BoolAt(5, j),
        Doorway = BoolAt(6, j),
        Fertility = NumberAt(7, j),
        Polluted = BoolAt(8, j),
        Glow = NumberAt(9, j),
        Roof = StringAt(10, j),
        ZoneId = StringAt(11, j),
        Room = StringAt(12, j),
        Terrain = StringAt(13, j),
        InHome = BoolAt(14, j),
        BaseTerrain = StringAt(15, j),
        SnowDepth = NumberAt(16, j),
        TopLayerRemovable = BoolAt(17, j),
        FoundationRemoval = BoolAt(18, j),
    };

    private bool? BoolAt(int array, int j) => _codes[array][j] switch
    {
        0 => null,
        2 => true,
        _ => false,
    };

    private double? NumberAt(int array, int j) => double.IsNaN(_numbers[array][j]) ? null : _numbers[array][j];

    private string? StringAt(int array, int j) => _indexes[array][j] == 0 ? null : _strings[_indexes[array][j] - 1];

    private void DecodeColumn(int array, Mirror.FieldArray wire, int n)
    {
        var kind = Kinds[array];
        var codes = kind == ArrayKind.Code ? new byte[n] : [];
        var numbers = kind == ArrayKind.Number ? new double[n] : [];
        var indexes = kind == ArrayKind.Index ? new uint[n] : [];
        if (kind == ArrayKind.Number)
        {
            Array.Fill(numbers, double.NaN);
        }

        void Set(int j, uint code, double number)
        {
            switch (kind)
            {
                case ArrayKind.Code:
                    codes[j] = code <= 2 ? (byte)code : throw Failure("code out of range");
                    break;
                case ArrayKind.Number:
                    numbers[j] = double.IsInfinity(number) ? throw Failure("number not finite") : number;
                    break;
                default:
                    indexes[j] = code <= _strings.Length ? code : throw Failure("string index out of range");
                    break;
            }
        }

        switch (wire.Form)
        {
            case Mirror.FieldArrayForm.CodesCase { Value: var bytes }:
                if (kind != ArrayKind.Code || bytes is null || bytes.Length != n)
                {
                    throw Failure("codes array");
                }

                for (var j = 0; j < bytes.Length; j++)
                {
                    Set(j, bytes[j], 0);
                }

                break;
            case Mirror.FieldArrayForm.NumbersCase { Value: var packed }:
                if (kind != ArrayKind.Number || packed is null || packed.Values.Count != n)
                {
                    throw Failure("numbers array");
                }

                for (var j = 0; j < n; j++)
                {
                    Set(j, 0, packed.Values[j]);
                }

                break;
            case Mirror.FieldArrayForm.IndexesCase { Value: var packed }:
                if (kind != ArrayKind.Index || packed is null || packed.Values.Count != n)
                {
                    throw Failure("indexes array");
                }

                for (var j = 0; j < n; j++)
                {
                    Set(j, packed.Values[j], 0);
                }

                break;
            case Mirror.FieldArrayForm.SparseCase { Value: var sparse }:
                if (sparse is null)
                {
                    throw Failure("array form");
                }

                var values = sparse.Code.Count;
                var other = sparse.Number.Count;
                if (kind == ArrayKind.Number)
                {
                    (values, other) = (other, values);
                }

                if (values != sparse.Index.Count || other != 0)
                {
                    throw Failure("sparse array");
                }

                long last = -1;
                for (var p = 0; p < sparse.Index.Count; p++)
                {
                    long at = sparse.Index[p];
                    if (at <= last || at >= n)
                    {
                        throw Failure("sparse index");
                    }

                    last = at;
                    if (kind == ArrayKind.Number)
                    {
                        Set((int)at, 0, sparse.Number[p]);
                    }
                    else
                    {
                        Set((int)at, sparse.Code[p], 0);
                    }
                }

                break;
            default:
                throw Failure("array form");
        }

        _codes[array] = codes;
        _numbers[array] = numbers;
        _indexes[array] = indexes;
    }
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
            snapshot = WireJson.Deserialize<Observations.CellsSnapshot>(observed.ToJsonString());
        }
        catch (JsonException e)
        {
            throw new AcceptanceException($"cells snapshot: {e.Message}", e);
        }

        return (snapshot, MapCellGrid.Apply(snapshot.Grid));
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
