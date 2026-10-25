using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Brain.Policy;

// Trader visits as a stock-arrival source (#3869 of #3650). The bridge decodes each storyteller's trader comps by comp class
// (Catalog/Storyteller.cs) and each trader kind's stock generators by generator class (Catalog/TraderStock.cs); the native read gives
// what a visit reads of the colony now (TradeVisitFacts). A visit carries a stock with the chance its trader kind's generators make
// enough of it and today's silver pays the buy price (#3855 Q2 A). MTB visits retain an exponential wait; native cycle visits
// apply their carry chance at each observed date. A comp
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

    /// <summary>StorytellerComp_OnOffCycle: the native cycle row indexed by the storyteller comp.</summary>
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
/// A category identity used to join trader incidents to their native comp observations.
/// </summary>
public sealed record TradeCategoryWeight(string Category, bool Trader);

/// <summary>
/// A comp's trader categories and population-intent weight floor. RandomMain probabilities come from the observed interval process.
/// </summary>
public sealed record TradeCategoryDraw(TradeDrawRule Rule)
{
    public EquatableArray<TradeCategoryWeight> Categories { get; init; }

    public double MinPopulationIntentFactor { get; init; }
}

/// <summary>
/// A storyteller comp that may send trader visits (#3869, #3909), from <see cref="MinDaysPassed"/> days after settling (with its
/// incident's earliestDay, and before forceRaidEnemyBeforeDaysPassed an on-off cycle sends raids). Its <see cref="Rate"/>: a
/// FactionInteraction and OnOffCycle comps consume native dates keyed by CompIndex. Native acceptance includes the ally fraction;
/// <see cref="FullAlliesOnly"/>, <see cref="MinWealth"/> and <see cref="MinDanger"/> name today's zero-visit gates.
/// MTB comps retain <see cref="IncidentsPerDay"/> (1 ÷ mtbDays) over
/// <see cref="MtbFactorByDaysPassed"/>. Each event is <see cref="Incident"/>, or drawn as <see cref="Draw"/> says (one of the two).
/// </summary>
public sealed record TradeVisitComp(TradeCompRate Rate, double MinDaysPassed)
{
    public int CompIndex { get; init; }
    public string Incident { get; init; } = "";

    public TradeCategoryDraw? Draw { get; init; }

    public double IncidentsPerDay { get; init; }

    public EquatableArray<(double Days, double Factor)> MtbFactorByDaysPassed { get; init; }

    public bool FullAlliesOnly { get; init; }

    public double MinWealth { get; init; }

    public StoryDangerLevel MinDanger { get; init; }
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
/// every incident of a trader category a comp draws), and StorytellerComp.IncreasesPopChanceByPopIntentCurve
/// (<see cref="PopulationChanceByIntent"/>).
/// </summary>
public sealed record TradeScheduleTable
{
    public IReadOnlyDictionary<string, StorytellerTrade> Storytellers { get; init; } = new Dictionary<string, StorytellerTrade>();

    public IReadOnlyDictionary<string, TradeIncident> Incidents { get; init; } = new Dictionary<string, TradeIncident>();

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
    /// <summary>The index of this generator in its owning trader kind. Shared across its resource draws.</summary>
    public int Generator { get; init; }
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
    /// <summary>One visit carries enough of at least one requested alternative. Shared generator choices are drawn once.</summary>
    internal double SuppliesAny(IReadOnlyList<Resource> requested, long count)
    {
        var resources = requested.Distinct().Where(r => Offers.TryGetValue(r, out var offer) && offer.MaxCount >= count).ToArray();
        if (resources.Length == 0) return 0;
        if (resources.Length == 1) return Offers[resources[0]].AtLeast(count);
        var n = checked((int)count);
        var zero = EquatableArray.From(Enumerable.Repeat(0, resources.Length));
        var distribution = new Dictionary<EquatableArray<int>, double> { [zero] = 1 };
        foreach (var group in resources.SelectMany((resource, index) => Offers[resource].Draws.Select(draw => (Index: index, Draw: draw))).GroupBy(r => r.Draw.Generator))
        {
            var candidates = group.ToArray();
            var law = candidates[0].Draw;
            var generator = new Dictionary<EquatableArray<int>, double>();
            void Choose(int item, List<int> picked, double probability)
            {
                if (item < candidates.Length)
                {
                    Choose(item + 1, picked, probability);
                    picked.Add(item);
                    Choose(item + 1, picked, probability);
                    picked.RemoveAt(picked.Count - 1);
                    return;
                }
                var excluded = Enumerable.Range(0, candidates.Length).Except(picked).ToArray();
                double Exact(int at, List<StockDraw> selected, double sign)
                {
                    if (at == excluded.Length) return sign * PickedTogether(law, selected);
                    var sum = Exact(at + 1, selected, sign);
                    selected.Add(candidates[excluded[at]].Draw);
                    sum += Exact(at + 1, selected, -sign);
                    selected.RemoveAt(selected.Count - 1);
                    return sum;
                }
                var chance = Exact(0, picked.Select(p => candidates[p].Draw).ToList(), 1);
                var counts = new int[resources.Length];
                void Counts(int at, double mass)
                {
                    if (at == picked.Count)
                    {
                        var key = EquatableArray.From(counts);
                        generator[key] = generator.GetValueOrDefault(key) + mass;
                        return;
                    }
                    var candidate = candidates[picked[at]];
                    var values = candidate.Draw.Counts(n);
                    for (var c = 0; c <= n; c++)
                    {
                        if (!(values[c] > 0)) continue;
                        counts[candidate.Index] = c;
                        Counts(at + 1, mass * values[c]);
                    }
                    counts[candidate.Index] = 0;
                }
                if (chance > 0) Counts(0, chance * probability);
            }
            Choose(0, [], 1);
            var next = new Dictionary<EquatableArray<int>, double>();
            foreach (var held in distribution)
                foreach (var incoming in generator)
                {
                    var key = EquatableArray.From(Enumerable.Range(0, resources.Length).Select(i => Math.Min(n, held.Key[i] + incoming.Key[i])));
                    next[key] = next.GetValueOrDefault(key) + held.Value * incoming.Value;
                }
            distribution = next;
        }
        return distribution.Where(p => p.Key.Any(c => c >= n)).Sum(p => p.Value);
    }
    /// <summary>One deterministic held-stock draw, shared by settlement previews and present trader visits.</summary>
    public static TraderKindStock Held(IEnumerable<SettlementStockItem> stock, string read)
    {
        var offers = stock.GroupBy(s => s.Definition, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select((g, index) =>
        {
            if (g.Any(s => s.Count <= 0 || !double.IsFinite(s.UnitPrice) || s.UnitPrice <= 0)) throw new RequiredFactException(read);
            var count = g.Sum(s => s.Count);
            // Mixed stacks use the highest observed price as a conservative affordability bound.
            return (Resource: new Resource(g.Key), Offer: new TraderOffer { UnitValue = g.Max(s => s.UnitPrice),
                Draws = [new(1, 1, 1, 1, count, count, 0, 0, 1) { Generator = index }] });
        }).ToDictionary(row => row.Resource, row => row.Offer);
        return new() { Offers = offers };
    }

    public bool Favor { get; init; }

    public IReadOnlyDictionary<Resource, TraderOffer> Offers { get; init; } = new Dictionary<Resource, TraderOffer>();

    public IReadOnlyList<string> Unmodelled { get; init; } = [];

    /// <summary>Expected units of this medicine when it is the cheapest kind actually carried. Generator picks are joint;
    /// count draws are independent after definitions have been picked, as in GenerateThings.</summary>
    public double CheapestMedicineUnits(Resource resource, IReadOnlyList<Resource> medicines, long cap, double priceFactor = 1)
    {
        if (!Offers.TryGetValue(resource, out var offer) || cap <= 0) return 0;
        foreach (var medicine in medicines.Where(Offers.ContainsKey))
            if (Offers[medicine].Unread.Length != 0) throw new RequiredFactException(Offers[medicine].Unread);
        var price = TradeSource.UnitPrice(offer.UnitValue, priceFactor);
        var cheaper = medicines.Where(m => Offers.TryGetValue(m, out var other)
            && (TradeSource.UnitPrice(other.UnitValue, priceFactor) < price || TradeSource.UnitPrice(other.UnitValue, priceFactor) == price && string.CompareOrdinal(m.Value, resource.Value) < 0)).ToArray();
        var n = (int)Math.Min(Math.Min(cap, offer.MaxCount), int.MaxValue - 1);
        var total = new double[n + 1];
        total[0] = 1;
        var relevant = cheaper.Append(resource).SelectMany(m => Offers[m].Draws.Select(d => (Resource: m, Draw: d)))
            .GroupBy(row => row.Draw.Generator);
        foreach (var generator in relevant)
        {
            var selected = generator.SingleOrDefault(row => row.Resource == resource).Draw;
            var lower = generator.Where(row => row.Resource != resource).Select(row => row.Draw).ToArray();
            var law = generator.First().Draw;
            // Inclusion/exclusion over the real cheaper medicine set, never the whole def pool.
            double Absent(bool includeSelected)
            {
                double Sum(int at, List<StockDraw> picked, double weight)
                {
                    if (at == lower.Length) return weight * PickedTogether(law, picked);
                    var without = Sum(at + 1, picked, weight);
                    picked.Add(lower[at]);
                    var included = Sum(at + 1, picked, -weight * (1 - lower[at].Counts(1)[0]));
                    picked.RemoveAt(picked.Count - 1);
                    return without + included;
                }
                return Sum(0, includeSelected && selected is not null ? [selected] : [], 1);
            }
            var absent = Absent(false);
            var joint = selected is null ? 0 : Absent(true);
            var counts = selected?.Counts(n) ?? new double[n + 1];
            for (var c = 0; c <= n; c++) counts[c] *= joint;
            counts[0] += absent - joint;
            var next = new double[n + 1];
            for (var have = 0; have <= n; have++)
                for (var c = 0; c <= n; c++) next[Math.Min(n, have + c)] += total[have] * counts[c];
            total = next;
        }
        return total.Select((chance, count) => chance * count).Sum();
    }

    // Uniform k distinct entries from the generator's pool. MultiDef has k=1 and may list a def more than once.
    private static double PickedTogether(StockDraw law, List<StockDraw> picked)
    {
        double Avoid(int copies, int k)
        {
            var p = 1.0;
            for (var i = 0; i < k; i++) p *= Math.Max(0, law.Pool - copies - i) / (double)(law.Pool - i);
            return p;
        }
        double Inclusion(int at, int copies, int k, double sign) => at == picked.Count ? sign * Avoid(copies, k)
            : Inclusion(at + 1, copies, k, sign) + Inclusion(at + 1, copies + picked[at].Copies, k, -sign);
        var probability = 0.0;
        for (var k = law.DefsMin; k <= law.DefsMax; k++) probability += Inclusion(0, 0, Math.Clamp(k, 0, law.Pool), 1);
        return probability / (law.DefsMax - law.DefsMin + 1);
    }
}

/// <summary>Every trader kind's stock of the stock-arrival resources by TraderKindDef defName (#3869).</summary>
public sealed record TraderStockTable
{
    public IReadOnlyDictionary<string, TraderKindStock> Kinds { get; init; } = new Dictionary<string, TraderKindStock>();
}

/// <summary>A trader kind a visit is of, with the chance it is, and its offer of the source's resource.</summary>
public sealed record TradeKindChance(double Chance, TraderOffer Offer)
{
    public TraderKindStock? Stock { get; init; }
    public double MedicineSilverClaim { get; init; }
}

/// <summary>
/// One trade source of a resource (#3869): one trader comp's visits at <see cref="VisitsPerDay"/> from <see cref="From"/>
/// days after the want, each of a kind in <see cref="Kinds"/>; a visit supplies n units when its kind's generators make n and n ×
/// the buy price is within <see cref="Silver"/>. The buy price is TradeUtility.GetPricePlayerBuy: the offer's unit value ×
/// <see cref="PriceFactor"/> ((1 + the difficulty's tradePriceFactorLoss) × (1 − the best negotiator's TradePriceImprovement)), at
/// least 0.5, rounded above 99.5.
/// </summary>
public sealed record TradeSource(Resource Resource, double From, double VisitsPerDay)
{
    internal static double SuppliesAny(IReadOnlyList<TradeSource> sources, long count)
    {
        if (sources.Count == 1) return sources[0].Supplies(count);
        IEqualityComparer<TraderKindStock> identity = ReferenceEqualityComparer.Instance;
        foreach (var source in sources)
            foreach (var kind in source.Kinds)
                if (kind.Offer.Unread.Length != 0) throw new RequiredFactException(kind.Offer.Unread);
        var kinds = sources.Where(source => source.CanPurchase).SelectMany(source => source.Kinds.Where(kind => count <= source.Authorized
                && count * UnitPrice(kind.Offer.UnitValue, source.PriceFactor) <= source.Silver - kind.MedicineSilverClaim)
            .Select(kind => (Source: source, Kind: kind)));
        return kinds.GroupBy(k => k.Kind.Stock ?? throw new RequiredFactException("joint_trade_kind_stock"), identity)
            .Sum(group => group.First().Kind.Chance * group.Key.SuppliesAny(group.Select(k => k.Source.Resource).Distinct().ToArray(), count));
    }
    public IncidentTiming Timing { get; init; } = new IncidentTiming.Mtb();

    public EquatableArray<TradeKindChance> Kinds { get; init; }

    public double PriceFactor { get; init; }

    public long Silver { get; init; }
    /// <summary>The selected outcome's bounded stock-wait authorization; unlimited before its purchase comparison.</summary>
    public long Authorized { get; init; } = long.MaxValue;

    /// <summary>A selected request sends one visit at From, rather than a recurring incident stream.</summary>
    public bool Once { get; init; }

    /// <summary>The current seller's departure on this want's timeline. A selected purchase must complete before it;
    /// goods bought before departure remain colony stock for later wants. Future visits buy on arrival and have no current departure deadline.</summary>
    public double PurchaseUntil { get; init; } = double.PositiveInfinity;
    public bool CanPurchase => From < PurchaseUntil;

    /// <summary>The buy price of one unit at <paramref name="unitValue"/>.</summary>
    public static double UnitPrice(double unitValue, double priceFactor)
    {
        var price = Math.Max(unitValue * priceFactor, 0.5);
        return price > 99.5 ? Math.Round(price, MidpointRounding.ToEven) : price;
    }

    /// <summary>The chance one visit supplies <paramref name="count"/> units the colony can pay for.</summary>
    public double Supplies(long count)
    {
        if (!CanPurchase) return 0;
        if (count > Authorized) return 0;
        var chance = 0.0;
        foreach (var kind in Kinds)
        {
            if (kind.Offer.Unread.Length != 0)
            {
                throw new RequiredFactException(kind.Offer.Unread);
            }

            if (count * UnitPrice(kind.Offer.UnitValue, PriceFactor) <= Silver - kind.MedicineSilverClaim)
            {
                chance += kind.Chance * kind.Offer.AtLeast(count);
            }
        }

        return chance;
    }

    /// <summary>The rate a day at which visits supply <paramref name="count"/> units.</summary>
    public double Rate(long count) => Once || Timing is not IncidentTiming.Mtb ? 0 : VisitsPerDay * Supplies(count);

    /// <summary>Native visit dates on this want's timeline. Earlier native visits cannot satisfy a later want.</summary>
    public IEnumerable<double> Dates(double through)
    {
        if (Timing is IncidentTiming.Process process)
        {
            foreach (var day in process.Program.Dates)
                if (day >= process.OriginDay && day - process.OriginDay >= From && day - process.OriginDay < through)
                    yield return day - process.OriginDay;
            yield break;
        }
        if (Timing is not IncidentTiming.Scheduled timed) yield break;
        foreach (var day in IncidentTiming.Covered(timed.Cycle, through + timed.OriginDay))
            if (day >= Math.Max(timed.From, timed.OriginDay) && day < timed.Until && day - timed.OriginDay >= From && day - timed.OriginDay <= through)
                yield return day - timed.OriginDay;
    }

    /// <summary>The chance stock has not arrived: an MTB exponential, discrete native visit misses, or one selected delivery.</summary>
    public double NotArrived(long count, double t) => count <= 0 ? 0 : Once ? t < From ? 1 : 1 - Supplies(count)
        : Timing is IncidentTiming.Process process ? process.Program.NotArrived(process.OriginDay, t,
            new Dictionary<(int Comp, string Incident), double> { [(process.CompIndex, process.Incident)] = Supplies(count) })
        : Timing is IncidentTiming.Scheduled timed ? Math.Pow(1 - timed.Chance * Supplies(count), Dates(t).Count())
        : t <= From ? 1 : Math.Exp(-Rate(count) * (t - From));
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

    /// <summary>An incident that fires with no trader kind to send (<c>:&lt;incident&gt;</c>): no orbital kind can spawn, no visitor faction trades.</summary>
    public const string NoTraderKind = "stock_arrival_trade_no_trader_kind";

    // IncidentWorker_VisitorGroup.TraderChance (private const): a visitor group converts one adult to a trader with this chance.
    internal const double VisitorTraderChance = 0.75;

    /// <summary>
    /// The trade sources (#3869, #3909) of <paramref name="storyteller"/>'s trader comps for the colony now. Per comp the events a
    /// day (a FactionInteraction comp's incidents a day × the ally fraction, none below its min wealth or min danger; an OnOffCycle
    /// comp's cycle rate at the days since settling and <paramref name="now"/>; an MTB comp's 1 ÷ mtbDays over its
    /// days-passed factor), from the days its start still lies ahead; each event is the comp's incident or one drawn from its
    /// categories as <see cref="IncidentChances"/> gives. None for an incident blocked now, an orbital trader without a powered comms
    /// console and trade beacon, travelers, or a colony without a negotiator. Each visit is of a kind drawn as the incident draws it
    /// (a source uniformly, then a kind by weight; a visitor group's trader with <see cref="VisitorTraderChance"/>) and supplies a
    /// resource as <see cref="TradeSource"/> prices it from the silver it is paid from: <paramref name="orbitalSilver"/> for an orbital
    /// trader (the silver within trade-beacon range, #3910), <paramref name="silver"/> (home-area/storage silver) for every other trader, each already above the
    /// routine trade's reserve (<see cref="TradePolicyRules.SpendableSilver"/>). An incident the read or the catalog does not
    /// carry, or a kind the catalog does not know, leaves the trade unread (the returned Unread).
    /// </summary>
    public static (IReadOnlyList<TradeSource> Sources, IReadOnlyList<string> Findings, string Unread) Sources(TradeScheduleTable schedule, StorytellerTrade storyteller,
        TraderStockTable stock, TradeVisitFacts facts, double wealth, long silver, long orbitalSilver,
        IReadOnlyList<IncidentCycleTimes> cycles)
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
        var process = facts.Clock is not null || storyteller.Comps.Any(c => c.Draw?.Rule == TradeDrawRule.RandomMain)
            ? new RandomMainProcess(facts, schedule.PopulationChanceByIntent) : null;
        if (process is not null) findings.UnionWith(RandomMainProcess.Conditions);
        foreach (var comp in storyteller.Comps)
        {
            var random = process?.Contains(comp.CompIndex) == true;
            var (chances, unread) = random
                ? ((IReadOnlyList<(string Incident, double Chance)>)[.. schedule.Incidents.Where(i => comp.Draw is { } draw ? draw.Categories.Any(c => c.Trader && c.Category == i.Value.Category) : i.Key == comp.Incident).Select(i => (i.Key, 1.0))], "")
                : IncidentChances(schedule, comp, reads, facts);
            if (unread.Length != 0)
            {
                return ([], [], unread);
            }

            var perDay = comp.Rate == TradeCompRate.Mtb ? VisitsPerDay(comp, facts) : 0;
            var allowed = comp.Rate != TradeCompRate.FactionInteraction ||
                (!(comp.MinWealth > 0 && wealth < comp.MinWealth) && facts.Danger >= comp.MinDanger);
            var fraction = comp.Rate == TradeCompRate.FactionInteraction ? (comp.FullAlliesOnly ? facts.FullAllyFraction : facts.AllyFraction) : 1;
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

                if (!(chance > 0) || !allowed || !(fraction > 0) || comp.Rate == TradeCompRate.Mtb && !(perDay > 0))
                {
                    continue;
                }

                if (incident.Worker == TradeWorker.Orbital && (!facts.CommsConsole || !facts.TradeBeacon))
                {
                    findings.Add(facts.CommsConsole ? OrbitalNoBeacon : OrbitalNoComms);
                    continue;
                }

                var kinds = KindChances(incident, read);
                if (random && incident.Worker == TradeWorker.Orbital)
                {
                    var success = 1 - read.Sources.SelectMany(s => s.Kinds).Where(k => k.Weight > 0)
                        .Aggregate(1.0, (none, kind) => none * (1 - kind.Qualifies));
                    if (success > 0) kinds = kinds.ToDictionary(k => k.Key, k => k.Value / success, StringComparer.Ordinal);
                }
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

                        list.Add(new TradeKindChance(kindChance, offer) { Stock = kind });
                    }
                }

                foreach (var (resource, list) in offers.OrderBy(o => o.Key.Value, StringComparer.Ordinal))
                {
                    var timing = random ? (IncidentTiming)new IncidentTiming.Process(process!, comp.CompIndex, name)
                        : comp.Rate == TradeCompRate.Mtb ? new IncidentTiming.Mtb()
                        : new IncidentTiming.Scheduled(IncidentTiming.RequireCycle(cycles, comp.CompIndex), chance) { From = from };
                    sources.Add(new TradeSource(resource, from, perDay * chance) { Timing = timing, Kinds = EquatableArray.From(list), PriceFactor = priceFactor, Silver = incident.Worker == TradeWorker.Orbital ? orbitalSilver : silver });
                }
            }
        }

        return (sources, [.. findings], "");
    }

    /// <summary>
    /// The incident draw of category or fixed-incident comps without a native interval observation. RandomMain requires its joint process.
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

                var weight = Math.Max(0, read.Weight * IntentFactor(incident.ChangesPopulation, incident.IntentOffset, facts.PopulationIntent, draw.MinPopulationIntentFactor));
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
                throw new RequiredFactException("incident_clock_unobserved");

            case TradeDrawRule.CategoryMtb:
                foreach (var category in draw.Categories.Where(c => c.Trader))
                {
                    chances.AddRange(SelectRandomIncident([.. usable[category.Category].Select(i => (i.Incident, i.Def.ChangesPopulation, i.Weight))], facts.PopulationIntent, schedule.PopulationChanceByIntent));
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
    /// StorytellerComp.TrySelectRandomIncident's chance of each usable incident: with the IncreasesPopChanceByPopIntentCurve chance at
    /// the population intent, one that changes the population by weight, else one that does not, either falling back to all of them
    /// when its own weight is 0; nothing when all weigh 0.
    /// </summary>
    internal static IEnumerable<(string Incident, double Chance)> SelectRandomIncident(IReadOnlyList<(string Incident, bool ChangesPopulation, double Weight)> usable,
        double populationIntent, IReadOnlyList<(double X, double Y)> chanceByIntent)
    {
        var all = usable.Sum(i => i.Weight);
        if (!(all > 0))
        {
            yield break;
        }

        var increase = Math.Clamp(ThreatSchedule.Curve(chanceByIntent, populationIntent), 0, 1);
        var changing = usable.Where(i => i.ChangesPopulation).Sum(i => i.Weight);
        var steady = all - changing;
        foreach (var (incident, changes, weight) in usable)
        {
            var whenIncrease = changing > 0 ? (changes ? weight / changing : 0) : weight / all;
            var whenNot = steady > 0 ? (changes ? 0 : weight / steady) : weight / all;
            yield return (incident, (increase * whenIncrease) + ((1 - increase) * whenNot));
        }
    }

    // StorytellerComp.IncidentChanceFactor_PopulationIntent: 1 for no population effect, else max(intent + the effect's offset, the
    // comp's floor).
    internal static double IntentFactor(bool changesPopulation, double intentOffset, double intent, double floor) =>
        changesPopulation ? Math.Max(intent + intentOffset, floor) : 1;

    // The kind a visit of the incident is of: a source (a candidate faction) uniformly, then a kind by weight; a visitor group's
    // trader only with TraderChance and an adult in the generated group.
    private static Dictionary<string, double> KindChances(TradeIncident incident, TraderIncidentRead read)
    {
        if (incident.Worker == TradeWorker.Orbital)
        {
            return OrbitalKindChances([.. read.Sources.SelectMany(s => s.Kinds)]);
        }

        var kinds = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var source in read.Sources)
        {
            if (source.Kinds.Count == 0)
            {
                continue;
            }

            var share = 1.0 / read.Sources.Count;
            if (incident.Worker == TradeWorker.Visitors)
            {
                share *= VisitorTraderChance * VisitorAdultChance.Of(source.Visitors
                    ?? throw new RequiredFactException("visitor_generation:" + source.Faction));
            }

            if (!(share > 0))
            {
                continue;
            }

            var total = source.Kinds.Sum(k => Math.Max(0, k.Weight));
            foreach (var kind in source.Kinds.Where(k => k.Weight > 0 && total > 0))
            {
                kinds[kind.Kind] = kinds.GetValueOrDefault(kind.Kind) + (kind.Weight / total * share);
            }
        }

        return kinds;
    }

    /// <summary>
    /// The chance an orbital trader incident sends a ship of each kind the colony can trade with (#3910), as
    /// IncidentWorker_OrbitalTraderArrival draws it: a kind is among the candidates when CanSpawn passes, which draws one of its
    /// faction def's factions at random (<see cref="TraderKindWeight.Qualifies"/> of the draws pass, independently per kind), then
    /// TryRandomElementByWeight picks a candidate by CalculatedCommonality; the ship's faction is drawn again, and
    /// TradeShip.CanCommunicateWith lets the colony trade only when that one qualifies too. So kind k is traded with at
    /// q × w ÷ (w + the weight of the other candidates) × q, over every set of other candidates.
    /// </summary>
    internal static Dictionary<string, double> OrbitalKindChances(IReadOnlyList<TraderKindWeight> kinds)
    {
        var candidates = kinds.Where(k => k.Weight > 0 && k.Qualifies > 0).ToList();
        var chances = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var kind in candidates)
        {
            var others = candidates.Where(k => k != kind).ToList();
            var sure = others.Where(k => k.Qualifies >= 1).Sum(k => k.Weight);
            var maybe = others.Where(k => k.Qualifies < 1).ToList();
            // Convolve the total passing weight. Equal sums share a state; native commonalities
            // are floats represented exactly as doubles. No quantisation, truncation or kind cap.
            var totals = new Dictionary<double, double> { [0] = 1 };
            foreach (var candidate in maybe)
            {
                var next = new Dictionary<double, double>();
                foreach (var (weight, probability) in totals)
                {
                    next[weight] = next.GetValueOrDefault(weight) + probability * (1 - candidate.Qualifies);
                    var passing = weight + candidate.Weight;
                    next[passing] = next.GetValueOrDefault(passing) + probability * candidate.Qualifies;
                }

                totals = next;
            }

            var share = totals.Sum(t => t.Value / (kind.Weight + sure + t.Key));
            chances[kind.Kind] = kind.Weight * kind.Qualifies * kind.Qualifies * share;
        }

        return chances;
    }

    // StorytellerComp_FactionInteraction: no visits below minWealth or minDanger or without an ally fraction; IncidentCycleUtility's
    // count is RoundRandom(base) over a year, then RoundRandom(hits × fraction): unbiased, so the rates multiply. OnOffCycle:
    // MTB retains its continuous rate; native cycle rows already contain cycle acceptance.
    private static double VisitsPerDay(TradeVisitComp comp, TradeVisitFacts facts)
    {
        var factor = ThreatSchedule.Curve(comp.MtbFactorByDaysPassed, facts.DaysSinceSettle);
        return factor > 0 ? comp.IncidentsPerDay / factor : throw new DomainException("trade mtb comp factor " + factor);
    }
}
