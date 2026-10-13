namespace RimGovernor.Brain.Policy;

/// <summary>
/// The ground a wind path is priced over: the cells the game counts blocked (a roofed cell or a thing whose def has
/// blockWind). A cell is blocked off the map, under a thick mountain roof, under a tree, in a fenced room (its roof and
/// walls) or under a placed footprint (the turbine def sets blockWind).
/// </summary>
public sealed class WindGround
{
    private readonly Bounds bounds;

    public WindGround(SiteEmbedInput input)
    {
        bounds = input.Embed.Survey.Bounds;
        foreach (var c in input.Embed.Survey.Cells)
        {
            if (c.ThickRoof || c.Tree)
            {
                Cells.Add(c.Cell);
            }
        }
        foreach (var b in input.Embedding.Blocks)
        {
            // An open-air room (the range, the ring) has no roof and no walls.
            var (role, ok) = PolicyFunctions.PlannedRoleOfGroup(input.Embed.Groups[b.Group]);
            if (ok && PolicyFunctions.ExactFloor(role))
            {
                continue;
            }
            Add(PolicyFunctions.RectRegion(PolicyFunctions.Pad(b.Interior, 1)));
        }
    }

    public HashSet<Cell> Cells { get; } = [];

    public void Add(Region r) => Cells.UnionWith(r.Cells());

    /// <summary>Counts the lane's cells the game counts blocked, the node's own footprint excepted.</summary>
    public int Blocked(Region lane, Region own)
    {
        var n = 0;
        foreach (var c in lane.Cells())
        {
            if (c.X < 0 || c.Z < 0 || c.X >= bounds.Width || c.Z >= bounds.Height || (Cells.Contains(c) && !own.Contains(c)))
            {
                n++;
            }
        }
        return n;
    }

    /// <summary>Blocked for a lane shifted by (dx, dz), the node's own footprint a rectangle: the embedder prices a lane at every origin without translating it.</summary>
    public int BlockedAt(Region lane, int dx, int dz, Rectangle own)
    {
        var n = 0;
        foreach (var run in lane.Runs)
        {
            var z = run.Z + dz;
            for (var x = run.X + dx; x < run.X + dx + run.Length; x++)
            {
                var c = new Cell(x, z);
                if (x < 0 || z < 0 || x >= bounds.Width || z >= bounds.Height || (Cells.Contains(c) && !PolicyFunctions.Contains(own, c)))
                {
                    n++;
                }
            }
        }
        return n;
    }
}

/// <summary>A turbine's turn, with whether the turbine stands on its pad's low row and faces the rising axis (TurbinePlacement's lowRow: north or east) and whether the pad lies turned.</summary>
internal readonly record struct TurbineTurn(Rotation Rot, bool LowRow, bool Turned);

/// <summary>Ports <c>layout_power_nodes.go</c>: the power sites as site nodes of the station graph.</summary>
public static partial class PolicyFunctions
{
    // The wind path is a game rule, read from CompPowerPlantWind and WindTurbineUtility.CalculateWindCells and
    // registered in DefTables under the WindTurbine def: the path is windPathWidth wide, windPathAhead rows in front
    // starting windPathStart out from the centre and windPathBehind behind (the turbine's own cells are not in it, so
    // it never blocks itself). Each path cell that is roofed, off the map or holds a thing whose def has blockWind
    // removes windBlockedLoss of the output; five or more remove all of it.
    internal const int WindPathWidth = 7;
    internal const int WindPathAhead = 10;
    internal const int WindPathStart = 2;
    internal const int WindPathBehind = 6;
    internal const double WindBlockedLoss = 0.2;

    /// <summary>The fraction of its rated output a turbine makes with blocked wind-path cells.</summary>
    public static double WindOutput(int blocked) => Math.Max(1 - (WindBlockedLoss * blocked), 0);

    /// <summary>Why the graph holds a power site node.</summary>
    public const string ReasonPowerSite = "planned_power_site";

    /// <summary>Why a turbine carries a wind lane.</summary>
    public const string ReasonWindLane = "wind_path";

    public static NodeKind NodeTurbine => NodeKind.Turbine;
    public static NodeKind NodeSolar => NodeKind.Solar;

    private static TurbineTurn[] TurbineTurns =>
    [
        new(Rotation.North, true, false),
        new(Rotation.South, false, false),
        new(Rotation.East, true, true),
        new(Rotation.West, false, true),
    ];

    /// <summary>Turbine i: its footprint is the 7x2 pad of a planned turbine (2x7 turned) and the wind lane the path of its centre.</summary>
    internal static SiteNode TurbineNode(string id)
    {
        var shapes = new List<SiteNode>();
        foreach (var t in TurbineTurns)
        {
            var pad = t.Turned ? new Rectangle { Width = 2, Height = WindPathWidth } : new Rectangle { Width = WindPathWidth, Height = 2 };
            var (centre, rot) = TurbinePlacement(pad, t.LowRow);
            var lane = new SiteLane { Id = "wind", Region = RegionOf(TurbineWindCells(centre, rot)), Reason = ReasonWindLane, Wind = true };
            shapes.Add(new SiteNode { Id = id, Footprint = RectRegion(pad), Lanes = [lane], Rotation = rot });
        }
        return shapes[0] with { Alts = shapes.Skip(1).ToList(), Kind = NodeTurbine, Reason = ReasonPowerSite };
    }

    /// <summary>
    /// The placed turbines and solar plots as the plan's stored sites, in node order. A geothermal enclosure is
    /// anchored on the reservation the plan already holds and a battery room is a room, so neither is stored again; a
    /// node that found no ground (no placement) is left out.
    /// </summary>
    public static List<PlannedSite> PowerSitesOf(IEnumerable<SiteNode> nodes, SiteEmbedding placed)
    {
        var output = new List<PlannedSite>();
        foreach (var n in nodes)
        {
            if (n.Kind != NodeTurbine && n.Kind != NodeSolar)
            {
                continue;
            }
            if (placed.Site(n.Id) is { } p)
            {
                output.Add(PlannedSite.Of(p, n.Kind) with { Reason = n.Reason });
            }
        }
        return output;
    }

    /// <summary>The id of the i-th (from 1) turbine node.</summary>
    internal static string WindNodeId(int i) => $"wind-{i}";

    /// <summary>The id of the i-th (from 1) solar plot node.</summary>
    internal static string SolarNodeId(int i) => $"solar-{i}";

    /// <summary>Solar plot i: a free square.</summary>
    internal static SiteNode SolarNode(string id) =>
        new() { Id = id, Kind = NodeSolar, Footprint = RectRegion(new Rectangle { Width = SolarSide, Height = SolarSide }), Reason = ReasonPowerSite };

    /// <summary>
    /// The site nodes for the plan's power sites: a node per turbine, solar plot and geothermal enclosure, ids numbered
    /// in plan order. A plan holds the sites it was grown with; a first plan derives how many it owes from the demand it
    /// plans for (DerivePowerSites).
    /// </summary>
    public static List<SiteNode> PowerNodesOf(LayoutPlan plan)
    {
        var nodes = new List<SiteNode>();
        for (var i = 0; i < PlannedPowerSites(plan, WindTurbineDefinition).Count; i++)
        {
            nodes.Add(TurbineNode(WindNodeId(i + 1)));
        }
        for (var i = 0; i < PlannedPowerSites(plan, SolarDefinition).Count; i++)
        {
            nodes.Add(SolarNode(SolarNodeId(i + 1)));
        }
        var n = 0;
        foreach (var r in plan.Reservations)
        {
            if (r.Kind != ReserveGeothermal)
            {
                continue;
            }
            n++;
            var area = r.Area.Bounds();
            nodes.Add(new SiteNode { Id = GeothermalNodeId(n), Footprint = RectRegion(new Rectangle { Width = area.Width, Height = area.Height }), Anchor = new Cell(area.X, area.Z), Reason = ReasonPowerSite });
        }
        return nodes;
    }

    /// <summary>The id of the i-th (from 1) geothermal enclosure node, in plan order; the defense record names the enclosure's tier by it.</summary>
    internal static string GeothermalNodeId(int i) => $"geothermal-{i}";
}
