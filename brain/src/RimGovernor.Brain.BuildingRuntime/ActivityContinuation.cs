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
            if (method.Batch.Tasks.Count != 0 && _config.Pawns is IActivityTaskSource taskSource)
            {
                var read = await taskSource.ReadActivityTasksAsync(new(Boundary.Identity(current), method.Batch), ct).ConfigureAwait(false);
                if (!read.Known || read.Context?.Tick is not { } taskTick) continue;
                _ = Boundary.Context(read.Context, current);
                var participants = read.Pawns.Select(p => p.Execution is { } e
                    ? new ActivityOwnership(p.Pawn, e.Assignment, e.Method, e.Revision, e.State)
                    : new ActivityOwnership(p.Pawn, "", "", 0, "unowned")).ToArray();
                if (PolicyFunctions.ReviewActivityOwnership(method.Batch, method.Needed, taskTick, participants).OwnershipEnded)
                {
                    _player.Journal.ResolveActivityMethod(method.Plan, current, new Tick(taskTick), participants, ct);
                    _advanced = true;
                    continue;
                }
                var needed = PolicyFunctions.ReviewProductiveActivityNeed(method.Batch, method.Needed, read);
                var next = PolicyFunctions.ContinueCoordinatedActivity(method.Batch, needed, read);
                if (method.Batch.Orders.Any(o => o.Method.StartsWith("coupled-", StringComparison.Ordinal)))
                    Tel.Decide(new Decision { Kind = "coupled_observed", Component = "worker", Target = method.Method.Value,
                        Verdict = next is null ? "continue" : "transition", Reason = "native_task_evidence",
                        Attrs = new Dictionary<string, object?> { ["observed_tick"] = taskTick,
                            ["satisfied_tasks"] = read.SatisfiedTasks.ToArray(),
                            ["remaining_tasks"] = method.Batch.Tasks.Select(t => t.Id).ToArray(),
                            ["native_states"] = participants.Select(p => p.Pawn + ":" + p.State).ToArray() } });
                if (next is not null)
                {
                    var id = PlanIds.MintPlanId();
                    _player.Journal.ContinueActivityMethod(method.Plan, current, new Tick(taskTick), participants,
                        new PlanSpec(id, new PlanRevision(1), [DomainAction.NewPawnActivitiesAction(new ActionId($"{id}-0"), next)]), ct);
                    _advanced = true;
                }
                continue;
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
