using System.Globalization;

namespace RimGovernor.Brain.Policy;

// Clustering turns the station graph into room groups, the seed the site planner places. It generalises
// affinityClusters (union-find over weighted trips) from role-keyed rooms to stations, and nothing in it is a
// per-role constant:
//
//   - Units. Every graph node is a pool of identical stations. A pool is split, by halves, until each part is
//     lossless (PredictRole keeps every gameplay effect its stations are for) and fits the area limit. Two
//     free beds predict a barracks, which is not the bedroom a bed is for, so beds end as one per group; four
//     medical beds predict the same hospital as one, so they stay together.
//   - Merging. Unit pairs are taken by descending affinity (the graph's flow weights, a required link first)
//     and merged unless the merged group loses an effect a part had (a bench slowed by the wrong role, a
//     bedroom that reads as barracks), would join an enclosed room with an open-sky one, or would not fit the
//     area limit. A cold room beside a station that works in a temperature band is not refused: the decision
//     records the conflict (Conflict) for the search to price. A filth node (a butcher) beside stations that
//     are not filthy is priced too (FilthCost) and kept apart unless a PairTogether rule joins the pair
//     (Regroup, #2878).
//   - Intended role. What a station is for is what the game makes of it alone: PredictRole of the single
//     station (a stove is a kitchen, a bed a bedroom, a medical bed a hospital).
//   - Area. A group's area is its stations' footprints (the catalog's shapes) plus the clearance the placer
//     keeps: one open cell per station (its interaction cell, or the neighbour that keeps it reachable), the
//     exterior door threshold and one more per required link (AddRequiredDoors). The limit is the largest room
//     the game's roof and the plan's shell bounds allow: a room is roofed only while every cell lies within
//     RoofMaxSupportDistance of a wall (RoomFootprint), which bounds its depth, and a shell is at most 3844
//     interior cells and 255 wall cells.
//   - Environment. Cold, clean, enclosed and open-sky requirements come from the catalog's needs of the
//     stations in the group.
//   - Cold is met by a cooling station: a group that needs cold gets the catalog's most efficient cooler
//     (least power draw per heat moved) in its station list, its footprint and one clearance cell in the area,
//     or a ClusterUnsatisfied decision when the catalog has none. Cooler power and wiring are the
//     refrigeration planner's; exposure to cold outside air is the climate envelope's (#2880).
//
// The result is pure data and deterministic: ties break by node id. The caller journals Decisions as telemetry
// decisions.

/// <summary>An environment a group's room must provide.</summary>
public enum EnvRequirement
{
    /// <summary>A station stores things that rot (NeedCold).</summary>
    [WireName("cold")]
    Cold,

    /// <summary>A station's effect scales with the room's cleanliness.</summary>
    [WireName("clean")]
    Clean,

    /// <summary>A station works only indoors (NeedIndoors).</summary>
    [WireName("enclosed")]
    Enclosed,

    /// <summary>A station cannot stand under a roof (NeedOpenSky).</summary>
    [WireName("open_sky")]
    OpenSky,
}

public static partial class PolicyConstants
{
    // Verdicts and reasons of clustering decisions.
    public const string ClusterMerge = "merge";
    public const string ClusterKeep = "keep";
    public const string ClusterSplit = "split";

    /// <summary>A station-less room node became a group (#2882).</summary>
    public const string ClusterRoom = "room";

    /// <summary>A group needs cold and the catalog has no cooling station.</summary>
    public const string ClusterUnsatisfied = "unsatisfied";

    public const string ReasonClusterAffinity = "cluster_affinity";
    public const string ReasonClusterLostEffect = "cluster_lost_effect";
    public const string ReasonClusterEnvConflict = "cluster_env_conflict";
    public const string ReasonClusterArea = "cluster_area";

    /// <summary>A filth node was kept from a room of other stations because no PairTogether rule joins them; the decision carries the price.</summary>
    public const string ReasonClusterFilthPriced = "cluster_filth_priced";

    /// <summary>A pair rule (ClusterInput.Pairs) kept two rooms apart.</summary>
    public const string ReasonClusterApart = "cluster_pair_apart";

    /// <summary>A pair rule (ClusterInput.Pairs) merged two rooms past a refusal.</summary>
    public const string ReasonClusterTogether = "cluster_pair_together";

    public const string ReasonClusterNoCooler = "cluster_no_cooler";

    /// <summary>A target-sized bedroom (StationNode.Floor) is its own room; no rule merges it with a barracks or another bedroom (#2939).</summary>
    public const string ReasonClusterSuite = "cluster_suite_own_room";

    /// <summary>The merged room would be one no planned room carries (several beds read as a barracks), which the tiler cannot place (#3017); no pair rule joins them.</summary>
    public const string ReasonClusterNoRoom = "cluster_no_planned_room";

    /// <summary>
    /// Recorded on a decision whose two parts are a cold group and a station that works in a temperature band:
    /// cold is kept below the rot-slowing temperature, under the band, so the room costs one or the other. The
    /// game gives no exclusion; the search prices it (#2896).
    /// </summary>
    public const string ConflictColdBand = "cold_vs_temperature_band";

    /// <summary>The room stat the surgery chain ends in; the catalog's NeedRoomStat names it as Input.</summary>
    internal const string CleanlinessStat = "Cleanliness";

    /// <summary>Shell bound of the plan's room footprint: at most 3844 interior cells.</summary>
    internal const int ShellMaxInterior = 3844;

    /// <summary>Shell bound of the plan's room footprint: at most 255 wall cells.</summary>
    internal const int ShellMaxWalls = 255;
}

/// <summary>Marks a graph the catalog cannot cluster (ErrCluster).</summary>
public sealed class ClusterException(string message) : DomainException("cluster stations: " + message);

/// <summary>Everything clustering reads.</summary>
public sealed record ClusterInput
{
    public required StationGraph Graph { get; init; }

    public required StationCatalog Catalog { get; init; }

    /// <summary>The game's RoofCollapseUtility.RoofMaxSupportDistance (the catalog's roof_max_support_distance).</summary>
    public double RoofDistance { get; init; }

    /// <summary>Per-pair overrides of the merge, set by the layout search (Regroup). The seed with none is the conservative clustering.</summary>
    public IReadOnlyList<NodePair> Pairs { get; init; } = [];
}

/// <summary>A per-pair override of the merge.</summary>
public enum PairRule
{
    None = 0,

    /// <summary>Merges two rooms that hold the pair's nodes past a refusal for a lost effect or filth (the merge is then priced by the search). An environment exclusion and the area limit still refuse it.</summary>
    Together,

    /// <summary>Keeps two rooms that hold the pair's nodes from ever merging.</summary>
    Apart,
}

/// <summary>
/// A rule between two graph nodes, by id; it holds between any two rooms that hold one node each, so a third
/// node cannot bridge an Apart pair. Apart beats Together.
/// </summary>
public sealed record NodePair(string A, string B, PairRule Rule)
{
    public bool Names(string a, string b) => (A == a && B == b) || (A == b && B == a);
}

/// <summary>The stations of one graph node in a group.</summary>
public sealed record GroupStation(string Node, string Def, StationPurpose Purpose, int Count);

/// <summary>The wish that two groups be neighbours; Required is a link the environment needs demand (cold storage beside the stove).</summary>
public sealed record GroupLink
{
    public int To { get; init; }

    public double Weight { get; set; }

    public bool Required { get; set; }
}

/// <summary>Stations that share a room.</summary>
public sealed record RoomGroup
{
    public int Id { get; set; }

    public List<GroupStation> Stations { get; set; } = [];

    /// <summary>The cells the stations stand on.</summary>
    public int Footprint { get; set; }

    /// <summary>The open cells the placer keeps.</summary>
    public int Clearance { get; set; }

    /// <summary>Footprint plus Clearance.</summary>
    public int Area { get; set; }

    /// <summary>The area of the standing suite the group is (StationNode.Kept): Area is never below it. 0 otherwise.</summary>
    public int Kept { get; set; }

    /// <summary>The native role the game would give the room.</summary>
    public RoomRole Role { get; set; }

    /// <summary>The planned room the group's contents decide when it is not the one Role gives (the butchery); None otherwise (PlannedRoleOfGroup).</summary>
    public PlannedRole Planned { get; set; }

    /// <summary>The fixed short side of the group's block, 0 for the squarest (a circulation corridor is a strip).</summary>
    public int Width { get; set; }

    /// <summary>Carries a sparring ring's node (#2887): its marker count.</summary>
    public int Markers { get; set; }

    public List<EnvRequirement> Requires { get; set; } = [];

    public List<GroupLink> Links { get; set; } = [];

    /// <summary>The group's trips a day (one way) to the outside node: the graph's PurposeOutside edges, split over the groups holding the node.</summary>
    public double Outside { get; set; }

    /// <summary>The throughput the room loses to the environment its stations do not get.</summary>
    public IReadOnlyList<ConflictCost> Costs { get; set; } = [];

    /// <summary>The role effects the room does not give.</summary>
    public IReadOnlyList<LostRoleEffect> LostEffects { get; set; } = [];

    /// <summary>The price of the filth nodes sharing the room with stations they are not for (empty for a closet), in game units.</summary>
    public IReadOnlyList<FilthCost> Filth { get; set; } = [];

    /// <summary>
    /// The work-days a day of cleaning the group's own filth takes, whether it shares the room or not (#2993):
    /// the same blood is cleaned either way, so it is the baseline a merge is compared against.
    /// </summary>
    public double Cleaning { get; set; }

    /// <summary>
    /// Counts the door each required link opens: tiling opens a door on every adjacent required pair, and a
    /// door keeps the cell inside it clear, so the group's floor grows by one threshold per required link
    /// beyond the exterior door eval counted. Other links are wishes; a door for one opens only where the room
    /// still furnishes. The merge area check runs before the links exist, so a merged group can pass it and
    /// exceed the limit by its required doors.
    /// </summary>
    public void AddRequiredDoors()
    {
        foreach (var l in Links)
        {
            if (l.Required)
            {
                Clearance++;
                Area++;
            }
        }
    }

    /// <summary>Keeps the group's area at the floor of the standing suite it is, which already holds its doors: the last step of the clustering, after every door the links add (#3020).</summary>
    public void HoldKept()
    {
        if (Area < Kept)
        {
            Clearance += Kept - Area;
            Area = Kept;
        }
    }

    /// <summary>Adds weight to the wish to stand beside group to, a new link if none.</summary>
    public void AddLink(int to, double weight)
    {
        foreach (var l in Links)
        {
            if (l.To == to)
            {
                l.Weight += weight;
                return;
            }
        }
        Links.Add(new GroupLink { To = to, Weight = weight });
    }
}

/// <summary>
/// The work ticks a day a conflict costs the stations of one graph node: the node's daily work times
/// (1/Factor - 1), Factor being the catalog need's rate multiplier (NeedRole x0.8 is a quarter more time;
/// NeedTemperature x0.7 outside the band). Kind is NeedRole or NeedTemperature; Role is the role a NeedRole
/// station wants. No other unit.
/// </summary>
public sealed record ConflictCost
{
    public EnvNeedKind Kind { get; init; }

    public string Node { get; init; } = string.Empty;

    public string Def { get; init; } = string.Empty;

    public RoomRole Role { get; init; }

    public int Count { get; set; }

    public double Factor { get; init; }

    public double TicksPerDay { get; set; }
}

/// <summary>
/// A role effect (sleep, patient or prison mood, throne, worship) a room does not give, with the sleepers the
/// graph puts in it. Got is the role the room is scored as instead. Its mood is priced by MoodModel.LostPoints
/// and PriceMood (#2954).
/// </summary>
public sealed record LostRoleEffect(RoleEffect Effect, RoomRole Role, RoomRole Got, int Pawns);

/// <summary>One clustering decision for the caller to journal. A is a graph node; B is the other node of a merge or keep.</summary>
public sealed record ClusterDecision
{
    public string A { get; init; } = string.Empty;

    public string B { get; init; } = string.Empty;

    public string Verdict { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public double Weight { get; init; }

    /// <summary>Names an environment conflict between the two parts, if any; it does not stop a merge.</summary>
    public string Conflict { get; init; } = string.Empty;

    /// <summary>What a merge would add to its parts (a merge or a keep) or what the unsplit pool loses (a split): data for the search (#2877, #2878); nothing here weighs them against flow.</summary>
    public IReadOnlyList<ConflictCost> Costs { get; init; } = [];

    public IReadOnlyList<LostRoleEffect> LostEffects { get; init; } = [];

    /// <summary>What a merge of a filth node with other stations costs or, on a merge, what the room now pays (FilthCost).</summary>
    public IReadOnlyList<FilthCost> Filth { get; init; } = [];
}

/// <summary>The room groups and why they are what they are.</summary>
public sealed record Clustering
{
    public IReadOnlyList<RoomGroup> Groups { get; init; } = [];

    public IReadOnlyList<ClusterDecision> Decisions { get; init; } = [];

    /// <summary>The distinct predicted roles of the groups, sorted.</summary>
    public IReadOnlyList<RoomRole> Roles() =>
        [.. Groups.Select(g => g.Role).Distinct().OrderBy(r => Wire.Name(r), StringComparer.Ordinal)];
}

/// <summary>Count stations of one graph node.</summary>
internal readonly record struct ClusterUnit(int Node, int Count);

/// <summary>What clustering knows of a candidate group.</summary>
internal sealed class GroupEval
{
    public HashSet<string> Lost { get; } = [];

    public int Footprint { get; set; }

    public int Clearance { get; set; }

    public int Area { get; set; }

    public bool Cold { get; set; }

    public bool Band { get; set; }

    public bool Enclosed { get; set; }

    public bool OpenSky { get; set; }

    public bool Clean { get; set; }

    /// <summary>A node's stations spawn filth in the room (StationNode.Filth).</summary>
    public bool Filth { get; set; }

    /// <summary>Every node of the group spawns filth.</summary>
    public bool Closet { get; set; }

    public RoomRole Role { get; set; }

    /// <summary>The group holds a target-sized bedroom (StationNode.Floor).</summary>
    public bool Suite { get; set; }

    /// <summary>The interior cells its target asks for.</summary>
    public int Floor { get; set; }

    /// <summary>The floor of the standing suite (StationNode.Kept).</summary>
    public int Kept { get; set; }

    public IReadOnlyList<ConflictCost> Costs { get; set; } = [];

    public List<LostRoleEffect> Effects { get; } = [];

    /// <summary>The nodes that spawn filth.</summary>
    public List<string> Filthy { get; } = [];

    /// <summary>The other nodes whose effect reads the room's cleanliness.</summary>
    public List<string> Exposed { get; } = [];

    /// <summary>Prices the filthy nodes against the room when it also holds a node that is not filthy.</summary>
    public IReadOnlyList<FilthCost> FilthCosts { get; set; } = [];

    /// <summary>The work-days a day of cleaning the group's filth takes, in every group that holds a filth node (FilthModel.CleaningWorkDays).</summary>
    public double Cleaning { get; set; }

    /// <summary>The meals a day the exposed nodes' stations in the group make.</summary>
    public double Meals { get; set; }

    /// <summary>Each filthy node's butchering load in the group (its runs a day and the work ticks of one), the share of the node its stations here make.</summary>
    public Dictionary<string, FilthRuns> Runs { get; } = [];

    public List<EnvRequirement> Requires()
    {
        var output = new List<EnvRequirement>();
        if (Clean)
        {
            output.Add(EnvRequirement.Clean);
        }
        if (Cold)
        {
            output.Add(EnvRequirement.Cold);
        }
        if (Enclosed)
        {
            output.Add(EnvRequirement.Enclosed);
        }
        if (OpenSky)
        {
            output.Add(EnvRequirement.OpenSky);
        }
        return output;
    }

    /// <summary>
    /// The planned room a group's contents decide over what the game predicts: a closet of filth stations the
    /// game reads as a kitchen (the butcher table scores as one) is the butchery. None when the prediction stands.
    /// </summary>
    public PlannedRole PlannedRole()
    {
        if (Suite)
        {
            return Policy.PlannedRole.Suite;
        }
        if (Closet && Role == RoomRole.Kitchen)
        {
            return Policy.PlannedRole.Butchery;
        }
        return Policy.PlannedRole.None;
    }

    /// <summary>Reports a group some planned room carries (PlannedRoleOfGroup); a barracks, which the game scores a room of several beds, is none.</summary>
    public bool Placeable()
    {
        var group = new RoomGroup { Role = Role, Planned = PlannedRole(), Requires = Requires() };
        return PolicyFunctions.PlannedRoleOfGroup(group).Ok;
    }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// The largest interior area a roofed room can have. A rectangle is roofed while its centre lies within
    /// roofDistance of a wall, so its depth is bounded and its length is bounded by the shell: a w by h
    /// interior has a wall ring of 2(w+h)+4 cells.
    /// </summary>
    internal static int MaxRoomArea(double roofDistance) => RoomLimits(roofDistance).Area;

    /// <summary>The deepest roof-supported square interior and the largest roof-supported interior area.</summary>
    internal static (int Depth, int Area) RoomLimits(double roofDistance)
    {
        if (roofDistance <= 0)
        {
            throw new ClusterException(string.Create(CultureInfo.InvariantCulture, $"roof support distance {roofDistance}"));
        }
        static RoomFootprint Rect(int w, int h)
        {
            var cells = new List<Cell>(w * h);
            for (var z = 1; z <= h; z++)
            {
                for (var x = 1; x <= w; x++)
                {
                    cells.Add(new Cell(x, z));
                }
            }
            return new RoomFootprint(cells, new Cell(1, 0), Rotation.South);
        }
        var depth = 0;
        for (var s = 1; s * s <= ShellMaxInterior; s++)
        {
            try
            {
                if (!Rect(s, s).RoofSupported(roofDistance))
                {
                    break;
                }
            }
            catch (DomainException)
            {
                break;
            }
            depth = s;
        }
        if (depth == 0)
        {
            throw new ClusterException(string.Create(CultureInfo.InvariantCulture, $"no room is roof supported at distance {roofDistance}"));
        }
        var length = Math.Min(((ShellMaxWalls - 4) / 2) - depth, ShellMaxInterior / depth);
        try
        {
            if (!Rect(length, depth).RoofSupported(roofDistance))
            {
                throw new ClusterException($"a {length}x{depth} room is not buildable: not roof supported");
            }
        }
        catch (DomainException e) when (e is not ClusterException)
        {
            throw new ClusterException($"a {length}x{depth} room is not buildable: {e.Message}");
        }
        return (depth, length * depth);
    }

    /// <summary>Groups the stations of the input's graph into rooms.</summary>
    public static Clustering ClusterStations(ClusterInput input)
    {
        var maxArea = MaxRoomArea(input.RoofDistance);
        var c = new Clusterer(input, maxArea);
        var graph = input.Graph;
        foreach (var n in graph.Nodes)
        {
            if (!input.Catalog.Stations.TryGetValue(n.Def, out var st) || st.Shape.Size.X <= 0 || st.Shape.Size.Z <= 0)
            {
                throw new ClusterException($"node {n.Id} has no station shape in the catalog");
            }
        }
        var decisions = new List<ClusterDecision>();
        var units = new List<ClusterUnit>();
        var chunks = new Dictionary<int, List<int>>(); // node index to its unit indexes
        for (var i = 0; i < graph.Nodes.Count; i++)
        {
            var n = graph.Nodes[i];
            if (n.Count <= 0)
            {
                continue;
            }
            foreach (var p in c.Split(i, n.Count, decisions))
            {
                if (!chunks.TryGetValue(i, out var list))
                {
                    list = [];
                    chunks[i] = list;
                }
                list.Add(units.Count);
                units.Add(p);
            }
        }
        List<int> Chunks(int node) => chunks.GetValueOrDefault(node) ?? [];
        var index = new Dictionary<string, int>();
        for (var i = 0; i < graph.Nodes.Count; i++)
        {
            index[graph.Nodes[i].Id] = i;
        }
        var pairs = new List<ClusterPair>();
        foreach (var e in graph.Edges)
        {
            if (!index.TryGetValue(e.A, out var ia) || !index.TryGetValue(e.B, out var ib))
            {
                continue;
            }
            double totA = graph.Nodes[ia].Count, totB = graph.Nodes[ib].Count;
            foreach (var ua in Chunks(ia))
            {
                foreach (var ub in Chunks(ib))
                {
                    var share = units[ua].Count * (double)units[ub].Count / (totA * totB);
                    pairs.Add(new ClusterPair(ua, ub, e.Weight * share, e.Required, e.A, e.B));
                }
            }
        }
        foreach (var rule in input.Pairs)
        {
            // A Together rule is a pair even where the graph has no flow.
            if (rule.Rule != PairRule.Together)
            {
                continue;
            }
            if (!index.TryGetValue(rule.A, out var ia) || !index.TryGetValue(rule.B, out var ib))
            {
                continue;
            }
            foreach (var ua in Chunks(ia))
            {
                foreach (var ub in Chunks(ib))
                {
                    pairs.Add(new ClusterPair(ua, ub, 0, false, rule.A, rule.B));
                }
            }
        }
        pairs = pairs.SortedBy((p, q) =>
        {
            if (p.Required != q.Required)
            {
                return p.Required;
            }
            if (p.Weight != q.Weight)
            {
                return p.Weight > q.Weight;
            }
            if (p.A != q.A)
            {
                return p.A < q.A;
            }
            return p.B < q.B;
        }).ToList();
        var parent = new int[units.Count];
        var members = new List<ClusterUnit>?[units.Count];
        for (var i = 0; i < units.Count; i++)
        {
            parent[i] = i;
            members[i] = [units[i]];
        }
        int Find(int i)
        {
            if (parent[i] != i)
            {
                parent[i] = Find(parent[i]);
            }
            return parent[i];
        }
        void Note(ClusterDecision d)
        {
            if (decisions.Count > 0)
            {
                var last = decisions[^1];
                if (last.A == d.A && last.B == d.B && last.Verdict == d.Verdict && last.Reason == d.Reason)
                {
                    return;
                }
            }
            decisions.Add(d);
        }
        foreach (var p in pairs)
        {
            int ra = Find(p.A), rb = Find(p.B);
            if (ra == rb)
            {
                continue;
            }
            var ea = c.Eval(members[ra]!);
            var eb = c.Eval(members[rb]!);
            List<ClusterUnit> merged = [.. members[ra]!, .. members[rb]!];
            var em = c.Eval(merged);
            var d = new ClusterDecision
            {
                A = p.NodeA,
                B = p.NodeB,
                Verdict = ClusterKeep,
                Weight = p.Weight,
                Costs = CostsAdded(em.Costs, ea.Costs, eb.Costs),
                LostEffects = EffectsAdded(em.Effects, ea.Effects, eb.Effects),
                Conflict = ColdBandConflict(ea, eb) ? ConflictColdBand : string.Empty,
                Filth = FilthConflict(ea, eb) ? em.FilthCosts : [],
            };
            var rule = c.RuleBetween(members[ra]!, members[rb]!);
            if (rule == PairRule.Apart)
            {
                d = d with { Reason = ReasonClusterApart };
            }
            else if (ea.Suite || eb.Suite)
            {
                d = d with { Reason = ReasonClusterSuite };
            }
            else if (Conflict(ea, eb))
            {
                d = d with { Reason = ReasonClusterEnvConflict };
            }
            else if (FilthConflict(ea, eb) && rule != PairRule.Together)
            {
                d = d with { Reason = ReasonClusterFilthPriced };
            }
            else if (!em.Placeable() && ea.Placeable() && eb.Placeable())
            {
                d = d with { Reason = ReasonClusterNoRoom };
            }
            else if (em.Area > c.MaxArea)
            {
                d = d with { Reason = ReasonClusterArea };
            }
            else if (NewLoss(em, ea, eb) && rule != PairRule.Together)
            {
                d = d with { Reason = ReasonClusterLostEffect };
            }
            else
            {
                d = d with { Verdict = ClusterMerge, Reason = rule == PairRule.Together ? ReasonClusterTogether : ReasonClusterAffinity };
                int lo = Math.Min(ra, rb), hi = Math.Max(ra, rb);
                parent[hi] = lo;
                members[lo] = merged;
                members[hi] = null;
            }
            Note(d);
        }
        var roots = new List<int>();
        for (var i = 0; i < units.Count; i++)
        {
            if (Find(i) == i)
            {
                roots.Add(i);
            }
        }
        var rootGroup = new Dictionary<int, int>();
        for (var gi = 0; gi < roots.Count; gi++)
        {
            rootGroup[roots[gi]] = gi;
        }
        var groups = new List<RoomGroup>();
        for (var gi = 0; gi < roots.Count; gi++)
        {
            var r = roots[gi];
            var e = c.Eval(members[r]!);
            var g = new RoomGroup
            {
                Id = gi,
                Footprint = e.Footprint,
                Clearance = e.Clearance,
                Area = e.Area,
                Kept = e.Kept,
                Role = e.Role,
                Planned = e.PlannedRole(),
                Requires = e.Requires(),
                Costs = e.Costs,
                LostEffects = e.Effects,
                Filth = e.FilthCosts,
                Cleaning = e.Cleaning,
            };
            var byNode = new Dictionary<int, int>();
            foreach (var m in members[r]!)
            {
                byNode[m.Node] = byNode.GetValueOrDefault(m.Node) + m.Count;
            }
            foreach (var n in byNode.Keys.Order())
            {
                var node = graph.Nodes[n];
                g.Stations.Add(new GroupStation(node.Id, node.Def, node.Purpose, byNode[n]));
            }
            if (e.Cold)
            {
                if (c.HasCooler)
                {
                    g.Stations.Add(new GroupStation("cooler", c.Cooler, StationPurpose.Cool, 1));
                }
                else
                {
                    decisions.Add(new ClusterDecision { A = g.Stations[0].Node, Verdict = ClusterUnsatisfied, Reason = ReasonClusterNoCooler });
                }
            }
            groups.Add(g);
        }
        var links = new Dictionary<(int, int), GroupLink>();
        foreach (var p in pairs)
        {
            int ga = rootGroup[Find(p.A)], gb = rootGroup[Find(p.B)];
            if (ga == gb)
            {
                continue;
            }
            foreach (var k in new[] { (ga, gb), (gb, ga) })
            {
                if (!links.TryGetValue(k, out var l))
                {
                    l = new GroupLink { To = k.Item2 };
                    links[k] = l;
                }
                l.Weight += p.Weight;
                l.Required = l.Required || p.Required;
            }
        }
        foreach (var (k, l) in links)
        {
            groups[k.Item1].Links.Add(l);
        }

        // The outside node holds no station: its edges weigh the groups holding the other end, in proportion
        // to the node's stations in each. A pen stands outdoors, so its edges are the outside node's (#2996).
        foreach (var e in graph.Edges)
        {
            bool outA = graph.Outdoor(e.A), outB = graph.Outdoor(e.B);
            if (outA == outB)
            {
                continue;
            }
            var other = outA ? e.B : e.A;
            if (!index.TryGetValue(other, out var ia) || graph.Nodes[ia].Count <= 0)
            {
                continue;
            }
            foreach (var u in Chunks(ia))
            {
                groups[rootGroup[Find(u)]].Outside += e.Weight * units[u].Count / graph.Nodes[ia].Count;
            }
        }
        foreach (var g in groups)
        {
            g.Links = [.. g.Links.OrderBy(l => l.To)];
            g.AddRequiredDoors();
        }
        var (depth, _) = RoomLimits(input.RoofDistance);
        IReadOnlyList<RoomGroup> current = groups;
        foreach (var r in graph.Rooms)
        {
            if (!r.Joins)
            {
                continue;
            }
            var (next, d) = AddCirculation(current, r, depth);
            current = next;
            decisions.Add(d);
        }
        var all = current.ToList();

        // The other room nodes come last: circulation counts the base's rooms.
        var first = all.Count;
        foreach (var r in graph.Rooms)
        {
            if (r.Joins)
            {
                continue;
            }
            c.AddRoom(all, decisions, r);
        }
        LinkRooms(all, first, graph.Rooms);
        FlowRooms(all, first, graph.Rooms, (node, add) =>
        {
            if (index.TryGetValue(node, out var ia) && graph.Nodes[ia].Count > 0)
            {
                foreach (var u in Chunks(ia))
                {
                    add(rootGroup[Find(u)], units[u].Count / (double)graph.Nodes[ia].Count);
                }
            }
        });
        foreach (var g in all)
        {
            g.HoldKept();
        }
        return new Clustering { Groups = all, Decisions = decisions };
    }

    /// <summary>
    /// Links the groups first.. of room nodes to the nodes they stand beside: a required link both ways, whose
    /// door keeps a threshold clear as every required link does (AddRequiredDoors).
    /// </summary>
    private static void LinkRooms(List<RoomGroup> groups, int first, IReadOnlyList<RoomNode> nodes)
    {
        var at = new Dictionary<string, int>();
        var i = first;
        foreach (var n in nodes)
        {
            if (!n.Joins)
            {
                at[n.Id] = i;
                i++;
            }
        }
        foreach (var n in nodes)
        {
            var ok = at.TryGetValue(n.Id, out var a);
            var ok2 = at.TryGetValue(n.Beside, out var b);
            if (n.Joins || n.Beside == string.Empty || !ok || !ok2)
            {
                continue;
            }
            groups[a].Links.Add(new GroupLink { To = b, Required = true });
            groups[b].Links.Add(new GroupLink { To = a, Required = true });
            foreach (var g in new[] { a, b })
            {
                if (ExactFloor(groups[g].Planned))
                {
                    continue;
                }
                groups[g].Clearance++;
                groups[g].Area++;
            }
        }
    }

    /// <summary>
    /// Links the groups first.. of room nodes to the groups holding the station nodes of their flows, a wish
    /// weighted by the trips (split over the groups in proportion to the node's stations in each, as the outside
    /// node's edges are). holders calls add for each group holding a share of a node; a node the graph lacks
    /// adds none, so a flow to nothing prices nothing.
    /// </summary>
    private static void FlowRooms(List<RoomGroup> groups, int first, IReadOnlyList<RoomNode> nodes, Action<string, Action<int, double>> holders)
    {
        var i = first;
        foreach (var n in nodes)
        {
            if (n.Joins)
            {
                continue;
            }
            var room = i;
            i++;
            foreach (var f in n.Flows)
            {
                if (f.Weight <= 0)
                {
                    continue;
                }
                holders(f.With, (g, share) =>
                {
                    groups[room].AddLink(g, f.Weight * share);
                    groups[g].AddLink(room, f.Weight * share);
                });
            }
            groups[room].Links = [.. groups[room].Links.OrderBy(l => l.To)];
        }
    }

    /// <summary>
    /// Reports two groups whose environments exclude each other: a station under an open sky cannot share a
    /// roofed room. Cold against a temperature band (ColdBandConflict) and filth against other stations
    /// (FilthConflict) are not exclusions: they have a price.
    /// </summary>
    private static bool Conflict(GroupEval a, GroupEval b) => (a.Enclosed && b.OpenSky) || (a.OpenSky && b.Enclosed);

    /// <summary>
    /// Reports a station whose work spawns filth beside stations that are not such: the filth lowers the
    /// cleanliness every station and sleeper of the room is read at (a cook's food-poisoning chance among
    /// them), which FilthCost prices.
    /// </summary>
    private static bool FilthConflict(GroupEval a, GroupEval b) => (a.Filth || b.Filth) && !(a.Closet && b.Closet);

    /// <summary>Reports a cold group beside a station that works in a temperature band.</summary>
    private static bool ColdBandConflict(GroupEval a, GroupEval b) => (a.Cold && b.Band) || (a.Band && b.Cold);

    /// <summary>The costs a merged room has beyond its two parts': per cost key, the merged ticks less the parts'.</summary>
    private static List<ConflictCost> CostsAdded(IReadOnlyList<ConflictCost> merged, IReadOnlyList<ConflictCost> a, IReadOnlyList<ConflictCost> b)
    {
        var output = new List<ConflictCost>();
        foreach (var m in merged)
        {
            var ticks = m.TicksPerDay;
            foreach (var part in new[] { a, b })
            {
                foreach (var p in part)
                {
                    if (p.Kind == m.Kind && p.Node == m.Node && p.Role == m.Role)
                    {
                        ticks -= p.TicksPerDay;
                    }
                }
            }
            if (ticks > 1e-9)
            {
                output.Add(m with { TicksPerDay = ticks });
            }
        }
        return output;
    }

    /// <summary>The lost role effects of a merged room that neither part lost.</summary>
    private static List<LostRoleEffect> EffectsAdded(IReadOnlyList<LostRoleEffect> merged, IReadOnlyList<LostRoleEffect> a, IReadOnlyList<LostRoleEffect> b)
    {
        var output = new List<LostRoleEffect>();
        foreach (var m in merged)
        {
            bool Known(IReadOnlyList<LostRoleEffect> list) => list.Any(x => x.Effect == m.Effect && x.Role == m.Role);
            if (!Known(a) && !Known(b))
            {
                output.Add(m);
            }
        }
        return output;
    }

    /// <summary>Reports a merged group that loses an effect neither part lost.</summary>
    private static bool NewLoss(GroupEval merged, GroupEval a, GroupEval b) =>
        merged.Lost.Any(k => !a.Lost.Contains(k) && !b.Lost.Contains(k));

    /// <summary>
    /// The instance state of a node's stations: the bed state the purpose gives a humanlike bed.
    /// </summary>
    internal static RoleCondition PieceState(StationPurpose purpose, Station st)
    {
        if (!HumanlikeBed(st))
        {
            return RoleCondition.Always;
        }
        return purpose switch
        {
            StationPurpose.SleepMed => RoleCondition.BedMedical,
            StationPurpose.SleepPrison => RoleCondition.BedPrisoner,
            _ => RoleCondition.BedFree,
        };
    }

    /// <summary>
    /// The catalog's cooling station that draws the least power per heat moved (the defs' energyPerSecond and
    /// power comp), by name when equal.
    /// </summary>
    internal static (string Def, bool Ok) BestCooler(StationCatalog cat)
    {
        var best = string.Empty;
        var bestRatio = 0.0;
        foreach (var def in cat.Coolers())
        {
            var st = cat.Stations[def];
            if (st.Shape.Size.X <= 0 || st.Shape.Size.Z <= 0)
            {
                continue;
            }
            var ratio = st.Cooling.PowerDraw / st.Cooling.HeatPerSecond;
            if (best == string.Empty || ratio < bestRatio)
            {
                best = def;
                bestRatio = ratio;
            }
        }
        return (best, best != string.Empty);
    }
}

/// <summary>A pair of units clustering may merge, by descending affinity.</summary>
internal readonly record struct ClusterPair(int A, int B, double Weight, bool Required, string NodeA, string NodeB);

internal sealed class Clusterer
{
    private readonly ClusterInput _in;

    public Clusterer(ClusterInput input, int maxArea)
    {
        _in = input;
        MaxArea = maxArea;
        (Cooler, HasCooler) = PolicyFunctions.BestCooler(input.Catalog);
        if (HasCooler)
        {
            var size = input.Catalog.Stations[Cooler].Shape.Size;
            CoolerSize = size.X * size.Z;
        }
    }

    public int MaxArea { get; }

    /// <summary>The cooling station a cold group carries.</summary>
    public string Cooler { get; }

    /// <summary>The cooler's footprint in cells.</summary>
    public int CoolerSize { get; }

    /// <summary>False when the catalog has no cooling station.</summary>
    public bool HasCooler { get; }

    private StationNode Node(ClusterUnit u) => _in.Graph.Nodes[u.Node];

    /// <summary>The rule of the pairs that name a node of a and a node of b: Apart when any is, else Together when any is, else None.</summary>
    public PairRule RuleBetween(IReadOnlyList<ClusterUnit> a, IReadOnlyList<ClusterUnit> b)
    {
        var output = PairRule.None;
        foreach (var p in _in.Pairs)
        {
            foreach (var ua in a)
            {
                foreach (var ub in b)
                {
                    if (!p.Names(Node(ua).Id, Node(ub).Id))
                    {
                        continue;
                    }
                    if (p.Rule == PairRule.Apart)
                    {
                        return PairRule.Apart;
                    }
                    output = PairRule.Together;
                }
            }
        }
        return output;
    }

    private List<RoomPiece> Pieces(IReadOnlyList<ClusterUnit> us)
    {
        var output = new List<RoomPiece>();
        foreach (var u in us)
        {
            var n = Node(u);
            var st = _in.Catalog.Stations[n.Def];
            for (var k = 0; k < u.Count; k++)
            {
                output.Add(new RoomPiece { Def = n.Def, State = PolicyFunctions.PieceState(n.Purpose, st) });
            }
        }
        return output;
    }

    public GroupEval Eval(IReadOnlyList<ClusterUnit> us)
    {
        var pieces = Pieces(us);
        var pred = PolicyFunctions.PredictRole(_in.Catalog, pieces);
        var e = new GroupEval { Role = pred.Role };
        var intended = new List<RoomRole>();
        var sleepers = new Dictionary<RoomRole, int>(); // sleepers whose bed is for the role
        foreach (var p in pieces)
        {
            var alone = PolicyFunctions.PredictRole(_in.Catalog, [p]);
            if (!intended.Contains(alone.Role))
            {
                intended.Add(alone.Role);
            }
            var cp = _in.Catalog.Stations[p.Def].Capacity;
            if (cp.Kind == CapacityKind.Sleepers)
            {
                sleepers[alone.Role] = sleepers.GetValueOrDefault(alone.Role) + cp.Slots;
            }
        }
        foreach (var role in intended)
        {
            foreach (var l in PolicyFunctions.LostEffects(_in.Catalog, role, pieces))
            {
                var key = $"{Wire.Name(l.Effect)}|{Wire.Name(l.Role)}|{l.Def}";
                if (!e.Lost.Contains(key) && l.Effect != RoleEffect.BenchSpeed)
                {
                    e.Effects.Add(new LostRoleEffect(l.Effect, l.Role, l.Got, sleepers.GetValueOrDefault(l.Role)));
                }
                e.Lost.Add(key);
            }
        }
        e.Closet = us.Count > 0;
        foreach (var u in us)
        {
            var n = Node(u);
            var size = _in.Catalog.Stations[n.Def].Shape.Size;
            e.Filth = e.Filth || n.Filth;
            e.Suite = e.Suite || n.Floor > 0;
            e.Floor = Math.Max(e.Floor, n.Floor);
            e.Kept = Math.Max(e.Kept, n.Kept);
            e.Closet = e.Closet && n.Filth;
            if (n.Filth && !e.Filthy.Contains(n.Id))
            {
                e.Filthy.Add(n.Id);
            }
            if (n.Filth && n.Count > 0)
            {
                e.Runs[n.Id] = e.Runs.GetValueOrDefault(n.Id).Plus(new FilthRuns { Runs = n.FilthRuns * u.Count / n.Count, Ticks = n.FilthWork });
            }
            e.Footprint += u.Count * size.X * size.Z;
            e.Clearance += u.Count;
            var exposedUnit = false;
            foreach (var need in n.Needs)
            {
                switch (need.Kind)
                {
                    case EnvNeedKind.Cold:
                        e.Cold = true;
                        break;
                    case EnvNeedKind.Temperature:
                        e.Band = true;
                        break;
                    case EnvNeedKind.Indoors:
                        e.Enclosed = true;
                        break;
                    case EnvNeedKind.OpenSky:
                        e.OpenSky = true;
                        break;
                    case EnvNeedKind.RoomStat:
                        e.Clean = e.Clean || need.Input == CleanlinessStat;
                        if (need.Input == CleanlinessStat && !e.Exposed.Contains(n.Id))
                        {
                            e.Exposed.Add(n.Id);
                        }
                        if (need.Input == CleanlinessStat && n.Count > 0 && !exposedUnit)
                        {
                            exposedUnit = true;
                            e.Meals += n.Meals * u.Count / n.Count;
                        }
                        break;
                }
            }
        }
        e.Costs = Costs(us, e);
        if (e.Cold && HasCooler)
        {
            e.Footprint += CoolerSize;
            e.Clearance++;
        }
        e.Clearance++; // the door threshold
        if (e.Suite)
        {
            // The suite's floor is what its target needs, open ground around the bed.
            e.Clearance = Math.Max(e.Clearance, e.Floor - e.Footprint);
        }
        e.Area = e.Footprint + e.Clearance;
        if (e.Filth)
        {
            e.Cleaning = _in.Catalog.Filth.CleaningWorkDays(e.Runs);
        }
        if (e.Filth && !e.Closet)
        {
            e.FilthCosts = _in.Catalog.Filth.FilthCosts(e.Filthy, e.Exposed, e.Area, e.Meals, e.Runs);
        }
        return e;
    }

    /// <summary>
    /// The work ticks a day the units' stations lose in a room of evaluation e: a bench whose NeedRole names
    /// another role than the room's, and a station with a temperature band in a room kept cold. A unit holds
    /// its share of the node's daily work (count over the node's count); a need whose Factor is not a slowdown
    /// costs nothing.
    /// </summary>
    private List<ConflictCost> Costs(IReadOnlyList<ClusterUnit> us, GroupEval e)
    {
        var output = new List<ConflictCost>();
        foreach (var u in us)
        {
            var n = Node(u);
            if (n.DailyWork <= 0 || n.Count <= 0)
            {
                continue;
            }
            foreach (var need in n.Needs)
            {
                if (need.Factor <= 0 || need.Factor >= 1)
                {
                    continue;
                }
                RoomRole role = default;
                switch (need.Kind)
                {
                    case EnvNeedKind.Role:
                        if (need.Role == e.Role)
                        {
                            continue;
                        }
                        role = need.Role;
                        break;
                    case EnvNeedKind.Temperature:
                        if (!e.Cold)
                        {
                            continue;
                        }
                        break;
                    default:
                        continue;
                }
                var ticks = n.DailyWork * u.Count / n.Count * ((1 / need.Factor) - 1);
                var i = output.FindIndex(x => x.Kind == need.Kind && x.Node == n.Id && x.Role == role);
                if (i >= 0)
                {
                    output[i].Count += u.Count;
                    output[i].TicksPerDay += ticks;
                    continue;
                }
                output.Add(new ConflictCost { Kind = need.Kind, Node = n.Id, Def = n.Def, Role = role, Count = u.Count, Factor = need.Factor, TicksPerDay = ticks });
            }
        }
        return [.. output.OrderBy(x => x.Node, StringComparer.Ordinal).ThenBy(x => Wire.Name(x.Kind), StringComparer.Ordinal).ThenBy(x => Wire.Name(x.Role), StringComparer.Ordinal)];
    }

    /// <summary>Divides a pool of count stations of node n by halves until every part is lossless and fits the area limit; a single station is never split.</summary>
    public List<ClusterUnit> Split(int n, int count, List<ClusterDecision> decisions)
    {
        var u = new ClusterUnit(n, count);
        var e = Eval([u]);
        var reason = string.Empty;
        if (e.Lost.Count > 0)
        {
            reason = PolicyConstants.ReasonClusterLostEffect;
        }
        else if (e.Area > MaxArea)
        {
            reason = PolicyConstants.ReasonClusterArea;
        }
        if (reason == string.Empty || count < 2)
        {
            return [u];
        }
        decisions.Add(new ClusterDecision { A = Node(u).Id, Verdict = PolicyConstants.ClusterSplit, Reason = reason, Costs = e.Costs, LostEffects = e.Effects });
        var left = Split(n, (count + 1) / 2, decisions);
        var right = Split(n, count / 2, decisions);
        return [.. left, .. right];
    }

    /// <summary>Appends the group of a station-less room node: its asked floor, plus the cooling station a cold group carries.</summary>
    public void AddRoom(List<RoomGroup> groups, List<ClusterDecision> decisions, RoomNode node)
    {
        var g = new RoomGroup { Id = groups.Count, Clearance = node.Area, Planned = node.Role, Width = node.Width, Markers = node.Markers };
        var d = new ClusterDecision { A = node.Id, Verdict = PolicyConstants.ClusterRoom, Reason = node.Reason };
        if (node.Cold)
        {
            g.Requires = [EnvRequirement.Cold];
            if (HasCooler)
            {
                g.Stations.Add(new GroupStation("cooler", Cooler, StationPurpose.Cool, 1));
                g.Footprint += CoolerSize;
                g.Clearance++;
            }
            else
            {
                d = new ClusterDecision { A = node.Id, Verdict = PolicyConstants.ClusterUnsatisfied, Reason = PolicyConstants.ReasonClusterNoCooler };
            }
        }
        g.Area = g.Footprint + g.Clearance;
        groups.Add(g);
        decisions.Add(d);
    }
}
