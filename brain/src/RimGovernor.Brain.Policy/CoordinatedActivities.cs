using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

public static class CoordinatedActivitiesRules
{
    /// <summary>Continue the selected attempt. No family names or independent assignment/valuation loop.</summary>
    public static PawnActivities? ContinueCoordinatedActivity(PawnActivities batch, ActivityNeed needed, ActivityTaskRead read)
    {
        if (batch.Tasks.Count == 0) return null;
        var tick = read.Context.Tick;
        var observed = read.Pawns.Select(p => p.Execution is { } e
            ? new ActivityOwnership(p.Pawn, e.Assignment, e.Method, e.Revision, e.State)
            : new ActivityOwnership(p.Pawn, "", "", 0, "unowned")).ToArray();
        if (batch.Orders.Any(o => !read.Pawns.Any(p => p.Pawn == o.Pawn))) return null;
        var rows = observed.ToDictionary(p => p.Pawn, StringComparer.Ordinal);
        // Any takeover ends this coordinated attempt; never release the new owner's pawn.
        if (needed == ActivityNeed.Ended || batch.Tasks.Any(t => t.Step.Deadline <= tick
                && !batch.Orders.Any(o => o.Task == t.Id && rows[o.Pawn].State == "completed"))
            || batch.Orders.Any(o => !o.Release && !ActivityContinuationRules.MatchesActivityOwner(o, rows[o.Pawn])))
            return ActivityContinuationRules.ReviewActivityOwnership(batch, ActivityNeed.Ended, tick, observed).Release;
        if (needed == ActivityNeed.Unlisted) return batch.Orders.Any(o => rows[o.Pawn].State == "failed")
            ? ActivityContinuationRules.ReviewActivityOwnership(batch, ActivityNeed.Ended, tick, observed).Release : null;

        var transition = RimGovernor.Brain.Policy.Methods.Binding.CoordinatedActivityBinding.Continue(batch, read, rows, tick);
        return transition.Release ? ActivityContinuationRules.ReviewActivityOwnership(batch, ActivityNeed.Ended, tick, observed).Release : transition.Batch;
    }
}
