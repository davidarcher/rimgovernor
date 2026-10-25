namespace RimGovernor.Brain.Policy;

public static partial class SilverGapRules
{
    // The silver gap the economy/trade Need answers with export orders: a purchase need the colony's silver, after
    // TradeSilverReserve, does not cover. Sold goods leave colony wealth, so raid headroom does not gate this
    // work. SelectTrade sells the goods as surplus.

    /// <summary>The colony's silver from the resource census; refusing a missing currency definition.</summary>
    public static long SilverStock(ItemFacts items, EquatableArray<Amount> resources)
    {
        if (items.Currency.Value.Length == 0)
        {
            throw new RequiredFactException("currency definition");
        }
        long n = 0;
        foreach (var row in resources)
        {
            if (row.Resource == items.Currency)
            {
                n += row.Count;
            }
        }
        return n;
    }

    /// <summary>The colony silver stock (SilverStock over f.Resources).</summary>
    public static long Silver(this RoundsFacts f) => SilverStock(f.Items, f.Resources);

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
            price += TradePolicyRules.MedicineClaim(items, n.MedicineReplenish);
        }
        if (n.ComponentShortfall > 0)
        {
            var component = items.MarketValue(TradePolicyRules.ComponentResource);
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
    /// hand, zero when the need has no purchase or the silver covers it. Missing prices refuse the calculation.
    /// </summary>
    public static double SilverGap(ItemFacts items, TradeNeed need, long silver, long colonists)
    {
        var (reserve, ok) = TradePolicyRules.TradeSilverReserve(colonists);
        if (!ok)
        {
            throw new RequiredFactException("colonist count");
        }
        var (price, any) = need.PurchasePrice(items);
        return any ? Math.Max(0, price + reserve - silver) : 0;
    }

    /// <summary>Whether purchase demand exceeds the silver above the colony reserve.</summary>
    public static bool SilverShort(ItemFacts items, TradeNeed need, long silver, long colonists) =>
        SilverGap(items, need, silver, colonists) > 0;
    /// <summary>The review's purchase need as the silver decisions read it.</summary>
    private static TradeNeed ReviewTradeNeedOf(RoundsFacts f, RoundsPolicy p, MedicalReserveReview medicine, DerivedDemand demand) =>
        TradePolicyRules.ReviewTradeNeed(f.Items, medicine, f.Resources, demand.Needs, TradePolicyRules.RoundsTradeFloors(p, null), f.Wealth, demand.Retained, TradePolicyRules.RoundsTradeFood(f, p));

    internal static double ReviewSilverGap(RoundsFacts f, RoundsPolicy p, MedicalReserveReview medicine, DerivedDemand demand) =>
        SilverGap(f.Items, ReviewTradeNeedOf(f, p, medicine, demand), f.Silver(), f.Colonists);

    private static bool ReviewSilverShort(RoundsFacts f, RoundsPolicy p, MedicalReserveReview medicine, DerivedDemand demand) =>
        SilverShort(f.Items, ReviewTradeNeedOf(f, p, medicine, demand), f.Silver(), f.Colonists);

    /// <summary>
    /// The seasonal policy and the medical reserve review a planner recomputes from its own read, with the
    /// review's latch, as DetectRounds derives them. Throws DomainException as ReviewMedicalReserve does.
    /// </summary>
    private static (RoundsPolicy Policy, MedicalReserveReview Medicine) RoundsMedicineReview(RoundsFacts f, RoundsPolicy p, bool medicineActive)
    {
        p = p.Seasonal(f.Calendar, f.DisasterConditions);
        var facts = f.MedicalReserve with { Colonists = f.Colonists };
        var medicine = MedicalRules.ReviewMedicalReserve(facts, medicineActive, p.MedicalReserve);
        return (p, medicine);
    }

    /// <summary>The review's silver runway deficit recomputed by a planner from its own read.</summary>
    public static bool RoundsSilverShort(RoundsFacts f, RoundsPolicy p, bool medicineActive)
    {
        var (policy, medicine) = RoundsMedicineReview(f, p, medicineActive);
        return ReviewSilverShort(f, policy, medicine, ResourceRules.ResourceDemandOf(f, policy, new RoundsLatches()));
    }
    /// <summary>The review's silver gap recomputed by a planner from its own read, with the review's derived resource demand beside it.</summary>
    public static (double Gap, DerivedDemand Demand) RoundsSilverGap(RoundsFacts f, RoundsPolicy p, bool medicineActive)
    {
        var (policy, medicine) = RoundsMedicineReview(f, p, medicineActive);
        var demand = ResourceRules.ResourceDemandOf(f, policy, new RoundsLatches());
        return (ReviewSilverGap(f, policy, medicine, demand), demand);
    }
    /// <summary>The market value a work tick adds (StatWorker_MarketValue, 0.0036 silver per tick).</summary>
    internal const double ValuePerWorkTick = 0.0036;
}
