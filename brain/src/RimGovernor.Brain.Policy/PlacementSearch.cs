using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Policy;

public enum PlacementEnvironment
{
    [WireName("")]
    None = 0,
    Anywhere,
    Indoors,
    Outdoors,
}

public sealed record PlacementSearchRequest
{
    public GenerationSnapshot Snapshot { get; init; }

    public Tick Tick { get; init; }

    public Bounds Bounds { get; init; }

    public Cell Center { get; init; }

    public IReadOnlyList<SiteCell> Cells { get; init; } = [];

    public IReadOnlyList<Cell> Protected { get; init; } = [];

    public PlacementEnvironment Environment { get; init; }

    public int Radius { get; init; }

    public int Limit { get; init; }

    /// <summary>
    /// The ids of the native stockpile zones. A building may stand on a stockpile cell (the game shrinks the
    /// zone), so such a cell counts as free; any other zone, or a zone of unknown kind, still blocks.
    /// </summary>
    public IReadOnlyDictionary<string, bool> StockpileZones { get; init; } = new Dictionary<string, bool>();

    /// <summary>When non-null, limits the sites to these cells; footprints may still cover any free cell.</summary>
    public IReadOnlyList<Cell>? Anchors { get; init; }
}

/// <summary>Explains why Select chose a footprint: Distance is the anchor's distance from the search center in cells, and Score equals it.</summary>
public sealed record PlacementScore(Cell Anchor, double Distance, double Score);

/// <summary>
/// A bounded native-grounded proposal set. It owns a copy of free geometry; callers cannot widen it after
/// native previews are requested.
/// </summary>
public sealed class PlacementSearch
{
    private readonly GenerationSnapshot _snapshot;
    private readonly Cell _center;
    private readonly List<Cell> _sites = [];
    private readonly HashSet<Cell> _free = [];

    /// <summary>
    /// Ports development.placement's nearest-cell ordering and bounded search. Unknown occupancy/zone/room
    /// facts never become free space. Throws DomainException on an invalid request.
    /// </summary>
    public PlacementSearch(PlacementSearchRequest r)
    {
        if (!r.Snapshot.IsValid || r.Tick.Value < 0 || r.Bounds.Width <= 0 || r.Bounds.Height <= 0 || r.Bounds.Width > 4096
            || r.Bounds.Height > 4096 || r.Radius < 0 || r.Radius > 4096 || r.Limit < 1 || r.Limit > 64)
        {
            throw new DomainException("invalid placement search");
        }
        if (r.Environment is not (PlacementEnvironment.Anywhere or PlacementEnvironment.Indoors or PlacementEnvironment.Outdoors))
        {
            throw new DomainException("invalid placement environment");
        }
        bool Inside(Cell c) => c.X >= 0 && c.Z >= 0 && c.X < r.Bounds.Width && c.Z < r.Bounds.Height;
        if (!Inside(r.Center))
        {
            throw new DomainException("invalid placement center");
        }
        var blocked = new HashSet<Cell>();
        foreach (var c in r.Protected)
        {
            if (!Inside(c))
            {
                throw new DomainException("protected placement cell out of bounds");
            }
            blocked.Add(c);
        }
        _snapshot = r.Snapshot;
        _center = r.Center;
        var anchors = r.Anchors is null ? null : new HashSet<Cell>(r.Anchors);
        var seen = new HashSet<Cell>();
        foreach (var row in r.Cells)
        {
            var c = row.Cell;
            if (!Inside(c) || !seen.Add(c))
            {
                throw new DomainException("invalid placement cell census");
            }
            var zoneKnown = row.Zone.TryGetValue(out var zone);
            if (blocked.Contains(c) || !Positive(row.Walkable) || row.Occupied() || !zoneKnown || (zone && !StockpileCell(row, r.StockpileZones)))
            {
                continue;
            }
            if (r.Environment != PlacementEnvironment.Anywhere)
            {
                if (!row.Indoors.TryGetValue(out var indoors) || indoors != (r.Environment == PlacementEnvironment.Indoors))
                {
                    continue;
                }
            }
            _free.Add(c);
            if (anchors is not null && !anchors.Contains(c))
            {
                continue;
            }
            if (c.X >= r.Center.X - r.Radius && c.X <= r.Center.X + r.Radius && c.Z >= r.Center.Z - r.Radius && c.Z <= r.Center.Z + r.Radius)
            {
                _sites.Add(c);
            }
        }
        _sites.Sort((a, b) =>
        {
            long da = SquaredDistance(a, r.Center), db = SquaredDistance(b, r.Center);
            if (da != db)
            {
                return da < db ? -1 : 1;
            }
            return CellLess(a, b) ? -1 : CellLess(b, a) ? 1 : 0;
        });
        if (_sites.Count > r.Limit)
        {
            _sites.RemoveRange(r.Limit, _sites.Count - r.Limit);
        }
    }

    public IReadOnlyList<Cell> Candidates() => [.. _sites];

    /// <summary>
    /// Chooses the fully inspected footprint for the requested native definition/stuff nearest the search
    /// center; ties fall to the nearest-site rank, then the lowest action id. Resource admission and
    /// authoritative competing holds still belong to the shared method store; a selected preview grants no
    /// execution. Null when no preview qualifies; throws DomainException on a preview that differs from the
    /// search.
    /// </summary>
    public Preview? Select(string definition, string stuff, IReadOnlyList<Preview> previews) =>
        SelectScored(definition, stuff, previews)?.Selected;

    /// <summary>The score Select gives a footprint anchored at the cell, for logs and tests.</summary>
    public PlacementScore Score(Cell anchor, IReadOnlyList<Cell> footprint)
    {
        var distance = Math.Sqrt(SquaredDistance(anchor, _center));
        return new PlacementScore(anchor, distance, distance);
    }

    /// <summary>Select with the chosen footprint's score.</summary>
    public (Preview Selected, PlacementScore Score)? SelectScored(string definition, string stuff, IReadOnlyList<Preview> previews)
    {
        _ = new Domain.Building(definition, default, Rotation.North, stuff);
        var ranks = new Dictionary<Cell, int>();
        for (var i = 0; i < _sites.Count; i++)
        {
            ranks[_sites[i]] = i;
        }
        var seen = new HashSet<ActionId>();
        Preview? selected = null;
        PlacementScore? chosen = null;
        var best = _sites.Count;
        foreach (var p in previews)
        {
            var b = p.Action.Building;
            if (b is null || b.Definition != definition || b.Stuff != stuff || !seen.Add(p.Action.Id) || !p.Snapshot.Matches(_snapshot))
            {
                throw new DomainException("placement preview differs from search");
            }
            if (!ranks.TryGetValue(b.Cell, out var rank))
            {
                throw new DomainException("unrequested placement anchor");
            }
            if (!Positive(p.CanPlace) || !Positive(p.SafeToPlace) || !p.Footprint.TryGetValue(out var cells) || cells.Count == 0)
            {
                continue;
            }
            bool valid = true, anchor = false;
            var used = new HashSet<Cell>();
            foreach (var c in cells)
            {
                if (!used.Add(c))
                {
                    throw new DomainException("duplicate placement footprint cell");
                }
                valid = valid && _free.Contains(c);
                anchor = anchor || c == b.Cell;
            }
            if (!valid || !anchor)
            {
                continue;
            }
            var score = Score(b.Cell, cells);
            if (best == _sites.Count || score.Score < chosen!.Score
                || (score.Score == chosen.Score && (rank < best || (rank == best && p.Action.Id.CompareTo(selected!.Action.Id) < 0))))
            {
                (selected, chosen) = (p, score);
                best = rank;
            }
        }
        if (best == _sites.Count)
        {
            return null;
        }
        // Detach the winner's lists from the caller's preview.
        selected!.Footprint.TryGetValue(out var footprint);
        var result = selected with { Footprint = Known(EquatableArray.From(footprint!)) };
        if (result.Costs.TryGetValue(out var costs))
        {
            result = result with { Costs = Known<EquatableArray<Amount>>([.. costs]) };
        }
        return (result, chosen!);
    }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Lists the four cells beside every observed doorway so indoor furnishing never blocks a room's
    /// entrance from either side; interaction cells of the furniture itself are the native preview's to
    /// check.
    /// </summary>
    public static IReadOnlyList<Cell> DoorwayAisles(Bounds bounds, IReadOnlyList<SiteCell> cells)
    {
        var aisles = new List<Cell>();
        foreach (var row in cells)
        {
            if (!Positive(row.Doorway))
            {
                continue;
            }
            var c = row.Cell;
            foreach (var n in new[] { new Cell(c.X + 1, c.Z), new Cell(c.X - 1, c.Z), new Cell(c.X, c.Z + 1), new Cell(c.X, c.Z - 1) })
            {
                if (n.X >= 0 && n.Z >= 0 && n.X < bounds.Width && n.Z < bounds.Height)
                {
                    aisles.Add(n);
                }
            }
        }
        return aisles;
    }

    /// <summary>The bounding rectangle of a non-empty cell set.</summary>
    internal static Rectangle CellsRectangle(IReadOnlyList<Cell> cells)
    {
        int minX = cells[0].X, minZ = cells[0].Z, maxX = cells[0].X, maxZ = cells[0].Z;
        for (var i = 1; i < cells.Count; i++)
        {
            var c = cells[i];
            minX = Math.Min(minX, c.X);
            maxX = Math.Max(maxX, c.X);
            minZ = Math.Min(minZ, c.Z);
            maxZ = Math.Max(maxZ, c.Z);
        }
        return new Rectangle(minX, minZ, maxX - minX + 1, maxZ - minZ + 1);
    }

    /// <summary>Reports whether the cell's zone is a known stockpile.</summary>
    internal static bool StockpileCell(SiteCell row, IReadOnlyDictionary<string, bool> stockpiles) =>
        row.ZoneId.TryGetValue(out var id) && stockpiles.GetValueOrDefault(id);
}
