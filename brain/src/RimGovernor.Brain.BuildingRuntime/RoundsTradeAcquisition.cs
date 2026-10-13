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
    Task<(ObservationsPb.TradeAcquisition Acquisition, BridgeResult Result)> ReadTradeAcquisitionAsync(C.Identity identity, OperationsPb.FormCaravanIntent? intent, CancellationToken ct);
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

    internal async Task PlanFoodAcquisitionAsync(GenerationSnapshot world, Observation.ColonyProjection p, CancellationToken ct)
    {
        var demands = new List<Pol.SupplyDemandResult>();
        if (p.Facts.FoodPlan.TryGetValue(out var plan))
        {
            var thresholds = Seasonal(p.Facts);
            try
            {
                var d = Pol.PolicyFunctions.NutritionDemand(new Pol.NutritionDemandInput { Forecast = plan.Forecast, MinDays = thresholds.FoodMinDays, TargetDays = thresholds.FoodTargetDays });
                demands.Add(new Pol.SupplyDemandResult(d, plan.DeliveredPerDay, plan.GapPerDay));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // A demand the forecast cannot price is left out, as an error leaves it out in Go.
            }
        }
        await PlanTradeAcquisitionAsync(world, "food", p, demands, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs inside the existing supply owner. Trips are exact native pack/crew options supplied by the mission
    /// planner, not guessed crews.
    /// </summary>
    internal async Task PlanTradeAcquisitionAsync(GenerationSnapshot world, string owner, Observation.ColonyProjection p, IReadOnlyList<Pol.SupplyDemandResult> demands, IReadOnlyList<Pol.TradeAcquisitionOption>? trips, CancellationToken ct)
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
                (facts, _) = await source.ReadTradeAcquisitionAsync(identity, null, ct).ConfigureAwait(false);
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
                    options.AddRange(RequestAcquisitionOptions(facts, catalog, demands));
                    options.AddRange(await SettlementAcquisitionOptionsAsync(world, owner, p, demands, catalog, ct).ConfigureAwait(false));
                }
            }
        }
        var (reserve, known) = Pol.PolicyFunctions.TradeSilverReserve(p.Facts.Colonists);
        var rf = known ? Known(reserve) : Unknown<long>();
        var plan = Pol.PolicyFunctions.PlanTradeAcquisition(new Pol.TradeAcquisitionRequest { Demands = demands, Options = options, Silver = p.Facts.Silver(), Reserve = rf, ActiveID = active });
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
        var consoles = facts.Consoles.OrderBy(c => (c.Id ?? ""), StringComparer.Ordinal).ToList();
        foreach (var row in facts.Requests)
        {
            var option = new Pol.TradeAcquisitionOption
            {
                ID = (row.FactionId ?? "") + "/" + ProtoNames.Of(row.Kind) + "/" + (row.TraderKind ?? ""),
                Faction = row.FactionId ?? "",
                TraderKind = row.TraderKind ?? "",
                LastRequestTick = row.LastRequestTick.GetValueOrDefault(),
                PassingShips = facts.PassingShips.Count,
                LaborTicks = Known(0.0),
                Kind = row.Kind == CommonPb.TradeRequestKind.Orbital ? Pol.TradeAcquisitionKind.Orbital : Pol.TradeAcquisitionKind.Caravan,
            };
            if (row.Eligible is not null)
            {
                option = option with { Eligible = Known(row.Eligible.GetValueOrDefault()) };
            }
            if (row.ArrivalMaxTicks is not null)
            {
                option = option with { LeadDays = Known((double)row.ArrivalMaxTicks.GetValueOrDefault() / GameTime.TicksPerDay) };
            }
            if (row.GoodwillCost is not null)
            {
                option = option with { GoodwillCost = Known(row.GoodwillCost.GetValueOrDefault()) };
            }
            if (row.RelationAfterPayment is not null)
            {
                option = option with { RelationAfterPayment = Known((row.RelationAfterPayment ?? "")) };
            }
            if (row.CooldownRemainingTicks is not null)
            {
                option = option with { CooldownTicks = Known(row.CooldownRemainingTicks.GetValueOrDefault()) };
            }
            if (facts.OrbitalAvailable is not null)
            {
                option = option with { OrbitalAvailable = Known(facts.OrbitalAvailable.GetValueOrDefault()) };
            }
            var negotiators = row.NegotiatorIds.OrderBy(n => n, StringComparer.Ordinal).ToList();
            foreach (var console in consoles)
            {
                foreach (var pawn in negotiators)
                {
                    if (console.NegotiatorIds.Contains(pawn))
                    {
                        option = option with { Console = console.Id ?? "" };
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
                if ((queued.FactionId ?? "") == (row.FactionId ?? "") && queued.Kind == row.Kind)
                {
                    option = option with { Eligible = Known(false) };
                }
            }
            foreach (var work in facts.CommsWork)
            {
                if ((work.FactionId ?? "") == (row.FactionId ?? ""))
                {
                    option = option with { Eligible = Known(false) };
                }
            }
            var compatible = new List<Pol.ResourceKey>();
            foreach (var demand in demands)
            {
                if (catalog.TraderKindCanSupply(option.TraderKind, demand.Demand.Good).TryGetValue(out var can) && can)
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
