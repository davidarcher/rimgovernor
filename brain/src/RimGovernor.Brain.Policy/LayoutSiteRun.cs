namespace RimGovernor.Brain.Policy;

/// <summary>
/// What a free site node is priced by. WindW is the watts a turbine delivers with its lane clear (its nominal output
/// times the profile's daily share); ConduitW and WallW the watts a cell of conduit and a cell of wall cost to build.
/// Weights are the layout's site weights; the zero value is DefaultSiteTypeWeights.
/// </summary>
public sealed record SitePrice
{
    public SiteTypeWeights Weights { get; init; }
    public double WindW { get; init; }
    public double ConduitW { get; init; }
    public double WallW { get; init; }

    internal SiteTypeWeights WeightsOrDefault() => Weights == new SiteTypeWeights() ? PolicyFunctions.DefaultSiteTypeWeights() : Weights;
}

/// <summary>The cheapest placement of a node: the turn and origin, the cells blocked in its own lane and the price in watts, split into what the wind loses (LaneW, its own lane and the lanes it blocks) and the rest.</summary>
public sealed class SiteQuote
{
    public SiteQuote(SiteNode shape, Cell origin)
    {
        Shape = shape;
        Origin = origin;
    }

    public SiteNode Shape { get; }
    public Cell Origin { get; }
    public int Blocked { get; set; }
    public double LaneW { get; set; }
    public double CostW { get; set; }

    /// <summary>The quote's whole price.</summary>
    public double PriceW() => LaneW + CostW;
}

/// <summary>A wind lane already placed: its cells, the footprint it belongs to (never a blocker of it) and the cells now blocked in it.</summary>
internal sealed class PlacedLane
{
    public Region Region { get; init; } = new();
    public Region Own { get; init; } = new();
    public bool[] Mask { get; init; } = [];
    public int Blocked { get; set; }
}

/// <summary>
/// The site embedder as a run that places one node at a time, so the power planner can quote a candidate site, take it
/// and quote the next without placing the sites before it again. A free node's origin is priced in sustained watts and
/// the cheapest wins; ties go to the lowest cell, so the result depends only on the inputs. A candidate costs the output
/// its wind lane loses (and the output its footprint takes from lanes already placed), the conduit between its
/// footprint and the nearest room or placed site, the ring cells it adds to the enclosure, and the soil its footprint
/// covers.
/// </summary>
public sealed class SiteRun
{
    private readonly SiteEmbedInput input;
    private readonly Embedder e;
    private readonly SitePrice price;
    private readonly int w;
    private readonly int h;
    private readonly HashSet<string> ids = new(StringComparer.Ordinal);

    /// <summary>Placed room interiors.</summary>
    private readonly List<Rectangle> rooms = [];

    /// <summary>Placed field interiors: no footprint stands on one.</summary>
    private readonly List<Rectangle> fields = [];

    private readonly List<Region> taken = [];

    /// <summary>The placed footprints' bounds, the conduit's other end.</summary>
    private readonly List<Rectangle> foot = [];

    private readonly WindGround ground;
    private readonly List<PlacedLane> lanes = [];

    /// <summary>The soil units a cell yields, times SoilFixed.</summary>
    private readonly Prefix units;

    // The wall prefixes count the walkable ground the enclosure does not yet cover: out to the killbox yard (WallIn) and out to the wall's thickness beyond it (WallGrown).
    private readonly bool[] walk;
    private readonly bool[] coverIn;
    private readonly bool[] coverGrown;
    private Prefix wallIn = null!;
    private Prefix wallGrown = null!;

    private readonly List<SitePlacement> outSites = [];
    private readonly List<SiteDecision> outDecisions = [];

    public SiteRun(SiteEmbedInput input)
    {
        this.input = input;
        price = input.Price;
        ground = new WindGround(input);
        var (depth, _) = PolicyFunctions.RoomLimits(input.Embed.RoofDistance);
        var scorer = new PlanScorer(input.Embed.Zones, input.Embed.Reservations, input.Embed.Survey);
        e = new Embedder(input.Embed, depth, scorer, input.Embedding.Level, new EmbedFixed(input.Embed, scorer));
        (w, h) = (input.Embed.Survey.Bounds.Width, input.Embed.Survey.Bounds.Height);
        var n = Math.Max(w * h, 0);
        var soilUnits = new int[n];
        walk = new bool[n];
        Array.Fill(walk, true); // an unread cell is not impassable
        foreach (var c in input.Embed.Survey.Cells)
        {
            if (c.Cell.X < 0 || c.Cell.Z < 0 || c.Cell.X >= w || c.Cell.Z >= h)
            {
                continue;
            }
            var i = (c.Cell.Z * w) + c.Cell.X;
            walk[i] = !(c.Rock || c.Hazard || (!c.Walkable && !c.Built));
            if (!c.Rock && c.Fertility >= PolicyFunctions.ZoneFieldFertilityValue)
            {
                soilUnits[i] = (int)(c.Fertility * SoilFixed);
            }
        }
        units = new Prefix(w, h, c => soilUnits[(c.Z * w) + c.X]);
        (coverIn, coverGrown) = (new bool[n], new bool[n]);
        foreach (var b in input.Embedding.Blocks)
        {
            rooms.Add(b.Interior);
            Cover(b.Interior);
        }
        foreach (var f in input.Embedding.Fields)
        {
            fields.Add(f.Interior);
        }
        RebuildWall();
    }

    /// <summary>The fixed-point scale of a soil unit in the ground prefix.</summary>
    internal const int SoilFixed = 1000;

    /// <summary>Claims a node's id; a node needs a unique id and a footprint.</summary>
    public void Admit(SiteNode n)
    {
        if (n.Id == "" || ids.Contains(n.Id) || n.Footprint.Empty())
        {
            throw new SiteNodeException($"\"{n.Id}\" needs a unique id and a footprint");
        }
        ids.Add(n.Id);
    }

    /// <summary>Marks the enclosure a block at b needs: its wall cells and the killbox yard (PerimeterGap), and the ring cells (PerimeterThick) outside them.</summary>
    private void Cover(Rectangle b)
    {
        const int yard = 1 + PolicyFunctions.PerimeterGap;
        var onMap = new Rectangle { Width = w, Height = h };
        void Mark(bool[] cover, Rectangle rc)
        {
            rc = PolicyFunctions.ClipRect(rc, onMap);
            for (var z = rc.Z; z < rc.Z + rc.Height; z++)
            {
                for (var x = rc.X; x < rc.X + rc.Width; x++)
                {
                    cover[(z * w) + x] = true;
                }
            }
        }
        Mark(coverIn, PolicyFunctions.Pad(b, yard));
        Mark(coverGrown, PolicyFunctions.Pad(b, yard + PolicyFunctions.PerimeterThick));
    }

    private void RebuildWall()
    {
        wallIn = new Prefix(w, h, c => { var i = (c.Z * w) + c.X; return PolicyFunctions.B2i(walk[i] && !coverIn[i]); });
        wallGrown = new Prefix(w, h, c => { var i = (c.Z * w) + c.X; return PolicyFunctions.B2i(walk[i] && !coverGrown[i]); });
    }

    /// <summary>The ring cells a footprint at b adds to the enclosure: the walkable ground its grown extent covers beyond the enclosure, less the walkable ground its own yard turns from ring into interior.</summary>
    private int WallCells(Rectangle b)
    {
        const int yard = 1 + PolicyFunctions.PerimeterGap;
        var onMap = new Rectangle { Width = w, Height = h };
        return (int)Math.Max(wallGrown.Sum(PolicyFunctions.ClipRect(PolicyFunctions.Pad(b, yard + PolicyFunctions.PerimeterThick), onMap)) - wallIn.Sum(PolicyFunctions.ClipRect(PolicyFunctions.Pad(b, yard), onMap)), 0);
    }

    /// <summary>The conduit run a footprint at b needs: the Manhattan gap to the nearest room or placed site; none while nothing stands.</summary>
    private int ConduitCells(Rectangle b)
    {
        var best = -1;
        void Consider(Rectangle t)
        {
            var d = Math.Max(PolicyFunctions.AxisGap(b.X, b.Width, t.X, t.Width), 0) + Math.Max(PolicyFunctions.AxisGap(b.Z, b.Height, t.Z, t.Height), 0);
            if (best < 0 || d < best)
            {
                best = d;
            }
        }
        foreach (var t in rooms)
        {
            Consider(t);
        }
        foreach (var t in foot)
        {
            Consider(t);
        }
        return Math.Max(best, 0);
    }

    /// <summary>A footprint at b off the rooms' walls, the fields and the footprints already taken. Lanes never enter into it.</summary>
    private bool FreeRect(Rectangle b)
    {
        foreach (var t in rooms)
        {
            if (PolicyFunctions.Collides(b, t))
            {
                return false;
            }
        }
        foreach (var t in fields)
        {
            if (PolicyFunctions.AxisGap(b.X, b.Width, t.X, t.Width) < 0 && PolicyFunctions.AxisGap(b.Z, b.Height, t.Z, t.Height) < 0)
            {
                return false;
            }
        }
        for (var i = 0; i < foot.Count; i++)
        {
            var t = foot[i];
            if (PolicyFunctions.AxisGap(b.X, b.Width, t.X, t.Width) < 0 && PolicyFunctions.AxisGap(b.Z, b.Height, t.Z, t.Height) < 0 && (taken[i].IsRect() || taken[i].OverlapsRect(b)))
            {
                return false;
            }
        }
        return true;
    }

    private bool FreeRegion(Region fp)
    {
        var b = fp.Bounds();
        foreach (var t in rooms)
        {
            if (PolicyFunctions.Collides(b, t))
            {
                return false;
            }
        }
        foreach (var t in fields)
        {
            if (fp.OverlapsRect(t))
            {
                return false;
            }
        }
        return !taken.Any(fp.Overlaps);
    }

    /// <summary>Every cell of a footprint on the level's core ground.</summary>
    private bool InCore(Region fp)
    {
        foreach (var c in fp.Cells())
        {
            var cell = new Rectangle { X = c.X, Z = c.Z, Width = 1, Height = 1 };
            if (!e.Level.Inside(cell) || e.Level.Sum(cell) != 1)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The output a footprint at b takes from the wind lanes already placed, in watts: the lane cells it newly blocks.</summary>
    private double Inflicted(Rectangle b)
    {
        var loss = 0.0;
        foreach (var l in lanes)
        {
            var add = 0;
            for (var z = b.Z; z < b.Z + b.Height; z++)
            {
                for (var x = b.X; x < b.X + b.Width; x++)
                {
                    if (l.Mask[(z * w) + x] && !ground.Cells.Contains(new Cell(x, z)))
                    {
                        add++;
                    }
                }
            }
            if (add > 0)
            {
                loss += (PolicyFunctions.WindOutput(l.Blocked) - PolicyFunctions.WindOutput(l.Blocked + add)) * price.WindW;
            }
        }
        return loss;
    }

    /// <summary>The cheapest placement of a free node over its turns, or null when no ground holds it. It changes nothing.</summary>
    public SiteQuote? Quote(SiteNode n)
    {
        var weights = price.WeightsOrDefault();
        SiteQuote? best = null;
        foreach (var shape in new[] { n }.Concat(n.Alts))
        {
            var box = shape.Footprint.Bounds();
            var rect = shape.Footprint.IsRect();
            foreach (var o in e.Core)
            {
                var b = new Rectangle { X = box.X + o.X, Z = box.Z + o.Z, Width = box.Width, Height = box.Height };
                if (!e.Level.Inside(b))
                {
                    continue;
                }
                if (rect)
                {
                    if (e.Level.Sum(b) != (long)b.Width * b.Height || !FreeRect(b))
                    {
                        continue;
                    }
                }
                else
                {
                    var moved = shape.Footprint.Translate(o.X, o.Z);
                    if (!InCore(moved) || !FreeRegion(moved))
                    {
                        continue;
                    }
                }
                var q = new SiteQuote(shape, o);
                var soil = (double)units.Sum(b) / SoilFixed;
                if (!rect)
                {
                    soil = 0;
                    foreach (var c in shape.Footprint.Translate(o.X, o.Z).Cells())
                    {
                        soil += (double)units.Sum(new Rectangle { X = c.X, Z = c.Z, Width = 1, Height = 1 }) / SoilFixed;
                    }
                }
                q.CostW = (ConduitCells(b) * price.ConduitW) + (WallCells(b) * price.WallW) + PolicyFunctions.UnitsW(weights, soil);
                if (best is not null && q.CostW >= best.PriceW())
                {
                    continue;
                }
                foreach (var l in shape.Lanes)
                {
                    if (l.Wind)
                    {
                        q.Blocked += ground.BlockedAt(l.Region, o.X, o.Z, b);
                    }
                }
                q.LaneW = ((PolicyFunctions.WindOutput(0) - PolicyFunctions.WindOutput(q.Blocked)) * price.WindW) + Inflicted(b);
                if (best is null || q.PriceW() < best.PriceW())
                {
                    best = q;
                }
            }
        }
        return best;
    }

    /// <summary>Puts a node on its anchor, or at the cheapest origin; a node no ground holds is left out with its decision.</summary>
    public void Place(SiteNode n)
    {
        if (n.Anchor is { } anchor)
        {
            var p = new SitePlacement { Node = n.Id, Replaces = n.Replaces, Rotation = n.Rotation, Origin = anchor, Anchored = true, Footprint = n.Footprint.Translate(anchor.X, anchor.Z) };
            var (verdict, reason) = (EmbedPlace, PolicyFunctions.ReasonSiteAnchored);
            if (!FreeRegion(p.Footprint))
            {
                (verdict, reason) = (EmbedUnsatisfied, PolicyFunctions.ReasonSiteAnchorBlocked);
            }
            Add(p, n.Lanes, verdict, reason);
            return;
        }
        var q = Quote(n);
        if (q is null)
        {
            outDecisions.Add(new SiteDecision { Node = n.Id, Verdict = EmbedUnplaceable, Reason = PolicyFunctions.ReasonSiteNoGround });
            return;
        }
        Take(n, q);
    }

    /// <summary>Places a free node as quoted.</summary>
    public void Take(SiteNode n, SiteQuote q)
    {
        var p = new SitePlacement { Node = n.Id, Replaces = n.Replaces, Origin = q.Origin, Rotation = q.Shape.Rotation, Footprint = q.Shape.Footprint.Translate(q.Origin.X, q.Origin.Z) };
        Add(p, q.Shape.Lanes, EmbedPlace, PolicyFunctions.ReasonSiteFree);
    }

    private void Add(SitePlacement p, IReadOnlyList<SiteLane> siteLanes, string verdict, string reason)
    {
        var placedLanes = siteLanes.Select(l => new SiteLane { Id = l.Id, Region = l.Region.Translate(p.Origin.X, p.Origin.Z), Reason = l.Reason, Wind = l.Wind }).ToList();
        p = p with { Lanes = placedLanes };
        var b = p.Footprint.Bounds();
        taken.Add(p.Footprint);
        foot.Add(b);
        ground.Add(p.Footprint);
        Cover(b);
        RebuildWall();
        foreach (var l in p.Lanes)
        {
            if (!l.Wind)
            {
                continue;
            }
            var mask = new bool[Math.Max(w * h, 0)];
            foreach (var c in l.Region.Cells())
            {
                if (c.X >= 0 && c.Z >= 0 && c.X < w && c.Z < h)
                {
                    mask[(c.Z * w) + c.X] = true;
                }
            }
            lanes.Add(new PlacedLane { Region = l.Region, Own = p.Footprint, Mask = mask });
        }
        foreach (var l in lanes)
        {
            l.Blocked = ground.Blocked(l.Region, l.Own);
        }
        outSites.Add(p);
        outDecisions.Add(new SiteDecision { Node = p.Node, Verdict = verdict, Reason = reason });
    }

    /// <summary>The placement so far, each wind lane priced against every footprint placed after it too.</summary>
    public SiteEmbedding Result()
    {
        var decisions = new List<SiteDecision>(outDecisions);
        var sites = new List<SitePlacement>();
        foreach (var p in outSites)
        {
            var lanesOut = new List<SiteLane>();
            foreach (var l in p.Lanes)
            {
                if (!l.Wind)
                {
                    lanesOut.Add(l);
                    continue;
                }
                var blocked = ground.Blocked(l.Region, p.Footprint);
                lanesOut.Add(l with { Blocked = blocked });
                if (blocked > 0)
                {
                    decisions.Add(new SiteDecision { Node = p.Node, Verdict = EmbedUnsatisfied, Reason = PolicyFunctions.ReasonSiteWindBlocked });
                }
            }
            sites.Add(p with { Lanes = lanesOut });
        }
        return new SiteEmbedding { Sites = sites, Decisions = decisions };
    }
}

/// <summary>Ports <c>layout_site_run.go</c>: the site embedder entry point.</summary>
public static partial class PolicyFunctions
{
    /// <summary>The least fertility a field cell needs (plain soil), shared with the site run.</summary>
    internal const double ZoneFieldFertilityValue = ZoneFieldFertility;

    /// <summary>Places the site nodes around the rooms. Anchored nodes stand first, on their anchors; the rest take, in turn, the origin of least price, so the result depends only on the inputs.</summary>
    public static SiteEmbedding EmbedSites(SiteEmbedInput input)
    {
        var r = new SiteRun(input);
        foreach (var n in input.Nodes)
        {
            r.Admit(n);
        }
        foreach (var n in input.Nodes.OrderByDescending(n => n.Anchor is not null))
        {
            r.Place(n);
        }
        return r.Result();
    }
}
