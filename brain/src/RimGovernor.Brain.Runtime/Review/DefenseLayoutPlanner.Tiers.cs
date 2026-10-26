using System.Globalization;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Runtime;

public sealed partial class DefenseLayoutPlanner
{
    internal const string DefenseTurretReplacePrefix = "turrets-replace-";
    internal const long DefenseReverifyTicks = 2500;

    internal static string DefenseTurretRung(ReviewReading read)
    {
        var facts = read.Reading.Facts;
        foreach (var rung in DefenseTurretsPolicy.TurretRungs(ArmoryRules.AssessArmory(facts).Tier))
        {
            if (DefenseDefinitionAvailable(read, rung))
            {
                return rung;
            }
        }
        return DefenseTurretsPolicy.TurretMini;
    }

    internal static Bounds DefenseTurretSize(ReviewReading read, string definition)
    {
        foreach (var d in read.Reading.Facts.Definitions)
        {
            if (d.Name == definition)
            {
                var size = d.Size;
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

    /// <summary>Replaces one standing turret below the current rung on plan and returns the plan the step holds after: the one recorded with the replacement, else plan.</summary>
    internal async Task<LayoutPlan> ReplaceTurretAsync(ControlState state, ReviewReading read, DefenseLayoutRecord record, LayoutPlan plan, CancellationToken call)
    {
        var projection = read.Reading;
        if (projection.Facts.Hostiles is not HostileCensus.Quiet)
        {
            return plan;
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
            return plan;
        }
        var identity = Boundary.Identity(state.Snapshot);
        var (killbox, region, home, ok) = DefenseKillbox(projection);
        if (!ok)
        {
            return plan;
        }
        var site = await DefenseSiteAsync(identity, projection, region, call).ConfigureAwait(false);
        request = request with { Bounds = projection.Facts.MapBounds };
        request = request with { Home = home };
        request = request with { Killbox = killbox };
        request = request with { Region = new Rectangle(region.Min.X, region.Min.Z, region.Max.X - region.Min.X + 1, region.Max.Z - region.Min.Z + 1) };
        var (requestCells, fogged) = await DefenseSiteCellsAsync(identity, request.Definitions, site, call).ConfigureAwait(false);
        request = request with { Cells = [.. requestCells], Fogged = [.. fogged] };
        Building old;
        Building replacement;
        try
        {
            if (DefenseTurretsPolicy.TurretReplacement(request, DefenseRecordGeometry(record, plan, killbox), standing) is not { } found)
            {
                return plan;
            }
            old = found.Old;
            replacement = found.Replacement;
        }
        catch (DomainException)
        {
            return plan;
        }
        var replaced = ApplyTurretReplacement(record, plan, old, replacement, standing, request.Turret.Size);
        DefensePipeline.DefenseAction("defense-layout", TelemetryLevel.Info, "applied", "turret_replace", request.Turret.Definition, new Dictionary<string, object?> { ["x"] = old.Cell.X, ["z"] = old.Cell.Z }, call);
        _reviewer.Player.Journal.SaveDefenseLayout(record, call);
        _reviewer.Player.Journal.RecordLayoutPlan(state.Snapshot, projection.Identity.Tick, replaced, call);
        return replaced;
    }

    /// <summary>
    /// Replaces old by replacement in the record's turret tier and in the plan's turret node, whose reservation becomes
    /// the turrets' footprints by definition size (the replacement's at its own size), and queues old's removal.
    /// Returns the plan with the replacement.
    /// </summary>
    internal static LayoutPlan ApplyTurretReplacement(DefenseLayoutRecord record, LayoutPlan plan, Building old, Building replacement, IReadOnlyList<StandingTurret> standing, Bounds size)
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
        var reserved = new List<Cell>();
        foreach (var t in standing)
        {
            if (t.Building.Cell == old.Cell)
            {
                reserved.AddRange(DefenseTurretsPolicy.TurretFootprint(old.Cell, size));
                continue;
            }
            reserved.AddRange(t.Cells);
        }
        record.SetTier(tier);
        var name = string.Create(CultureInfo.InvariantCulture, $"{DefenseTurretReplacePrefix}{old.Cell.X}-{old.Cell.Z}-{DefenseTurretsPolicy.TurretRank(replacement.Definition)}");
        record.Tiers.Add(new DefenseTierRecord
        {
            Name = new DefenseTierName(name),
            Remove = true,
            Buildings = [new DefenseBuilding { Definition = old.Definition, Cell = old.Cell, Rotation = old.Rotation, Stuff = old.Stuff }],
        });
        return plan.WithTurretReplaced(old, replacement, reserved);
    }

    /// <summary>
    /// The record's tier cells with the reserved cells of the plan's tier nodes (#3584), and the firing line and the
    /// trap lane of the plan's DefFiring and DefLane nodes (#3583).
    /// </summary>
    internal static DefenseGeometry DefenseRecordGeometry(DefenseLayoutRecord record, LayoutPlan plan, DefenseKillbox killbox)
    {
        var tiers = plan.DefenseTierGeometry();
        var planned = plan.PlannedDefenseTiers();
        var reserved = new List<Cell>();
        var walls = new List<Cell>();
        foreach (var tier in record.Tiers)
        {
            if (tier.Name == DefenseTurretsPolicy.TierTurrets || LayoutDefenseRules.IsPerimeterTier(tier.Name) || tier.Name.Value.StartsWith(DefenseTurretReplacePrefix, StringComparison.Ordinal))
            {
                continue;
            }
            if (planned.TryGetValue(tier.Name, out var nodes))
            {
                reserved.AddRange(nodes.Reserved);
            }
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
    internal static string DefenseCombatKey(Player p, ReviewStep review, CancellationToken ct)
    {
        var owner = p.Journal.LoadNeed(MilitaryFamily.Combat, review.Snapshot, ct);
        return p.Journal.OwnerMethodPlan(owner, DefensePlanner.EngageMethod(review.Snapshot), ct)?.Value ?? "";
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

    /// <summary>The census of the cells the tiers of <paramref name="scope"/> place; null when they place nothing or the plan has no killbox.</summary>
    internal async Task<DefenseCensus?> ReadTierCensusAsync(ControlState state, ReviewReading read, DefenseLayoutRecord scope, CancellationToken call)
    {
        if (!scope.Tiers.Any(t => t.Buildings.Count > 0))
        {
            return null;
        }
        var projection = read.Reading;
        var census = new DefenseCensus();
        var (_, baseCell, _, ok) = DefenseKillbox(projection);
        if (!ok)
        {
            return null;
        }
        var site = await DefenseSiteAsync(Boundary.Identity(state.Snapshot), projection, DefenseCensusRegion(scope, projection.Facts.MapBounds, baseCell), call).ConfigureAwait(false);
        foreach (var cell in site)
        {
            census.Fogged[cell.Cell] = cell is RimGovernor.Brain.GameFacts.Observations.DefenseCell.Fogged;
            if (cell is RimGovernor.Brain.GameFacts.Observations.DefenseCell.Seen seen)
            {
                census.Edifice[cell.Cell] = seen.Edifice?.DefName ?? "";
                census.EdificeStuff[cell.Cell] = seen.Edifice?.Stuff ?? "";
                census.Terrain[cell.Cell] = seen.Terrain;
                census.Cover[cell.Cell] = seen.Cover;
                census.Unbridging[cell.Cell] = seen.FoundationRemovalDesignated;
            }
        }
        foreach (var c in projection.Facts.EssentialInputs.Power.Conduits)
        {
            census.Conduits[c] = true;
        }
        foreach (var b in projection.Facts.EssentialInputs.Power.Buildings)
        {
            census.Consumers[b.Cell] = b;
        }
        return census;
    }

    /// <summary>Settles the record's tiers on the census: a finished removal leaves, a tier reads built when all of it stands.</summary>
    internal void SettleTierCensus(DefenseLayoutRecord record, DefenseCensus? census, CancellationToken call)
    {
        if (census is null)
        {
            return;
        }
        LogUnbuiltTiers(record, census, call);
        if (DefenseTierCensusUpdate(record, census))
        {
            _reviewer.Player.Journal.SaveDefenseLayout(record, call);
        }
    }

    internal static List<Building> DefenseMissingBuildings(List<Building> buildings, DefenseCensus? census)
    {
        if (census is null)
        {
            return buildings;
        }
        return buildings.Where(b => !census.Standing(b.Definition, b.Cell)).ToList();
    }

    /// <summary>
    /// Proposes the layout on plan's killbox: the layout, the colony doors, and plan with the layout's tiers as its tier
    /// nodes (#3584), which the caller records with the record.
    /// </summary>
    internal async Task<(DefenseLayout Layout, List<Cell> Entrances, LayoutPlan Sited, Verdict Held, bool Ok)> ProposeAsync(NeedOwner goal, ReviewStep review, ControlState state, ReviewReading read, LayoutPlan plan, CancellationToken call, CancellationToken epoch)
    {
        var projection = read.Reading;
        var (killbox, region, home, ok) = DefenseKillbox(projection);
        if (!ok)
        {
            return (new DefenseLayout(), [], plan, AwaitingPlan("layout_plan", "killbox"), false);
        }
        var (request, held, cok) = await DefenseCensusRequestAsync(state, read, killbox, region, home, call).ConfigureAwait(false);
        if (!cok)
        {
            return (new DefenseLayout(), [], plan, held, false);
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
                return (new DefenseLayout(), [], plan, result.Verdict, false);
            }
        }
        DefenseRecorder.RecordLayoutSnapshot(state.Snapshot, projection.Identity.Tick, new SnapshotLayout { Point = SnapshotLayoutReplay.LayoutPropose, Request = request }, call);
        DefenseLayout layout;
        LayoutPlan sited;
        try
        {
            layout = DefenseLayout.DefenseLayouts(request);
            sited = plan.WithProposedTiers(request.Definitions, layout);
        }
        catch (DomainException)
        {
            return (new DefenseLayout(), [], plan, NoSpace("defense_layout"), false);
        }
        if (layout.Firing.Count == 0)
        {
            return (new DefenseLayout(), [], plan, NoSpace("firing_cell"), false);
        }
        foreach (var g in layout.Gated)
        {
            DefensePipeline.DefenseTierGated(g.Tier.Value, g.Reason, call);
        }
        return (layout, request.Entrances.ToList(), sited, default, true);
    }

    internal async Task<(BuildingResult Result, bool Handled)> DigKillboxAsync(NeedOwner goal, ReviewStep review, ControlState state, ReviewReading read, Cell access, IReadOnlyList<RoleCell> planned, CancellationToken call, CancellationToken epoch)
    {
        if (_native is not GameClient source)
        {
            return (new BuildingResult(), false);
        }
        var dig = new BuildingPlanner { Reviewer = _reviewer, Concern = LayoutFamily.Defense.FilingKey, Need = LayoutFamily.Defense, Excavation = source };
        var step = new ExcavationStep { State = state, Review = review, Owner = goal, Facts = read.Reading };
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

partial class DefenseLayoutPlanner
{
    /// <summary>
    /// The layout/defense Need's facts (#3314): the stored layout of this world, whether the step verified it complete
    /// in this process (the ColonyReview's in-memory verification, #3008) at the map's latest fight within the re-verify
    /// window, and whether an open fight waits in rooms to harden. Combat is read as facts only; no combat Need gates
    /// the layout.
    /// </summary>
    internal static DefenseLayoutFacts DefenseLayoutFactsOf(ColonyReview reviewer, ReviewStep review, CancellationToken ct)
    {
        var p = reviewer.Player;
        var world = new StoreWorld { Colony = review.Snapshot.Colony, Load = review.Snapshot.Load, Map = review.Snapshot.Map };
        var record = p.Journal.LoadDefenseLayout(world, ct);
        var current = record is not null && record.Killbox != "" && record.World == world;
        var verified = reviewer.DefenseVerification.Get(world);
        var combat = DefenseLayoutPlanner.DefenseCombatKey(p, review, ct);
        return new DefenseLayoutFacts
        {
            Standing = current && verified.Complete && record!.AllBuilt(),
            Verified = current && combat != "" && verified.Complete && !DefenseLayoutPlanner.DefenseReverifyDue(verified, review.Tick, combat),
            WaitingFight = DefenseWaitingFight(p.Journal, world, ct) is not null,
        };
    }
}
