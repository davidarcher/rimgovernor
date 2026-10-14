using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    // One bounded trip, at most three observed attempts, under the original acquisition horizon.
    // Native route costs are used as travel estimates; no distance-derived timing or output credit.
    public static PawnActivities? StartFoodActivity(FoodActivityRead read, ulong authority, string method, long deadline)
    {
        if (!read.Known || read.Context?.Tick is not { } tick || deadline <= tick) return null;
        var option = FoodOption(read, deadline);
        if (option is null) return null;
        var output = new ActivityOutputDelivery(option.Resource, option.Origin, option.Destination, option.Count, deadline, 3);
        return new(authority, [new(option.Pawn, method + "/" + option.Pawn, method, 0, 1, false,
            [new(PawnActivityKind.Work, option.Target, option.WorkGiver, "Harvest", 0, deadline)]) { OutputDelivery = output }]);
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
        var option = useful && !read.Carrying && output.AttemptsLeft > 0 && read.Context.Tick < output.Deadline
            && !(execution.State == "failed" && order.Steps[0].Kind == PawnActivityKind.Work)
            && !(execution.State == "completed" && order.Steps[0].Kind == PawnActivityKind.Deliver)
            ? FoodOption(read with { Options = EquatableArray.From(read.Options.Where(o => o.Pawn == order.Pawn && o.Resource == output.Resource)) }, output.Deadline) : null;
        var next = order with { ExpectedRevision = order.Revision, Revision = order.Revision + 1,
            Release = option is null, Steps = option is null ? [] : [new(PawnActivityKind.Deliver, option.Target, "", "",
                Math.Min(output.Count, option.Count), output.Deadline) { Destination = option.Destination }],
            OutputDelivery = option is null ? null : output with { Destination = option.Destination, AttemptsLeft = output.AttemptsLeft - 1 } };
        return new(batch.AuthorityGeneration, [next]);
    }

    private static FoodActivityOption? FoodOption(FoodActivityRead read, long deadline)
    {
        var horizon = deadline - read.Context!.Tick!.Value;
        if (horizon <= 0) return null;
        FoodActivityOption? best = null;
        var bestCost = double.PositiveInfinity;
        foreach (var option in read.Options.OrderBy(o => o.Pawn, StringComparer.Ordinal).ThenBy(o => o.Target, StringComparer.Ordinal)
            .ThenBy(o => o.Destination.X).ThenBy(o => o.Destination.Z).Take(128))
        {
            if (option.Count <= 0 || !double.IsFinite(option.TravelTicks) || option.TravelTicks < 0
                || !double.IsFinite(option.WorkTicks) || option.WorkTicks < 0) continue;
            // Use ColonyForecast's bounded allocator for this same-pawn trip. Equal unit labor rates,
            // no estimated injury on the native Danger.None route, and the acquisition horizon are
            // explicit local assumptions, not calibrated values for other colony obligations.
            var comparison = CompareActivityPlans(new()
            {
                Activities = [new("trip", "service", "done", [])],
                Pawns = [new("worker", "start", Known<EquatableArray<ForecastTimeWindow>>([new(0, horizon)]), Known(1.0))],
                Options = [new("trip", "worker", Known(option.WorkTicks), Known(0.0))],
                Routes = [new("worker", "start", "service", Known(option.TravelTicks))],
                Deadline = Known((double)horizon), AlternativeLaborCost = Known((double)horizon), ExpansionBudget = 16,
            });
            if (comparison.Selected is not { } plan || plan.LaborCost / option.Count >= bestCost) continue;
            best = option;
            bestCost = plan.LaborCost / option.Count;
        }
        return best;
    }
}
