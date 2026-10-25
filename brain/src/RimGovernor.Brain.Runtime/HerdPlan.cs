using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Runtime;

internal static class HerdPlans
{
    /// <summary>The herd plan under the food plan being built: the plan's productive-animal terms are demand for the race's job.</summary>
    public static HerdPolicy HerdPolicyOf(ReviewFacts f, FoodPlan plan)
    {
        var input = f.HerdPlanInput() with { Food = plan };
        return HerdPlanRules.PlanHerd(input).Policy;
    }
}
