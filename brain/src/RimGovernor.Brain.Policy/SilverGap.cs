namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    // The silver gap MaintainTrade answers with export orders: a purchase need the colony's silver, after
    // TradeSilverReserve, does not cover. Sold goods leave colony wealth, so raid headroom does not gate this
    // work. SelectTrade sells the goods as surplus.

    /// <summary>The colony's silver from the resource census; unknown while the census or the catalog's currency is.</summary>
    public static Fact<long> SilverStock(ItemFacts items, Fact<EquatableArray<Amount>> resources)
    {
        if (!resources.TryGetValue(out var rows) || items.Currency.Value.Length == 0)
        {
            return Unknown<long>();
        }
        long n = 0;
        foreach (var row in rows)
        {
            if (row.Resource == items.Currency)
            {
                n += row.Count;
            }
        }
        return Known(n);
    }

    /// <summary>The colony silver stock (SilverStock over f.Resources).</summary>
    public static Fact<long> Silver(this RoundsFacts f) => SilverStock(f.Items, f.Resources);

    /// <summary>
    /// The rough silver the need's purchases cost at the game's market values (ItemFacts): the cheapest medicine
    /// a replenish buys, a component, a unit of nutrition as the cheapest raw plant food, and each shortfall
    /// resource. Any is false when the need has no purchase (a surplus or a pawn purchase alone is no silver
    /// need); a price the catalog lacks throws DomainException.
    /// </summary>
    public static (double Price, bool Any) PurchasePrice(this TradeNeed n, ItemFacts items)
    {
        var any = n.MedicineReplenish > 0 || n.ComponentShortfall > 0 || n.Food.Nutrition > 0 || n.Food.Missing.Count > 0 || n.Shortfall.Count > 0;
        var price = 0.0;
        if (n.MedicineReplenish > 0)
        {
            var (_, medicine) = items.CheapestMedicine();
            price += n.MedicineReplenish * medicine;
        }
        if (n.ComponentShortfall > 0)
        {
            var component = items.MarketValue(ComponentResource);
            price += n.ComponentShortfall * component;
        }
        var nutrition = Math.Max(0, n.Food.Nutrition) + (n.Food.Missing.Count * Math.Max(0, n.Food.IngredientNutrition));
        if (nutrition > 0)
        {
            var perNutrition = items.RawFoodNutritionPrice();
            price += nutrition * perNutrition;
        }
        foreach (var row in n.Shortfall)
        {
            var value = items.MarketValue(row.Resource);
            price += row.Count * value;
        }
        return (price, any);
    }

    /// <summary>
    /// The silver the colony lacks: the need's rough purchase price plus TradeSilverReserve less the silver on
    /// hand, zero when the need has no purchase or the silver covers it. Unknown while any input is.
    /// </summary>
    public static Fact<double> SilverGap(ItemFacts items, Fact<TradeNeed> need, Fact<long> silver, Fact<long> colonists)
    {
        var nk = need.TryGetValue(out var n);
        var sk = silver.TryGetValue(out var s);
        var (reserve, rk) = TradeSilverReserve(colonists);
        if (!nk || !sk || !rk)
        {
            return Unknown<double>();
        }
        double price;
        bool any;
        try
        {
            (price, any) = n.PurchasePrice(items);
        }
        catch (DomainException)
        {
            return Unknown<double>();
        }
        if (!any)
        {
            return Known(0.0);
        }
        return Known(Math.Max(0, price + reserve - s));
    }

    /// <summary>
    /// The silver runway deficit: a purchase need exists and the silver on hand is below its rough price plus
    /// TradeSilverReserve (a positive SilverGap). MaintainTrade's exports and sale organ harvests answer it;
    /// unknown while any input is.
    /// </summary>
    public static Fact<bool> SilverShort(ItemFacts items, Fact<TradeNeed> need, Fact<long> silver, Fact<long> colonists)
    {
        if (!SilverGap(items, need, silver, colonists).TryGetValue(out var gap))
        {
            return Unknown<bool>();
        }
        return Known(gap > 0);
    }

    /// <summary>The review's purchase need as the silver decisions read it.</summary>
    private static Fact<TradeNeed> ReviewTradeNeedOf(RoundsFacts f, RoundsPolicy p, MedicalReserveReview medicine, DerivedDemand demand) =>
        ReviewTradeNeed(f.Items, medicine, f.Resources, demand.Needs, RoundsTradeFloors(p, null), f.Wealth, demand.Retained, RoundsTradeFood(f, p));

    private static Fact<double> ReviewSilverGap(RoundsFacts f, RoundsPolicy p, MedicalReserveReview medicine, DerivedDemand demand) =>
        SilverGap(f.Items, ReviewTradeNeedOf(f, p, medicine, demand), f.Silver(), f.Colonists);

    private static Fact<bool> ReviewSilverShort(RoundsFacts f, RoundsPolicy p, MedicalReserveReview medicine, DerivedDemand demand) =>
        SilverShort(f.Items, ReviewTradeNeedOf(f, p, medicine, demand), f.Silver(), f.Colonists);

    /// <summary>
    /// The seasonal policy and the medical reserve review a planner recomputes from its own read, with the
    /// review's latch, as DetectRounds derives them. Throws DomainException as ReviewMedicalReserve does.
    /// </summary>
    private static (RoundsPolicy Policy, MedicalReserveReview Medicine) RoundsMedicineReview(RoundsFacts f, RoundsPolicy p, bool medicineActive)
    {
        p = p.Seasonal(f.Calendar, f.DisasterConditions);
        var facts = f.MedicalReserve with { Colonists = f.Colonists };
        var medicine = ReviewMedicalReserve(facts, medicineActive, p.MedicalReserve);
        return (p, medicine);
    }

    /// <summary>The review's silver runway deficit recomputed by a planner from its own read.</summary>
    public static Fact<bool> RoundsSilverShort(RoundsFacts f, RoundsPolicy p, bool medicineActive)
    {
        try
        {
            var (policy, medicine) = RoundsMedicineReview(f, p, medicineActive);
            return ReviewSilverShort(f, policy, medicine, ResourceDemandOf(f, policy, new RoundsLatches()));
        }
        catch (DomainException)
        {
            return Unknown<bool>();
        }
    }

    /// <summary>The review's silver gap recomputed by a planner from its own read, with the review's derived resource demand beside it.</summary>
    public static (Fact<double> Gap, DerivedDemand Demand) RoundsSilverGap(RoundsFacts f, RoundsPolicy p, bool medicineActive)
    {
        try
        {
            var (policy, medicine) = RoundsMedicineReview(f, p, medicineActive);
            var demand = ResourceDemandOf(f, policy, new RoundsLatches());
            return (ReviewSilverGap(f, policy, medicine, demand), demand);
        }
        catch (DomainException)
        {
            return (Unknown<double>(), new DerivedDemand());
        }
    }

    /// <summary>The market value a work tick adds (StatWorker_MarketValue, 0.0036 silver per tick).</summary>
    internal const double ValuePerWorkTick = 0.0036;
}
