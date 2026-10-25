using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>A current trader's exact native participant and held stock. Opening remains a shared labor alternative.</summary>
public sealed record ForecastTradeVisit(Trader Native, EquatableArray<TradeSource> Sources)
{
    public string Id => "trade/visit/" + Native.Key();
    public ForecastActivity Opening(string location) => new(Id + "/open", location, location, [])
    {
        LatestFinish = Native.Visit!.AvailableTicks - 1,
    };
    public ForecastCommitment Commitment() => new(Id, []) { TradeVisit = Id, ActivityCompletions = [Id + "/open"], ExclusiveOutcome = "trade/session" };
}

public static class ForecastTradeVisits
{
    public static EquatableArray<ForecastTradeVisit> Options(IEnumerable<Trader> traders, long silver, long orbitalSilver) => EquatableArray.From(traders
        .Where(t => t.CanTrade && t.Visit is { AvailableTicks: > 0 }).Select(t =>
        {
            var stock = TraderKindStock.Held(t.Visit!.Stock, "trader_stock:" + t.Id);
            return new ForecastTradeVisit(t, EquatableArray.From(stock.Offers.Select(o =>
                new TradeSource(o.Key, 0, 0)
                {
                    Once = true, PurchaseUntil = t.Visit.AvailableTicks / (double)GameTime.TicksPerDay,
                    Silver = t.Orbital ? orbitalSilver : silver, PriceFactor = 1,
                    Kinds = [new(1, o.Value) { Stock = stock }],
                })));
        }));
}
