namespace RimGovernor.Brain.Policy;

/// <summary>A colonist's time in the layout's units: WorkDayW the watts a work-day is worth (the food upkeep) and CellWalkDays the work-days a colonist spends walking one cell.</summary>
public readonly record struct WorkPrice(double WorkDayW, double CellWalkDays)
{
    /// <summary>A price with both numbers known.</summary>
    public bool Valid() => PolicyFunctions.FinitePositive(WorkDayW) && PolicyFunctions.FinitePositive(CellWalkDays);

    /// <summary>A work-day count in score points: a point is a cell walked by a movement (PlanWeights.Walk), a cell a CellWalkDays of a work-day.</summary>
    public double Points(double workDays) => PlanWeights.Walk * workDays / CellWalkDays;
}

/// <summary>What the conflicts are priced with. Expected is the colonists' mean mood level, 0 to 1; unknown leaves the lost role effects unpriced.</summary>
public sealed record RegroupPrice
{
    public WorkPrice Work { get; init; }
    public MoodModel Mood { get; init; } = new();
    public Fact<double> Expected { get; init; }

    /// <summary>A lost role effect in score points: its mood points taken from each pawn at the planned room's stage, as break risk. 0 while the mood is not priced.</summary>
    internal double EffectPoints(LostRoleEffect l)
    {
        var known = Expected.TryGetValue(out var level);
        var (lost, ok) = Mood.LostPoints(l, MoodJudgment.PlannedStage);
        if (!known || !ok)
        {
            return 0;
        }
        var (price, priced) = PolicyFunctions.PriceMood(new MoodPriceInput { Model = Mood.Break, WorkDayW = Work.WorkDayW, Points = lost, Pawns = l.Pawns, ExpectedMood = level });
        return priced ? Work.Points(price.WorkDays) : 0;
    }

    /// <summary>The conflicts of one room in points, and the lost effects it counts (the guard's included).</summary>
    internal (double Points, int Lost) Group(RoomGroup g, IReadOnlyList<LostRoleEffect> guard)
    {
        var ticks = g.Costs.Sum(c => c.TicksPerDay);
        var points = Work.Points(ticks / GameTime.TicksPerDay);
        var lost = 0;
        foreach (var l in g.LostEffects.Concat(guard))
        {
            points += EffectPoints(l);
            lost++;
        }
        return (points, lost);
    }

    /// <summary>
    /// What a room's filth nodes cost it, in points, through the same work-day to points rate as every other conflict:
    /// the food poisoning (the hediff's work-days per expected poisoning of the meals made while the filth stands) and
    /// the cleaning labor, which every group holding a filth node pays whether it shares the room or not: it is the
    /// common baseline of the merged and the apart grouping, so a merge changes it by nothing.
    /// </summary>
    internal (double Poison, double Clean) Filth(RoomGroup g)
    {
        var poisonDays = g.Filth.Sum(f => f.PoisonWorkDays);
        return (Work.Points(poisonDays), Work.Points(g.Cleaning));
    }
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

/// <summary>
/// One regrouping decision for the caller to journal: a move applied, or why none was. A and B are the nodes of the
/// move's pair rule. Before and After are the total (score points, lower is better) of the grouping before and after
/// the move; Lost the lost role effects after it. Walk, Poison and Clean are the move's parts of Before to After, in
/// score points: the change of the embedding's score (the walk it saves is negative), the poisoning price it adds and
/// the cleaning labor it changes (zero for a merge: both groupings pay the same cleaning labor); the rest is the
/// conflicts' ticks and lost effects.
/// </summary>
public sealed record RegroupDecision
{
    public string Kind { get; init; } = "";
    public string Verdict { get; init; } = "";
    public string Reason { get; init; } = "";
    public string A { get; init; } = "";
    public string B { get; init; } = "";
    public double Before { get; init; }
    public double After { get; init; }
    public int Lost { get; init; }
    public double Walk { get; init; }
    public double Poison { get; init; }
    public double Clean { get; init; }
}

/// <summary>The groups the search ended on. Embed is the embedding's input over those groups, Embedding its result; Pairs are the rules that make the grouping (empty for the seed).</summary>
public sealed record Regrouping
{
    public Clustering Clustering { get; init; } = new();
    public EmbedInput Embed { get; init; } = new();
    public Embedding Embedding { get; init; } = new();
    public IReadOnlyList<NodePair> Pairs { get; init; } = [];
    public IReadOnlyList<RegroupDecision> Decisions { get; init; } = [];
}

/// <summary>One grouping, clustered, embedded and priced.</summary>
internal sealed class RegroupTrial
{
    public IReadOnlyList<NodePair> Pairs { get; init; } = [];
    public Clustering Clustering { get; init; } = new();
    public EmbedInput Embed { get; init; } = new();
    public Embedding Embedding { get; init; } = new();
    public int Failed { get; set; }
    public double Points { get; set; }

    /// <summary>The embedding's part of Points.</summary>
    public double EmbedPts { get; set; }

    /// <summary>The poisoning price's part of Points.</summary>
    public double PoisonPts { get; set; }

    /// <summary>The cleaning labor's part of Points.</summary>
    public double CleanPts { get; set; }

    public int Lost { get; set; }

    /// <summary>Groups that carry a role-guard cost.</summary>
    public Dictionary<int, bool> Guarded { get; } = [];

    /// <summary>Ranks trials: fewer undone, a lower total, fewer lost effects.</summary>
    public bool Better(RegroupTrial o)
    {
        if (Failed != o.Failed)
        {
            return Failed < o.Failed;
        }
        if (Math.Abs(Points - o.Points) > 1e-9)
        {
            return Points < o.Points;
        }
        return Lost < o.Lost;
    }

    /// <summary>Names the grouping: each room's nodes and counts.</summary>
    public string Signature()
    {
        var rooms = Clustering.Groups.Select(g => string.Join(",", g.Stations.Select(s => $"{s.Node}:{s.Count}"))).ToList();
        rooms.Sort(StringComparer.Ordinal);
        return string.Join("|", rooms);
    }

    public Regrouping Result(IReadOnlyList<RegroupDecision> decisions) => new() { Clustering = Clustering, Embed = Embed, Embedding = Embedding, Pairs = Pairs, Decisions = decisions };
}

internal sealed record RegroupProposal(string Kind, string Reason, string A, string B, IReadOnlyList<NodePair> Rules);

internal sealed class Regrouper(RegroupInput input)
{
    public RegroupInput In { get; } = input;

    public RegroupTrial Evaluate(IReadOnlyList<NodePair> pairs)
    {
        var ci = In.Cluster with { Pairs = pairs };
        var cl = PolicyFunctions.ClusterStations(ci);
        var embed = In.Embed with { Groups = cl.Groups, Fields = PolicyFunctions.FieldEmbedLinks(ci.Graph, cl.Groups) };
        var emb = PolicyFunctions.Embed(embed);
        var t = new RegroupTrial { Pairs = pairs, Clustering = cl, Embed = embed, Embedding = emb };
        t.Failed = emb.Unplaceable().Count + emb.Score.UnmetLinks;
        t.EmbedPts = -(double)emb.Score.Total();
        t.Points = t.EmbedPts;
        var guard = GuardEffects(cl.Groups);
        foreach (var g in cl.Groups)
        {
            var effects = guard.GetValueOrDefault(g.Id) ?? [];
            var (points, lost) = In.Price.Group(g, effects);
            var (poison, clean) = In.Price.Filth(g);
            t.Points += points + poison + clean;
            t.PoisonPts += poison;
            t.CleanPts += clean;
            t.Lost += lost;
            t.Guarded[g.Id] = effects.Count > 0;
        }
        return t;
    }

    /// <summary>A room whose stations are for more than one role (each station's role alone): the room PredictRole has to weigh, so the one a mismatch can be blamed on. A room of one role is the same prediction however it is cut.</summary>
    private bool Mixed(RoomGroup g)
    {
        var cat = In.Cluster.Catalog;
        var roles = new List<RoomRole>();
        foreach (var s in g.Stations)
        {
            if (s.Purpose == StationPurpose.Cool)
            {
                continue;
            }
            RoomRole role;
            try
            {
                role = PolicyFunctions.PredictRole(cat, [new RoomPiece { Def = s.Def, State = PolicyFunctions.PieceState(s.Purpose, cat.Stations[s.Def]) }]).Role;
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

    /// <summary>
    /// Per group, the effect of the role the group predicts when the game disproved it for the built room the group is
    /// matched to: the room stands, and its stations are read as the native role. Only a mixed room carries it, since
    /// cutting a room of one role cannot change what is predicted.
    /// </summary>
    private Dictionary<int, List<LostRoleEffect>> GuardEffects(IReadOnlyList<RoomGroup> groups)
    {
        var output = new Dictionary<int, List<LostRoleEffect>>();
        if (In.Mismatches.Count == 0)
        {
            return output;
        }
        foreach (var m in PolicyFunctions.MatchGroupRooms(groups, In.Embed.Built))
        {
            var g = groups[m.Group];
            foreach (var mm in In.Mismatches)
            {
                if (g.Role != mm.Predicted || !m.Rooms.Contains(mm.Room) || !Mixed(g))
                {
                    continue;
                }
                if (!output.TryGetValue(g.Id, out var list))
                {
                    output[g.Id] = list = [];
                }
                list.Add(new LostRoleEffect(PolicyFunctions.RoleEffects[mm.Predicted], mm.Predicted, mm.Native, Sleepers(g)));
            }
        }
        return output;
    }

    /// <summary>The pawns the group's beds sleep.</summary>
    private int Sleepers(RoomGroup g)
    {
        var n = 0;
        foreach (var s in g.Stations)
        {
            var c = In.Cluster.Catalog.Stations[s.Def].Capacity;
            if (c.Kind == CapacityKind.Sleepers)
            {
                n += c.Slots * s.Count;
            }
        }
        return n;
    }

    /// <summary>A split of a room that carries a role-guard cost.</summary>
    public static bool GuardedPair(RegroupTrial cur, RegroupProposal m) =>
        cur.Clustering.Groups.Any(g => cur.Guarded.GetValueOrDefault(g.Id) && PolicyFunctions.HoldsNode(g, m.A) && PolicyFunctions.HoldsNode(g, m.B));

    /// <summary>The moves open from a grouping, in a fixed order.</summary>
    public List<RegroupProposal> Candidates(RegroupTrial cur)
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
            if (d.Verdict != ClusterKeep || d.Reason != ReasonClusterLostEffect || d.LostEffects.Count > 0 || d.A == "" || d.B == "" || d.A == d.B || PolicyFunctions.RuleOf(cur.Pairs, d.A, d.B) != PairRule.None)
            {
                continue;
            }
            Add(new RegroupProposal(PolicyFunctions.RegroupMerge, PolicyFunctions.ReasonRegroupMerge, d.A, d.B, [new NodePair(d.A, d.B, PairRule.Together)]));
        }

        // merge: a filth node kept in its closet joins the room of the node it flows to, when the walk saved outweighs the poisoning it costs the room's cooks. A filth model that cannot price the poisoning proposes nothing: the merge would look free.
        var priced = In.Cluster.Catalog.Filth.Priced();
        foreach (var d in cur.Clustering.Decisions)
        {
            if (!priced || d.Verdict != ClusterKeep || d.Reason != ReasonClusterFilthPriced || d.A == "" || d.B == "" || d.A == d.B || PolicyFunctions.RuleOf(cur.Pairs, d.A, d.B) != PairRule.None)
            {
                continue;
            }
            Add(new RegroupProposal(PolicyFunctions.RegroupMerge, PolicyFunctions.ReasonRegroupFilthMerge, d.A, d.B, [new NodePair(d.A, d.B, PairRule.Together)]));
        }
        var groups = cur.Clustering.Groups;
        foreach (var g in groups)
        {
            var nodes = PolicyFunctions.RoomNodes(g);
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
                    if ((whole || suffers.Contains(a) || suffers.Contains(b)) && PolicyFunctions.RuleOf(cur.Pairs, a, b) != PairRule.Apart)
                    {
                        Add(new RegroupProposal(PolicyFunctions.RegroupSplit, PolicyFunctions.ReasonRegroupSplitConflict, a, b, [new NodePair(a, b, PairRule.Apart)]));
                    }
                }
            }

            // move-a-need: a node that pays goes to the room of the role it wants.
            foreach (var c in g.Costs)
            {
                foreach (var h in groups)
                {
                    if (h.Id == g.Id || PolicyFunctions.RoomNodes(h).Count == 0 || PolicyFunctions.HoldsNode(h, c.Node) || !Target(c, g, h))
                    {
                        continue;
                    }
                    var first = PolicyFunctions.RoomNodes(h)[0];
                    var rules = new List<NodePair> { new(c.Node, first, PairRule.Together) };
                    foreach (var o in nodes)
                    {
                        if (o != c.Node)
                        {
                            rules.Add(new NodePair(c.Node, o, PairRule.Apart));
                        }
                    }
                    Add(new RegroupProposal(PolicyFunctions.RegroupMove, PolicyFunctions.ReasonRegroupMove, c.Node, first, rules));
                }
            }
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

/// <summary>
/// Ports <c>layout_regroup.go</c>: regrouping. ClusterStations is the conservative seed: it merges the stations that
/// flow together unless the merged room loses an effect, and splits nothing it can keep. The embedding moves blocks of
/// a fixed group set and cannot change the groups, so the search over the groups is an outer loop around it: a move
/// is proposed, the stations are clustered again with a per-pair rule, the groups are embedded, and the move is kept
/// when the priced total falls. The tiling runs once, on the winner.
/// </summary>
/// <remarks>
/// Moves: merge (two nodes the seed kept apart for a lost bench speed share a room), split (two nodes of a room that
/// pays a conflict stand in rooms of their own) and move-a-need (a station class moves from the room that slows it to
/// a room of the role it wants, or out of a cold room). The price is one total in the embedding's score points: the
/// embedding cost, the throughput lost, the lost role effects (mood points to break risk to work-days to points) and
/// the role guard. A trial ranks by what it leaves undone, then by the total, then by the lost effects; ties keep the
/// current grouping. The moves are listed in a fixed order and sampled with the site's seeded stream when more than
/// RegroupProposals are open, so the result is the same at any thread count.
/// </remarks>
public static partial class PolicyFunctions
{
    public const string RegroupMerge = "merge";
    public const string RegroupSplit = "split";
    public const string RegroupMove = "move";

    public const string RegroupApplied = "applied";
    public const string RegroupKept = "kept";
    public const string RegroupSkipped = "skipped";

    public const string ReasonRegroupMerge = "regroup_merge_total_falls";
    public const string ReasonRegroupSplitConflict = "regroup_split_conflict_priced";
    public const string ReasonRegroupSplitRoleGuard = "regroup_split_role_guard";
    public const string ReasonRegroupMove = "regroup_move_need_total_falls";
    public const string ReasonRegroupNoGain = "regroup_no_move_improves";
    public const string ReasonRegroupUnpriced = "regroup_work_price_unknown";

    /// <summary>A butcher joined a room of cooks because the walk it saves outweighs the poisoning it costs.</summary>
    public const string ReasonRegroupFilthMerge = "regroup_filth_merge_walk_beats_poison";

    /// <summary>The butcher stayed a closet because the poisoning costs more than the walk saved.</summary>
    public const string ReasonRegroupFilthKept = "regroup_filth_merge_poison_beats_walk";

    // The search budget, as SiteSearchTop / SiteSearchIters bound the embedding's: moves tried a round and rounds, each trial one clustering and one embedding.
    internal const int RegroupProposals = SiteSearchTop;
    internal const int RegroupRounds = SiteSearchTop / 2;

    /// <summary>Prices a colonist's time from the station facts: the lamp price's own work-day and the walk of a cell at the game's ticks a second and a colonist's move speed. Invalid while the food census, the colonists, a crop or the game's tick rate is unknown.</summary>
    public static WorkPrice StationWorkPrice(StationFacts f)
    {
        var ticks = f.Catalog.Climate.TicksPerSecond;
        if (!FinitePositive(ticks))
        {
            return default;
        }
        var p = new WorkPrice(StationWorkDayW(f), ticks / ColonistMoveSpeed / GameTime.TicksPerDay);
        return p.Valid() ? p : default;
    }

    /// <summary>
    /// Searches the room groups: the seed clustering, then the best move that lowers the priced total, until none does
    /// or the budget is spent. The price must be known (Price.Work.Valid); without it the seed stands, with a skipped
    /// decision.
    /// </summary>
    public static Regrouping Regroup(RegroupInput input)
    {
        var r = new Regrouper(input);
        var cur = r.Evaluate(WithRules([], input.Cluster.Pairs));
        var decisions = new List<RegroupDecision>();
        if (!input.Price.Work.Valid())
        {
            decisions.Add(new RegroupDecision { Verdict = RegroupSkipped, Reason = ReasonRegroupUnpriced });
            return cur.Result(decisions);
        }
        var rng = new SearchRand(cur.Embedding.Seed);
        var refused = new Dictionary<(string, string), RegroupDecision>(); // filth merges priced higher than the closet, by pair
        for (var round = 0; round < RegroupRounds; round++)
        {
            var cands = r.Candidates(cur);
            RegroupTrial? best = null;
            RegroupProposal? bestMove = null;
            foreach (var m in Sample(cands, RegroupProposals, rng))
            {
                var t = r.Evaluate(WithRules(cur.Pairs, m.Rules));
                if (m.Reason == ReasonRegroupFilthMerge && t.Signature() != cur.Signature() && !t.Better(cur))
                {
                    refused[PairKey(m.A, m.B)] = MoveDecision(m, cur, t, RegroupKept, ReasonRegroupFilthKept);
                }
                if (Unplaceable(t.Clustering.Groups) > Unplaceable(cur.Clustering.Groups) || t.Signature() == cur.Signature() || !t.Better(cur) || (best is not null && !t.Better(best)))
                {
                    continue;
                }
                (best, bestMove) = (t, m);
            }
            if (best is null || bestMove is null)
            {
                break;
            }
            var reason = bestMove.Reason;
            if (bestMove.Kind == RegroupSplit && Regrouper.GuardedPair(cur, bestMove))
            {
                reason = ReasonRegroupSplitRoleGuard;
            }
            decisions.Add(MoveDecision(bestMove, cur, best, RegroupApplied, reason));
            refused.Remove(PairKey(bestMove.A, bestMove.B));
            cur = best;
        }
        var keys = refused.Keys.ToList();
        keys.Sort((a, b) => string.CompareOrdinal(a.Item1 + "|" + a.Item2, b.Item1 + "|" + b.Item2));
        foreach (var k in keys)
        {
            if (RuleOf(cur.Pairs, k.Item1, k.Item2) == PairRule.None)
            {
                decisions.Add(refused[k]);
            }
        }
        if (decisions.Count == 0)
        {
            decisions.Add(new RegroupDecision { Verdict = RegroupKept, Reason = ReasonRegroupNoGain, Before = cur.Points, After = cur.Points, Lost = cur.Lost });
        }
        return cur.Result(decisions);
    }

    /// <summary>Counts the groups no planned room carries (a barracks): the tiler cannot place them, so no move may add one.</summary>
    private static int Unplaceable(IReadOnlyList<RoomGroup> groups) => groups.Count(g => !PlannedRoleOfGroup(g).Ok);

    /// <summary>The move m taking the grouping from to to.</summary>
    private static RegroupDecision MoveDecision(RegroupProposal m, RegroupTrial from, RegroupTrial to, string verdict, string reason) =>
        new()
        {
            Kind = m.Kind,
            Verdict = verdict,
            Reason = reason,
            A = m.A,
            B = m.B,
            Before = from.Points,
            After = to.Points,
            Lost = to.Lost,
            Walk = to.EmbedPts - from.EmbedPts,
            Poison = to.PoisonPts - from.PoisonPts,
            Clean = to.CleanPts - from.CleanPts,
        };

    /// <summary>At most n of the moves, in their listed order: all of them, or n drawn from the seeded stream.</summary>
    private static List<RegroupProposal> Sample(List<RegroupProposal> moves, int n, SearchRand rng)
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
    private static List<NodePair> WithRules(IReadOnlyList<NodePair> pairs, IReadOnlyList<NodePair> rules)
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

    /// <summary>The graph nodes a room holds, sorted; the cooling station a cold room carries is no node of the graph.</summary>
    internal static List<string> RoomNodes(RoomGroup g)
    {
        var output = g.Stations.Where(s => s.Purpose != StationPurpose.Cool).Select(s => s.Node).ToList();
        output.Sort(StringComparer.Ordinal);
        return output;
    }

    internal static bool HoldsNode(RoomGroup g, string node) => RoomNodes(g).Contains(node);
}
