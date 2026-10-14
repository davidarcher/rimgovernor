namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class Worker
{
    /// <summary>Existing Method cleanup runs independently of new-work Concern eligibility. Reads and
    /// journal transitions only: ordinary worker/Hands dispatch performs the resulting release.</summary>
    private async Task ContinueActivityMethodsAsync(GenerationSnapshot current, CancellationToken ct)
    {
        var methods = _player.Journal.ActivityMethods(current, ct);
        if (methods.Count == 0 || _config.Pawns is null) return;
        var ids = methods.SelectMany(m => m.Batch.Orders.Select(o => o.Pawn)).Distinct(StringComparer.Ordinal).ToArray();
        var (rows, tick) = await ArrivalRowsAsync(current, ids, ct).ConfigureAwait(false);
        var ownership = rows.Select(pair => pair.Value.ActivityExecution is { } e
            ? new ActivityOwnership(pair.Key, e.Assignment, e.Method, e.Revision, e.State)
            : new ActivityOwnership(pair.Key, "", "", 0, "unowned")).ToArray();
        foreach (var method in methods)
        {
            if (method.Batch.Orders.Length == 1 && method.Batch.Orders[0].OutputDelivery is not null
                && _config.Pawns is IFoodActivitySource foodSource)
            {
                var food = await foodSource.ReadFoodActivityAsync(new(Boundary.Identity(current), [], method.Batch.Orders[0]), ct).ConfigureAwait(false);
                if (!food.Known || food.Context?.Tick is not { } foodTick) continue;
                _ = Boundary.Context(food.Context, current);
                var next = PolicyFunctions.ContinueFoodActivity(method.Batch, method.Needed, food, out _,
                    _player.Journal.WorkResources(current, ct, method.Plan));
                if (next is not null && food.Execution is { } execution)
                {
                    var id = PlanIds.MintPlanId();
                    var order = method.Batch.Orders[0];
                    _player.Journal.ContinueActivityMethod(method.Plan, current, new Tick(foodTick),
                        [new(order.Pawn, execution.Assignment, execution.Method, execution.Revision, execution.State)],
                        new PlanSpec(id, new PlanRevision(1), [DomainAction.NewPawnActivitiesAction(new ActionId($"{id}-0"), next)]), ct);
                    _advanced = true;
                    continue;
                }
            }
            var decision = PolicyFunctions.ReviewActivityOwnership(method.Batch, method.Needed, tick, ownership);
            if (decision.OwnershipEnded)
            {
                _player.Journal.ResolveActivityMethod(method.Plan, current, new Tick(tick), ownership, ct);
                _advanced = true;
            }
            else if (decision.Release is { } release)
            {
                var id = PlanIds.MintPlanId();
                var action = DomainAction.NewPawnActivitiesAction(new ActionId($"{id}-0"), release);
                _player.Journal.ContinueActivityMethod(method.Plan, current, new Tick(tick), ownership,
                    new PlanSpec(id, new PlanRevision(1), [action]), ct);
                _advanced = true;
            }
        }
    }
}
