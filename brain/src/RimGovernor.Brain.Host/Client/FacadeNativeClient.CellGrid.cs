using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Host;

/// <summary>
/// Go <c>cellgrid.Grid</c> (cellgrid.go, things.go): a decoded whole-map grid, one array per <see cref="P.SiteCell"/>
/// field over a rectangle, with the wire's sentinels (cell 0 not held, 1 held; bools 0 unknown, 1 false, 2 true;
/// floats NaN unknown; strings 0 unknown, k for strings[k-1]) and the per-cell thing lists. A grid is never changed
/// once built. Every grid the native facade hands over is a keyframe (<c>CellGridEncoder.AttachWhole</c>), so only
/// the keyframe decode of Go's <c>Apply</c> is ported: a delta grid is refused by <see cref="Complete"/> and never
/// reaches <see cref="Apply"/>.
/// </summary>
internal sealed class FrameCellGrid
{
    /// <summary>Go <c>cellgrid.MaxCells</c>: a grid's rect holds at most a whole map of 1024 x 1024.</summary>
    internal const long MaxCells = 1 << 20;

    private const int PresenceArray = 0;
    private const int RoomArray = 12;

    private enum ArrayKind : byte
    {
        Code,
        Number,
        Index,
    }

    // The arrays in the wire's field order (presence first), as Go's `arrays`.
    private static readonly ArrayKind[] Kinds =
    [
        ArrayKind.Code, ArrayKind.Code, ArrayKind.Code, ArrayKind.Code, ArrayKind.Code, ArrayKind.Code, ArrayKind.Code,
        ArrayKind.Number, ArrayKind.Code, ArrayKind.Number, ArrayKind.Index, ArrayKind.Index, ArrayKind.Index, ArrayKind.Index,
        ArrayKind.Code, ArrayKind.Index, ArrayKind.Number, ArrayKind.Code, ArrayKind.Code,
    ];

    private static readonly Func<PbMirror.CellGrid, PbMirror.FieldArray?>[] Slots =
    [
        g => g.Cell, g => g.Walkable, g => g.Zone, g => g.Roofed, g => g.Indoors, g => g.StorageEmpty, g => g.Doorway,
        g => g.Fertility, g => g.Polluted, g => g.Glow, g => g.Roof, g => g.ZoneId, g => g.Room, g => g.Terrain,
        g => g.InHome, g => g.BaseTerrain, g => g.SnowDepth, g => g.TopLayerRemovable, g => g.FoundationRemoval,
    ];

    private readonly byte[][] _codes = new byte[Kinds.Length][];
    private readonly double[][] _numbers = new double[Kinds.Length][];
    private readonly uint[][] _indexes = new uint[Kinds.Length][];
    private readonly string[] _strings;
    private readonly P.Thing[]?[]? _things;
    private readonly object _memoGate = new();
    private P.Rectangle _memoRect;
    private bool _memoBuilt;
    private IReadOnlyList<P.SiteCell> _memoCells = [];
    private ulong _memoFogged;

    private FrameCellGrid(P.Rectangle rect, string[] strings, P.Thing[]?[]? things)
    {
        Rect = rect;
        _strings = strings;
        _things = things;
    }

    /// <summary>The rectangle the grid covers.</summary>
    internal P.Rectangle Rect { get; }

    /// <summary>Go <c>cellgrid.Complete</c>: whether the wire grid carries every array and the thing list, a keyframe.</summary>
    internal static bool Complete(PbMirror.CellGrid grid)
    {
        foreach (var slot in Slots)
        {
            if (slot(grid) is null)
            {
                return false;
            }
        }
        return grid.Things is not null;
    }

    /// <summary>Go <c>cellgrid.Apply(nil, true, g)</c>: decodes a keyframe grid.</summary>
    internal static FrameCellGrid Apply(PbMirror.CellGrid? grid)
    {
        if (grid is null || !ValidRect(grid.Rect))
        {
            throw Failure("mirror cell grid rect");
        }
        var rect = new P.Rectangle(grid.Rect!.X.GetValueOrDefault(), grid.Rect.Z.GetValueOrDefault(), grid.Rect.Width.GetValueOrDefault(), grid.Rect.Height.GetValueOrDefault());
        var n = checked(rect.Width * rect.Height);
        var strings = new string[grid.Strings.Count];
        for (var i = 0; i < strings.Length; i++)
        {
            strings[i] = grid.Strings[i];
        }
        var things = ApplyThings(grid.Things, n, strings);
        var result = new FrameCellGrid(rect, strings, things);
        for (var i = 0; i < Kinds.Length; i++)
        {
            var wire = Slots[i](grid) ?? throw Failure("mirror cell grid keyframe missing an array");
            result.DecodeColumn(i, wire, n);
        }
        return result;
    }

    /// <summary>
    /// Go <c>Grid.Window</c>: the held cells inside <paramref name="rect"/> (clipped to the grid), row-major, and the
    /// count of the rect's cells the grid does not hold (fogged). The last window is memoised: a grid never changes,
    /// so its window over a rect is the same every time the planners ask in the frame.
    /// </summary>
    internal (IReadOnlyList<P.SiteCell> Cells, ulong Fogged) Window(P.Rectangle rect)
    {
        lock (_memoGate)
        {
            if (!_memoBuilt || _memoRect != rect)
            {
                (_memoCells, _memoFogged) = BuildWindow(rect);
                (_memoRect, _memoBuilt) = (rect, true);
            }
            return (_memoCells, _memoFogged);
        }
    }

    /// <summary>
    /// Go <c>Grid.CellsByRoom</c>: the held cells grouped by their grid_room key, row-major within a key; reads the
    /// room column alone.
    /// </summary>
    internal Dictionary<string, List<Cell>> CellsByRoom()
    {
        var result = new Dictionary<string, List<Cell>>(StringComparer.Ordinal);
        var presence = _codes[PresenceArray];
        var rooms = _indexes[RoomArray];
        for (var j = 0; j < presence.Length; j++)
        {
            if (presence[j] != 1 || rooms[j] == 0)
            {
                continue;
            }
            var key = _strings[rooms[j] - 1];
            if (!result.TryGetValue(key, out var list))
            {
                list = [];
                result[key] = list;
            }
            list.Add(new Cell(Rect.X + (j % Rect.Width), Rect.Z + (j / Rect.Width)));
        }
        return result;
    }

    private static BridgeContractException Failure(string message) => BridgeFunctions.Contract("cellgrid: " + message);

    private static bool ValidRect(PbMirror.CellRect? r) =>
        r is not null && r.X is not null && r.Z is not null && r.X.GetValueOrDefault() >= 0 && r.Z.GetValueOrDefault() >= 0
        && r.Width.GetValueOrDefault() >= 1 && r.Height.GetValueOrDefault() >= 1
        && (long)r.Width.GetValueOrDefault() * r.Height.GetValueOrDefault() <= MaxCells;

    private (IReadOnlyList<P.SiteCell> Cells, ulong Fogged) BuildWindow(P.Rectangle rect)
    {
        var minX = Math.Max(rect.X, Rect.X);
        var minZ = Math.Max(rect.Z, Rect.Z);
        var maxX = Math.Min(rect.X + rect.Width, Rect.X + Rect.Width);
        var maxZ = Math.Min(rect.Z + rect.Height, Rect.Z + Rect.Height);
        var presence = _codes[PresenceArray];
        var cells = new List<P.SiteCell>();
        ulong fogged = 0;
        for (var z = minZ; z < maxZ; z++)
        {
            for (var x = minX; x < maxX; x++)
            {
                var j = ((z - Rect.Z) * Rect.Width) + (x - Rect.X);
                if (presence[j] != 1)
                {
                    fogged++;
                    continue;
                }
                cells.Add(CellAt(j));
            }
        }
        return (cells, fogged);
    }

    private P.SiteCell CellAt(int j) =>
        new()
        {
            Cell = new Cell(Rect.X + (j % Rect.Width), Rect.Z + (j / Rect.Width)),
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
            Things = _things is not null && _things[j] is { } list ? list : [],
        };

    private Fact<bool> BoolAt(int array, int j) =>
        _codes[array][j] switch
        {
            0 => Fact.Unknown<bool>(),
            2 => Fact.Known(true),
            _ => Fact.Known(false),
        };

    private Fact<double> NumberAt(int array, int j) =>
        double.IsNaN(_numbers[array][j]) ? Fact.Unknown<double>() : Fact.Known(_numbers[array][j]);

    private Fact<string> StringAt(int array, int j) =>
        _indexes[array][j] == 0 ? Fact.Unknown<string>() : Fact.Known(_strings[_indexes[array][j] - 1]);

    /// <summary>Go <c>decodeColumn</c> for a keyframe: the wire array in its dense or sparse form into array <paramref name="array"/>.</summary>
    private void DecodeColumn(int array, PbMirror.FieldArray wire, int n)
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
                    if (code > 2)
                    {
                        throw Failure("mirror cell grid code out of range");
                    }
                    codes[j] = (byte)code;
                    break;
                case ArrayKind.Number:
                    if (double.IsInfinity(number))
                    {
                        throw Failure("mirror cell grid number not finite");
                    }
                    numbers[j] = number;
                    break;
                default:
                    if (code > _strings.Length)
                    {
                        throw Failure("mirror cell grid string index out of range");
                    }
                    indexes[j] = code;
                    break;
            }
        }

        switch (wire.Form)
        {
            case PbMirror.FieldArrayForm.CodesCase { Value: var bytes }:
                if (kind != ArrayKind.Code || bytes is null || bytes.Length != n)
                {
                    throw Failure("mirror cell grid codes array");
                }
                for (var j = 0; j < bytes.Length; j++)
                {
                    Set(j, bytes[j], 0);
                }
                break;
            case PbMirror.FieldArrayForm.NumbersCase { Value: var packed }:
                if (kind != ArrayKind.Number || packed is null || packed.Values.Count != n)
                {
                    throw Failure("mirror cell grid numbers array");
                }
                for (var j = 0; j < n; j++)
                {
                    Set(j, 0, packed.Values[j]);
                }
                break;
            case PbMirror.FieldArrayForm.IndexesCase { Value: var packed }:
                if (kind != ArrayKind.Index || packed is null || packed.Values.Count != n)
                {
                    throw Failure("mirror cell grid indexes array");
                }
                for (var j = 0; j < n; j++)
                {
                    Set(j, packed.Values[j], 0);
                }
                break;
            case PbMirror.FieldArrayForm.SparseCase { Value: var sparse }:
                if (sparse is null)
                {
                    throw Failure("mirror cell grid array form");
                }
                var values = sparse.Code.Count;
                var other = sparse.Number.Count;
                if (kind == ArrayKind.Number)
                {
                    (values, other) = (other, values);
                }
                if (values != sparse.Index.Count || other != 0)
                {
                    throw Failure("mirror cell grid sparse array");
                }
                long last = -1;
                for (var p = 0; p < sparse.Index.Count; p++)
                {
                    long at = sparse.Index[p];
                    if (at <= last || at >= n)
                    {
                        throw Failure("mirror cell grid sparse index");
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
                throw Failure("mirror cell grid array form");
        }
        _codes[array] = codes;
        _numbers[array] = numbers;
        _indexes[array] = indexes;
    }

    /// <summary>Go <c>applyThings</c> over no base: the wire thing list as one list per listed cell; null when no cell holds a thing.</summary>
    private static P.Thing[]?[]? ApplyThings(PbMirror.ThingList? list, int n, string[] table)
    {
        if (list is null)
        {
            return null;
        }
        var cells = list.Cells;
        var offsets = list.Offsets;
        var things = list.Things;
        if (offsets.Count != cells.Count + 1 || offsets[0] != 0 || offsets[offsets.Count - 1] != things.Count)
        {
            throw Failure("mirror cell grid thing offsets");
        }
        long last = -1;
        for (var p = 0; p < cells.Count; p++)
        {
            if (cells[p] <= last || cells[p] >= n)
            {
                throw Failure("mirror cell grid thing cell index");
            }
            last = cells[p];
            if (offsets[p + 1] < offsets[p])
            {
                throw Failure("mirror cell grid thing offsets not monotonic");
            }
        }
        if (cells.Count == 0)
        {
            return null;
        }
        var decoded = new P.Thing[things.Count];
        for (var i = 0; i < decoded.Length; i++)
        {
            decoded[i] = DecodeThing(things[i], table);
        }
        var result = new P.Thing[]?[n];
        for (var p = 0; p < cells.Count; p++)
        {
            var count = (int)(offsets[p + 1] - offsets[p]);
            result[cells[p]] = count == 0 ? null : decoded.AsSpan((int)offsets[p], count).ToArray();
        }
        return result;
    }

    /// <summary>Go <c>decodeThing</c>: one wire thing as a model record, its strings resolved against the table; the state must be the one its category carries.</summary>
    private static P.Thing DecodeThing(PbMirror.Thing wire, string[] table)
    {
        string Str(uint k) =>
            k == 0 || k > table.Length ? throw Failure("mirror cell grid thing string index out of range") : table[k - 1];

        var def = Str(wire.Def);
        var category = wire.Category switch
        {
            PbMirror.ThingCategory.Item => P.ThingCategory.Item,
            PbMirror.ThingCategory.Building => P.ThingCategory.Building,
            PbMirror.ThingCategory.Plant => P.ThingCategory.Plant,
            PbMirror.ThingCategory.Filth => P.ThingCategory.Filth,
            PbMirror.ThingCategory.Corpse => P.ThingCategory.Corpse,
            PbMirror.ThingCategory.Other => P.ThingCategory.Other,
            _ => throw Failure("mirror cell grid thing category"),
        };
        if ((int)wire.Faction < 0 || (int)wire.Faction > (int)PbMirror.ThingFaction.Hostile)
        {
            throw Failure("mirror cell grid thing faction");
        }
        if ((wire.Flags & ~(uint)P.ThingMarks.Known) != 0)
        {
            throw Failure("mirror cell grid thing flags");
        }
        var thing = new P.Thing
        {
            Def = def,
            Category = category,
            Faction = (P.ThingFaction)(int)wire.Faction,
            Flags = (P.ThingMarks)wire.Flags,
            Id = wire.Id,
            Count = wire.Count,
        };
        var mismatch = "mirror cell grid thing state does not match its category";
        switch (wire.State)
        {
            case null:
                if (category != P.ThingCategory.Other)
                {
                    throw Failure(mismatch);
                }
                return thing;
            case PbMirror.ThingState.PlantCase { Value: var plant }:
                if (category != P.ThingCategory.Plant || plant is null || !float.IsFinite(plant.Growth))
                {
                    throw Failure(mismatch);
                }
                return thing with { Plant = new P.PlantState { Growth = plant.Growth, Blighted = plant.Blighted } };
            case PbMirror.ThingState.CorpseCase { Value: var corpse }:
                if (category != P.ThingCategory.Corpse || corpse is null || !float.IsFinite(corpse.Rot) || (int)corpse.Class < 0 || (int)corpse.Class > (int)PbMirror.CorpseClass.Animal)
                {
                    throw Failure(mismatch);
                }
                return thing with { Corpse = new P.CorpseState { Class = (P.CorpseClass)(int)corpse.Class, Rot = corpse.Rot } };
            case PbMirror.ThingState.FilthCase { Value: var filth }:
                if (category != P.ThingCategory.Filth || filth is null)
                {
                    throw Failure(mismatch);
                }
                return thing with { FilthThickness = filth.Thickness };
            case PbMirror.ThingState.ItemCase { Value: var item }:
                if (category != P.ThingCategory.Item || item is null || !float.IsFinite(item.Deterioration))
                {
                    throw Failure(mismatch);
                }
                return thing with { ItemDeterioration = item.Deterioration };
            case PbMirror.ThingState.BuildingCase { Value: var building }:
                if (category != P.ThingCategory.Building || building is null)
                {
                    throw Failure(mismatch);
                }
                var needed = new List<P.Material>(building.Needed.Count);
                foreach (var need in building.Needed)
                {
                    needed.Add(new P.Material(Str(need.Def), need.Count));
                }
                var casket = new List<string>(building.Casket.Count);
                foreach (var held in building.Casket)
                {
                    casket.Add(Str(held));
                }
                return thing with
                {
                    Building = new P.BuildingState
                    {
                        HitPoints = building.HitPoints,
                        Burning = building.Burning,
                        Reserved = building.Reserved,
                        Needed = EquatableArray.From(needed),
                        Casket = EquatableArray.From(casket),
                    },
                };
            default:
                throw Failure(mismatch);
        }
    }
}

internal sealed partial class FacadeNativeClient
{
    /// <summary>Go <c>gridFrameMethod</c>: keys a frame's grid, frames-only; the reply is the frame's context.</summary>
    private const string GridFrameMethod = "rimgovernor/snapshot_frame_grid";

    private const string GetCellsMethod = "rimgovernor/observations_get_cells";

    /// <summary>
    /// Go <c>cellsRead</c>: one validated <c>observations_get_cells</c> read, the map's bounds and the rect as a decoded
    /// grid.
    /// </summary>
    internal sealed record CellsRead(PbCommon.ObservationContext? Context, P.Bounds Bounds, FrameCellGrid Grid);

    /// <summary>
    /// Go <c>Client.ReadMapBounds</c>: reads the one-cell grid at a caller-observed anchor for the map's dimensions. A
    /// failed, incomplete or mismatched observation never supplies default bounds. Bounds never change, so the read
    /// asks any frame (Go <c>WithAnyFrame</c>); get_cells is no frame-served read and always goes to the facade.
    /// </summary>
    internal async Task<(MapBounds Value, BridgeResult Receipt)> ReadMapBoundsAsync(PbCommon.Identity identity, Cell anchor, CancellationToken cancellationToken)
    {
        BridgeFunctions.AuthorityIdentity(identity);
        if (anchor.X < 0 || anchor.Z < 0)
        {
            throw BridgeFunctions.Contract("negative bounds anchor");
        }
        var read = await ReadCellsAsync(identity, new P.Rectangle(anchor.X, anchor.Z, 1, 1), cancellationToken).ConfigureAwait(false);
        return (new MapBounds { Context = read.Context, Bounds = read.Bounds }, NoReceipt);
    }

    /// <summary>
    /// Go <c>Client.ReadPlanningWindow</c>: the planning window rect from the snapshot stream's whole-map grid; a
    /// client without a stream serves none (unavailable) and the caller falls back to whatever window it holds.
    /// </summary>
    internal async Task<(PlanningWindow Value, BridgeResult Receipt)> ReadPlanningWindowAsync(PbCommon.Identity identity, P.Rectangle rect, CancellationToken cancellationToken)
    {
        BridgeFunctions.AuthorityIdentity(identity);
        if (rect.X < 0 || rect.Z < 0 || rect.Width < 1 || rect.Height < 1)
        {
            throw BridgeFunctions.Contract("invalid planning window rect");
        }
        RequireFrames("the planning window");
        var served = await FrameReadAsync(
            GridFrameMethod,
            identity,
            fallback: false,
            anyFrame: false,
            view => new FrameLookup<FrameView>(view.Snapshot.Grid is not null, view, view.Snapshot.Grid is not null),
            cancellationToken).ConfigureAwait(false);
        var frame = served.Value!;
        var held = frame.Grid;
        if (held.Error is { } error)
        {
            throw error;
        }
        if (held.Grid is null || !BridgeFunctions.SameIdentity(frame.Snapshot.Context?.Identity, identity))
        {
            throw new BridgeUnavailableException("no snapshot frame grid for this world");
        }
        var (cells, fogged) = held.Grid.Window(rect);
        return (new PlanningWindow(frame.Snapshot.Context, rect, cells, fogged), NoReceipt);
    }

    /// <summary>
    /// Go <c>RoomCells</c> (temperature_rooms.go): resolves each room of the census to its cells in the frame grid,
    /// the held cells carrying its grid_room key, row-major. A room without a key, or whose key the grid does not
    /// carry, is absent.
    /// </summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<Cell>> RoomCellsOf(PbObservations.RoomsSnapshot? rooms, FrameCellGrid? grid)
    {
        var result = new Dictionary<string, IReadOnlyList<Cell>>(StringComparer.Ordinal);
        if (rooms is null || grid is null)
        {
            return result;
        }
        var byKey = grid.CellsByRoom();
        foreach (var room in rooms.Rooms)
        {
            if (room.GridRoom is not null && byKey.TryGetValue(room.GridRoom, out var cells) && cells.Count > 0)
            {
                result[room.Id ?? ""] = cells;
            }
        }
        return result;
    }

    /// <summary>
    /// Go <c>Client.readCells</c>: reads <paramref name="rect"/> through <c>observations_get_cells</c> and validates
    /// the reply against the request.
    /// </summary>
    internal async Task<CellsRead> ReadCellsAsync(PbCommon.Identity identity, P.Rectangle rect, CancellationToken cancellationToken)
    {
        BridgeFunctions.AuthorityIdentity(identity);
        if (rect.X < 0 || rect.Z < 0 || rect.Width < 1 || rect.Height < 1 || (long)rect.Width * rect.Height > FrameCellGrid.MaxCells)
        {
            throw BridgeFunctions.Contract("invalid cells rect");
        }
        var request = new PbObservations.GetCellsRequest
        {
            Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
            Rectangle = new PbObservations.Rectangle
            {
                Minimum = new PbCommon.Cell { X = rect.X, Z = rect.Z },
                Maximum = new PbCommon.Cell { X = rect.X + rect.Width - 1, Z = rect.Z + rect.Height - 1 },
            },
        };
        var reply = await Facade.GetCellsAsync(request, cancellationToken).ConfigureAwait(false);
        var outcome = reply.Outcome ?? throw BridgeFunctions.Contract("missing cells outcome");
        if (outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }
        if (outcome.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }
        if (outcome.Observed is not { } observed)
        {
            throw BridgeFunctions.Contract("missing cells outcome");
        }
        return ValidateCells(observed, identity, rect);
    }

    /// <summary>Go <c>validateCells</c>: checks a cells snapshot answers the request: its world, a map holding the rect and a keyframe grid over exactly the rect.</summary>
    internal static CellsRead ValidateCells(PbObservations.CellsSnapshot? snapshot, PbCommon.Identity identity, P.Rectangle rect)
    {
        if (snapshot is null)
        {
            throw BridgeFunctions.Contract("missing cells snapshot");
        }
        BridgeFunctions.ValidateContext(snapshot.Context);
        if (!BridgeFunctions.SameIdentity(snapshot.Context!.Identity, identity))
        {
            throw BridgeFunctions.Contract("cells identity mismatch");
        }
        var size = snapshot.MapSize;
        if (size is null || size.Width is null || size.Height is null || size.Width.GetValueOrDefault() == 0 || size.Height.GetValueOrDefault() == 0
            || size.Width.GetValueOrDefault() > int.MaxValue || size.Height.GetValueOrDefault() > int.MaxValue)
        {
            throw BridgeFunctions.Contract("invalid map dimensions");
        }
        var bounds = new P.Bounds((int)size.Width.GetValueOrDefault(), (int)size.Height.GetValueOrDefault());
        if ((long)rect.X + rect.Width > bounds.Width || (long)rect.Z + rect.Height > bounds.Height)
        {
            throw BridgeFunctions.Contract("cells rect off the map");
        }
        if (snapshot.Grid is null || !FrameCellGrid.Complete(snapshot.Grid))
        {
            throw BridgeFunctions.Contract("cells grid is not a keyframe");
        }
        var grid = FrameCellGrid.Apply(snapshot.Grid);
        if (grid.Rect != rect)
        {
            throw BridgeFunctions.Contract("cells grid rect differs");
        }
        return new CellsRead(snapshot.Context, bounds, grid);
    }
}
