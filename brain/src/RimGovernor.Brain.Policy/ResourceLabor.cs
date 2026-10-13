namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>
    /// The daily budget after selected or retained food commitments. Unknown commitment state or cost leaves
    /// the residual unknown. A known empty plan reserves nothing; each kind/ID is charged once. Throws
    /// <see cref="SupplyPlanFactsException"/> on invalid inputs.
    /// </summary>
    public static Fact<double> ResidualSupplyLabor(Fact<int> workers, Fact<FoodPlan> food)
    {
        var workersKnown = workers.TryGetValue(out var count);
        if (workersKnown && count < 0)
        {
            throw new SupplyPlanFactsException();
        }
        var planKnown = food.TryGetValue(out var knownPlan);
        var plan = planKnown ? knownPlan! : new FoodPlan();
        var known = workersKnown && planKnown;
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
                var stateKnown = e.Channel.State.TryGetValue(out var state);
                var committed = e.Selected() || (stateKnown && state != CandidateState.Closed);
                if (!committed)
                {
                    if (!stateKnown)
                    {
                        known = false;
                    }
                    continue;
                }
                var workKnown = e.Channel.LaborPerDay.TryGetValue(out var work);
                if (workKnown && !FoodNumber(work))
                {
                    throw new SupplyPlanFactsException();
                }
                if (!workKnown)
                {
                    known = false;
                    continue;
                }
                var id = (Wire.Name(e.Channel.Kind), e.Channel.Id);
                if (charged.Add(id))
                {
                    budget -= work;
                }
            }
        }
        if (!known)
        {
            return Unknown<double>();
        }
        if (double.IsInfinity(budget) || double.IsNaN(budget))
        {
            throw new SupplyPlanFactsException();
        }
        return Known(Math.Max(0, budget));
    }
}
