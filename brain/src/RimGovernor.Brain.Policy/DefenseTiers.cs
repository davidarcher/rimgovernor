namespace RimGovernor.Brain.Policy;

// The defense tiers as plan nodes (#2973). PlanDefenseTiers cuts the killbox node into the tiers that stand in
// it: the corridor and its trap lane, the funnel walls, the fence bar, the firing line and the shooters' floor,
// the spike traps, the turrets, the mortars, the IEDs and the bait. Each is a PlannedSite whose Tier facts name
// the node it stands under (the killbox), the tier of the defense record it belongs to and the placements it
// holds.
//
// Where a tier stands depends on live cover, trees and standing buildings, so the nodes are a function of the
// plan and a census, not of the survey: they are re-sited from the defense round's census and stored in the
// layout plan (#3583), where combat and the defense planner read the firing line and the trap lane from the
// DefFiring and DefLane nodes. A replan keeps them while the killbox they stand under is the same node over the
// same ground. A tier that is started (built, an attempt admitted, or any of its node placements standing) is
// pinned, as the replan holds a room that holds anything of ours: its pin is its nodes (PlannedDefenseTiers), the
// whole tier where it stands, and the tiers still to build are sited around it. The census is an explicit input.

/// <summary>One building a tier node places.</summary>
public sealed record TierPlacement(string Definition, Cell Cell, Rotation Rotation, string Stuff)
{
    /// <summary>The placement as the domain building.</summary>
    public Building ToBuilding() => new(Definition, Cell, Rotation, Stuff);
}

/// <summary>A defense tier node's own attributes.</summary>
public sealed record TierFacts
{
    /// <summary>The killbox node the tier stands under.</summary>
    public string Under { get; init; } = "";

    /// <summary>The defense tier it is cut from (a tier of several nodes: the firing line and its floor, the trap corridor's traps and fence).</summary>
    public DefenseTierName Tier { get; init; }

    /// <summary>The buildings the node places, in the layout's order.</summary>
    public EquatableArray<TierPlacement> Places { get; init; }

    /// <summary>The cells the tier holds against routine construction; the tier's first node carries them.</summary>
    public EquatableArray<Cell> Reserved { get; init; }

    /// <summary>The trap lane in raider order, the exit cell last (the lane node only).</summary>
    public EquatableArray<Cell> Path { get; init; }

    /// <summary>The shooters with their cover and retreat cells (the firing line node only).</summary>
    public EquatableArray<FiringPosition> Firing { get; init; }

    /// <summary>Marks a node kept where it stands because the tier is built.</summary>
    public bool Pinned { get; init; }
}

/// <summary>The tiers of the defense record that are started: kept where they stand while the tiers still to build are sited around them.</summary>
public sealed record DefenseTierPins
{
    /// <summary>The built tiers' placements and reserved cells.</summary>
    public IReadOnlyDictionary<DefenseTierName, DefenseTier> Tiers { get; init; } = new Dictionary<DefenseTierName, DefenseTier>();

    /// <summary>The trap lane of the plan's DefLane node, kept while the chokepoint tier is pinned.</summary>
    public EquatableArray<Cell> TrapLane { get; init; }

    /// <summary>The firing line of the plan's DefFiring node, kept while the firing line tier is pinned.</summary>
    public EquatableArray<FiringPosition> Firing { get; init; }
}

/// <summary>The firing line and trap lane the plan's DefFiring and DefLane nodes hold.</summary>
public sealed record TierGeometry
{
    /// <summary>The shooters with their cover and retreat cells; empty without a DefFiring node.</summary>
    public EquatableArray<FiringPosition> Firing { get; init; }

    /// <summary>The trap lane in raider order, the exit cell last; empty without a DefLane node.</summary>
    public EquatableArray<Cell> TrapLane { get; init; }
}

/// <summary>What PlanDefenseTiers cut.</summary>
public sealed record DefenseTiers
{
    public EquatableArray<PlannedSite> Nodes { get; init; }

    /// <summary>The tier geometry the nodes were cut from: its firing line, trap lane, approaches and the gates that held tiers short.</summary>
    public DefenseLayout Layout { get; init; } = new();

    /// <summary>Why the layout planner sited nothing, when it refused.</summary>
    public string Refusal { get; init; } = "";

    public EquatableArray<SiteDecision> Decisions { get; init; }

    /// <summary>The tiers the census sites with nothing pinned: the places a blocked tier could move to (PriceTierBlocks).</summary>
    public EquatableArray<DefenseTier> Sited { get; init; }
}

public static class DefenseTiersPolicy
{
    /// <summary>The passage raiders walk from the opening to the kill row.</summary>
    public static NodeKind NodeDefCorridor => NodeKind.DefCorridor;

    /// <summary>The ordered snake path inside the corridor.</summary>
    public static NodeKind NodeDefLane => NodeKind.DefLane;

    public static NodeKind NodeDefFunnel => NodeKind.DefFunnel;

    /// <summary>The fence bar across the kill zone.</summary>
    public static NodeKind NodeDefFence => NodeKind.DefFence;

    /// <summary>The spike traps on the lane's turns.</summary>
    public static NodeKind NodeDefTraps => NodeKind.DefTraps;

    /// <summary>The cover of the firing line.</summary>
    public static NodeKind NodeDefFiring => NodeKind.DefFiring;

    /// <summary>The floor under each shooter.</summary>
    public static NodeKind NodeDefShooter => NodeKind.DefShooter;

    public static NodeKind NodeDefTurrets => NodeKind.DefTurrets;
    public static NodeKind NodeDefMortars => NodeKind.DefMortars;
    public static NodeKind NodeDefIEDs => NodeKind.DefIEDs;
    public static NodeKind NodeDefBait => NodeKind.DefBait;

    // Why a tier node stands where it does; the caller journals them as layout_plan rows (target defense_tiers).

    /// <summary>The tier is sited from the census.</summary>
    public const string ReasonTierSited = "tier_sited";

    /// <summary>The tier is built, so it stays where it stands.</summary>
    public const string ReasonTierPinned = "tier_pinned";

    /// <summary>The census holds no corridor (the layout planner's refusal is the attr); only the built tiers stand as nodes.</summary>
    public const string ReasonTierLayoutFailed = "tier_layout_failed";

    /// <summary>A tier still to build was sited on a cell a built tier holds, and gave it up.</summary>
    public const string ReasonTierDisplaced = "tier_displaced_by_built";

    /// <summary>The nodes PlanDefenseTiers produces.</summary>
    internal static readonly IReadOnlySet<NodeKind> DefenseTierKinds = new HashSet<NodeKind>
    {
        NodeDefCorridor, NodeDefLane, NodeDefFunnel, NodeDefFence, NodeDefTraps,
        NodeDefFiring, NodeDefShooter, NodeDefTurrets, NodeDefMortars, NodeDefIEDs, NodeDefBait,
    };

    /// <summary>Reports a node PlanDefenseTiers produced.</summary>
    public static bool IsDefenseTierNode(NodeKind kind) => DefenseTierKinds.Contains(kind);

    /// <summary>The order tiers become nodes in, and so the order of the nodes in the plan.</summary>
    internal static readonly IReadOnlyList<DefenseTierName> DefenseTierOrder =
        [DefenseLayout.TierChokepoint, DefenseLayout.TierFunnel, DefenseLayout.TierTrapCorridor, DefenseLayout.TierFiringLine, DefenseTurretsPolicy.TierTurrets, DefenseMortarsPolicy.TierMortars, DefenseIEDs.TierIEDs, DefenseArrivalRoutes.TierBait];

    /// <summary>The plan with its defense tier nodes replaced by the given ones.</summary>
    public static LayoutPlan WithDefenseTiers(this LayoutPlan p, IEnumerable<PlannedSite> nodes)
    {
        var stripped = p.WithoutSites([.. DefenseTierKinds]);
        return stripped with { Sites = EquatableArray.From(stripped.Sites.Concat(nodes)) };
    }

    /// <summary>
    /// The replanned plan next with the defense tier nodes of prev, the plan it grew from, while next's killbox is
    /// prev's node over the same ground; a killbox cut again drops them, and the defense round's next siting cuts them
    /// anew.
    /// </summary>
    public static LayoutPlan WithDefenseTiersOf(this LayoutPlan next, LayoutPlan prev)
    {
        var (id, digest, ok) = LayoutPlan.KillboxNode(next);
        var (prevId, prevDigest, prevOk) = LayoutPlan.KillboxNode(prev);
        var keep = ok && prevOk && id == prevId && digest == prevDigest;
        return next.WithDefenseTiers(keep ? prev.Sites.Where(s => IsDefenseTierNode(s.Kind)) : []);
    }

    /// <summary>The firing line and trap lane the plan's DefFiring and DefLane nodes hold.</summary>
    public static TierGeometry DefenseTierGeometry(this LayoutPlan p)
    {
        var g = new TierGeometry();
        foreach (var s in p.Sites)
        {
            if (s.Tier is not { } facts)
            {
                continue;
            }
            if (s.Kind == NodeDefFiring)
            {
                g = g with { Firing = facts.Firing };
            }
            else if (s.Kind == NodeDefLane)
            {
                g = g with { TrapLane = facts.Path };
            }
        }
        return g;
    }

    /// <summary>
    /// The defense tiers the plan's tier nodes hold, by tier: the placements of all its nodes, in plan order, and the
    /// cells they reserve. A started tier's pin and the reserved cells cover clearing and the turret replacement keep
    /// clear are read here (#3584).
    /// </summary>
    public static IReadOnlyDictionary<DefenseTierName, DefenseTier> PlannedDefenseTiers(this LayoutPlan p)
    {
        var buildings = new Dictionary<DefenseTierName, List<Building>>();
        var reserved = new Dictionary<DefenseTierName, List<Cell>>();
        foreach (var s in p.Sites)
        {
            if (s.Tier is not { } facts || !IsDefenseTierNode(s.Kind))
            {
                continue;
            }
            if (!buildings.TryGetValue(facts.Tier, out var places))
            {
                places = [];
                buildings[facts.Tier] = places;
                reserved[facts.Tier] = [];
            }
            places.AddRange(facts.Places.Select(x => x.ToBuilding()));
            reserved[facts.Tier].AddRange(facts.Reserved);
        }
        return buildings.ToDictionary(kv => kv.Key, kv => new DefenseTier
        {
            Name = kv.Key,
            Buildings = EquatableArray.From(kv.Value),
            Reserved = EquatableArray.From(reserved[kv.Key]),
        });
    }

    /// <summary>
    /// The plan with the proposed layout's tiers as its defense tier nodes under its killbox, nothing pinned: the
    /// proposal records it, so every tier the record starts has its nodes (#3584). Throws when the plan has no killbox.
    /// </summary>
    public static LayoutPlan WithProposedTiers(this LayoutPlan p, DefenseDefinitions defs, DefenseLayout layout)
    {
        var (under, _, ok) = LayoutPlan.KillboxNode(p);
        DomainException.Require(ok, "plan has no killbox");
        return p.WithDefenseTiers(TierNodes(under, defs, layout, []));
    }

    /// <summary>
    /// The plan with the turret node's placement of old replaced in place and its reservation set to reserved, the
    /// turrets' footprints by definition size; unchanged without a turret node.
    /// </summary>
    public static LayoutPlan WithTurretReplaced(this LayoutPlan p, Building old, Building replacement, IReadOnlyList<Cell> reserved)
    {
        var sites = p.Sites.Select(s => s.Kind != NodeDefTurrets || s.Tier is not { } facts
            ? s
            : s with
            {
                Tier = facts with
                {
                    Places = EquatableArray.From(facts.Places.Select(x => x.Cell == old.Cell && x.Definition == old.Definition
                        ? new TierPlacement(replacement.Definition, replacement.Cell, replacement.Rotation, replacement.Stuff)
                        : x)),
                    Reserved = EquatableArray.From(reserved),
                },
            });
        return p with { Sites = EquatableArray.From(sites) };
    }

    /// <summary>
    /// The plan with a firing line and a trap lane as its only defense tier nodes, under its killbox: the geometry a
    /// combat fixture stages for combat to read. With no cover building to place, the firing node stands on the
    /// shooters' cells. Throws when the plan has no killbox.
    /// </summary>
    public static LayoutPlan WithDefenseTierGeometry(this LayoutPlan p, IReadOnlyList<FiringPosition> firing, IReadOnlyList<Cell> trapLane)
    {
        var (under, _, ok) = LayoutPlan.KillboxNode(p);
        DomainException.Require(ok, "plan has no killbox");
        var nodes = new List<PlannedSite>();
        if (trapLane.Count > 0)
        {
            nodes.Add(TierNode(under, NodeDefLane, new TierFacts { Tier = DefenseLayout.TierChokepoint, Path = EquatableArray.From(trapLane) }, trapLane));
        }
        if (firing.Count > 0)
        {
            nodes.Add(TierNode(under, NodeDefFiring, new TierFacts { Tier = DefenseLayout.TierFiringLine, Firing = EquatableArray.From(firing) }, [.. firing.Select(f => f.Cell)]));
        }
        return p.WithDefenseTiers(nodes);
    }

    /// <summary>The tier node of kind under the killbox node id, standing on cells.</summary>
    private static PlannedSite TierNode(string under, NodeKind kind, TierFacts facts, IReadOnlyList<Cell> cells) =>
        LayoutPerimeterSitesRules.NewSite($"{under}-{Wire.Name(kind)}", kind, RegionRules.RegionOf(cells), "") with { Tier = facts with { Under = under } };

    /// <summary>
    /// Cuts the plan's killbox into the tier nodes standing in it, sited from the census. census carries the
    /// cells (any window of the map; the killbox's region is taken from the plan), the defenders, the
    /// definitions and the turret, mortar and IED gates; its Killbox, Home and Region are the plan's. pins are
    /// the built tiers. Deterministic: a census and pins give one node set. Throws when the plan has no killbox
    /// opening; a census the layout planner refuses leaves the pinned tiers as the nodes.
    /// </summary>
    public static DefenseTiers PlanDefenseTiers(LayoutPlan plan, DefenseRequest census, DefenseTierPins pins)
    {
        var (id, _, ok) = LayoutPlan.KillboxNode(plan);
        var (kb, region, home, found) = LayoutPlan.LayoutKillbox(plan, census.Bounds);
        if (!ok || !found)
        {
            throw new DefenseException("plan has no killbox opening");
        }
        var req = census with
        {
            Killbox = kb,
            Home = home,
            Region = region,
            Entrances = EquatableArray.From(census.Entrances.Where(c => LayoutPerimeterRules.Contains(region, c))),
            Cells = EquatableArray.From(PinnedCensus(census.Cells, region, pins)),
        };

        var decisions = new List<SiteDecision>();
        var refusal = "";
        var refused = false;
        DefenseLayout layout;
        try
        {
            layout = DefenseLayout.DefenseLayouts(req);
        }
        catch (Exception e) when (e is DefenseException or DomainException)
        {
            layout = new DefenseLayout { Toward = kb.Toward, Entry = kb.Entry };
            decisions.Add(new SiteDecision { Node = id, Verdict = LayoutEmbedRules.EmbedUnplaceable, Reason = ReasonTierLayoutFailed });
            refusal = e.Message;
            refused = true;
        }
        var sited = layout.Tiers;
        var (pinnedLayout, pinned) = ApplyPins(layout, pins);
        layout = pinnedLayout;
        if (pinned.Count == 0 && refused)
        {
            return new DefenseTiers { Layout = layout, Refusal = refusal, Decisions = EquatableArray.From(decisions), Sited = sited };
        }
        List<DefenseTierName> displaced;
        (layout, displaced) = DisplaceBuilt(layout, pinned);
        if (!pinned.ContainsKey(DefenseMortarsPolicy.TierMortars))
        {
            var geometry = layout.Geometry();
            var turretCells = new List<Cell>();
            foreach (var t in layout.Tiers)
            {
                if (t.Name != DefenseTurretsPolicy.TierTurrets)
                {
                    continue;
                }
                turretCells.AddRange(t.Reserved);
                turretCells.AddRange(t.Buildings.Select(b => b.Cell));
            }
            geometry = geometry with { Turrets = EquatableArray.From(turretCells) };
            var mortars = DefenseMortarsPolicy.DefenseMortars(req, geometry);
            if (mortars.Buildings.Count > 0)
            {
                layout = layout with { Tiers = EquatableArray.From(layout.Tiers.Append(mortars)) };
            }
        }
        var nodes = TierNodes(id, req.Definitions, layout, pinned);
        foreach (var n in nodes)
        {
            var reason = n.Tier?.Pinned == true ? ReasonTierPinned : ReasonTierSited;
            decisions.Add(new SiteDecision { Node = n.Node, Verdict = LayoutEmbedRules.EmbedPlace, Reason = reason });
        }
        foreach (var name in displaced)
        {
            decisions.Add(new SiteDecision { Node = id + "-" + name.Value, Verdict = LayoutEmbedRules.EmbedPlace, Reason = ReasonTierDisplaced });
        }
        return new DefenseTiers
        {
            Nodes = EquatableArray.From(nodes),
            Layout = layout,
            Refusal = refusal,
            Decisions = EquatableArray.From(decisions),
            Sited = sited,
        };
    }

    /// <summary>
    /// The census window with the built tiers' standing buildings read as the ground they stand on: free,
    /// walkable and uncovered, so the same census that sited the tier sites it on the same cells. Cells outside
    /// region drop.
    /// </summary>
    internal static List<DefenseCell> PinnedCensus(IEnumerable<DefenseCell> cells, Rectangle region, DefenseTierPins pins)
    {
        var standing = new Dictionary<Cell, string>();
        foreach (var t in pins.Tiers.Values)
        {
            foreach (var b in t.Buildings)
            {
                standing[b.Cell] = b.Definition;
            }
        }
        var output = new List<DefenseCell>();
        foreach (var c0 in cells)
        {
            var c = c0;
            if (!LayoutPerimeterRules.Contains(region, c.Cell))
            {
                continue;
            }
            if (standing.TryGetValue(c.Cell, out var def) && c.Edifice == def)
            {
                c = c with
                {
                    Edifice = "",
                    PlayerOwned = false,
                    Door = false,
                    Walkable = true,
                    Passable = true,
                    CoverFill = 0.0,
                };
            }
            output.Add(c);
        }
        return output;
    }

    /// <summary>
    /// Replaces the layout's tiers by the pinned ones (appending a built tier the census no longer sites) and
    /// returns the pinned tiers with the cells they hold. The pinned corridor and firing line keep their geometry.
    /// </summary>
    internal static (DefenseLayout Layout, Dictionary<DefenseTierName, DefenseTier> Pinned) ApplyPins(DefenseLayout layout, DefenseTierPins pins)
    {
        var pinned = new Dictionary<DefenseTierName, DefenseTier>();
        var tiers = layout.Tiers.ToList();
        foreach (var name in pins.Tiers.Keys.OrderBy(n => n.Value, StringComparer.Ordinal))
        {
            var tier = pins.Tiers[name] with { Name = name };
            pinned[name] = tier;
            var i = tiers.FindIndex(t => t.Name == name);
            if (i >= 0)
            {
                tiers[i] = tier;
            }
            else
            {
                tiers.Add(tier);
            }
        }
        layout = layout with { Tiers = EquatableArray.From(tiers) };
        if (pinned.ContainsKey(DefenseLayout.TierChokepoint) && pins.TrapLane.Count > 0)
        {
            layout = layout with { TrapLane = pins.TrapLane };
        }
        if (pinned.ContainsKey(DefenseLayout.TierFiringLine) && pins.Firing.Count > 0)
        {
            layout = layout with { Firing = pins.Firing };
        }
        return (layout, pinned);
    }

    /// <summary>Drops the buildings of the tiers still to build that sit on a cell a built tier holds, and returns the tiers that lost one.</summary>
    internal static (DefenseLayout Layout, List<DefenseTierName> Lost) DisplaceBuilt(DefenseLayout layout, Dictionary<DefenseTierName, DefenseTier> pinned)
    {
        var held = new HashSet<Cell>();
        foreach (var t in pinned.Values)
        {
            foreach (var c in t.Reserved)
            {
                held.Add(c);
            }
            foreach (var b in t.Buildings)
            {
                held.Add(b.Cell);
            }
        }
        var lost = new List<DefenseTierName>();
        var tiers = layout.Tiers.ToList();
        for (var i = 0; i < tiers.Count; i++)
        {
            var t = tiers[i];
            if (pinned.ContainsKey(t.Name))
            {
                continue;
            }
            var kept = t.Buildings.Where(b => !held.Contains(b.Cell)).ToList();
            if (kept.Count < t.Buildings.Count)
            {
                tiers[i] = t with { Buildings = EquatableArray.From(kept) };
                lost.Add(t.Name);
            }
        }
        return (layout with { Tiers = EquatableArray.From(tiers) }, lost);
    }

    /// <summary>Cuts the layout's tiers into nodes under the killbox node id, in DefenseTierOrder; a tier that places nothing and reserves nothing has no node.</summary>
    internal static List<PlannedSite> TierNodes(string under, DefenseDefinitions defs, DefenseLayout layout, Dictionary<DefenseTierName, DefenseTier> pinned)
    {
        var output = new List<PlannedSite>();

        void Add(DefenseTier tier, NodeKind kind, IReadOnlyList<Cell> cells, IReadOnlyList<Building> places, TierFacts facts)
        {
            if (cells.Count == 0)
            {
                return;
            }
            facts = facts with
            {
                Tier = tier.Name,
                Pinned = pinned.ContainsKey(tier.Name),
                Places = EquatableArray.From(places.Select(b => new TierPlacement(b.Definition, b.Cell, b.Rotation, b.Stuff))),
            };
            output.Add(TierNode(under, kind, facts, cells));
        }

        static List<Cell> CellsOf(IEnumerable<Building> bs) => bs.Select(b => b.Cell).ToList();

        static (List<Building> Match, List<Building> Remainder) Split(IEnumerable<Building> bs, string definition)
        {
            var match = new List<Building>();
            var rest = new List<Building>();
            foreach (var b in bs)
            {
                (b.Definition == definition ? match : rest).Add(b);
            }
            return (match, rest);
        }

        foreach (var name in DefenseTierOrder)
        {
            var i = -1;
            for (var k = 0; k < layout.Tiers.Count; k++)
            {
                if (layout.Tiers[k].Name == name)
                {
                    i = k;
                    break;
                }
            }
            if (i < 0)
            {
                continue;
            }
            var tier = layout.Tiers[i];
            if (name == DefenseLayout.TierChokepoint)
            {
                Add(tier, NodeDefCorridor, tier.Reserved, [], new TierFacts { Reserved = tier.Reserved });
                Add(tier, NodeDefLane, layout.TrapLane, [], new TierFacts { Path = layout.TrapLane });
            }
            else if (name == DefenseLayout.TierFunnel)
            {
                Add(tier, NodeDefFunnel, CellsOf(tier.Buildings), tier.Buildings, new TierFacts { Reserved = tier.Reserved });
            }
            else if (name == DefenseLayout.TierTrapCorridor)
            {
                var (traps, rest) = Split(tier.Buildings, defs.Trap);
                var (fences, _) = Split(rest, defs.Fence);
                Add(tier, NodeDefTraps, CellsOf(traps), traps, new TierFacts());
                Add(tier, NodeDefFence, CellsOf(fences), fences, new TierFacts());
            }
            else if (name == DefenseLayout.TierFiringLine)
            {
                var (floors, cover) = Split(tier.Buildings, defs.Floor);
                // A firing line with no cover to build (it fights from cover that stands) holds its shooters' cells.
                var stands = cover.Count > 0 ? CellsOf(cover) : [.. layout.Firing.Select(f => f.Cell)];
                Add(tier, NodeDefFiring, stands, cover, new TierFacts { Reserved = tier.Reserved, Firing = layout.Firing });
                Add(tier, NodeDefShooter, CellsOf(floors), floors, new TierFacts());
            }
            else if (name == DefenseTurretsPolicy.TierTurrets)
            {
                Add(tier, NodeDefTurrets, CellsOf(tier.Buildings), tier.Buildings, new TierFacts { Reserved = tier.Reserved });
            }
            else if (name == DefenseMortarsPolicy.TierMortars)
            {
                Add(tier, NodeDefMortars, CellsOf(tier.Buildings), tier.Buildings, new TierFacts { Reserved = tier.Reserved });
            }
            else if (name == DefenseIEDs.TierIEDs)
            {
                Add(tier, NodeDefIEDs, CellsOf(tier.Buildings), tier.Buildings, new TierFacts { Reserved = tier.Reserved });
            }
            else if (name == DefenseArrivalRoutes.TierBait)
            {
                Add(tier, NodeDefBait, CellsOf(tier.Buildings), tier.Buildings, new TierFacts { Reserved = tier.Reserved });
            }
        }
        return output;
    }
}
