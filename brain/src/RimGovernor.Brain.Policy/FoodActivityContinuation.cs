using RimGovernor.Contracts.Wire.Observations;
using RimGovernor.Brain.Policy.Methods.Generation;
using RimGovernor.Brain.Policy.Methods.Binding;

namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    public static ActivityOutputDelivery? FoodReconsideration(PawnActivities batch, ActivityTaskRead read)
    {
        if (!read.Known || !batch.Tasks.Any(t => t.Step.Job == "Harvest" && batch.Orders.Any(o => o.Task == t.Id
            && read.Pawns.Any(p => p.Pawn == o.Pawn && p.Execution is { State: "completed" } e
                && e.Assignment == o.Assignment && e.Revision == o.Revision)))) return null;
        var delivery = batch.Tasks.SingleOrDefault(t => t.Step.Kind == PawnActivityKind.Deliver);
        return delivery is null || delivery.Source is not { } source || delivery.Step.Destination is not { } destination ? null
            : new(delivery.Resource, source, destination, delivery.Step.Count, delivery.Step.Deadline, delivery.AttemptsLeft);
    }

    public static PawnActivities? ReconsiderObservedFood(PawnActivities batch, Fact<bool> needed, ActivityTaskRead execution,
        FoodActivityRead food, out ForecastActivityOutcomeComparison? comparison, WorkResourceView? resources = null)
    {
        comparison = null;
        var output = FoodReconsideration(batch, execution);
        var ownership = execution.Pawns.Select(p => p.Execution is { } e ? new ActivityOwnership(p.Pawn, e.Assignment, e.Method, e.Revision, e.State)
            : new ActivityOwnership(p.Pawn, "", "", 0, "unowned")).ToArray();
        var takeover = batch.Orders.Any(o => !o.Release && ownership.Any(p => p.Pawn == o.Pawn && !MatchesActivityOwner(o, p)));
        var cleanup = ReviewActivityOwnership(batch, takeover ? Known(false) : needed, execution.Context?.Tick ?? 0, ownership).Release;
        if (cleanup is not null) return cleanup;
        // Keep the decision boundary alive while a helper finishes its already selected approach.
        if (batch.Orders.Any(o => !execution.Pawns.Any(p => p.Pawn == o.Pawn && p.Execution is { } e
            && e.Assignment == o.Assignment && e.Method == o.Method && e.Revision == o.Revision
            && (e.State == "completed" || o.Preparing && e.State == "holding")))) return null;
        if (output is null || !needed.TryGetValue(out var useful) || !useful || food.Context?.Tick is not { } tick
            || batch.Orders.Any(o => o.Revision == long.MaxValue)) return null;
        if (resources is not null && !resources.Materials.IsKnown) return null;
        var candidates = FoodMethodCandidates.GenerateObserved(food, output.Deadline, resources);
        if (candidates.Problem is null) return null;
        comparison = CompareActivityOutcomes(candidates.Problem, candidates.Outcomes, ForecastOutcomeObjective.LowestLaborPerUnit,
            generationTruncated: candidates.Truncated);
        var next = FoodActivityBinding.Reconsider(comparison, candidates.Bindings, batch, tick);
        return next ?? (comparison.Findings.Any(f => f.Contains("unknown", StringComparison.Ordinal)) ? null
            : ReviewActivityOwnership(batch, Known(false), tick, ownership).Release);
    }
    // One bounded trip, at most three observed attempts, under the original acquisition horizon.
    // Native route costs are used as travel estimates; no distance-derived timing or output credit.
    public static PawnActivities? StartFoodActivity(FoodActivityRead read, ulong authority, string method, long deadline, out ForecastActivityOutcomeComparison? comparison, WorkResourceView? resources = null)
    {
        comparison = null;
        if (!read.Known || read.Context?.Tick is not { } tick || deadline <= tick) return null;
        var candidates = FoodMethodCandidates.GenerateStart(read, deadline, resources);
        if (candidates.Problem is null) return null;
        comparison = CompareActivityOutcomes(candidates.Problem, candidates.Outcomes, ForecastOutcomeObjective.LowestLaborPerUnit,
            generationTruncated: candidates.Truncated);
        return FoodActivityBinding.StartCoordinated(comparison, candidates.Bindings, authority, method, tick);
    }

}
