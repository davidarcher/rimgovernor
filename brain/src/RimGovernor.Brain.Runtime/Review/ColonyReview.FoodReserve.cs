using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Runtime;

public sealed partial class ColonyReview
{
    internal void ReviewReserve(ref ColonyReading p)
    {
        var staged = Policy;
        FoodReserveState reserve = staged.FoodReserveDays == 0
            ? new FoodReserveState.Disabled()
            : FoodReserveRules.ReviewFoodReserve(p.Facts.FoodSupply, null, staged.FoodReserveDays, Seasonal(p.Facts).FoodMinDays, FoodReserveRules.FoodDeliveryDays(p.Facts.FoodPlan));
        p = p with { Facts = p.Facts with { FoodReserve = reserve } };
    }
}
