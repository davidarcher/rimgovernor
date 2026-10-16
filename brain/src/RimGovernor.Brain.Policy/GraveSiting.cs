namespace RimGovernor.Brain.Policy;

// Siting a plain grave. A grave needs no room or zone: it stands on open, diggable ground near the base. The
// sites are FreeSites' (walkable, unoccupied, unzoned, light-supporting cells, so no crop field and no
// stockpile, the food stores among them) outside the ground GraveProtected keeps: the plan's rooms with their
// walls and a one-cell margin (the planned expansion ground, reserve rooms included), its reservations, its
// perimeter sites and its planned zones, the entrances with the cells around them, and the cells colonists
// were seen walking. They are ranked by walking distance from the base's entrances (HaulCosts); a site no
// entrance reaches is dropped.

/// <summary>What GraveSites reads: the cell census, the plan and the observed traffic.</summary>
public sealed record GraveSiteInput
{
    public Bounds Bounds { get; init; }

    public IReadOnlyList<SiteCell> Cells { get; init; } = [];

    public LightTerrains Light { get; init; } = new();

    public LayoutPlan Plan { get; init; } = new();

    /// <summary>The cells colonists were seen walking (RoutesObservation.Traffic); empty while unread.</summary>
    public IReadOnlyList<Cell> Traffic { get; init; } = [];
}

public static partial class PolicyFunctions
{
    /// <summary>The cells kept clear around each planned room's walls, so a grave never blocks a door or the room's own edge.</summary>
    internal const int GraveRoomMargin = 1;

    /// <summary>The ground a grave never stands on: planned ground, the entrances and their neighbours, and the observed traffic.</summary>
    internal static HashSet<Cell> GraveProtected(GraveSiteInput input)
    {
        var plan = input.Plan;
        var output = new HashSet<Cell>(input.Traffic);
        foreach (var r in plan.Rooms)
        {
            output.UnionWith(r.GroundRegion().Grow(GraveRoomMargin).Cells());
        }
        foreach (var r in plan.Reservations)
        {
            output.UnionWith(r.Area.Cells());
        }
        foreach (var s in plan.Sites)
        {
            output.UnionWith(s.Footprint.Cells());
        }
        foreach (var z in plan.Zones)
        {
            output.UnionWith(RegionOfRuns(z.Runs).Cells());
        }
        foreach (var e in plan.Entrances)
        {
            output.UnionWith(RectCells(new Rectangle { X = e.X - 1, Z = e.Z - 1, Width = 3, Height = 3 }));
        }
        return output;
    }

    /// <summary>
    /// The candidate sites for a plain grave (GraveDefinition, 1x2 north), nearest the base's entrances by
    /// walking distance first; empty when the plan has no rooms. A proposal only: the native preview decides
    /// legality.
    /// </summary>
    public static IReadOnlyList<Rectangle> GraveSites(GraveSiteInput input)
    {
        var plan = input.Plan;
        if (plan.Core() is not { } core)
        {
            return [];
        }
        bool InBounds(Cell c) => c.X >= 0 && c.Z >= 0 && c.X < input.Bounds.Width && c.Z < input.Bounds.Height;
        if (!InBounds(core))
        {
            return [];
        }
        var sites = FreeSites(new FreeSiteRequest
        {
            Bounds = input.Bounds,
            Anchor = core,
            Cells = input.Cells,
            Protected = [.. GraveProtected(input).Where(InBounds)],
            Light = input.Light,
        }, 1, 2);
        IReadOnlyList<Cell> from = plan.Entrances.Count > 0 ? plan.Entrances : [core];
        var costs = HaulCosts(input.Cells, [new HaulConsumer { Cells = from, Weight = 1 }]);
        return [.. RankSitesByHaul(sites, costs).Where(s => RectCells(s).Any(costs.ContainsKey))];
    }
}
