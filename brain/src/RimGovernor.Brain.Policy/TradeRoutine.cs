namespace RimGovernor.Brain.Policy;

// Go: trade_routine.go. The routine trade goal: while a tradeable caravan stands on the map and the colony has
// something to buy from it (the medicine shortfall MaintainMedicalReserves already reports, or a component
// shortfall under the derived component need) or to sell to it (stock above a MaintainResource target),
// RoundsTradePlanner opens one bounded session per caravan and settles it. It is config-only work like a
// configuration push: a negotiator's conversation, not a development project, so it holds no development slot.

/// <summary>
/// One map trader from the trader list as the routine review reads it: CanTrade is native's own verdict that a
/// session can be opened with it now (CanTradeNow, not dismissed, arrived), Travelling that its caravan is still
/// walking to its trade spot, GoodsStacks the size of what it carries. Unpriced marks a tradeable caravan whose
/// sheet the negotiator has no fresh record of: the always-browse phase opens a session to read it.
/// </summary>
public sealed record TraderFacts
{
    public TradeParticipant Participant { get; init; } = new(TradeParticipantKind.Map, "");

    public string ID { get; init; } = "";

    public string Kind { get; init; } = "";

    public string Faction { get; init; } = "";

    public bool CanTrade { get; init; }

    public bool Travelling { get; init; }

    public bool Unpriced { get; init; }

    public long GoodsStacks { get; init; }
}

/// <summary>
/// The colony wealth split native's WealthWatcher reports: the market value held as items, buildings and pawns,
/// and their total. Items count in full toward storyteller wealth where buildings count half, which is why the
/// surplus rule keys on the item share.
/// </summary>
public sealed record WealthFacts(double Items, double Buildings, double Pawns, double Total);

/// <summary>
/// What the review measured worth trading for: the medicine units MaintainMedicalReserves wants, the components
/// short of the target, each MaintainResource target's stock above its floor, and each raw-material hoard
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

    /// <summary>Each MaintainResource floor stock is below: a caravan selling it is the catalog's trade method.</summary>
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

    /// <summary>Go <c>retain</c>: a copy that keeps <paramref name="count"/> of the resource after its sale.</summary>
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

public static partial class PolicyFunctions
{
    /// <summary>The one component definition the trade goal buys and the resource family produces toward under the derived component need.</summary>
    public static readonly Resource ComponentResource = new("ComponentIndustrial");

    /// <summary>The share of total colony wealth held as items past which WealthSurplus sells the raw-material hoards down to their floors.</summary>
    public const double TradeItemWealthShare = 0.6;

    /// <summary>
    /// The silver a purchase never spends below: 100 per colonist, at least 200. An unknown or empty count
    /// reports false and the routine buys nothing (fail closed).
    /// </summary>
    public static (long Reserve, bool Ok) TradeSilverReserve(Fact<long> colonists)
    {
        if (!colonists.TryGetValue(out var n) || n <= 0)
        {
            return (200, false);
        }
        return (Math.Max(200, Math.Min(100 * n, 100000)), true);
    }

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
    /// builds with has no entry and is wholly surplus. Unknown while the consumption is: an unread rate is not an
    /// absence of use.
    /// </summary>
    public static Fact<IReadOnlyDictionary<Resource, long>> TradeRetained(Fact<ResourceConsumption> consumption, IReadOnlyList<ResourceRunway> runways, IReadOnlyDictionary<Resource, long> construction)
    {
        if (!consumption.IsKnown)
        {
            return Unknown<IReadOnlyDictionary<Resource, long>>();
        }
        var output = new Dictionary<Resource, long>();
        foreach (var row in runways)
        {
            var (line, ok) = row.ProtectedLine();
            if (ok)
            {
                output[row.Resource] = output.GetValueOrDefault(row.Resource) + line;
            }
        }
        foreach (var (resource, demand) in construction)
        {
            output[resource] = output.GetValueOrDefault(resource) + Math.Max(0, demand);
        }
        return Known<IReadOnlyDictionary<Resource, long>>(output);
    }

    /// <summary>
    /// The wealth-driven half of the trade surplus: while the item share of total wealth exceeds
    /// TradeItemWealthShare, every deep-deposit def (the raw ore and metal hoards, ItemFacts.DeepResources; never
    /// the currency) stocked above max(target, floor, retained) is a surplus of the difference, so a sale lands
    /// exactly on the highest floor. Unknown wealth, unknown retention, an unmet share or a stock at its floor
    /// yields nothing; the rows come back in DeepResources order.
    /// </summary>
    public static IReadOnlyList<Amount> WealthSurplus(
        IReadOnlyList<Amount> stock,
        ItemFacts items,
        IReadOnlyDictionary<Resource, long> targets,
        IReadOnlyDictionary<string, long> floors,
        Fact<IReadOnlyDictionary<Resource, long>> retainedFact,
        Fact<WealthFacts> wealth
    )
    {
        var known = wealth.TryGetValue(out var facts);
        var retainedKnown = retainedFact.TryGetValue(out var retained);
        if (!known || !retainedKnown)
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
            var keep = Math.Max(targets.GetValueOrDefault(resource), Math.Max(floors.GetValueOrDefault(resource.Value), retained!.GetValueOrDefault(resource)));
            var surplus = counts.GetValueOrDefault(resource) - keep;
            if (surplus > 0)
            {
                output.Add(new Amount(resource, surplus));
            }
        }
        return output;
    }

    /// <summary>Adds the shed_art reason to the need: known negative wealth headroom and unreserved packed art. Anything unknown adds nothing.</summary>
    public static Fact<TradeNeed> ShedArtNeed(Fact<TradeNeed> need, Fact<double> headroom, Fact<long> saleArt)
    {
        var nk = need.TryGetValue(out var n);
        var hk = headroom.TryGetValue(out var h);
        var ak = saleArt.TryGetValue(out var art);
        if (!nk || !hk || !ak || !Finite(h) || h >= 0 || art <= 0)
        {
            return need;
        }
        return Known(n! with { ShedArt = art });
    }

    /// <summary>
    /// Measures the trade need from the same facts the other reviews already produced. It is unknown while the
    /// medicine reserve or the resource census is unknown: a trade opened on a guessed need is not one. An unknown
    /// wealth fact only leaves the wealth-driven surplus out. Floors are EconomicReserves's per-definition floors
    /// (reserves plus construction deficits); target-driven surplus rows win over wealth-driven ones for the same
    /// resource. items.Currency is the colony census's coin, which is neither target nor surplus. A single
    /// optional food context adds the shared ledger's food needs; omitting it leaves food purchases and protected
    /// crop exports disabled.
    /// </summary>
    public static Fact<TradeNeed> ReviewTradeNeed(
        ItemFacts items,
        MedicalReserveReview medicine,
        Fact<EquatableArray<Amount>> resources,
        IReadOnlyDictionary<Resource, long> targets,
        IReadOnlyDictionary<string, long> floors,
        Fact<WealthFacts> wealth,
        Fact<IReadOnlyDictionary<Resource, long>> retained,
        TradeFoodContext? food = null
    )
    {
        var known = medicine.Replenish.TryGetValue(out var replenish);
        var rowsKnown = resources.TryGetValue(out var rows);
        if (!known || !rowsKnown)
        {
            return Unknown<TradeNeed>();
        }
        var stock = new Dictionary<Resource, long>();
        foreach (var row in rows!)
        {
            stock[row.Resource] = stock.GetValueOrDefault(row.Resource) + row.Count;
        }
        var need = new TradeNeed { MedicineReplenish = Math.Max(0, replenish) };
        if (food is not null)
        {
            need = need with { Food = ReviewTradeFood(food) };
        }
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
        foreach (var row in WealthSurplus(rows!, items, targets, floors, retained, wealth))
        {
            if (keeps.ContainsKey(row.Resource))
            {
                continue;
            }
            surplusRows.Add(row);
            keeps[row.Resource] = stock.GetValueOrDefault(row.Resource) - row.Count;
        }
        return Known(need with { Shortfall = shortfall, Surplus = surplusRows, Retained = keeps });
    }

    /// <summary>
    /// TradeWithCaravan's recovered fact: known true when no tradeable or arriving caravan is present or nothing
    /// is worth trading, known false while both hold or while a tradeable caravan is unpriced (the always-browse
    /// session), unknown while the need is. A caravan still travelling counts as present so the goal stands (and
    /// the planner keeps the clock moving) until it arrives. An unknown trader census (a source without a trader
    /// list) also reads as recovered: there is no caravan the planner could open with, so the goal stays off
    /// rather than standing unknown forever.
    /// </summary>
    public static Fact<bool> TradeRecovered(Fact<EquatableArray<TraderFacts>> traders, Fact<TradeNeed> need)
    {
        if (!traders.TryGetValue(out var rows))
        {
            return Known(true);
        }
        var present = false;
        var browse = false;
        foreach (var row in rows!)
        {
            present = present || row.CanTrade || row.Travelling;
            browse = browse || (row.CanTrade && row.Unpriced);
        }
        if (!present)
        {
            return Known(true);
        }
        if (!need.TryGetValue(out var n))
        {
            return Unknown<bool>();
        }
        return Known(!n!.Any() && !browse);
    }

    /// <summary>Picks the caravan to open with: tradeable, not already settled this epoch, most goods first, id order on ties.</summary>
    public static (TraderFacts Trader, bool Ok) SelectTrader(IReadOnlyList<TraderFacts> traders, IReadOnlySet<string> settled)
    {
        var best = new TraderFacts();
        var found = false;
        foreach (var row in traders)
        {
            if (!row.CanTrade || settled.Contains(row.ID))
            {
                continue;
            }
            if (!found || row.GoodsStacks > best.GoodsStacks || (row.GoodsStacks == best.GoodsStacks && string.CompareOrdinal(row.ID, best.ID) < 0))
            {
                best = row;
                found = true;
            }
        }
        return (best, found);
    }

    /// <summary>
    /// Turns the measured need into SelectTrade's ordered targets against one live sheet: food first, medicine
    /// (the cheapest definition the trader carries), then components, then each surplus sale. Purchases are
    /// bounded only by the silver above the reserve; sales take any positive price, since the alternative is the
    /// surplus sitting unsold.
    /// </summary>
    public static TradeEconomicPolicy RoundsTradeTargets(ItemFacts items, TradeNeed need, IReadOnlyList<TradeSheetRowFact> rows, IReadOnlyDictionary<Resource, long> targets, Fact<long> colonists)
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
                if (!items.IsMedicine(new Resource(row.DefName)) || row.TraderCount <= 0 || !row.BuyPriceKnown || !Finite(row.BuyPrice) || row.BuyPrice <= 0)
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
                    if (r.DefName == item.Value && r.TraderCount > 0 && r.BuyPriceKnown && Finite(r.BuyPrice) && r.BuyPrice > 0)
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
