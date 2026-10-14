using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>Continue the selected attempt. No family names or independent assignment/valuation loop.</summary>
    public static PawnActivities? ContinueCoordinatedActivity(PawnActivities batch, Fact<bool> needed, ActivityTaskRead read)
    {
        if (batch.Tasks.Count == 0 || !read.Known || read.Context?.Tick is not { } tick) return null;
        var observed = read.Pawns.Select(p => p.Execution is { } e
            ? new ActivityOwnership(p.Pawn, e.Assignment, e.Method, e.Revision, e.State)
            : new ActivityOwnership(p.Pawn, "", "", 0, "unowned")).ToArray();
        if (batch.Orders.Any(o => !read.Pawns.Any(p => p.Pawn == o.Pawn))) return null;
        var rows = observed.ToDictionary(p => p.Pawn, StringComparer.Ordinal);
        // Any takeover ends this coordinated attempt; never release the new owner's pawn.
        var knownNeed = needed.TryGetValue(out var useful);
        if (knownNeed && !useful || batch.Tasks.Any(t => t.Step.Deadline <= tick)
            || batch.Orders.Any(o => !o.Release && !MatchesActivityOwner(o, rows[o.Pawn])))
            return ReviewActivityOwnership(batch, Known(false), tick, observed).Release;
        if (!knownNeed) return batch.Orders.Any(o => rows[o.Pawn].State == "failed")
            ? ReviewActivityOwnership(batch, Known(false), tick, observed).Release : null;

        var transition = RimGovernor.Brain.Policy.Methods.Binding.CoordinatedActivityBinding.Continue(batch, read, rows, tick);
        return transition.Release ? ReviewActivityOwnership(batch, Known(false), tick, observed).Release : transition.Batch;
    }
}
