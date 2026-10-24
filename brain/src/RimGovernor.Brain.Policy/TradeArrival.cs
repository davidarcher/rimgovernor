using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

// Trader visits as a stock-arrival source (#3869 of #3650). The bridge decodes each storyteller's trader comps by comp class
// (Catalog/Storyteller.cs) and each trader kind's stock generators by generator class (Catalog/TraderStock.cs); the native read gives
// what a visit reads of the colony now (TradeVisitFacts). A visit carries a stock with the chance its trader kind's generators make
// enough of it and today's silver pays the buy price (#3855 Q2 A); visits come at a steady rate from the day the comp starts, so a
// trade source's not-arrived chance is exponential: 1 until From, e^(−rate × carry chance × (t − From)) after (#3855 Q3).

/// <summary>
/// A storyteller comp that sends trader visits (#3869) of <see cref="Incident"/> (an IncidentWorker_TraderCaravanArrival or, when
/// <see cref="Orbital"/>, an IncidentWorker_OrbitalTraderArrival) from <see cref="MinDaysPassed"/> days after settling. A
/// StorytellerCompProperties_FactionInteraction comp sends <see cref="IncidentsPerDay"/> (baseIncidentsPerYear over the year
/// IncidentCycleUtility cycles), scaled by StorytellerUtility.AllyIncidentFraction (full allies only when <see cref="FullAlliesOnly"/>),
/// none while the colony's wealth is below <see cref="MinWealth"/>; a StorytellerCompProperties_OnOffCycle comp sends its
/// <see cref="Cycle"/>'s incidents.
/// </summary>
public sealed record TradeVisitComp(string Incident, bool Orbital, double MinDaysPassed)
{
    public double IncidentsPerDay { get; init; }

    public bool FullAlliesOnly { get; init; }

    public double MinWealth { get; init; }

    public ThreatCycleComp? Cycle { get; init; }
}

/// <summary>
/// One storyteller's decoded trader comps (#3869). <see cref="Unmodelled"/> names the comps that may send a trader the decoder does
/// not model (a comp class it does not know, a category comp drawing trader incidents, a trader incident of another worker class);
/// each is a named finding and adds no visits.
/// </summary>
public sealed record StorytellerTrade
{
    public IReadOnlyList<TradeVisitComp> Comps { get; init; } = [];

    public IReadOnlyList<string> Unmodelled { get; init; } = [];
}

/// <summary>Every storyteller def's trader comps by defName (#3869).</summary>
public sealed record TradeScheduleTable
{
    public IReadOnlyDictionary<string, StorytellerTrade> Storytellers { get; init; } = new Dictionary<string, StorytellerTrade>();
}

/// <summary>
/// One stock generator's draw of a def (#3869): it picks the def with the chance <see cref="Chosen"/> gives and then makes
/// StockGenerator.RandomCountOf of it, from the count range [<see cref="CountMin"/>, <see cref="CountMax"/>] (its
/// customCountRanges row for the def, else countRange) and the total price range [<see cref="PriceMin"/>, <see cref="PriceMax"/>]
/// at the def's base <see cref="MarketValue"/>. The pick: <see cref="Copies"/> of the generator's <see cref="Pool"/> candidates are
/// the def, and it draws [<see cref="DefsMin"/>, <see cref="DefsMax"/>] distinct candidates uniformly (a SingleDef: 1 of 1; a
/// MultiDef: 1 of its thingDefs; a Category or Tag: thingDefCountRange of its tradeable candidates).
/// </summary>
public sealed record StockDraw(int Pool, int Copies, int DefsMin, int DefsMax, int CountMin, int CountMax, double PriceMin, double PriceMax, double MarketValue)
{
    /// <summary>The chance the generator picks the def: Copies × E[min(k, Pool)] ÷ Pool for k uniform in [DefsMin, DefsMax].</summary>
    public double Chosen()
    {
        if (Pool <= 0 || Copies <= 0 || DefsMax < DefsMin)
        {
            return 0;
        }

        var picked = 0.0;
        for (var k = DefsMin; k <= DefsMax; k++)
        {
            picked += Math.Clamp(k, 0, Pool);
        }

        return Math.Min(1, Copies * picked / (DefsMax - DefsMin + 1) / Pool);
    }

    /// <summary>
    /// The distribution of the count RandomCountOf makes, entries 0 to <paramref name="n"/> − 1 exact and entry n the chance of n or
    /// more (a count at or below 0 makes nothing, StockGeneratorUtility.TryMakeForStock).
    /// </summary>
    public double[] Counts(int n)
    {
        var counts = new double[n + 1];
        var countSet = CountMax > 0;
        var priceSet = PriceMax > 0;
        if (!countSet && !priceSet)
        {
            counts[0] = 1;
            return counts;
        }

        if (CountMax < CountMin)
        {
            throw new DomainException($"stock draw count range {CountMin}~{CountMax}");
        }

        if (!priceSet)
        {
            // IntRange.RandomInRange: uniform over the integers of the range.
            var width = CountMax - CountMin + 1;
            for (var c = CountMin; c <= CountMax; c++)
            {
                counts[Math.Clamp(c, 0, n)] += 1.0 / width;
            }

            return counts;
        }

        if (!countSet)
        {
            // Mathf.RoundToInt(totalPriceRange.RandomInRange / BaseMarketValue): count ≥ c while the price draw is at least (c − ½) units.
            var below = 0.0;
            for (var c = 1; c <= n; c++)
            {
                var atLeast = PriceAtLeast((c - 0.5) * MarketValue);
                counts[c - 1] = 1 - atLeast - below;
                below += counts[c - 1];
            }

            counts[n] = 1 - below;
            return counts;
        }

        // Both: up to 100 count draws until one's price lies in the total price range; the 101st is kept whatever its price.
        var size = CountMax - CountMin + 1;
        var accepted = Enumerable.Range(CountMin, size).Count(Includes);
        var miss = 1 - ((double)accepted / size);
        var last = Math.Pow(miss, 100) / size;
        var kept = accepted == 0 ? 0 : (1 - Math.Pow(miss, 100)) / accepted;
        for (var c = CountMin; c <= CountMax; c++)
        {
            counts[Math.Clamp(c, 0, n)] += last + (Includes(c) ? kept : 0);
        }

        return counts;
    }

    // FloatRange.Includes: min ≤ count × value ≤ max.
    private bool Includes(int count) => count * MarketValue >= PriceMin && count * MarketValue <= PriceMax;

    // The chance a uniform draw of [PriceMin, PriceMax] is at least x.
    private double PriceAtLeast(double x) =>
        PriceMax <= PriceMin ? (PriceMin >= x ? 1 : 0) : Math.Clamp((PriceMax - x) / (PriceMax - PriceMin), 0, 1);
}

/// <summary>
/// What a trader kind's generators make of one stock resource (#3869): each generator that makes it (<see cref="Draws"/>,
/// independent), and its unit buy value before the colony's factors, <see cref="UnitValue"/>: the base market value ×
/// TradeUtility.PriceFactorBuy_Global × the multiplier of TraderKindDef.PriceTypeFor's PlayerBuys price type. <see cref="Unread"/>
/// names a fact the catalog could not read (the def's market value); a rule that then waits on it raises
/// <see cref="RequiredFactException"/>.
/// </summary>
public sealed record TraderOffer
{
    public double UnitValue { get; init; }

    public EquatableArray<StockDraw> Draws { get; init; }

    public string Unread { get; init; } = "";

    /// <summary>The chance the generators together make at least <paramref name="count"/> units: their counts convolved.</summary>
    public double AtLeast(long count)
    {
        if (count <= 0)
        {
            return 1;
        }

        var n = (int)Math.Min(count, int.MaxValue - 1);
        var total = new double[n + 1];
        total[0] = 1;
        foreach (var draw in Draws)
        {
            var chosen = draw.Chosen();
            var counts = draw.Counts(n);
            var next = new double[n + 1];
            for (var have = 0; have <= n; have++)
            {
                if (total[have] == 0)
                {
                    continue;
                }

                next[have] += total[have] * (1 - chosen);
                for (var c = 0; c <= n; c++)
                {
                    next[Math.Min(n, have + c)] += total[have] * chosen * counts[c];
                }
            }

            total = next;
        }

        return total[n];
    }
}

/// <summary>
/// One trader kind's stock (#3869): its offers by the stock resource; <see cref="Favor"/> when it trades for royal favor
/// (TradeCurrency.Favor), which the model does not price; <see cref="Unmodelled"/> names the generator classes the decoder does not
/// know that handle a stock resource the kind can sell.
/// </summary>
public sealed record TraderKindStock
{
    public bool Favor { get; init; }

    public IReadOnlyDictionary<Resource, TraderOffer> Offers { get; init; } = new Dictionary<Resource, TraderOffer>();

    public IReadOnlyList<string> Unmodelled { get; init; } = [];
}

/// <summary>Every trader kind's stock of the stock-arrival resources by TraderKindDef defName (#3869).</summary>
public sealed record TraderStockTable
{
    public IReadOnlyDictionary<string, TraderKindStock> Kinds { get; init; } = new Dictionary<string, TraderKindStock>();
}

/// <summary>A trader kind a visit is of, with the chance it is, and its offer of the source's resource.</summary>
public sealed record TradeKindChance(double Chance, TraderOffer Offer);

/// <summary>
/// One trade source of a resource (#3869): one trader comp's visits at <see cref="VisitsPerDay"/> from <see cref="From"/>
/// days after the want, each of a kind in <see cref="Kinds"/>; a visit supplies n units when its kind's generators make n and n ×
/// the buy price is within <see cref="Silver"/>. The buy price is TradeUtility.GetPricePlayerBuy: the offer's unit value ×
/// <see cref="PriceFactor"/> ((1 + the difficulty's tradePriceFactorLoss) × (1 − the best negotiator's TradePriceImprovement)), at
/// least 0.5, rounded above 99.5.
/// </summary>
public sealed record TradeSource(Resource Resource, double From, double VisitsPerDay)
{
    public EquatableArray<TradeKindChance> Kinds { get; init; }

    public double PriceFactor { get; init; }

    public long Silver { get; init; }

    /// <summary>The buy price of one unit at <paramref name="unitValue"/>.</summary>
    public double UnitPrice(double unitValue)
    {
        var price = Math.Max(unitValue * PriceFactor, 0.5);
        return price > 99.5 ? Math.Round(price, MidpointRounding.ToEven) : price;
    }

    /// <summary>The chance one visit supplies <paramref name="count"/> units the colony can pay for.</summary>
    public double Supplies(long count)
    {
        var chance = 0.0;
        foreach (var kind in Kinds)
        {
            if (kind.Offer.Unread.Length != 0)
            {
                throw new RequiredFactException(kind.Offer.Unread);
            }

            if (count * UnitPrice(kind.Offer.UnitValue) <= Silver)
            {
                chance += kind.Chance * kind.Offer.AtLeast(count);
            }
        }

        return chance;
    }

    /// <summary>The rate a day at which visits supply <paramref name="count"/> units.</summary>
    public double Rate(long count) => VisitsPerDay * Supplies(count);

    /// <summary>The chance <paramref name="count"/> units have not arrived <paramref name="t"/> days after the want: 1 until From, then exponential.</summary>
    public double NotArrived(long count, double t) => count <= 0 ? 0 : t <= From ? 1 : Math.Exp(-Rate(count) * (t - From));
}

public static class TradeArrivalRules
{
    /// <summary>Silver income over the horizon is not modelled (#3855 Q2 A): a purchase is paid from today's silver.</summary>
    public const string SilverIncomeUnmodelled = "stock_arrival_trade_silver_income_unmodelled";

    /// <summary>Storyteller comps that may send traders the decoder does not model (<c>:&lt;comps&gt;</c>).</summary>
    public const string CompUnmodelled = "stock_arrival_trade_comp_unmodelled";

    /// <summary>A trader kind's generator classes the decoder does not know that handle a stock resource (<c>:&lt;kind&gt;:&lt;class&gt;</c>).</summary>
    public const string StockUnmodelled = "stock_arrival_trade_stock_unmodelled";

    /// <summary>A trader kind that trades for royal favor, which the model does not price (<c>:&lt;kind&gt;</c>).</summary>
    public const string FavorUnmodelled = "stock_arrival_trade_favor_unmodelled";

    /// <summary>States with no trade visits (true zeros, named): no negotiator, an orbital trader without a powered comms console or trade beacon, a blocked incident.</summary>
    public const string NoNegotiator = "stock_arrival_trade_no_negotiator";

    public const string OrbitalNoComms = "stock_arrival_orbital_no_comms_console";

    public const string OrbitalNoBeacon = "stock_arrival_orbital_no_trade_beacon";

    public const string Blocked = "stock_arrival_trade_blocked";

    /// <summary>
    /// The trade sources (#3869) of <paramref name="storyteller"/>'s trader comps for the colony now: per comp the visits a day (a
    /// FactionInteraction comp's incidents a day × the ally fraction, none below its min wealth; an OnOffCycle comp's cycle rate at
    /// the days since settling and <paramref name="threatPoints"/>), from the days its minDaysPassed still lies ahead; none for an
    /// incident blocked now, an orbital trader without a powered comms console and trade beacon, or a colony without a negotiator.
    /// Each visit is of a kind drawn as the incident draws it (a source uniformly, then a kind by weight) and supplies a resource as
    /// <see cref="TradeSource"/> prices it from <paramref name="silver"/>. An incident the read does not carry, or a kind the catalog
    /// does not know, leaves the trade unread (the returned Unread).
    /// </summary>
    public static (IReadOnlyList<TradeSource> Sources, IReadOnlyList<string> Findings, string Unread) Sources(StorytellerTrade storyteller, TraderStockTable stock,
        TradeVisitFacts facts, double threatPoints, double wealth, long silver)
    {
        var findings = new SortedSet<string>(StringComparer.Ordinal) { SilverIncomeUnmodelled };
        if (storyteller.Unmodelled.Count > 0)
        {
            findings.Add(CompUnmodelled + ":" + string.Join(",", storyteller.Unmodelled));
        }

        if (!facts.Negotiator)
        {
            findings.Add(NoNegotiator);
            return ([], [.. findings], "");
        }

        var priceFactor = (1 + facts.PriceFactorLoss) * (1 - facts.NegotiatorPriceImprovement);
        var sources = new List<TradeSource>();
        foreach (var comp in storyteller.Comps)
        {
            if (facts.Incidents.FirstOrDefault(i => i.Incident == comp.Incident) is not { } incident)
            {
                return ([], [], "trade_incident_unread:" + comp.Incident);
            }

            if (incident.Blocked.Count > 0)
            {
                findings.Add(Blocked + ":" + comp.Incident + ":" + string.Join(",", incident.Blocked));
                continue;
            }

            if (comp.Orbital && (!facts.CommsConsole || !facts.TradeBeacon))
            {
                findings.Add(facts.CommsConsole ? OrbitalNoBeacon : OrbitalNoComms);
                continue;
            }

            var perDay = VisitsPerDay(comp, facts, threatPoints, wealth);
            if (!(perDay > 0))
            {
                continue;
            }

            var kinds = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var source in incident.Sources)
            {
                var total = source.Kinds.Sum(k => Math.Max(0, k.Weight));
                foreach (var kind in source.Kinds.Where(k => k.Weight > 0 && total > 0))
                {
                    kinds[kind.Kind] = kinds.GetValueOrDefault(kind.Kind) + (kind.Weight / total / incident.Sources.Count);
                }
            }

            var offers = new Dictionary<Resource, List<TradeKindChance>>();
            foreach (var (name, chance) in kinds.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (!stock.Kinds.TryGetValue(name, out var kind))
                {
                    return ([], [], "trader_kind_unknown:" + name);
                }

                findings.UnionWith(kind.Unmodelled.Select(c => StockUnmodelled + ":" + name + ":" + c));
                if (kind.Favor)
                {
                    if (kind.Offers.Count > 0)
                    {
                        findings.Add(FavorUnmodelled + ":" + name);
                    }

                    continue;
                }

                foreach (var (resource, offer) in kind.Offers)
                {
                    if (!offers.TryGetValue(resource, out var list))
                    {
                        offers[resource] = list = [];
                    }

                    list.Add(new TradeKindChance(chance, offer));
                }
            }

            var from = Math.Max(0, comp.MinDaysPassed - facts.DaysSinceSettle);
            foreach (var (resource, list) in offers.OrderBy(o => o.Key.Value, StringComparer.Ordinal))
            {
                sources.Add(new TradeSource(resource, from, perDay) { Kinds = EquatableArray.From(list), PriceFactor = priceFactor, Silver = silver });
            }
        }

        return (sources, [.. findings], "");
    }

    // StorytellerComp_FactionInteraction: no visits below minWealth or without an ally fraction; IncidentCycleUtility's count is
    // RoundRandom(base) over a year, then RoundRandom(hits × fraction): unbiased, so the rates multiply. OnOffCycle: ThreatSchedule's
    // cycle rate.
    private static double VisitsPerDay(TradeVisitComp comp, TradeVisitFacts facts, double threatPoints, double wealth)
    {
        if (comp.Cycle is { } cycle)
        {
            return ThreatSchedule.CyclePerDay(cycle, facts.DaysSinceSettle, threatPoints);
        }

        if (comp.MinWealth > 0 && wealth < comp.MinWealth)
        {
            return 0;
        }

        var fraction = comp.FullAlliesOnly ? facts.FullAllyFraction : facts.AllyFraction;
        return fraction > 0 ? comp.IncidentsPerDay * fraction : 0;
    }
}
