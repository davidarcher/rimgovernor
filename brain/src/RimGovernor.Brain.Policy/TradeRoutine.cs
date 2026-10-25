using Trader = RimGovernor.Contracts.Wire.Observations.Trader;

namespace RimGovernor.Brain.Policy;

// The routine trade goal: while a tradeable caravan stands on the map and the colony has
// something to buy from it (the medicine shortfall medical/reserves already reports, or a component
// shortfall under the derived component need) or to sell to it (stock above an economy/resource target),
// RoundsTradePlanner opens one bounded session per caravan and settles it. It is config-only work like a
// configuration push: a negotiator's conversation, not a development project, so it holds no development slot.

/// <summary>
/// The census trader's participant: a passing ship is an orbital participant, every other trader a map one. Its
/// Key() is the trader id the routine, the offer book and the journal name it by.
/// </summary>
public static class TraderParticipant
{
    public static TradeParticipant Participant(this Trader t) => new(t.Orbital ? TradeParticipantKind.Orbital : TradeParticipantKind.Map, t.Id);

    public static string Key(this Trader t) => t.Participant().Key();
}

/// <summary>
/// The colony wealth native's WealthWatcher reports: the market value held as items, and the total. Items count in full toward storyteller wealth where buildings count half, which is why the
/// surplus rule keys on the item share.
/// </summary>
public sealed record WealthFacts(double Items, double Total);

/// <summary>
/// What the review measured worth trading for: the medicine units medical/reserves wants, the components
/// short of the target, each the economy/resource Need target's stock above its floor, and each raw-material hoard
/// WealthSurplus sells down. Retained is the stock every Surplus row keeps after its sale (the target, or the
/// wealth rule's floor).
/// </summary>
public sealed record TradeNeed
{
    public TradeFoodNeed Food { get; init; } = new();

    public long MedicineReplenish { get; init; }

    public long ComponentShortfall { get; init; }

    public IReadOnlyList<Amount> Surplus { get; init; } = [];

    public IReadOnlyDictionary<Resource, long> Retained { get; init; } = new Dictionary<Resource, long>();

    /// <summary>Each the economy/resource Need floor stock is below: a caravan selling it is the catalog's trade method.</summary>
    public IReadOnlyList<Amount> Shortfall { get; init; } = [];

    /// <summary>Set while the colony can host one more colonist (JoinerCapacity): a caravan offering a slave or prisoner is worth opening for.</summary>
    public bool Population { get; init; }

    /// <summary>The restore parts no bench can fabricate, highest priority first: a trader selling one is worth opening for.</summary>
    public IReadOnlyList<SurgeryPart> SurgeryParts { get; init; } = [];

    /// <summary>The shed_art reason: the count of packed art no owed room reserves (SaleSculptures) while the wealth headroom is known and negative. The selection sells it first (ArtFirst).</summary>
    public long ShedArt { get; init; }

    /// <summary>The count of colony animals the herd plan lets go (HerdSaleAnimals) while the colony wants silver: a caravan is worth opening for them.</summary>
    public long SurplusAnimals { get; init; }

    /// <summary>The gold stock above FavorGoldKeep while a tribute collector is present: sold for royal favor.</summary>
    public long FavorGold { get; init; }

    /// <summary>The count of surplus prisoners (SurplusPrisoners) while a tribute collector is present: sold for royal favor.</summary>
    public long FavorPrisoners { get; init; }

    /// <summary>A copy that keeps <paramref name="count"/> of the resource after its sale.</summary>
    internal TradeNeed Retain(Resource resource, long count)
    {
        var retained = new Dictionary<Resource, long>(Retained) { [resource] = count };
        return this with { Retained = retained };
    }

    public bool Any() =>
        FavorGold > 0
        || FavorPrisoners > 0
        || ShedArt > 0
        || SurplusAnimals > 0
        || Population
        || SurgeryParts.Count > 0
        || MedicineReplenish > 0
        || ComponentShortfall > 0
        || Surplus.Count > 0
        || Shortfall.Count > 0
        || Food.Nutrition > 0
        || Food.Missing.Count > 0;
}

public static partial class TradePolicyRules
{
    /// <summary>The one component definition the trade goal buys and the resource family produces toward under the derived component need.</summary>
    public static readonly Resource ComponentResource = new("ComponentIndustrial");

    /// <summary>The share of total colony wealth held as items past which WealthSurplus sells the raw-material hoards down to their floors.</summary>
    public const double TradeItemWealthShare = 0.6;

    /// <summary>
    /// The silver a purchase never spends below: 100 per colonist, at least 200. An empty count
    /// reports false and the routine buys nothing (fail closed).
    /// </summary>
    public static (long Reserve, bool Ok) TradeSilverReserve(long colonists)
    {
        if (colonists <= 0)
        {
            return (200, false);
        }
        return (Math.Max(200, Math.Min(100 * colonists, 100000)), true);
    }

    /// <summary>
    /// The silver a purchase may spend (#3907): the silver above <see cref="TradeSilverReserve"/>, none below it and none for an empty
    /// colony (the routine buys nothing then). The stock-arrival model prices trader purchases from it.
    /// </summary>
    public static long SpendableSilver(long silver, long colonists)
    {
        var (reserve, ok) = TradeSilverReserve(colonists);
        return ok ? Math.Max(0, silver - reserve) : 0;
    }



    /// <summary>The rough silver a medicine replenish of <paramref name="replenish"/> units costs: the cheapest medicine's market value each (the routine trade's purchase, #3907).</summary>
    public static double MedicineClaim(ItemFacts items, long replenish) => replenish * items.CheapestMedicine().Price;

    /// <summary>
    /// The per-definition floor set the routine trade review sells against: EconomicReserves over native's
    /// outstanding construction deficits when the caller has read them (empty otherwise); stock targets, which
    /// carry the derived resource needs, apply separately.
    /// </summary>
    public static IReadOnlyDictionary<string, long> RoundsTradeFloors(RoundsPolicy p, IReadOnlyDictionary<string, long>? construction) =>
        EconomicReserves(new TradeEconomicPolicy(default, 0), new TradeReserveFacts { Construction = construction ?? new Dictionary<string, long>() }).Floors;

    /// <summary>
    /// The stock the colony keeps of each resource before a sale: the runway's protected line (reserve plus the
    /// observed rate over the projection horizon) plus the construction demand. A resource nothing consumes or
    /// builds with has no entry and is wholly surplus. A runway still unrated (an unread ring) keeps nothing.
    /// </summary>
    public static IReadOnlyDictionary<Resource, long> TradeRetained(IReadOnlyList<ResourceRunway> runways, IReadOnlyDictionary<Resource, long> construction)
    {
        var output = new Dictionary<Resource, long>();
        foreach (var row in runways)
        {
            if (row.TryProtectedLine(out var line))
            {
                output[row.Resource] = output.GetValueOrDefault(row.Resource) + line;
            }
        }
        foreach (var (resource, demand) in construction)
        {
            output[resource] = output.GetValueOrDefault(resource) + Math.Max(0, demand);
        }
        return output;
    }

    /// <summary>
    /// The wealth-driven half of the trade surplus: while the item share of total wealth exceeds
    /// TradeItemWealthShare, every deep-deposit def (the raw ore and metal hoards, ItemFacts.DeepResources; never
    /// the currency) stocked above max(target, floor, retained) is a surplus of the difference, so a sale lands
    /// exactly on the highest floor. An unassessed retention plan, an unmet share or a stock at its floor
    /// yields nothing; the rows come back in DeepResources order.
    /// </summary>
    public static IReadOnlyList<Amount> WealthSurplus(
        IReadOnlyList<Amount> stock,
        ItemFacts items,
        IReadOnlyDictionary<Resource, long> targets,
        IReadOnlyDictionary<string, long> floors,
        TradeRetention retainedFact,
        WealthFacts wealth
    )
    {
        var facts = wealth ?? throw new RequiredFactException("wealth");
        var retainedKnown = retainedFact is TradeRetention.Assessed;
        if (!retainedKnown)
        {
            return [];
        }
        if (!Finite(facts!.Items) || !Finite(facts.Total) || facts.Total <= 0 || facts.Items < 0 || facts.Items / facts.Total <= TradeItemWealthShare)
        {
            return [];
        }
        var counts = new Dictionary<Resource, long>();
        foreach (var row in stock)
        {
            counts[row.Resource] = counts.GetValueOrDefault(row.Resource) + row.Count;
        }
        var output = new List<Amount>();
        foreach (var resource in items.DeepResources)
        {
            if (resource == items.Currency)
            {
                continue;
            }
            var keep = Math.Max(targets.GetValueOrDefault(resource), Math.Max(floors.GetValueOrDefault(resource.Value), ((TradeRetention.Assessed)retainedFact).Stock.GetValueOrDefault(resource)));
            var surplus = counts.GetValueOrDefault(resource) - keep;
            if (surplus > 0)
            {
                output.Add(new Amount(resource, surplus));
            }
        }
        return output;
    }

    /// <summary>Adds unreserved packed art under negative wealth headroom to the trade need.</summary>
    public static TradeNeed ShedArtNeed(TradeNeed need, double headroom, long saleArt)
    {
        var n = need;
        var h = headroom;
        var art = saleArt;
        if (!Finite(h) || h >= 0 || art <= 0)
        {
            return need;
        }
        return n with { ShedArt = art };
    }

    /// <summary>
    /// Measures trade demand from the shared medicine, resource, wealth and food reviews.
    /// Floors protect construction reserves; retained stock protects the resource runway.
    /// Target-driven surplus wins over wealth-driven surplus for the same resource.
    /// Currency is neither a target nor a surplus.
    /// </summary>
    public static TradeNeed ReviewTradeNeed(
        ItemFacts items,
        MedicalReserveReview medicine,
        EquatableArray<Amount> resources,
        IReadOnlyDictionary<Resource, long> targets,
        IReadOnlyDictionary<string, long> floors,
        WealthFacts wealth,
        TradeRetention retained,
        TradeFoodContext food
    )
    {
        var replenish = medicine.Replenish;
        var stock = new Dictionary<Resource, long>();
        foreach (var row in resources)
        {
            stock[row.Resource] = stock.GetValueOrDefault(row.Resource) + row.Count;
        }
        var need = new TradeNeed { MedicineReplenish = Math.Max(0, replenish) };
        need = need with { Food = ReviewTradeFood(food) };
        need = need with { ComponentShortfall = Math.Max(0, targets.GetValueOrDefault(ComponentResource) - stock.GetValueOrDefault(ComponentResource)) };
        var names = targets.Keys.Select(r => r.Value).ToList();
        names.Sort(StringComparer.Ordinal);
        var shortfall = new List<Amount>();
        var surplusRows = new List<Amount>();
        var keeps = new Dictionary<Resource, long>();
        foreach (var name in names)
        {
            var resource = new Resource(name);
            if (resource == ComponentResource || resource == items.Currency)
            {
                continue;
            }
            var missing = targets.GetValueOrDefault(resource) - stock.GetValueOrDefault(resource);
            if (missing > 0)
            {
                shortfall.Add(new Amount(resource, missing));
            }
            var keep = Math.Max(targets.GetValueOrDefault(resource), floors.GetValueOrDefault(name));
            var surplus = stock.GetValueOrDefault(resource) - keep;
            if (surplus > 0)
            {
                surplusRows.Add(new Amount(resource, surplus));
                keeps[resource] = keep;
            }
        }
        foreach (var row in WealthSurplus(resources, items, targets, floors, retained, wealth))
        {
            if (keeps.ContainsKey(row.Resource))
            {
                continue;
            }
            surplusRows.Add(row);
            keeps[row.Resource] = stock.GetValueOrDefault(row.Resource) - row.Count;
        }
        return need with { Shortfall = shortfall, Surplus = surplusRows, Retained = keeps };
    }

    /// <summary>
    /// Recovery means no tradeable or arriving caravan remains, or nothing is worth trading
    /// and every present caravan has a fresh priced sheet. Arriving caravans keep the Need
    /// standing until arrival; an absent census is refused at the observation boundary.
    /// </summary>
    public static bool TradeRecovered(EquatableArray<Trader> traders, EquatableArray<string> unpriced, TradeNeed need)
    {
        var rows = traders;
        var present = false;
        var browse = false;
        foreach (var row in rows!)
        {
            present = present || row.CanTrade || row.Travelling;
            browse = browse || (row.CanTrade && unpriced.Contains(row.Key()));
        }
        if (!present)
        {
            return true;
        }
        var n = need;
        return !n.Any() && !browse;
    }

    /// <summary>Picks the caravan to open with: tradeable, not already settled this epoch, most goods first, id order on ties; null when none is.</summary>
    public static Trader? SelectTrader(IReadOnlyList<Trader> traders, IReadOnlySet<string> settled)
    {
        Trader? best = null;
        foreach (var row in traders)
        {
            if (!row.CanTrade || settled.Contains(row.Key()))
            {
                continue;
            }
            if (best is null || row.GoodsStacks > best.GoodsStacks || (row.GoodsStacks == best.GoodsStacks && string.CompareOrdinal(row.Key(), best.Key()) < 0))
            {
                best = row;
            }
        }
        return best;
    }

    /// <summary>
    /// Turns the measured need into SelectTrade's ordered targets against one live sheet: food first, medicine
    /// (the cheapest definition the trader carries), then components, then each surplus sale. Purchases are
    /// bounded only by the silver above the reserve; sales take any positive price, since the alternative is the
    /// surplus sitting unsold.
    /// </summary>
    public static TradeEconomicPolicy RoundsTradeTargets(ItemFacts items, TradeNeed need, IReadOnlyList<TradeSheetRowFact> rows, IReadOnlyDictionary<Resource, long> targets, long colonists)
    {
        var (reserve, buy) = TradeSilverReserve(colonists);
        var built = BuildRoundsTradeTargets(items, need, rows, targets);
        if (!buy)
        {
            built = [.. built.Select(t => t with { MaxBuy = 0 })];
        }
        return new TradeEconomicPolicy(EquatableArray.From(built), reserve);
    }

    private static List<TradeTarget> BuildRoundsTradeTargets(ItemFacts items, TradeNeed need, IReadOnlyList<TradeSheetRowFact> rows, IReadOnlyDictionary<Resource, long> targets)
    {
        // Leave room for medicine and components while prioritizing the food bridge.
        var output = new List<TradeTarget>(TradeFoodTargets(need.Food, rows));
        if (need.MedicineReplenish > 0)
        {
            TradeSheetRowFact? pick = null;
            foreach (var row in rows)
            {
                if (!items.IsMedicine(new Resource(row.DefName)) || row.TraderCount <= 0 || !Finite(row.BuyPrice) || row.BuyPrice <= 0)
                {
                    continue;
                }
                if (pick is null || row.BuyPrice < pick.BuyPrice)
                {
                    pick = row;
                }
            }
            if (pick is not null)
            {
                output.Add(new TradeTarget(pick.DefName, pick.ColonyCount + need.MedicineReplenish, need.MedicineReplenish, 0, 0));
            }
        }
        if (need.ComponentShortfall > 0)
        {
            output.Add(new TradeTarget(ComponentResource.Value, targets.GetValueOrDefault(ComponentResource), need.ComponentShortfall, 0, 0));
        }
        var seen = new HashSet<string>(output.Select(t => t.Item), StringComparer.Ordinal);
        foreach (var target in SurgeryPartTargets(need.SurgeryParts, rows, seen))
        {
            seen.Add(target.Item);
            output.Add(target);
        }
        foreach (var missing in need.Shortfall)
        {
            if (!seen.Add(missing.Resource.Value))
            {
                continue;
            }
            output.Add(new TradeTarget(missing.Resource.Value, targets.GetValueOrDefault(missing.Resource), missing.Count, 0, 0));
        }
        foreach (var surplus in need.Surplus)
        {
            if (seen.Contains(surplus.Resource.Value))
            {
                continue;
            }
            output.Add(
                new TradeTarget(
                    surplus.Resource.Value,
                    Math.Max(targets.GetValueOrDefault(surplus.Resource), need.Retained.GetValueOrDefault(surplus.Resource)),
                    0,
                    surplus.Count,
                    double.Epsilon
                )
            );
        }
        return output;
    }

    // Buys each part's best item the trader carries at a known price, one unit per part.
    private static List<TradeTarget> SurgeryPartTargets(IReadOnlyList<SurgeryPart> parts, IReadOnlyList<TradeSheetRowFact> rows, HashSet<string> seen)
    {
        var output = new List<TradeTarget>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var part in parts)
        {
            foreach (var item in part.Items)
            {
                TradeSheetRowFact? row = null;
                foreach (var r in rows)
                {
                    if (r.DefName == item.Value && r.TraderCount > 0 && Finite(r.BuyPrice) && r.BuyPrice > 0)
                    {
                        row = r;
                        break;
                    }
                }
                if (row is null)
                {
                    continue;
                }
                if (index.TryGetValue(row.DefName, out var i))
                {
                    output[i] = output[i] with { Stock = output[i].Stock + 1, MaxBuy = Math.Min(output[i].MaxBuy + 1, row.TraderCount) };
                }
                else if (!seen.Contains(row.DefName))
                {
                    index[row.DefName] = output.Count;
                    output.Add(new TradeTarget(row.DefName, row.ColonyCount + 1, 1, 0, 0));
                }
                break;
            }
        }
        return output;
    }
}
