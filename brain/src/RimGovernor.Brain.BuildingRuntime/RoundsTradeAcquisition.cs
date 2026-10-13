namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The derived trade-acquisition state of one world; Mu guards every field.</summary>
internal sealed class TradeAcquisitionMemory
{
    public readonly object Mu = new();
    public GenerationSnapshot World;
    public Dictionary<string, Pol.TradeAcquisitionPlan> Proposals = [];
    public Dictionary<string, string> Active = [];
    public Dictionary<string, TradeMission> Missions = [];
}

/// <summary>The native read the acquisition options need beyond the definition catalog.</summary>
public interface ITradeAcquisitionSource : Observation.IDefinitionSource
{
    (ObservationsPb.TradeAcquisition Acquisition, BridgeResult Result) ReadTradeAcquisition(CancellationToken ct, C.Identity identity, OperationsPb.FormCaravanIntent? intent);
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
            plan = plan with { Decisions = [.. plan.Decisions] };
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
            if (plan?.Proposal is null || plan.Proposal.Option.Id != id || m.Active.Count != 0)
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

    internal void PlanFoodAcquisition(CancellationToken ct, GenerationSnapshot world, Observation.ColonyProjection p)
    {
        var demands = new List<Pol.SupplyDemandResult>();
        if (p.Facts.FoodPlan.TryGetValue(out var plan))
        {
            var thresholds = Seasonal(p.Facts);
            try
            {
                var d = Pol.PolicyFunctions.NutritionDemand(new Pol.NutritionDemandInput { Forecast = plan.Forecast, MinDays = thresholds.FoodMinDays, TargetDays = thresholds.FoodTargetDays });
                demands.Add(new Pol.SupplyDemandResult { Demand = d, Gap = plan.GapPerDay, Delivered = plan.DeliveredPerDay });
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // A demand the forecast cannot price is left out, as an error leaves it out in Go.
            }
        }
        PlanTradeAcquisition(ct, world, "food", p, demands, null);
    }

    /// <summary>
    /// Runs inside the existing supply owner. Trips are exact native pack/crew options supplied by the mission
    /// planner, not guessed crews.
    /// </summary>
    internal void PlanTradeAcquisition(CancellationToken ct, GenerationSnapshot world, string owner, Observation.ColonyProjection p, IReadOnlyList<Pol.SupplyDemandResult> demands, IReadOnlyList<Pol.TradeAcquisitionOption>? trips)
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
        if (Native is ITradeAcquisitionSource source && unmet)
        {
            var identity = Boundary.Identity(world);
            ObservationsPb.TradeAcquisition? facts = null;
            try
            {
                (facts, _) = source.ReadTradeAcquisition(ct, identity, null);
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
                    catalog = source.DefinitionCatalog(ct, identity);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    catalog = null;
                }
                if (catalog is not null)
                {
                    options.AddRange(RequestAcquisitionOptions(facts, catalog, demands));
                    options.AddRange(SettlementAcquisitionOptions(ct, world, owner, p, demands, catalog));
                }
            }
        }
        var known = Pol.PolicyFunctions.TradeSilverReserve(p.Facts.Colonists).TryGetValue(out var reserve);
        var rf = known ? Known(reserve) : Unknown<long>();
        var plan = Pol.PolicyFunctions.PlanTradeAcquisition(new Pol.TradeAcquisitionRequest { Demands = demands, Options = options, Silver = p.Facts.Silver(), Reserve = rf, ActiveId = active });
        lock (m.Mu)
        {
            m.Proposals[owner] = plan;
        }
    }
}

internal static partial class BuildingRuntimePackage
{
    internal static List<Pol.TradeAcquisitionOption> RequestAcquisitionOptions(ObservationsPb.TradeAcquisition facts, Bridge.DefinitionCatalog catalog, IReadOnlyList<Pol.SupplyDemandResult> demands)
    {
        var options = new List<Pol.TradeAcquisitionOption>();
        var consoles = facts.Consoles.OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
        foreach (var row in facts.Requests)
        {
            var option = new Pol.TradeAcquisitionOption
            {
                Id = row.FactionId + "/" + Wire.ProtoName(row.Kind) + "/" + row.TraderKind,
                Faction = row.FactionId,
                TraderKind = row.TraderKind,
                LastRequestTick = row.LastRequestTick,
                PassingShips = facts.PassingShips.Count,
                LaborTicks = Known(0.0),
                Kind = row.Kind == CommonPb.TradeRequestKind.Orbital ? Pol.TradeAcquireKind.Orbital : Pol.TradeAcquireKind.Caravan,
            };
            if (row.HasEligible)
            {
                option.Eligible = Known(row.Eligible);
            }
            if (row.HasArrivalMaxTicks)
            {
                option.LeadDays = Known((double)row.ArrivalMaxTicks / GameTime.TicksPerDay);
            }
            if (row.HasGoodwillCost)
            {
                option.GoodwillCost = Known(row.GoodwillCost);
            }
            if (row.HasRelationAfterPayment)
            {
                option.RelationAfterPayment = Known(row.RelationAfterPayment);
            }
            if (row.HasCooldownRemainingTicks)
            {
                option.CooldownTicks = Known(row.CooldownRemainingTicks);
            }
            if (facts.HasOrbitalAvailable)
            {
                option.OrbitalAvailable = Known(facts.OrbitalAvailable);
            }
            var negotiators = row.NegotiatorIds.OrderBy(n => n, StringComparer.Ordinal).ToList();
            foreach (var console in consoles)
            {
                foreach (var pawn in negotiators)
                {
                    if (console.NegotiatorIds.Contains(pawn))
                    {
                        option.Console = console.Id;
                        option.Negotiator = pawn;
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
                    option.Eligible = Known(false);
                }
            }
            foreach (var work in facts.CommsWork)
            {
                if (work.FactionId == row.FactionId)
                {
                    option.Eligible = Known(false);
                }
            }
            var compatible = new List<Pol.Resource>();
            foreach (var demand in demands)
            {
                if (catalog.TraderKindCanSupply(option.TraderKind, demand.Demand.Good).TryGetValue(out var can) && can)
                {
                    compatible.Add(demand.Demand.Good);
                }
            }
            option.Compatible = compatible;
            options.Add(option);
        }
        return options;
    }
}
