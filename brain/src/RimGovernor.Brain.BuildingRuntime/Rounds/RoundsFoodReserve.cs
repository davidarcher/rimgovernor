using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class Rounder
{
    internal void ReviewReserve(ref ColonyProjection p)
    {
        p = p with { Facts = p.Facts with { FoodReserve = Unknown<FoodReserveReview>() } };
        var known = p.FoodSupply.TryGetValue(out var supply);
        var staged = Policy;
        if (!known || staged.FoodReserveDays == 0)
        {
            return;
        }

        try
        {
            var reserve = FoodReserveRules.ReviewFoodReserve(supply!, null, staged.FoodReserveDays, Seasonal(p.Facts).FoodMinDays, FoodDeliveryDays(p.Facts.FoodPlan));
            p = p with { Facts = p.Facts with { FoodReserve = Known(reserve) } };
        }
        catch (DomainException)
        {
            // An unreadable review leaves the reserve unknown.
        }
    }
}

partial class Rounder
{
    internal static Fact<EquatableArray<double>> FoodDeliveryDays(Fact<FoodPlan> f)
    {
        if (!f.TryGetValue(out var plan) || plan.Unknown.Count > 0)
        {
            return Unknown<EquatableArray<double>>();
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
                return Unknown<EquatableArray<double>>();
            }

            days.Add(lead);
        }

        return Known(EquatableArray.From(days));
    }
}
