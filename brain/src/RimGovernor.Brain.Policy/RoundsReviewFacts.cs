namespace RimGovernor.Brain.Policy;

public static partial class RoundsRules
{
    internal static Fact<bool> NotFact(Fact<bool> f) => Measured(f, v => !v);

    /// <summary>
    /// The admin/work-assignments Need's measure (#3304): owed while a coupled activity runs or is selected or deferred,
    /// a hostility, nickname, medicine carry or medical care setting is owed, a work type is uncovered or a cleanup pawn
    /// is idle; unknown while the coverage or cleanup census is unread.
    /// </summary>
    public static Fact<bool> WorkAssignmentsOwed(RoundsFacts f, ForecastCoupledSelection? coupled)
    {
        if (f.CoupledWorkActive || coupled is { Selection.Selected.Length: > 0 } or { Deferred.Count: > 0 })
        {
            return Known(true);
        }
        if (Positive(f.HostilityOwed) || Positive(f.NamesOwed) || Positive(f.MedicineCarryOwed) || Positive(f.MedicalCareOwed))
        {
            return Known(true);
        }
        return NotFact(AllFacts(f.WorkCoverage, Measured(f.CleanupPawns, v => !v)));
    }

    /// <summary>
    /// The review's trade facts the economy family's trade and caravan Needs read (#3316): the silver gap a
    /// purchase need leaves open, and whether the caravans present leave nothing worth trading.
    /// </summary>
    internal static TradeReview TradeReviewOf(RoundsFacts f, RoundsPolicy p, MedicalReserveReview medicine, DerivedDemand demand) => new()
    {
        SilverGap = SilverGapRules.ReviewSilverGap(f, p, medicine, demand),
        CaravanRecovered = CaravanRecoveredOf(f, p, medicine, demand),
    };

    /// <summary>
    /// Whether a caravan visit leaves nothing to trade: known true when no tradeable caravan is present or nothing
    /// is worth trading, unknown while the trader census or the need is unread. Restore parts a bench could make
    /// do not count.
    /// </summary>
    internal static Fact<bool> CaravanRecoveredOf(RoundsFacts f, RoundsPolicy p, MedicalReserveReview medicine, DerivedDemand demand)
    {
        var tradeNeed = HerdSaleRules.AnimalSaleNeed(
            f.Items,
            TradePolicyRules.ShedArtNeed(
                SurgeryRules.SurgeryTradeNeed(
                    OrganHarvestRules.ReserveSurgeryStock(
                        OrganHarvestRules.OrganSaleSurplus(
                            f.Items,
                            ExportOrdersRules.ExportSaleSurplus(
                                f.Items,
                                TradePolicyRules.ReviewTradeNeed(f.Items, medicine, f.Resources, demand.Needs, TradePolicyRules.RoundsTradeFloors(p, null), f.Wealth, demand.Retained, TradePolicyRules.RoundsTradeFood(f, p)),
                                f.Resources,
                                f.Colonists,
                                f.ExportProducts),
                            f.Resources,
                            f.Colonists,
                            f.Recipes),
                        f.MedicalPawns),
                    SurgeryRules.SurgeryPurchaseParts(f.MedicalPawns, f.SurgeryContextOf(), SurgeryRules.SurgeryParts(SurgeryRules.SelectSurgery(f.MedicalPawns, null, new SurgeryContext()).Wants), f.FabricableParts ?? new HashSet<Resource>())),
                f.WealthBudget(),
                f.SaleArt),
            f.SaleAnimals(),
            f.Silver(),
            f.Colonists);
        tradeNeed = TradePolicyRules.FavorGoldNeed(tradeNeed, f.Traders, f.Resources, demand.Needs, TradePolicyRules.RoundsTradeFloors(p, null), demand.Retained);
        var shortage = SilverGapRules.RoundsSilverShort(f, p, medicine.Active).TryGetValue(out var silverShort) && silverShort;
        tradeNeed = TradePolicyRules.FavorPrisonerNeed(tradeNeed, f.Traders, f.SurplusPrisoners(shortage));
        return TradePolicyRules.TradeRecovered(f.Traders, f.UnpricedTraders, TradePolicyRules.PopulationTradeNeed(tradeNeed, PopulationRules.JoinerCapacity(f.JoinerCapacity())));
    }
}
