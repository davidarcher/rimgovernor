using System.Globalization;
using System.Security.Cryptography;
using System.Text;

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
            q.SpareW.TryGetValue(out cut.Spare);
        }
        cut.Prison = DefensePrisonTurrets(plan, q);
        return (cut, true);
    }

    internal static IReadOnlyList<PerimeterSection> DefensePrisonTurrets(LayoutPlan plan, DefenseTurretRequest q)
    {
        var ak = q.Available.TryGetValue(out var available);
        var dk = q.DrawW.TryGetValue(out var draw);
        var sk = q.SpareW.TryGetValue(out var spare);
        if (!ak || !available || !dk || !sk)
        {
            return [];
        }
        List<PerimeterSection> sections;
        try
        {
            sections = PolicyApi.PerimeterPrisonTurrets(plan, q.Definition, q.Stuff, q.Conduit, q.Transmitters).ToList();
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
        projection.Facts.Research.TryGetValue(out var research) && research.Finished.Contains(new ResearchProjectId(project));

    internal static string DefenseBuildingKey(DefenseBuilding b) =>
        string.Create(CultureInfo.InvariantCulture, $"{b.Definition}@{b.Cell.X},{b.Cell.Z}/{b.Stuff}");

    internal static DefenseBuilding DefenseStored(Building b) =>
        new() { Definition = b.Definition, Cell = b.Cell, Rotation = b.Rotation, Stuff = b.Stuff };

    internal static (List<PerimeterSection> Ring, List<PerimeterSection> Fences, List<PerimeterSection> Pumps) DefenseNodeSections(LayoutPlan plan, string bridge, IReadOnlyList<Cell> transmitters)
    {
        var ring = PolicyApi.PerimeterSections(plan, DefenseDefinitionNames.Wall, DefenseDefinitionNames.Door, bridge).ToList();
        var fences = PolicyApi.FenceSections(plan, DefenseDefinitionNames.Fence, DefenseDefinitionNames.FenceStuff).ToList();
        var pumps = PolicyApi.PerimeterPumps(plan, DefenseMoisturePump, DefenseConduitDefinition, transmitters).ToList();
        return (ring, fences, pumps);
    }

    internal static (PerimeterSection Section, bool Ok) DefenseSectionFor(DefenseTierRecord tier, IReadOnlyList<PerimeterSection> sections)
    {
        var named = sections.Where(s => s.Nodes.SequenceEqual(tier.Nodes)).ToList();
        if (named.Count == 1)
        {
            return (named[0], true);
        }
        var baseName = PolicyApi.PerimeterSectionBase(tier.Name);
        foreach (var s in named)
        {
            if (PolicyApi.PerimeterSectionBase(s.Name) == baseName)
            {
                return (s, true);
            }
        }
        return (new PerimeterSection(), false);
    }

    internal static List<DefenseBuilding> DefenseResolve(DefenseTierRecord tier, IReadOnlyList<PerimeterSection> sections, ISet<Cell> taken)
    {
        var output = new List<DefenseBuilding>();
        var (section, ok) = DefenseSectionFor(tier, sections);
        if (!ok)
        {
            return output;
        }
        foreach (var b in section.Buildings)
        {
            if (!taken.Contains(b.Cell) && !tier.Refused.Contains(b.Cell))
            {
                output.Add(DefenseStored(b));
            }
        }
        return output;
    }

    internal static HashSet<Cell> DefenseTaken(IEnumerable<DefenseTierRecord> tiers)
    {
        var taken = new HashSet<Cell>();
        foreach (var t in tiers)
        {
            if (PolicyApi.IsPerimeterTier(t.Name))
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

    /// <summary>Fills the node-backed tiers of the record with the buildings the current cut resolves them to.</summary>
    internal static void DefenseHydrate(DefenseLayoutRecord record, DefenseCut cut)
    {
        var (ring, fences, pumps) = DefenseNodeSections(cut.Plan, cut.Bridge, cut.Transmitters);
        var sections = ring.Concat(fences).Concat(pumps).ToList();
        var taken = DefenseTaken(record.Tiers);
        foreach (var t in record.Tiers)
        {
            if (t.NodeBacked && !t.Remove)
            {
                t.Buildings = DefenseResolve(t, sections, taken);
            }
        }
        var (wanted, building) = DefenseWanted(record.Tiers);
        var cuts = new Dictionary<string, List<PerimeterSection>>(StringComparer.Ordinal);
        foreach (var t in record.Tiers)
        {
            if (t.NodeBacked && t.Remove)
            {
                var key = t.Bridge + "/" + string.Join(",", t.Nodes);
                if (!cuts.TryGetValue(key, out var cutSections))
                {
                    var (r2, f2, p2) = DefenseNodeSections(cut.Plan.SiteView(t.Nodes), t.Bridge, cut.Transmitters);
                    cutSections = r2.Concat(f2).Concat(p2).ToList();
                    cuts[key] = cutSections;
                }
                t.Buildings = DefenseResolveRemoval(t, cutSections, taken, wanted, building);
            }
        }
    }

    internal static (HashSet<string> Wanted, HashSet<Cell> Building) DefenseWanted(IEnumerable<DefenseTierRecord> tiers)
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        var building = new HashSet<Cell>();
        foreach (var t in tiers)
        {
            if (!PolicyApi.IsPerimeterTier(t.Name) || t.Remove)
            {
                continue;
            }
            foreach (var b in t.Buildings)
            {
                wanted.Add(DefenseBuildingKey(b));
                building.Add(b.Cell);
            }
        }
        return (wanted, building);
    }

    internal static List<DefenseBuilding> DefenseResolveRemoval(DefenseTierRecord tier, IReadOnlyList<PerimeterSection> sections, ISet<Cell> taken, ISet<string> wanted, ISet<Cell> building)
    {
        var output = new List<DefenseBuilding>();
        foreach (var s in sections)
        {
            foreach (var b in s.Buildings)
            {
                var sb = DefenseStored(b);
                if (!taken.Contains(sb.Cell) && !wanted.Contains(DefenseBuildingKey(sb)) && DefenseFoundation(sb.Definition) == tier.Foundation && building.Contains(sb.Cell) == tier.Before)
                {
                    output.Add(sb);
                }
            }
        }
        return output;
    }

    /// <summary>Recuts the perimeter tiers of the record against the current cut; true when the record changed (or the stored killbox no longer matches and must be proposed again).</summary>
    internal static bool DefenseRecutPerimeter(DefenseLayoutRecord record, DefenseCut cut)
    {
        var kept = new List<DefenseTierRecord>();
        var old = new List<DefenseTierRecord>();
        var have = new HashSet<Cell>();
        foreach (var t in record.Tiers)
        {
            if (!PolicyApi.IsPerimeterTier(t.Name))
            {
                kept.Add(t);
                continue;
            }
            old.Add(t);
            if (t.Remove)
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
        if (old.Count > 0)
        {
            var (id, digest, ok) = PolicyApi.KillboxNode(cut.Plan);
            if (!ok)
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
        var (ring, fences, pumps) = DefenseNodeSections(cut.Plan, cut.Bridge, cut.Transmitters);
        var pockets = PolicyApi.PocketSections(cut.Plan, DefenseDefinitionNames.Wall).ToList();
        var lights = PolicyApi.LightSections(cut.Plan, DefenseRoomLamp, cut.LampReach).ToList();
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
        var taken = DefenseTaken(kept);
        var digestText = new StringBuilder();
        digestText.Append(CultureInfo.InvariantCulture, $"sections/{cut.Bridge}/{(pumps.Count > 0 ? "true" : "false")}\n");
        var keys = new List<string>();
        var fresh = new List<DefenseTierRecord>();
        foreach (var s in ring.Concat(pockets).Concat(lights).Concat(fences).Concat(pumps).Concat(cut.Prison))
        {
            var t = new DefenseTierRecord { Name = s.Name, Nodes = s.Nodes.ToList() };
            keys.Add("nodes/" + string.Join(",", s.Nodes));
            foreach (var b in s.Buildings)
            {
                if (taken.Contains(b.Cell))
                {
                    continue;
                }
                var rb = DefenseStored(b);
                t.Buildings.Add(rb);
                if (rb.Definition != DefenseConduitDefinition)
                {
                    keys.Add(DefenseBuildingKey(rb));
                }
            }
            if (t.Buildings.Count > 0)
            {
                fresh.Add(t);
            }
        }
        keys.Sort(string.CompareOrdinal);
        foreach (var k in keys)
        {
            digestText.Append(k).Append('\n');
        }
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(digestText.ToString())).AsSpan(0, 16));
        if (key == record.PerimeterKey)
        {
            DefenseHydrate(record, cut);
            return false;
        }
        if (old.Count == 0)
        {
            record.PerimeterKey = key;
            record.PerimeterBridge = cut.Bridge;
            kept.AddRange(fresh);
            record.Tiers = kept;
            record.Validate();
            return true;
        }
        var previous = record.PerimeterBridge != "" ? record.PerimeterBridge : cut.Bridge;
        record.PerimeterKey = key;
        record.PerimeterBridge = cut.Bridge;
        record.PerimeterRevision++;
        var prefix = string.Create(CultureInfo.InvariantCulture, $"{PolicyConstants.TierPerimeterPrefix}r{record.PerimeterRevision}-");
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        var building = new HashSet<Cell>();
        foreach (var t in fresh)
        {
            foreach (var b in t.Buildings)
            {
                wanted.Add(DefenseBuildingKey(b));
                building.Add(b.Cell);
            }
        }
        var removals = new List<DefenseTierRecord>[2, 2];
        for (var a = 0; a < 2; a++)
        {
            for (var l = 0; l < 2; l++)
            {
                removals[a, l] = [];
            }
        }
        void Stage(DefenseTierRecord t)
        {
            var after = t.Before ? 0 : 1;
            var lift = t.Foundation ? 1 : 0;
            removals[after, lift].Add(t);
        }
        var n = 0;
        string Name(string letter)
        {
            n++;
            return string.Create(CultureInfo.InvariantCulture, $"{prefix}{letter}{n - 1:D2}");
        }
        string Letter(int lift) => lift == 0 ? "x" : "u";
        var ids = new List<string>();
        foreach (var t in old)
        {
            if (t.Remove && t.NodeBacked)
            {
                Stage(t);
            }
            else if (t.Remove)
            {
                t.Buildings = t.Buildings.Where(b => !wanted.Contains(DefenseBuildingKey(b))).ToList();
                if (t.Buildings.Count > 0)
                {
                    Stage(t);
                }
            }
            else if (t.NodeBacked)
            {
                foreach (var id in t.Nodes)
                {
                    if (!ids.Contains(id))
                    {
                        ids.Add(id);
                    }
                }
            }
            else
            {
                var split = new List<DefenseBuilding>[2, 2];
                for (var a = 0; a < 2; a++)
                {
                    for (var l = 0; l < 2; l++)
                    {
                        split[a, l] = [];
                    }
                }
                foreach (var b in t.Buildings)
                {
                    if (wanted.Contains(DefenseBuildingKey(b)))
                    {
                        continue;
                    }
                    var after = building.Contains(b.Cell) ? 0 : 1;
                    var lift = DefenseFoundation(b.Definition) ? 1 : 0;
                    split[after, lift].Add(b);
                }
                for (var after = 0; after < 2; after++)
                {
                    for (var lift = 0; lift < 2; lift++)
                    {
                        if (split[after, lift].Count > 0)
                        {
                            Stage(new DefenseTierRecord { Name = new DefenseTierName(Name(Letter(lift))), Buildings = split[after, lift], Remove = true, Before = after == 0, Foundation = lift == 1 });
                        }
                    }
                }
            }
        }
        if (ids.Count > 0)
        {
            for (var after = 0; after < 2; after++)
            {
                for (var lift = 0; lift < 2; lift++)
                {
                    Stage(new DefenseTierRecord { Name = new DefenseTierName(Name(Letter(lift))), Nodes = ids, Bridge = previous, Remove = true, Before = after == 0, Foundation = lift == 1 });
                }
            }
        }
        foreach (var f in fresh)
        {
            var fresh0 = f.Name.Value;
            f.Name = new DefenseTierName(prefix + (fresh0.StartsWith(PolicyConstants.TierPerimeterPrefix, StringComparison.Ordinal) ? fresh0[PolicyConstants.TierPerimeterPrefix.Length..] : fresh0));
        }
        var tiers = new List<DefenseTierRecord>(kept);
        tiers.AddRange(removals[0, 0]);
        tiers.AddRange(removals[0, 1]);
        tiers.AddRange(fresh);
        tiers.AddRange(removals[1, 0]);
        tiers.AddRange(removals[1, 1]);
        record.Tiers = tiers;
        record.Complete = false;
        DefenseHydrate(record, cut);
        record.Tiers.RemoveAll(t => t.Remove && t.NodeBacked && t.Buildings.Count == 0);
        record.Validate();
        return true;
    }

    internal static bool DefenseFoundation(string definition) =>
        definition == PolicyConstants.PerimeterBridge || definition == PolicyConstants.PerimeterHeavyBridge;

    internal static bool DefenseRemovalGone(DefenseTierRecord tier, DefenseCensus census)
    {
        foreach (var b in tier.Buildings)
        {
            if (census.Standing(b.Definition, b.Cell))
            {
                return false;
            }
        }
        return true;
    }

    internal RoundsDefenseLayoutResult Remove(StandardState goal, ControlState state, RoundsReading read, DefenseLayoutRecord record, DefenseTierRecord tier, DefenseCensus? census, CancellationToken call, CancellationToken epoch)
    {
        var p = _reviewer.Player;
        var key = DefenseTierMethodId(tier);
        var id = PlanIds.MintPlanId();
        var (actions, pending, lost) = DefenseRemovalActions(id, tier, census!);
        if (lost.Count > 0)
        {
            tier.Buildings = tier.Buildings.Where(b => !lost.Contains(b.Cell)).ToList();
            record.SetTier(tier);
            DefenseAction("defense-layout", TelemetryLevel.Info, "refused", "nothing_removable", tier.Name.Value, new Dictionary<string, object?> { ["cells"] = lost.Count }, call);
            p.Journal.SaveDefenseLayout(record, call);
        }
        if (actions.Count == 0)
        {
            if (pending)
            {
                return new RoundsDefenseLayoutResult { Verdict = FieldUnavailable("perimeter_preview"), Tier = tier.Name.Value, NativeWorkTicks = DefenseNativeWorkTicks };
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
        p.Journal.CommitMethodReason(goal.Standard.Id, goal.Revision, key, "", plan, call);
        tier.Attempts++;
        record.SetTier(tier);
        p.Journal.SaveDefenseLayout(record, call);
        DefenseAction("defense-layout", TelemetryLevel.Info, "applied", "removals", tier.Name.Value, new Dictionary<string, object?> { ["removals"] = actions.Count, ["method"] = key.Value }, call);
        return new RoundsDefenseLayoutResult { Verdict = BuildingReasonAdmitted, Plan = id, Tier = tier.Name.Value };
    }

    internal static (List<DomainAction> Actions, bool Pending, HashSet<Cell> Lost) DefenseRemovalActions(PlanId id, DefenseTierRecord tier, DefenseCensus census)
    {
        var actions = new List<DomainAction>();
        var pending = false;
        var lost = new HashSet<Cell>();
        foreach (var b in tier.Buildings)
        {
            if (!census.Standing(b.Definition, b.Cell) || actions.Count == MaxDefenseRemovals)
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
                if (cover is null || cover.DefName != b.Definition || cover.Designation != CoverClearanceDesignation.Deconstruct)
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
