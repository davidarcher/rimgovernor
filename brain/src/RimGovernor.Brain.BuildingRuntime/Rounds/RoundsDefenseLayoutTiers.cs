using System.Globalization;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsDefenseLayoutPlanner
{
    internal const string DefenseTurretReplacePrefix = "turrets-replace-";
    internal const long DefenseReverifyTicks = 2500;

    internal static string DefenseTurretRung(RoundsReading read)
    {
        var facts = read.Projection.Facts;
        foreach (var rung in DefenseTurretsPolicy.TurretRungs(ArmoryRules.AssessArmory(facts.RaidPoints, facts.DefenseCapacity, facts.Research).Tier))
        {
            if (DefenseDefinitionAvailable(read, rung))
            {
                return rung;
            }
        }
        return DefenseTurretsPolicy.TurretMini;
    }

    internal static Bounds DefenseTurretSize(RoundsReading read, string definition)
    {
        foreach (var d in read.Projection.Definitions)
        {
            if (d.Name == definition)
            {
                d.Size.TryGetValue(out var size);
                return size;
            }
        }
        return new Bounds();
    }

    internal static bool DefenseTurretReplacing(DefenseLayoutRecord record) =>
        record.Tiers.Any(t => t.Name.Value.StartsWith(DefenseTurretReplacePrefix, StringComparison.Ordinal));

    internal static bool DefenseReplaceDue(DefenseLayoutRecord record, DefenseVerified verified, Tick tick)
    {
        var (turrets, _, ok) = record.Tier(DefenseTurretsPolicy.TierTurrets);
        return ok && turrets.Built && turrets.Buildings.Count > 0 && !DefenseTurretReplacing(record) && DefenseSitingDue(verified, tick);
    }

    internal async Task ReplaceTurretAsync(ControlState state, RoundsReading read, DefenseLayoutRecord record, CancellationToken call)
    {
        var projection = read.Projection;
        if (!projection.Facts.Hostiles.TryGetValue(out var hostiles) || hostiles > 0)
        {
            return;
        }
        var request = DefenseTurretRequest(read);
        var (tier, _, _) = record.Tier(DefenseTurretsPolicy.TierTurrets);
        var standing = new List<StandingTurret>();
        foreach (var b in tier.Buildings)
        {
            if (DefenseTurretsPolicy.TurretRank(b.Definition) < 0)
            {
                continue;
            }
            var building = new Building(b.Definition, b.Cell, b.Rotation, b.Stuff);
            standing.Add(new StandingTurret(building, [.. DefenseTurretsPolicy.TurretFootprint(b.Cell, DefenseTurretSize(read, b.Definition))]));
        }
        if (standing.Count == 0 || !standing.Any(t => DefenseTurretsPolicy.TurretRank(t.Building.Definition) < DefenseTurretsPolicy.TurretRank(request.Turret.Definition)))
        {
            return;
        }
        var identity = Boundary.Identity(state.Snapshot);
        var (killbox, region, home, ok) = DefenseKillbox(projection);
        if (!ok || !projection.LayoutPlan.TryGetValue(out var plan))
        {
            return;
        }
        var site = await DefenseSiteAsync(identity, projection, region, call).ConfigureAwait(false);
        request = request with { Bounds = projection.Bounds };
        request = request with { Home = home };
        request = request with { Killbox = killbox };
        request = request with { Region = new Rectangle(region.Min.X, region.Min.Z, region.Max.X - region.Min.X + 1, region.Max.Z - region.Min.Z + 1) };
        var requestCells = new List<DefenseCell>();
        foreach (var cell in site.Cells)
        {
            requestCells.Add(DefenseCellFacts(cell));
        }
        request = request with { Cells = [.. requestCells] };
        Building old;
        Building replacement;
        try
        {
            if (DefenseTurretsPolicy.TurretReplacement(request, DefenseRecordGeometry(record, plan, killbox), standing) is not { } found)
            {
                return;
            }
            old = found.Old;
            replacement = found.Replacement;
        }
        catch (DomainException)
        {
            return;
        }
        ApplyTurretReplacement(record, old, replacement, standing, request.Turret.Size);
        DefensePipeline.DefenseAction("defense-layout", TelemetryLevel.Info, "applied", "turret_replace", request.Turret.Definition, new Dictionary<string, object?> { ["x"] = old.Cell.X, ["z"] = old.Cell.Z }, call);
        _reviewer.Player.Journal.SaveDefenseLayout(record, call);
    }

    internal static void ApplyTurretReplacement(DefenseLayoutRecord record, Building old, Building replacement, IReadOnlyList<StandingTurret> standing, Bounds size)
    {
        var (tier, _, _) = record.Tier(DefenseTurretsPolicy.TierTurrets);
        for (var i = 0; i < tier.Buildings.Count; i++)
        {
            var b = tier.Buildings[i];
            if (b.Cell == old.Cell && b.Definition == old.Definition)
            {
                tier.Buildings[i] = new DefenseBuilding { Definition = replacement.Definition, Cell = replacement.Cell, Rotation = replacement.Rotation, Stuff = replacement.Stuff };
            }
        }
        tier.Reserved = [];
        foreach (var t in standing)
        {
            if (t.Building.Cell == old.Cell)
            {
                tier.Reserved.AddRange(DefenseTurretsPolicy.TurretFootprint(old.Cell, size));
                continue;
            }
            tier.Reserved.AddRange(t.Cells);
        }
        record.SetTier(tier);
        var name = string.Create(CultureInfo.InvariantCulture, $"{DefenseTurretReplacePrefix}{old.Cell.X}-{old.Cell.Z}-{DefenseTurretsPolicy.TurretRank(replacement.Definition)}");
        record.Tiers.Add(new DefenseTierRecord
        {
            Name = new DefenseTierName(name),
            Remove = true,
            Buildings = [new DefenseBuilding { Definition = old.Definition, Cell = old.Cell, Rotation = old.Rotation, Stuff = old.Stuff }],
        });
    }

    /// <summary>The conduit cells connected to the buildings of a power network, sorted by x then z.</summary>
    internal static List<Cell> DefenseNetworkConduits(PowerTopology topology, string network)
    {
        var conduit = new HashSet<Cell>(topology.Conduits);
        var seen = new HashSet<Cell>();
        var queue = new Queue<Cell>();
        var output = new List<Cell>();
        foreach (var b in topology.Buildings)
        {
            if (b.Network != network)
            {
                continue;
            }
            foreach (var cell in b.Occupied)
            {
                foreach (var c in topology.Conduits)
                {
                    if (!seen.Contains(c) && Math.Abs(c.X - cell.X) <= 6 && Math.Abs(c.Z - cell.Z) <= 6)
                    {
                        seen.Add(c);
                        queue.Enqueue(c);
                    }
                }
            }
        }
        Cell[] directions = [new Cell(1, 0), new Cell(-1, 0), new Cell(0, 1), new Cell(0, -1)];
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            output.Add(c);
            foreach (var d in directions)
            {
                var n = new Cell(c.X + d.X, c.Z + d.Z);
                if (conduit.Contains(n) && seen.Add(n))
                {
                    queue.Enqueue(n);
                }
            }
        }
        output.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Z.CompareTo(b.Z));
        return output;
    }

    /// <summary>The record's tier cells, with the firing line and the trap lane of the plan's DefFiring and DefLane nodes (#3583).</summary>
    internal static DefenseGeometry DefenseRecordGeometry(DefenseLayoutRecord record, LayoutPlan plan, DefenseKillbox killbox)
    {
        var tiers = plan.DefenseTierGeometry();
        var reserved = new List<Cell>();
        var walls = new List<Cell>();
        foreach (var tier in record.Tiers)
        {
            if (tier.Name == DefenseTurretsPolicy.TierTurrets || LayoutDefenseRules.IsPerimeterTier(tier.Name) || tier.Name.Value.StartsWith(DefenseTurretReplacePrefix, StringComparison.Ordinal))
            {
                continue;
            }
            reserved.AddRange(tier.Reserved);
            foreach (var b in tier.Buildings)
            {
                reserved.Add(b.Cell);
                if (tier.Name == DefenseLayout.TierFunnel)
                {
                    walls.Add(b.Cell);
                }
            }
        }
        return new DefenseGeometry
        {
            Entry = killbox.Entry,
            Approach = tiers.TrapLane,
            Toward = killbox.Toward,
            Firing = [.. tiers.Firing.Select(f => f.Cell)],
            Lanes = tiers.TrapLane,
            Reserved = [.. reserved],
            Walls = [.. walls],
        };
    }

    internal static bool DefenseReverifyDue(DefenseVerified verified, Tick tick, string combat) =>
        combat != verified.Combat || tick.Value - verified.Tick.Value >= DefenseReverifyTicks;

    /// <summary>The plan of the map's latest fight (<c>engage-&lt;map&gt;</c>'s latest attempt), or empty when there is none.</summary>
    internal static string DefenseCombatKey(Player p, RoundsReview review, CancellationToken ct)
    {
        var owner = p.Journal.LoadNeed(MilitaryFamily.Combat, review.Snapshot, ct);
        return p.Journal.OwnerMethodPlan(owner, RoundsDefensePlanner.EngageMethod(review.Snapshot), ct)?.Value ?? "";
    }

    internal static bool DefenseTierCensusUpdate(DefenseLayoutRecord record, DefenseCensus census)
    {
        var gone = record.Tiers.Count;
        record.Tiers.RemoveAll(t => t.Remove && DefenseRemovalGone(t, census));
        var changed = gone != record.Tiers.Count;
        foreach (var tier in record.Tiers.ToList())
        {
            if (tier.Buildings.Count == 0 || tier.Remove)
            {
                continue;
            }
            var standing = true;
            foreach (var b in tier.Buildings)
            {
                standing = standing && census.Standing(b.Definition, b.Cell);
            }
            if (standing == tier.Built)
            {
                continue;
            }
            tier.Built = standing;
            record.SetTier(tier);
            changed = true;
        }
        return changed;
    }

    internal async Task<DefenseCensus?> ObserveTiersAsync(ControlState state, RoundsReading read, DefenseLayoutRecord record, CancellationToken call)
    {
        var placed = false;
        foreach (var tier in record.Tiers)
        {
            placed = placed || tier.Buildings.Count > 0;
        }
        if (!placed)
        {
            return null;
        }
        var projection = read.Projection;
        var census = new DefenseCensus();
        var (_, baseCell, _, ok) = DefenseKillbox(projection);
        if (!ok)
        {
            return null;
        }
        var site = await DefenseSiteAsync(Boundary.Identity(state.Snapshot), projection, DefenseCensusRegion(record, projection.Bounds, baseCell), call).ConfigureAwait(false);
        foreach (var cell in site.Cells)
        {
            census.Fogged[cell.Cell] = cell.Fogged;
            if (!cell.Fogged)
            {
                census.Edifice[cell.Cell] = cell.EdificeDefName;
                census.Terrain[cell.Cell] = cell.Terrain;
                census.Cover[cell.Cell] = cell.Cover;
                census.Unbridging[cell.Cell] = cell.Unbridging;
            }
        }
        if (projection.PowerPlanning.TryGetValue(out var topology))
        {
            foreach (var c in topology.Conduits)
            {
                census.Conduits[c] = true;
            }
            foreach (var b in topology.Buildings)
            {
                census.Consumers[b.Cell] = b;
            }
        }
        LogUnbuiltTiers(record, census, call);
        if (!DefenseTierCensusUpdate(record, census))
        {
            return census;
        }
        _reviewer.Player.Journal.SaveDefenseLayout(record, call);
        return census;
    }

    internal static List<Building> DefenseMissingBuildings(List<Building> buildings, DefenseCensus? census)
    {
        if (census is null)
        {
            return buildings;
        }
        return buildings.Where(b => !census.Standing(b.Definition, b.Cell)).ToList();
    }

    internal async Task<(DefenseLayout Layout, List<Cell> Entrances, Verdict Held, bool Ok)> ProposeAsync(NeedOwner goal, RoundsReview review, ControlState state, RoundsReading read, CancellationToken call, CancellationToken epoch)
    {
        var projection = read.Projection;
        var (killbox, region, home, ok) = DefenseKillbox(projection);
        if (!ok)
        {
            return (new DefenseLayout(), [], AwaitingPlan("layout_plan", "killbox"), false);
        }
        var (request, held, cok) = await DefenseCensusRequestAsync(state, read, killbox, region, home, call).ConfigureAwait(false);
        if (!cok)
        {
            return (new DefenseLayout(), [], held, false);
        }
        IReadOnlyList<RoleCell>? rock = null;
        try
        {
            rock = DefenseLayout.DefenseRockCells(request);
        }
        catch (DomainException)
        {
            // No rock to dig: the killbox is laid out on the open ground.
        }
        if (rock is not null)
        {
            var (result, handled) = await DigKillboxAsync(goal, review, state, read, request.Home, rock, call, epoch).ConfigureAwait(false);
            if (handled)
            {
                return (new DefenseLayout(), [], result.Verdict, false);
            }
        }
        DefenseRecorder.RecordLayoutSnapshot(state.Snapshot, projection.Identity.Tick, new SnapshotLayout { Point = SnapshotLayoutReplay.LayoutPropose, Request = request }, call);
        DefenseLayout layout;
        try
        {
            layout = DefenseLayout.DefenseLayouts(request);
        }
        catch (DomainException)
        {
            return (new DefenseLayout(), [], NoSpace("defense_layout"), false);
        }
        if (layout.Firing.Count == 0)
        {
            return (new DefenseLayout(), [], NoSpace("firing_cell"), false);
        }
        foreach (var g in layout.Gated)
        {
            DefensePipeline.DefenseTierGated(g.Tier.Value, g.Reason, call);
        }
        return (layout, request.Entrances.ToList(), default, true);
    }

    internal async Task<(RoundsBuildingResult Result, bool Handled)> DigKillboxAsync(NeedOwner goal, RoundsReview review, ControlState state, RoundsReading read, Cell access, IReadOnlyList<RoleCell> planned, CancellationToken call, CancellationToken epoch)
    {
        if (_native is not FacadeNativeClient source)
        {
            return (new RoundsBuildingResult(), false);
        }
        var dig = new RoundsBuildingPlanner { Reviewer = _reviewer, Concern = LayoutFamily.Defense.FilingKey, Need = LayoutFamily.Defense, Excavation = source };
        var step = new ExcavationStep { State = state, Review = review, Owner = goal, Facts = read.Projection, Read = read.Reading };
        var p = _reviewer.Player;
        void Check()
        {
            p.Current(call, epoch);
            if (p.Session.State() != state)
            {
                throw new ControlException("digKillbox: session state changed");
            }
        }
        return await dig.AdmitRockStepAsync(step, planned, access, new MethodId("plan-dig-killbox"), [], Check, call, epoch).ConfigureAwait(false);
    }
}

partial class RoundsDefenseLayoutPlanner
{
    /// <summary>
    /// The layout/defense Need's facts (#3314): the stored layout of this world, whether the step verified it complete
    /// in this process (the Rounder's in-memory verification, #3008) at the map's latest fight within the re-verify
    /// window, and whether an open fight waits in rooms to harden. Combat is read as facts only; no combat Need gates
    /// the layout.
    /// </summary>
    internal static DefenseLayoutFacts DefenseLayoutFactsOf(Rounder reviewer, RoundsReview review, CancellationToken ct)
    {
        var p = reviewer.Player;
        var world = new StoreWorld { Colony = review.Snapshot.Colony, Load = review.Snapshot.Load, Map = review.Snapshot.Map };
        var record = p.Journal.LoadDefenseLayout(world, ct);
        var current = record is not null && record.Killbox != "" && record.World == world;
        var verified = reviewer.DefenseVerification.Get(world);
        var combat = RoundsDefenseLayoutPlanner.DefenseCombatKey(p, review, ct);
        return new DefenseLayoutFacts
        {
            Standing = current && verified.Complete && record!.AllBuilt(),
            Verified = current && combat != "" && verified.Complete && !RoundsDefenseLayoutPlanner.DefenseReverifyDue(verified, review.Tick, combat),
            WaitingFight = DefenseWaitingFight(p.Journal, world, ct) is not null,
        };
    }
}
