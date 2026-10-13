using System.Globalization;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The native reads of the defense layout planner.</summary>
public interface IRoundsDefenseLayoutSource
{
    Task<(BridgeDefenseSite Site, BridgeResult Result)> ReadDefenseSiteAsync(C.Identity identity, BridgeCellRect region, CancellationToken ct);

    Task<(BridgeSpatialAccess Access, BridgeResult Result)> ReadSpatialAccessAsync(C.Identity identity, IReadOnlyList<Cell> from, IReadOnlyList<Cell> toCells, IReadOnlyList<string> kinds, CancellationToken ct);

    Task<(O.ListPawnsReply Reply, BridgeResult Result)> ReadCombatPawnsAsync(C.Identity identity, IReadOnlyList<string> ids, CancellationToken ct);

    Task<(IReadOnlyList<BridgeBuildingPreview> Previews, BridgeResult Result)> PreviewBuildingsAsync(IReadOnlyList<DomainAction> actions, GenerationSnapshot snapshot, CancellationToken ct);
}

public sealed class RoundsDefenseLayoutResult : IPlannerMethod
{
    public Verdict Verdict { get; set; }

    public PlanId Plan { get; set; }

    public string Tier { get; set; } = "";

    public uint NativeWorkTicks { get; set; }
}

/// <summary>Plans the colony's defensive layout: the killbox, the firing line, the perimeter, the turrets and mortars, and the cover to clear, one tier at a time.</summary>
public sealed partial class RoundsDefenseLayoutPlanner
{
    internal const string DefenseTurretDefinition = PolicyApi.TurretMini;
    internal const string DefenseConduitDefinition = "HiddenConduit";
    internal const string DefenseMortarDefinition = "Turret_Mortar";
    internal const string DefenseIedHighExplosive = "TrapIED_HighExplosive";
    internal const string DefenseIedIncendiary = "TrapIED_Incendiary";
    internal const uint DefenseNativeWorkTicks = 2500;

    internal static readonly DefenseDefinitions DefenseDefinitionNames = new()
    {
        Sandbag = "Barricade", SandbagStuff = "WoodLog", Wall = "Wall", WallStuff = "WoodLog", Fence = "Fence", FenceStuff = "WoodLog",
        Trap = "TrapSpike", TrapStuff = "WoodLog", Door = "Door", DoorStuff = "WoodLog", Floor = "WoodPlankFloor", Bait = "Stool", BaitStuff = "WoodLog",
    };

    internal static readonly string[] DefenseExtraDefinitions =
    [
        PolicyApi.TurretMini, PolicyApi.TurretAutocannon, PolicyApi.TurretSniper, DefenseMortarDefinition, DefenseConduitDefinition,
        PolicyApi.DefenseSandbags, PolicyApi.DefenseEmbrasure, DefenseIedHighExplosive, DefenseIedIncendiary,
    ];

    internal static readonly DefenseTierName[] DefenseTierOrder =
    [
        PolicyApi.TierChokepoint, PolicyApi.TierFiringLine, PolicyApi.TierFunnel, PolicyApi.TierTrapCorridor,
        PolicyApi.TierTurrets, PolicyApi.TierMortars, PolicyApi.TierBait, PolicyApi.TierIEDs,
    ];

    private readonly Rounder _reviewer;
    private readonly IRoundsDefenseLayoutSource _native;
    internal Dictionary<string, string> Unbuilt = new(StringComparer.Ordinal);
    internal string TiersLogged = "";
    internal string BlocksLogged = "";

    public RoundsDefenseLayoutPlanner(Rounder reviewer, IRoundsDefenseLayoutSource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsDefenseLayoutPlanner: reviewer == nil || native == nil");
        }
        if (reviewer.Native is null)
        {
            throw new ControlException("NewRoundsDefenseLayoutPlanner: reviewer.native == nil");
        }
        _reviewer = reviewer;
        _native = native;
    }

    internal static bool DefenseDefinitionAvailable(RoundsReading read, string name)
    {
        var rk = read.Projection.Facts.Research.TryGetValue(out var research);
        foreach (var d in read.Projection.Definitions)
        {
            if (d.Name != name)
            {
                continue;
            }
            if (!d.Available.TryGetValue(out var v) || !v || (d.Research.Count > 0 && !rk))
            {
                return false;
            }
            foreach (var prerequisite in d.Research)
            {
                if (!research!.Finished.Contains(new ResearchProjectId(prerequisite)))
                {
                    return false;
                }
            }
            return true;
        }
        return false;
    }

    internal static bool DefenseRoamerOwned(ColonyProjection projection)
    {
        if (!projection.Facts.AnimalUpkeep.Animals.TryGetValue(out var animals))
        {
            return false;
        }
        foreach (var a in animals)
        {
            if (a.RequiresPen.TryGetValue(out var pen) && pen)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The order in which the tiers are considered: the perimeter ring first when roamers are owned, then the fixed order, perimeter tiers and turret replacements.</summary>
    internal static List<DefenseTierName> DefenseTierSequence(DefenseLayoutRecord record, bool roamerOwned)
    {
        var order = new List<DefenseTierName>(DefenseTierOrder);
        var ring = new List<DefenseTierName>();
        foreach (var t in record.Tiers)
        {
            if (PolicyApi.IsPerimeterTier(t.Name))
            {
                if (roamerOwned && !t.Built && !t.Remove && PolicyApi.IsCorePerimeterTier(t.Name))
                {
                    ring.Add(t.Name);
                }
                else
                {
                    order.Add(t.Name);
                }
            }
            if (t.Name.Value.StartsWith(DefenseTurretReplacePrefix, StringComparison.Ordinal))
            {
                order.Insert(order.IndexOf(PolicyApi.TierTurrets), t.Name);
            }
        }
        ring.AddRange(order);
        return ring;
    }

    internal static string DefenseTierPrefix(string tier) => "defense-" + tier + "-";

    internal static MethodId DefenseTierMethodId(DefenseTierRecord tier) =>
        tier.Reopened == 0
            ? new MethodId(string.Create(CultureInfo.InvariantCulture, $"{DefenseTierPrefix(tier.Name.Value)}{tier.Attempts}"))
            : new MethodId(string.Create(CultureInfo.InvariantCulture, $"{DefenseTierPrefix(tier.Name.Value)}r{tier.Reopened}-{tier.Attempts}"));

    internal static string DefenseTierSubject(DefenseTierRecord tier) =>
        tier.Reopened == 0
            ? DefenseTierPrefix(tier.Name.Value)
            : string.Create(CultureInfo.InvariantCulture, $"{DefenseTierPrefix(tier.Name.Value)}r{tier.Reopened}-");

    internal static StandardState? DefenseLayoutProject(Player p, RoundsReview review, CancellationToken ct)
    {
        if (review.StandardFor(PolicyApi.EnsureDefensiveLayout, out var id))
        {
            return p.Journal.LoadStandard(id, ct);
        }
        return null;
    }

    internal async Task<RoundsDefenseLayoutResult> StepAsync(StepArbiter? arbiter, CancellationToken call, CancellationToken epoch)
    {
        var p = _reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsDefenseLayoutResult { Verdict = BuildingReasonDisabled };
        }
        if (!state.ObservationKnown || !state.Snapshot.IsValid)
        {
            throw DefenseControlErr(113);
        }
        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return new RoundsDefenseLayoutResult { Verdict = BuildingReasonNoReview };
        }
        var world = new StoreWorld { Colony = state.Snapshot.Colony, Load = state.Snapshot.Load, Map = state.Snapshot.Map };
        if (DefenseLayoutProject(p, review, call) is not { } goal || goal.Standard.Status != StandardStatus.Open)
        {
            return new RoundsDefenseLayoutResult { Verdict = BuildingReasonNoDeficit };
        }
        var wait = DefenseWaitingFight(p.Journal, world, call);
        if (wait is null && (goal.Standard.Finding != Finding.Unmet || review.Veto(goal.Standard) != ""))
        {
            return new RoundsDefenseLayoutResult { Verdict = BuildingReasonNoDeficit };
        }
        var (pipe, pipelinable) = DefenseOpenTiers(goal, id => p.Journal.LoadPlan(id, call));
        if (!pipelinable || (wait is not null && pipe.Piped()))
        {
            return new RoundsDefenseLayoutResult { Verdict = BuildingReasonExistingWork };
        }
        if (wait is not null)
        {
            return await FightAsync(goal, state, wait, call, epoch).ConfigureAwait(false);
        }
        var combat = DefenseCombatKey(p, review, call);
        if (combat == "")
        {
            return new RoundsDefenseLayoutResult { Verdict = BuildingReasonThreatPending };
        }
        var record = p.Journal.LoadDefenseLayout(world, call) ?? new DefenseLayoutRecord();
        var stored = record.Killbox != "";
        if (stored && record.World != world)
        {
            record.World = world;
            record.Complete = false;
            p.Journal.SaveDefenseLayout(record, call);
        }
        if (stored && (record.Standard != goal.Standard.Id || record.Episode != goal.Standard.Episode))
        {
            record.Standard = goal.Standard.Id;
            record.Episode = goal.Standard.Episode;
            foreach (var t in record.Tiers)
            {
                if (!t.Built)
                {
                    t.Attempts = 0;
                }
            }
            p.Journal.SaveDefenseLayout(record, call);
        }
        if (stored && record.Complete && !DefenseReverifyDue(record, review.Tick, combat))
        {
            return new RoundsDefenseLayoutResult { Verdict = BuildingReasonNoDeficit };
        }
        var expected = await StepScopeAsync(_reviewer.Native, call).ConfigureAwait(false);
        if (!RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw DefenseControlErr(168);
        }
        var claims = p.Journal.ConstructionClaims(state.Snapshot, expected.Tick, call);
        string[] definitions = [];
        var siteDue = DefenseSitingDue(record, expected.Tick);
        var replaceDue = DefenseReplaceDue(record, expected.Tick);
        if (!stored || siteDue || replaceDue)
        {
            definitions = DefenseExtraDefinitions;
        }
        var read = await _reviewer.ObserveOwnedAsync(_reviewer.Native, expected, claims, definitions, call).ConfigureAwait(false);
        var (cut, planned) = DefensePerimeterCut(read);
        if (!planned)
        {
            return new RoundsDefenseLayoutResult { Verdict = AwaitingPlan("layout_plan", "killbox") };
        }
        if (!stored)
        {
            var (layout, entrances, held, ok) = await ProposeAsync(goal, review, state, read, call, epoch).ConfigureAwait(false);
            if (!ok)
            {
                if (held.IsZero)
                {
                    throw DefenseControlErr(289);
                }
                return new RoundsDefenseLayoutResult { Verdict = held };
            }
            var (killbox, digest, _) = PolicyApi.KillboxNode(cut.Plan);
            record = DefenseLayoutRecord.From(world, goal.Standard.Id, goal.Standard.Episode, layout, entrances, killbox, digest);
            DefenseRecutPerimeter(record, cut);
            p.Journal.SaveDefenseLayout(record, call);
        }
        else if (DefenseRecutPerimeter(record, cut))
        {
            DefenseAction("defense-layout", TelemetryLevel.Info, "applied", "perimeter_recut", "perimeter", new Dictionary<string, object?> { ["revision"] = record.PerimeterRevision }, call);
            p.Journal.SaveDefenseLayout(record, call);
        }
        var census = await ObserveTiersAsync(state, read, record, call).ConfigureAwait(false);
        var tick = read.Projection.Identity.Tick;
        pipe.Settle(census);
        if (!pipe.Piped() && definitions.Length > 0 && stored && siteDue)
        {
            record.TurretsProbedTick = tick;
            await SiteTiersAsync(state, read, record, census, call).ConfigureAwait(false);
            p.Journal.SaveDefenseLayout(record, call);
        }
        if (!pipe.Piped() && definitions.Length > 0 && stored && replaceDue)
        {
            await ReplaceTurretAsync(state, read, record, call).ConfigureAwait(false);
        }
        var (retierResult, retierDone) = Retier(goal, state, read, record, call, epoch);
        if (retierDone)
        {
            return retierResult;
        }
        var order = DefenseTierSequence(record, DefenseRoamerOwned(read.Projection));
        RoundsDefenseLayoutResult? admitted = null;
        foreach (var name in order)
        {
            var (tier, buildings, tok) = record.Tier(name);
            if (!tok || buildings.Count == 0 || tier.Built || DefenseTierOpen(pipe.Open, name.Value))
            {
                continue;
            }
            if (pipe.Piped() && (!PolicyApi.IsPerimeterTier(name) || tier.Remove))
            {
                break;
            }
            var (spent, ok) = AdmitStandardSubject(p.Journal, goal, DefenseTierSubject(tier), state.Snapshot, call);
            if (!ok && pipe.Piped())
            {
                continue;
            }
            if (!ok)
            {
                record.VerifiedTick = tick;
                record.VerifiedCombat = combat;
                p.Journal.SaveDefenseLayout(record, call);
                return new RoundsDefenseLayoutResult { Verdict = spent, Tier = name.Value };
            }
            if (tier.Remove)
            {
                return Remove(goal, state, read, record, tier, census, call, epoch);
            }
            buildings = DefenseMissingBuildings(buildings, census);
            if (buildings.Count == 0)
            {
                if (pipe.Piped())
                {
                    continue;
                }
                return new RoundsDefenseLayoutResult { Verdict = FieldUnavailable("fogged_defense_cells"), Tier = name.Value };
            }
            if (pipe.Piped() && pipe.Overlaps(buildings))
            {
                continue;
            }
            if (PolicyApi.IsPerimeterTier(name) && DefenseNeedsStone(buildings))
            {
                var (stuff, sok) = DefensePerimeterStone(read.Projection, buildings);
                if (!sok)
                {
                    if (pipe.Piped())
                    {
                        break;
                    }
                    var gate = PolicyApi.ResearchGate([PolicyApi.StoneShellResearch], read.Projection.Facts.Research);
                    if (gate != "")
                    {
                        return new RoundsDefenseLayoutResult { Verdict = ResearchWait(gate), Tier = name.Value };
                    }
                    return new RoundsDefenseLayoutResult { Verdict = BuildingReasonDefensePerimeterNoStone, Tier = name.Value };
                }
                for (var i = 0; i < buildings.Count; i++)
                {
                    var b = buildings[i];
                    if (!DefenseStoneBuilding(b))
                    {
                        continue;
                    }
                    buildings[i] = new Building(b.Definition, b.Cell, b.Rotation, stuff);
                }
            }
            var result = await AdmitAsync(goal, state, read, record, tier, buildings, DefenseTierMethodId(tier), pipe, call, epoch).ConfigureAwait(false);
            if (result.Verdict != BuildingReasonAdmitted)
            {
                if (admitted is not null)
                {
                    return admitted;
                }
                if (pipe.Open.Count > 0)
                {
                    return new RoundsDefenseLayoutResult { Verdict = BuildingReasonExistingWork };
                }
                return result;
            }
            admitted = result;
            if (p.Journal.LoadDefenseLayout(world, call) is { } reloaded)
            {
                record = reloaded;
            }
            DefenseHydrate(record, cut);
            if (DefenseLayoutProject(p, review, call) is { } reproject)
            {
                goal = reproject;
            }
        }
        if (admitted is not null)
        {
            return admitted;
        }
        if (pipe.Open.Count > 0)
        {
            return new RoundsDefenseLayoutResult { Verdict = BuildingReasonExistingWork };
        }
        record.Complete = true;
        record.VerifiedTick = tick;
        record.VerifiedCombat = combat;
        read.Projection.WorkPawns.TryGetValue(out var workers);
        var upkeep = PolicyApi.DefenseRearmTurrets(DefenseTurretFactsOf(record, census), workers, read.Projection.Resources, PolicyApi.PowerOutageHold(read.Projection.Facts.DisasterConditions));
        p.Journal.SaveDefenseLayout(record, call);
        if (upkeep.Rearm.Count > 0)
        {
            return Rearm(goal, review, state, read, upkeep.Rearm[0], arbiter, call, epoch);
        }
        if (upkeep.Unpowered.Count > 0 || upkeep.Empty.Count > 0)
        {
            var subject = upkeep.Unpowered.Count > 0 ? "turret_power" : "turret_fuel";
            return new RoundsDefenseLayoutResult { Verdict = AwaitingPlan(subject, ""), Tier = PolicyApi.TierTurrets.Value };
        }
        var (cover, handled) = await ClearCoverAsync(goal, review, state, read, record, call, epoch).ConfigureAwait(false);
        if (handled)
        {
            return cover;
        }
        return new RoundsDefenseLayoutResult { Verdict = BuildingReasonNoDeficit };
    }

    internal static string DefenseRearmPrefix(string turret) => "defense-rearm-" + turret + "-";

    internal RoundsDefenseLayoutResult Rearm(StandardState goal, RoundsReview review, ControlState state, RoundsReading read, DefenseRearm order, StepArbiter? arbiter, CancellationToken call, CancellationToken epoch)
    {
        var p = _reviewer.Player;
        var tick = read.Projection.Identity.Tick;
        var (verdict, ok) = AdmitStandardSubject(p.Journal, goal, DefenseRearmPrefix(order.Turret), state.Snapshot, call);
        if (!ok)
        {
            return new RoundsDefenseLayoutResult { Verdict = verdict, Tier = PolicyApi.TierTurrets.Value };
        }
        if (arbiter is null || !arbiter.TryClaim([order.Pawn], "defense-rearm:" + order.Turret))
        {
            return new RoundsDefenseLayoutResult { Verdict = WaitFor(Cause.MethodUsed, "defense_rearm_claim"), Tier = PolicyApi.TierTurrets.Value };
        }
        var method = new MethodId(string.Create(CultureInfo.InvariantCulture, $"{DefenseRearmPrefix(order.Turret)}{tick.Value}"));
        var id = PlanIds.MintPlanId();
        var service = new RecoveryService((Domain.PawnId)order.Pawn, order.Turret, Domain.RecoveryMethod.Refuel);
        var action = DomainAction.NewRecoveryServiceAction(new ActionId($"{id}-0"), service);
        var plan = new PlanSpec(id, new PlanRevision(1), [action]);
        p.Current(call, epoch);
        var now = _reviewer.Clock.Now();
        if (p.Session.State() != state || now < read.StartedAt || now - read.StartedAt > _reviewer.MaxAge)
        {
            throw DefenseControlErr(360);
        }
        p.Journal.CommitMethodReason(goal.Standard.Id, goal.Revision, method, "", plan, call);
        DefenseAction("defense-layout", TelemetryLevel.Info, "applied", "rearm", order.Turret, new Dictionary<string, object?> { ["pawn"] = order.Pawn, ["fuel"] = order.Fuel, ["x"] = order.Cell.X, ["z"] = order.Cell.Z, ["method"] = method.Value }, call);
        return new RoundsDefenseLayoutResult { Verdict = BuildingReasonAdmitted, Plan = id, Tier = PolicyApi.TierTurrets.Value };
    }

    internal static List<DefenseTurretFacts> DefenseTurretFactsOf(DefenseLayoutRecord record, DefenseCensus? census)
    {
        var output = new List<DefenseTurretFacts>();
        if (census is null)
        {
            return output;
        }
        foreach (var tier in record.Tiers)
        {
            if (tier.Name != PolicyApi.TierTurrets)
            {
                continue;
            }
            foreach (var b in tier.Buildings)
            {
                if (b.Definition == DefenseConduitDefinition)
                {
                    continue;
                }
                var facts = new DefenseTurretFacts { Cell = b.Cell };
                if (census.Consumers.TryGetValue(b.Cell, out var site))
                {
                    facts = facts with { Definition = site.Definition };
                    facts = facts with { Dps = site.TurretDps };
                    facts = facts with { Id = site.Id };
                    facts = facts with { Powered = site.Powered };
                    facts = facts with { OutOfFuel = site.OutOfFuel };
                    facts = facts with { FuelDefinitions = [.. site.FuelDefinitions.Select(d => new Resource(d))] };
                }
                output.Add(facts);
            }
        }
        return output;
    }

    internal static bool DefenseSitingDue(DefenseLayoutRecord record, Tick tick) =>
        record.TurretsProbedTick == default || tick.Value - record.TurretsProbedTick.Value >= DefenseReverifyTicks;

    internal static DefenseRequest DefenseMortarRequest(RoundsReading read)
    {
        var projection = read.Projection;
        var request = new DefenseRequest { Definitions = DefenseDefinitionNames };
        var unitCosts = new Dictionary<string, EquatableArray<Amount>>(StringComparer.Ordinal);
        var mortar = new DefenseMortarRequest
        {
            Definition = DefenseMortarDefinition,
            Stock = projection.Resources,
            Max = PolicyApi.MortarBudget(projection.Facts.RaidPoints),
            Available = Known(DefenseDefinitionAvailable(read, DefenseMortarDefinition)),
        };
        foreach (var d in projection.Definitions)
        {
            if (d.Name != DefenseMortarDefinition)
            {
                continue;
            }
            var (price, ok) = DefenseUnitPrice(projection, d);
            if (ok)
            {
                unitCosts[d.Name] = [.. price.Costs];
                mortar = mortar with { Stuff = price.Stuff };
            }
        }
        return request with { Mortar = mortar, UnitCosts = unitCosts };
    }

    internal static (StuffPrice Price, bool Ok) DefenseUnitPrice(ColonyProjection projection, PlanningDefinition d)
    {
        var stock = projection.Resources.TryGetValue(out var known) ? known : null;
        try
        {
            return (d.StuffChoice(StuffCriterion.MaxHitPointsPerCost, stock), true);
        }
        catch (DomainException)
        {
            return (new StuffPrice("", []), false);
        }
    }

    internal static DefenseRequest DefenseTurretRequest(RoundsReading read)
    {
        var projection = read.Projection;
        var request = new DefenseRequest { Definitions = DefenseDefinitionNames };
        var unitCosts = new Dictionary<string, EquatableArray<Amount>>(StringComparer.Ordinal);
        var turret = new DefenseTurretRequest { Definition = DefenseTurretRung(read), Conduit = DefenseConduitDefinition, Stock = projection.Resources, Max = PolicyApi.TurretBudget(projection.Facts.RaidPoints) };
        DefenseRequest Done() => request with { Turret = turret, UnitCosts = unitCosts };
        var rk = projection.Facts.Research.TryGetValue(out var research);
        var finished = new HashSet<ResearchProjectId>(research?.Finished ?? []);
        foreach (var d in projection.Definitions)
        {
            if (d.Name != turret.Definition && d.Name != DefenseConduitDefinition)
            {
                continue;
            }
            var (price, priced) = DefenseUnitPrice(projection, d);
            if (priced)
            {
                unitCosts[d.Name] = [.. price.Costs];
            }
            if (d.Name != turret.Definition)
            {
                continue;
            }
            var available = d.Available;
            if (rk)
            {
                foreach (var prerequisite in d.Research)
                {
                    var k = available.TryGetValue(out var v);
                    available = Known(k && v && finished.Contains(new ResearchProjectId(prerequisite)));
                }
            }
            else if (d.Research.Count > 0)
            {
                available = Unknown<bool>();
            }
            turret = turret with { Available = available };
            if (priced)
            {
                turret = turret with { Stuff = price.Stuff };
            }
            if (d.PowerW.TryGetValue(out var w) && w >= 0)
            {
                turret = turret with { DrawW = Known(w) };
            }
            if (d.Size.TryGetValue(out var size))
            {
                turret = turret with { Size = size };
            }
        }
        if (!projection.PowerPlanning.TryGetValue(out var topology))
        {
            return Done();
        }
        var best = "";
        var spare = 0.0;
        foreach (var net in topology.Networks)
        {
            var gk = net.GenerationW.TryGetValue(out var generation);
            var ck = net.ConsumptionW.TryGetValue(out var consumption);
            if (gk && ck && generation > 0 && (best == "" || generation - consumption > spare))
            {
                best = net.Id;
                spare = generation - consumption;
            }
        }
        if (best == "")
        {
            return Done();
        }
        turret = turret with { SpareW = Known(spare), Transmitters = [.. DefenseNetworkConduits(topology, best)] };
        return Done();
    }
}

/// <summary>What the observed site says stands in the layout cells: edifices, terrain, conduits, power consumers and cover.</summary>
internal sealed class DefenseCensus
{
    public Dictionary<Cell, string> Edifice = [];
    public Dictionary<Cell, string> Terrain = [];
    public Dictionary<Cell, bool> Conduits = [];
    public Dictionary<Cell, PowerSite> Consumers = [];
    public Dictionary<Cell, BridgeDefenseCover?> Cover = [];
    public Dictionary<Cell, bool> Unbridging = [];
    public Dictionary<Cell, bool> Fogged = [];

    internal bool Standing(string definition, Cell cell)
    {
        if (definition == RoundsDefenseLayoutPlanner.DefenseConduitDefinition)
        {
            return Conduits.GetValueOrDefault(cell);
        }
        if (RoundsDefenseLayoutPlanner.DefenseTerrain(definition))
        {
            return Terrain.GetValueOrDefault(cell) == definition;
        }
        return Edifice.GetValueOrDefault(cell) == definition;
    }
}
