using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// Asks which candidate footprints best host a building that pollutes or produces wastepacks (a
/// PlanningDefinition with Pollutes known true: CompToxifier, CompPolluteOverTime, CompWasteProducer). The
/// caller owns candidate legality (free, buildable ground); this rule only ranks them. Cells are map cells
/// inside Bounds; an empty avoid set means the colony has none of it yet.
/// </summary>
public sealed record PollutionSiteRequest
{
    public Bounds Bounds { get; init; }

    public IReadOnlyList<Rectangle> Candidates { get; init; } = [];

    // Avoid sets: the footprint stays far from each. Field zone cells, bedroom cells and living-room cells
    // (room cells whose role the caller read natively) and cells the pollution grid marks polluted.
    public IReadOnlyList<Cell> FieldCells { get; init; } = [];

    public IReadOnlyList<Cell> BedroomCells { get; init; } = [];

    public IReadOnlyList<Cell> LivingCells { get; init; } = [];

    public IReadOnlyList<Cell> PollutedCells { get; init; } = [];

    /// <summary>
    /// Where wastepacks leave the map: wastepack atomizer cells. The footprint stays near it; empty means the
    /// colony has no disposal and the disposal term does not rank.
    /// </summary>
    public IReadOnlyList<Cell> Disposal { get; init; } = [];
}

/// <summary>
/// One ranked candidate. ClearanceSquared is the squared distance in cells from the footprint to the nearest
/// avoided cell (the smallest across the four avoid sets; -1 when every set is empty). DisposalSquared is the
/// squared distance to the nearest disposal cell (-1 without disposal).
/// </summary>
public sealed record PollutionSite(Rectangle Site, long ClearanceSquared, long DisposalSquared);

public static partial class PolicyFunctions
{
    /// <summary>
    /// Ranks the candidates: farthest from fields, bedrooms, living rooms and polluted cells first, then
    /// nearest wastepack disposal, then by cell order. It is a pure ranking over distance only: no wind term
    /// (no sourced rule), no reservation, no threshold, so a goal takes the first candidate its native
    /// placement preview accepts. Throws DomainException on bad input.
    /// </summary>
    public static IReadOnlyList<PollutionSite> PollutionSites(PollutionSiteRequest r)
    {
        if (r.Bounds.Width <= 0 || r.Bounds.Height <= 0)
        {
            throw new DomainException("invalid pollution site bounds");
        }
        bool InBounds(Cell c) => c.X >= 0 && c.Z >= 0 && c.X < r.Bounds.Width && c.Z < r.Bounds.Height;
        (string Name, IReadOnlyList<Cell> Set)[] sets =
        [
            ("field", r.FieldCells), ("bedroom", r.BedroomCells), ("living", r.LivingCells),
            ("polluted", r.PollutedCells), ("disposal", r.Disposal),
        ];
        foreach (var (name, set) in sets)
        {
            if (set.Any(c => !InBounds(c)))
            {
                throw new DomainException($"pollution site {name} cell out of bounds");
            }
        }
        var avoid = new List<Cell>();
        avoid.AddRange(r.FieldCells);
        avoid.AddRange(r.BedroomCells);
        avoid.AddRange(r.LivingCells);
        avoid.AddRange(r.PollutedCells);
        var sites = new List<PollutionSite>(r.Candidates.Count);
        foreach (var c in r.Candidates)
        {
            if (c.Width <= 0 || c.Height <= 0 || c.X < 0 || c.Z < 0 || c.X + c.Width > r.Bounds.Width || c.Z + c.Height > r.Bounds.Height)
            {
                throw new DomainException("pollution site candidate out of bounds");
            }
            var cells = RectCells(c);
            sites.Add(new PollutionSite(c, NearestSquared(cells, avoid), NearestSquared(cells, r.Disposal)));
        }
        // OrderBy is stable, matching sort.SliceStable.
        return [.. sites
            .OrderByDescending(s => s.ClearanceSquared)
            .ThenBy(s => s.DisposalSquared)
            .ThenBy(s => new Cell(s.Site.X, s.Site.Z), LessComparer<Cell>(CellLess))];
    }

    /// <summary>The smallest squared distance from any cell to any target, -1 when there is no target. A target inside the footprint is 0.</summary>
    internal static long NearestSquared(IReadOnlyList<Cell> cells, IReadOnlyList<Cell> targets)
    {
        var best = -1L;
        foreach (var t in targets)
        {
            foreach (var c in cells)
            {
                var d = SquaredDistance(c, t);
                if (best < 0 || d < best)
                {
                    best = d;
                }
            }
        }
        return best;
    }
}
