using System.Globalization;

namespace RimGovernor.Brain.Policy;

// Go: tunnel_site.go. Tunnel siting: a 1-wide corridor driven into a visible natural rock face from a walkable
// access cell, ending beside a buried ore deposit that mining then opens. Dug rooms are the layout plan's; this only
// reaches ore.
//
// Fogged cells are unknown, never assumed empty or safe: a corridor may run through fogged space, but any known cell
// inside or around it must be rock. Native ReadExcavationSite remains the authority for roof support and per-cell
// mining legality; this file only proposes geometry.

public sealed record ExcavationSiteRequest
{
    public Bounds Bounds { get; init; }

    /// <summary>The observed window: a cell absent from Cells inside it is fogged (unknown); outside it nothing was read, so no target may touch it.</summary>
    public Rectangle Region { get; init; }

    public Cell Anchor { get; init; }

    public EquatableArray<SiteCell> Cells { get; init; }

    public EquatableArray<Cell> Protected { get; init; }

    /// <summary>MinCorridor..MaxCorridor bound the corridor length tried per face.</summary>
    public int MinCorridor { get; init; }

    public int MaxCorridor { get; init; }

    /// <summary>The game's roof support radius (the catalog's roof_max_support_distance): every target cell must lie within it of some non-target cell.</summary>
    public double RoofSupport { get; init; }

    /// <summary>The load's roof rules: a roofed cell whose def is not in them fails the site read as an unknown roof.</summary>
    public IReadOnlyDictionary<string, RoofRule> Roofs { get; init; } = new Dictionary<string, RoofRule>();
}

/// <summary>
/// One proposed tunnel: Access is the walkable cell the corridor starts from, Corridor runs inward from the face
/// (Corridor[0] is the face cell) and Door is its last cell, beside the ore.
/// </summary>
public sealed record ExcavationTarget
{
    public Cell Access { get; init; }

    public Cell Direction { get; init; }

    public EquatableArray<Cell> Corridor { get; init; }

    public Cell Door { get; init; }

    public long Score { get; init; }

    /// <summary>The ordered target, face first, so a frontier walk digs the way a miner would.</summary>
    public IReadOnlyList<Cell> Cells() => Corridor.Items.ToList();

    /// <summary>
    /// A compact, reversible identity for the target so per-stage plan identities survive restarts and cleared cells
    /// without any extra table: "accessX.accessZ.dirX.dirZ.length".
    /// </summary>
    public string Key() => string.Create(CultureInfo.InvariantCulture, $"{Access.X}.{Access.Z}.{Direction.X}.{Direction.Z}.{Corridor.Count}");
}

public static partial class PolicyFunctions
{
    /// <summary>Matches NativeExcavationSite.MaxCells so a whole target fits in one site read.</summary>
    private const int ExcavationMaxCells = 64;

    /// <summary>
    /// Rebuilds a target from Key. It validates shape only; the caller re-reads the site before trusting it, and
    /// the native site read is the authority on roof support. Throws on an invalid key.
    /// </summary>
    public static ExcavationTarget ParseExcavationKey(string key)
    {
        var parts = key.Split('.');
        if (parts.Length != 5)
        {
            throw new DomainException("invalid excavation key");
        }
        var v = new int[5];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v[i]) || v[i].ToString(CultureInfo.InvariantCulture) != parts[i])
            {
                throw new DomainException("invalid excavation key");
            }
        }
        var access = new Cell(v[0], v[1]);
        var direction = new Cell(v[2], v[3]);
        var length = v[4];
        if ((direction.X == 0) == (direction.Z == 0) || direction.X < -1 || direction.X > 1 || direction.Z < -1 || direction.Z > 1 || length <= 0 || length > ExcavationMaxCells || access.X < 0 || access.Z < 0)
        {
            throw new DomainException("invalid excavation key");
        }
        var corridor = new List<Cell>(length);
        for (var i = 1; i <= length; i++)
        {
            corridor.Add(new Cell(access.X + (direction.X * i), access.Z + (direction.Z * i)));
        }
        if (corridor[0].X < 0 || corridor[0].Z < 0)
        {
            throw new DomainException("invalid excavation key");
        }
        return new ExcavationTarget { Access = access, Direction = direction, Corridor = EquatableArray.From(corridor), Door = corridor[^1] };
    }

    // Whether every target cell lies within RimWorld's roof support radius of some cell outside the target, so the
    // rock left standing supports the roof over the whole dig.
    private static bool ExcavationSupported(IReadOnlyList<Cell> cells, double support)
    {
        var inside = new HashSet<Cell>(cells);
        var r = (int)support;
        foreach (var c in cells)
        {
            var held = false;
            for (var dz = -r; dz <= r && !held; dz++)
            {
                for (var dx = -r; dx <= r; dx++)
                {
                    if ((double)((dx * dx) + (dz * dz)) <= support * support && !inside.Contains(new Cell(c.X + dx, c.Z + dz)))
                    {
                        held = true;
                        break;
                    }
                }
            }
            if (!held)
            {
                return false;
            }
        }
        return true;
    }

    // A visible natural rock cell: solid, impassable natural rock, roofed or not (the rim of a mountain carries no
    // roof, and a miner reaches the rock behind it only by mining it).
    private static bool RockCell(SiteCell c) => c.Occupied() && c.Walkable.TryGetValue(out var walkable) && !walkable && c.NaturalRock();

    // Open ground still under a natural rock roof that is not part of a proper room: rock that has already been dug
    // (or a natural pocket), so a half-dug tunnel is re-planned over the same cells. Only a natural roof marks a cell
    // as part of a mountain, so a constructed wall under a built roof is never mistaken for a rock face.
    private static bool ExcavatedCell(SiteCell c, IReadOnlyDictionary<string, RoofRule> roofs)
    {
        var wk = c.Walkable.TryGetValue(out var walkable);
        var rk = c.Roof.TryGetValue(out var roof);
        var ik = c.Indoors.TryGetValue(out var indoors);
        return !c.Occupied() && wk && walkable && rk && roofs.GetValueOrDefault(roof!).Natural && (!ik || !indoors);
    }

    private static readonly Cell[] ExcavationDirections = [new Cell(0, 1), new Cell(1, 0), new Cell(0, -1), new Cell(-1, 0)];

    /// <summary>
    /// Proposes corridor targets whose door is cardinally adjacent to the buried ore cell, best first. A target is
    /// rejected when any known cell inside it is not rock, when any known cell cardinally bordering it (other than
    /// the access cell) is passable, or when it touches the map edge. Throws on an invalid request.
    /// </summary>
    public static IReadOnlyList<ExcavationTarget> CorridorExcavationSites(ExcavationSiteRequest r, Cell ore)
    {
        if (r.Bounds.Width <= 0 || r.Bounds.Height <= 0 || r.Bounds.Width > 4096 || r.Bounds.Height > 4096)
        {
            throw new DomainException("invalid excavation site bounds");
        }
        if (r.MinCorridor <= 0 || r.MaxCorridor < r.MinCorridor || r.MaxCorridor > ExcavationMaxCells)
        {
            throw new DomainException("invalid excavation corridor");
        }
        if (!(r.RoofSupport > 0))
        {
            throw new DomainException("invalid excavation roof support distance");
        }
        bool InBounds(Cell c) => c.X >= 0 && c.Z >= 0 && c.X < r.Bounds.Width && c.Z < r.Bounds.Height;
        if (r.Region.Width <= 0 || r.Region.Height <= 0 || !InBounds(new Cell(r.Region.X, r.Region.Z)) || !InBounds(new Cell(r.Region.X + r.Region.Width - 1, r.Region.Z + r.Region.Height - 1)))
        {
            throw new DomainException("invalid observed region");
        }
        bool Observed(Cell c) => c.X >= r.Region.X && c.Z >= r.Region.Z && c.X < r.Region.X + r.Region.Width && c.Z < r.Region.Z + r.Region.Height;

        // Digging against the map edge leaves no rock to hold the roof there.
        bool Interior(Cell c) => Observed(c) && c.X >= 1 && c.Z >= 1 && c.X < r.Bounds.Width - 1 && c.Z < r.Bounds.Height - 1;
        if (!InBounds(r.Anchor))
        {
            throw new DomainException("invalid colony anchor");
        }
        var cells = new Dictionary<Cell, SiteCell>(r.Cells.Count);
        var ordered = new List<Cell>(r.Cells.Count);
        foreach (var c in r.Cells)
        {
            if (!InBounds(c.Cell))
            {
                throw new DomainException("site cell out of bounds");
            }
            if (cells.ContainsKey(c.Cell))
            {
                throw new DomainException("duplicate site cell");
            }
            if (c.Roof.TryGetValue(out var roof) && roof.Length != 0 && !r.Roofs.ContainsKey(roof))
            {
                throw new DomainException($"unknown roof def \"{roof}\" at {c.Cell}");
            }
            cells[c.Cell] = c;
            ordered.Add(c.Cell);
        }
        ordered.Sort((a, b) => CellLess(a, b) ? -1 : CellLess(b, a) ? 1 : 0);
        var protectedCells = new HashSet<Cell>();
        foreach (var c in r.Protected)
        {
            if (!InBounds(c))
            {
                throw new DomainException("protected cell out of bounds");
            }
            protectedCells.Add(c);
        }
        bool Free(Cell p) => cells.TryGetValue(p, out var c) && !protectedCells.Contains(p) && Positive(c.Walkable) && !c.Occupied();

        // Diggable: unknown (fogged), visible rock or already excavated, never protected.
        bool Diggable(Cell p)
        {
            if (!Interior(p) || protectedCells.Contains(p))
            {
                return false;
            }
            return !cells.TryGetValue(p, out var c) || RockCell(c) || ExcavatedCell(c, r.Roofs);
        }

        // Sealed: a bordering cell that is unknown or known impassable keeps the tunnel enclosed once dug.
        bool Sealed(Cell p)
        {
            if (!InBounds(p) || !Observed(p))
            {
                return false;
            }
            if (!cells.TryGetValue(p, out var c))
            {
                return true;
            }
            return c.Walkable.TryGetValue(out var walkable) && !walkable;
        }

        // Judge accepts one corridor when every cell is diggable, at least one is rock, and its cardinal neighbours
        // are sealed; it returns the score.
        (long Score, bool Ok) Judge(ExcavationTarget t)
        {
            var inside = new HashSet<Cell>();
            var work = false;
            long done = 0;
            foreach (var p in t.Corridor)
            {
                if (!Diggable(p))
                {
                    return (0, false);
                }
                inside.Add(p);
                if (!cells.TryGetValue(p, out var c) || RockCell(c))
                {
                    work = true;
                }
                else
                {
                    done++;
                }
            }

            // An already-open corridor is not an excavation.
            if (!work)
            {
                return (0, false);
            }
            foreach (var p in t.Corridor)
            {
                foreach (var d in ExcavationDirections)
                {
                    var n = new Cell(p.X + d.X, p.Z + d.Z);
                    if (inside.Contains(n) || n == t.Access || n == ore)
                    {
                        continue;
                    }
                    if (!Sealed(n))
                    {
                        return (0, false);
                    }
                }
            }

            // Work already done outweighs distance: a half-dug tunnel wins over a fresh face a cell nearer.
            return (SquaredDistance(t.Door, r.Anchor) + (4 * (long)t.Corridor.Count) - (done * 4096), true);
        }

        var targets = new List<ExcavationTarget>();
        foreach (var access in ordered)
        {
            if (!Free(access))
            {
                continue;
            }
            foreach (var d in ExcavationDirections)
            {
                var face = new Cell(access.X + d.X, access.Z + d.Z);
                if (!cells.TryGetValue(face, out var fc) || !(RockCell(fc) || ExcavatedCell(fc, r.Roofs)))
                {
                    continue;
                }
                for (var length = r.MinCorridor; length <= r.MaxCorridor; length++)
                {
                    var corridor = new List<Cell>(length);
                    var through = false;
                    for (var i = 1; i <= length; i++)
                    {
                        var p = new Cell(access.X + (d.X * i), access.Z + (d.Z * i));
                        through = through || p == ore;
                        corridor.Add(p);
                    }
                    if (through)
                    {
                        break;
                    }
                    var t = new ExcavationTarget { Access = access, Direction = d, Corridor = EquatableArray.From(corridor), Door = corridor[^1] };
                    var dx = t.Door.X - ore.X;
                    var dz = t.Door.Z - ore.Z;
                    if ((dx * dx) + (dz * dz) != 1 || !ExcavationSupported(corridor, r.RoofSupport))
                    {
                        continue;
                    }
                    var (score, ok) = Judge(t);
                    if (ok)
                    {
                        targets.Add(t with { Score = score });
                        break;
                    }
                }
            }
        }
        var sorted = targets.OrderBy(t => t.Score).ThenBy(t => t.Access, Comparer<Cell>.Create((a, b) => CellLess(a, b) ? -1 : CellLess(b, a) ? 1 : 0))
            .ThenBy(t => t.Direction, Comparer<Cell>.Create((a, b) => CellLess(a, b) ? -1 : CellLess(b, a) ? 1 : 0));
        return [.. sorted.Take(12)];
    }
}
