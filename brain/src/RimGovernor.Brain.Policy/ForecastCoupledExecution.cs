namespace RimGovernor.Brain.Policy;

public sealed record ForecastIncumbentRelease(ForecastObservedIncumbent Incumbent, PawnActivities Batch);
public sealed partial record ForecastCoupledExecution(EquatableArray<ForecastIncumbentRelease> Releases,
    ForecastCoupledSelection? Dispatch);

public sealed partial record ForecastCoupledExecution
{
    /// <summary>Retained work remains under its existing Method. Counterfactual replacement first releases
    /// exact ownership; a subsequent observed review may admit new work for the released pawn. No forecast invents a
    /// release receipt. The rest of the selection is dispatched in the same review (#3598): every allocation except what the
    /// incumbents hold (<see cref="ForecastIncumbentHold.Dispatched"/>). A reconsidered productive Method (one with
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
                && bindings.TryGetValue((a.Activity, a.Pawn), out var b) && b.Recipe?.Bill is { } bill
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
        var hold = ForecastIncumbentHold.Of(incumbents);
        var (selected, allocations) = hold.Dispatched(selection.Selection.Selected, plan.Allocations,
            activities?.ToDictionary(a => a.Id, StringComparer.Ordinal));
        var deferred = selection.Deferred.Where(d => !hold.Pawns.Contains(d)).ToArray();
        // Retention with future work for its own pawn and nothing else to start keeps the incumbent alone.
        if (allocations.Length == 0) return new(EquatableArray.From(releases), null);
        return new(EquatableArray.From(releases), selection with
        {
            Selection = selection.Selection with { Selected = selected, Plan = plan with { Allocations = allocations } },
            Deferred = EquatableArray.From(deferred)
        });
    }
}

/// <summary>
/// What the review's incumbents hold from a fresh order now (#3598): their commitments, their own tasks, and their pawns, each
/// retained under its owner or released with its release not yet observed. The review dispatches a selection without them
/// (<see cref="Dispatched"/>), and the forecast reads the same dispatch for whom the outcome orders at tick 0 (#3889), so both
/// come from this one rule. Set on the context by <see cref="ForecastIncumbentPreparation.PrepareCoupledIncumbents"/>; a review
/// with no incumbent holds nothing.
/// </summary>
public sealed record ForecastIncumbentHold(EquatableArray<string> Commitments, EquatableArray<string> Tasks, EquatableArray<string> Pawns)
{
    public static readonly ForecastIncumbentHold Nothing = new([], [], []);

    public static ForecastIncumbentHold Of(IEnumerable<ForecastObservedIncumbent> incumbents)
    {
        var all = incumbents.ToArray();
        return new(EquatableArray.From(all.Select(i => i.Commitment).Distinct(StringComparer.Ordinal)),
            EquatableArray.From(all.SelectMany(i => i.Activities.Count > 0 ? i.Activities : [i.Commitment]).Distinct(StringComparer.Ordinal)),
            EquatableArray.From(all.SelectMany(i => i.Batch.Orders.Where(o => !o.Release).Select(o => o.Pawn)).Distinct(StringComparer.Ordinal)));
    }

    /// <summary>The selection the review dispatches now: its ids but the incumbents' commitments, and its allocations but the
    /// incumbents' own tasks, any task of an incumbent's pawn and any task waiting on one of those (by
    /// <paramref name="activities"/>' prerequisites, when given), which would never start. A forecast may value future work after
    /// retention or release; it cannot install a second owner now.</summary>
    internal (string[] Selected, ForecastActivityAllocation[] Allocations) Dispatched(IEnumerable<string> selected,
        IReadOnlyList<ForecastActivityAllocation> allocations, IReadOnlyDictionary<string, ForecastActivity>? activities)
    {
        var commitments = Commitments.ToHashSet(StringComparer.Ordinal);
        var tasks = Tasks.ToHashSet(StringComparer.Ordinal);
        var pawns = Pawns.ToHashSet(StringComparer.Ordinal);
        var dropped = allocations.Where(a => tasks.Contains(a.Activity) || pawns.Contains(a.Pawn)).Select(a => a.Activity).ToHashSet(StringComparer.Ordinal);
        if (activities is not null && dropped.Count != 0)
        {
            bool Waits(string task, HashSet<string> seen) => activities.TryGetValue(task, out var before)
                && before.Prerequisites.Any(p => dropped.Contains(p) || seen.Add(p) && Waits(p, seen));
            foreach (var a in allocations.Where(a => !dropped.Contains(a.Activity) && Waits(a.Activity, new(StringComparer.Ordinal))).ToArray())
                dropped.Add(a.Activity);
        }
        return (selected.Where(id => !commitments.Contains(id)).ToArray(), allocations.Where(a => !dropped.Contains(a.Activity)).ToArray());
    }
}
