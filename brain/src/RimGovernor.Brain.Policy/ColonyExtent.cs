using HomeCoverageFacts = RimGovernor.Contracts.Wire.Observations.HomeCoverageFacts;
using HomeCoverageTarget = RimGovernor.Contracts.Wire.Observations.HomeCoverageTarget;

namespace RimGovernor.Brain.Policy;

public enum ExtentOrigin
{
    None,

    [WireName("facility")]
    Facility,

    [WireName("enclosed_interior")]
    EnclosedInterior,

    [WireName("corridor")]
    Corridor,

    [WireName("margin")]
    Margin,
}


public sealed record ExtentProvenance
{
    public ExtentOrigin Origin { get; init; }

    public string Facility { get; init; } = "";

    public PlanId Plan { get; init; }

    public ActionId Action { get; init; }

    public ConcernId Concern { get; init; }
}

public sealed record ExtentCell
{
    public Cell Cell { get; init; }

    public EquatableArray<ExtentProvenance> Provenance { get; init; }
}

/// <summary>
/// Regions are four-neighbor components of observed geometry. Margins belong to their original component:
/// overlapping margins never merge regions. Cells and provenance are sorted; no returned slice aliases inputs.
/// </summary>
public sealed record ColonyExtent
{
    public EquatableArray<ExtentRegion> Regions { get; init; }
}

public sealed record ExtentRegion
{
    public EquatableArray<ExtentCell> Cells { get; init; }
}

public sealed record ColonyExtentRequest
{
    public Fact<Bounds> Bounds { get; init; }

    public Fact<CurrentConstruction> Construction { get; init; }

    public Fact<EquatableArray<ConstructionClaim>> Claims { get; init; }

    /// <summary>The Home census; null when no colony facts read built the projection.</summary>
    public HomeCoverageFacts? Home { get; init; }

    /// <summary>A Chebyshev radius, from zero through eight cells.</summary>
    public int Margin { get; init; }
}

public static class ColonyExtentRules
{
    private const int ExtentCellLimit = 65536;

    /// <summary>
    /// Derives the colony's extent from the construction census, the claims and the home coverage. Unknown
    /// when the evidence is incomplete; throws for contradictory geometry or bounds.
    /// </summary>
    public static Fact<ColonyExtent> DeriveColonyExtent(ColonyExtentRequest r)
    {
        var unknown = Unknown<ColonyExtent>();
        static DomainException Invalid() => new("invalid colony extent geometry or bounds");

        var bk = r.Bounds.TryGetValue(out var bounds);
        var ck = r.Construction.TryGetValue(out var census);
        var home = r.Home;
        if (r.Margin < 0 || r.Margin > 8)
        {
            throw Invalid();
        }

        if (!bk || !ck || home is null || !census!.Colony)
        {
            return unknown;
        }

        if (bounds.Width <= 0 || bounds.Height <= 0 || bounds.Width > 4096 || bounds.Height > 4096)
        {
            throw Invalid();
        }

        // Missing footprints are incomplete evidence, not a known empty colony.
        if (census.Buildings.Items.Any(b => b.Cells.Count == 0))
        {
            return unknown;
        }

        // Ambiguous optional history cannot select provenance by input order.
        r.Claims.TryGetValue(out var history);
        var claimIds = new HashSet<ActionId>();
        foreach (var claim in history.Items)
        {
            if (!claimIds.Add(claim.Action))
            {
                throw new DomainException("ambiguous colony extent construction provenance");
            }
        }

        var owned = ConstructionOwnershipRules.OwnedConstructions(r.Claims, r.Construction);
        owned.TryGetValue(out var buildings);
        if (home.Revision < 0)
        {
            throw Invalid();
        }

        var targets = new Dictionary<string, HomeCoverageTarget>(StringComparer.Ordinal);
        foreach (var target in home.Targets)
        {
            if (!FoodForecastRules.FoodId(target.Id) || !targets.TryAdd(target.Id, target))
            {
                throw Invalid();
            }
        }

        bool InBounds(Cell c) => c.X >= 0 && c.Z >= 0 && c.X < bounds.Width && c.Z < bounds.Height;

        var cells = new Dictionary<Cell, HashSet<ExtentProvenance>>();

        bool Add(Cell c, ExtentProvenance p)
        {
            if (!InBounds(c))
            {
                return false;
            }

            if (!cells.TryGetValue(c, out var set))
            {
                set = [];
                cells[c] = set;
            }

            set.Add(p);
            return cells.Count <= ExtentCellLimit;
        }

        foreach (var b in buildings.Items)
        {
            // A building outside the census, or one without bounded visible geometry, cannot establish territory.
            if (!targets.TryGetValue(b.Identity.Current, out var target) || target is not HomeCoverageTarget.Scoped { Extent: var geometry })
            {
                return unknown;
            }

            var p = new ExtentProvenance
            {
                Origin = ExtentOrigin.Facility,
                Facility = b.Identity.Current,
                Plan = b.Plan,
                Action = b.Action,
                Concern = b.Concern,
            };
            var local = new HashSet<Cell>();
            var groups = new (ExtentOrigin Origin, IReadOnlyList<Cell> Cells)[]
            {
                (ExtentOrigin.Facility, b.Cells.Items),
                (ExtentOrigin.EnclosedInterior, geometry.EnclosedInterior.Items),
                (ExtentOrigin.Corridor, geometry.Corridor.Items),
            };
            foreach (var group in groups)
            {
                p = p with { Origin = group.Origin };
                var seen = new HashSet<Cell>();
                foreach (var c in group.Cells)
                {
                    if (seen.Contains(c) || !Add(c, p))
                    {
                        throw Invalid();
                    }

                    seen.Add(c);
                    local.Add(c);
                }
            }

            // A target cannot smuggle an unconnected island into its interior.
            var queue = new List<Cell>(b.Cells.Items);
            foreach (var c in queue)
            {
                local.Remove(c);
            }

            for (var i = 0; i < queue.Count; i++)
            {
                foreach (var next in ExtentNeighbors(queue[i]))
                {
                    if (local.Remove(next))
                    {
                        queue.Add(next);
                    }
                }
            }

            if (local.Count != 0)
            {
                throw Invalid();
            }
        }

        // Every census stockpile is territory at its whole current footprint, whoever zoned it. A target that
        // is not a building and has no bounded visible geometry adds nothing.
        var facility = new HashSet<string>(buildings.Items.Select(b => b.Identity.Current), StringComparer.Ordinal);
        foreach (var target in home.Targets)
        {
            if (facility.Contains(target.Id) || target is not HomeCoverageTarget.Scoped { Extent: var geometry })
            {
                continue;
            }

            var seen = new HashSet<Cell>();
            foreach (var c in geometry.Zone.Items)
            {
                if (seen.Contains(c) || !Add(c, new ExtentProvenance { Origin = ExtentOrigin.Facility, Facility = target.Id }))
                {
                    throw Invalid();
                }

                seen.Add(c);
            }
        }

        var ordered = cells.Keys.OrderBy(c => c.X).ThenBy(c => c.Z).ToList();
        var visited = new HashSet<Cell>();
        var regions = new List<ExtentRegion>();
        var total = 0;
        foreach (var seed in ordered)
        {
            if (visited.Contains(seed))
            {
                continue;
            }

            var queue = new List<Cell> { seed };
            visited.Add(seed);
            for (var i = 0; i < queue.Count; i++)
            {
                foreach (var next in ExtentNeighbors(queue[i]))
                {
                    if (cells.ContainsKey(next) && visited.Add(next))
                    {
                        queue.Add(next);
                    }
                }
            }

            var region = new Dictionary<Cell, HashSet<ExtentProvenance>>();
            foreach (var c in queue)
            {
                region[c] = cells[c];
            }

            foreach (var c in queue)
            {
                for (var dx = -r.Margin; dx <= r.Margin; dx++)
                {
                    for (var dz = -r.Margin; dz <= r.Margin; dz++)
                    {
                        var next = new Cell(c.X + dx, c.Z + dz);
                        if (!InBounds(next) || cells.ContainsKey(next))
                        {
                            continue;
                        }

                        if (!region.ContainsKey(next))
                        {
                            region[next] = [new ExtentProvenance { Origin = ExtentOrigin.Margin }];
                        }
                    }
                }

                if (total + region.Count > ExtentCellLimit)
                {
                    throw Invalid();
                }
            }

            var rows = new List<ExtentCell>(region.Count);
            foreach (var (c, reasons) in region)
            {
                var provenance = reasons
                    .OrderBy(p => Wire.Name(p.Origin), StringComparer.Ordinal)
                    .ThenBy(p => p.Facility, StringComparer.Ordinal);
                rows.Add(new ExtentCell { Cell = c, Provenance = EquatableArray.From(provenance) });
            }

            rows.Sort((a, b) => ExtentCellLess(a.Cell, b.Cell) ? -1 : ExtentCellLess(b.Cell, a.Cell) ? 1 : 0);
            regions.Add(new ExtentRegion { Cells = EquatableArray.From(rows) });
            total += region.Count;
        }

        return Known(new ColonyExtent { Regions = EquatableArray.From(regions) });
    }

    internal static bool ExtentCellLess(Cell a, Cell b) => a.X < b.X || (a.X == b.X && a.Z < b.Z);

    private static Cell[] ExtentNeighbors(Cell c) => [new(c.X - 1, c.Z), new(c.X + 1, c.Z), new(c.X, c.Z - 1), new(c.X, c.Z + 1)];
}
