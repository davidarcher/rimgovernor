using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy.Methods.Binding;

internal static class FoodActivityBinding
{
    internal static PawnActivities? Start(ForecastActivityOutcomeComparison? selection, IReadOnlyDictionary<string, FoodActivityOption> bindings, ulong authority, string method, long deadline)
    {
        var option = Selected(selection, bindings);
        if (option is null) return null;
        var output = new ActivityOutputDelivery(option.Resource, option.Origin, option.Destination, option.Count, deadline, 3);
        return new(authority, [new(option.Pawn, method + "/" + option.Pawn, method, 0, 1, false,
            [new(PawnActivityKind.Work, option.Target, option.WorkGiver, "Harvest", 0, deadline)]) { OutputDelivery = output }]);
    }

    internal static PawnActivities Continue(PawnActivities batch, ForecastActivityOutcomeComparison? selection, IReadOnlyDictionary<string, FoodActivityOption> bindings)
    {
        var order = batch.Orders[0];
        var output = order.OutputDelivery!;
        var option = Selected(selection, bindings);
        if (option?.Cooking is { } cooking)
        {
            var continuation = cooking.Continuation;
            var step = cooking.Supplied
                ? new PawnActivityStep(PawnActivityKind.Cook, continuation.Bench, continuation.WorkGiver, "", 0, output.Deadline)
                    { Cooking = new(continuation.Bill, continuation.Recipe, [new(option.Target, cooking.IngredientCount)]) }
                : new PawnActivityStep(PawnActivityKind.Deliver, option.Target, "", "", cooking.IngredientCount, output.Deadline)
                    { Destination = option.Destination };
            var remaining = cooking.Supplied
                ? new ActivityOutputDelivery(continuation.Product, continuation.Origin, continuation.Destination, continuation.ProductCount, output.Deadline, output.AttemptsLeft)
                : output with { Count = cooking.IngredientCount, Destination = option.Destination, Cooking = continuation };
            return new(batch.AuthorityGeneration, [order with { ExpectedRevision = order.Revision, Revision = order.Revision + 1,
                Release = false, Steps = [step], OutputDelivery = remaining }]);
        }
        var next = order with { ExpectedRevision = order.Revision, Revision = order.Revision + 1,
            Release = option is null, Steps = option is null ? [] : [new(PawnActivityKind.Deliver, option.Target, "", "",
                Math.Min(output.Count, option.Count), output.Deadline) { Destination = option.Destination }],
            OutputDelivery = option is null ? null : output with { Destination = option.Destination, AttemptsLeft = output.AttemptsLeft - 1 } };
        return new(batch.AuthorityGeneration, [next]);
    }
    private static FoodActivityOption? Selected(ForecastActivityOutcomeComparison? selection, IReadOnlyDictionary<string, FoodActivityOption> bindings)
    {
        if (selection?.Plan is null || selection.Selected.Length != 1) return null;
        var id = selection.Selected[0];
        return selection.Plan.Allocations.Any(a => a.Activity == id) && bindings.TryGetValue(id, out var option) ? option : null;
    }
}
