namespace RimGovernor.Brain.Policy;

/// <summary>One positively read pawn. Missing rows are unknown, not unowned. <see cref="Reason"/> is the native execution's
/// stop or release reason ("" while it runs), which tells a native job takeover from the order's own failure.</summary>
public sealed record ActivityOwnership(string Pawn, string Assignment, string Method, long Revision, string State)
{
    public string Reason { get; init; } = "";
}
public sealed record ActivityContinuationDecision(PawnActivities? Release, bool OwnershipEnded);

/// <summary>Whether the Need an installed activity Method serves still owes its work.</summary>
public enum ActivityNeed
{
    /// <summary>The current review lists the Need unmet or working a Method: the Method's work goes on.</summary>
    Owed,

    /// <summary>The Method's work is over (its Need's plan was superseded, or its service is complete): the batch is released.</summary>
    Ended,

    /// <summary>The current review lists no row for the Need: it starts no new work, allowing observed terminal cleanup,
    /// native-service handback and release of a failed or expired batch.</summary>
    Unlisted,
}

public static class ActivityContinuationRules
{
    public static bool MatchesActivityOwner(PawnActivityOrder order, ActivityOwnership row) =>
        row.Pawn == order.Pawn && row.Assignment == order.Assignment && row.Method == order.Method && row.Revision == order.Revision;

    /// <summary>Lifecycle decisions only. An unmet Concern's useful work continues; failed or expired
    /// attempts release for reconsideration. A satisfied Concern cannot strand its participants.</summary>
    public static ActivityContinuationDecision ReviewActivityOwnership(PawnActivities batch, ActivityNeed needed, long tick,
        IReadOnlyList<ActivityOwnership> observed)
    {
        batch.Validate();
        if (tick < 0 || observed.Select(r => r.Pawn).Distinct(StringComparer.Ordinal).Count() != observed.Count)
            throw new DomainException("invalid activity ownership observation");
        var rows = observed.ToDictionary(r => r.Pawn, StringComparer.Ordinal);
        if (batch.Orders.Any(o => !rows.ContainsKey(o.Pawn))) return new(null, false);
        var owned = batch.Orders.Where(o => !o.Release && MatchesActivityOwner(o, rows[o.Pawn])).ToArray();
        if (owned.Length == 0) return new(null, true);
        // An owned failure (its own job condition) and a native job takeover of the owned order alike end the attempt.
        var failed = owned.Any(o => InterventionPurposes.Observe(o, rows[o.Pawn]) is ObservedOutcome.Failed or ObservedOutcome.Replaced);
        var takenOver = batch.Orders.Any(o => !o.Release && !MatchesActivityOwner(o, rows[o.Pawn]));
        var expired = owned.Any(o => o.Steps.Length > 0 && o.Steps.Max(s => s.Deadline) <= tick);
        if (needed != ActivityNeed.Ended && !takenOver && !failed && !expired) return new(null, false);
        if (owned.Any(o => o.Revision == long.MaxValue)) return new(null, false);
        var releases = owned.Select(o => o with { ExpectedRevision = o.Revision, Revision = o.Revision + 1, Release = true, Steps = [] }).ToArray();
        return new(new(batch.AuthorityGeneration, releases), false);
    }

    /// <summary>
    /// The tasks a coupled Method's workers read finished while the worker has bills left (#3624), one key per order
    /// (Method, pawn and revision), so each finished task is one event however often it is read. The rounds review
    /// then reconsiders who takes that worker's next task; no timer does. Any one worker's finished task counts, whatever
    /// the other participants are doing.
    /// </summary>
    public static IReadOnlyList<string> FinishedTasks(PawnActivities batch, IReadOnlyList<ActivityOwnership> observed)
    {
        var orders = batch.Orders.Where(o => !o.Release && !o.Preparing && PawnActivities.Intervention(o)).ToArray();
        var current = orders.Select(o => o.Task).ToHashSet(StringComparer.Ordinal);
        return orders.Where(o => batch.Tasks.Any(t => t.Pawn == o.Pawn && !current.Contains(t.Id) && t.Step.Recipe?.Bill is { Length: > 0 })
                && observed.Any(r => r.State == "completed" && MatchesActivityOwner(o, r)))
            .Select(o => o.Method + "/" + o.Pawn + "/" + o.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    }
}
