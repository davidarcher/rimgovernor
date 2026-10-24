using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The planners' reads of a frame's cell grid (<see cref="PbMirror.CellGrid"/>), straight from its typed arrays. The
/// grid is never changed once published, so the last window is memoised: the planners ask the same rect several
/// times a frame.
/// </summary>
internal sealed class FrameCellGrid(PbMirror.CellGrid grid)
{
    private readonly object _memoGate = new();
    private P.Rectangle _memoRect;
    private bool _memoBuilt;
    private IReadOnlyList<P.SiteCell> _memoCells = [];
    private ulong _memoFogged;

    /// <summary>The rectangle the grid covers.</summary>
    internal P.Rectangle Rect { get; } = new(grid.X, grid.Z, grid.Width, grid.Height);

    /// <summary>
    /// The held cells inside <paramref name="rect"/> (clipped to the grid), row-major, and the
    /// count of the rect's cells the grid does not hold (fogged).
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
    /// The held cells of each of <paramref name="keys"/>' room keys, row-major within a key; a key no held cell
    /// carries is absent. Each key's cells are counted first, so its array is built at its size.
    /// </summary>
    internal Dictionary<int, Cell[]> CellsByRoom(IReadOnlyCollection<int> keys)
    {
        var counts = new Dictionary<int, int>(keys.Count);
        foreach (var key in keys)
        {
            counts[key] = 0;
        }
        for (var j = 0; j < grid.Held.Length; j++)
        {
            if (grid.Held[j] && grid.Room[j] is { } key && counts.TryGetValue(key, out var n))
            {
                counts[key] = n + 1;
            }
        }
        var result = new Dictionary<int, Cell[]>(counts.Count);
        foreach (var (key, n) in counts)
        {
            if (n > 0)
            {
                result[key] = new Cell[n];
            }
        }
        var filled = new Dictionary<int, int>(result.Count);
        for (var j = 0; j < grid.Held.Length; j++)
        {
            if (grid.Held[j] && grid.Room[j] is { } key && result.TryGetValue(key, out var cells))
            {
                var at = filled.GetValueOrDefault(key);
                cells[at] = new Cell(Rect.X + (j % Rect.Width), Rect.Z + (j / Rect.Width));
                filled[key] = at + 1;
            }
        }
        return result;
    }

    private (IReadOnlyList<P.SiteCell> Cells, ulong Fogged) BuildWindow(P.Rectangle rect)
    {
        var minX = Math.Max(rect.X, Rect.X);
        var minZ = Math.Max(rect.Z, Rect.Z);
        var maxX = Math.Min(rect.X + rect.Width, Rect.X + Rect.Width);
        var maxZ = Math.Min(rect.Z + rect.Height, Rect.Z + Rect.Height);
        var cells = new List<P.SiteCell>();
        ulong fogged = 0;
        for (var z = minZ; z < maxZ; z++)
        {
            for (var x = minX; x < maxX; x++)
            {
                var j = ((z - Rect.Z) * Rect.Width) + (x - Rect.X);
                if (!grid.Held[j])
                {
                    fogged++;
                    continue;
                }
                cells.Add(CellAt(j, new Cell(x, z)));
            }
        }
        return (cells, fogged);
    }

    private P.SiteCell CellAt(int j, Cell cell) =>
        new()
        {
            Cell = cell,
            Walkable = Fact.Known(grid.Walkable[j]),
            Zone = Fact.Known(grid.ZoneId[j] is not null),
            Roofed = Fact.Known(grid.Roof[j] is not null),
            Indoors = Fact.Known(grid.Indoors[j]),
            StorageStacks = Fact.Known(grid.StorageStacks[j]),
            Doorway = Fact.Known(grid.Doorway[j]),
            Fertility = Fact.Known((double)grid.Fertility[j]),
            Polluted = Fact.Known(grid.Polluted[j]),
            Glow = Fact.Known((double)grid.Glow[j]),
            Roof = Present(grid.Roof[j]),
            ZoneId = Present(grid.ZoneId[j]),
            Room = grid.Room[j] is { } room ? Fact.Known(room) : Fact.Unknown<int>(),
            Terrain = Fact.Known(grid.Terrain[j]),
            InHome = Fact.Known(grid.InHome[j]),
            BaseTerrain = Fact.Known(grid.BaseTerrain[j]),
            FoundationRemoval = Fact.Known(grid.FoundationRemoval[j]),
            StorageCapacity = Fact.Known(grid.StorageCapacity[j]),
            Things = grid.Things[j] ?? [],
        };

    // A roof or zone the cell lacks is no fact about one.
    private static Fact<string> Present(string? value) => value is null ? Fact.Unknown<string>() : Fact.Known(value);
}

public sealed partial class FacadeNativeClient
{
    /// <summary>Keys a frame's grid, frames-only; the reply is the frame's context.</summary>
    private const string GridFrameMethod = "rimgovernor/snapshot_frame_grid";

    /// <summary>
    /// Reads the one-cell grid at a caller-observed anchor for the map's dimensions. A
    /// failed or unavailable observation throws; it never supplies default bounds.
    /// get_cells is no frame-served read and always goes to the facade.
    /// </summary>
    internal async Task<MapBounds> ReadMapBoundsAsync(PbCommon.Identity identity, Cell anchor, CancellationToken cancellationToken)
    {
        if (anchor.X < 0 || anchor.Z < 0)
        {
            throw BridgeContractException.Contract("negative bounds anchor");
        }
        var rect = new P.Rectangle(anchor.X, anchor.Z, 1, 1);
        var request = new PbObservations.GetCellsRequest
        {
            Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
            Rectangle = new PbObservations.Rectangle
            {
                Minimum = new PbCommon.Cell { X = rect.X, Z = rect.Z },
                Maximum = new PbCommon.Cell { X = rect.X + rect.Width - 1, Z = rect.Z + rect.Height - 1 },
            },
        };
        var observed = await Facade.GetCellsAsync(request, cancellationToken).ConfigureAwait(false);
        return new MapBounds { Context = observed.Context, Bounds = new P.Bounds((int)observed.MapSize.Width, (int)observed.MapSize.Height) };
    }

    /// <summary>
    /// The planning window rect from the snapshot stream's whole-map grid; a
    /// frame read that finds none is unavailable and the caller falls back to whatever window it holds.
    /// </summary>
    internal async Task<PlanningWindowRead> ReadPlanningWindowAsync(PbCommon.Identity identity, P.Rectangle rect, CancellationToken cancellationToken)
    {
        if (rect.X < 0 || rect.Z < 0 || rect.Width < 1 || rect.Height < 1)
        {
            throw BridgeContractException.Contract("invalid planning window rect");
        }
        var frame = await FrameReadAsync(GridFrameMethod, identity, view => view.Snapshot.Grid is not null ? view : null, cancellationToken).ConfigureAwait(false);
        if (frame.Grid is not { } grid || !WireValidation.SameIdentity(frame.Snapshot.Context?.Identity, identity))
        {
            throw new BridgeUnavailableException("no snapshot frame grid for this world");
        }
        var (cells, fogged) = grid.Window(rect);
        return new PlanningWindowRead(frame.Snapshot.Context, rect, cells, fogged);
    }

    /// <summary>
    /// Resolves each room of the census to its cells in the frame grid,
    /// the held cells carrying its grid room key, row-major. A room whose key the grid does not carry (every cell
    /// fogged) is absent.
    /// </summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<Cell>> RoomCellsOf(PbObservations.RoomsSnapshot? rooms, FrameCellGrid? grid)
    {
        var result = new Dictionary<string, IReadOnlyList<Cell>>(StringComparer.Ordinal);
        if (rooms is null || grid is null)
        {
            return result;
        }
        var byKey = grid.CellsByRoom(rooms.Rooms.Select(room => room.GridRoom).ToHashSet());
        foreach (var room in rooms.Rooms)
        {
            if (byKey.TryGetValue(room.GridRoom, out var cells))
            {
                result[room.Id] = cells;
            }
        }
        return result;
    }
}
