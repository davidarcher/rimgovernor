using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>A demand-independent comms option and its one native caravan visit. It is not secured stock.</summary>
public sealed record ForecastTradeRequest(TradeRequestOption Native, EquatableArray<TradeSource> Sources)
{
    public string Id => "trade/request/" + Native.FactionId + "/" + Native.TraderKind;

    public ForecastCommitment Commitment() => new(Id, [])
    {
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
            if (request.GoodwillCost < 0 || request.ArrivalMaxTicks <= 0)
                throw new RequiredFactException("trade_request_cost_or_arrival:" + request.FactionId);
            if (!catalog.Kinds.TryGetValue(request.TraderKind, out var kind))
                throw new RequiredFactException("trader_kind_unknown:" + request.TraderKind);
            // Royal-favor stock has no silver price; unsupported generators authorize no stock promise.
            if (kind.Favor) continue;
            var sources = kind.Offers.OrderBy(o => o.Key.Value, StringComparer.Ordinal).Select(o => new TradeSource(o.Key,
                request.ArrivalMaxTicks / (double)GameTime.TicksPerDay, 0)
            {
                Once = true, Kinds = [new TradeKindChance(1, o.Value)], Silver = silver,
                PriceFactor = (1 + facts.PriceFactorLoss) * (1 - facts.NegotiatorPriceImprovement),
            });
            result.Add(new(request, EquatableArray.From(sources)));
        }
        return EquatableArray.From(result);
    }

    private static readonly PerOutcome<ForecastCoupledContext> Contexts = new((_, chosen, context, _) =>
    {
        var ids = chosen.Where(c => c.TradeRequest.Length != 0).Select(c => c.TradeRequest).ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0) return context;
        var requests = context.Stock.Requests.Where(r => ids.Contains(r.Id)).ToArray();
        if (requests.Length != ids.Count) throw new RequiredFactException("trade_request_option");
        return context with
        {
            StockBeforeRequests = context.Stock,
            Stock = context.Stock with { Trade = EquatableArray.From(context.Stock.Trade.Concat(requests.SelectMany(r => r.Sources))) },
        };
    });

    internal static ForecastCoupledContext Context(ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> chosen,
        ForecastCoupledContext context, double horizon) => context.StockBeforeRequests is not null ? context : Contexts.Of(outcome, chosen, context, horizon);

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
        foreach (var request in requests)
        {
            // Goodwill pays the native request, no silver fee. Losing the alliance is infeasible at the native read;
            // the per-faction exclusion prices the shared cooldown as a constraint, never as invented capability-days.
            findings.Add("trade_request_goodwill:" + request.Id + ":" + request.Native.GoodwillCost);
            var after = basis with { Trade = EquatableArray.From(basis.Trade.Concat(request.Sources)) };
            var days = context.InvestmentHorizonDays;
            if (days > horizon / GameTime.TicksPerDay && request.Sources.Any(s => s.From < days))
            {
                var value = Loss(after, days * GameTime.TicksPerDay) - Loss(basis, days * GameTime.TicksPerDay)
                    - (Loss(after, horizon) - Loss(basis, horizon));
                rows.Add(new(request.Id, "", request.Id, 1, value));
            }
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
