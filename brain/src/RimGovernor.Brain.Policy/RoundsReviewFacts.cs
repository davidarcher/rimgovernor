namespace RimGovernor.Brain.Policy;

/// <summary>The work planner lifecycle and the completed review's resulting demand.</summary>
public enum WorkAssignmentsReview
{
    AwaitingPlanner,
    Settled,
    Owed,
}

public static partial class RoundsRules
{


    /// <summary>
    /// The admin/work-assignments Need's measure (#3304): owed while a coupled activity runs or is selected or deferred,
    /// a hostility, nickname, medicine carry or medical care setting is owed, a work type is uncovered or a cleanup pawn
    /// is idle; AwaitingPlanner until coverage has been planned and nothing else is owed.
    /// </summary>
    public static WorkAssignmentsReview WorkAssignmentsOwed(RoundsFacts f, ForecastCoupledSelection? coupled)
    {
        var owed = f.CoupledWorkActive
            || coupled is { Selection.Selected.Length: > 0 } or { Deferred.Count: > 0 }
            || f.HostilityOwed
            || f.CleanupPawns
            || f.NamesOwed
            || f.MedicineCarryOwed
            || f.MedicalCareOwed
            || f.WorkCoverage == WorkCoverageState.Unmatched;
        if (owed)
        {
            return WorkAssignmentsReview.Owed;
        }
        return f.WorkCoverage == WorkCoverageState.Matched ? WorkAssignmentsReview.Settled : WorkAssignmentsReview.AwaitingPlanner;
    }

    /// <summary>
    /// The review's trade facts the economy family's trade and caravan Needs read (#3316): the silver gap a
    /// purchase need leaves open, and whether the caravans present leave nothing worth trading.
    /// </summary>
    internal static TradeReview TradeReviewOf(RoundsFacts f, RoundsPolicy p, MedicalReserveReview medicine, DerivedDemand demand)
    {
        if (f.FoodPlan is null)
            return new TradeReview.AwaitingFoodPlan();
        return new TradeReview.Assessed(
            SilverGapRules.ReviewSilverGap(f, p, medicine, demand),
            CaravanRecoveredOf(f, p, medicine, demand));
    }

    /// <summary>
    /// Whether a caravan visit leaves nothing to trade: known true when no tradeable caravan is present or nothing
    /// is worth trading, unknown while the trader census or the need is unread. Restore parts a bench could make
    /// do not count.
    /// </summary>
    internal static bool CaravanRecoveredOf(RoundsFacts f, RoundsPolicy p, MedicalReserveReview medicine, DerivedDemand demand)
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
                                TradePolicyRules.ReviewTradeNeed(f.Items, medicine, f.ObservedStock(), demand.Needs, TradePolicyRules.RoundsTradeFloors(p, null), f.Census.Threat.Observed, demand.Retained, TradePolicyRules.RoundsTradeFood(f, p)),
                                f.ObservedStock(),
                                f.Census.ColonistCount,
                                f.ExportProducts),
                            f.ObservedStock(),
                            f.Census.ColonistCount,
                            f.Recipes),
                        f.MedicalPawns),
                    SurgeryRules.SurgeryPurchaseParts(f.MedicalPawns, f.SurgeryContextOf(), SurgeryRules.SurgeryParts(SurgeryRules.SelectSurgery(f.MedicalPawns, null, new SurgeryContext()).Wants), f.FabricableParts ?? new HashSet<Resource>())),
                f.WealthBudget(),
                f.SaleArt),
            f.SaleAnimals(),
            f.Silver(),
            f.Census.ColonistCount);
        tradeNeed = TradePolicyRules.FavorGoldNeed(tradeNeed, f.Traders, f.ObservedStock(), demand.Needs, TradePolicyRules.RoundsTradeFloors(p, null), demand.Retained);
        var shortage = SilverGapRules.RoundsSilverShort(f, p, medicine.Active);
        tradeNeed = TradePolicyRules.FavorPrisonerNeed(tradeNeed, f.Traders, f.SurplusPrisoners(shortage));
        return TradePolicyRules.TradeRecovered(f.Traders, f.UnpricedTraders, TradePolicyRules.PopulationTradeNeed(tradeNeed, PopulationRules.JoinerCapacity(f.JoinerCapacity())));
    }
}
