namespace RimGovernor.Brain.Policy;

public sealed record ForecastIncumbentRelease(ForecastObservedIncumbent Incumbent, PawnActivities Batch);
public sealed partial record ForecastCoupledExecution(EquatableArray<ForecastIncumbentRelease> Releases,
    ForecastCoupledSelection? Dispatch);

public sealed partial record ForecastCoupledExecution
{
    /// <summary>Retained work remains under its existing Method. Counterfactual replacement first releases
    /// exact ownership; a subsequent observed review may admit new work. No forecast invents a release receipt.</summary>
    public static ForecastCoupledExecution ReviewCoupledExecution(ForecastCoupledSelection selection,
        EquatableArray<ForecastObservedIncumbent> incumbents, long tick)
    {
        if (incumbents.Count == 0) return new([], selection);
        var retained = selection.Selection.Selected.ToHashSet(StringComparer.Ordinal);
        var releases = new List<ForecastIncumbentRelease>();
        foreach (var incumbent in incumbents.Where(i => !retained.Contains(i.Commitment)))
        {
            var decision = ReviewActivityOwnership(incumbent.Batch, Known(false), tick, [incumbent.Ownership, .. incumbent.ReleasedOwnership]);
            if (decision.Release is not { } release) return new([], null);
            releases.Add(new(incumbent, release));
        }
        if (releases.Count != 0) return new(EquatableArray.From(releases), null);
        var commitments = incumbents.Select(i => i.Commitment).ToHashSet(StringComparer.Ordinal);
        var tasks = incumbents.SelectMany(i => i.Activities.Count > 0 ? i.Activities : [i.Commitment]).ToHashSet(StringComparer.Ordinal);
        var pawns = incumbents.SelectMany(i => i.Batch.Orders.Where(o => !o.Release).Select(o => o.Pawn)).ToHashSet(StringComparer.Ordinal);
        if (selection.Selection.Plan is not { } plan) return new([], null);
        // A forecast may value future work after retention; it cannot install a second owner now.
        var allocations = plan.Allocations.Where(a => !tasks.Contains(a.Activity)).ToArray();
        if (allocations.Any(a => pawns.Contains(a.Pawn))) return new([], null);
        return new([], selection with
        {
            Selection = selection.Selection with
            {
                Selected = selection.Selection.Selected.Where(id => !commitments.Contains(id)).ToArray(),
                Plan = plan with { Allocations = allocations }
            },
            Deferred = EquatableArray.From(selection.Deferred.Where(d => !pawns.Contains(d.Pawn)))
        });
    }
}
