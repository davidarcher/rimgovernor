namespace RimGovernor.Brain.Policy;

/// <summary>One positively read pawn. Missing rows are unknown, not unowned.</summary>
public sealed record ActivityOwnership(string Pawn, string Assignment, string Method, long Revision, string State);
public sealed record ActivityContinuationDecision(PawnActivities? Release, bool OwnershipEnded);

public static class ActivityContinuationRules
{
    public static bool MatchesActivityOwner(PawnActivityOrder order, ActivityOwnership row) =>
        row.Pawn == order.Pawn && row.Assignment == order.Assignment && row.Method == order.Method && row.Revision == order.Revision;

    /// <summary>Lifecycle decisions only. An unmet Concern's useful work continues; failed or expired
    /// attempts release for reconsideration. A satisfied Concern cannot strand its participants.</summary>
    public static ActivityContinuationDecision ReviewActivityOwnership(PawnActivities batch, Fact<bool> needed, long tick,
        IReadOnlyList<ActivityOwnership> observed)
    {
        batch.Validate();
        if (tick < 0 || observed.Select(r => r.Pawn).Distinct(StringComparer.Ordinal).Count() != observed.Count)
            throw new DomainException("invalid activity ownership observation");
        var rows = observed.ToDictionary(r => r.Pawn, StringComparer.Ordinal);
        if (batch.Orders.Any(o => !rows.ContainsKey(o.Pawn))) return new(null, false);
        var owned = batch.Orders.Where(o => !o.Release && MatchesActivityOwner(o, rows[o.Pawn])).ToArray();
        if (owned.Length == 0) return new(null, true);
        // Standby serves no Concern and has no deadline: it ends only when other work or native state takes the pawn (#3322).
        if (owned.All(PawnActivities.IsStandby)) return new(null, false);
        var failed = owned.Any(o => rows[o.Pawn].State == "failed");
        var expired = owned.Any(o => o.Steps.Length > 0 && o.Steps.Max(s => s.Deadline) <= tick);
        var knownNeed = needed.TryGetValue(out var useful);
        if ((!knownNeed || useful) && !failed && !expired) return new(null, false);
        if (owned.Any(o => o.Revision == long.MaxValue)) return new(null, false);
        var releases = owned.Select(o => o with { ExpectedRevision = o.Revision, Revision = o.Revision + 1, Release = true, Steps = [] }).ToArray();
        return new(new(batch.AuthorityGeneration, releases), false);
    }

    /// <summary>
    /// The tasks a coupled Method's workers read finished while the Method has bills left (#3624), one key per order
    /// (Method, pawn and revision), so each finished task is one event however often it is read. The rounds review
    /// then reconsiders who takes that worker's next task; no timer does.
    /// </summary>
    public static IReadOnlyList<string> FinishedTasks(PawnActivities batch, IReadOnlyList<ActivityOwnership> observed)
    {
        var orders = batch.Orders.Where(o => !o.Release && !o.Preparing && o.Method.StartsWith("coupled-", StringComparison.Ordinal)).ToArray();
        var current = orders.Select(o => o.Task).ToHashSet(StringComparer.Ordinal);
        if (!batch.Tasks.Any(t => !current.Contains(t.Id) && t.Step.Recipe?.Bill is { Length: > 0 })) return [];
        return orders.Where(o => observed.Any(r => r.State == "completed" && MatchesActivityOwner(o, r)))
            .Select(o => o.Method + "/" + o.Pawn + "/" + o.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    }
}
