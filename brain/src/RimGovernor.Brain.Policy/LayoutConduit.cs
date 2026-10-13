using System.Globalization;

namespace RimGovernor.Brain.Policy;

// Conduits (#2889): the planner owns the power conduit paths. A tiled plan carries one ConduitLink per routed
// end, derived with the plan. Conduits run under walls, doors and furniture (the Conduits layer; ConduitCells),
// so they compete for no floor: the coupling is path length and, later, the lifecycle of the room a path
// crosses. power_method still decides which sites connect, by which method and at what budget; a planned link
// is what the planner would lay, not an order.
//
// Topology: one net grows. The first power site is the root; every later site, then every consuming room,
// routes from its own cells to the nearest cell of the net so far (the root's cells and every laid path), so
// cells are shared and no star forms on any node, the battery room included.
public static partial class PolicyConstants
{
    internal const string ConduitRoomTag = "room:";
    internal const string ConduitSiteTag = "site:";

    // Conduit decisions; the caller journals them with telemetry.Decide.
    public const string ReasonConduitRouted = "conduit_routed";
    public const string ReasonConduitKept = "conduit_kept";
    public const string ReasonConduitBlocked = "conduit_no_route";
    public const string ReasonConduitNoSource = "conduit_no_source";

    /// <summary>The reservations that stand on the power net: the geothermal enclosures and the battery room. The turbines and solar plots are stored sites (PowerSiteNodes).</summary>
    internal static readonly IReadOnlyList<ReservationKind> PowerSiteKinds = [ReservationKind.BatteryRoom, ReservationKind.Geothermal];
}

/// <summary>
/// Names one end of a link: "room:&lt;id&gt;" for a planned room or "site:&lt;node id&gt;" for a site: a stored
/// turbine or solar plot by its node id, a geothermal enclosure or battery-room reservation by
/// ReservationSiteID.
/// </summary>
public readonly record struct ConduitRef(string Value)
{
    /// <summary>The ref of planned room id.</summary>
    public static ConduitRef ForRoom(int id) => new(PolicyConstants.ConduitRoomTag + id.ToString(CultureInfo.InvariantCulture));

    /// <summary>The ref of the site node with the given id.</summary>
    public static ConduitRef ForSite(string node) => new(PolicyConstants.ConduitSiteTag + node);

    /// <summary>The room id of a room ref.</summary>
    public (int Id, bool Ok) Room()
    {
        if (Value is null || !Value.StartsWith(PolicyConstants.ConduitRoomTag, StringComparison.Ordinal))
        {
            return (0, false);
        }
        var parsed = int.TryParse(Value[PolicyConstants.ConduitRoomTag.Length..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id);
        return (id, parsed && id > 0);
    }

    /// <summary>The node id of a site ref.</summary>
    public (string Node, bool Ok) Site()
    {
        if (Value is null || !Value.StartsWith(PolicyConstants.ConduitSiteTag, StringComparison.Ordinal))
        {
            return (string.Empty, false);
        }
        var rest = Value[PolicyConstants.ConduitSiteTag.Length..];
        return (rest, rest != string.Empty);
    }
}

/// <summary>
/// One laid conduit path: a node joined to the net at the cell of another. Cells run from the From node's
/// cells to the first cell of the net, 4-connected, ends included. Paths share cells where they meet.
/// </summary>
public sealed record ConduitLink(ConduitRef From, ConduitRef To, EquatableArray<Cell> Cells);

/// <summary>One conduit decision for the caller to journal. To is empty when the node found no net to join.</summary>
public sealed record ConduitDecision(ConduitRef From, ConduitRef To, string Verdict, string Reason);

/// <summary>A thing the net joins: its ref and the cells it stands on.</summary>
public sealed record ConduitNode(ConduitRef Ref, IReadOnlyList<Cell> Cells);

/// <summary>The net as it grows: the cells the router may use, the cells laid, who laid each, and the links the colony already holds (HeldLinks).</summary>
internal sealed class ConduitNet(HashSet<Cell> allowed, IReadOnlyDictionary<ConduitRef, ConduitLink> held)
{
    private readonly HashSet<Cell> _allowed = allowed;
    private readonly Dictionary<ConduitRef, ConduitLink> _held = new(held);

    public HashSet<Cell> Net { get; } = [];

    public Dictionary<Cell, ConduitRef> Owner { get; } = [];

    /// <summary>Puts a node's own cells into the net, where no earlier node laid them.</summary>
    public void Stand(ConduitNode n)
    {
        foreach (var c in n.Cells)
        {
            if (Net.Add(c))
            {
                Owner[c] = n.Ref;
            }
        }
    }

    /// <summary>
    /// Reports the held link of n as still good: it starts on n, every cell is ground a conduit may cross, and
    /// it ends on the net so far. A link that holds is the path already built or ordered, so a change elsewhere
    /// in the plan does not move it.
    /// </summary>
    private bool Keeps(ConduitNode n, ConduitLink? l)
    {
        if (l is null || l.Cells.Count == 0 || !n.Cells.Contains(l.Cells[0]) || !Net.Contains(l.Cells[l.Cells.Count - 1]))
        {
            return false;
        }
        return l.Cells.All(_allowed.Contains);
    }

    /// <summary>Joins node n to the net: its held link where that still holds, else the shortest path to the net. Ok is false for a node the net cannot reach.</summary>
    public (ConduitLink? Link, ConduitDecision Decision, bool Ok) Lay(ConduitNode n)
    {
        _held.TryGetValue(n.Ref, out var link);
        var reason = PolicyConstants.ReasonConduitKept;
        if (!Keeps(n, link))
        {
            var path = PolicyFunctions.RouteCells(n.Cells, Net, _allowed);
            if (path.Count == 0)
            {
                return (null, new ConduitDecision(n.Ref, default, PolicyFunctions.TileUnsatisfied, PolicyConstants.ReasonConduitBlocked), false);
            }
            path = [.. Enumerable.Reverse(path)]; // from the node to the net
            link = new ConduitLink(default, default, EquatableArray.From(path));
            reason = PolicyConstants.ReasonConduitRouted;
        }
        link = link! with { From = n.Ref, To = Owner[link.Cells[link.Cells.Count - 1]] };
        foreach (var c in link.Cells)
        {
            if (Net.Add(c))
            {
                Owner[c] = n.Ref;
            }
        }
        Stand(n);
        return (link, new ConduitDecision(n.Ref, link.To, PolicyFunctions.TilePlace, reason), true);
    }
}

public static partial class PolicyFunctions
{
    /// <summary>The ref of the site node with the given id.</summary>
    public static ConduitRef SiteRef(string node) => ConduitRef.ForSite(node);

    /// <summary>The ref of planned room id.</summary>
    public static ConduitRef RoomRef(int id) => ConduitRef.ForRoom(id);

    /// <summary>Names reservation index i as a conduit site: the kind and its position in LayoutPlan.Reservations.</summary>
    public static string ReservationSiteID(ReservationKind kind, int i) =>
        Wire.Name(kind) + "-" + i.ToString(CultureInfo.InvariantCulture);

    /// <summary>The stored turbines and solar plots, in plan order.</summary>
    private static IReadOnlyList<PlannedSite> PowerSiteNodes(this LayoutPlan p) =>
        [.. p.LiveSites(NodeKind.Turbine), .. p.LiveSites(NodeKind.Solar)];

    /// <summary>The power site node ids the plan holds.</summary>
    private static HashSet<string> ConduitSites(this LayoutPlan p)
    {
        var output = new HashSet<string>();
        foreach (var s in p.PowerSiteNodes())
        {
            output.Add(s.Node);
        }
        for (var i = 0; i < p.Reservations.Count; i++)
        {
            var r = p.Reservations[i];
            if (PowerSiteKinds.Contains(r.Kind))
            {
                output.Add(ReservationSiteID(r.Kind, i));
            }
        }
        return output;
    }

    /// <summary>
    /// Reports the conduit links as well formed: both ends name a room or a power site of the plan, differ,
    /// and the cells are a non-empty 4-connected path with no repeat.
    /// </summary>
    internal static bool ValidConduits(this LayoutPlan p, IReadOnlyDictionary<int, Rectangle> rooms)
    {
        var sites = p.ConduitSites();
        bool Known(ConduitRef r)
        {
            var (id, ok) = r.Room();
            if (ok)
            {
                return rooms.ContainsKey(id);
            }
            var (node, sok) = r.Site();
            return sok && sites.Contains(node);
        }
        foreach (var l in p.Conduits)
        {
            if (l.From == l.To || !Known(l.From) || !Known(l.To) || l.Cells.Count == 0)
            {
                return false;
            }
            var seen = new HashSet<Cell>();
            for (var i = 0; i < l.Cells.Count; i++)
            {
                var c = l.Cells[i];
                if (seen.Contains(c) || (i > 0 && Math.Abs(c.X - l.Cells[i - 1].X) + Math.Abs(c.Z - l.Cells[i - 1].Z) != 1))
                {
                    return false;
                }
                seen.Add(c);
            }
        }
        return true;
    }

    /// <summary>The ground the plan's nodes may route over: the survey's conduit cells, and each node's own cells, which are the plan's ground whatever the survey holds there.</summary>
    private static ConduitNet NewConduitNet(LayoutPlan plan, IEnumerable<ConduitNode> nodes, MapSurvey survey, IReadOnlyDictionary<ConduitRef, ConduitLink> held)
    {
        var allowed = new HashSet<Cell>(ConduitCells(survey, plan.Rooms));
        foreach (var n in nodes)
        {
            foreach (var c in n.Cells)
            {
                allowed.Add(c);
            }
        }
        return new ConduitNet(allowed, held);
    }

    /// <summary>The power sites of the plan as net nodes, in plan order.</summary>
    private static List<ConduitNode> SiteNodes(this LayoutPlan p)
    {
        var nodes = new List<ConduitNode>();
        foreach (var s in p.PowerSiteNodes())
        {
            nodes.Add(new ConduitNode(SiteRef(s.Node), s.Footprint.Cells().SortedBy(CellLess)));
        }
        for (var i = 0; i < p.Reservations.Count; i++)
        {
            var r = p.Reservations[i];
            if (PowerSiteKinds.Contains(r.Kind))
            {
                nodes.Add(new ConduitNode(SiteRef(ReservationSiteID(r.Kind, i)), r.Area.Cells().SortedBy(CellLess)));
            }
        }
        return nodes;
    }

    /// <summary>
    /// The links prev holds, by their From end, for the plan to keep where they still hold (#2966). A link of a
    /// room the plan replaces, or of a site it replaces, is the successor's: the successor joins the net by the
    /// path its predecessor's power runs on, so a replaced room keeps its conduit.
    /// </summary>
    private static Dictionary<ConduitRef, ConduitLink> HeldLinks(IReadOnlyList<ConduitLink> prev, LayoutPlan plan)
    {
        var held = new Dictionary<ConduitRef, ConduitLink>();
        foreach (var l in prev)
        {
            held[l.From] = l;
        }
        foreach (var r in plan.Rooms)
        {
            if (held.TryGetValue(RoomRef(r.Replaces), out var l) && r.Replaces != 0 && !held.ContainsKey(RoomRef(r.Id)))
            {
                held[RoomRef(r.Id)] = l;
            }
        }
        foreach (var s in plan.Sites)
        {
            foreach (var old in s.ReplacesSites)
            {
                if (held.TryGetValue(SiteRef(old), out var l) && !held.ContainsKey(SiteRef(s.Node)))
                {
                    held[SiteRef(s.Node)] = l;
                }
            }
        }
        return held;
    }

    /// <summary>
    /// Lays the plan's conduit net. Sites are the power reservations (in reservation order) and consumers the
    /// rooms holding a station that draws power, with the cells those stations stand on (the whole room where
    /// none are set). prev are the links the colony holds: a node whose held link still holds keeps it
    /// (conduit_kept) and only the nodes it no longer serves route anew, so a changed wall or room moves the
    /// conduits it touches and no other. The result is deterministic in its inputs.
    /// </summary>
    public static (IReadOnlyList<ConduitLink> Links, IReadOnlyList<ConduitDecision> Decisions) RouteConduits(LayoutPlan plan, IReadOnlyList<ConduitNode> consumers, MapSurvey survey, IReadOnlyList<ConduitLink> prev)
    {
        var nodes = plan.SiteNodes();
        if (nodes.Count == 0)
        {
            var unsourced = consumers.Select(n => new ConduitDecision(n.Ref, default, TileUnsatisfied, ReasonConduitNoSource)).ToList();
            return ([], unsourced);
        }
        nodes.AddRange(consumers);
        var g = NewConduitNet(plan, nodes, survey, HeldLinks(prev, plan));
        g.Stand(nodes[0]);
        var links = new List<ConduitLink>();
        var decisions = new List<ConduitDecision>();
        foreach (var n in nodes.Skip(1))
        {
            var (link, d, ok) = g.Lay(n);
            if (ok)
            {
                links.Add(link!);
            }
            decisions.Add(d);
        }
        return (links, decisions);
    }

    /// <summary>
    /// Joins consumers to the net the plan already holds (plan.Conduits and its power sites): the rooms a
    /// replan keeps that the tiler did not plan. Each keeps the link prev held where it still holds, else
    /// routes to the nearest cell of the net. It returns the plan's links with theirs and one decision per consumer.
    /// </summary>
    public static (IReadOnlyList<ConduitLink> Links, IReadOnlyList<ConduitDecision> Decisions) JoinConduits(LayoutPlan plan, IReadOnlyList<ConduitNode> consumers, MapSurvey survey, IReadOnlyList<ConduitLink> prev)
    {
        var sites = plan.SiteNodes();
        var g = NewConduitNet(plan, sites.Concat(consumers), survey, HeldLinks(prev, plan));
        foreach (var n in sites)
        {
            g.Stand(n);
        }
        foreach (var l in plan.Conduits)
        {
            foreach (var c in l.Cells)
            {
                if (g.Net.Add(c))
                {
                    g.Owner[c] = l.From;
                }
            }
        }
        var links = new List<ConduitLink>(plan.Conduits);
        var decisions = new List<ConduitDecision>();
        foreach (var n in consumers)
        {
            var (link, d, ok) = g.Lay(n);
            if (ok)
            {
                links.Add(link!);
            }
            decisions.Add(d);
        }
        return (links, decisions);
    }

    /// <summary>
    /// Plans the conduit net over the furnished plan and records it. Consumers are the planned rooms holding a
    /// station whose def draws power (Station.BaseW &lt; 0); a room's cells are those stations' footprints, or the
    /// whole room while the placer set none.
    /// </summary>
    internal static void RouteTileConduits(this Tiler t)
    {
        var consumers = new List<ConduitNode>();
        var rooms = t.Out.Plan.Rooms;
        for (var i = 0; i < rooms.Count; i++)
        {
            if (i >= t.Rooms.Count)
            {
                break; // the airlocks follow the group rooms and draw nothing
            }
            var room = rooms[i];
            var g = t.In.Groups[t.Rooms[i].Group];
            var draws = new HashSet<string>();
            foreach (var s in g.Stations)
            {
                var (st, ok) = t.In.Catalog.Station(s.Def);
                if (ok && st!.DrawsPower())
                {
                    draws.Add(s.Def);
                }
            }
            if (draws.Count == 0)
            {
                continue;
            }
            var cells = new List<Cell>();
            foreach (var p in t.Out.Pieces.GetValueOrDefault(room.Id) ?? [])
            {
                if (draws.Contains(p.Def))
                {
                    cells.AddRange(RectRegion(p.Rect).Cells());
                }
            }
            if (cells.Count == 0)
            {
                cells.AddRange(room.Region.Cells());
            }
            consumers.Add(new ConduitNode(RoomRef(room.Id), [.. cells.SortedBy(CellLess).Distinct()]));
        }
        var (links, decisions) = RouteConduits(t.Out.Plan, consumers, t.In.Survey, t.In.Conduits);
        t.Out.Plan = t.Out.Plan with { Conduits = EquatableArray.From(links) };
        t.Out.ConduitDecisions.Clear();
        t.Out.ConduitDecisions.AddRange(decisions);
    }
}
