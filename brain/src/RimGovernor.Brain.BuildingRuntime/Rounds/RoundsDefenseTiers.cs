using System.Text;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The tier nodes the latest siting produced, and the key that says whether they changed.</summary>
internal sealed class TierOverlay
{
    public string Key = "";
    public IReadOnlyList<PlannedSite> Nodes = [];
}

public sealed partial class RoundsDefenseLayoutPlanner
{
    internal static readonly string[] DefenseKillboxTiers =
    [
        PolicyApi.TierChokepoint, PolicyApi.TierFiringLine, PolicyApi.TierFunnel, PolicyApi.TierTrapCorridor,
        PolicyApi.TierTurrets, PolicyApi.TierMortars, PolicyApi.TierBait, PolicyApi.TierIeds,
    ];

    /// <summary>The policy request of the site census: the turret and mortar budgets, the defenders, the cover choice and the IEDs.</summary>
    internal (DefenseRequest Request, Verdict Held, bool Ok) DefenseCensusRequest(CancellationToken call, ControlState state, RoundsReading read, DefenseKillbox killbox, BridgeCellRect region, Cell home)
    {
        var projection = read.Projection;
        var identity = BoundaryApi.Identity(state.Snapshot);
        var site = DefenseSite(call, identity, projection, region);
        var request = DefenseTurretRequest(read);
        var mortar = DefenseMortarRequest(read);
        request.Mortar = mortar.Mortar;
        request.UnitCosts[DefenseMortarDefinition] = mortar.UnitCosts.GetValueOrDefault(DefenseMortarDefinition)!;
        request.Bounds = projection.Bounds;
        request.Home = home;
        request.Killbox = killbox;
        request.Arrivals = DefenseArrivals(projection.Raids, region);
        request.Tick = projection.Identity.Tick;
        request.Region = new Rectangle(region.Min.X, region.Min.Z, region.Max.X - region.Min.X + 1, region.Max.Z - region.Min.Z + 1);
        foreach (var cell in site.Cells)
        {
            request.Cells.Add(DefenseCellFacts(cell));
            if (!cell.Fogged && cell.Door && cell.PlayerOwned)
            {
                request.Entrances.Add(cell.Cell);
            }
        }
        var (defenders, minRange, ok) = DefenderRange(call, state, read);
        if (!ok)
        {
            return (new DefenseRequest(), NoWorker("ranged_defender"), false);
        }
        request.Defenders = defenders;
        request.MinRange = minRange;
        var stockKnown = projection.Resources.TryGetValue(out var stock);
        request.Definitions = PolicyApi.DefenseCoverChoice(projection.Facts.Items, request.Definitions, stock, stockKnown,
            DefenseDefinitionAvailable(read, PolicyApi.DefenseSandbags), DefenseDefinitionAvailable(read, PolicyApi.DefenseEmbrasure), defenders);
        DefenseIedRequest(read, request);
        MarkWallSupport(call, identity, request, site.Cells);
        return (request, default, true);
    }

    internal static bool DefenseTierStarted(DefenseTierRecord t, DefenseCensus? census)
    {
        if (t.Built || t.Attempts > 0 || t.Reopened > 0)
        {
            return true;
        }
        return census is not null && t.Buildings.Any(b => census.Standing(b.Definition, b.Cell));
    }

    internal static DefenseTierPins DefenseTierPins(DefenseLayoutRecord record, DefenseCensus? census)
    {
        var pins = new DefenseTierPins { TrapLane = record.TrapLane.ToList() };
        for (var i = 0; i < record.Firing.Count; i++)
        {
            var cell = record.Firing[i];
            var position = new FiringPosition { Cell = cell, Cover = cell };
            if (record.Retreat.Count == record.Firing.Count)
            {
                position.Retreat = record.Retreat[i];
            }
            pins.Firing.Add(position);
        }
        foreach (var t in record.Tiers)
        {
            if (!DefenseTierStarted(t, census) || t.Remove || t.NodeBacked() || PolicyApi.IsPerimeterTier(t.Name))
            {
                continue;
            }
            var (_, buildings, ok) = record.Tier(t.Name);
            if (!ok)
            {
                continue;
            }
            pins.Tiers[t.Name] = new DefenseTier { Name = t.Name, Buildings = buildings.ToList(), Reserved = t.Reserved.ToList() };
        }
        var (choke, _, cok) = record.Tier(PolicyApi.TierChokepoint);
        if (cok && pins.Tiers.Count > 0)
        {
            pins.Tiers[PolicyApi.TierChokepoint] = new DefenseTier { Name = PolicyApi.TierChokepoint, Reserved = choke.Reserved.ToList() };
        }
        return pins;
    }

    /// <summary>Applies the newly sited tiers to the record for every tier not pinned by work already started; true when the siting changed.</summary>
    internal static bool SiteUnstartedTiers(DefenseLayoutRecord record, DefenseTiers tiers, DefenseTierPins pins)
    {
        if (tiers.Refusal != "" || tiers.Layout.Firing.Count == 0)
        {
            return false;
        }
        var before = SitingSnapshot.Of(record);
        foreach (var name in DefenseKillboxTiers)
        {
            if (pins.Tiers.ContainsKey(name))
            {
                continue;
            }
            var sited = tiers.Layout.Tiers.FindIndex(t => t.Name == name);
            var old = record.Tiers.FindIndex(t => t.Name == name);
            if (sited < 0 && old >= 0)
            {
                record.Tiers.RemoveAt(old);
            }
            else if (sited >= 0)
            {
                record.SetPolicyTier(tiers.Layout.Tiers[sited]);
            }
        }
        if (!pins.Tiers.ContainsKey(PolicyApi.TierChokepoint))
        {
            record.TrapLane = tiers.Layout.TrapLane.ToList();
        }
        if (!pins.Tiers.ContainsKey(PolicyApi.TierFiringLine))
        {
            record.Firing = [];
            record.Retreat = [];
            foreach (var f in tiers.Layout.Firing)
            {
                record.Firing.Add(f.Cell);
                record.Retreat.Add(f.Retreat);
            }
        }
        return !before.Same(SitingSnapshot.Of(record));
    }

    private sealed class SitingSnapshot
    {
        public List<Cell> TrapLane = [];
        public List<Cell> Firing = [];
        public List<Cell> Retreat = [];
        public List<DefenseTierRecord> Tiers = [];

        public static SitingSnapshot Of(DefenseLayoutRecord r) => new()
        {
            TrapLane = r.TrapLane.ToList(),
            Firing = r.Firing.ToList(),
            Retreat = r.Retreat.ToList(),
            Tiers = r.Tiers.Select(t => t.Clone()).ToList(),
        };

        public bool Same(SitingSnapshot b)
        {
            if (!TrapLane.SequenceEqual(b.TrapLane) || !Firing.SequenceEqual(b.Firing) || !Retreat.SequenceEqual(b.Retreat) || Tiers.Count != b.Tiers.Count)
            {
                return false;
            }
            foreach (var t in Tiers)
            {
                var i = b.Tiers.FindIndex(u => u.Name == t.Name);
                if (i < 0)
                {
                    return false;
                }
                var u = b.Tiers[i];
                if (!t.Buildings.SequenceEqual(u.Buildings) || !t.Reserved.SequenceEqual(u.Reserved) || t.Attempts != u.Attempts || t.Built != u.Built || t.Reopened != u.Reopened)
                {
                    return false;
                }
            }
            return true;
        }
    }

    internal void SiteTiers(CancellationToken call, ControlState state, RoundsReading read, DefenseLayoutRecord record, DefenseCensus? tierCensus)
    {
        if (!read.Projection.LayoutPlan.TryGetValue(out var plan))
        {
            return;
        }
        var (killbox, region, home, ok) = DefenseKillbox(read.Projection);
        if (!ok)
        {
            return;
        }
        var (census, _, cok) = DefenseCensusRequest(call, state, read, killbox, region, home);
        if (!cok)
        {
            return;
        }
        var pins = DefenseTierPins(record, tierCensus);
        DefenseTiers tiers;
        try
        {
            tiers = PolicyApi.PlanDefenseTiers(plan, census, pins);
        }
        catch (DomainException)
        {
            return;
        }
        SiteUnstartedTiers(record, tiers, pins);
        foreach (var g in tiers.Layout.Gated)
        {
            DefenseTierGated(call, g.Tier, g.Reason);
        }
        AdviseBlocks(call, state, read, census, pins, tiers);
        var key = TierKey(tiers.Nodes);
        _reviewer.Tiers.Store(new TierOverlay { Key = key, Nodes = tiers.Nodes });
        if (key == TiersLogged)
        {
            return;
        }
        TiersLogged = key;
        foreach (var d in tiers.Decisions)
        {
            TelemetryApi.Decide(call, new Decision
            {
                Kind = "layout_plan",
                Component = "layout",
                Level = LogLevel.Info,
                Verdict = d.Verdict,
                Reason = d.Reason,
                Target = "defense_tiers",
                Attrs = new Dictionary<string, object?> { ["node"] = d.Node, ["nodes"] = tiers.Nodes.Count, ["why"] = tiers.Refusal },
            });
        }
    }

    internal void AdviseBlocks(CancellationToken call, ControlState state, RoundsReading read, DefenseRequest census, DefenseTierPins pins, DefenseTiers tiers)
    {
        var things = PolicyApi.BlockedThings(census.Cells, pins);
        if (things.Count == 0)
        {
            BlocksLogged = "";
            return;
        }
        DefinitionCatalog? catalog;
        try
        {
            catalog = PawnCatalog(call, _native, BoundaryApi.Identity(state.Snapshot));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return;
        }
        if (catalog is null)
        {
            return;
        }
        var rows = new Dictionary<string, ClearRow>(StringComparer.Ordinal);
        foreach (var thing in things)
        {
            try
            {
                var (row, found) = catalog.ClearRow(thing.Def, thing.Stuff);
                if (found)
                {
                    rows[PolicyApi.ClearKey(thing.Def, thing.Stuff)] = row;
                }
            }
            catch (DomainException)
            {
                // A thing without a priced clear row stays unpriced.
            }
        }
        var p = read.Projection;
        var facts = new StationFacts { Catalog = p.Stations, Items = p.Facts.Items, Colonists = p.Facts.Colonists, Food = p.FoodSupply, WorkPawns = p.WorkPawns, Crops = GrowingCrops(p) };
        var decisions = PolicyApi.PriceTierBlocks(census.Cells, pins, tiers.Sited, PolicyApi.NewBlockPricing(facts, census.UnitCosts, rows));
        var key = string.Join(" ", decisions.Select(d => d.ToString()));
        if (key == BlocksLogged)
        {
            return;
        }
        BlocksLogged = key;
        foreach (var d in decisions)
        {
            TelemetryApi.Decide(call, new Decision
            {
                Kind = "layout_plan",
                Component = "layout",
                Level = LogLevel.Info,
                Verdict = d.Choice.ToString(),
                Reason = d.Reason,
                Target = "defense_tier_block",
                Attrs = new Dictionary<string, object?>
                {
                    ["tier"] = d.Tier,
                    ["blocked"] = d.Blocked.Count,
                    ["clear_w"] = d.ClearW,
                    ["clear_known"] = d.ClearKnown,
                    ["move_w"] = d.MoveW,
                    ["move_known"] = d.MoveKnown,
                    ["downtime"] = "unpriced",
                },
            });
        }
    }

    internal static string TierKey(IReadOnlyList<PlannedSite> nodes)
    {
        var key = new StringBuilder();
        foreach (var n in nodes)
        {
            key.Append(n.Node).Append('/').Append(n.Tier is not null && n.Tier.Pinned).Append('/').Append(string.Join(",", n.Footprint.Runs)).Append(';');
        }
        return key.ToString();
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>The cover the cell's thing offers: its definition and kind, and for a building the stuff when it is the cell's edifice.</summary>
    internal static CoverThing DefenseCover(BridgeDefenseCell cell)
    {
        if (cell.Cover is null)
        {
            return new CoverThing();
        }
        var thing = new CoverThing { Def = cell.Cover.DefName };
        switch (cell.Cover.Kind)
        {
            case O.CoverKind.Plant:
                thing.Kind = CoverKind.Plant;
                break;
            case O.CoverKind.Chunk:
                thing.Kind = CoverKind.Chunk;
                break;
            case O.CoverKind.Mineable:
                thing.Kind = CoverKind.Mineable;
                break;
            case O.CoverKind.Building:
                thing.Kind = CoverKind.Building;
                if (cell.EdificeDefName == thing.Def)
                {
                    thing.Stuff = cell.EdificeStuff;
                }
                break;
        }
        return thing;
    }
}
