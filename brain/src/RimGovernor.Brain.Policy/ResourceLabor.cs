namespace RimGovernor.Brain.Policy;

public static partial class ResourceRules
{
    /// <summary>
    /// The daily budget after selected or retained food commitments. Unpriced commitment work refuses the calculation. An empty plan reserves nothing; each kind/ID is charged once. Throws
    /// <see cref="SupplyPlanFactsException"/> on invalid inputs.
    /// </summary>
    public static double ResidualSupplyLabor(int workers, FoodPlan food)
    {
        var count = workers;
        if (count < 0)
        {
            throw new SupplyPlanFactsException();
        }
        var plan = food;
        var budget = (double)count * 20000;
        var charged = new HashSet<(string, string)>();
        foreach (var rows in new[] { plan.Portfolio, plan.Unknown })
        {
            foreach (var e in rows)
            {
                if (e.Decision == FoodPlanDecision.Close)
                {
                    continue;
                }
                if (!e.Selected && e.Channel.State == CandidateState.Closed)
                {
                    continue;
                }
                if (e.Channel.Work is not CandidateWork.PerDay { Ticks: var work })
                {
                    throw new RequiredFactException("food commitment work");
                }
                if (!FoodForecastRules.FoodNumber(work))
                {
                    throw new SupplyPlanFactsException();
                }
                var id = (Wire.Name(e.Channel.Kind), e.Channel.Id);
                if (charged.Add(id))
                {
                    budget -= work;
                }
            }
        }
        if (double.IsInfinity(budget) || double.IsNaN(budget))
        {
            throw new SupplyPlanFactsException();
        }
        return Math.Max(0, budget);
    }
}
