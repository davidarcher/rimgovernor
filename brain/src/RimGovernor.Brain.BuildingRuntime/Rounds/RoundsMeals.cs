namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class Rounder
{
    /// <summary>Sets the projection's cooking fact from the meal tier the policy picks; returns the projection unchanged when the tier cannot be reviewed.</summary>
    internal ColonyProjection ReviewMeals(ColonyProjection p)
    {
        var seasonal = Seasonal(p.Facts);
        var request = p.MealRequest(seasonal.FoodMinDays, seasonal.FoodTargetDays);
        MealTierReview review;
        try
        {
            review = PolicyFunctions.ReviewMealTier(request, p.ProductionBenches);
        }
        catch (DomainException)
        {
            return p;
        }

        if (review.Tier == MealTier.Paste)
        {
            if (!p.FoodChannels.TryGetValue(out var channels))
            {
                return p;
            }

            var pasteReady = false;
            foreach (var dispenser in channels.PasteDispenser)
            {
                var pk = dispenser.Powered.TryGetValue(out var powered);
                var nk = dispenser.HopperNutrition.TryGetValue(out var nutrition);
                pasteReady = pasteReady || (pk && powered && nk && nutrition > 0);
            }

            return p with { Facts = p.Facts with { Cooking = Known(pasteReady) } };
        }

        if (review.Recipes.Count == 0)
        {
            return p;
        }

        p.ProductionBenches.TryGetValue(out var benches);
        var ready = false;
        foreach (var choice in review.Recipes)
        {
            foreach (var bench in benches)
            {
                if (bench.Id != choice.Bench)
                {
                    continue;
                }

                foreach (var bill in bench.Bills)
                {
                    var known = bill.Active.TryGetValue(out var active);
                    ready = ready || (known && active && bill.Recipe == choice.Recipe);
                }
            }
        }

        return p with { Facts = p.Facts with { Cooking = Known(ready) } };
    }
}
