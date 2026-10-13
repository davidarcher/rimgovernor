namespace RimGovernor.Brain.Policy;

/// <summary>The ring's own attributes.</summary>
public sealed record RingFacts
{
    /// <summary>The wall cells on light footing (the wall there is wood). Lies within the ring's footprint.</summary>
    public Region Light { get; init; } = new();

    /// <summary>The wall cells on water (a bridge goes down first). Lies within the ring's footprint.</summary>
    public Region Bridged { get; init; } = new();

    /// <summary>The walkable ring cells nothing can close.</summary>
    public Region Gaps { get; init; } = new();

    /// <summary>The ground strictly inside the ring (the base region).</summary>
    public Region Inner { get; init; } = new();

    /// <summary>The inner ground with the ring itself, the opening and the terrain stretches included. Consumers read the colony's enclosed ground here.</summary>
    public Region Sealed { get; init; } = new();

    /// <summary>Why the killbox opening is where it is (the Reason*Opening values), including why there is none.</summary>
    public string Opening { get; init; } = "";

    /// <summary>Counts the times the ring was replaced while the defense record held the old one; it suffixes the ids of the ring, its gates, the fence and the pumps (revisedID).</summary>
    public int Revision { get; init; }
}

/// <summary>One stored site node: a SitePlacement plus its kind, the nodes it replaces and the reason it stands.</summary>
public sealed record PlannedSite
{
    public string Node { get; init; } = "";
    public Cell Origin { get; init; }
    public Region Footprint { get; init; } = new();
    public IReadOnlyList<SiteLane> Lanes { get; init; } = [];
    public Rotation? Rotation { get; init; }
    public bool Anchored { get; init; }
    public int Replaces { get; init; }
    public NodeKind Kind { get; init; }

    /// <summary>The ids of the site nodes this one supersedes; empty for none. A replacement ring names the whole generation it replaces.</summary>
    public IReadOnlyList<string> ReplacesSites { get; init; } = [];

    public string Reason { get; init; } = "";

    /// <summary>Set on the ring node only.</summary>
    public RingFacts? Ring { get; init; }

    /// <summary>Set on a defense tier node only (PlanDefenseTiers).</summary>
    public TierFacts? Tier { get; init; }

    /// <summary>The embedded placement (Go's embedded <c>SitePlacement</c>).</summary>
    public SitePlacement Placement => new() { Node = Node, Origin = Origin, Footprint = Footprint, Lanes = Lanes, Rotation = Rotation, Anchored = Anchored, Replaces = Replaces };

    /// <summary>A site node holding the placement (Go <c>PlannedSite{SitePlacement: p, ...}</c>).</summary>
    public static PlannedSite Of(SitePlacement p, NodeKind kind) => new()
    {
        Node = p.Node,
        Origin = p.Origin,
        Footprint = p.Footprint,
        Lanes = p.Lanes,
        Rotation = p.Rotation,
        Anchored = p.Anchored,
        Replaces = p.Replaces,
        Kind = kind,
    };
}

public sealed partial record LayoutPlan
{
    /// <summary>The plan's nodes of kind that no other node replaces, in plan order.</summary>
    public List<PlannedSite> LiveSites(NodeKind kind) => [.. Sites.Where(s => s.Kind == kind && !ReplacedSite(s.Node))];

    private bool ReplacedSite(string id) => Sites.Any(o => o.ReplacesSites.Contains(id));

    /// <summary>The node with the id, retired or live; null when absent.</summary>
    public PlannedSite? Site(string id) => Sites.FirstOrDefault(s => s.Node == id);

    /// <summary>The live ring node; Ok is false when absent.</summary>
    public (PlannedSite Site, bool Ok) Ring()
    {
        var rings = LiveSites(NodeKind.Ring);
        return rings.Count > 0 && rings[0].Ring is not null ? (rings[0], true) : (new PlannedSite(), false);
    }

    /// <summary>The live killbox opening node; Ok is false when absent.</summary>
    public (PlannedSite Site, bool Ok) Killbox()
    {
        var k = LiveSites(NodeKind.Killbox);
        return k.Count > 0 ? (k[0], true) : (new PlannedSite(), false);
    }

    /// <summary>The ground the plan holds: its reservations and the footprint of every site node, for the placers that keep clear of whatever is claimed.</summary>
    public List<LayoutReservation> Reserved()
    {
        if (Sites.Count == 0)
        {
            return [.. Reservations];
        }
        var output = new List<LayoutReservation>(Reservations);
        foreach (var s in Sites)
        {
            output.Add(new LayoutReservation { Kind = ReservationKindOf(s.Kind), Area = s.Footprint });
            if (s.Ring is not null && !s.Ring.Gaps.Empty())
            {
                output.Add(new LayoutReservation { Kind = ReservationKindOf(s.Kind), Area = s.Ring.Gaps });
            }
        }
        return output;
    }

    /// <summary>The cells the ring holds: its wall, the gaps nothing can close and the gates.</summary>
    public HashSet<Cell> WallCells()
    {
        var output = new HashSet<Cell>();
        foreach (var s in LiveSites(PolicyFunctions.NodeRing))
        {
            output.UnionWith(s.Footprint.Cells());
            if (s.Ring is not null)
            {
                output.UnionWith(s.Ring.Gaps.Cells());
            }
        }
        foreach (var s in LiveSites(PolicyFunctions.NodeGate))
        {
            output.UnionWith(s.Footprint.Cells());
        }
        return output;
    }

    /// <summary>Drops the nodes that stand or fall with the ring.</summary>
    internal LayoutPlan WithoutPerimeterSites() => this with { Sites = [.. Sites.Where(s => !PolicyFunctions.PerimeterSiteKinds.Contains(s.Kind))] };

    /// <summary>Drops the nodes of the given kinds.</summary>
    internal LayoutPlan WithoutSites(params NodeKind[] kinds) => this with { Sites = [.. Sites.Where(s => !kinds.Contains(s.Kind))] };
}

/// <summary>Ports <c>layout_perimeter_sites.go</c> and <c>layout_perimeter_tiers.go</c>: the defense ground as site nodes.</summary>
public static partial class PolicyFunctions
{
    public static NodeKind NodeRing => NodeKind.Ring;
    public static NodeKind NodeGate => NodeKind.Gate;
    public static NodeKind NodeKillbox => NodeKind.Killbox;
    public static NodeKind NodeApproach => NodeKind.Approach;
    public static NodeKind NodeFence => NodeKind.Fence;
    public static NodeKind NodePump => NodeKind.Pump;
    public static NodeKind NodeMortar => NodeKind.Mortar;
    public static NodeKind NodeCoverClear => NodeKind.CoverClear;

    /// <summary>The dark pockets beside the base are produced after the ring node on the same model and walled solid. They stand and fall with the ring, and their sections keep the perimeter tier's names.</summary>
    public static NodeKind NodePocketWall => NodeKind.PocketWall;

    /// <summary>The ring was traced.</summary>
    public const string ReasonRingTraced = "ring_traced";

    /// <summary>The dry ring position most raider routes cross. A replan keeps the opening while it still opens, and with it this reason.</summary>
    public const string ReasonOpeningRoutes = "opening_most_routes";

    /// <summary>No route reaches a dry ring position that opens.</summary>
    public const string ReasonOpeningNone = "opening_none";

    /// <summary>The ring was traced again while the defense record held the old one, so a replacement stands beside it and the old ring is demolished once the new one is closed.</summary>
    public const string ReasonRingReplaced = "ring_replaced";

    /// <summary>The nodes that stand or fall with the ring: PlanPerimeter owns them (and drops the lot when it traces again), the pocket walls are produced after it.</summary>
    internal static HashSet<NodeKind> PerimeterSiteKinds => [NodeRing, NodeGate, NodeKillbox, NodeApproach, NodeFence, NodePump, NodeMortar, NodeCoverClear, NodePocketWall];

    internal static PlannedSite NewSite(string id, NodeKind kind, Region fp, string reason)
    {
        var b = fp.Bounds();
        return new PlannedSite { Node = id, Origin = new Cell(b.X, b.Z), Footprint = fp, Kind = kind, Reason = reason };
    }

    /// <summary>The ring's and the opening's decisions, for the caller to journal: the ring is traced, and the opening is kept, chosen by the routes, or absent; then each pocket wall placed.</summary>
    public static List<SiteDecision> PerimeterDecisions(LayoutPlan p)
    {
        var (ring, hasRing) = p.Ring();
        if (!hasRing)
        {
            return [];
        }
        var output = new List<SiteDecision> { new() { Node = ring.Node, Verdict = EmbedPlace, Reason = ring.Reason } };
        var (k, hasKillbox) = p.Killbox();
        if (hasKillbox)
        {
            output.Add(new SiteDecision { Node = k.Node, Verdict = EmbedPlace, Reason = k.Reason });
        }
        else
        {
            output.Add(new SiteDecision { Node = Wire.Name(NodeKillbox), Verdict = EmbedUnplaceable, Reason = ring.Ring!.Opening });
        }
        foreach (var s in p.LiveSites(NodePocketWall))
        {
            output.Add(new SiteDecision { Node = s.Node, Verdict = EmbedPlace, Reason = s.Reason });
        }
        return output;
    }

    /// <summary>Whether next traces other defense ground than prev.</summary>
    public static bool PerimeterChanged(LayoutPlan prev, LayoutPlan next) => !SameSites(prev, next);

    /// <summary>Whether a and b hold the same perimeter nodes: the same ground, whatever the reasons they stand (a kept opening reads as chosen).</summary>
    internal static bool SameSites(LayoutPlan a, LayoutPlan b) => SameSitesOf(a, b, PerimeterSiteKinds);

    /// <summary>SameSites over the nodes of the given kinds.</summary>
    internal static bool SameSitesOf(LayoutPlan a, LayoutPlan b, HashSet<NodeKind> kinds)
    {
        List<PlannedSite> Pick(LayoutPlan p) => [.. p.Sites.Where(s => kinds.Contains(s.Kind))];
        var x = Pick(a);
        var y = Pick(b);
        return x.Count == y.Count && x.Zip(y).All(t =>
            t.First.Node == t.Second.Node
            && t.First.Kind == t.Second.Kind
            && t.First.Footprint.Equal(t.Second.Footprint)
            && t.First.Anchored == t.Second.Anchored
            && t.First.ReplacesSites.SequenceEqual(t.Second.ReplacesSites)
            && SameRing(t.First.Ring, t.Second.Ring));
    }

    private static bool SameRing(RingFacts? a, RingFacts? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }
        return a.Revision == b.Revision && a.Light.Equal(b.Light) && a.Bridged.Equal(b.Bridged) && a.Gaps.Equal(b.Gaps) && a.Inner.Equal(b.Inner) && a.Sealed.Equal(b.Sealed);
    }

    /// <summary>The region as the rectangles the wall sections cut: the same decomposition CellRects makes of its cells.</summary>
    internal static List<Rectangle> RegionRects(Region g) => CellRects([.. g.Cells()]);

    private static readonly System.Text.RegularExpressions.Regex CorePerimeterTier = new("^" + System.Text.RegularExpressions.Regex.Escape(TierPerimeterPrefix) + "(r[0-9]+-)?[0-9]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Whether a tier is one of the perimeter wall's own sections (perimeter-NN, or perimeter-rN-NN after a re-cut), not a pump, light, fence or removal tier.</summary>
    public static bool IsCorePerimeterTier(DefenseTierName name) => CorePerimeterTier.IsMatch(name.Value);
}
