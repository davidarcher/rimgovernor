namespace RimGovernor.Brain.Policy;

/// <summary>A colonist's time in the layout's units: WorkDayW the watts a work-day is worth (the food upkeep) and CellWalkDays the work-days a colonist spends walking one cell.</summary>
public readonly record struct WorkPrice(double WorkDayW, double CellWalkDays)
{
    /// <summary>A price with both numbers known.</summary>
    public bool Valid() => QuestCapacityForecast.FinitePositive(WorkDayW) && QuestCapacityForecast.FinitePositive(CellWalkDays);

}

/// <summary>The layout-specific walking price. Every other cost belongs to canonical forecast consequences.</summary>
public sealed record RegroupPrice
{
    public WorkPrice Work { get; init; }
}

/// <summary>The chain's input up to the embedding. Embed.Groups and Embed.Fields are set per trial.</summary>
public sealed record RegroupInput
{
    public required ClusterInput Cluster { get; init; }
    public required EmbedInput Embed { get; init; }
    public RegroupPrice Price { get; init; } = new();

    /// <summary>The standing rooms whose native role is not the one PredictRole gives their contents (GuardNativeRoles).</summary>
    public IReadOnlyList<RoleMismatch> Mismatches { get; init; } = [];
}

/// <summary>A candidate adoption decision. Before is zero; After the shared canonical ledger delta in capability-days.
/// Walk is the additional layout walking delta, counted once. Effects names the canonical credit losses.</summary>
public sealed record RegroupDecision
{
    public string Kind { get; init; } = "";
    public string Verdict { get; init; } = "";
    public string Reason { get; init; } = "";
    public string A { get; init; } = "";
    public string B { get; init; } = "";
    public double Before { get; init; }
    public double After { get; init; }
    public double Walk { get; init; }

    /// <summary>The lost effects (RoleEffect wire names) of the room the move's pair shares after it, sorted; empty when the pair shares no room.</summary>
    public IReadOnlyList<string> Effects { get; init; } = [];
}

/// <summary>The groups the search ended on. Embed is the embedding's input over those groups, Embedding its result.</summary>
public sealed record Regrouping
{
    public StationGraph Graph { get; init; } = default!;
    public Clustering Clustering { get; init; } = new();
    public EmbedInput Embed { get; init; } = new();
    public Embedding Embedding { get; init; } = new();
    public IReadOnlyList<RegroupDecision> Decisions { get; init; } = [];
}

/// <summary>One grouping, clustered, embedded and priced.</summary>
internal sealed class RegroupTrial
{
    public StationGraph Graph { get; init; } = default!;
    public IReadOnlyList<NodePair> Pairs { get; init; } = [];
    public Clustering Clustering { get; init; } = new();
    public EmbedInput Embed { get; init; } = new();
    public Embedding Embedding { get; init; } = new();
    public Dictionary<int, bool> Guarded { get; } = [];

    /// <summary>Names the grouping: each room's nodes and counts.</summary>
    public string Signature()
    {
        var rooms = Clustering.Groups.Select(g => string.Join(",", g.Stations.Select(s => $"{s.Node}:{s.Count}"))).ToList();
        rooms.Sort(StringComparer.Ordinal);
        return string.Join("|", rooms);
    }

    public Regrouping Result(IReadOnlyList<RegroupDecision> decisions) => new() { Graph = Graph, Clustering = Clustering, Embed = Embed, Embedding = Embedding, Decisions = decisions };
}

internal sealed record RegroupProposal(string Kind, string A, string B, IReadOnlyList<NodePair> Rules);

internal sealed class Regrouper(RegroupInput input)
{
    public RegroupInput In { get; } = input;

    public RegroupTrial Evaluate(IReadOnlyList<NodePair> pairs)
    {
        var ci = In.Cluster with { Pairs = pairs };
        var cl = LayoutClusterRules.ClusterStations(ci);
        var embed = In.Embed with { Groups = cl.Groups, Fields = StationGraph.FieldEmbedLinks(ci.Graph, cl.Groups) };
        var emb = Embedding.Embed(embed);
        var t = new RegroupTrial { Graph = ci.Graph, Pairs = pairs, Clustering = cl, Embed = embed, Embedding = emb };
        foreach (var group in GuardedGroups(cl.Groups)) t.Guarded[group] = true;
        return t;
    }

    /// <summary>A room whose stations are for more than one role (each station's role alone): the room PredictRole has to weigh, so the one a mismatch can be blamed on. A room of one role is the same prediction however it is cut.</summary>
    private bool Mixed(RoomGroup g)
    {
        var cat = In.Cluster.Catalog;
        var roles = new List<RoomRole>();
        foreach (var s in g.Stations)
        {
            if (s.Purpose.ClusterAdded())
            {
                continue;
            }
            RoomRole role;
            try
            {
                role = RoomRoleRules.PredictRole(cat, [new RoomPiece { Def = s.Def, State = LayoutClusterRules.PieceState(s.Purpose, cat.Stations[s.Def]) }]).Role;
            }
            catch (DomainException)
            {
                return false;
            }
            if (!roles.Contains(role))
            {
                roles.Add(role);
            }
        }
        return roles.Count > 1;
    }

    /// <summary>Mixed standing groups whose observed native role disproves the prediction: splits are candidates, not separately priced penalties.</summary>
    private HashSet<int> GuardedGroups(IReadOnlyList<RoomGroup> groups) =>
        LayoutStationPlanRules.MatchGroupRooms(groups, In.Embed.Built)
            .Where(match => In.Mismatches.Any(mismatch => groups[match.Group].Role == mismatch.Predicted && match.Rooms.Contains(mismatch.Room)) && Mixed(groups[match.Group]))
            .Select(match => groups[match.Group].Id).ToHashSet();
    /// <summary>The moves open from a grouping, in a fixed order.</summary>
    public static List<RegroupProposal> Candidates(RegroupTrial cur)
    {
        var output = new List<RegroupProposal>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(RegroupProposal m)
        {
            var key = m.Kind + " " + string.Join(" ", m.Rules.Select(r => $"{{{r.A} {r.B} {(int)r.Rule}}}"));
            if (seen.Add(key))
            {
                output.Add(m);
            }
        }

        // merge: pairs the seed kept for a lost bench speed alone.
        foreach (var d in cur.Clustering.Decisions)
        {
            if (d.Verdict != LayoutClusterRules.ClusterKeep || d.Reason != LayoutClusterRules.ReasonClusterLostEffect || d.LostEffects.Count > 0 || d.A == "" || d.B == "" || d.A == d.B || LayoutRegroupRules.RuleOf(cur.Pairs, d.A, d.B) != PairRule.None)
            {
                continue;
            }
            Add(new RegroupProposal(LayoutRegroupRules.RegroupMerge, d.A, d.B, [new NodePair(d.A, d.B, PairRule.Together)]));
        }

        // A separated filth node may share the room it flows to; canonical credits price every affected function.
        foreach (var d in cur.Clustering.Decisions)
        {
            if (d.Verdict != LayoutClusterRules.ClusterKeep || d.Reason != LayoutClusterRules.ReasonClusterFilthPriced || d.A == "" || d.B == "" || d.A == d.B || LayoutRegroupRules.RuleOf(cur.Pairs, d.A, d.B) != PairRule.None)
            {
                continue;
            }
            Add(new RegroupProposal(LayoutRegroupRules.RegroupMerge, d.A, d.B, [new NodePair(d.A, d.B, PairRule.Together)]));
        }

        // Propose sharing every role conflict; canonical native role/stage and sleep credits decide adoption.
        foreach (var d in cur.Clustering.Decisions)
        {
            if (d.Verdict != LayoutClusterRules.ClusterKeep || d.Reason != LayoutClusterRules.ReasonClusterLostEffect || d.LostEffects.Count == 0
                || d.A == "" || d.B == "" || d.A == d.B || LayoutRegroupRules.RuleOf(cur.Pairs, d.A, d.B) != PairRule.None)
            {
                continue;
            }
            Add(new RegroupProposal(LayoutRegroupRules.RegroupMerge, d.A, d.B, [new NodePair(d.A, d.B, PairRule.Together)]));
        }
        var groups = cur.Clustering.Groups;
        foreach (var g in groups)
        {
            var nodes = LayoutRegroupRules.RoomNodes(g);
            if (nodes.Count < 2)
            {
                continue;
            }
            var suffers = new HashSet<string>(g.Costs.Select(c => c.Node), StringComparer.Ordinal);
            var whole = g.LostEffects.Count > 0 || cur.Guarded.GetValueOrDefault(g.Id); // a room that lost a role: any pair may be the cause

            // split: the pair of nodes whose room pays.
            for (var i = 0; i < nodes.Count; i++)
            {
                for (var j = i + 1; j < nodes.Count; j++)
                {
                    string a = nodes[i], b = nodes[j];
                    if ((whole || suffers.Contains(a) || suffers.Contains(b)) && LayoutRegroupRules.RuleOf(cur.Pairs, a, b) != PairRule.Apart)
                    {
                        Add(new RegroupProposal(LayoutRegroupRules.RegroupSplit, a, b, [new NodePair(a, b, PairRule.Apart)]));
                    }
                }
            }

            // move-a-need: a node that pays goes to the room of the role it wants.
            foreach (var c in g.Costs)
            {
                foreach (var h in groups)
                {
                    if (h.Id == g.Id || LayoutRegroupRules.RoomNodes(h).Count == 0 || LayoutRegroupRules.HoldsNode(h, c.Node) || !Target(c, g, h))
                    {
                        continue;
                    }
                    var first = LayoutRegroupRules.RoomNodes(h)[0];
                    var rules = new List<NodePair> { new(c.Node, first, PairRule.Together) };
                    foreach (var o in nodes)
                    {
                        if (o != c.Node)
                        {
                            rules.Add(new NodePair(c.Node, o, PairRule.Apart));
                        }
                    }
                    Add(new RegroupProposal(LayoutRegroupRules.RegroupMove, c.Node, first, rules));
                }
            }
        }
        // Seed diagnostics order promising proposals; they cannot veto a canonical trial.
        // Native predictions and the forecast can expose costs absent from the conservative
        // clustering (including a safety floor in an otherwise quiet shared bedroom).
        foreach (var group in groups)
        {
            var nodes = LayoutRegroupRules.RoomNodes(group);
            for (var i = 0; i < nodes.Count; i++)
                for (var j = i + 1; j < nodes.Count; j++)
                    if (LayoutRegroupRules.RuleOf(cur.Pairs, nodes[i], nodes[j]) != PairRule.Apart)
                        Add(new RegroupProposal(LayoutRegroupRules.RegroupSplit, nodes[i], nodes[j], [new(nodes[i], nodes[j], PairRule.Apart)]));
            foreach (var other in groups.Where(other => other.Id > group.Id))
                foreach (var a in nodes)
                    foreach (var b in LayoutRegroupRules.RoomNodes(other))
                        if (LayoutRegroupRules.RuleOf(cur.Pairs, a, b) != PairRule.Together)
                            Add(new RegroupProposal(LayoutRegroupRules.RegroupMerge, a, b, [new(a, b, PairRule.Together)]));
        }
        return output;
    }

    /// <summary>A room h the station class of cost c may move to from g: a room of the role a slowed bench wants, or a room beside g that is not kept cold for a band station.</summary>
    private static bool Target(ConflictCost c, RoomGroup g, RoomGroup h)
    {
        if (c.Kind == EnvNeedKind.Role)
        {
            return h.Role == c.Role;
        }
        if (c.Kind == EnvNeedKind.Temperature)
        {
            return !h.Requires.Contains(EnvRequirement.Cold) && g.Links.Any(l => l.To == h.Id);
        }
        return false;
    }
}

/// <summary>Proposes geometry from the conservative station clustering and compares candidates through canonical consequences.
/// The existing seeded eight-by-four proposal budget bounds the search; no language model is called.</summary>
public static partial class LayoutRegroupRules
{
    public const string RegroupMerge = "merge";
    public const string RegroupSplit = "split";
    public const string RegroupMove = "move";

    public const string RegroupApplied = "applied";
    public const string RegroupKept = "kept";

    // The search budget, as SiteSearchTop / SiteSearchIters bound the embedding's: moves tried a round and rounds, each trial one clustering and one embedding.
    internal const int RegroupProposals = LayoutSiteGroundRules.SiteSearchTop;
    internal const int RegroupReview = LayoutSiteGroundRules.SiteSearchTop / 2;

    /// <summary>Prices a colonist's time from the station facts: the lamp price's own work-day and the walk of a cell at the game's ticks a second and a colonist's move speed. Invalid while the work-day has no price (StationWorkDayOf) or the game's tick rate is unknown.</summary>
    public static WorkPrice StationWorkPrice(StationFacts f)
    {
        var ticks = f.Catalog.Climate.TicksPerSecond;
        if (!QuestCapacityForecast.FinitePositive(ticks) || LayoutLightRules.StationWorkDayOf(f) is not StationWorkDay.Priced day)
        {
            return default;
        }
        var p = new WorkPrice(day.W, CellWalkDays(ticks));
        return p.Valid() ? p : default;
    }

    /// <summary>The work-days a colonist spends walking one cell at the game's ticks a second (WorkPrice.CellWalkDays).</summary>
    internal static double CellWalkDays(double ticksPerSecond) => ticksPerSecond / CombatManhunterRules.ColonistMoveSpeed / GameTime.TicksPerDay;

    /// <summary>The conservative geometry seed; economic adoption always uses RegroupAsync.</summary>
    internal static Regrouping Seed(RegroupInput input) => new Regrouper(input).Evaluate(WithRules([], input.Cluster.Pairs)).Result([]);

    /// <summary>At most n of the moves, in their listed order: all of them, or n drawn from the seeded stream.</summary>
    internal static List<RegroupProposal> Sample(List<RegroupProposal> moves, int n, SearchRand rng)
    {
        if (moves.Count <= n)
        {
            return moves;
        }
        var idx = Enumerable.Range(0, moves.Count).ToArray();
        for (var i = 0; i < n; i++)
        {
            var j = i + rng.Intn(idx.Length - i);
            (idx[i], idx[j]) = (idx[j], idx[i]);
        }
        var chosen = idx.Take(n).Order().ToList();
        return [.. chosen.Select(i => moves[i])];
    }

    private static (string, string) PairKey(string a, string b) => string.CompareOrdinal(b, a) < 0 ? (b, a) : (a, b);

    /// <summary>The rule set with rules added; a rule replaces any on the same pair.</summary>
    internal static List<NodePair> WithRules(IReadOnlyList<NodePair> pairs, IReadOnlyList<NodePair> rules)
    {
        var replaced = new HashSet<(string, string)>(rules.Select(p => PairKey(p.A, p.B)));
        var output = pairs.Where(p => !replaced.Contains(PairKey(p.A, p.B))).ToList();
        foreach (var p in rules)
        {
            var (a, b) = PairKey(p.A, p.B);
            output.Add(p with { A = a, B = b });
        }

        // A stable sort, as slices.SortFunc is not, on a key that is total over the pairs the callers build.
        return [.. output.OrderBy(p => p.A, StringComparer.Ordinal).ThenBy(p => p.B, StringComparer.Ordinal)];
    }

    internal static PairRule RuleOf(IReadOnlyList<NodePair> pairs, string a, string b)
    {
        foreach (var p in pairs)
        {
            if (p.Names(a, b))
            {
                return p.Rule;
            }
        }
        return PairRule.None;
    }

    /// <summary>The graph nodes a room holds, sorted; the stations clustering adds (a cold room's cooler, a babies' room's play buildings) are no node of the graph.</summary>
    internal static List<string> RoomNodes(RoomGroup g)
    {
        var output = g.Stations.Where(s => !s.Purpose.ClusterAdded()).Select(s => s.Node).ToList();
        output.Sort(StringComparer.Ordinal);
        return output;
    }

    internal static bool HoldsNode(RoomGroup g, string node) => RoomNodes(g).Contains(node);
}
