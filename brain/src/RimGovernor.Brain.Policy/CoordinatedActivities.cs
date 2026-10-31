using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Brain.Policy;

public static class CoordinatedActivitiesRules
{
    /// <summary>Continue the selected attempt. No family names or independent assignment/valuation loop. When the binding
    /// knows why it released the attempt (a stale prerequisite), <paramref name="released"/> receives that reason.</summary>
    public static PawnActivities? ContinueCoordinatedActivity(PawnActivities batch, ActivityNeed needed, ActivityTaskRead read,
        Action<string>? released = null)
    {
        if (batch.Tasks.Count == 0) return null;
        var tick = read.Context.Tick;
        var observed = read.Pawns.Select(p => p.Execution is { } e
            ? new ActivityOwnership(p.Pawn, e.Assignment, e.Method, e.Revision, e.State) { Reason = e.Reason }
            : new ActivityOwnership(p.Pawn, "", "", 0, "unowned")).ToArray();
        if (batch.Orders.Any(o => !read.Pawns.Any(p => p.Pawn == o.Pawn))) return null;
        var rows = observed.ToDictionary(p => p.Pawn, StringComparer.Ordinal);
        // Any takeover ends this coordinated attempt; never release the new owner's pawn.
        if (needed == ActivityNeed.Ended || batch.Tasks.Any(t => t.Step.Deadline <= tick
                && !batch.Orders.Any(o => o.Task == t.Id && rows[o.Pawn].State == "completed"))
            || batch.Orders.Any(o => !o.Release && !ActivityContinuationRules.MatchesActivityOwner(o, rows[o.Pawn])))
            return ActivityContinuationRules.ReviewActivityOwnership(batch, ActivityNeed.Ended, tick, observed).Release;
        if (needed == ActivityNeed.Unlisted && batch.Orders.Any(o => rows[o.Pawn].State == "failed"))
            return ActivityContinuationRules.ReviewActivityOwnership(batch, ActivityNeed.Ended, tick, observed).Release;

        var transition = RimGovernor.Brain.Policy.Methods.Binding.CoordinatedActivityBinding.Continue(batch, read, rows, tick,
            startWork: needed == ActivityNeed.Owed);
        if (transition.Release && transition.Reason.Length != 0) released?.Invoke(transition.Reason);
        return transition.Release ? ActivityContinuationRules.ReviewActivityOwnership(batch, ActivityNeed.Ended, tick, observed).Release : transition.Batch;
    }
}
