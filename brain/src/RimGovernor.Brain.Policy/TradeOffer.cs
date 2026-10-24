namespace RimGovernor.Brain.Policy;


/// <summary>
/// What one caravan's open trade sheet priced when the negotiator last looked at it (derived state, memory only).
/// A caravan's prices exist only in a session's sheet, so the supply plan reads these records as trade
/// candidates; the live sheet re-prices every purchase when it executes.
/// </summary>
public sealed record TradeOffers
{
    public TradeParticipant Participant { get; init; } = new(TradeParticipantKind.Map, "");

    public string Trader { get; init; } = "";

    /// <summary>The observed tick of the sheet read.</summary>
    public Tick Tick { get; init; }

    /// <summary>The colony's silver on the sheet.</summary>
    public long Silver { get; init; }

    /// <summary>The number of goods stacks the trader carried: with Silver, the two facts whose change makes the record stale (TradeOffersFresh).</summary>
    public long GoodsStacks { get; init; }

    public IReadOnlyList<TradeOffer> Rows { get; init; } = [];
}

/// <summary>
/// One priced row the trader sells: the definition, how many units it holds and the silver price of one. A Food row
/// carries its food classification, a Pawn row the live pawn's sex (Def is its race, what an animal purchase
/// candidate prices), and Goods is every other row.
/// </summary>
public abstract record TradeOffer
{
    private protected TradeOffer() { }

    public required string Def { get; init; }

    public required long Count { get; init; }

    public required double Price { get; init; }

    public sealed record Goods : TradeOffer;

    public sealed record Food : TradeOffer
    {
        public required TradeFoodGood Good { get; init; }
    }

    public sealed record Pawn : TradeOffer
    {
        public required RimGovernor.Contracts.Wire.Defs.Gender Gender { get; init; }
    }
}

public static partial class TradePolicyRules
{
    /// <summary>A record is read again once it is older than this (a few game hours)...</summary>
    public static readonly Tick TradeOffersMaxAgeTicks = new(3L * GameTime.TicksPerHour);

    /// <summary>...or the colony's silver or the trader's goods-stack count moved by more than this share of the recorded value.</summary>
    public const double TradeOffersShift = 0.25;

    /// <summary>Whether a record still describes the trader at tick now, given the colony's current silver and the trader's current goods stacks.</summary>
    public static bool TradeOffersFresh(TradeOffers o, Tick now, long silver, long goodsStacks)
    {
        if (o.Tick < now && now.Value - o.Tick.Value > TradeOffersMaxAgeTicks.Value)
        {
            return false;
        }
        return !Shifted(o.Silver, silver) && !Shifted(o.GoodsStacks, goodsStacks);
    }

    private static bool Shifted(long was, long now) => Math.Abs((double)(now - was)) > TradeOffersShift * Math.Max(1, Math.Abs((double)was));

    /// <summary>Names the candidate of one resource bought from one trader.</summary>
    public static string TradeCandidateID(string trader, Resource resource) => trader + "/" + resource.Value;

    /// <summary>
    /// The catalog rows of buying resource from the recorded offers: the cheapest priced row of each trader, as
    /// many units as the deficit, the trader's stock and the silver above the reserve allow.
    /// </summary>
    public static IReadOnlyList<SupplyCandidate> TradeOfferCandidates(Resource resource, IReadOnlyList<TradeOffers> offers, long deficit, long silver, long reserve)
    {
        var output = new List<SupplyCandidate>();
        foreach (var record in offers)
        {
            if (record.Participant.Kind == TradeParticipantKind.Settlement)
            {
                continue;
            }
            TradeOffer? best = null;
            foreach (var row in record.Rows)
            {
                if (new Resource(row.Def) != resource || row.Count <= 0 || !Finite(row.Price) || row.Price <= 0)
                {
                    continue;
                }
                if (best is null || row.Price < best.Price)
                {
                    best = row;
                }
            }
            if (best is null)
            {
                continue;
            }
            var afford = (long)Math.Floor((double)(silver - reserve) / best.Price);
            if (AcquisitionCatalogRules.TradeCandidate(resource, record.Trader, Math.Min(Math.Min(deficit, best.Count), afford), best.Price, out var c))
            {
                output.Add(c);
            }
        }
        return output;
    }

    /// <summary>
    /// The food candidates of buying from the recorded offers of the traders present: one one-shot channel per
    /// trader (ID is the trader id) holding the nutrition its cheapest-per-nutrition food rows give, as far as the
    /// silver above the reserve and want (nutrition) allow. It has no lead (the walk is a fraction of a day), no
    /// steady work and the silver as its upfront cost, priced as labor (TradeLaborPerSilver). A trader without a
    /// record, or whose record prices no food, has no candidate: an arrival the colony has not looked at is never
    /// planned for.
    /// </summary>
    public static IReadOnlyList<SupplyCandidate> TradeFoodChannels(IReadOnlyList<TradeOffers> offers, long silver, long reserve, double want)
    {
        var output = new List<SupplyCandidate>();
        foreach (var record in offers)
        {
            if (record.Participant.Kind == TradeParticipantKind.Settlement)
            {
                continue;
            }
            static double PerNutrition(TradeOffer.Food r) => r.Price / r.Good.Nutrition;

            var rows = record
                .Rows.OfType<TradeOffer.Food>()
                .Where(row => ValidTradeFood(row.Good) && row.Count > 0 && Finite(row.Price) && row.Price > 0)
                .OrderBy(
                    x => x,
                    Comparer<TradeOffer.Food>.Create(
                        (a, b) =>
                        {
                            var pa = PerNutrition(a);
                            var pb = PerNutrition(b);
                            if (pa != pb)
                            {
                                return pa < pb ? -1 : 1;
                            }
                            return string.CompareOrdinal(a.Def, b.Def);
                        }
                    )
                )
                .ToList();
            double budget = silver - reserve;
            var wanted = want;
            var nutrition = 0.0;
            var spent = 0.0;
            foreach (var row in rows)
            {
                var g = row.Good;
                var count = Math.Min(row.Count, Math.Min(Math.Floor(budget / row.Price), Math.Ceiling(wanted / g.Nutrition)));
                if (!(count > 0))
                {
                    continue;
                }
                nutrition += count * g.Nutrition;
                spent += count * row.Price;
                budget -= count * row.Price;
                wanted -= count * g.Nutrition;
            }
            var stock = (long)Math.Floor(nutrition);
            if (stock >= 1)
            {
                var c = CandidateRules.FoodCandidate(CandidateKind.Trade, record.Trader, new YieldSupply.Stock(stock), CandidateState.Closed, new CandidateLead.Days(0), new CandidateWork.PerDay(0));
                output.Add(
                    c with
                    {
                        UpfrontCost = c.UpfrontCost with { LaborTicks = spent * AcquisitionCatalogRules.TradeLaborPerSilver },
                        Terms = EquatableArray.From([new CandidateTerm("trade_silver", spent)]),
                    }
                );
            }
        }
        return output;
    }

    /// <summary>The nutrition the plan opened to buy from trader.</summary>
    public static double PlannedTradeNutrition(FoodPlan plan, string trader)
    {
        var total = 0.0;
        foreach (var e in plan.Portfolio)
        {
            if (e.Channel.Kind == CandidateKind.Trade && e.Channel.Id == trader && e.Decision == FoodPlanDecision.Open)
            {
                if (e.Channel.Nutrition() is { } y && y.StockOf(out var stock))
                {
                    total += stock;
                }
            }
        }
        return total;
    }
}
