using RimGovernor.Brain.Policy.Activities.Evaluation;
using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy.Activities.Binding;

internal static class FoodActivityBinding
{
    internal static PawnActivities Start(ActivitySelection<FoodActivityOption> selection, ulong authority, string method, long deadline)
    {
        var option = selection.Binding;
        var output = new ActivityOutputDelivery(option.Resource, option.Origin, option.Destination, option.Count, deadline, 3);
        return new(authority, [new(option.Pawn, method + "/" + option.Pawn, method, 0, 1, false,
            [new(PawnActivityKind.Work, option.Target, option.WorkGiver, "Harvest", 0, deadline)]) { OutputDelivery = output }]);
    }

    internal static PawnActivities Continue(PawnActivities batch, ActivitySelection<FoodActivityOption>? selection)
    {
        var order = batch.Orders[0];
        var output = order.OutputDelivery!;
        var option = selection?.Binding;
        var next = order with { ExpectedRevision = order.Revision, Revision = order.Revision + 1,
            Release = option is null, Steps = option is null ? [] : [new(PawnActivityKind.Deliver, option.Target, "", "",
                Math.Min(output.Count, option.Count), output.Deadline) { Destination = option.Destination }],
            OutputDelivery = option is null ? null : output with { Destination = option.Destination, AttemptsLeft = output.AttemptsLeft - 1 } };
        return new(batch.AuthorityGeneration, [next]);
    }
}
