namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class Rounder
{
    /// <summary>Sets the projection's cooking fact from the meal tier the policy picks; returns the projection unchanged when the tier cannot be reviewed.</summary>
    internal ColonyReading ReviewMeals(ColonyReading p)
    {
        var seasonal = Seasonal(p.Facts);
        var request = ColonyPlanningRules.MealRequest(p.Facts, p.Snapshot.ControlledEnvironment, seasonal.FoodMinDays, seasonal.FoodTargetDays);
        MealTierReview review;
        try
        {
            review = MealRules.ReviewMealTier(request, p.Facts.EssentialInputs.Benches);
        }
        catch (DomainException)
        {
            return p;
        }

        if (review.Tier == MealTier.Paste)
        {
            var pasteReady = false;
            foreach (var dispenser in p.Facts.FoodChannels.PasteDispenser)
            {
                pasteReady = pasteReady || (dispenser.Powered && dispenser.HopperNutrition > 0);
            }

            return p with { Facts = p.Facts with { Cooking = pasteReady } };
        }

        if (review.Recipes.Count == 0)
        {
            return p;
        }

        var ready = false;
        foreach (var choice in review.Recipes)
        {
            foreach (var bench in p.Facts.EssentialInputs.Benches)
            {
                if (bench.Id != choice.Bench)
                {
                    continue;
                }

                foreach (var bill in bench.Bills)
                {
                    ready = ready || (bill.Active && bill.Recipe == choice.Recipe);
                }
            }
        }

        return p with { Facts = p.Facts with { Cooking = ready } };
    }
}
