namespace RimGovernor.Brain.Policy.Activities.Evaluation;

/// <summary>Only comparison can mint a selection; binding cannot accept an unevaluated candidate.</summary>
internal sealed class ActivitySelection<T>
{
    private ActivitySelection(T binding) => Binding = binding;
    internal T Binding { get; }

    // Shared, family-blind local trip valuation. Full portfolio comparison still belongs to
    // ColonyForecast's outcome evaluation; this adapter does not claim to implement it.
    internal static ActivitySelection<T>? Compare(IEnumerable<ActivityCandidate<T>> candidates)
    {
        ActivitySelection<T>? selected = null;
        var bestCost = double.PositiveInfinity;
        foreach (var candidate in candidates)
        {
            if (!double.IsFinite(candidate.Units) || candidate.Units <= 0) continue;
            var comparison = CompareActivityPlans(candidate.Problem);
            if (comparison.Selected is not { } plan || plan.LaborCost / candidate.Units >= bestCost) continue;
            selected = new(candidate.Binding);
            bestCost = plan.LaborCost / candidate.Units;
        }
        return selected;
    }
}
