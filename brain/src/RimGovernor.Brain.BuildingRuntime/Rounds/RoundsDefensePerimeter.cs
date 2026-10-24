using System.Globalization;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>What the perimeter cut of one review holds: the layout plan, the bridge foundation, the power conduits and the prison turret sections.</summary>
internal sealed class DefenseCut
{
    public LayoutPlan Plan = new();
    public string Bridge = "";
    public IReadOnlyList<Cell> Transmitters = [];
    public double Spare;
    public double LampReach;
    public IReadOnlyList<PerimeterSection> Prison = [];
}

public sealed partial class RoundsDefenseLayoutPlanner
{
    internal const string DefenseMoisturePump = "MoisturePump";
    internal const string DefenseMoistureResearch = "MoisturePump";
    internal const double DefenseMoisturePumpW = 150.0;
    internal const string DefenseRoomLamp = "StandingLamp";
    internal const int MaxDefenseRemovals = 64;

    /// <summary>Names the perimeter's removal tiers: this prefix, x (deconstruct) or u (lift a bridge), b (ahead of the cut's sections) or a (behind them), and the base of the tier the buildings came from.</summary>
    internal const string DefenseRemovalPrefix = LayoutDefenseRules.TierPerimeterPrefix + "remove-";

    /// <summary>The bridge materials a bridged perimeter cell can stand on.</summary>
    private static readonly string[] DefenseBridges = [LayoutDefenseRules.PerimeterBridge, LayoutDefenseRules.PerimeterHeavyBridge];

    internal static (DefenseCut Cut, bool Known) DefensePerimeterCut(RoundsReading read)
    {
        var projection = read.Projection;
        if (!projection.LayoutPlan.TryGetValue(out var plan))
        {
            return (new DefenseCut(), false);
        }
        var cut = new DefenseCut { Plan = plan, Bridge = DefensePerimeterBridge(projection), LampReach = projection.Stations.Light.StandingReach() };
        var q = DefenseTurretRequest(read).Turret;
        if (DefenseResearched(projection, DefenseMoistureResearch))
        {
            cut.Transmitters = q.Transmitters;
            cut.Spare = q.SpareW;
        }
        cut.Prison = DefensePrisonTurrets(plan, q);
        return (cut, true);
    }

    internal static IReadOnlyList<PerimeterSection> DefensePrisonTurrets(LayoutPlan plan, DefenseTurretRequest q)
    {
        var (draw, spare) = (q.DrawW, q.SpareW);
        if (!q.Available)
        {
            return [];
        }
        List<PerimeterSection> sections;
        try
        {
            sections = PrisonLayoutRules.PerimeterPrisonTurrets(plan, q.Definition, q.Stuff, q.Conduit, q.Transmitters).ToList();
        }
        catch (DomainException)
        {
            return [];
        }
        for (var i = 0; i < sections.Count; i++)
        {
            if ((i + 1) * draw > spare)
            {
                return sections.GetRange(0, i);
            }
        }
        return sections;
    }

    internal static bool DefenseResearched(ColonyProjection projection, string project) =>
        projection.Facts.Research is { } research && research.Finished.Contains(new ResearchProjectId(project));

    internal static string DefenseBuildingKey(DefenseBuilding b) =>
        string.Create(CultureInfo.InvariantCulture, $"{b.Definition}@{b.Cell.X},{b.Cell.Z}/{b.Stuff}");

    internal static DefenseBuilding DefenseStored(Building b) =>
        new() { Definition = b.Definition, Cell = b.Cell, Rotation = b.Rotation, Stuff = b.Stuff };

    internal static (List<PerimeterSection> Ring, List<PerimeterSection> Fences, List<PerimeterSection> Pumps) DefenseNodeSections(LayoutPlan plan, string bridge, IReadOnlyList<Cell> transmitters)
    {
        var ring = LayoutPlan.PerimeterSections(plan, DefenseDefinitionNames.Wall, DefenseDefinitionNames.Door, bridge).ToList();
        var fences = LayoutPlan.FenceSections(plan, DefenseDefinitionNames.Fence, DefenseDefinitionNames.FenceStuff).ToList();
        var pumps = LayoutPlan.PerimeterPumps(plan, DefenseMoisturePump, DefenseConduitDefinition, transmitters).ToList();
        return (ring, fences, pumps);
    }

    internal static List<DefenseBuilding> DefenseResolve(DefenseTierRecord tier, IReadOnlyList<PerimeterSection> sections, ISet<Cell> taken)
    {
        var section = sections.FirstOrDefault(s => s.Name == tier.Name && s.Nodes.SequenceEqual(tier.Nodes));
        return section is null ? [] : [.. section.Buildings.Where(b => !taken.Contains(b.Cell)).Select(DefenseStored)];
    }

    internal static HashSet<Cell> DefenseTaken(IEnumerable<DefenseTierRecord> tiers)
    {
        var taken = new HashSet<Cell>();
        foreach (var t in tiers)
        {
            if (LayoutDefenseRules.IsPerimeterTier(t.Name))
            {
                continue;
            }
            foreach (var b in t.Buildings)
            {
                taken.Add(b.Cell);
            }
        }
        return taken;
    }

    /// <summary>
    /// The plan the tiers' site nodes resolve on: the whole plan, or, once a ring replacement retired the generation
    /// they name, that generation's own view (<see cref="LayoutPlan.SiteView"/>).
    /// </summary>
    internal static LayoutPlan DefenseNodeView(IEnumerable<DefenseTierRecord> tiers, LayoutPlan plan)
    {
        var ids = tiers.Where(t => t.NodeBacked).SelectMany(t => t.Nodes).Distinct(StringComparer.Ordinal).ToList();
        var retired = ids.Any(id => plan.Site(id) is { } s && plan.LiveSites(s.Kind).All(l => l.Node != id));
        return retired ? plan.SiteView(ids) : plan;
    }

    /// <summary>Fills the node-backed tiers of the record with the buildings the current cut resolves them to.</summary>
    internal static void DefenseHydrate(DefenseLayoutRecord record, DefenseCut cut)
    {
        var (ring, fences, pumps) = DefenseNodeSections(DefenseNodeView(record.Tiers, cut.Plan), cut.Bridge, cut.Transmitters);
        var sections = ring.Concat(fences).Concat(pumps).ToList();
        var taken = DefenseTaken(record.Tiers);
        foreach (var t in record.Tiers)
        {
            if (t.NodeBacked)
            {
                t.Buildings = DefenseResolve(t, sections, taken);
            }
        }
    }

    /// <summary>
    /// The perimeter tiers the cut wants: ring, pockets, lights, fence, pumps and prison turrets, clear of the cells the
    /// record's other tiers hold. A pump site a perimeter tier of the record already names keeps its slot; a new pump
    /// is cut only while the spare power covers its draw.
    /// </summary>
    internal static List<DefenseTierRecord> DefensePerimeterWants(DefenseLayoutRecord record, DefenseCut cut)
    {
        var have = new HashSet<Cell>();
        foreach (var t in record.Tiers)
        {
            if (!LayoutDefenseRules.IsPerimeterTier(t.Name) || t.Remove)
            {
                continue;
            }
            foreach (var id in t.Nodes)
            {
                if (cut.Plan.Site(id) is { Kind: NodeKind.Pump } site)
                {
                    have.Add(site.Origin);
                }
            }
        }
        var (ring, fences, pumps) = DefenseNodeSections(cut.Plan, cut.Bridge, cut.Transmitters);
        var pockets = LayoutPlan.PocketSections(cut.Plan, DefenseDefinitionNames.Wall).ToList();
        var lights = LayoutPlan.LightSections(cut.Plan, DefenseRoomLamp, cut.LampReach).ToList();
        var spare = cut.Spare;
        for (var i = 0; i < pumps.Count; i++)
        {
            if (cut.Plan.Site(pumps[i].Nodes[0]) is { } site && have.Contains(site.Origin))
            {
                continue;
            }
            if (spare < DefenseMoisturePumpW)
            {
                pumps = pumps.GetRange(0, i);
                break;
            }
            spare -= DefenseMoisturePumpW;
        }
        var taken = DefenseTaken(record.Tiers);
        var wants = new List<DefenseTierRecord>();
        foreach (var s in ring.Concat(pockets).Concat(lights).Concat(fences).Concat(pumps).Concat(cut.Prison))
        {
            var t = new DefenseTierRecord { Name = s.Name, Nodes = [.. s.Nodes] };
            t.Buildings.AddRange(s.Buildings.Where(b => !taken.Contains(b.Cell)).Select(DefenseStored));
            if (t.Buildings.Count > 0)
            {
                wants.Add(t);
            }
        }
        return wants;
    }

    /// <summary>
    /// What a perimeter tier owns as it stands. A removal tier, or a tier of no site node, owns its stored buildings;
    /// a node tier what its nodes resolve to, each bridged cell with the bridge the census shows on it (and the wall
    /// on it in that bridge's stuff), the cut's bridge where none stands.
    /// </summary>
    internal static List<DefenseBuilding> DefenseOwned(DefenseTierRecord tier, IReadOnlyDictionary<string, List<PerimeterSection>> byBridge, string bridge, DefenseCensus census, ISet<Cell> taken)
    {
        if (!tier.NodeBacked)
        {
            return tier.Buildings;
        }
        var owned = DefenseResolve(tier, byBridge[bridge], taken);
        var resolved = byBridge.ToDictionary(kv => kv.Key, kv => DefenseResolve(tier, kv.Value, taken), StringComparer.Ordinal);
        for (var i = 0; i < owned.Count; i++)
        {
            if (census.Terrain.TryGetValue(owned[i].Cell, out var terrain) && terrain != bridge && resolved.TryGetValue(terrain, out var other) && other.Count == owned.Count)
            {
                owned[i] = other[i];
            }
        }
        return owned;
    }

    /// <summary>The name of the removal tier for buildings of <paramref name="source"/>: deconstructed or lifted, ahead of the cut's sections or behind them.</summary>
    internal static DefenseTierName DefenseRemovalName(DefenseTierRecord source, bool lift, bool ahead)
    {
        var rest = source.Name.Value[LayoutDefenseRules.TierPerimeterPrefix.Length..];
        if (source.Remove && source.Name.Value.StartsWith(DefenseRemovalPrefix, StringComparison.Ordinal))
        {
            rest = source.Name.Value[(DefenseRemovalPrefix.Length + 3)..];
        }
        return new DefenseTierName(string.Create(CultureInfo.InvariantCulture, $"{DefenseRemovalPrefix}{(lift ? 'u' : 'x')}{(ahead ? 'b' : 'a')}-{rest}"));
    }

    /// <summary>The perimeter tiers in order, by name, state, nodes and stored buildings: what a recut that changed nothing leaves alike.</summary>
    private static string DefensePerimeterSignature(IEnumerable<DefenseTierRecord> tiers) =>
        string.Join('\n', tiers.Where(t => LayoutDefenseRules.IsPerimeterTier(t.Name)).Select(t =>
            $"{t.Name.Value}|{t.Remove}|{t.Built}|{string.Join(',', t.Nodes)}|{(t.NodeBacked ? "" : string.Join(',', t.Buildings.Select(DefenseBuildingKey).Order(StringComparer.Ordinal)))}"));

    /// <summary>
    /// Recuts the perimeter tiers of the record against the current cut and the census, every review: the tiers become
    /// the ones the cut wants, and the buildings the old perimeter tiers own that the cut no longer wants and the census
    /// shows standing become removal tiers. A removal is staged by its cell and kind, derived again each review: ahead
    /// of the cut's sections where they build on the cell, behind them (the old wall defends until the new one stands)
    /// elsewhere; a bridge is lifted after what stands on it. Conduits are never removed: the pump runs follow the
    /// power net, which a laid run joins. True when the record changed, or the stored killbox no longer matches the
    /// plan and must be proposed again. Without a census nothing a stored perimeter owns is known, so it is kept.
    /// </summary>
    internal static bool DefenseRecutPerimeter(DefenseLayoutRecord record, DefenseCut cut, DefenseCensus? census)
    {
        var kept = record.Tiers.Where(t => !LayoutDefenseRules.IsPerimeterTier(t.Name)).ToList();
        var old = record.Tiers.Where(t => LayoutDefenseRules.IsPerimeterTier(t.Name)).ToList();
        if (old.Count > 0)
        {
            var (id, digest, ok) = LayoutPlan.KillboxNode(cut.Plan);
            if (!ok || census is null)
            {
                DefenseHydrate(record, cut);
                return false;
            }
            if (id != record.Killbox || digest != record.KillboxDigest)
            {
                record.Killbox = "";
                return true;
            }
        }
        var before = DefensePerimeterSignature(old);
        var wants = DefensePerimeterWants(record, cut);
        var wanted = new HashSet<string>(wants.SelectMany(t => t.Buildings).Select(DefenseBuildingKey), StringComparer.Ordinal);
        var building = new HashSet<Cell>(wants.SelectMany(t => t.Buildings).Select(b => b.Cell));
        var taken = DefenseTaken(kept);
        var view = DefenseNodeView(old, cut.Plan);
        var byBridge = new Dictionary<string, List<PerimeterSection>>(StringComparer.Ordinal);
        foreach (var bridge in DefenseBridges)
        {
            var (r, f, p) = DefenseNodeSections(view, bridge, cut.Transmitters);
            byBridge[bridge] = [.. r, .. f, .. p];
        }
        var prior = old.ToDictionary(t => t.Name.Value, StringComparer.Ordinal);
        var owned = new Dictionary<string, List<DefenseBuilding>>(StringComparer.Ordinal);
        var removals = new List<(DefenseTierRecord Tier, bool Lift, bool Ahead, HashSet<string> Keys)>();
        foreach (var t in old)
        {
            // A stored perimeter returned above without a census: old is empty or census is set.
            var standing = census!;
            var mine = DefenseOwned(t, byBridge, cut.Bridge, standing, taken);
            owned[t.Name.Value] = mine;
            foreach (var b in mine)
            {
                var key = DefenseBuildingKey(b);
                if (b.Definition == DefenseConduitDefinition || wanted.Contains(key) || !standing.Standing(b))
                {
                    continue;
                }
                var lift = DefenseFoundation(b.Definition);
                var ahead = building.Contains(b.Cell);
                var name = DefenseRemovalName(t, lift, ahead);
                var i = removals.FindIndex(r => r.Tier.Name == name);
                if (i < 0)
                {
                    var tier = new DefenseTierRecord { Name = name, Remove = true };
                    if (prior.TryGetValue(name.Value, out var was) && was.Remove)
                    {
                        tier.Attempts = was.Attempts;
                    }
                    removals.Add((tier, lift, ahead, new HashSet<string>(StringComparer.Ordinal)));
                    i = removals.Count - 1;
                }
                var removal = removals[i];
                if (removal.Keys.Add(key))
                {
                    removal.Tier.Buildings.Add(b);
                }
                foreach (var node in t.Nodes.Where(n => !removal.Tier.Nodes.Contains(n)))
                {
                    removal.Tier.Nodes.Add(node);
                }
            }
        }
        foreach (var w in wants)
        {
            if (!prior.TryGetValue(w.Name.Value, out var was) || was.Remove)
            {
                continue;
            }
            w.Attempts = was.Attempts;
            var same = owned[w.Name.Value].Select(DefenseBuildingKey).ToHashSet(StringComparer.Ordinal).SetEquals(w.Buildings.Select(DefenseBuildingKey));
            w.Built = was.Built && same;
        }
        var tiers = new List<DefenseTierRecord>(kept);
        tiers.AddRange(removals.Where(r => r.Ahead && !r.Lift).Select(r => r.Tier));
        tiers.AddRange(removals.Where(r => r.Ahead && r.Lift).Select(r => r.Tier));
        tiers.AddRange(wants);
        tiers.AddRange(removals.Where(r => !r.Ahead && !r.Lift).Select(r => r.Tier));
        tiers.AddRange(removals.Where(r => !r.Ahead && r.Lift).Select(r => r.Tier));
        record.Tiers = tiers;
        record.Validate();
        return DefensePerimeterSignature(tiers) != before;
    }

    internal static bool DefenseFoundation(string definition) =>
        definition == LayoutDefenseRules.PerimeterBridge || definition == LayoutDefenseRules.PerimeterHeavyBridge;

    internal static bool DefenseRemovalGone(DefenseTierRecord tier, DefenseCensus census)
    {
        foreach (var b in tier.Buildings)
        {
            if (census.Standing(b))
            {
                return false;
            }
        }
        return true;
    }

    internal RoundsDefenseLayoutResult Remove(NeedOwner goal, ControlState state, RoundsReading read, DefenseLayoutRecord record, DefenseTierRecord tier, DefenseCensus? census, CancellationToken call, CancellationToken epoch)
    {
        var p = _reviewer.Player;
        var key = DefenseTierMethodId(tier);
        var id = PlanIds.MintPlanId();
        var (actions, pending, lost) = DefenseRemovalActions(id, tier, census!);
        if (lost.Count > 0)
        {
            tier.Buildings = tier.Buildings.Where(b => !lost.Contains(b.Cell)).ToList();
            record.SetTier(tier);
            DefensePipeline.DefenseAction("defense-layout", TelemetryLevel.Info, "refused", "nothing_removable", tier.Name.Value, new Dictionary<string, object?> { ["cells"] = lost.Count }, call);
            p.Journal.SaveDefenseLayout(record, call);
        }
        if (actions.Count == 0)
        {
            if (pending)
            {
                return new RoundsDefenseLayoutResult { Verdict = FieldUnavailable("perimeter_preview"), Tier = tier.Name.Value };
            }
            return new RoundsDefenseLayoutResult { Verdict = NoSpace("perimeter_section"), Tier = tier.Name.Value };
        }
        var plan = new PlanSpec(id, new PlanRevision(1), actions);
        p.Current(call, epoch);
        var now = _reviewer.Clock.Now();
        if (p.Session.State() != state || now < read.StartedAt || now - read.StartedAt > _reviewer.MaxAge)
        {
            throw DefenseControlErr(1);
        }
        var commit = p.Journal.CommitOwnerWork(goal, key, "", plan, call);
        if (commit.Outcome != Sto.ClaimOutcome.Admitted)
        {
            return new RoundsDefenseLayoutResult { Verdict = RoundsBuildingPlanner.OwnerCommitVerdict(commit), Tier = tier.Name.Value };
        }
        tier.Attempts++;
        record.SetTier(tier);
        p.Journal.SaveDefenseLayout(record, call);
        DefensePipeline.DefenseAction("defense-layout", TelemetryLevel.Info, "applied", "removals", tier.Name.Value, new Dictionary<string, object?> { ["removals"] = actions.Count, ["method"] = key.Value }, call);
        return new RoundsDefenseLayoutResult { Verdict = BuildingReasonAdmitted, Plan = id, Tier = tier.Name.Value };
    }

    internal static (List<DomainAction> Actions, bool Pending, HashSet<Cell> Lost) DefenseRemovalActions(PlanId id, DefenseTierRecord tier, DefenseCensus census)
    {
        var actions = new List<DomainAction>();
        var pending = false;
        var lost = new HashSet<Cell>();
        foreach (var b in tier.Buildings)
        {
            if (!census.Standing(b) || actions.Count == MaxDefenseRemovals)
            {
                continue;
            }
            var actionId = new ActionId(string.Create(CultureInfo.InvariantCulture, $"{id}-{actions.Count}"));
            DomainAction action;
            if (DefenseFoundation(b.Definition))
            {
                if (census.Unbridging.GetValueOrDefault(b.Cell))
                {
                    pending = true;
                    continue;
                }
                action = DomainAction.NewFoundationRemovalAction(actionId, new FoundationRemoval(b.Definition, b.Cell));
            }
            else
            {
                census.Cover.TryGetValue(b.Cell, out var cover);
                if (cover is not null && cover.Designated)
                {
                    pending = true;
                    continue;
                }
                if (cover is null || cover.DefName != b.Definition || cover.Designation() != CoverClearanceDesignation.Deconstruct)
                {
                    lost.Add(b.Cell);
                    continue;
                }
                action = DomainAction.NewCoverClearanceAction(actionId, new CoverClearance(cover.ThingId, cover.DefName, CoverClearanceDesignation.Deconstruct, b.Cell));
            }
            actions.Add(action);
        }
        return (actions, pending, lost);
    }
}
