using System.Text.RegularExpressions;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// What the defense record holds of the perimeter. Held are the ids of the site nodes a defense tier names: built,
/// being built, or waiting to be demolished. Removing is set while a removal tier of the perimeter stands in the
/// record: a rebuild is in flight.
/// </summary>
public sealed record PerimeterHold
{
    public IReadOnlySet<string> Held { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public bool Removing { get; init; }

    /// <summary>A retired node the record still names.</summary>
    internal bool RetiredHeld(LayoutPlan p) => p.Sites.Any(s => p.ReplacedSiteNode(s.Node) && Held.Contains(s.Node));

    /// <summary>A rebuild in flight: a retired node is still held, or the record has a removal tier standing.</summary>
    internal bool Rebuilding(LayoutPlan p) => Removing || RetiredHeld(p);
}

public sealed partial record LayoutPlan
{
    /// <summary>Whether another node replaces the node with the id.</summary>
    internal bool ReplacedSiteNode(string id) => ReplacedSite(id);

    /// <summary>The live ring's revision (0 without one).</summary>
    internal int RingRevision() => Ring() is { Ok: true } r ? r.Site.Ring!.Revision : 0;

    /// <summary>Names the ring and its family at revision rev.</summary>
    internal LayoutPlan WithRevision(int rev) =>
        this with
        {
            Sites = [.. Sites.Select(s =>
            {
                if (!PolicyFunctions.PerimeterFamily(s.Kind))
                {
                    return s;
                }
                var revised = s with { Node = PolicyFunctions.RevisedId(s.Node, rev) };
                return s.Ring is null ? revised : revised with { Ring = s.Ring with { Revision = rev } };
            })],
        };

    /// <summary>Drops the retired nodes the record does not name.</summary>
    internal LayoutPlan WithoutUnheldRetired(IReadOnlySet<string> held)
    {
        var gone = new HashSet<string>(Sites.Where(s => ReplacedSite(s.Node) && !held.Contains(s.Node)).Select(s => s.Node), StringComparer.Ordinal);
        if (gone.Count == 0)
        {
            return this;
        }
        var sites = new List<PlannedSite>();
        foreach (var s in Sites)
        {
            if (gone.Contains(s.Node))
            {
                continue;
            }
            sites.Add(s with { ReplacesSites = [.. s.ReplacesSites.Where(id => !gone.Contains(id))] });
        }
        return this with { Sites = sites };
    }

    /// <summary>The plan with the perimeter's nodes as from holds them, retired ones included.</summary>
    internal LayoutPlan WithPerimeterSitesOf(LayoutPlan from)
    {
        var p = WithoutPerimeterSites();
        return p with { Sites = [.. p.Sites, .. from.Sites.Where(s => PolicyFunctions.PerimeterSiteKinds.Contains(s.Kind))] };
    }

    /// <summary>
    /// The plan as the named nodes alone make it: they are live whatever replaced them, and the live killbox stays for
    /// the sections to order by. The wall sections of a retired generation are cut from it.
    /// </summary>
    public LayoutPlan SiteView(IReadOnlyList<string> ids)
    {
        var sites = new List<PlannedSite>();
        foreach (var s in Sites)
        {
            if (ids.Contains(s.Node))
            {
                sites.Add(s with { ReplacesSites = [] });
            }
            else if (s.Kind == PolicyFunctions.NodeKillbox && !ReplacedSite(s.Node))
            {
                sites.Add(s);
            }
        }
        return this with { Sites = sites };
    }

    /// <summary>Turns a ring traced over the held one into its replacement.</summary>
    internal LayoutPlan Replacing(LayoutPlan prev, PerimeterHold hold)
    {
        var (old, hasOld) = prev.Ring();
        if (!hasOld || !hold.Held.Contains(old.Node) || PolicyFunctions.SameFamily(prev, this))
        {
            return this;
        }
        var p = WithRevision(old.Ring!.Revision + 1);
        var retired = new List<PlannedSite>();
        var ids = new List<string>();
        foreach (var s in prev.Sites)
        {
            if (PolicyFunctions.PerimeterFamily(s.Kind) && !prev.ReplacedSite(s.Node) && hold.Held.Contains(s.Node))
            {
                retired.Add(s);
                ids.Add(s.Node);
            }
        }
        var sites = new List<PlannedSite>(p.Sites);
        for (var i = 0; i < sites.Count; i++)
        {
            if (sites[i].Kind == PolicyFunctions.NodeRing && sites[i].Ring is not null)
            {
                sites[i] = sites[i] with { ReplacesSites = ids, Reason = PolicyFunctions.ReasonRingReplaced };
                break;
            }
        }
        sites.AddRange(retired);
        return p with { Sites = sites };
    }
}

/// <summary>
/// Ports <c>layout_perimeter_replace.go</c>. A ring traced again while the defense record still holds the old one is
/// not edited in place: the planner stores the new ring, its gates, fence and pumps under revision-suffixed ids beside
/// the old ones, which stay in the plan as retired nodes (replaced by the new ring: off the live ground, still on the
/// reserved ground). The runtime builds the new generation while the old one defends, opens the old wall only once the
/// new ring stands, and demolishes the old through removal tiers that name the retired nodes; the retired nodes leave
/// the plan once the record no longer names them. One rebuild runs at a time: while the record holds a retired node or
/// a removal tier the ring is not traced again.
/// </summary>
public static partial class PolicyFunctions
{
    /// <summary>The node kinds whose ids carry the ring's revision: the ring and what the defense record cuts from it.</summary>
    internal static bool PerimeterFamily(NodeKind k) => k == NodeRing || k == NodeGate || k == NodeFence || k == NodePump;

    private static readonly Regex RevisionSuffix = new(@"-r[0-9]+$", RegexOptions.CultureInvariant);

    /// <summary>The id at revision rev: the base id, suffixed from the first replacement on.</summary>
    internal static string RevisedId(string id, int rev)
    {
        id = RevisionSuffix.Replace(id, "");
        return rev == 0 ? id : $"{id}-r{rev}";
    }

    /// <summary>Reports retired nodes in the plan that the record no longer names: a replan drops them.</summary>
    public static bool PerimeterRetirable(LayoutPlan p, IReadOnlySet<string> held) => p.Sites.Any(s => p.ReplacedSiteNode(s.Node) && !held.Contains(s.Node));

    /// <summary>Whether a and b hold the same ring, gates, fence and pumps: a ring traced again over the same ground replaces nothing. The ground inside the ring is no part of it: rooms added inside change that.</summary>
    internal static bool SameFamily(LayoutPlan a, LayoutPlan b)
    {
        List<PlannedSite> Pick(LayoutPlan p) =>
        [
            .. p.Sites.Where(s => PerimeterFamily(s.Kind) && !p.ReplacedSiteNode(s.Node))
                .Select(s => s.Ring is null ? s : s with { Ring = new RingFacts { Light = s.Ring.Light, Bridged = s.Ring.Bridged, Gaps = s.Ring.Gaps, Revision = s.Ring.Revision } }),
        ];
        var x = Pick(a);
        var y = Pick(b);
        return x.Count == y.Count && x.Zip(y).All(t => t.First.Node == t.Second.Node && t.First.Kind == t.Second.Kind && t.First.Footprint.Equal(t.Second.Footprint) && SameRing(t.First.Ring, t.Second.Ring));
    }

    /// <summary>
    /// The perimeter step of ReplanLayout: lays the perimeter around next, the plan a replan grew from prev. While the
    /// record holds a retired node or a removal tier the ring is not traced again (one rebuild at a time). A ring that
    /// comes out other than prev's, with prev's held by the record, is laid as a replacement beside the old one.
    /// </summary>
    public static LayoutPlan LayPerimeter(LayoutPlan prev, LayoutPlan next, MapSurvey s, PerimeterHold hold)
    {
        prev = prev.WithoutUnheldRetired(hold.Held);
        next = next.WithoutUnheldRetired(hold.Held);
        if (hold.Rebuilding(prev))
        {
            return next.WithPerimeterSitesOf(prev);
        }
        var laid = WithoutCore(PlanMountainPockets(PlanPerimeter(next, s), s));
        return laid.Replacing(prev, hold);
    }
}
