namespace RimGovernor.Brain.Policy;

// The defense tiers as plan nodes (#2973). PlanDefenseTiers cuts the killbox node into the tiers that stand in
// it: the corridor and its trap lane, the funnel walls, the fence bar, the firing line and the shooters' floor,
// the spike traps, the turrets, the mortars, the IEDs and the bait. Each is a PlannedSite whose Tier facts name
// the node it stands under (the killbox), the tier of the defense record it belongs to and the placements it
// holds.
//
// Where a tier stands depends on live cover, trees and standing buildings, so the nodes are a function of the
// plan and a census, not of the survey: they are re-sited from every census and are not part of the stored
// plan. A tier that is started (built, an attempt admitted, or any building standing) is pinned, as the replan
// holds a room that holds anything of ours: its node is the built tier where it stands, and the tiers still to
// build are sited around it. The census is an explicit input.

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

    /// <summary>The geometry of the built corridor, used while the chokepoint tier is pinned.</summary>
    public EquatableArray<Cell> TrapLane { get; init; }

    /// <summary>The geometry of the built firing line, used while the firing line tier is pinned.</summary>
    public EquatableArray<FiringPosition> Firing { get; init; }
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

public static partial class PolicyFunctions
{
    /// <summary>The passage raiders walk from the opening to the kill row.</summary>
    public static readonly NodeKind NodeDefCorridor = new("defense_corridor");

    /// <summary>The ordered snake path inside the corridor.</summary>
    public static readonly NodeKind NodeDefLane = new("defense_trap_lane");

    public static readonly NodeKind NodeDefFunnel = new("defense_funnel");

    /// <summary>The fence bar across the kill zone.</summary>
    public static readonly NodeKind NodeDefFence = new("defense_kill_fence");

    /// <summary>The spike traps on the lane's turns.</summary>
    public static readonly NodeKind NodeDefTraps = new("defense_spike_traps");

    /// <summary>The cover of the firing line.</summary>
    public static readonly NodeKind NodeDefFiring = new("defense_firing_line");

    /// <summary>The floor under each shooter.</summary>
    public static readonly NodeKind NodeDefShooter = new("defense_shooter_floor");

    public static readonly NodeKind NodeDefTurrets = new("defense_turrets");
    public static readonly NodeKind NodeDefMortars = new("defense_mortars");
    public static readonly NodeKind NodeDefIEDs = new("defense_ieds");
    public static readonly NodeKind NodeDefBait = new("defense_bait");

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
        [TierChokepoint, TierFunnel, TierTrapCorridor, TierFiringLine, TierTurrets, TierMortars, TierIEDs, TierBait];

    /// <summary>The plan with its defense tier nodes replaced by the given ones. The result is for drawing and reading; it is not stored.</summary>
    public static LayoutPlan WithDefenseTiers(this LayoutPlan p, IEnumerable<PlannedSite> nodes)
    {
        var stripped = p.WithoutSites([.. DefenseTierKinds]);
        return stripped with { Sites = EquatableArray.From(stripped.Sites.Concat(nodes)) };
    }

    /// <summary>
    /// Cuts the plan's killbox into the tier nodes standing in it, sited from the census. census carries the
    /// cells (any window of the map; the killbox's region is taken from the plan), the defenders, the
    /// definitions and the turret, mortar and IED gates; its Killbox, Home and Region are the plan's. pins are
    /// the built tiers. Deterministic: a census and pins give one node set. Throws when the plan has no killbox
    /// opening; a census the layout planner refuses leaves the pinned tiers as the nodes.
    /// </summary>
    public static DefenseTiers PlanDefenseTiers(LayoutPlan plan, DefenseRequest census, DefenseTierPins pins)
    {
        var (id, _, ok) = KillboxNode(plan);
        var (kb, region, home, found) = LayoutKillbox(plan, census.Bounds);
        if (!ok || !found)
        {
            throw new DefenseException("plan has no killbox opening");
        }
        var req = census with
        {
            Killbox = kb,
            Home = home,
            Region = region,
            Entrances = EquatableArray.From(census.Entrances.Where(c => Contains(region, c))),
            Cells = EquatableArray.From(PinnedCensus(census.Cells, region, pins)),
        };

        var decisions = new List<SiteDecision>();
        var refusal = "";
        var refused = false;
        DefenseLayout layout;
        try
        {
            layout = DefenseLayouts(req);
        }
        catch (Exception e) when (e is DefenseException or DomainException)
        {
            layout = new DefenseLayout { Toward = kb.Toward, Width = kb.Width, Entry = kb.Entry };
            decisions.Add(new SiteDecision { Node = id, Verdict = EmbedVerdict.Unplaceable, Reason = ReasonTierLayoutFailed });
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
        if (!pinned.ContainsKey(TierMortars))
        {
            var geometry = layout.Geometry();
            var turretCells = new List<Cell>();
            foreach (var t in layout.Tiers)
            {
                if (t.Name != TierTurrets)
                {
                    continue;
                }
                turretCells.AddRange(t.Reserved);
                turretCells.AddRange(t.Buildings.Select(b => b.Cell));
            }
            geometry = geometry with { Turrets = EquatableArray.From(turretCells) };
            try
            {
                var mortars = DefenseMortars(req, geometry);
                if (mortars.Buildings.Count > 0)
                {
                    layout = layout with { Tiers = EquatableArray.From(layout.Tiers.Append(mortars)) };
                }
            }
            catch (Exception e) when (e is DefenseException or DomainException)
            {
                // A census the mortar tier refuses leaves the tier out.
            }
        }
        var nodes = TierNodes(id, req.Definitions, layout, pinned);
        foreach (var n in nodes)
        {
            var reason = n.Tier?.Pinned == true ? ReasonTierPinned : ReasonTierSited;
            decisions.Add(new SiteDecision { Node = n.Node, Verdict = EmbedVerdict.Place, Reason = reason });
        }
        foreach (var name in displaced)
        {
            decisions.Add(new SiteDecision { Node = id + "-" + name.Value, Verdict = EmbedVerdict.Place, Reason = ReasonTierDisplaced });
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
            if (!Contains(region, c.Cell))
            {
                continue;
            }
            if (standing.TryGetValue(c.Cell, out var def) && c.Edifice == def)
            {
                c = c with
                {
                    Edifice = "",
                    PlayerOwned = Known(false),
                    Door = Known(false),
                    Walkable = Known(true),
                    Passable = Known(true),
                    CoverFill = Known(0.0),
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
        if (pinned.ContainsKey(TierChokepoint) && pins.TrapLane.Count > 0)
        {
            layout = layout with { TrapLane = pins.TrapLane };
        }
        if (pinned.ContainsKey(TierFiringLine) && pins.Firing.Count > 0)
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
                Under = under,
                Tier = tier.Name,
                Pinned = pinned.ContainsKey(tier.Name),
                Places = EquatableArray.From(places.Select(b => new TierPlacement(b.Definition, b.Cell, b.Rotation, b.Stuff))),
            };
            var site = NewSite($"{under}-{kind.Value}", kind, RegionOf(cells), "");
            output.Add(site with { Tier = facts });
        }

        static List<Cell> CellsOf(IEnumerable<Building> bs) => bs.Select(b => b.Cell).ToList();

        static (List<Building> Match, List<Building> Rest) Split(IEnumerable<Building> bs, string definition)
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
            if (name == TierChokepoint)
            {
                Add(tier, NodeDefCorridor, tier.Reserved, [], new TierFacts { Reserved = tier.Reserved });
                Add(tier, NodeDefLane, layout.TrapLane, [], new TierFacts { Path = layout.TrapLane });
            }
            else if (name == TierFunnel)
            {
                Add(tier, NodeDefFunnel, CellsOf(tier.Buildings), tier.Buildings, new TierFacts { Reserved = tier.Reserved });
            }
            else if (name == TierTrapCorridor)
            {
                var (traps, rest) = Split(tier.Buildings, defs.Trap);
                var (fences, _) = Split(rest, defs.Fence);
                Add(tier, NodeDefTraps, CellsOf(traps), traps, new TierFacts());
                Add(tier, NodeDefFence, CellsOf(fences), fences, new TierFacts());
            }
            else if (name == TierFiringLine)
            {
                var (floors, cover) = Split(tier.Buildings, defs.Floor);
                Add(tier, NodeDefFiring, CellsOf(cover), cover, new TierFacts { Reserved = tier.Reserved, Firing = layout.Firing });
                Add(tier, NodeDefShooter, CellsOf(floors), floors, new TierFacts());
            }
            else if (name == TierTurrets)
            {
                Add(tier, NodeDefTurrets, CellsOf(tier.Buildings), tier.Buildings, new TierFacts { Reserved = tier.Reserved });
            }
            else if (name == TierMortars)
            {
                Add(tier, NodeDefMortars, CellsOf(tier.Buildings), tier.Buildings, new TierFacts { Reserved = tier.Reserved });
            }
            else if (name == TierIEDs)
            {
                Add(tier, NodeDefIEDs, CellsOf(tier.Buildings), tier.Buildings, new TierFacts { Reserved = tier.Reserved });
            }
            else if (name == TierBait)
            {
                Add(tier, NodeDefBait, CellsOf(tier.Buildings), tier.Buildings, new TierFacts { Reserved = tier.Reserved });
            }
        }
        return output;
    }
}
