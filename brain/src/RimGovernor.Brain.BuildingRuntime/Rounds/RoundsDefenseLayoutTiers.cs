using System.Globalization;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsDefenseLayoutPlanner
{
    internal const string DefenseTurretReplacePrefix = "turrets-replace-";
    internal const long DefenseReverifyTicks = 2500;

    internal static string DefenseTurretRung(RoundsReading read)
    {
        var facts = read.Projection.Facts;
        foreach (var rung in PolicyApi.TurretRungs(PolicyApi.AssessArmory(facts.RaidPoints, facts.Research).Tier))
        {
            if (DefenseDefinitionAvailable(read, rung))
            {
                return rung;
            }
        }
        return PolicyApi.TurretMini;
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
        record.Tiers.Any(t => t.Name.StartsWith(DefenseTurretReplacePrefix, StringComparison.Ordinal));

    internal static bool DefenseReplaceDue(DefenseLayoutRecord record, Tick tick)
    {
        var (turrets, _, ok) = record.Tier(PolicyApi.TierTurrets);
        return ok && turrets.Built && turrets.Buildings.Count > 0 && !DefenseTurretReplacing(record)
            && (record.TurretsProbedTick == default || tick.Value - record.TurretsProbedTick.Value >= DefenseReverifyTicks);
    }

    internal void ReplaceTurret(CancellationToken call, ControlState state, RoundsReading read, DefenseLayoutRecord record)
    {
        var projection = read.Projection;
        var journal = _reviewer.Player.Journal;
        record.TurretsProbedTick = projection.Identity.Tick;
        if (!projection.Facts.Hostiles.TryGetValue(out var hostiles) || hostiles > 0)
        {
            journal.SaveDefenseLayout(record, call);
            return;
        }
        var request = DefenseTurretRequest(read);
        var (tier, _, _) = record.Tier(PolicyApi.TierTurrets);
        var standing = new List<StandingTurret>();
        foreach (var b in tier.Buildings)
        {
            if (PolicyApi.TurretRank(b.Definition) < 0)
            {
                continue;
            }
            var building = DomainApi.NewBuilding(b.Definition, b.Cell, b.Rotation, b.Stuff);
            standing.Add(new StandingTurret { Building = building, Cells = PolicyApi.TurretFootprint(b.Cell, DefenseTurretSize(read, b.Definition)) });
        }
        if (standing.Count == 0 || !standing.Any(t => PolicyApi.TurretRank(t.Building.Definition) < PolicyApi.TurretRank(request.Turret.Definition)))
        {
            journal.SaveDefenseLayout(record, call);
            return;
        }
        var identity = Boundary.Identity(state.Snapshot);
        var (killbox, region, home, ok) = DefenseKillbox(projection);
        if (!ok)
        {
            journal.SaveDefenseLayout(record, call);
            return;
        }
        var site = DefenseSite(call, identity, projection, region);
        request.Bounds = projection.Bounds;
        request.Home = home;
        request.Killbox = killbox;
        request.Region = new Rectangle(region.Min.X, region.Min.Z, region.Max.X - region.Min.X + 1, region.Max.Z - region.Min.Z + 1);
        foreach (var cell in site.Cells)
        {
            request.Cells.Add(DefenseCellFacts(cell));
        }
        Building old;
        Building replacement;
        try
        {
            var (o, r, found) = PolicyApi.TurretReplacement(request, DefenseRecordGeometry(record, killbox), standing);
            if (!found)
            {
                journal.SaveDefenseLayout(record, call);
                return;
            }
            old = o;
            replacement = r;
        }
        catch (DomainException)
        {
            journal.SaveDefenseLayout(record, call);
            return;
        }
        ApplyTurretReplacement(record, old, replacement, standing, request.Turret.Size);
        DefenseAction(call, "defense-layout", LogLevel.Info, "applied", "turret_replace", request.Turret.Definition, new Dictionary<string, object?> { ["x"] = old.Cell.X, ["z"] = old.Cell.Z });
        journal.SaveDefenseLayout(record, call);
    }

    internal static void ApplyTurretReplacement(DefenseLayoutRecord record, Building old, Building replacement, IReadOnlyList<StandingTurret> standing, Bounds size)
    {
        var (tier, _, _) = record.Tier(PolicyApi.TierTurrets);
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
                tier.Reserved.AddRange(PolicyApi.TurretFootprint(old.Cell, size));
                continue;
            }
            tier.Reserved.AddRange(t.Cells);
        }
        record.SetTier(tier);
        var name = string.Create(CultureInfo.InvariantCulture, $"{DefenseTurretReplacePrefix}{old.Cell.X}-{old.Cell.Z}-{PolicyApi.TurretRank(replacement.Definition)}");
        record.Tiers.Add(new DefenseTierRecord
        {
            Name = name,
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
            if (!b.Network.TryGetValue(out var id) || id != network)
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

    internal static DefenseGeometry DefenseRecordGeometry(DefenseLayoutRecord record, DefenseKillbox killbox)
    {
        var g = new DefenseGeometry
        {
            Entry = killbox.Entry,
            Approach = record.TrapLane.ToList(),
            Toward = killbox.Toward,
            Firing = record.Firing.ToList(),
            Lanes = record.TrapLane.ToList(),
        };
        foreach (var tier in record.Tiers)
        {
            if (tier.Name == PolicyApi.TierTurrets || PolicyApi.IsPerimeterTier(tier.Name) || tier.Name.StartsWith(DefenseTurretReplacePrefix, StringComparison.Ordinal))
            {
                continue;
            }
            g.Reserved.AddRange(tier.Reserved);
            foreach (var b in tier.Buildings)
            {
                g.Reserved.Add(b.Cell);
                if (tier.Name == PolicyApi.TierFunnel)
                {
                    g.Walls.Add(b.Cell);
                }
            }
        }
        return g;
    }

    internal static bool DefenseReverifyDue(DefenseLayoutRecord record, Tick tick, string combat) =>
        combat != record.VerifiedCombat || tick.Value - record.VerifiedTick.Value >= DefenseReverifyTicks;

    /// <summary>The id of the latest active-combat incident, or empty when there is none.</summary>
    internal static string DefenseCombatKey(CancellationToken ct, Player p, RoundsReview review)
    {
        var s = review.Snapshot;
        var (latest, ok) = p.Journal.LatestIncident(new StoreWorld { Colony = s.Colony, Load = s.Load, Map = s.Map }, PolicyApi.ActiveCombat, ct);
        return ok ? latest.Incident.Id.Value : "";
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
            if (!standing)
            {
                tier.Attempts = 0;
                tier.Reopened++;
            }
            record.SetTier(tier);
            changed = true;
        }
        return changed;
    }

    internal DefenseCensus? ObserveTiers(CancellationToken call, ControlState state, RoundsReading read, DefenseLayoutRecord record)
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
        var site = DefenseSite(call, Boundary.Identity(state.Snapshot), projection, DefenseCensusRegion(record, projection.Bounds, baseCell));
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
        LogUnbuiltTiers(call, record, census);
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

    internal (DefenseLayout Layout, List<Cell> Entrances, Verdict Held, bool Ok) Propose(CancellationToken call, CancellationToken epoch, StandardState goal, RoundsReview review, ControlState state, RoundsReading read)
    {
        var projection = read.Projection;
        var (killbox, region, home, ok) = DefenseKillbox(projection);
        if (!ok)
        {
            return (new DefenseLayout(), [], AwaitingPlan("layout_plan", "killbox"), false);
        }
        var (request, held, cok) = DefenseCensusRequest(call, state, read, killbox, region, home);
        if (!cok)
        {
            return (new DefenseLayout(), [], held, false);
        }
        IReadOnlyList<RoleCell>? rock = null;
        try
        {
            rock = PolicyApi.DefenseRockCells(request);
        }
        catch (DomainException)
        {
            // No rock to dig: the killbox is laid out on the open ground.
        }
        if (rock is not null)
        {
            var (result, handled) = DigKillbox(call, epoch, goal, review, state, read, request.Home, rock);
            if (handled)
            {
                return (new DefenseLayout(), [], result.Verdict, false);
            }
        }
        RecordLayoutSnapshot(call, state.Snapshot, projection.Identity.Tick, new SnapshotLayout { Point = SnapshotApi.LayoutPropose, Request = request });
        DefenseLayout layout;
        try
        {
            layout = PolicyApi.DefenseLayouts(request);
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
            DefenseTierGated(call, g.Tier, g.Reason);
        }
        return (layout, request.Entrances.ToList(), default, true);
    }

    internal (RoundsBuildingResult Result, bool Handled) DigKillbox(CancellationToken call, CancellationToken epoch, StandardState goal, RoundsReview review, ControlState state, RoundsReading read, Cell access, IReadOnlyList<RoleCell> planned)
    {
        if (_native is not IRoundsExcavationSource source)
        {
            return (new RoundsBuildingResult(), false);
        }
        var dig = new RoundsBuildingPlanner { Reviewer = _reviewer, Concern = PolicyApi.EnsureDefensiveLayout, Excavation = source };
        var step = new ExcavationStep { State = state, Review = review, Owner = goal, Facts = read.Projection, Read = read.ColonyReading };
        var p = _reviewer.Player;
        void Check()
        {
            p.Current(call, epoch);
            if (p.Session.State() != state)
            {
                throw new ControlException("digKillbox: session state changed");
            }
        }
        return dig.AdmitRockStep(call, epoch, step, planned, access, "plan-dig-killbox", null, Check);
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>Whether the colony's defensive layout stands, as a fact; unknown when the policy does not ask for one.</summary>
    internal static Fact<bool> RoundsDefensiveLayoutStanding(CancellationToken ct, StoreJournal journal, RoundsPolicy p, GenerationSnapshot snapshot)
    {
        if (!p.DefensiveLayout)
        {
            return Unknown<bool>();
        }
        var world = new StoreWorld { Colony = snapshot.Colony, Load = snapshot.Load, Map = snapshot.Map };
        var (record, stored) = journal.LoadDefenseLayout(world, ct);
        if (!stored || record.World != world)
        {
            return Known(false);
        }
        return Known(record.Standing());
    }
}
