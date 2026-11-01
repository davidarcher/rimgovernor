using System.Globalization;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Runtime;

public sealed class DefenseLayoutResult : IPlannerResult
{
    public Verdict Verdict { get; set; }

    public PlanId Plan { get; set; }

    public string Tier { get; set; } = "";
}

/// <summary>Plans the colony's defensive layout: generates the defense alternatives, probes and compares them against retaining what stands, binds the chosen one into the plan and works it as a chain (habitat milestone 7).</summary>
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

    private readonly ColonyReview _reviewer;
    private readonly GameClient _native;
    internal Dictionary<string, string> Unbuilt = new(StringComparer.Ordinal);

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

    /// <summary>
    /// The <c>layout/defense</c> Need's step (#3314), run while the Need is unmet. During an open fight it hardens the
    /// waiting rooms. Otherwise it compares the defense alternatives over the plan against retaining it (task 7.3,
    /// CompareAsync), binds the chosen one into the plan and takes its chain's next step (task 7.4, ChainAsync); the
    /// protection it owes is re-derived from the probe of what stands, so an admitted build changes nothing until its pieces
    /// stand and draw. Spent turret barrels are refuelled through the maintenance choice (Refuel). A stored layout the
    /// comparison retains is kept as it stands (settled on the census, re-tiered, its turrets kept powered and its cover
    /// cleared).
    /// </summary>
    internal async Task<Verdict> StepAsync(NeedWork work, CancellationToken call)
    {
        var result = await TierStepAsync(work.Owner, work.State, call, work.Epoch).ConfigureAwait(false);
        return result.Verdict;
    }

    private async Task<DefenseLayoutResult> TierStepAsync(NeedOwner goal, ControlState state, CancellationToken call, CancellationToken epoch)
    {
        var p = _reviewer.Player;
        var review = await p.Journal.LoadReviewAsync(call).ConfigureAwait(false);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return new DefenseLayoutResult { Verdict = BuildingReasonNoReview };
        }
        var world = new StoreWorld { Colony = state.Snapshot.Colony, Load = state.Snapshot.Load, Map = state.Snapshot.Map };
        if (DefensePipeline.DefenseWorkOpen(goal, id => p.Journal.LoadPlan(id, call)))
        {
            return new DefenseLayoutResult { Verdict = BuildingReasonExistingWork };
        }
        if (DefenseWaitingFight(p.Journal, world, call) is { } wait)
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
            // Another load or map: the in-memory verification is per world, so the layout re-verifies.
            record.World = world;
            p.Journal.SaveDefenseLayout(record, call);
        }
        var verification = _reviewer.DefenseVerification;
        var verified = verification.Get(world);
        if (verified.Complete && verified.Deficit <= 0 && !DefenseReverifyDue(verified, review.Tick, combat))
        {
            return new DefenseLayoutResult { Verdict = BuildingReasonNoDeficit };
        }
        var expected = await StepScope.StepScopeAsync(_reviewer.Native, call).ConfigureAwait(false);
        if (!BuildingPlanner.BuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw DefenseControlErr(168);
        }
        var claims = p.Journal.ConstructionClaims(state.Snapshot, expected.Tick, call);
        var read = await _reviewer.ObserveOwnedAsync(_reviewer.Native, expected, claims, DefenseExtraDefinitions, call).ConfigureAwait(false);
        var (cut, planned) = DefensePerimeterCut(read);
        if (!planned)
        {
            return new DefenseLayoutResult { Verdict = AwaitingPlan("layout_plan", "defense") };
        }
        var tick = read.Reading.Identity.Tick;
        DefenseCensus? census = null;
        if (stored)
        {
            DefenseHydrate(record, cut);
            census = await ReadTierCensusAsync(state, read, record, call).ConfigureAwait(false);
            SettleTierCensus(record, census, call);
        }
        var decision = await CompareAsync(state, read, cut, world, tick, call).ConfigureAwait(false);
        verification.Update(world, v => v with { Deficit = decision.Deficit });
        var (chain, chained) = await ChainAsync(goal, state, read, cut, decision, call, epoch).ConfigureAwait(false);
        if (chained)
        {
            return chain;
        }
        var (refuel, refuelled) = Refuel(goal, state, read, call, epoch);
        if (refuelled)
        {
            return refuel;
        }
        if (!stored || !record.AllBuilt() || decision.Kind != DefenseCandidateKind.Retain)
        {
            verification.Update(world, v => v with { Complete = true, Tick = tick, Combat = combat });
            return new DefenseLayoutResult { Verdict = decision.Deficit > 0 ? AwaitingPlan(DefenseProtectionOwed, decision.Reason) : BuildingReasonNoDeficit };
        }
        var (retierResult, retierDone) = Retier(goal, state, read, record, call, epoch);
        if (retierDone)
        {
            return retierResult;
        }
        verification.Update(world, v => v with { Complete = true, Tick = tick, Combat = combat });
        var upkeep = DefenseRearm.DefenseRearmTurrets(DefenseTurretFactsOf(record, census), DisasterRules.PowerOutageHold(read.Reading.Facts.DisasterConditions));
        if (upkeep.Unpowered.Count > 0)
        {
            return new DefenseLayoutResult { Verdict = AwaitingPlan(DefenseBindingRules.TurretPower, ""), Tier = DefenseTurretsPolicy.TierTurrets.Value };
        }
        var (cover, handled) = await ClearCoverAsync(goal, review, state, read, record, cut.Plan, call, epoch).ConfigureAwait(false);
        if (handled)
        {
            return cover;
        }
        return new DefenseLayoutResult { Verdict = BuildingReasonNoDeficit };
    }

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
