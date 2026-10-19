namespace RimGovernor.Brain.Policy;

public sealed record ForecastIncumbentRelease(ForecastObservedIncumbent Incumbent, PawnActivities Batch);
public sealed partial record ForecastCoupledExecution(EquatableArray<ForecastIncumbentRelease> Releases,
    ForecastCoupledSelection? Dispatch);

public sealed partial record ForecastCoupledExecution
{
    /// <summary>Retained work remains under its existing Method. Counterfactual replacement first releases
    /// exact ownership; a subsequent observed review may admit new work for the released pawn. No forecast invents a
    /// release receipt. The rest of the selection is dispatched in the same review (#3598): every allocation except an
    /// incumbent's own tasks, any task of an incumbent's pawn (retained: it has an owner; released: its release is not yet
    /// observed) and any task waiting on one of those, which would never start.</summary>
    public static ForecastCoupledExecution ReviewCoupledExecution(ForecastCoupledSelection selection,
        EquatableArray<ForecastObservedIncumbent> incumbents, long tick, IReadOnlyList<ForecastActivity>? activities = null)
    {
        if (incumbents.Count == 0) return new([], selection);
        var retained = selection.Selection.Selected.ToHashSet(StringComparer.Ordinal);
        var releases = new List<ForecastIncumbentRelease>();
        foreach (var incumbent in incumbents.Where(i => !retained.Contains(i.Commitment)))
        {
            var decision = ActivityContinuationRules.ReviewActivityOwnership(incumbent.Batch, Known(false), tick, [incumbent.Ownership, .. incumbent.ReleasedOwnership]);
            if (decision.Release is not { } release) return new([], null);
            releases.Add(new(incumbent, release));
        }
        if (selection.Selection.Plan is not { } plan) return new(EquatableArray.From(releases), null);
        var commitments = incumbents.Select(i => i.Commitment).ToHashSet(StringComparer.Ordinal);
        var tasks = incumbents.SelectMany(i => i.Activities.Count > 0 ? i.Activities : [i.Commitment]).ToHashSet(StringComparer.Ordinal);
        var pawns = incumbents.SelectMany(i => i.Batch.Orders.Where(o => !o.Release).Select(o => o.Pawn)).ToHashSet(StringComparer.Ordinal);
        // A forecast may value future work after retention or release; it cannot install a second owner now.
        var dropped = plan.Allocations.Where(a => tasks.Contains(a.Activity) || pawns.Contains(a.Pawn)).Select(a => a.Activity).ToHashSet(StringComparer.Ordinal);
        if (activities is not null)
        {
            var prerequisites = activities.ToDictionary(a => a.Id, a => a.Prerequisites, StringComparer.Ordinal);
            bool Waits(string task, HashSet<string> seen) => prerequisites.TryGetValue(task, out var before)
                && before.Any(p => dropped.Contains(p) || seen.Add(p) && Waits(p, seen));
            foreach (var a in plan.Allocations.Where(a => !dropped.Contains(a.Activity) && Waits(a.Activity, new(StringComparer.Ordinal))).ToArray())
                dropped.Add(a.Activity);
        }
        var allocations = plan.Allocations.Where(a => !dropped.Contains(a.Activity)).ToArray();
        var deferred = selection.Deferred.Where(d => !pawns.Contains(d.Pawn)).ToArray();
        // Retention with future work for its own pawn and nothing else to start keeps the incumbent alone.
        if (allocations.Length == 0 && deferred.Length == 0) return new(EquatableArray.From(releases), null);
        return new(EquatableArray.From(releases), selection with
        {
            Selection = selection.Selection with
            {
                Selected = selection.Selection.Selected.Where(id => !commitments.Contains(id)).ToArray(),
                Plan = plan with { Allocations = allocations }
            },
            Deferred = EquatableArray.From(deferred)
        });
    }
}
