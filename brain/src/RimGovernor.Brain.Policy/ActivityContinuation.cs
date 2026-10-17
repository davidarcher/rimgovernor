namespace RimGovernor.Brain.Policy;

/// <summary>One positively read pawn. Missing rows are unknown, not unowned.</summary>
public sealed record ActivityOwnership(string Pawn, string Assignment, string Method, long Revision, string State);
public sealed record ActivityContinuationDecision(PawnActivities? Release, bool OwnershipEnded, string Reason);

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
        if (batch.Orders.Any(o => !rows.ContainsKey(o.Pawn))) return new(null, false, "unknown_activity_ownership");
        var owned = batch.Orders.Where(o => !o.Release && MatchesActivityOwner(o, rows[o.Pawn])).ToArray();
        if (owned.Length == 0) return new(null, true, "activity_ownership_ended");
        // Standby serves no Concern and has no deadline: it ends only when other work or native state takes the pawn (#3322).
        if (owned.All(PawnActivities.IsStandby)) return new(null, false, "standby");
        var failed = owned.Any(o => rows[o.Pawn].State == "failed");
        var expired = owned.Any(o => o.Steps.Length > 0 && o.Steps.Max(s => s.Deadline) <= tick);
        var knownNeed = needed.TryGetValue(out var useful);
        if ((!knownNeed || useful) && !failed && !expired)
            return new(null, false, knownNeed ? "continue_activity_method" : "unknown_activity_need");
        if (owned.Any(o => o.Revision == long.MaxValue)) return new(null, false, "activity_revision_exhausted");
        var releases = owned.Select(o => o with { ExpectedRevision = o.Revision, Revision = o.Revision + 1, Release = true, Steps = [] }).ToArray();
        return new(new(batch.AuthorityGeneration, releases), false,
            knownNeed && !useful ? "activity_concern_recovered" : failed ? "activity_failed" : "activity_deadline");
    }
}
