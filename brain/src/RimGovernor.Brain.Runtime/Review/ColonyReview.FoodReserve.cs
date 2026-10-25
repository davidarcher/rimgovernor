using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Runtime;

public sealed partial class ColonyReview
{
    internal void ReviewReserve(ref ColonyReading p)
    {
        var staged = Policy;
        FoodReserveState reserve = staged.FoodReserveDays == 0
            ? new FoodReserveState.Disabled()
            : FoodReserveRules.ReviewFoodReserve(p.Facts.FoodSupply, null, staged.FoodReserveDays, Seasonal(p.Facts).FoodMinDays, FoodDeliveryDays(p.Facts.FoodPlan));
        p = p with { Facts = p.Facts with { FoodReserve = reserve } };
    }
}

partial class ColonyReview
{
    internal static FoodDeliveries FoodDeliveryDays(FoodPlan? plan)
    {
        if (plan is null || plan.Unknown.Count > 0)
        {
            return new FoodDeliveries.Unconfirmed();
        }

        var days = new List<double>();
        foreach (var row in plan.Portfolio)
        {
            if (row.Decision == FoodPlanDecision.Close || row.DeliveredPerDay <= 0)
            {
                continue;
            }

            if (row.Channel.Lead is not CandidateLead.Days { Value: var lead })
            {
                return new FoodDeliveries.Unconfirmed();
            }

            days.Add(lead);
        }

        return new FoodDeliveries.Confirmed(EquatableArray.From(days));
    }
}
