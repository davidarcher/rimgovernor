namespace RimGovernor.Brain.Policy;

// The interior objective scores a furniture layout in one region. It is the measure a seeded local search
// improves (#2869): hard constraints a layout must meet, then soft terms reported one by one. There is
// deliberately no scalar total here: no term has a game-derived weight against another, so layouts compare per
// term (NoWorseThan) until a search needs one and a DefTables entry states the judgment.
//
// Hard: pieces on region cells and overlapping nothing, doors only on the layout's candidate cells and each
// opening into the region, door thresholds clear, interaction cells open, all free floor reachable from the
// doors, required counts met.
//
// Soft: free floor (the game's Space stat counts open cells), facility links (the FacilityLink rules of the
// catalog: bed with end table and dresser, hospital bed with vitals monitor, research bench with analyzer, work
// bench with tool cabinet, each within its MaxDistance), flow-weighted walking distance (InteriorFlows), and
// beauty when the caller knows it.

/// <summary>The input to ScoreInterior.</summary>
public sealed record InteriorObjective
{
    public required Region Region { get; init; }

    /// <summary>The wall cells the layout lets a door stand on; null accepts any cell outside the region.</summary>
    public IReadOnlyList<Cell>? Candidates { get; init; }

    /// <summary>The chosen doors, each outside the region beside a region cell.</summary>
    public IReadOnlyList<Cell> Doors { get; init; } = [];

    public IReadOnlyList<InteriorPiece> Pieces { get; init; } = [];

    /// <summary>The least count of each definition the layout must hold.</summary>
    public IReadOnlyDictionary<string, int> Required { get; init; } = new Dictionary<string, int>();

    public PieceShapes Shapes { get; init; } = new();

    /// <summary>Weights the walks between stations; null has no walk term.</summary>
    public IInteriorFlows? Flows { get; init; }

    /// <summary>
    /// The Beauty stat of each definition, from the game's defs (the station catalog does not carry it yet,
    /// so it is unknown when empty or when a definition is missing; unknowns are listed, not guessed).
    /// </summary>
    public IReadOnlyDictionary<string, double> Beauty { get; init; } = new Dictionary<string, double>();

    /// <summary>Names the chair, which colonists walk over.</summary>
    public DiningFurniture Dining { get; init; } = new();
}

/// <summary>One end of a flow edge: a door (any chosen door) or a piece.</summary>
public readonly record struct FlowEnd(bool Door, string Slot);

/// <summary>
/// A pull between two stations: Weight is how much traffic (trips, recipe or item flow) goes between them.
/// Informational marks traffic of people (meals, sleep, rest) that is reported but not gated; recipe and item
/// flows are gated.
/// </summary>
public readonly record struct FlowEdge(FlowEnd From, FlowEnd To, double Weight, bool Informational);

/// <summary>Says which stations colonists walk between.</summary>
public interface IInteriorFlows
{
    IReadOnlyList<FlowEdge> Edges(IReadOnlyList<InteriorPiece> pieces);
}

/// <summary>
/// Reads the flows from the station graph (#2870): each graph edge is the daily movements between two station
/// nodes (ingredients store to stove, meals stove to table and bed to table, products to the store). A node's
/// pieces in the room are those of its definition. An edge between two nodes both in the room is a walk between
/// their pieces, the weight shared over the piece pairs. An edge whose far node has no piece in the room is
/// traffic that leaves by a door, shared over the near node's pieces: a bed walks to a door by its meal flow, a
/// stove to the door by the ingredients it fetches and the meals it carries out. No weight here is a constant:
/// all of it is the graph's. Required (zero weight) links carry no traffic and add nothing to the walk.
/// A bed is not a definition: one Bed def serves the colonist, the patient and the prisoner, and the hospital
/// places a plain bed set medical. So a sleep node (any StationPurpose of sleeping) takes the room's beds by the
/// room's purpose, Sleep, whatever their def (Furniture says which are beds); a prison cell's beds take no
/// colonist meal flow and a bedroom's none of the patients'. A room with no Sleep purpose has no bed flows.
/// An edge with a sleep end is the meal, sleep and rest traffic of people and is Informational: reported, not
/// gated. The rest are recipe and item flows (ingredients in, products out, meals carried from the stove).
/// </summary>
/// <param name="Graph">The station graph.</param>
/// <param name="Sleep">The purpose of this room's beds; None when it holds none.</param>
/// <param name="Furniture">Says which definitions are beds.</param>
public sealed record GraphFlows(StationGraph Graph, StationPurpose Sleep, RoomFurniture Furniture) : IInteriorFlows
{
    public IReadOnlyList<FlowEdge> Edges(IReadOnlyList<InteriorPiece> pieces)
    {
        List<string> Slots(StationNode n)
        {
            var output = new List<string>();
            foreach (var p in pieces)
            {
                if (n.Purpose.Sleeps())
                {
                    if (n.Purpose == Sleep && Furniture.IsBed(p.Def))
                    {
                        output.Add(p.Slot);
                    }
                }
                else if (p.Def == n.Def)
                {
                    output.Add(p.Slot);
                }
            }
            return output;
        }
        var edges = new List<FlowEdge>();
        foreach (var e in Graph.Edges)
        {
            var (na, okA) = Graph.Node(e.A);
            var (nb, okB) = Graph.Node(e.B);
            if (e.Weight <= 0 || !okA || !okB)
            {
                continue;
            }
            var a = Slots(na);
            var b = Slots(nb);
            var info = na.Purpose.Sleeps() || nb.Purpose.Sleeps();
            if (a.Count > 0 && b.Count > 0)
            {
                var w = e.Weight / (a.Count * b.Count);
                foreach (var from in a)
                {
                    foreach (var to in b)
                    {
                        if (from != to)
                        {
                            edges.Add(new FlowEdge(new FlowEnd(false, from), new FlowEnd(false, to), w, info));
                        }
                    }
                }
            }
            else if (a.Count > 0 || b.Count > 0)
            {
                var near = a.Concat(b).ToList();
                foreach (var s in near)
                {
                    edges.Add(new FlowEdge(new FlowEnd(true, string.Empty), new FlowEnd(false, s), e.Weight / near.Count, info));
                }
            }
        }
        return edges;
    }
}

/// <summary>A layout's measure: Violations are the hard constraints it breaks, the rest the soft terms.</summary>
public sealed record InteriorScore
{
    public IReadOnlyList<string> Violations { get; init; } = [];

    /// <summary>The region's area.</summary>
    public int Cells { get; init; }

    /// <summary>The cells no piece covers.</summary>
    public int FreeCells { get; init; }

    /// <summary>Counts facility-serviceable targets.</summary>
    public int LinksWanted { get; init; }

    /// <summary>Counts the targets with a facility in range.</summary>
    public int LinksServed { get; init; }

    /// <summary>The weight-averaged walking distance in steps over the gated (recipe and item) flow edges; 0 with no edges.</summary>
    public double Walk { get; init; }

    /// <summary>The number of gated flow edges Walk averages.</summary>
    public int WalkEdges { get; init; }

    /// <summary>The average over the gated edges that end at a door (a part of Walk).</summary>
    public double DoorWalk { get; init; }

    /// <summary>The average over the gated edges between two pieces (a part of Walk).</summary>
    public double PairWalk { get; init; }

    /// <summary>The same average over the informational edges (meals, sleep, rest): reported, never gated.</summary>
    public double InfoWalk { get; init; }

    public int InfoEdges { get; init; }

    /// <summary>Sums the known Beauty of the pieces.</summary>
    public double Beauty { get; init; }

    /// <summary>Lists the definitions whose Beauty was not known.</summary>
    public IReadOnlyList<string> Unknown { get; init; } = [];

    /// <summary>The count of pieces placed.</summary>
    public int Pieces { get; init; }

    /// <summary>Reports whether every hard constraint holds.</summary>
    public bool Feasible() => Violations.Count == 0;

    /// <summary>The free floor fraction.</summary>
    public double Free() => Cells == 0 ? 0 : (double)FreeCells / Cells;

    /// <summary>The fraction of link targets served; 1 when nothing is wanted.</summary>
    public double Links() => LinksWanted == 0 ? 1 : (double)LinksServed / LinksWanted;

    /// <summary>Reports whether this is at least as good as o on every soft term. Beauty compares only when neither side has unknowns.</summary>
    public bool NoWorseThan(InteriorScore o) => WorseTerms(o).Count == 0;

    /// <summary>
    /// Names the gated soft terms on which this is worse than o: free floor, facility links, pair walk and door
    /// walk (of recipe and item flows), and beauty when known. Informational walking is not compared.
    /// </summary>
    public IReadOnlyList<string> WorseTerms(InteriorScore o)
    {
        const double Eps = 1e-9;
        var output = new List<string>();
        if (Free() < o.Free() - Eps)
        {
            output.Add("free");
        }
        if (Links() < o.Links() - Eps)
        {
            output.Add("links");
        }
        if (PairWalk > o.PairWalk + Eps)
        {
            output.Add("pair_walk");
        }
        if (DoorWalk > o.DoorWalk + Eps)
        {
            output.Add("door_walk");
        }
        if (Unknown.Count == 0 && o.Unknown.Count == 0 && Beauty < o.Beauty - Eps)
        {
            output.Add("beauty");
        }
        return output;
    }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// The sleep purpose of the beds a room role holds: the bedroom's are the colonists', the hospital's
    /// medical, the prison cell's the prisoners'. Other roles hold no bed worth a flow.
    /// </summary>
    public static StationPurpose SleepPurposeOf(RoomRole role) => role switch
    {
        RoomRole.Bedroom => StationPurpose.Sleep,
        RoomRole.Hospital => StationPurpose.SleepMed,
        RoomRole.PrisonCell => StationPurpose.SleepPrison,
        _ => StationPurpose.None,
    };

    internal static bool Sleeps(this StationPurpose p) =>
        p is StationPurpose.Sleep or StationPurpose.SleepMed or StationPurpose.SleepPrison or StationPurpose.SleepBaby or StationPurpose.SleepAnimal;

    private static string Show(Cell c) => $"{{{c.X} {c.Z}}}";

    /// <summary>Measures a layout against the objective.</summary>
    public static InteriorScore ScoreInterior(InteriorObjective input)
    {
        var g = input.Region;
        var violations = new List<string>();
        void Bad(string message) => violations.Add(message);

        var blocked = new Dictionary<Cell, string>();
        var counts = new Dictionary<string, int>();
        foreach (var p in input.Pieces)
        {
            counts[p.Def] = counts.GetValueOrDefault(p.Def) + 1;
            foreach (var c in RectCells(p.Rect))
            {
                if (!g.Contains(c))
                {
                    Bad($"{p.Slot}: cell {Show(c)} outside the region");
                    continue;
                }
                if (blocked.TryGetValue(c, out var other))
                {
                    Bad($"{other} and {p.Slot} overlap at {Show(c)}");
                }
                blocked[c] = p.Slot;
            }
        }
        var cells = g.Area();
        var freeCells = cells - blocked.Count;

        var thresholds = new List<Cell>();
        var candidate = new HashSet<Cell>(input.Candidates ?? []);
        var isDoor = new HashSet<Cell>();
        foreach (var d in input.Doors)
        {
            isDoor.Add(d);
            if (input.Candidates is not null && !candidate.Contains(d))
            {
                Bad($"door {Show(d)} is not a candidate door cell");
            }
            var opens = false;
            foreach (var n in Neighbours(d))
            {
                if (g.Contains(n))
                {
                    opens = true;
                    thresholds.Add(n);
                    if (blocked.TryGetValue(n, out var p))
                    {
                        Bad($"{p} stands inside the door at {Show(d)}");
                    }
                }
            }
            if (g.Contains(d) || !opens)
            {
                Bad($"door {Show(d)} does not open into the region");
            }
        }
        if (input.Doors.Count == 0)
        {
            Bad("no door");
        }

        bool Free(Cell c) => g.Contains(c) && !blocked.ContainsKey(c);
        if (thresholds.Count > 0)
        {
            var reach = Flood(Free, thresholds);
            foreach (var c in g.Cells())
            {
                if (Free(c) && !reach.ContainsKey(c))
                {
                    Bad($"floor at {Show(c)} is cut off from the doors");
                }
            }
        }
        // Chairs are walked over, so a table ringed by its chairs is reached.
        var chairs = new Dictionary<string, bool>();
        foreach (var p in input.Pieces)
        {
            chairs[p.Slot] = input.Dining.IsChair(p.Def);
        }
        bool Walkable(Cell c)
        {
            if (Free(c))
            {
                return true;
            }
            return blocked.TryGetValue(c, out var slot) && chairs.GetValueOrDefault(slot);
        }
        var stand = new Dictionary<string, List<Cell>>();
        foreach (var p in input.Pieces)
        {
            if (p.Clear > 0)
            {
                foreach (var c in p.Reserves())
                {
                    if (blocked.ContainsKey(c) || !g.Contains(c))
                    {
                        Bad($"{p.Slot}: lane cell {Show(c)} is not open floor");
                    }
                }
            }
            var (ic, ok) = p.Interaction();
            if (ok)
            {
                if (blocked.ContainsKey(ic) || (!g.Contains(ic) && !isDoor.Contains(ic)))
                {
                    Bad($"{p.Slot}: interaction cell {Show(ic)} is not open floor");
                }
                stand[p.Slot] = [ic];
                continue;
            }
            foreach (var c in RectCells(p.Rect))
            {
                foreach (var n in Neighbours(c))
                {
                    if (Walkable(n))
                    {
                        if (!stand.TryGetValue(p.Slot, out var list))
                        {
                            list = [];
                            stand[p.Slot] = list;
                        }
                        list.Add(n);
                    }
                }
            }
        }
        foreach (var def in input.Required.Keys.Order(StringComparer.Ordinal))
        {
            var have = counts.GetValueOrDefault(def);
            if (have < input.Required[def])
            {
                Bad($"{def}: {have} of the {input.Required[def]} required");
            }
        }

        var (wanted, served) = FacilityLinks(input);
        var score = new InteriorScore
        {
            Violations = violations,
            Cells = cells,
            FreeCells = freeCells,
            LinksWanted = wanted,
            LinksServed = served,
            Pieces = input.Pieces.Count,
        };
        score = WalkDistance(score, input, Walkable, thresholds, stand);
        var (beauty, unknown) = PieceBeauty(input);
        return score with { Beauty = beauty, Unknown = unknown };
    }

    internal static Cell[] Neighbours(Cell c) => [new(c.X + 1, c.Z), new(c.X - 1, c.Z), new(c.X, c.Z + 1), new(c.X, c.Z - 1)];

    /// <summary>The orthogonal step distance from the nearest source over free cells; sources that are not free are skipped.</summary>
    internal static Dictionary<Cell, int> Flood(Func<Cell, bool> free, IEnumerable<Cell> sources)
    {
        var dist = new Dictionary<Cell, int>();
        var queue = new Queue<Cell>();
        foreach (var c in sources)
        {
            if (!dist.ContainsKey(c) && free(c))
            {
                dist[c] = 0;
                queue.Enqueue(c);
            }
        }
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            foreach (var n in Neighbours(c))
            {
                if (!dist.ContainsKey(n) && free(n))
                {
                    dist[n] = dist[c] + 1;
                    queue.Enqueue(n);
                }
            }
        }
        return dist;
    }

    /// <summary>Fills the walking terms: the gated average over recipe and item edges (with its door and pair parts) and the informational average.</summary>
    private static InteriorScore WalkDistance(InteriorScore s, InteriorObjective input, Func<Cell, bool> free, IReadOnlyList<Cell> thresholds, Dictionary<string, List<Cell>> stand)
    {
        var flows = input.Flows;
        if (flows is null)
        {
            return s;
        }
        IReadOnlyList<Cell> Ends(FlowEnd e) => e.Door ? thresholds : stand.GetValueOrDefault(e.Slot ?? string.Empty) ?? [];
        double sum = 0, weight = 0, doorSum = 0, doorWeight = 0, infoSum = 0, infoWeight = 0;
        int walkEdges = 0, infoEdges = 0;
        foreach (var e in flows.Edges(input.Pieces))
        {
            var from = Ends(e.From);
            var to = Ends(e.To);
            if (e.Weight <= 0 || from.Count == 0 || to.Count == 0)
            {
                continue;
            }
            var dist = Flood(free, from);
            var best = int.MaxValue;
            var found = false;
            foreach (var c in to)
            {
                if (dist.TryGetValue(c, out var d) && d < best)
                {
                    best = d;
                    found = true;
                }
            }
            if (!found)
            {
                continue;
            }
            var cost = e.Weight * best;
            if (e.Informational)
            {
                infoSum += cost;
                infoWeight += e.Weight;
                infoEdges++;
                continue;
            }
            sum += cost;
            weight += e.Weight;
            if (e.From.Door || e.To.Door)
            {
                doorSum += cost;
                doorWeight += e.Weight;
            }
            walkEdges++;
        }
        static double Avg(double total, double w) => w == 0 ? 0 : total / w;
        return s with
        {
            WalkEdges = walkEdges,
            InfoEdges = infoEdges,
            Walk = Avg(sum, weight),
            DoorWalk = Avg(doorSum, doorWeight),
            PairWalk = Avg(sum - doorSum, weight - doorWeight),
            InfoWalk = Avg(infoSum, infoWeight),
        };
    }

    private static (double Total, IReadOnlyList<string> Unknown) PieceBeauty(InteriorObjective input)
    {
        double total = 0;
        var unknown = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in input.Pieces)
        {
            if (!input.Beauty.TryGetValue(p.Def, out var b))
            {
                unknown.Add(p.Def);
                continue;
            }
            total += b;
        }
        return (total, [.. unknown.Order(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Reads which pieces each of the catalog's facility links serves: beds take the end table and dresser,
    /// hospital beds the monitor, research benches the analyzer and workshop benches the cabinet. The links
    /// themselves (distance, simultaneous, adjacency) are catalog rows.
    /// </summary>
    private static IReadOnlyList<(FacilityLink Link, Func<InteriorPiece, bool> Target)> FacilityRules(PieceShapes shapes)
    {
        var f = shapes.Furniture;
        Func<InteriorPiece, bool> Family(RoomRole role) => p =>
        {
            var (d, ok) = shapes.Get(p.Def);
            return ok && d.Family == role;
        };
        bool Bed(InteriorPiece p) => f.IsBed(p.Def);
        return
        [
            (f.EndTable, Bed),
            (f.Dresser, Bed),
            (f.Monitor, Family(RoomRole.Hospital)),
            (f.Analyzer, Family(RoomRole.Laboratory)),
            (f.Cabinet, Family(RoomRole.Workshop)),
        ];
    }

    /// <summary>
    /// Counts the targets of every active rule and those a facility serves. A rule is active when the layout
    /// or its requirements hold the facility; a facility serves at most MaxSimultaneous targets, each target
    /// taking the nearest facility with capacity.
    /// </summary>
    private static (int Wanted, int Served) FacilityLinks(InteriorObjective input)
    {
        int wanted = 0, served = 0;
        // A piece placed beside another (a chair at its table) is one link, served when it faces and touches one.
        foreach (var p in input.Pieces)
        {
            if (p.Beside != string.Empty)
            {
                wanted++;
                if (p.Faces(input.Pieces))
                {
                    served++;
                }
            }
        }
        foreach (var (l, target) in FacilityRules(input.Shapes))
        {
            if (l.Def == string.Empty)
            {
                continue;
            }
            var facilities = new List<InteriorPiece>();
            var targets = new List<InteriorPiece>();
            foreach (var p in input.Pieces)
            {
                if (p.Def == l.Def)
                {
                    facilities.Add(p);
                }
                else if (target(p))
                {
                    targets.Add(p);
                }
            }
            if (facilities.Count == 0 && input.Required.GetValueOrDefault(l.Def) == 0)
            {
                continue;
            }
            var load = new int[facilities.Count];
            foreach (var t in targets)
            {
                wanted++;
                var best = -1;
                var bestD = 0.0;
                for (var i = 0; i < facilities.Count; i++)
                {
                    if (load[i] >= Math.Max(l.MaxSimultaneous, 1) || !LinkReaches(l, facilities[i], t))
                    {
                        continue;
                    }
                    var d = CentreDistance(facilities[i].Rect, t.Rect);
                    if (best < 0 || d < bestD)
                    {
                        best = i;
                        bestD = d;
                    }
                }
                if (best >= 0)
                {
                    load[best]++;
                    served++;
                }
            }
        }
        return (wanted, served);
    }

    internal static (double X, double Z) RectCentre(Rectangle r) => (r.X + (r.Width / 2.0), r.Z + (r.Height / 2.0));

    internal static double CentreDistance(Rectangle a, Rectangle b)
    {
        var (ax, az) = RectCentre(a);
        var (bx, bz) = RectCentre(b);
        var dx = ax - bx;
        var dz = az - bz;
        return Math.Sqrt((dx * dx) + (dz * dz));
    }

    /// <summary>
    /// Reports whether facility f links target t under link l: in range, and when required touching the
    /// target (beside its head for a bed, whose anchor is the head cell).
    /// </summary>
    internal static bool LinkReaches(FacilityLink l, InteriorPiece f, InteriorPiece t)
    {
        if (l.MaxDistance > 0 && CentreDistance(f.Rect, t.Rect) > l.MaxDistance)
        {
            return false;
        }
        if (l.CardinalToHead)
        {
            var head = t.Anchor();
            return Neighbours(head).Any(n => RectContains(f.Rect, n));
        }
        if (l.Adjacent)
        {
            foreach (var c in RectCells(f.Rect))
            {
                if (Neighbours(c).Any(n => RectContains(t.Rect, n)))
                {
                    return true;
                }
            }
            return false;
        }
        return true;
    }
}
