using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>A demand-independent comms option and its one native caravan visit. It is not secured stock.</summary>
public sealed record ForecastTradeRequest(TradeRequestOption Native, EquatableArray<TradeSource> Sources)
{
    public string Id => "trade/request/" + Native.FactionId + "/" + Native.TraderKind;

    public ForecastCommitment Commitment() => new(Id, [])
    {
        ActivityCompletions = Native.ConsoleId.Length == 0 ? [] : [Id + "/comms"],
        TradeRequest = Id,
        // All kinds use the faction's same lastTraderRequestTick.
        ExclusiveOutcome = "trade/request/" + Native.FactionId,
    };
}

/// <summary>The selected requests own their stock in one immutable context; all stock-sensitive caches are scoped to it.</summary>
public static class ForecastTradeRequests
{
    public static EquatableArray<ForecastTradeRequest> Options(TraderStockTable catalog, TradeVisitFacts facts, long silver)
    {
        var result = new List<ForecastTradeRequest>();
        foreach (var request in facts.Requests.OrderBy(r => r.FactionId, StringComparer.Ordinal).ThenBy(r => r.TraderKind, StringComparer.Ordinal))
        {
            if (request.Kind != RimGovernor.Brain.GameFacts.Common.TradeRequestKind.Caravan || !request.Eligible
                || request.CooldownRemainingTicks != 0 || request.RelationAfterPayment != "Ally" || request.NegotiatorIds.Count == 0)
                continue;
            if (request.GoodwillCost < 0 || request.ArrivalMaxTicks <= 0 || request.ConsoleId.Length == 0)
                throw new RequiredFactException("trade_request_cost_or_arrival:" + request.FactionId);
            if (!catalog.Kinds.TryGetValue(request.TraderKind, out var kind))
                throw new RequiredFactException("trader_kind_unknown:" + request.TraderKind);
            // Royal-favor stock has no silver price; unsupported generators authorize no stock promise.
            if (kind.Favor) continue;
            var sources = kind.Offers.OrderBy(o => o.Key.Value, StringComparer.Ordinal).Select(o => new TradeSource(o.Key,
                request.ArrivalMaxTicks / (double)GameTime.TicksPerDay, 0)
            {
                Once = true, Kinds = [new TradeKindChance(1, o.Value) { Stock = kind }], Silver = silver,
                PriceFactor = (1 + facts.PriceFactorLoss) * (1 - facts.NegotiatorPriceImprovement),
            });
            result.Add(new(request, EquatableArray.From(sources)));
        }
        return EquatableArray.From(result);
    }

    private static readonly PerOutcome<ForecastCoupledContext> Contexts = new((outcome, chosen, context, horizon) =>
    {
        var ids = chosen.Where(c => c.TradeRequest.Length != 0).Select(c => c.TradeRequest).ToHashSet(StringComparer.Ordinal);
        var missionIds = chosen.Where(c => c.TradeMission.Length != 0).Select(c => c.TradeMission).ToHashSet(StringComparer.Ordinal);
        var visitIds = chosen.Where(c => c.TradeVisit.Length != 0).Select(c => c.TradeVisit).ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0 && missionIds.Count == 0 && visitIds.Count == 0 && context.Stock.Trade.Count == 0) return context;
        var requests = context.Stock.Requests.Where(r => ids.Contains(r.Id)).ToArray();
        if (requests.Length != ids.Count) throw new RequiredFactException("trade_request_option");
        var missions = context.Stock.Missions.Where(m => missionIds.Contains(m.Id)).ToArray();
        if (missions.Length != missionIds.Count) throw new RequiredFactException("trade_mission_option");
        var visits = context.Stock.Visits.Where(v => visitIds.Contains(v.Id)).ToArray();
        if (visits.Length != visitIds.Count) throw new RequiredFactException("trade_visit_option");
        double At(string root, bool completes) => outcome.Plan.Allocations.Where(a => context.TradeWork.Any(w => w.Root == root && w.Task == a.Activity))
            .Select(a => completes ? a.End : a.Depart).DefaultIfEmpty(0).Max() / GameTime.TicksPerDay;
        var selected = context with
        {
            StockBeforeRequests = context.Stock,
            Stock = context.Stock with { Trade = EquatableArray.From(context.Stock.Trade
                .Concat(requests.SelectMany(r => r.Sources.Select(s => s with { From = s.From + At(r.Id, true) })))
                .Concat(missions.SelectMany(m => m.Sources.Select(s => s with { From = s.From + At(m.Id, false) })))
                .Concat(visits.SelectMany(v => v.Sources.Select(s => s with { From = s.From + At(v.Id, true) })))) },
        };
        var needs = TradeStockPurchases.Waits(outcome, selected, chosen, Math.Max(horizon, context.InvestmentHorizonDays * GameTime.TicksPerDay));
        var authorization = Authorize(selected.Stock, needs);
        return selected with { Stock = selected.Stock with
        {
            Trade = EquatableArray.From(selected.Stock.Trade.Select(s => s with { Authorized = authorization.Where(p => p.Resource == s.Resource).Sum(p => p.Count) })),
        } };
    });

    /// <summary>Forecast affordability uses each definition's highest observed/generated buy price. The same purchase rule
    /// consumes medicine, silver and the outcome's waits on a live sheet; stock randomness remains in TradeSource.</summary>
    public static EquatableArray<StockPurchase> Authorize(StockArrival stock, IReadOnlyList<StockNeed> needs)
    {
        var offers = stock.Trade.GroupBy(s => s.Resource).Select(g => new StockPurchaseOffer(g.Key.Value, g.Key, long.MaxValue,
            g.SelectMany(s => s.Kinds.Select(k => TradeSource.UnitPrice(k.Offer.UnitValue, s.PriceFactor))).Max(), 0)).ToArray();
        return TradeStockPurchases.Plan(needs, stock.Medicines, offers, stock.Trade.Select(s => s.Silver).DefaultIfEmpty(0).Max());
    }

    internal static ForecastCoupledContext Context(ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> chosen,
        ForecastCoupledContext context, double horizon) => context.StockBeforeRequests is not null ? context : Contexts.Of(outcome, chosen, context, horizon);

    public static EquatableArray<StockNeed> SelectedWaits(ForecastCoupledSelection selection, ForecastCoupledCandidates candidates)
    {
        var ids = selection.Selection.Selected.ToHashSet(StringComparer.Ordinal);
        var chosen = candidates.Alternatives.Concat(candidates.Incumbents).Where(c => ids.Contains(c.Id)).ToArray();
        var context = Context(selection.Selected, chosen, candidates.Context, selection.Horizon);
        return TradeStockPurchases.Waits(selection.Selected, context, chosen,
            Math.Max(selection.Horizon, context.InvestmentHorizonDays * GameTime.TicksPerDay));
    }

    private sealed record RequestCredit(EquatableArray<ForecastInvestmentContribution> Rows, EquatableArray<string> Findings);
    private static readonly PerOutcome<RequestCredit> Credits = new((outcome, chosen, context, horizon) =>
    {
        if (context.StockBeforeRequests is not { } basis) return new([], []);
        var ids = chosen.Select(c => c.TradeRequest).ToHashSet(StringComparer.Ordinal);
        var requests = basis.Requests.Where(r => ids.Contains(r.Id)).OrderBy(r => r.Id, StringComparer.Ordinal);
        var rows = new List<ForecastInvestmentContribution>();
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var pawns = context.Needs.Pawns.ToDictionary(p => p.Pawn, StringComparer.Ordinal);
        double Loss(StockArrival stock, double ticks)
        {
            // Fresh harm caches at each stock AND horizon: raid and illness distributions depend on both.
            var term = HarmLedger.Of(outcome, context with { Stock = stock }, ticks, new(), pawns, chosen, new());
            findings.UnionWith(term.Findings);
            return term.Loss is LedgerTerm.Priced(var loss) ? loss : throw new RequiredFactException("trade_request_harm");
        }
        var needs = TradeStockPurchases.Waits(outcome, context, chosen, Math.Max(horizon, context.InvestmentHorizonDays * GameTime.TicksPerDay));
        StockArrival Append(StockArrival before, string root, IEnumerable<TradeSource> sources, bool completes)
        {
            var at = outcome.Plan.Allocations.Where(a => context.TradeWork.Any(w => w.Root == root && w.Task == a.Activity))
                .Select(a => completes ? a.End : a.Depart).DefaultIfEmpty(0).Max() / GameTime.TicksPerDay;
            var after = before with { Trade = EquatableArray.From(before.Trade.Concat(sources.Select(s => s with { From = s.From + at }))) };
            var authorization = Authorize(after, needs);
            return after with { Trade = EquatableArray.From(after.Trade.Select(s => s with
                { Authorized = authorization.Where(p => p.Resource == s.Resource).Sum(p => p.Count) })) };
        }
        var initial = Authorize(basis, needs);
        basis = basis with { Trade = EquatableArray.From(basis.Trade.Select(s => s with
            { Authorized = initial.Where(p => p.Resource == s.Resource).Sum(p => p.Count) })) };
        foreach (var request in requests)
        {
            // Goodwill pays the native request, no silver fee. Losing the alliance is infeasible at the native read;
            // the per-faction exclusion prices the shared cooldown as a constraint, never as invented capability-days.
            findings.Add("trade_request_goodwill:" + request.Id + ":" + request.Native.GoodwillCost);
            var after = Append(basis, request.Id, request.Sources, true);
            var days = context.InvestmentHorizonDays;
            if (days > horizon / GameTime.TicksPerDay && request.Sources.Any(s => s.From < days))
            {
                var value = Loss(after, days * GameTime.TicksPerDay) - Loss(basis, days * GameTime.TicksPerDay)
                    - (Loss(after, horizon) - Loss(basis, horizon));
                rows.Add(new(request.Id, "", request.Id, 1, value));
            }
            basis = after;
        }
        var missionIds = chosen.Select(c => c.TradeMission).ToHashSet(StringComparer.Ordinal);
        foreach (var mission in basis.Missions.Where(m => missionIds.Contains(m.Id)).OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            var after = Append(basis, mission.Id, mission.Sources, false);
            var days = context.InvestmentHorizonDays;
            double gain = 0;
            if (days > horizon / GameTime.TicksPerDay && mission.Sources.Any(s => s.From < days))
                gain = Loss(after, days * GameTime.TicksPerDay) - Loss(basis, days * GameTime.TicksPerDay)
                    - (Loss(after, horizon) - Loss(basis, horizon));
            var lost = ForecastSettlementMissions.LostWork(mission, outcome, chosen, context, horizon);
            rows.Add(new(mission.Id, "", mission.Id, 1, gain + lost));
            findings.Add("trade_mission_lost_work:" + mission.Id);
            basis = after;
        }
        var visitIds = chosen.Select(c => c.TradeVisit).ToHashSet(StringComparer.Ordinal);
        foreach (var visit in basis.Visits.Where(v => visitIds.Contains(v.Id)).OrderBy(v => v.Id, StringComparer.Ordinal))
        {
            var after = Append(basis, visit.Id, visit.Sources, true);
            var ticks = context.InvestmentHorizonDays * GameTime.TicksPerDay;
            if (ticks > horizon)
                rows.Add(new(visit.Id, "", visit.Id, 1, Loss(after, ticks) - Loss(basis, ticks) - (Loss(after, horizon) - Loss(basis, horizon))));
            basis = after;
        }
        return new(EquatableArray.From(rows), EquatableArray.From(findings));
    });

    internal static void Credit(ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> chosen, ForecastCoupledContext context,
        double horizon, List<ForecastInvestmentContribution> rows, SortedSet<string> findings)
    {
        var credit = Credits.Of(outcome, chosen, context, horizon);
        rows.AddRange(credit.Rows);
        findings.UnionWith(credit.Findings);
    }
}
