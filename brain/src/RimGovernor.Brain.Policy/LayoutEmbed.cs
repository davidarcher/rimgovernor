namespace RimGovernor.Brain.Policy;

// Topology embedding places the room groups (layout_cluster.go) on the site as area-sized rectangular blocks,
// coarsely: no regions, doors or walls yet (tiling is the next stage). It reuses the siting machinery rather
// than a parallel one: the obstacle levels (layout_build_ground.go) and candidate seeds (layout_site_ground.go),
// the PlanScore terms per cell (soil and rock, edge, centre), planWeights for every weight and the seeded
// searchRand. No constant is new.
//
// Objective, lower is better, over blocks b and group links (i, j):
//
//   sum_b  cells(b) x [Soil x soilCost + Rock x rock + Centre] + padded(b) x Edge
//   sum_l  Walk x weight(l) x (gap(i, j) - 1)       gap 1 = sharing a wall
//   sum_l  BadRoutes                                 per required link not adjacent
//   sum_u  Wall x wall cells + Footprint x cells     the enclosure a room no flow links adds (enclosureCost, #2987)
//
// The link weight is the clustering's summed flow weight (movements a day), so the walking term is the same
// trip-weighted distance PlanScore.Walk prices, with the station graph's flows instead of the role trips. Every
// cell term is a prefix sum over the map, so pricing a block is O(1) however big it is.
//
// Heuristic. The graph is built like Prim's tree: the heaviest group first at the nearest fit to a siting seed,
// then always the group with the most weight to what is placed (required links first), put at the best fitting
// position on the smallest ring around its placed neighbours that has one. That gives a compact, flow-ordered
// start. A seeded local search then lifts one block at a time and puts it back beside a linked block, beside
// any block, or at a random core cell, or swaps two blocks, keeping only strict gains. The best start wins, so
// the result never scores below the greedy start of its best seed.
//
// Environment requirements drive placement only where the survey says something: open sky is a constraint (no
// rock or mountain roof under the block), and every block gets walls (enclosed). Cold is not a site
// requirement: a cold group carries its cooling station (ClusterStations), so mountain is only the rock dig
// cost every group pays. A clean room has no site signal and is reported unsatisfied with a reason, not
// guessed at.
//
// Obstacle levels: every level runs and the one with the fewest failures wins (a room with no block, a
// required link not adjacent). On equal failures the strictest keeps the plan unless a looser one beats its
// objective by FieldGain (#2979). The first block avoids rich soil while any fit does.
public static partial class PolicyConstants
{
    // Verdicts and reasons of embedding decisions.
    public const string EmbedPlace = "place";
    public const string EmbedUnplaceable = "unplaceable";
    public const string EmbedUnsatisfied = "unsatisfied";

    public const string ReasonEmbedPlaced = "embed_placed";
    public const string ReasonEmbedNoGround = "embed_no_ground";
    public const string ReasonEmbedOpenSkyGround = "embed_open_sky_ground";
    public const string ReasonEmbedNoSiteSignal = "embed_no_site_signal";
    public const string ReasonEmbedLinkNotAdjacent = "embed_link_not_adjacent";
}

/// <summary>Everything the embedding reads.</summary>
public sealed record EmbedInput
{
    public IReadOnlyList<RoomGroup> Groups { get; init; } = [];

    public IReadOnlyList<LayoutZone> Zones { get; init; } = [];

    public IReadOnlyList<LayoutReservation> Reservations { get; init; } = [];

    public MapSurvey Survey { get; init; } = new();

    /// <summary>The game's roof support distance (as for ClusterInput): it bounds a block's depth.</summary>
    public double RoofDistance { get; init; }

    /// <summary>The field nodes placed with the groups (layout_field_place.go). With any, rooms may stand on field soil: the zone obstacle is dropped.</summary>
    public IReadOnlyList<EmbedField> Fields { get; init; } = [];

    /// <summary>
    /// The standing rooms (BuiltRegions): each is the fixed block of the group MatchGroupRooms gives it, and
    /// moves only by the relocation pass (layout_built.go). Unbuilt rooms are free.
    /// </summary>
    public IReadOnlyList<BuiltRegion> Built { get; init; } = [];

    /// <summary>
    /// The pinned standing rooms that must stay whether or not a group carries them (#3019): each one that is
    /// not a group's home block is an obstacle no block overlaps and comes back as Embedding.Held, until the
    /// caller stops pinning it. Role is unused; Planned is the room's planned role.
    /// </summary>
    public IReadOnlyList<BuiltRegion> Standing { get; init; } = [];

    /// <summary>A rebuild is under way (a successor pending or a room being built), so no built room moves.</summary>
    public bool InFlight { get; init; }

    /// <summary>The plan's retired ground not yet cleared; no new block stands on it.</summary>
    public IReadOnlyList<Region> RetiredGround { get; init; } = [];
}

/// <summary>Where a group's interior stands. Built is the standing room the block keeps, Replaces the standing room a moved block supersedes; 0 for neither.</summary>
public sealed record EmbeddedBlock
{
    public int Group { get; init; }

    public Rectangle Interior { get; init; }

    public int Built { get; init; }

    public int Replaces { get; init; }
}

/// <summary>A pinned standing room no group's block keeps (EmbedInput.Standing): it stays where it stands as an obstacle to every block.</summary>
public sealed record HeldRoom(int Room, Rectangle Interior, PlannedRole Role);

/// <summary>One embedding decision for the caller to journal. Other is the second group of a link decision, -1 otherwise.</summary>
public sealed record EmbedDecision
{
    public int Group { get; init; }

    public int Other { get; init; } = -1;

    public string Verdict { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    /// <summary>Names the environment of an unsatisfied requirement.</summary>
    public EnvRequirement? Requirement { get; init; }

    /// <summary>Names the field of a field decision (Group is -1 then).</summary>
    public string Field { get; init; } = string.Empty;

    /// <summary>The standing room of a built-room decision (layout_built.go).</summary>
    public int Room { get; init; }
}

/// <summary>
/// The embedding's per-term values, signed like PlanScore (costs negative). Soil includes the rock dig cost;
/// Walk is the weighted walking distance between linked blocks; UnmetLinks counts required links not adjacent,
/// each charged planWeights.BadRoutes in Total.
/// </summary>
public sealed record EmbedScore
{
    public int Soil { get; set; }

    public int Edge { get; set; }

    public int Centre { get; set; }

    public int Walk { get; set; }

    public int UnmetLinks { get; set; }

    /// <summary>The cost of the built room that moved (switchThreshold), negative.</summary>
    public int Switch { get; set; }

    /// <summary>The interior area placed.</summary>
    public int Cells { get; set; }

    /// <summary>The cell terms alone: soil, edge, centre.</summary>
    public int SiteTerms() => Soil + Edge + Centre;

    /// <summary>The sum of the terms and the required-link charges.</summary>
    public int Total() => Soil + Edge + Centre + Walk + Switch - (PlanWeights.BadRoutes * UnmetLinks);
}

/// <summary>The coarse placement.</summary>
public sealed partial class Embedding
{
    /// <summary>The obstacle level the blocks were placed under.</summary>
    public ObstacleLevel Level { get; set; }

    /// <summary>The siting seed whose start won.</summary>
    public Cell Seed { get; set; }

    public List<EmbeddedBlock> Blocks { get; set; } = [];

    /// <summary>The pinned standing rooms no block keeps, by room id.</summary>
    public List<HeldRoom> Held { get; set; } = [];

    /// <summary>The placed fields, by input order.</summary>
    public List<EmbeddedField> Fields { get; set; } = [];

    /// <summary>One entry per group (placed or unplaceable) and one per unsatisfied requirement or required link.</summary>
    public List<EmbedDecision> Decisions { get; set; } = [];

    public EmbedScore Score { get; set; } = new();

    /// <summary>The group's block.</summary>
    public (Rectangle Block, bool Ok) Block(int group)
    {
        foreach (var b in Blocks)
        {
            if (b.Group == group)
            {
                return (b.Interior, true);
            }
        }
        return (default, false);
    }

    /// <summary>The groups no position fit, with the reason.</summary>
    public List<EmbedDecision> Unplaceable() => [.. Decisions.Where(d => d.Verdict == PolicyConstants.EmbedUnplaceable)];

    /// <summary>The obstacle level the blocks were placed under: 0 is the strictest (every field cell and rich soil avoided), higher is looser.</summary>
    public int ObstacleLevel() => (int)Level;
}

/// <summary>A 2D prefix sum over the map.</summary>
internal sealed class Prefix
{
    private readonly long[] _s;

    public Prefix(int w, int h, Func<Cell, int> f)
    {
        W = w;
        H = h;
        _s = new long[(w + 1) * (h + 1)];
        for (var z = 0; z < h; z++)
        {
            for (var x = 0; x < w; x++)
            {
                long v = f(new Cell(x, z));
                _s[((z + 1) * (w + 1)) + x + 1] = v + _s[(z * (w + 1)) + x + 1] + _s[((z + 1) * (w + 1)) + x] - _s[(z * (w + 1)) + x];
            }
        }
    }

    public int W { get; }

    public int H { get; }

    /// <summary>The total over r, which lies inside the map.</summary>
    public long Sum(Rectangle r)
    {
        int x0 = r.X, z0 = r.Z, x1 = r.X + r.Width, z1 = r.Z + r.Height;
        var stride = W + 1;
        return _s[(z1 * stride) + x1] - _s[(z0 * stride) + x1] - _s[(z1 * stride) + x0] + _s[(z0 * stride) + x0];
    }

    public bool Inside(Rectangle r) => r.X >= 0 && r.Z >= 0 && r.X + r.Width <= W && r.Z + r.Height <= H;
}

/// <summary>
/// What the site prices the same at every obstacle level, built once per Embed and shared by its levels'
/// embedders; it is never written after that.
/// </summary>
internal partial class EmbedFixed
{
    public EmbedFixed(EmbedInput input, PlanScorer scorer)
    {
        int w = input.Survey.Bounds.Width, h = input.Survey.Bounds.Height;
        var cells = new Dictionary<Cell, SurveyCell>();
        foreach (var c in input.Survey.Cells)
        {
            cells[c.Cell] = c;
        }
        Base = new Prefix(w, h, c => PolicyFunctions.B2i(scorer.G.Core.Contains(c)));
        Soil = new Prefix(w, h, c => -((PlanWeights.Soil * scorer.G.Soil.GetValueOrDefault(c)) + (PlanWeights.Rock * PolicyFunctions.B2i(scorer.G.Rock.Contains(c)))));
        var clear = scorer.EdgeClear();
        Edge = new Prefix(w, h, c => -PlanScorer.EdgeCellCost(scorer.Ground, c, clear));
        Centre = new Prefix(w, h, c => -scorer.CentreCell(c));
        Rich = new Prefix(w, h, c => PolicyFunctions.B2i(scorer.G.Soil.GetValueOrDefault(c) == PolicyConstants.SoilCostRich));
        Solid = new Prefix(w, h, c => PolicyFunctions.B2i(scorer.G.Rock.Contains(c) || (cells.TryGetValue(c, out var sc) && sc.ThickRoof)));
        Walkable = new Prefix(w, h, c => PolicyFunctions.B2i(PolicyFunctions.WalkableCell(cells, c)));
        FieldMasks(input, scorer);
        var retired = new HashSet<Cell>();
        foreach (var g in input.RetiredGround)
        {
            foreach (var c in PolicyFunctions.RectCells(g.Bounds()))
            {
                retired.Add(c);
            }
        }
        Retired = new Prefix(w, h, c => PolicyFunctions.B2i(retired.Contains(c)));
    }

    /// <summary>Copies the shared pricing into one level's embedder.</summary>
    protected EmbedFixed(EmbedFixed f)
    {
        Base = f.Base;
        Soil = f.Soil;
        Edge = f.Edge;
        Centre = f.Centre;
        Solid = f.Solid;
        Walkable = f.Walkable;
        Rich = f.Rich;
        Retired = f.Retired;
        FieldMask = f.FieldMask;
        FieldCells = f.FieldCells;
    }

    /// <summary>Core cells with no obstacle.</summary>
    public Prefix Base { get; }

    /// <summary>Weighted cell costs.</summary>
    public Prefix Soil { get; }

    public Prefix Edge { get; }

    public Prefix Centre { get; }

    /// <summary>Rock or mountain roof.</summary>
    public Prefix Solid { get; }

    /// <summary>Ground a wall can stand on (enclosureCost).</summary>
    public Prefix Walkable { get; }

    /// <summary>Rich soil (PlanScore.RichCells).</summary>
    public Prefix Rich { get; }

    /// <summary>The retired ground under the map.</summary>
    public Prefix Retired { get; }

    /// <summary>Per field group, the soil mask of its crop.</summary>
    public IReadOnlyList<Prefix> FieldMask { get; private set; } = [];

    /// <summary>Per field group, the candidate cells of its crop.</summary>
    public IReadOnlyList<IReadOnlyList<Cell>> FieldCells { get; private set; } = [];
}

/// <summary>One level's immutable pricing of the site.</summary>
internal sealed partial class Embedder : EmbedFixed
{
    private readonly IReadOnlyList<RoomGroup> _groups;
    private readonly List<IReadOnlyList<(int W, int H)>> _shapes = []; // per group, the interiors (width, height) it may take
    private readonly List<bool> _sky = []; // per group: needs open sky
    private readonly Prefix _level; // core cells at the level
    private readonly List<Cell> _core;
    private readonly IReadOnlyList<Cell> _seeds = [];

    // The built world (layout_built.go): per group the standing room that is its block (zero Width for none)
    // and its id, whether it must not move, the retired ground under the map (rich soil is Rich).
    private List<Rectangle> _home;
    private List<int> _homeRoom;
    private readonly bool[] _pinned;
    private readonly bool _inFlight;

    // The group needs more floor than its standing rooms give.
    private readonly bool[] _outgrown;
    private readonly List<EmbedDecision> _builtReport;
    private readonly List<HeldRoom> _held = [];

    // The groups from nRooms on are the fields (EmbedInput.WithFields).
    private readonly int _nRooms;

    public Embedder(EmbedInput input, int depth, PlanScorer scorer, ObstacleLevel level, EmbedFixed fixedPricing)
        : base(fixedPricing)
    {
        int w = input.Survey.Bounds.Width, h = input.Survey.Bounds.Height;
        _groups = input.Groups;
        _nRooms = input.Groups.Count - input.Fields.Count;
        for (var i = 0; i < input.Groups.Count; i++)
        {
            var g = input.Groups[i];
            _shapes.Add(i >= _nRooms ? PolicyFunctions.BlockShapes(g.Area, g.Area) : PolicyFunctions.GroupShapes(g, depth)); // no roof bounds a field
            _sky.Add(g.Requires.Contains(EnvRequirement.OpenSky));
        }
        var lg = scorer.G.Without(scorer.G.CoreObstaclesFor(input.Zones, level, input.Fields.Count == 0));
        _level = new Prefix(w, h, c => PolicyFunctions.B2i(lg.Core.Contains(c)));
        _core = [.. lg.Core];
        PolicyFunctions.SortCells(_core);
        _inFlight = input.InFlight;
        _pinned = new bool[input.Groups.Count];
        var (home, homeRoom, outgrown, report) = PolicyFunctions.BuiltHomes([.. input.Groups.Take(_nRooms)], input.Built);
        _outgrown = outgrown;
        _builtReport = [.. report];
        _home = [.. home, .. new Rectangle[input.Groups.Count - _nRooms]];
        _homeRoom = [.. homeRoom, .. new int[input.Groups.Count - _nRooms]];
        for (var i = 0; i < _home.Count; i++)
        {
            // A room whose ring is off the map is no block.
            if (_home[i].Width > 0 && !Base.Inside(PolicyFunctions.Pad(_home[i], 1)))
            {
                _builtReport.Add(new EmbedDecision { Group = i, Other = -1, Verdict = PolicyConstants.EmbedKeep, Reason = PolicyConstants.ReasonEmbedBuiltOffMap, Room = _homeRoom[i] });
                _home[i] = default;
                _homeRoom[i] = 0;
            }
        }
        foreach (var b in input.Built)
        {
            for (var i = 0; i < _homeRoom.Count; i++)
            {
                if (_homeRoom[i] == b.Room)
                {
                    _pinned[i] = b.Pinned;
                }
            }
        }
        HoldStanding(input.Standing);
        if (_core.Count == 0)
        {
            return;
        }
        var seeds = lg.SiteSeeds(PolicyFunctions.SiteSearchTop);
        _seeds = seeds.Count == 0 ? PolicyFunctions.Stepped(_core, PolicyFunctions.SiteSearchTop) : seeds;
    }

    internal IReadOnlyList<RoomGroup> Groups => _groups;

    internal IReadOnlyList<Cell> Core => _core;

    internal IReadOnlyList<Cell> Seeds => _seeds;

    internal int NRooms => _nRooms;

    internal IReadOnlyList<Rectangle> Home => _home;

    internal IReadOnlyList<int> HomeRoom => _homeRoom;

    internal IReadOnlyList<HeldRoom> Held => _held;

    internal Prefix Level => _level;

    internal IReadOnlyList<bool> Sky => _sky;

    internal IReadOnlyList<IReadOnlyList<(int W, int H)>> Shapes => _shapes;

    public bool Fits(int i, Rectangle r, IReadOnlyList<Rectangle> blocks, bool sky)
    {
        if (IsField(i))
        {
            return FieldFits(i, r, blocks);
        }
        var ring = PolicyFunctions.Pad(r, 1);
        if (!Base.Inside(ring) || Base.Sum(ring) != (long)ring.Width * ring.Height || _level.Sum(r) != (long)r.Width * r.Height)
        {
            return false;
        }
        if ((sky && Solid.Sum(r) != 0) || Retired.Sum(r) != 0)
        {
            return false;
        }
        for (var j = 0; j < blocks.Count; j++)
        {
            if (j != i && blocks[j].Width > 0 && PolicyFunctions.Collides(r, blocks[j]))
            {
                return false;
            }
        }
        return !HitsHeld(r);
    }

    /// <summary>The objective's share of group i at r: cell terms and every link to a placed block. Higher is worse.</summary>
    public double BlockCost(int i, Rectangle r, IReadOnlyList<Rectangle> blocks) =>
        BlockCostWith(i, r, blocks, EnclosureFor(i, blocks));

    /// <summary>The enclosureSet group i is priced against, null when the group is not priced by enclosure.</summary>
    public EnclosureSet? EnclosureFor(int i, IReadOnlyList<Rectangle> blocks)
    {
        if (IsField(i) || !Unpriced(i))
        {
            return null;
        }
        return EnclosureRects(i, blocks);
    }

    /// <summary>BlockCost with the enclosureSet EnclosureFor built, for a loop over candidates against fixed blocks.</summary>
    public double BlockCostWith(int i, Rectangle r, IReadOnlyList<Rectangle> blocks, EnclosureSet? enc)
    {
        double cost;
        if (IsField(i))
        {
            cost = FieldCellCost(r);
        }
        else
        {
            cost = -(Soil.Sum(r) + Centre.Sum(r) + Edge.Sum(PolicyFunctions.Pad(r, 1)));
            if (enc is not null)
            {
                cost += EnclosureCost(r, enc);
            }
        }
        foreach (var l in _groups[i].Links)
        {
            var b = blocks[l.To];
            if (b.Width > 0 && l.To != i)
            {
                cost += PolicyFunctions.LinkCost(l, Gap(i, r, l.To, b));
            }
        }
        return cost;
    }

    /// <summary>The cheapest fitting position on the smallest ring around the anchors that has one.</summary>
    public (Rectangle Rect, bool Ok) PlaceNear(int i, Rectangle[] blocks, IReadOnlyList<int> anchors)
    {
        var maxRing = _level.W + _level.H;
        var enc = EnclosureFor(i, blocks);
        for (var ring = 1; ring <= maxRing; ring++)
        {
            var best = default(Rectangle);
            double bestCost = 0;
            var found = false;
            foreach (var a in anchors)
            {
                var ab = blocks[a];
                var fieldGap = IsField(i) || IsField(a);
                foreach (var sh in _shapes[i])
                {
                    PolicyFunctions.RingPositions(ab, sh, ring, fieldGap, (x, z) =>
                    {
                        var r = new Rectangle(x, z, sh.W, sh.H);
                        if (!Fits(i, r, blocks, _sky[i]))
                        {
                            return;
                        }
                        var c = BlockCostWith(i, r, blocks, enc);
                        if (PolicyFunctions.Better(c, r, bestCost, best, found))
                        {
                            best = r;
                            bestCost = c;
                            found = true;
                        }
                    });
                }
            }
            if (found)
            {
                return (best, true);
            }
        }
        return (default, false);
    }

    /// <summary>The cheapest fitting position on the map, or the fitting one nearest toward when toward is set (a start's first block).</summary>
    public (Rectangle Rect, bool Ok) PlaceAnywhere(int i, Rectangle[] blocks, bool sky, Cell? toward)
    {
        var best = default(Rectangle);
        double bestCost = 0;
        var found = false;
        var enc = EnclosureFor(i, blocks);
        foreach (var c in CellsFor(i))
        {
            foreach (var sh in _shapes[i])
            {
                var r = new Rectangle(c.X, c.Z, sh.W, sh.H);
                if (!Fits(i, r, blocks, sky))
                {
                    continue;
                }
                double cost;
                if (toward is { } t)
                {
                    double dx = r.X + (r.Width / 2) - t.X, dz = r.Z + (r.Height / 2) - t.Z;
                    cost = (dx * dx) + (dz * dz);
                    if (!IsField(i) && Rich.Sum(r) > 0)
                    {
                        cost += (double)_level.W * _level.W * _level.H * _level.H; // after every fit clear of rich soil
                    }
                }
                else
                {
                    cost = BlockCostWith(i, r, blocks, enc);
                }
                if (PolicyFunctions.Better(cost, r, bestCost, best, found))
                {
                    best = r;
                    bestCost = cost;
                    found = true;
                }
            }
        }
        return (best, found);
    }

    /// <summary>Builds the greedy embedding from a siting seed and searches it.</summary>
    public (Rectangle[] Blocks, BuiltRun Built) Start(Cell seed, int iters)
    {
        var n = _groups.Count;
        var blocks = new Rectangle[n];
        var done = new bool[n]; // tried: placed or unplaceable
        double Total(int i) => _groups[i].Links.Sum(l => l.Weight);
        var placedAny = false;
        for (var i = 0; i < _home.Count; i++)
        {
            // The standing rooms are the fixed start.
            if (_home[i].Width > 0)
            {
                blocks[i] = _home[i];
                done[i] = true;
                placedAny = true;
            }
        }
        for (var step = 0; step < n; step++)
        {
            int pick = -1;
            var pickReq = false;
            var pickW = -1.0;
            for (var i = 0; i < _groups.Count; i++)
            {
                if (done[i])
                {
                    continue;
                }
                var req = false;
                var w = 0.0;
                foreach (var l in _groups[i].Links)
                {
                    if (blocks[l.To].Width > 0)
                    {
                        w += l.Weight;
                        req = req || l.Required;
                    }
                }
                if (w == 0)
                {
                    // Unlinked to what is placed: after every linked group (their weights are positive), the heavier first.
                    w = -1 / (1 + Total(i));
                }
                if (pick < 0 || (req && !pickReq) || (req == pickReq && w > pickW))
                {
                    pick = i;
                    pickReq = req;
                    pickW = w;
                }
            }
            if (pick < 0)
            {
                break;
            }
            done[pick] = true;
            var anchors = new List<int>();
            foreach (var l in _groups[pick].Links)
            {
                if (blocks[l.To].Width > 0)
                {
                    anchors.Add(l.To);
                }
            }
            Rectangle r;
            bool ok;
            if (anchors.Count > 0)
            {
                (r, ok) = PlaceNear(pick, blocks, anchors);
            }
            else if (step == 0 && !placedAny)
            {
                (r, ok) = PlaceAnywhere(pick, blocks, _sky[pick], seed);
            }
            else
            {
                (r, ok) = PlaceAnywhere(pick, blocks, _sky[pick], null);
            }
            if (ok)
            {
                blocks[pick] = r;
            }
        }
        Search(blocks, seed, iters);
        // The relocation pass (layout_built.go) is the one place a built room moves; the unbuilt rooms are
        // searched again around the result.
        var built = Relocate(blocks);
        if (built.Moved >= 0)
        {
            Search(blocks, seed, iters);
        }
        return (blocks, built);
    }

    /// <summary>The seeded local search: siteSearchIters moves per block, a move kept only when the objective strictly falls. A built room is never moved here (see Relocate).</summary>
    public void Search(Rectangle[] blocks, Cell seed, int iters)
    {
        var placed = new List<int>();
        var movable = new List<int>();
        for (var i = 0; i < blocks.Length; i++)
        {
            if (blocks[i].Width > 0)
            {
                placed.Add(i);
                if (_home[i].Width == 0)
                {
                    movable.Add(i);
                }
            }
        }
        if (movable.Count == 0)
        {
            return;
        }
        var rng = new SearchRand(seed);
        var total = iters * placed.Count;
        for (var it = 0; it < total; it++)
        {
            var i = movable[rng.Intn(movable.Count)];
            var old = blocks[i];
            switch (rng.Intn(4))
            {
                case 0:
                case 1: // beside a linked block, else any block
                {
                    List<int> anchors = [.. _groups[i].Links.Where(l => blocks[l.To].Width > 0).Select(l => l.To)];
                    if (anchors.Count == 0 || rng.Intn(2) == 0)
                    {
                        anchors = placed;
                    }
                    var a = anchors[rng.Intn(anchors.Count)];
                    if (a == i)
                    {
                        continue;
                    }
                    var before = BlockCost(i, old, blocks);
                    blocks[i] = default;
                    var (r, ok) = PlaceNear(i, blocks, [a]);
                    if (ok && BlockCost(i, r, blocks) < before)
                    {
                        blocks[i] = r;
                    }
                    else
                    {
                        blocks[i] = old;
                    }
                    break;
                }

                case 2: // a random core cell
                {
                    var before = BlockCost(i, old, blocks);
                    blocks[i] = default;
                    var cells = CellsFor(i);
                    var c = cells[rng.Intn(cells.Count)];
                    var sh = _shapes[i][rng.Intn(_shapes[i].Count)];
                    var r = new Rectangle(c.X, c.Z, sh.W, sh.H);
                    if (Fits(i, r, blocks, _sky[i]) && BlockCost(i, r, blocks) < before)
                    {
                        blocks[i] = r;
                    }
                    else
                    {
                        blocks[i] = old;
                    }
                    break;
                }

                default: // swap with another block
                {
                    var j = movable[rng.Intn(movable.Count)];
                    if (j == i)
                    {
                        continue;
                    }
                    var oldJ = blocks[j];
                    var before = BlockCost(i, old, blocks) + BlockCost(j, oldJ, blocks);
                    blocks[i] = default;
                    blocks[j] = default;
                    var (ri, okI) = PlaceAt(i, blocks, oldJ);
                    if (okI)
                    {
                        blocks[i] = ri;
                    }
                    var (rj, okJ) = PlaceAt(j, blocks, old);
                    if (okI && okJ)
                    {
                        blocks[j] = rj;
                        if (BlockCost(i, ri, blocks) + BlockCost(j, rj, blocks) < before)
                        {
                            continue;
                        }
                    }
                    blocks[i] = old;
                    blocks[j] = oldJ;
                    break;
                }
            }
        }
    }

    /// <summary>Group i's block with its top-left at at's, in the first shape that fits.</summary>
    private (Rectangle Rect, bool Ok) PlaceAt(int i, Rectangle[] blocks, Rectangle at)
    {
        var best = default(Rectangle);
        double bestCost = 0;
        var found = false;
        foreach (var sh in _shapes[i])
        {
            var r = new Rectangle(at.X, at.Z, sh.W, sh.H);
            if (!Fits(i, r, blocks, _sky[i]))
            {
                continue;
            }
            var c = BlockCost(i, r, blocks);
            if (PolicyFunctions.Better(c, r, bestCost, best, found))
            {
                best = r;
                bestCost = c;
                found = true;
            }
        }
        return (best, found);
    }

    /// <summary>The whole embedding's cost.</summary>
    public double Objective(Rectangle[] blocks)
    {
        var total = 0.0;
        for (var i = 0; i < blocks.Length; i++)
        {
            var r = blocks[i];
            if (r.Width == 0)
            {
                continue;
            }
            total += BlockCost(i, r, blocks);
            // BlockCost counts each link from both ends; the pair is one cost.
            foreach (var l in _groups[i].Links)
            {
                var b = blocks[l.To];
                if (b.Width > 0 && l.To > i)
                {
                    total -= PolicyFunctions.LinkCost(l, Gap(i, r, l.To, b));
                }
            }
        }
        return total;
    }

    /// <summary>Counts the required links between placed rooms that are not adjacent, once per pair.</summary>
    public int UnmetLinks(Rectangle[] blocks)
    {
        var n = 0;
        for (var i = 0; i < _nRooms; i++)
        {
            var b = blocks[i];
            foreach (var l in _groups[i].Links)
            {
                if (l.Required && l.To > i && b.Width > 0 && blocks[l.To].Width > 0 && Gap(i, b, l.To, blocks[l.To]) != 1)
                {
                    n++;
                }
            }
        }
        return n;
    }
}

/// <summary>One obstacle level's best start.</summary>
internal sealed partial class EmbedRun
{
    public required Embedder E { get; init; }

    public ObstacleLevel Level { get; init; }

    public Cell Seed { get; init; }

    public Rectangle[] Blocks { get; init; } = [];

    public int Unplaced { get; set; }

    /// <summary>The relocation pass's verdict (layout_built.go).</summary>
    public BuiltRun Built { get; init; } = new();

    /// <summary>Counts the required links between placed blocks that are not adjacent.</summary>
    public int Unmet { get; set; }

    /// <summary>The objective plus the relocation price.</summary>
    public double Obj { get; set; }

    /// <summary>
    /// Counts what the run leaves undone: a room with no block and a required link not adjacent. The plan score
    /// prices both at 100000 (MissingRoom, BadRoutes), so a looser obstacle level that keeps every required
    /// link beats a stricter one that breaks six.
    /// </summary>
    public int Failed() => Unplaced + Unmet;

    /// <summary>Reads the run into the result: the blocks, the score and a decision for every group and every requirement the site cannot meet.</summary>
    public Embedding ToEmbedding(EmbedInput input, PlanScorer scorer)
    {
        var e = E;
        var output = new Embedding { Level = Level, Seed = Seed };
        output.Held = [.. e.Held];
        output.Decisions.AddRange(e.BuiltReport);
        output.Decisions.AddRange(Built.Decisions);
        output.Score.Switch = -(int)Built.Price;
        for (var i = 0; i < input.Groups.Count; i++)
        {
            var g = input.Groups[i];
            var b = Blocks[i];
            if (e.IsField(i))
            {
                EmbedField(output, input.Fields[i - e.NRooms].Node.Id, b);
                continue;
            }
            if (b.Width == 0)
            {
                var reason = PolicyConstants.ReasonEmbedNoGround;
                if (e.Sky[i])
                {
                    var (_, ok) = e.PlaceAnywhere(i, Blocks, false, null);
                    if (ok)
                    {
                        reason = PolicyConstants.ReasonEmbedOpenSkyGround;
                    }
                }
                output.Decisions.Add(new EmbedDecision { Group = i, Other = -1, Verdict = PolicyConstants.EmbedUnplaceable, Reason = reason });
                continue;
            }
            var built = 0;
            var replaces = 0;
            if (i < e.NRooms && e.Home[i].Width > 0)
            {
                if (Built.Moved == i)
                {
                    replaces = e.HomeRoom[i];
                }
                else
                {
                    built = e.HomeRoom[i];
                }
            }
            output.Blocks.Add(new EmbeddedBlock { Group = i, Interior = b, Built = built, Replaces = replaces });
            output.Decisions.Add(new EmbedDecision { Group = i, Other = -1, Verdict = PolicyConstants.EmbedPlace, Reason = PolicyConstants.ReasonEmbedPlaced });
            output.Score.Cells += b.Width * b.Height;
            output.Score.Soil += (int)e.Soil.Sum(b);
            output.Score.Centre += (int)e.Centre.Sum(b);
            output.Score.Edge += (int)e.Edge.Sum(PolicyFunctions.Pad(b, 1));
            foreach (var req in g.Requires)
            {
                if (req == EnvRequirement.Clean)
                {
                    output.Decisions.Add(new EmbedDecision { Group = i, Other = -1, Verdict = PolicyConstants.EmbedUnsatisfied, Reason = PolicyConstants.ReasonEmbedNoSiteSignal, Requirement = req });
                }
            }
            foreach (var l in g.Links)
            {
                var ob = Blocks[l.To];
                if (ob.Width == 0 || l.To < i)
                {
                    continue;
                }
                var gap = e.Gap(i, b, l.To, ob);
                output.Score.Walk -= (int)Math.Round(PlanWeights.Walk * l.Weight * (gap - 1), MidpointRounding.AwayFromZero);
                if (l.Required && gap != 1)
                {
                    output.Score.UnmetLinks++;
                    output.Decisions.Add(new EmbedDecision { Group = i, Other = l.To, Verdict = PolicyConstants.EmbedUnsatisfied, Reason = PolicyConstants.ReasonEmbedLinkNotAdjacent });
                }
            }
        }
        return output;
    }
}

public static partial class PolicyFunctions
{
    internal static int B2i(bool b) => b ? 1 : 0;

    /// <summary>The interiors a group of area cells may take: as deep as the roof allows is never needed, so the squarest rectangle within depth, and its turn.</summary>
    internal static IReadOnlyList<(int W, int H)> BlockShapes(int area, int depth)
    {
        area = Math.Max(area, 1);
        var h = Math.Min(depth, (int)Math.Ceiling(Math.Sqrt(area)));
        var w = (area + h - 1) / h;
        var output = new List<(int W, int H)> { (w, h) };
        if (w != h)
        {
            output.Add((h, w));
        }
        return output;
    }

    /// <summary>The interiors a group may take: a strip of its Width and the length its area needs, and its turn, else the squarest blocks.</summary>
    internal static IReadOnlyList<(int W, int H)> GroupShapes(RoomGroup g, int depth)
    {
        if (g.Width <= 0)
        {
            return BlockShapes(g.Area, depth);
        }
        var length = (Math.Max(g.Area, 1) + g.Width - 1) / g.Width;
        return [(length, g.Width), (g.Width, length)];
    }

    /// <summary>
    /// The walking gap between two interiors in cells: 1 when they share a wall with overlapping sides
    /// (adjacent), the distance between them otherwise. Interiors collide when the walls of one would touch the
    /// other (Collides).
    /// </summary>
    internal static int GapDist(Rectangle a, Rectangle b)
    {
        int sx = AxisGap(a.X, a.Width, b.X, b.Width), sz = AxisGap(a.Z, a.Height, b.Z, b.Height);
        if (sx >= 1 && sz >= 1)
        {
            return sx + sz;
        }
        if (sx >= 1)
        {
            return sx + B2i(sz == 0);
        }
        if (sz >= 1)
        {
            return sz + B2i(sx == 0);
        }
        return 0;
    }

    internal static int AxisGap(int a0, int al, int b0, int bl) => Math.Max(b0 - (a0 + al), a0 - (b0 + bl));

    /// <summary>Reports interiors that overlap or have no wall cell between them.</summary>
    internal static bool Collides(Rectangle a, Rectangle b) =>
        // AxisGap < 1 on both axes: each interval reaches the other's start.
        b.X <= a.X + a.Width && a.X <= b.X + b.Width && b.Z <= a.Z + a.Height && a.Z <= b.Z + b.Height;

    internal static double LinkCost(GroupLink l, int gap)
    {
        var c = PlanWeights.Walk * l.Weight * (gap - 1);
        if (l.Required && gap != 1)
        {
            c += PlanWeights.BadRoutes;
        }
        return c;
    }

    /// <summary>Ranks candidate positions: lower cost, then (Z, X).</summary>
    internal static bool Better(double cost, Rectangle r, double bestCost, Rectangle best, bool found)
    {
        if (!found || cost < bestCost)
        {
            return true;
        }
        return cost == bestCost && (r.Z < best.Z || (r.Z == best.Z && r.X < best.X));
    }

    /// <summary>
    /// Calls f with every start (x, z) of a block of interior sh whose gap to the anchor ab is ring (GapDist,
    /// or the field gap when field): exactly the starts the window ab padded by ring holds with that gap,
    /// enumerated by axis gap (AxisGap) rather than filtered from the whole window. Each axis offers two starts
    /// at every gap s >= 1 (beside the anchor on either side), two at gap 0 (flush), and the starts in between
    /// that overlap it (gap &lt; 0). GapDist is sx+sz when both gaps are >= 1, sx plus one when sz is 0 and sx
    /// >= 1, sx alone when sz &lt; 0 (and the same turned); the field gap is max(sx,0)+max(sz,0), at least 1.
    /// The order of the calls is not the window's, which no caller reads: Better ranks by cost and then (Z, X).
    /// </summary>
    internal static void RingPositions(Rectangle ab, (int W, int H) sh, int ring, bool field, Action<int, int> f)
    {
        int w = sh.W, h = sh.H;
        var r = ring;
        AxisSet Xs(int s) => new(ab.X - w - s, ab.X + ab.Width + s, true);
        AxisSet Zs(int s) => new(ab.Z - h - s, ab.Z + ab.Height + s, true);
        var xLow = new AxisSet(ab.X - w, ab.X + ab.Width, false); // gap <= 0
        var zLow = new AxisSet(ab.Z - h, ab.Z + ab.Height, false); // gap <= 0
        var xNeg = new AxisSet(ab.X - w + 1, ab.X + ab.Width - 1, false); // gap < 0
        var zNeg = new AxisSet(ab.Z - h + 1, ab.Z + ab.Height - 1, false);
        void Product(AxisSet xa, AxisSet za) => za.Each(z => xa.Each(x => f(x, z)));
        for (var s = 1; s < r; s++)
        {
            // Both gaps >= 1: they sum to ring.
            Product(Xs(s), Zs(r - s));
        }
        if (field && r == 1)
        {
            // The gap is floored at 1: touching, or overlapping.
            Product(xLow, zLow);
            Product(Xs(1), zLow);
            Product(xLow, Zs(1));
        }
        else if (field)
        {
            Product(Xs(r), zLow);
            Product(xLow, Zs(r));
        }
        else
        {
            Product(Xs(r), zNeg); // beside the anchor, overlapping on the other axis
            Product(xNeg, Zs(r));
            if (r >= 2)
            {
                // Flush on the other axis: the wall counts one.
                Product(Xs(r - 1), Zs(0));
                Product(Xs(0), Zs(r - 1));
            }
        }
    }

    /// <summary>
    /// Places the groups on the site. The result is deterministic: the seeds and the search stream come from
    /// the site, and ties break by position.
    /// </summary>
    public static Embedding Embed(EmbedInput input)
    {
        var (depth, _) = RoomLimits(input.RoofDistance);
        for (var i = 0; i < input.Groups.Count; i++)
        {
            var g = input.Groups[i];
            if (g.Id != i)
            {
                throw new ClusterException(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"group {i} has id {g.Id}"));
            }
        }
        input = input.WithFields();
        var scorer = new PlanScorer(input.Zones, input.Reservations, input.Survey);
        var fixedPricing = new EmbedFixed(input, scorer);
        EmbedRun? best = null;
        foreach (var level in ObstacleLevels.All)
        {
            var run = EmbedLevel(input, depth, scorer, level, fixedPricing);
            // A looser level takes over only by FieldGain: the strictest level keeps the plan unless the
            // ground it spares costs more than that.
            if (best is null || run.Failed() < best.Failed() || (run.Failed() == best.Failed() && run.Obj + PlanWeights.FieldGain < best.Obj))
            {
                best = run;
            }
        }
        return best!.ToEmbedding(input, scorer);
    }

    private static EmbedRun EmbedLevel(EmbedInput input, int depth, PlanScorer scorer, ObstacleLevel level, EmbedFixed fixedPricing)
    {
        var e = new Embedder(input, depth, scorer, level, fixedPricing);
        if (input.Groups.Count == 0 || e.Core.Count == 0)
        {
            var empty = new Rectangle[input.Groups.Count];
            var emptyRun = new EmbedRun { E = e, Level = level, Blocks = empty, Built = new BuiltRun { Moved = -1 } };
            for (var i = 0; i < e.NRooms; i++)
            {
                // The standing rooms stand with or without core.
                empty[i] = e.Home[i];
                if (empty[i].Width == 0)
                {
                    emptyRun.Unplaced++;
                }
            }
            return emptyRun;
        }
        var runs = new EmbedRun[e.Seeds.Count];
        EachIndex(runs.Length, i =>
        {
            var (blocks, built) = e.Start(e.Seeds[i], SiteSearchIters);
            var run = new EmbedRun { E = e, Level = level, Seed = e.Seeds[i], Blocks = blocks, Built = built };
            for (var k = 0; k < e.NRooms; k++)
            {
                // A field no soil fits is the same at every level.
                if (blocks[k].Width == 0)
                {
                    run.Unplaced++;
                }
            }
            run.Unmet = e.UnmetLinks(blocks);
            runs[i] = run;
        });
        foreach (var r in runs)
        {
            r.Obj = e.Objective(r.Blocks) + r.Built.Price;
        }
        var best = runs[0];
        foreach (var r in runs.Skip(1))
        {
            if (r.Failed() < best.Failed() || (r.Failed() == best.Failed() && r.Obj < best.Obj))
            {
                best = r;
            }
        }
        return best;
    }
}

/// <summary>The start coordinates on one axis that put a block at one axis gap from an anchor: the two starts Lo and Hi when Pair, else every start from Lo to Hi.</summary>
internal readonly record struct AxisSet(int Lo, int Hi, bool Pair)
{
    public void Each(Action<int> f)
    {
        if (Pair)
        {
            f(Lo);
            f(Hi);
            return;
        }
        for (var v = Lo; v <= Hi; v++)
        {
            f(v);
        }
    }
}
