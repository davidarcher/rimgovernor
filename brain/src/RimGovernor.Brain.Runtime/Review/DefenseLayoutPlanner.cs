using System.Globalization;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Runtime;

public sealed class DefenseLayoutResult : IPlannerResult
{
    public Verdict Verdict { get; set; }

    public PlanId Plan { get; set; }

    public string Tier { get; set; } = "";
}

/// <summary>Plans the colony's defensive layout: the killbox, the firing line, the perimeter, the turrets and mortars, and the cover to clear, one tier at a time.</summary>
public sealed partial class DefenseLayoutPlanner
{
    internal const string DefenseTurretDefinition = DefenseTurretsPolicy.TurretMini;
    internal const string DefenseConduitDefinition = "HiddenConduit";
    internal const string DefenseMortarDefinition = "Turret_Mortar";

    internal static readonly DefenseDefinitions DefenseDefinitionNames = new()
    {
        Sandbag = "Barricade", SandbagStuff = "WoodLog", Wall = "Wall", WallStuff = "WoodLog", Fence = "Fence", FenceStuff = "WoodLog",
        Trap = "TrapSpike", TrapStuff = "WoodLog", Door = "Door", DoorStuff = "WoodLog", Floor = "WoodPlankFloor", Bait = "Stool", BaitStuff = "WoodLog",
    };

    internal static readonly string[] DefenseExtraDefinitions =
    [
        DefenseTurretsPolicy.TurretMini, DefenseTurretsPolicy.TurretAutocannon, DefenseTurretsPolicy.TurretSniper, DefenseMortarDefinition, DefenseConduitDefinition,
        DefenseCoverStock.DefenseSandbags, DefenseCoverStock.DefenseEmbrasure, DefenseIEDs.HighExplosive, DefenseIEDs.Incendiary,
    ];

    internal static readonly DefenseTierName[] DefenseTierOrder =
    [
        DefenseLayout.TierChokepoint, DefenseLayout.TierFiringLine, DefenseLayout.TierFunnel, DefenseLayout.TierTrapCorridor,
        DefenseTurretsPolicy.TierTurrets, DefenseMortarsPolicy.TierMortars, DefenseArrivalRoutes.TierBait, DefenseIEDs.TierIEDs,
    ];

    private readonly ColonyReview _reviewer;
    private readonly GameClient _native;
    internal Dictionary<string, string> Unbuilt = new(StringComparer.Ordinal);
    internal string TiersLogged = "";
    internal string BlocksLogged = "";

    public DefenseLayoutPlanner(ColonyReview reviewer, GameClient native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewReviewDefenseLayoutPlanner: reviewer == nil || native == nil");
        }
        if (reviewer.Native is null)
        {
            throw new ControlException("NewReviewDefenseLayoutPlanner: reviewer.native == nil");
        }
        _reviewer = reviewer;
        _native = native;
    }

    /// <summary>Whether a defense may build <paramref name="name"/>: read available with its research finished; an unread fact is no option.</summary>
    internal static bool DefenseDefinitionAvailable(ReviewReading read, string name) =>
        LayoutDefenseRules.DefinitionReadinessOf(read.Reading.Facts, name) == DefinitionReadiness.Available;

    internal static bool DefenseRoamerOwned(ColonyReading projection)
    {
        foreach (var a in projection.Facts.AnimalUpkeep.Animals)
        {
            if (a.RequiresPen)
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
            if (LayoutDefenseRules.IsPerimeterTier(t.Name))
            {
                if (roamerOwned && !t.Built && !t.Remove && LayoutPerimeterSitesRules.IsCorePerimeterTier(t.Name))
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
                order.Insert(order.IndexOf(DefenseTurretsPolicy.TierTurrets), t.Name);
            }
        }
        ring.AddRange(order);
        return ring;
    }

    internal static string DefenseTierPrefix(string tier) => "defense-" + tier + "-";

    internal static MethodId DefenseTierMethodId(DefenseTierRecord tier) => DefenseTierAttemptId(tier, tier.Attempts);

    private static MethodId DefenseTierAttemptId(DefenseTierRecord tier, int attempt) =>
        new(string.Create(CultureInfo.InvariantCulture, $"{DefenseTierPrefix(tier.Name.Value)}{attempt}"));

    /// <summary>
    /// The tier's refusal budget over its latest attempt only (#3586): a fallen tier's next attempt starts fresh
    /// unless the attempt that built it carried a refusal.
    /// </summary>
    internal static (Verdict Verdict, bool Admitted) AdmitDefenseTier(StoreJournal journal, NeedOwner goal, DefenseTierRecord tier, GenerationSnapshot world, CancellationToken call) =>
        tier.Attempts == 0
            ? ReviewRefusalBudget.AdmitSubject(journal, DefenseTierMethodId(tier).Value, [], world, call)
            : ReviewRefusalBudget.AdmitLatestAttempt(journal, goal, DefenseTierAttemptId(tier, tier.Attempts - 1), world, call);

    /// <summary>
    /// The <c>layout/defense</c> Need's step (#3314), run while the Need is unmet: during an open fight it hardens the
    /// waiting rooms; otherwise it proposes the layout, re-verifies it and admits one tier at a time, each tier a
    /// Method of the Need owner. A stored layout from another load or map is re-verified and its unbuilt tiers' attempt
    /// counts restart with the Need's per-load Method history.
    /// </summary>
    internal async Task<Verdict> StepAsync(NeedWork work, CancellationToken call)
    {
        var result = await TierStepAsync(work.Owner, work.State, work.Arbiter, call, work.Epoch).ConfigureAwait(false);
        return result.Verdict;
    }

    private async Task<DefenseLayoutResult> TierStepAsync(NeedOwner goal, ControlState state, StepArbiter? arbiter, CancellationToken call, CancellationToken epoch)
    {
        var p = _reviewer.Player;
        var review = await p.Journal.LoadReviewAsync(call).ConfigureAwait(false);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return new DefenseLayoutResult { Verdict = BuildingReasonNoReview };
        }
        var world = new StoreWorld { Colony = state.Snapshot.Colony, Load = state.Snapshot.Load, Map = state.Snapshot.Map };
        var wait = DefenseWaitingFight(p.Journal, world, call);
        var (pipe, pipelinable) = DefensePipeline.DefenseOpenTiers(goal, id => p.Journal.LoadPlan(id, call));
        if (!pipelinable || (wait is not null && pipe.Piped()))
        {
            return new DefenseLayoutResult { Verdict = BuildingReasonExistingWork };
        }
        if (wait is not null)
        {
            return await FightAsync(goal, state, wait, call, epoch).ConfigureAwait(false);
        }
        var combat = DefenseCombatKey(p, review, call);
        if (combat == "")
        {
            return new DefenseLayoutResult { Verdict = BuildingReasonThreatPending };
        }
        var record = p.Journal.LoadDefenseLayout(world, call) ?? new DefenseLayoutRecord();
        var stored = record.Killbox != "";
        if (stored && record.World != world)
        {
            // Another load or map: the Need's Method history (need_methods) is per load, so the unbuilt tiers'
            // attempt counts restart with it, as they did with the retired Standard's per-load id. The in-memory
            // verification is per world, so the layout re-verifies.
            record.World = world;
            foreach (var t in record.Tiers)
            {
                if (!t.Built)
                {
                    t.Attempts = 0;
                }
            }
            p.Journal.SaveDefenseLayout(record, call);
        }
        var verification = _reviewer.DefenseVerification;
        var verified = verification.Get(world);
        if (stored && verified.Complete && !DefenseReverifyDue(verified, review.Tick, combat))
        {
            return new DefenseLayoutResult { Verdict = BuildingReasonNoDeficit };
        }
        var expected = await StepScope.StepScopeAsync(_reviewer.Native, call).ConfigureAwait(false);
        if (!BuildingPlanner.BuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw DefenseControlErr(168);
        }
        var claims = p.Journal.ConstructionClaims(state.Snapshot, expected.Tick, call);
        string[] definitions = [];
        var siteDue = DefenseSitingDue(verified, expected.Tick);
        var replaceDue = DefenseReplaceDue(record, verified, expected.Tick);
        if (!stored || siteDue || replaceDue)
        {
            definitions = DefenseExtraDefinitions;
        }
        var read = await _reviewer.ObserveOwnedAsync(_reviewer.Native, expected, claims, definitions, call).ConfigureAwait(false);
        var (cut, planned) = DefensePerimeterCut(read);
        if (!planned)
        {
            return new DefenseLayoutResult { Verdict = AwaitingPlan("layout_plan", "killbox") };
        }
        if (!stored)
        {
            var (layout, entrances, sited, held, ok) = await ProposeAsync(goal, review, state, read, cut.Plan, call, epoch).ConfigureAwait(false);
            if (!ok)
            {
                if (held.IsZero)
                {
                    throw DefenseControlErr(289);
                }
                return new DefenseLayoutResult { Verdict = held };
            }
            var (killbox, digest, _) = LayoutPlan.KillboxNode(cut.Plan);
            record = DefenseLayoutRecord.From(world, layout, entrances, killbox, digest);
            DefenseRecutPerimeter(record, cut, null);
            p.Journal.SaveDefenseLayout(record, call);
            // The proposal's tiers are the plan's tier nodes from the start, so every tier the record starts is pinned
            // from its nodes (#3584).
            if (!LayoutPlan.SamePlan(cut.Plan, sited))
            {
                p.Journal.RecordLayoutPlan(state.Snapshot, read.Reading.Identity.Tick, sited, call);
                cut.Plan = sited;
            }
            verification.Set(world, new DefenseVerified());
        }
        else
        {
            DefenseHydrate(record, cut);
        }
        // The census covers what the record holds and what the cut wants: the recut diffs the one against the other.
        var census = await ReadTierCensusAsync(state, read, record with { Tiers = [.. record.Tiers, .. DefensePerimeterWants(record, cut)] }, call).ConfigureAwait(false);
        if (stored && DefenseRecutPerimeter(record, cut, census))
        {
            var removals = record.Tiers.Where(t => t.Remove).Sum(t => t.Buildings.Count);
            DefensePipeline.DefenseAction("defense-layout", TelemetryLevel.Info, "applied", "perimeter_recut", "perimeter", new Dictionary<string, object?> { ["removals"] = removals }, call);
            p.Journal.SaveDefenseLayout(record, call);
            verification.Update(world, v => v with { Complete = false });
        }
        SettleTierCensus(record, census, call);
        var tick = read.Reading.Identity.Tick;
        pipe.Settle(census);
        if (!pipe.Piped() && definitions.Length > 0 && stored && siteDue)
        {
            verification.Update(world, v => v with { TurretsProbed = tick });
            cut.Plan = await SiteTiersAsync(state, read, record, cut.Plan, census, call).ConfigureAwait(false);
            p.Journal.SaveDefenseLayout(record, call);
        }
        if (!pipe.Piped() && definitions.Length > 0 && stored && replaceDue)
        {
            verification.Update(world, v => v with { TurretsProbed = tick });
            cut.Plan = await ReplaceTurretAsync(state, read, record, cut.Plan, call).ConfigureAwait(false);
        }
        var (retierResult, retierDone) = Retier(goal, state, read, record, call, epoch);
        if (retierDone)
        {
            return retierResult;
        }
        var order = DefenseTierSequence(record, DefenseRoamerOwned(read.Reading));
        DefenseLayoutResult? admitted = null;
        foreach (var name in order)
        {
            var (tier, buildings, tok) = record.Tier(name);
            if (!tok || buildings.Count == 0 || tier.Built || DefensePipeline.DefenseTierOpen(pipe.Open, name.Value))
            {
                continue;
            }
            if (pipe.Piped() && (!LayoutDefenseRules.IsPerimeterTier(name) || tier.Remove))
            {
                break;
            }
            var (spent, ok) = AdmitDefenseTier(p.Journal, goal, tier, state.Snapshot, call);
            if (!ok && pipe.Piped())
            {
                continue;
            }
            if (!ok)
            {
                verification.Update(world, v => v with { Tick = tick, Combat = combat });
                return new DefenseLayoutResult { Verdict = spent, Tier = name.Value };
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
                return new DefenseLayoutResult { Verdict = FieldUnavailable("fogged_defense_cells"), Tier = name.Value };
            }
            if (pipe.Piped() && pipe.Overlaps(buildings))
            {
                continue;
            }
            if (LayoutDefenseRules.IsPerimeterTier(name) && DefenseNeedsStone(buildings))
            {
                var (stuff, sok) = DefensePerimeterStone(read.Reading, buildings);
                if (!sok)
                {
                    if (pipe.Piped())
                    {
                        break;
                    }
                    var gate = ResearchRules.ResearchGate([FacilityUpkeepRules.StoneShellResearch], read.Reading.Facts.Research);
                    if (gate != "")
                    {
                        return new DefenseLayoutResult { Verdict = ResearchWait(gate), Tier = name.Value };
                    }
                    return new DefenseLayoutResult { Verdict = BuildingReasonDefensePerimeterNoStone, Tier = name.Value };
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
                    return new DefenseLayoutResult { Verdict = BuildingReasonExistingWork };
                }
                return result;
            }
            admitted = result;
            if (p.Journal.LoadDefenseLayout(world, call) is { } reloaded)
            {
                record = reloaded;
            }
            DefenseHydrate(record, cut);
            goal = p.Journal.LoadNeed(LayoutFamily.Defense, state.Snapshot, call);
        }
        if (admitted is not null)
        {
            return admitted;
        }
        if (pipe.Open.Count > 0)
        {
            return new DefenseLayoutResult { Verdict = BuildingReasonExistingWork };
        }
        verification.Update(world, v => v with { Complete = true, Tick = tick, Combat = combat });
        var workers = read.Reading.Facts.ForecastObserved.WorkPawns.Pawns;
        var upkeep = DefenseRearm.DefenseRearmTurrets(DefenseTurretFactsOf(record, census), workers, ResourceStockRules.Count(read.Reading.Snapshot.Resources), DisasterRules.PowerOutageHold(read.Reading.Facts.DisasterConditions));
        if (upkeep.Rearm)
        {
            return Rearm();
        }
        if (upkeep.Unpowered.Count > 0 || upkeep.Empty.Count > 0)
        {
            var subject = upkeep.Unpowered.Count > 0 ? "turret_power" : "turret_fuel";
            return new DefenseLayoutResult { Verdict = AwaitingPlan(subject, ""), Tier = DefenseTurretsPolicy.TierTurrets.Value };
        }
        var (cover, handled) = await ClearCoverAsync(goal, review, state, read, record, cut.Plan, call, epoch).ConfigureAwait(false);
        if (handled)
        {
            return cover;
        }
        return new DefenseLayoutResult { Verdict = BuildingReasonNoDeficit };
    }

    internal static DefenseLayoutResult Rearm() =>
        new() { Verdict = Verdict.AwaitingPlan("unsupported_forecast_turret_refuel",
            "Routine turret refuel requires exact fuel, worker and outcome alternatives."), Tier = DefenseTurretsPolicy.TierTurrets.Value };
    /// <summary>The tier turrets the census finds standing as armed power consumers; a turret it does not find is no service fact.</summary>
    internal static List<DefenseTurretFacts> DefenseTurretFactsOf(DefenseLayoutRecord record, DefenseCensus? census)
    {
        var output = new List<DefenseTurretFacts>();
        if (census is null)
        {
            return output;
        }
        foreach (var tier in record.Tiers)
        {
            if (tier.Name != DefenseTurretsPolicy.TierTurrets)
            {
                continue;
            }
            foreach (var b in tier.Buildings)
            {
                if (b.Definition == DefenseConduitDefinition)
                {
                    continue;
                }
                if (!census.Consumers.TryGetValue(b.Cell, out var site) || site.Turret is not { } gun)
                {
                    continue;
                }
                output.Add(site);
            }
        }
        return output;
    }

    internal static bool DefenseSitingDue(DefenseVerified verified, Tick tick) =>
        verified.TurretsProbed == default || tick.Value - verified.TurretsProbed.Value >= DefenseReverifyTicks;

    internal static DefenseRequest DefenseMortarRequest(ReviewReading read)
    {
        var projection = read.Reading;
        var request = new DefenseRequest { Definitions = DefenseDefinitionNames };
        var unitCosts = new Dictionary<string, EquatableArray<Amount>>(StringComparer.Ordinal);
        var mortar = new DefenseMortarRequest
        {
            Definition = DefenseMortarDefinition,
            Stock = ResourceStockRules.Count(projection.Snapshot.Resources),
            Max = DefenseMortarsPolicy.MortarBudget(RaidDefense.Of(projection.Facts), projection.Facts.EssentialInputs.Power.Buildings.Where(b => b.Turret is not null).ToArray()),
            Available = DefenseDefinitionAvailable(read, DefenseMortarDefinition),
        };
        foreach (var d in projection.Facts.Definitions)
        {
            if (d.Name != DefenseMortarDefinition)
            {
                continue;
            }
            var (price, ok) = LayoutDefenseRules.DefenseUnitPrice(d, ResourceStockRules.Count(projection.Snapshot.Resources));
            if (ok)
            {
                unitCosts[d.Name] = [.. price.Costs];
                mortar = mortar with { Stuff = price.Stuff };
            }
        }
        return request with { Mortar = mortar, UnitCosts = unitCosts };
    }

    /// <summary>
    /// The powered turret tier's request: the turret is available when its definition is ready (read available, research
    /// finished), its draw is read and a generating network carries it; the network with the most spare watts is the
    /// one the tier wires to. An unread power topology is no generating network.
    /// </summary>
    internal static DefenseRequest DefenseTurretRequest(ReviewReading read)
    {
        var projection = read.Reading;
        var request = new DefenseRequest { Definitions = DefenseDefinitionNames };
        var unitCosts = new Dictionary<string, EquatableArray<Amount>>(StringComparer.Ordinal);
        var turret = new DefenseTurretRequest { Definition = DefenseTurretRung(read), Conduit = DefenseConduitDefinition, Stock = ResourceStockRules.Count(projection.Snapshot.Resources), Max = DefenseBudget.TurretBudget(RaidDefense.Of(projection.Facts), projection.Facts.EssentialInputs.Power.Buildings.Where(b => b.Turret is not null).ToArray(), projection.Facts.GoalBuild.Turrets) };
        var drawRead = false;
        foreach (var d in projection.Facts.Definitions)
        {
            if (d.Name != turret.Definition && d.Name != DefenseConduitDefinition)
            {
                continue;
            }
            var (price, priced) = LayoutDefenseRules.DefenseUnitPrice(d, ResourceStockRules.Count(projection.Snapshot.Resources));
            if (priced)
            {
                unitCosts[d.Name] = [.. price.Costs];
            }
            if (d.Name != turret.Definition)
            {
                continue;
            }
            if (priced)
            {
                turret = turret with { Stuff = price.Stuff };
            }
            var w = (d.Power ?? throw new RequiredFactException("planning Power")).PowerW;
            if (w >= 0)
            {
                drawRead = true;
                turret = turret with { DrawW = w };
            }
            var size = d.Size;
            {
                turret = turret with { Size = size };
            }
        }
        var best = "";
        var spare = 0.0;
        var topology = projection.Facts.EssentialInputs.Power;
        foreach (var net in topology.Networks)
        {
            if (net.GenerationW > 0 && (best == "" || net.GenerationW - net.ConsumptionW > spare))
            {
                best = net.Id;
                spare = net.GenerationW - net.ConsumptionW;
            }
        }
        if (best != "")
        {
            turret = turret with { SpareW = spare, Transmitters = [.. LayoutDefenseRules.DefenseNetworkConduits(topology, best)] };
        }
        var ready = LayoutDefenseRules.DefinitionReadinessOf(projection.Facts, turret.Definition) == DefinitionReadiness.Available;
        turret = turret with { Available = ready && drawRead && best != "" };
        return request with { Turret = turret, UnitCosts = unitCosts };
    }
}

/// <summary>What the observed site says stands in the layout cells: edifices, terrain, conduits, power consumers and cover.</summary>
internal sealed class DefenseCensus
{
    public Dictionary<Cell, string> Edifice = [];

    /// <summary>The stuff of the edifice on the cell, empty for a stuffless one.</summary>
    public Dictionary<Cell, string> EdificeStuff = [];
    public Dictionary<Cell, string> Terrain = [];
    public Dictionary<Cell, bool> Conduits = [];
    public Dictionary<Cell, PowerSite> Consumers = [];
    public Dictionary<Cell, RimGovernor.Brain.GameFacts.Observations.DefenseCover?> Cover = [];
    public Dictionary<Cell, bool> Unbridging = [];
    public Dictionary<Cell, bool> Fogged = [];

    internal bool Standing(string definition, Cell cell)
    {
        if (definition == DefenseLayoutPlanner.DefenseConduitDefinition)
        {
            return Conduits.GetValueOrDefault(cell);
        }
        if (DefenseLayoutPlanner.DefenseTerrain(definition))
        {
            return Terrain.GetValueOrDefault(cell) == definition;
        }
        return Edifice.GetValueOrDefault(cell) == definition;
    }

    /// <summary>
    /// Whether <paramref name="b"/> itself stands: its definition, and for an edifice its stuff, so an old wooden wall
    /// is told from the stone wall built in its place. An empty stored stuff matches any: the stone of a wall or door
    /// is chosen at admission and not stored, and a stuffless definition has none.
    /// </summary>
    internal bool Standing(DefenseBuilding b)
    {
        if (!Standing(b.Definition, b.Cell))
        {
            return false;
        }
        if (b.Stuff == "" || b.Definition == DefenseLayoutPlanner.DefenseConduitDefinition || DefenseLayoutPlanner.DefenseTerrain(b.Definition))
        {
            return true;
        }
        return EdificeStuff.GetValueOrDefault(b.Cell) == b.Stuff;
    }
}
