namespace RimGovernor.Brain.BuildingRuntime;

internal static class HerdPlans
{
    /// <summary>The herd plan under the food plan being built: the plan's productive-animal terms are demand for the race's job.</summary>
    public static HerdPolicy HerdPolicyOf(RoundsFacts f, FoodPlan plan)
    {
        var input = f.HerdPlanInput() with { Food = Known(plan) };
        return PlanHerd(input).Policy;
    }
}
