using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

// Trader visits as a stock-arrival source (#3869 of #3650). The bridge decodes each storyteller's trader comps by comp class
// (Catalog/Storyteller.cs) and each trader kind's stock generators by generator class (Catalog/TraderStock.cs); the native read gives
// what a visit reads of the colony now (TradeVisitFacts). A visit carries a stock with the chance its trader kind's generators make
// enough of it and today's silver pays the buy price (#3855 Q2 A); visits come at a steady rate from the day the comp starts, so a
// trade source's not-arrived chance is exponential: 1 until From, e^(−rate × carry chance × (t − From)) after (#3855 Q3). A comp
// that draws its incident from categories (#3909) sends each incident with the chance its draw gives at today's facts (#3909 Q3).

/// <summary>
/// The exact worker class of an incident a trader comp can draw (#3909). A class the decoder does not know (a mod's, a subclass of
/// any of these) is no worker: the comp drawing it is named <see cref="TradeArrivalRules.CompUnmodelled"/>.
/// </summary>
public enum TradeWorker
{
    /// <summary>IncidentWorker_TraderCaravanArrival: a candidate faction, then one of its caravan kinds.</summary>
    Caravan,

    /// <summary>IncidentWorker_OrbitalTraderArrival: an orbital kind that can spawn; traded with by comms console, paid at a beacon.</summary>
    Orbital,

    /// <summary>IncidentWorker_VisitorGroup: a candidate faction's visitors, one of whom trades with TraderChance.</summary>
    Visitors,

    /// <summary>IncidentWorker_CaravanArrivalTributeCollector: the Empire's caravan of a TributeCollector kind.</summary>
    TributeCollector,

    /// <summary>IncidentWorker_TravelerGroup: never a trader; drawn against the others of its category.</summary>
    Travelers,
}

/// <summary>
/// An incident a trader comp can draw (#3909): its IncidentCategoryDef, worker and earliestDay; when it
/// <see cref="ChangesPopulation"/> (a populationEffect other than None) its <see cref="IntentOffset"/>, the offset
/// StorytellerComp.IncidentChanceFactor_PopulationIntent adds to the population intent for its effect.
/// </summary>
public sealed record TradeIncident(string Category, TradeWorker Worker, double EarliestDay)
{
    public bool ChangesPopulation { get; init; }

    public double IntentOffset { get; init; }
}

/// <summary>How a comp's rate is set (#3909): its class's interval rule.</summary>
public enum TradeCompRate
{
    /// <summary>StorytellerComp_FactionInteraction: baseIncidentsPerYear over the 60-day year, the ally fraction, the minWealth and minDanger gates.</summary>
    FactionInteraction,

    /// <summary>StorytellerComp_OnOffCycle: its cycle (ThreatSchedule.CyclePerDay).</summary>
    OnOffCycle,

    /// <summary>StorytellerComp_RandomMain, CategoryMTB, SingleMTB: Rand.MTBEventOccurs, 1 ÷ (mtbDays × the days-passed factor) a day.</summary>
    Mtb,
}

/// <summary>How a category comp draws (#3909), as its class does.</summary>
public enum TradeDrawRule
{
    /// <summary>
    /// StorytellerComp_RandomMain: a category by weight (ThreatBig forced past maxThreatBigIntervalDays), redrawn without it while
    /// the category has no usable incident, then TrySelectRandomIncident.
    /// </summary>
    RandomMain,

    /// <summary>StorytellerComp_CategoryMTB: its category, TrySelectRandomIncident; nothing when no incident is usable.</summary>
    CategoryMtb,

    /// <summary>StorytellerComp_OnOffCycle by category: UsableIncidentsInCategory by IncidentChanceFinal weight.</summary>
    OnOffCycle,
}

/// <summary>
/// One category a category comp draws, at its weight (RandomMain's categoryWeights; 1 for the one category of the others).
/// <see cref="Trader"/>: it holds a trader incident, so the decoder reads its incidents' usability exactly (#3909 Q2 B); any other
/// category counts as always drawable.
/// </summary>
public sealed record TradeCategoryWeight(string Category, double Weight, bool Trader);

/// <summary>
/// A comp's draw of an incident from categories (#3909): <see cref="Categories"/> other than ThreatBig, whose RandomMain weight is
/// <see cref="ThreatBigWeight"/> (forced after <see cref="MaxThreatBigIntervalDays"/> since the last), and the comp's
/// minIncChancePopulationIntentFactor (<see cref="MinPopulationIntentFactor"/>, IncidentChanceFactor_PopulationIntent's floor).
/// </summary>
public sealed record TradeCategoryDraw(TradeDrawRule Rule)
{
    public EquatableArray<TradeCategoryWeight> Categories { get; init; }

    public double ThreatBigWeight { get; init; }

    public double MaxThreatBigIntervalDays { get; init; }

    public double MinPopulationIntentFactor { get; init; }
}

/// <summary>
/// A storyteller comp that may send trader visits (#3869, #3909), from <see cref="MinDaysPassed"/> days after settling (with its
/// incident's earliestDay, and before forceRaidEnemyBeforeDaysPassed an on-off cycle sends raids). Its <see cref="Rate"/>: a
/// FactionInteraction comp sends <see cref="IncidentsPerDay"/> (baseIncidentsPerYear over the year IncidentCycleUtility cycles),
/// scaled by StorytellerUtility.AllyIncidentFraction (full allies only when <see cref="FullAlliesOnly"/>), none while the colony's
/// wealth is below <see cref="MinWealth"/> or the map's danger below <see cref="MinDanger"/>; an OnOffCycle comp its
/// <see cref="Cycle"/>'s incidents; an MTB comp <see cref="IncidentsPerDay"/> (1 ÷ mtbDays) over its
/// <see cref="MtbFactorByDaysPassed"/>. Each event is <see cref="Incident"/>, or drawn as <see cref="Draw"/> says (one of the two).
/// </summary>
public sealed record TradeVisitComp(TradeCompRate Rate, double MinDaysPassed)
{
    public string Incident { get; init; } = "";

    public TradeCategoryDraw? Draw { get; init; }

    public double IncidentsPerDay { get; init; }

    public EquatableArray<(double Days, double Factor)> MtbFactorByDaysPassed { get; init; }

    public bool FullAlliesOnly { get; init; }

    public double MinWealth { get; init; }

    public StoryDangerLevel MinDanger { get; init; }

    public ThreatCycleComp? Cycle { get; init; }
}

/// <summary>
/// One storyteller's decoded trader comps (#3869). <see cref="Unmodelled"/> names the comps that may send a trader the decoder does
/// not model (a comp class it does not know, a trader-like incident of another worker class, a trader category holding one, a cycle
/// scaled by the progress score); each is a named finding and adds no visits.
/// </summary>
public sealed record StorytellerTrade
{
    public IReadOnlyList<TradeVisitComp> Comps { get; init; } = [];

    public IReadOnlyList<string> Unmodelled { get; init; } = [];
}

/// <summary>
/// Every storyteller def's trader comps by defName (#3869), the incidents they draw by defName (#3909: each comp's incident and
/// every incident of a trader category a comp draws), the FactionDefs whose Peaceful group makers hold a child kind
/// (<see cref="ChildVisitorFactions"/>, #3909 Q1 B), and StorytellerComp.IncreasesPopChanceByPopIntentCurve
/// (<see cref="PopulationChanceByIntent"/>).
/// </summary>
public sealed record TradeScheduleTable
{
    public IReadOnlyDictionary<string, StorytellerTrade> Storytellers { get; init; } = new Dictionary<string, StorytellerTrade>();

    public IReadOnlyDictionary<string, TradeIncident> Incidents { get; init; } = new Dictionary<string, TradeIncident>();

    public IReadOnlySet<string> ChildVisitorFactions { get; init; } = new HashSet<string>();

    public EquatableArray<(double Intent, double Chance)> PopulationChanceByIntent { get; init; }
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
        return Distribution(n)[n];
    }

    /// <summary>
    /// The units a visit sells when it can pay for at most <paramref name="cap"/> (#3907): E[min(count the generators make, cap)]. The
    /// count never exceeds <see cref="MaxCount"/>, which bounds the distribution.
    /// </summary>
    public double Expected(long cap)
    {
        var n = (int)Math.Min(Math.Min(cap, MaxCount), int.MaxValue - 1);
        if (n <= 0)
        {
            return 0;
        }

        var total = Distribution(n);
        var expected = n * total[n];
        for (var have = 1; have < n; have++)
        {
            expected += have * total[have];
        }

        return expected;
    }

    /// <summary>The most units the generators can make together; <see cref="long.MaxValue"/> for a price range over a def with no market value.</summary>
    public long MaxCount
    {
        get
        {
            var most = 0L;
            foreach (var draw in Draws)
            {
                if (draw.CountMax > 0)
                {
                    most += draw.CountMax;
                }
                else if (draw.PriceMax > 0)
                {
                    if (!(draw.MarketValue > 0))
                    {
                        return long.MaxValue;
                    }

                    most += (long)Math.Ceiling((draw.PriceMax / draw.MarketValue) + 0.5);
                }
            }

            return most;
        }
    }

    // The chance the generators together make exactly have units, for have below n, and n or more at n.
    private double[] Distribution(int n)
    {
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

        return total;
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

    /// <summary>
    /// The units a day the routine trade buys from this comp's visits (#3907): the visits a day × the expected units a visit sells that
    /// <see cref="Silver"/> pays for (a buy takes what the silver allows, unlike a part's whole count).
    /// </summary>
    public double UnitsPerDay()
    {
        var units = 0.0;
        foreach (var kind in Kinds)
        {
            if (kind.Offer.Unread.Length != 0)
            {
                throw new RequiredFactException(kind.Offer.Unread);
            }

            var price = UnitPrice(kind.Offer.UnitValue);
            var cap = (long)Math.Min(Math.Floor(Silver / price), long.MaxValue / 2.0);
            units += kind.Chance * kind.Offer.Expected(cap);
        }

        return VisitsPerDay * units;
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

    /// <summary>
    /// A visitor faction whose Peaceful group makers hold a child kind (<c>:&lt;faction&gt;</c>, #3909 Q1 B): whether its group
    /// holds an adult to trade is not modelled, so its visitor groups supply nothing.
    /// </summary>
    public const string VisitorChildUnmodelled = "stock_arrival_visitor_child_group_unmodelled";

    /// <summary>States with no trade visits (true zeros, named): no negotiator, an orbital trader without a powered comms console or trade beacon, a blocked incident.</summary>
    public const string NoNegotiator = "stock_arrival_trade_no_negotiator";

    public const string OrbitalNoComms = "stock_arrival_orbital_no_comms_console";

    public const string OrbitalNoBeacon = "stock_arrival_orbital_no_trade_beacon";

    public const string Blocked = "stock_arrival_trade_blocked";

    /// <summary>An incident that fires with no trader kind to send (<c>:&lt;incident&gt;</c>): no orbital kind can spawn, no visitor faction trades.</summary>
    public const string NoTraderKind = "stock_arrival_trade_no_trader_kind";

    // IncidentWorker_VisitorGroup.TraderChance (private const): a visitor group converts one adult to a trader with this chance.
    internal const double VisitorTraderChance = 0.75;

    /// <summary>
    /// The trade sources (#3869, #3909) of <paramref name="storyteller"/>'s trader comps for the colony now. Per comp the events a
    /// day (a FactionInteraction comp's incidents a day × the ally fraction, none below its min wealth or min danger; an OnOffCycle
    /// comp's cycle rate at the days since settling and <paramref name="threatPoints"/>; an MTB comp's 1 ÷ mtbDays over its
    /// days-passed factor), from the days its start still lies ahead; each event is the comp's incident or one drawn from its
    /// categories as <see cref="IncidentChances"/> gives. None for an incident blocked now, an orbital trader without a powered comms
    /// console and trade beacon, travelers, or a colony without a negotiator. Each visit is of a kind drawn as the incident draws it
    /// (a source uniformly, then a kind by weight; a visitor group's trader with <see cref="VisitorTraderChance"/>) and supplies a
    /// resource as <see cref="TradeSource"/> prices it from <paramref name="silver"/>. An incident the read or the catalog does not
    /// carry, or a kind the catalog does not know, leaves the trade unread (the returned Unread).
    /// </summary>
    public static (IReadOnlyList<TradeSource> Sources, IReadOnlyList<string> Findings, string Unread) Sources(TradeScheduleTable schedule, StorytellerTrade storyteller,
        TraderStockTable stock, TradeVisitFacts facts, double threatPoints, double wealth, long silver)
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

        var reads = new Dictionary<string, TraderIncidentRead>(StringComparer.Ordinal);
        foreach (var read in facts.Incidents)
        {
            reads[read.Incident] = read;
        }

        var priceFactor = (1 + facts.PriceFactorLoss) * (1 - facts.NegotiatorPriceImprovement);
        var sources = new List<TradeSource>();
        foreach (var comp in storyteller.Comps)
        {
            var (chances, unread) = IncidentChances(schedule, comp, reads, facts);
            if (unread.Length != 0)
            {
                return ([], [], unread);
            }

            var perDay = VisitsPerDay(comp, facts, threatPoints, wealth);
            var from = Math.Max(0, comp.MinDaysPassed - facts.DaysSinceSettle);
            foreach (var (name, chance) in chances)
            {
                var incident = schedule.Incidents[name];
                var read = reads[name];
                if (incident.Worker == TradeWorker.Travelers)
                {
                    continue;
                }

                if (read.Blocked.Count > 0)
                {
                    findings.Add(Blocked + ":" + name + ":" + string.Join(",", read.Blocked));
                    continue;
                }

                if (!(chance > 0) || !(perDay > 0))
                {
                    continue;
                }

                if (incident.Worker == TradeWorker.Orbital && (!facts.CommsConsole || !facts.TradeBeacon))
                {
                    findings.Add(facts.CommsConsole ? OrbitalNoBeacon : OrbitalNoComms);
                    continue;
                }

                var kinds = KindChances(schedule, incident, read, findings);
                if (kinds.Count == 0)
                {
                    findings.Add(NoTraderKind + ":" + name);
                }

                var offers = new Dictionary<Resource, List<TradeKindChance>>();
                foreach (var (kindName, kindChance) in kinds.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    if (!stock.Kinds.TryGetValue(kindName, out var kind))
                    {
                        return ([], [], "trader_kind_unknown:" + kindName);
                    }

                    findings.UnionWith(kind.Unmodelled.Select(c => StockUnmodelled + ":" + kindName + ":" + c));
                    if (kind.Favor)
                    {
                        if (kind.Offers.Count > 0)
                        {
                            findings.Add(FavorUnmodelled + ":" + kindName);
                        }

                        continue;
                    }

                    foreach (var (resource, offer) in kind.Offers)
                    {
                        if (!offers.TryGetValue(resource, out var list))
                        {
                            offers[resource] = list = [];
                        }

                        list.Add(new TradeKindChance(kindChance, offer));
                    }
                }

                foreach (var (resource, list) in offers.OrderBy(o => o.Key.Value, StringComparer.Ordinal))
                {
                    sources.Add(new TradeSource(resource, from, perDay * chance) { Kinds = EquatableArray.From(list), PriceFactor = priceFactor, Silver = silver });
                }
            }
        }

        return (sources, [.. findings], "");
    }

    /// <summary>
    /// The chance each event of <paramref name="comp"/> is each incident (#3909), at today's facts (#3909 Q3). A comp of one incident
    /// sends it. A category comp draws as its class does: a trader category's incident is usable when it is not blocked, its
    /// earliestDay has come by the comp's start or today, and its IncidentChanceFinal (the read weight × the population-intent factor)
    /// is above 0; any other category counts as always drawable (#3909 Q2 B). RandomMain draws ThreatBig with its long-run fraction
    /// (<see cref="ThreatBigFraction"/>) and each other category by its weight over the usable categories' weight, then
    /// TrySelectRandomIncident; CategoryMTB TrySelectRandomIncident of its category; an OnOffCycle by category the usable incidents by
    /// weight.
    /// </summary>
    internal static (IReadOnlyList<(string Incident, double Chance)> Chances, string Unread) IncidentChances(TradeScheduleTable schedule, TradeVisitComp comp,
        IReadOnlyDictionary<string, TraderIncidentRead> reads, TradeVisitFacts facts)
    {
        if (comp.Draw is not { } draw)
        {
            if (!schedule.Incidents.ContainsKey(comp.Incident))
            {
                return ([], "trade_incident_unknown:" + comp.Incident);
            }

            return reads.ContainsKey(comp.Incident) ? ([(comp.Incident, 1.0)], "") : ([], "trade_incident_unread:" + comp.Incident);
        }

        // Each trader category's usable incidents with their IncidentChanceFinal weights.
        var usable = new Dictionary<string, List<(string Incident, TradeIncident Def, double Weight)>>(StringComparer.Ordinal);
        foreach (var category in draw.Categories.Where(c => c.Trader))
        {
            var list = new List<(string, TradeIncident, double)>();
            foreach (var (name, incident) in schedule.Incidents.Where(i => i.Value.Category == category.Category).OrderBy(i => i.Key, StringComparer.Ordinal))
            {
                if (!reads.TryGetValue(name, out var read))
                {
                    return ([], "trade_incident_unread:" + name);
                }

                var weight = Math.Max(0, read.Weight * IntentFactor(incident, facts.PopulationIntent, draw.MinPopulationIntentFactor));
                if (read.Blocked.Count == 0 && incident.EarliestDay <= Math.Max(facts.DaysSinceSettle, comp.MinDaysPassed) && weight > 0)
                {
                    list.Add((name, incident, weight));
                }
            }

            usable[category.Category] = list;
        }

        var chances = new List<(string, double)>();
        switch (draw.Rule)
        {
            case TradeDrawRule.RandomMain:
            {
                var drawable = draw.Categories.Where(c => !c.Trader || usable[c.Category].Count > 0).ToList();
                var others = drawable.Sum(c => Math.Max(0, c.Weight));
                var threatBig = Math.Max(0, draw.ThreatBigWeight);
                if (!(others > 0))
                {
                    break;
                }

                var notThreatBig = 1 - ThreatBigFraction(threatBig / (threatBig + others), comp.IncidentsPerDay, draw.MaxThreatBigIntervalDays);
                foreach (var category in drawable.Where(c => c.Trader && c.Weight > 0))
                {
                    chances.AddRange(SelectRandomIncident(usable[category.Category], facts.PopulationIntent, schedule.PopulationChanceByIntent)
                        .Select(i => (i.Incident, notThreatBig * category.Weight / others * i.Chance)));
                }

                break;
            }

            case TradeDrawRule.CategoryMtb:
                foreach (var category in draw.Categories.Where(c => c.Trader))
                {
                    chances.AddRange(SelectRandomIncident(usable[category.Category], facts.PopulationIntent, schedule.PopulationChanceByIntent));
                }

                break;
            case TradeDrawRule.OnOffCycle:
                foreach (var category in draw.Categories.Where(c => c.Trader))
                {
                    var total = usable[category.Category].Sum(i => i.Weight);
                    chances.AddRange(usable[category.Category].Select(i => (i.Incident, i.Weight / total)));
                }

                break;
        }

        return (chances, "");
    }

    /// <summary>
    /// The long-run fraction of a RandomMain comp's events that are ThreatBig: a renewal from each ThreatBig, whose events before
    /// <paramref name="maxIntervalDays"/> are ThreatBig with <paramref name="share"/> (its weight over the usable weight) and whose
    /// first event after is forced ThreatBig. At <paramref name="eventsPerDay"/> λ and share p the cycle holds
    /// 1 + (1 − p)(1 − e^(−λpM)) ÷ p events (1 + λM at p = 0), one of them ThreatBig.
    /// </summary>
    internal static double ThreatBigFraction(double share, double eventsPerDay, double maxIntervalDays)
    {
        var events = share > 0
            ? 1 + ((1 - share) * -double.ExpM1(-eventsPerDay * share * maxIntervalDays) / share)
            : 1 + (eventsPerDay * maxIntervalDays);
        return 1 / events;
    }

    /// <summary>
    /// StorytellerComp.TrySelectRandomIncident's chance of each usable incident: with the IncreasesPopChanceByPopIntentCurve chance at
    /// the population intent, one that changes the population by weight, else one that does not, either falling back to all of them
    /// when its own weight is 0; nothing when all weigh 0.
    /// </summary>
    internal static IEnumerable<(string Incident, double Chance)> SelectRandomIncident(IReadOnlyList<(string Incident, TradeIncident Def, double Weight)> usable,
        double populationIntent, IReadOnlyList<(double X, double Y)> chanceByIntent)
    {
        var all = usable.Sum(i => i.Weight);
        if (!(all > 0))
        {
            yield break;
        }

        var increase = Math.Clamp(ThreatSchedule.Curve(chanceByIntent, populationIntent), 0, 1);
        var changing = usable.Where(i => i.Def.ChangesPopulation).Sum(i => i.Weight);
        var steady = all - changing;
        foreach (var (incident, def, weight) in usable)
        {
            var changes = def.ChangesPopulation;
            var whenIncrease = changing > 0 ? (changes ? weight / changing : 0) : weight / all;
            var whenNot = steady > 0 ? (changes ? 0 : weight / steady) : weight / all;
            yield return (incident, (increase * whenIncrease) + ((1 - increase) * whenNot));
        }
    }

    // StorytellerComp.IncidentChanceFactor_PopulationIntent: 1 for no population effect, else max(intent + the effect's offset, the
    // comp's floor).
    private static double IntentFactor(TradeIncident incident, double intent, double floor) =>
        incident.ChangesPopulation ? Math.Max(intent + incident.IntentOffset, floor) : 1;

    // The kind a visit of the incident is of: a source (a candidate faction) uniformly, then a kind by weight; a visitor group's
    // trader only with TraderChance, and none of a faction whose Peaceful groups may be children (named).
    private static Dictionary<string, double> KindChances(TradeScheduleTable schedule, TradeIncident incident, TraderIncidentRead read, SortedSet<string> findings)
    {
        var kinds = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var source in read.Sources)
        {
            var share = 1.0 / read.Sources.Count;
            if (incident.Worker == TradeWorker.Visitors)
            {
                if (schedule.ChildVisitorFactions.Contains(source.Faction))
                {
                    if (source.Kinds.Count > 0)
                    {
                        findings.Add(VisitorChildUnmodelled + ":" + source.Faction);
                    }

                    continue;
                }

                share *= VisitorTraderChance;
            }

            var total = source.Kinds.Sum(k => Math.Max(0, k.Weight));
            foreach (var kind in source.Kinds.Where(k => k.Weight > 0 && total > 0))
            {
                kinds[kind.Kind] = kinds.GetValueOrDefault(kind.Kind) + (kind.Weight / total * share);
            }
        }

        return kinds;
    }

    // StorytellerComp_FactionInteraction: no visits below minWealth or minDanger or without an ally fraction; IncidentCycleUtility's
    // count is RoundRandom(base) over a year, then RoundRandom(hits × fraction): unbiased, so the rates multiply. OnOffCycle:
    // ThreatSchedule's cycle rate. MTB: Rand.MTBEventOccurs at 1 ÷ (mtbDays × the days-passed factor) a day.
    private static double VisitsPerDay(TradeVisitComp comp, TradeVisitFacts facts, double threatPoints, double wealth)
    {
        switch (comp.Rate)
        {
            case TradeCompRate.OnOffCycle:
                return comp.Cycle is { } cycle ? ThreatSchedule.CyclePerDay(cycle, facts.DaysSinceSettle, threatPoints) : throw new DomainException("trade cycle comp without a cycle");
            case TradeCompRate.Mtb:
                var factor = ThreatSchedule.Curve(comp.MtbFactorByDaysPassed, facts.DaysSinceSettle);
                return factor > 0 ? comp.IncidentsPerDay / factor : throw new DomainException("trade mtb comp factor " + factor);
            case TradeCompRate.FactionInteraction:
                if ((comp.MinWealth > 0 && wealth < comp.MinWealth) || facts.Danger < comp.MinDanger)
                {
                    return 0;
                }

                var fraction = comp.FullAlliesOnly ? facts.FullAllyFraction : facts.AllyFraction;
                return fraction > 0 ? comp.IncidentsPerDay * fraction : 0;
        }

        throw new DomainException("trade comp rate " + comp.Rate);
    }
}
