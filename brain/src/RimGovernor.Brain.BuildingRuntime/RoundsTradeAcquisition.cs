namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The derived trade-acquisition state of one world; Mu guards every field.</summary>
internal sealed partial class TradeAcquisitionMemory
{
    public readonly object Mu = new();
    public GenerationSnapshot World;
    public Dictionary<string, Pol.TradeAcquisitionPlan> Proposals = [];
    public Dictionary<string, string> Active = [];
    public Dictionary<string, TradeMission> Missions = [];

    /// <summary>
    /// The catalog-derived supply facts of a trader kind, built once per catalog: the definitions a settlement of the
    /// kind can be asked for (every ThingDef against the kind's stock generators) and whether it supplies a demanded
    /// good. Pure catalog data, the same at every review; the settlement search once rebuilt them per settlement.
    /// </summary>
    public Bridge.DefinitionCatalog? SupplyCatalog;
    public Dictionary<string, IReadOnlyList<Pol.MissionSupplyDefinition>> SupplyDefinitions = new(StringComparer.Ordinal);
    public Dictionary<(string Kind, Pol.ResourceKey Good), bool> SupplyCompatible = [];

    /// <summary>Forgets the supply facts of another catalog (a new load). Call under Mu.</summary>
    public void ForCatalog(Bridge.DefinitionCatalog catalog)
    {
        if (ReferenceEquals(SupplyCatalog, catalog))
        {
            return;
        }
        SupplyCatalog = catalog;
        SupplyDefinitions.Clear();
        SupplyCompatible.Clear();
    }
}

public sealed partial class Rounder
{
    internal TradeAcquisitionMemory TradeAcquisition { get; } = new();

    /// <summary>
    /// Derived supply intent for the food/resources owner. Executors must claim before creating shared Hands
    /// work and release only after reconciling its outcome; saved/native evidence owns restart recovery.
    /// </summary>
    public Pol.TradeAcquisitionPlan TradeAcquisitionPlan(string owner)
    {
        var m = TradeAcquisition;
        lock (m.Mu)
        {
            m.Proposals.TryGetValue(owner, out var plan);
            plan ??= new Pol.TradeAcquisitionPlan();
            if (plan.Proposal is not null)
            {
                var own = plan.Proposal with { };
                own = own with { Option = own.Option with { Compatible = [.. own.Option.Compatible] } };
                plan = plan with { Proposal = own };
            }
            return plan;
        }
    }

    public bool ClaimTradeAcquisition(string owner, string id)
    {
        var m = TradeAcquisition;
        lock (m.Mu)
        {
            m.Proposals.TryGetValue(owner, out var plan);
            if (plan?.Proposal is null || plan.Proposal.Option.ID != id || m.Active.Count != 0)
            {
                return false;
            }
            m.Active[owner] = id;
            return true;
        }
    }

    public void ReleaseTradeAcquisition(string owner, string id)
    {
        var m = TradeAcquisition;
        lock (m.Mu)
        {
            if (m.Active.TryGetValue(owner, out var held) && held == id)
            {
                m.Active.Remove(owner);
            }
        }
    }

    internal async Task PlanFoodAcquisitionAsync(GenerationSnapshot world, RimGovernor.Brain.Bridge.Contracts.ColonyReading p, CancellationToken ct)
    {
        var demands = new List<Pol.SupplyDemandResult>();
        if (p.Facts.FoodPlan is { } plan)
        {
            var thresholds = Seasonal(p.Facts);
            try
            {
                var d = Pol.SupplyPlanRules.NutritionDemand(new Pol.NutritionDemandInput { Forecast = plan.Forecast, MinDays = thresholds.FoodMinDays, TargetDays = thresholds.FoodTargetDays });
                demands.Add(new Pol.SupplyDemandResult(d, plan.DeliveredPerDay, plan.GapPerDay));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // A demand the forecast cannot price is left out.
            }
        }
        await PlanTradeAcquisitionAsync(world, "food", p, demands, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs inside the existing supply owner. Trips are exact native pack/crew options supplied by the mission
    /// planner, not guessed crews.
    /// </summary>
    internal async Task PlanTradeAcquisitionAsync(GenerationSnapshot world, string owner, RimGovernor.Brain.Bridge.Contracts.ColonyReading p, IReadOnlyList<Pol.SupplyDemandResult> demands, IReadOnlyList<Pol.TradeAcquisitionOption>? trips, CancellationToken ct)
    {
        var m = TradeAcquisition;
        string active = "";
        lock (m.Mu)
        {
            if (m.World != world)
            {
                m.World = world;
                m.Proposals = [];
                m.Active = [];
                m.Missions = [];
            }
            foreach (var id in m.Active.Values)
            {
                active = id;
                break;
            }
        }
        var options = new List<Pol.TradeAcquisitionOption>(trips ?? []);
        var unmet = demands.Any(d => d.Gap > 0);
        if (Native is FacadeNativeClient source && unmet)
        {
            var identity = Boundary.Identity(world);
            ObservationsPb.TradeAcquisition? facts = null;
            try
            {
                facts = await source.ReadTradeAcquisitionAsync(identity, null, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                facts = null;
            }
            if (facts is not null)
            {
                if (facts.Arrivals.Count != 0 || facts.CommsWork.Count != 0)
                {
                    active = "native_pending";
                }
                Bridge.DefinitionCatalog? catalog = null;
                try
                {
                    catalog = await source.DefinitionCatalogAsync(identity, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    catalog = null;
                }
                if (catalog is not null)
                {
                    options.AddRange(TradeAcquisitionMemory.RequestAcquisitionOptions(facts, catalog, demands));
                    options.AddRange(await SettlementAcquisitionOptionsAsync(world, owner, p, demands, catalog, ct).ConfigureAwait(false));
                }
            }
        }
        // No attempt is proposed while the colony's silver is unread or its size prices no reserve.
        var (reserve, known) = Pol.TradePolicyRules.TradeSilverReserve(p.Facts.Census.ColonistCount);
        var plan = known
            ? Pol.TradePolicyRules.PlanTradeAcquisition(new Pol.TradeAcquisitionRequest { Demands = demands, Options = options, Silver = p.Facts.Silver(), Reserve = reserve, ActiveID = active })
            : new Pol.TradeAcquisitionPlan();
        lock (m.Mu)
        {
            m.Proposals[owner] = plan;
        }
    }
}

partial class TradeAcquisitionMemory
{
    internal static List<Pol.TradeAcquisitionOption> RequestAcquisitionOptions(ObservationsPb.TradeAcquisition facts, Bridge.DefinitionCatalog catalog, IReadOnlyList<Pol.SupplyDemandResult> demands)
    {
        var options = new List<Pol.TradeAcquisitionOption>();
        var consoles = facts.Consoles.OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
        foreach (var row in facts.Requests)
        {
            var option = new Pol.TradeAcquisitionOption
            {
                ID = row.FactionId + "/" + ProtoNames.Of(row.Kind) + "/" + row.TraderKind,
                Faction = row.FactionId,
                TraderKind = row.TraderKind,
                PassingShips = facts.PassingShips.Count,
                LaborTicks = 0,
                Kind = row.Kind == CommonPb.TradeRequestKind.Orbital ? Pol.TradeAcquisitionKind.Orbital : Pol.TradeAcquisitionKind.Caravan,
                Eligible = row.Eligible,
                LeadDays = (double)row.ArrivalMaxTicks / GameTime.TicksPerDay,
                GoodwillCost = row.GoodwillCost,
                RelationAfterPayment = row.RelationAfterPayment,
                CooldownTicks = row.CooldownRemainingTicks,
                OrbitalAvailable = facts.OrbitalAvailable,
            };
            var negotiators = row.NegotiatorIds.OrderBy(n => n, StringComparer.Ordinal).ToList();
            foreach (var console in consoles)
            {
                foreach (var pawn in negotiators)
                {
                    if (console.NegotiatorIds.Contains(pawn))
                    {
                        option = option with { Console = console.Id };
                        option = option with { Negotiator = pawn };
                        break;
                    }
                }
                if (option.Console != "")
                {
                    break;
                }
            }
            foreach (var queued in facts.Arrivals)
            {
                if (queued.FactionId == row.FactionId && queued.Kind == row.Kind)
                {
                    option = option with { Eligible = false };
                }
            }
            foreach (var work in facts.CommsWork)
            {
                if (work.FactionId == row.FactionId)
                {
                    option = option with { Eligible = false };
                }
            }
            var compatible = new List<Pol.ResourceKey>();
            foreach (var demand in demands)
            {
                if (catalog.TraderKindCanSupply(option.TraderKind, demand.Demand.Good) == Pol.TradeCompatibility.Trades)
                {
                    compatible.Add(demand.Demand.Good);
                }
            }
            option = option with { Compatible = [.. compatible] };
            options.Add(option);
        }
        return options;
    }
}
