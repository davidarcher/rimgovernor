using RimGovernor.Contracts.Wire.Observations;
using RimGovernor.Brain.Policy.Activities.Generation;
using RimGovernor.Brain.Policy.Activities.Evaluation;
using RimGovernor.Brain.Policy.Activities.Binding;

namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    // One bounded trip, at most three observed attempts, under the original acquisition horizon.
    // Native route costs are used as travel estimates; no distance-derived timing or output credit.
    public static PawnActivities? StartFoodActivity(FoodActivityRead read, ulong authority, string method, long deadline)
    {
        if (!read.Known || read.Context?.Tick is not { } tick || deadline <= tick) return null;
        var selection = ActivitySelection<FoodActivityOption>.Compare(FoodActivityCandidates.Generate(read, deadline));
        return selection is null ? null : FoodActivityBinding.Start(selection, authority, method, deadline);
    }

    public static PawnActivities? ContinueFoodActivity(PawnActivities batch, Fact<bool> needed, FoodActivityRead read)
    {
        if (batch.Orders.Length != 1 || batch.Orders[0].OutputDelivery is not { } output) return null;
        var order = batch.Orders[0];
        if (!read.Known || read.Context?.Tick is not { } tick || read.Execution is not { } execution) return null;
        var owner = new ActivityOwnership(order.Pawn, execution.Assignment, execution.Method, execution.Revision, execution.State);
        if (!MatchesActivityOwner(order, owner) || execution.State is not ("completed" or "failed") || order.Revision == long.MaxValue) return null;
        // Recovery and unknown demand cannot authorize new productive work. Completing delivery ends this trip.
        if (!needed.TryGetValue(out var useful)) return null;
        var selection = useful && !read.Carrying && output.AttemptsLeft > 0 && read.Context.Tick < output.Deadline
            && !(execution.State == "failed" && order.Steps[0].Kind == PawnActivityKind.Work)
            && !(execution.State == "completed" && order.Steps[0].Kind == PawnActivityKind.Deliver)
            ? ActivitySelection<FoodActivityOption>.Compare(FoodActivityCandidates.Generate(read, output.Deadline, order.Pawn, output.Resource)) : null;
        return FoodActivityBinding.Continue(batch, selection);
    }
}
