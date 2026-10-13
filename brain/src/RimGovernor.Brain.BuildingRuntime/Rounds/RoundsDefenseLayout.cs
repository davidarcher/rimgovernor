using System.Globalization;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The native reads of the defense layout planner.</summary>
public interface IRoundsDefenseLayoutSource
{
    (BridgeDefenseSite Site, BridgeResult Result) ReadDefenseSite(C.Identity identity, BridgeCellRect region, CancellationToken ct);

    (BridgeSpatialAccess Access, BridgeResult Result) ReadSpatialAccess(C.Identity identity, IReadOnlyList<Cell> from, IReadOnlyList<Cell> to, IReadOnlyList<string> kinds, CancellationToken ct);

    (O.ListPawnsReply Reply, BridgeResult Result) ReadCombatPawns(C.Identity identity, IReadOnlyList<string> ids, CancellationToken ct);

    (IReadOnlyList<BridgeBuildingPreview> Previews, BridgeResult Result) PreviewBuildings(IReadOnlyList<DomainAction> actions, GenerationSnapshot snapshot, CancellationToken ct);
}

public sealed class RoundsDefenseLayoutResult
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

    internal static readonly string[] DefenseTierOrder =
    [
        PolicyApi.TierChokepoint, PolicyApi.TierFiringLine, PolicyApi.TierFunnel, PolicyApi.TierTrapCorridor,
        PolicyApi.TierTurrets, PolicyApi.TierMortars, PolicyApi.TierBait, PolicyApi.TierIeds,
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
    internal static List<string> DefenseTierSequence(DefenseLayoutRecord record, bool roamerOwned)
    {
        var order = new List<string>(DefenseTierOrder);
        var ring = new List<string>();
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
            if (t.Name.StartsWith(DefenseTurretReplacePrefix, StringComparison.Ordinal))
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
            ? new MethodId(string.Create(CultureInfo.InvariantCulture, $"{DefenseTierPrefix(tier.Name)}{tier.Attempts}"))
            : new MethodId(string.Create(CultureInfo.InvariantCulture, $"{DefenseTierPrefix(tier.Name)}r{tier.Reopened}-{tier.Attempts}"));

    internal static string DefenseTierSubject(DefenseTierRecord tier) =>
        tier.Reopened == 0
            ? DefenseTierPrefix(tier.Name)
            : string.Create(CultureInfo.InvariantCulture, $"{DefenseTierPrefix(tier.Name)}r{tier.Reopened}-");

    internal static (StandardState Project, bool Found) DefenseLayoutProject(CancellationToken ct, Player p, RoundsReview review)
    {
        if (review.StandardFor(PolicyApi.EnsureDefensiveLayout, out var id))
        {
            return (p.Journal.LoadStandard(id, ct), true);
        }
        return (new StandardState(), false);
    }

    internal RoundsDefenseLayoutResult Step(CancellationToken call, CancellationToken epoch, StepArbiter? arbiter)
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
        var (goal, found) = DefenseLayoutProject(call, p, review);
        if (!found || goal.Standard.Status != StandardStatus.Open)
        {
            return new RoundsDefenseLayoutResult { Verdict = BuildingReasonNoDeficit };
        }
        var wait = DefenseWaitingFight(call, p.Journal, world);
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
            return Fight(call, epoch, goal, state, wait);
        }
        var combat = DefenseCombatKey(call, p, review);
        if (combat == "")
        {
            return new RoundsDefenseLayoutResult { Verdict = BuildingThreatPending };
        }
        var (record, stored) = p.Journal.LoadDefenseLayout(world, call);
        stored = stored && record.Killbox != "";
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
        var expected = StepScope(call, _reviewer.Native);
        if (!RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw DefenseControlErr(168);
        }
        var claims = p.Journal.ConstructionClaims(call, state.Snapshot, expected.Tick);
        IReadOnlyList<string> definitions = [];
        var siteDue = DefenseSitingDue(record, expected.Tick);
        var replaceDue = DefenseReplaceDue(record, expected.Tick);
        if (!stored || siteDue || replaceDue)
        {
            definitions = DefenseExtraDefinitions;
        }
        var read = _reviewer.ObserveOwned(call, _reviewer.Native, expected, claims, definitions);
        var (cut, planned) = DefensePerimeterCut(read);
        if (!planned)
        {
            return new RoundsDefenseLayoutResult { Verdict = AwaitingPlan("layout_plan", "killbox") };
        }
        if (!stored)
        {
            var (layout, entrances, held, ok) = Propose(call, epoch, goal, review, state, read);
            if (!ok)
            {
                if (held.IsZero)
                {
                    throw DefenseControlErr(289);
                }
                return new RoundsDefenseLayoutResult { Verdict = held };
            }
            var (killbox, digest, _) = PolicyApi.KillboxNode(cut.Plan);
            record = StorePackage.NewDefenseLayoutRecord(world, goal.Standard.Id, goal.Standard.Episode, layout, entrances, killbox, digest);
            DefenseRecutPerimeter(record, cut);
            p.Journal.SaveDefenseLayout(record, call);
        }
        else if (DefenseRecutPerimeter(record, cut))
        {
            DefenseAction(call, "defense-layout", LogLevel.Info, "applied", "perimeter_recut", "perimeter", new Dictionary<string, object?> { ["revision"] = record.PerimeterRevision });
            p.Journal.SaveDefenseLayout(record, call);
        }
        var census = ObserveTiers(call, state, read, record);
        var tick = read.Projection.Identity.Tick;
        pipe.Settle(census);
        if (!pipe.Piped() && definitions.Count > 0 && stored && siteDue)
        {
            record.TurretsProbedTick = tick;
            SiteTiers(call, state, read, record, census);
            p.Journal.SaveDefenseLayout(record, call);
        }
        if (!pipe.Piped() && definitions.Count > 0 && stored && replaceDue)
        {
            ReplaceTurret(call, state, read, record);
        }
        var (retierResult, retierDone) = Retier(call, epoch, goal, state, read, record);
        if (retierDone)
        {
            return retierResult;
        }
        var order = DefenseTierSequence(record, DefenseRoamerOwned(read.Projection));
        RoundsDefenseLayoutResult? admitted = null;
        foreach (var name in order)
        {
            var (tier, buildings, tok) = record.Tier(name);
            if (!tok || buildings.Count == 0 || tier.Built || DefenseTierOpen(pipe.Open, name))
            {
                continue;
            }
            if (pipe.Piped() && (!PolicyApi.IsPerimeterTier(name) || tier.Remove))
            {
                break;
            }
            var (spent, ok) = AdmitStandardSubject(call, p.Journal, goal, DefenseTierSubject(tier), state.Snapshot);
            if (!ok && pipe.Piped())
            {
                continue;
            }
            if (!ok)
            {
                record.VerifiedTick = tick;
                record.VerifiedCombat = combat;
                p.Journal.SaveDefenseLayout(record, call);
                return new RoundsDefenseLayoutResult { Verdict = spent, Tier = name };
            }
            if (tier.Remove)
            {
                return Remove(call, epoch, goal, state, read, record, tier, census);
            }
            buildings = DefenseMissingBuildings(buildings, census);
            if (buildings.Count == 0)
            {
                if (pipe.Piped())
                {
                    continue;
                }
                return new RoundsDefenseLayoutResult { Verdict = FieldUnavailable("fogged_defense_cells"), Tier = name };
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
                        return new RoundsDefenseLayoutResult { Verdict = ResearchWait(gate), Tier = name };
                    }
                    return new RoundsDefenseLayoutResult { Verdict = DefensePerimeterNoStone, Tier = name };
                }
                for (var i = 0; i < buildings.Count; i++)
                {
                    var b = buildings[i];
                    if (!DefenseStoneBuilding(b))
                    {
                        continue;
                    }
                    buildings[i] = DomainApi.NewBuilding(b.Definition, b.Cell, b.Rotation, stuff);
                }
            }
            var result = Admit(call, epoch, goal, state, read, record, tier, buildings, DefenseTierMethodId(tier), pipe);
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
            (record, _) = p.Journal.LoadDefenseLayout(world, call);
            DefenseHydrate(record, cut);
            (goal, _) = DefenseLayoutProject(call, p, review);
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
            return Rearm(call, epoch, goal, review, state, read, upkeep.Rearm[0], arbiter);
        }
        if (upkeep.Unpowered.Count > 0 || upkeep.Empty.Count > 0)
        {
            var subject = upkeep.Unpowered.Count > 0 ? "turret_power" : "turret_fuel";
            return new RoundsDefenseLayoutResult { Verdict = AwaitingPlan(subject, ""), Tier = PolicyApi.TierTurrets };
        }
        var (cover, handled) = ClearCover(call, epoch, goal, review, state, read, record);
        if (handled)
        {
            return cover;
        }
        return new RoundsDefenseLayoutResult { Verdict = BuildingReasonNoDeficit };
    }

    internal static string DefenseRearmPrefix(string turret) => "defense-rearm-" + turret + "-";

    internal RoundsDefenseLayoutResult Rearm(CancellationToken call, CancellationToken epoch, StandardState goal, RoundsReview review, ControlState state, RoundsReading read, DefenseRearm order, StepArbiter? arbiter)
    {
        var p = _reviewer.Player;
        var tick = read.Projection.Identity.Tick;
        var (verdict, ok) = AdmitStandardSubject(call, p.Journal, goal, DefenseRearmPrefix(order.Turret), state.Snapshot);
        if (!ok)
        {
            return new RoundsDefenseLayoutResult { Verdict = verdict, Tier = PolicyApi.TierTurrets };
        }
        if (arbiter is null || !arbiter.TryClaim([new PawnId(order.Pawn)], "defense-rearm:" + order.Turret))
        {
            return new RoundsDefenseLayoutResult { Verdict = WaitFor(PolicyApi.CauseMethodUsed, "defense_rearm_claim"), Tier = PolicyApi.TierTurrets };
        }
        var method = new MethodId(string.Create(CultureInfo.InvariantCulture, $"{DefenseRearmPrefix(order.Turret)}{tick.Value}"));
        var id = PlanIds.MintPlanId();
        var service = DomainApi.NewRecoveryService(new PawnId(order.Pawn), order.Turret, RecoveryServiceKind.Refuel);
        var action = DomainAction.NewRecoveryServiceAction(new ActionId($"{id}-0"), service);
        var plan = new PlanSpec(id, new PlanRevision(1), [action]);
        p.Current(call, epoch);
        var now = _reviewer.Clock.Now();
        if (p.Session.State() != state || now < read.StartedAt || now - read.StartedAt > _reviewer.MaxAge)
        {
            throw DefenseControlErr(360);
        }
        p.Journal.CommitMethodReason(call, goal.Standard.Id, goal.Revision, method, "", plan);
        DefenseAction(call, "defense-layout", LogLevel.Info, "applied", "rearm", order.Turret, new Dictionary<string, object?> { ["pawn"] = order.Pawn, ["fuel"] = order.Fuel, ["x"] = order.Cell.X, ["z"] = order.Cell.Z, ["method"] = method.Value });
        return new RoundsDefenseLayoutResult { Verdict = BuildingReasonAdmitted, Plan = id, Tier = PolicyApi.TierTurrets };
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
                    facts.Definition = site.Definition;
                    facts.Dps = site.TurretDps;
                    facts.Id = site.Id;
                    facts.Powered = site.Powered;
                    facts.OutOfFuel = site.OutOfFuel;
                    foreach (var d in site.FuelDefinitions)
                    {
                        facts.FuelDefinitions.Add(new Resource(d));
                    }
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
        var request = new DefenseRequest { Definitions = DefenseDefinitionNames, UnitCosts = new Dictionary<string, IReadOnlyList<Amount>>(StringComparer.Ordinal) };
        request.Mortar = new DefenseMortarRequest
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
                request.UnitCosts[d.Name] = price.Costs;
                request.Mortar.Stuff = price.Stuff;
            }
        }
        return request;
    }

    internal static (StuffPrice Price, bool Ok) DefenseUnitPrice(ColonyProjection projection, PlanningDefinition d)
    {
        var (stock, _) = projection.Stock();
        try
        {
            return (d.StuffChoice(ObservationApi.MaxHitPointsPerCost, stock), true);
        }
        catch (DomainException)
        {
            return (new StuffPrice(), false);
        }
    }

    internal static DefenseRequest DefenseTurretRequest(RoundsReading read)
    {
        var projection = read.Projection;
        var request = new DefenseRequest { Definitions = DefenseDefinitionNames, UnitCosts = new Dictionary<string, IReadOnlyList<Amount>>(StringComparer.Ordinal) };
        request.Turret = new DefenseTurretRequest { Definition = DefenseTurretRung(read), Conduit = DefenseConduitDefinition, Stock = projection.Resources, Max = PolicyApi.TurretBudget(projection.Facts.RaidPoints) };
        var rk = projection.Facts.Research.TryGetValue(out var research);
        var finished = new HashSet<ResearchProjectId>(research?.Finished ?? []);
        foreach (var d in projection.Definitions)
        {
            if (d.Name != request.Turret.Definition && d.Name != DefenseConduitDefinition)
            {
                continue;
            }
            var (price, priced) = DefenseUnitPrice(projection, d);
            if (priced)
            {
                request.UnitCosts[d.Name] = price.Costs;
            }
            if (d.Name != request.Turret.Definition)
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
            request.Turret.Available = available;
            if (priced)
            {
                request.Turret.Stuff = price.Stuff;
            }
            if (d.PowerW.TryGetValue(out var w) && w >= 0)
            {
                request.Turret.DrawW = Known(w);
            }
            if (d.Size.TryGetValue(out var size))
            {
                request.Turret.Size = size;
            }
        }
        if (!projection.PowerPlanning.TryGetValue(out var topology))
        {
            return request;
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
            return request;
        }
        request.Turret.SpareW = Known(spare);
        request.Turret.Transmitters = DefenseNetworkConduits(topology, best);
        return request;
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
        if (BuildingRuntimePackage.DefenseTerrain(definition))
        {
            return Terrain.GetValueOrDefault(cell) == definition;
        }
        return Edifice.GetValueOrDefault(cell) == definition;
    }
}
