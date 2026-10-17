namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>The standby step: no target, no deadline. Native offers its own colony wander, never a work giver.</summary>
    public static PawnActivityStep StandbyStep() => new(PawnActivityKind.Standby, "", "", "", 0, long.MaxValue);

    /// <summary>
    /// The colonists that get the explicit idle (#3322): under brain authority every controlled colonist holds a Hands
    /// assignment, and a colonist the review selected nothing for gets standby. Only a positively read colonist
    /// qualifies: native reports it unassigned, it is available and its current job is idle, no open Method claims it,
    /// no journaled activity action for it is still in flight, and <paramref name="selected"/> (the pawns this review's
    /// own work just took) does not hold it. Sorted, at most 64.
    /// </summary>
    public static EquatableArray<string> StandbyPawnsOf(RoundsFacts facts, WorkActionCensus actions, IReadOnlySet<string> selected)
    {
        if (facts.WorkView is not { } view || actions.Truncated || view.Claims.Truncated
            || !view.Evidence.WorkResources.TryGetValue(out var native) || !view.Evidence.Pawns.TryGetValue(out var pawns)) return [];
        var inFlight = actions.Actions.Where(a => a.Progress.Unresolved || a.Progress.Stage is Stage.Pending or Stage.Prepared
                or Stage.Dispatched or Stage.AwaitingObservation)
            .SelectMany(a => a.Action.PawnActivities?.Orders ?? []).Select(o => o.Pawn).ToHashSet(StringComparer.Ordinal);
        return EquatableArray.From(pawns
            .Where(p => p.Available == Known(true) && p.Occupied == Known(false)
                && native.Where(n => n.Pawn == p.Pawn).ToArray() is [{ Assignment.Length: 0 }]
                && !view.Claims.Pawns.Contains(p.Pawn) && !inFlight.Contains(p.Pawn) && !selected.Contains(p.Pawn))
            .Select(p => p.Pawn).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(64));
    }

    /// <summary>One colonist's standby, its own Method: it claims nothing, so selected work replaces it from revision zero,
    /// and one refused colonist (drafted since the read) never blocks another's.</summary>
    public static PawnActivities StandbyActivity(string pawn, ulong authority, string method) =>
        new(authority, [new PawnActivityOrder(pawn, method + "/" + pawn, method, 0, 1, false, [StandbyStep()])]);
}
