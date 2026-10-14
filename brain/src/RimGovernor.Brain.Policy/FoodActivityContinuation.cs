using RimGovernor.Contracts.Wire.Observations;
using RimGovernor.Brain.Policy.Methods.Generation;
using RimGovernor.Brain.Policy.Methods.Binding;

namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    // One bounded trip, at most three observed attempts, under the original acquisition horizon.
    // Native route costs are used as travel estimates; no distance-derived timing or output credit.
    public static PawnActivities? StartFoodActivity(FoodActivityRead read, ulong authority, string method, long deadline, out ForecastActivityOutcomeComparison? comparison)
    {
        comparison = null;
        if (!read.Known || read.Context?.Tick is not { } tick || deadline <= tick) return null;
        var candidates = FoodMethodCandidates.Generate(read, deadline);
        if (candidates.Problem is null) return null;
        comparison = CompareActivityOutcomes(candidates.Problem, candidates.Outcomes, ForecastOutcomeObjective.LowestLaborPerUnit,
            generationTruncated: candidates.Truncated);
        return FoodActivityBinding.Start(comparison, candidates.Bindings, authority, method, deadline);
    }

    public static PawnActivities? ContinueFoodActivity(PawnActivities batch, Fact<bool> needed, FoodActivityRead read, out ForecastActivityOutcomeComparison? comparison)
    {
        comparison = null;
        if (batch.Orders.Length != 1 || batch.Orders[0].OutputDelivery is not { } output) return null;
        var order = batch.Orders[0];
        if (!read.Known || read.Context?.Tick is not { } tick || read.Execution is not { } execution) return null;
        var owner = new ActivityOwnership(order.Pawn, execution.Assignment, execution.Method, execution.Revision, execution.State);
        if (!MatchesActivityOwner(order, owner) || execution.State is not ("completed" or "failed") || order.Revision == long.MaxValue) return null;
        // Recovery and unknown demand cannot authorize new productive work. Completing delivery ends this trip.
        if (!needed.TryGetValue(out var useful)) return null;
        var candidates = FoodMethodCandidates.Generate(read, output.Deadline, order.Pawn, output.Resource, output,
            allowCooking: order.Steps[0].Kind == PawnActivityKind.Work);
        comparison = candidates.Problem is not null && useful && !read.Carrying && output.AttemptsLeft > 0 && read.Context.Tick < output.Deadline
            && !(execution.State == "failed" && order.Steps[0].Kind == PawnActivityKind.Work)
            && !(execution.State == "failed" && (order.Steps[0].Kind == PawnActivityKind.Cook || output.Cooking is not null))
            && !(execution.State == "completed" && order.Steps[0].Kind == PawnActivityKind.Deliver && output.Cooking is null)
            ? CompareActivityOutcomes(candidates.Problem, candidates.Outcomes, ForecastOutcomeObjective.LowestLaborPerUnit, generationTruncated: candidates.Truncated) : null;
        return FoodActivityBinding.Continue(batch, comparison, candidates.Bindings);
    }
}
