using System.Text.RegularExpressions;

namespace RimGovernor.Brain.Policy;

/// <summary>What the defense record holds of the plan's perimeter: the ids of the site nodes its tiers name and its killbox.</summary>
public sealed record PerimeterHold
{
    public IReadOnlySet<string> Held { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

public sealed partial record LayoutPlan
{
    /// <summary>The live ring's revision (0 without one).</summary>
    internal int RingRevision() => Ring() is { Ok: true } r ? r.Site.Ring!.Revision : 0;

    /// <summary>Names the ring and its family at revision rev.</summary>
    internal LayoutPlan WithRevision(int rev) =>
        this with
        {
            Sites = [.. Sites.Select(s =>
            {
                if (!LayoutPerimeterReplaceRules.PerimeterFamily(s.Kind))
                {
                    return s;
                }
                var revised = s with { Node = LayoutPerimeterReplaceRules.RevisedId(s.Node, rev) };
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
        return p with { Sites = [.. p.Sites, .. from.Sites.Where(s => LayoutPerimeterSitesRules.PerimeterSiteKinds.Contains(s.Kind))] };
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
            else if (s.Kind == LayoutPerimeterSitesRules.NodeKillbox && !ReplacedSite(s.Node))
            {
                sites.Add(s);
            }
        }
        return this with { Sites = sites };
    }
}

/// <summary>The ids of the ring's family carry its revision (a plan stored before task 7.1 may hold a revised generation).</summary>
public static partial class LayoutPerimeterReplaceRules
{
    /// <summary>The node kinds whose ids carry the ring's revision: the ring and what the defense record cuts from it.</summary>
    internal static bool PerimeterFamily(NodeKind k) => k == LayoutPerimeterSitesRules.NodeRing || k == LayoutPerimeterSitesRules.NodeGate || k == LayoutPerimeterSitesRules.NodeFence || k == LayoutPerimeterSitesRules.NodePump;

    private static readonly Regex RevisionSuffix = new(@"-r[0-9]+$", RegexOptions.CultureInvariant);

    /// <summary>The id at revision rev: the base id, suffixed from the first replacement on.</summary>
    internal static string RevisedId(string id, int rev)
    {
        id = RevisionSuffix.Replace(id, "");
        return rev == 0 ? id : $"{id}-r{rev}";
    }
}

partial record LayoutPlan
{
    /// <summary>
    /// The perimeter step of ReplanLayout, retain only (task 7.1): no defense is traced here. While the defense record
    /// holds any of prev's defense nodes the replan keeps prev's perimeter family as it stands (the retired nodes the
    /// record no longer names dropped); with nothing held it carries no perimeter, killbox or pockets at all. A new
    /// defense is a candidate of DefenseCandidateRules, not a plan prescription.
    /// </summary>
    public static LayoutPlan RetainPerimeter(LayoutPlan prev, LayoutPlan next, PerimeterHold hold) =>
        hold.Held.Count == 0
            ? next.WithoutPerimeterSites()
            : next.WithPerimeterSitesOf(prev.WithoutUnheldRetired(hold.Held));
}
