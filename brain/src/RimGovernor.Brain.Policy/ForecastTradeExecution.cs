namespace RimGovernor.Brain.Policy;

/// <summary>An existing trade action's place in the shared schedule. Its Method owns execution; later work is reconsidered
/// from fresh native facts after the external action completes, just like existing service boundaries.</summary>
public sealed record ForecastTradeWork(string Root, string Task, string Pawn, IActionPayload Action)
{
    public ForecastSettlementMission? Mission { get; init; }
}

public static class ForecastTradeExecution
{
    public static EquatableArray<ForecastTradeWork> Work(StockArrival stock)
    {
        var rows = new List<ForecastTradeWork>();
        foreach (var request in stock.Requests.Where(r => r.Native.ConsoleId.Length != 0))
            foreach (var pawn in request.Native.NegotiatorIds)
                rows.Add(new(request.Id, request.Id + "/comms", pawn,
                    new CommsTradeRequest(TradeRequestKind.Caravan, request.Native.FactionId, request.Native.TraderKind,
                        request.Native.ConsoleId, pawn, request.Native.LastRequestTick)));
        foreach (var mission in stock.Missions.Where(m => m.Departure is not null))
            foreach (var pawn in mission.Crew)
                rows.Add(new(mission.Id, mission.Id + "/depart/" + pawn, pawn, mission.Departure!) { Mission = mission });
        foreach (var visit in stock.Visits)
            rows.Add(new(visit.Id, visit.Id + "/open", visit.Native.Visit!.Negotiator.Value,
                Trade.NewTradeOpen(visit.Native.Key(), visit.Native.Visit.Negotiator, false).WithParticipant(visit.Native.Participant())));
        return EquatableArray.From(rows);
    }

    /// <summary>An external action starts only when it is the participant's first scheduled task and that task departs now.
    /// Earlier shared work runs first; its existing continuation requests a fresh review before this action is considered again.</summary>
    public static EquatableArray<ForecastTradeWork> Ready(ForecastActivityOutcomeComparison selection, ForecastCoupledContext context)
    {
        if (selection.Plan is not { } plan) return [];
        var rows = context.TradeWork.Where(w => selection.Selected.Contains(w.Root, StringComparer.Ordinal)
            && plan.Allocations.Any(a => a.Activity == w.Task && a.Pawn == w.Pawn && a.Depart <= 0
                && plan.Allocations.Where(before => before.Pawn == a.Pawn).OrderBy(before => before.Depart)
                    .ThenBy(before => before.Activity, StringComparer.Ordinal).First().Activity == a.Activity)).ToArray();
        return EquatableArray.From(rows.Where(w => w.Mission is null || w.Mission.Crew.All(p => rows.Any(r => r.Root == w.Root && r.Pawn == p))));
    }

    public static TradeMission Mission(ForecastSettlementMission option, GenerationSnapshot world, long silver,
        IReadOnlyList<StockNeed> needs, IReadOnlyList<Resource> medicines)
    {
        var departure = option.Departure ?? throw new RequiredFactException("trade_mission_departure:" + option.Settlement);
        var offers = option.Sources.GroupBy(s => s.Resource).Select(g => new StockPurchaseOffer(g.Key.Value, g.Key, long.MaxValue,
            g.SelectMany(s => s.Kinds.Select(k => s.UnitPrice(k.Offer.UnitValue))).Max(), 0));
        var authorization = TradeStockPurchases.Plan(needs, medicines, offers, silver);
        var demand = authorization.Select(p => new CargoItem(p.Resource.Value, (ulong)p.Count)).OrderBy(c => c.Definition, StringComparer.Ordinal).ToArray();
        var mission = new TradeMission
        {
            HomeColony = world.Colony, HomeMap = world.Map, HomeTile = option.HomeTile,
            Settlement = option.Settlement, SettlementTile = departure.DestinationTile, Crew = departure.Crew,
            Negotiator = departure.Crew[0], SilverBudget = silver, Demand = EquatableArray.From(demand), Pack = departure.Cargo,
            Phase = TradeMissionPhase.Departing, ReturnHome = true,
        };
        mission.Validate();
        return mission;
    }
}
