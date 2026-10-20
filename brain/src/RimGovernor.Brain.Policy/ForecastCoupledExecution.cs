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
    /// observed) and any task waiting on one of those, which would never start. A reconsidered productive Method (one with
    /// bills, #3167) has no retain commitment: when the selection gives one of its reconsidered workers' bills to a pawn other
    /// than its cooks, those workers alone are released and its other participants keep working (#3624); otherwise it keeps its
    /// owners like any retained incumbent.</summary>
    public static ForecastCoupledExecution ReviewCoupledExecution(ForecastCoupledSelection selection,
        EquatableArray<ForecastObservedIncumbent> incumbents, long tick, IReadOnlyList<ForecastActivity>? activities = null,
        ForecastCoupledCandidates? candidates = null)
    {
        if (incumbents.Count == 0) return new([], selection);
        var retained = selection.Selection.Selected.ToHashSet(StringComparer.Ordinal);
        var bindings = candidates?.Context.Bindings;
        bool Moved(ForecastObservedIncumbent incumbent)
        {
            var cooks = ForecastObservedIncumbent.CooksOf(incumbent.Batch, incumbent.Bills);
            return bindings is not null && selection.Selection.Plan is { } chosen && chosen.Allocations.Any(a => !cooks.Contains(a.Pawn)
                && bindings.TryGetValue((a.Activity, a.Pawn), out var b) && (b.Recipe?.Bill ?? b.Cooking?.Continuation.Bill) is { } bill
                && incumbent.Bills.Contains(bill));
        }
        var releases = new List<ForecastIncumbentRelease>();
        foreach (var incumbent in incumbents.Where(i => i.Bills.Count > 0 ? Moved(i) : !retained.Contains(i.Commitment)))
        {
            // A reconsidered Method releases only the workers its review decided for; the rest keep working (#3624).
            var release = incumbent.Bills.Count > 0
                ? Methods.Binding.CoordinatedActivityBinding.Release(incumbent.Batch, incumbent.Reconsidered)
                : ActivityContinuationRules.ReviewActivityOwnership(incumbent.Batch, Known(false), tick, incumbent.Owned).Release;
            if (release is null) return new([], null);
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
        var deferred = selection.Deferred.Where(d => !pawns.Contains(d)).ToArray();
        // Retention with future work for its own pawn and nothing else to start keeps the incumbent alone.
        if (allocations.Length == 0) return new(EquatableArray.From(releases), null);
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
