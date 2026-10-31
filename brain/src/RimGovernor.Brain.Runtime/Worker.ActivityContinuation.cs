using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;

namespace RimGovernor.Brain.Runtime;

public sealed partial class Worker
{
    /// <summary>Existing Method cleanup runs independently of new-work Need eligibility. Reads and
    /// journal transitions only: ordinary worker/Hands dispatch performs the resulting release.</summary>
    private async Task ContinueActivityMethodsAsync(GenerationSnapshot current, CancellationToken ct)
    {
        var methods = _player.Journal.ActivityMethods(current, ct);
        if (methods.Count == 0 || _config.Pawns is null) return;
        var ids = methods.SelectMany(m => m.Batch.Orders.Select(o => o.Pawn)).Distinct(StringComparer.Ordinal).ToArray();
        var (rows, tick) = await ActionObservations.ReadPawnsAsync(_config.Pawns, current, ids, ct).ConfigureAwait(false);
        var ownership = rows.Select(pair => pair.Value.ActivityExecution is { } e
            ? new ActivityOwnership(pair.Key, e.Assignment, e.Method, e.Revision, e.State) { Reason = e.Reason }
            : new ActivityOwnership(pair.Key, "", "", 0, "unowned")).ToArray();
        foreach (var method in methods)
        {
            if (method.Batch.Tasks.Count != 0 && _config.Pawns is IActivityTaskSource taskSource)
            {
                var read = await taskSource.ReadActivityTasksAsync(new(Boundary.Identity(current), method.Batch), ct).ConfigureAwait(false);
                var taskTick = read.Context.Tick;
                _ = Boundary.Context(read.Context, current);
                _player.ForecastEvidence.Tasks(current, method.Batch, read);
                var participants = read.Pawns.Select(p => p.Execution is { } e
                    ? new ActivityOwnership(p.Pawn, e.Assignment, e.Method, e.Revision, e.State) { Reason = e.Reason }
                    : new ActivityOwnership(p.Pawn, "", "", 0, "unowned")).ToArray();
                if (ActivityContinuationRules.ReviewActivityOwnership(method.Batch, method.Needed, taskTick, participants).OwnershipEnded)
                {
                    _player.Journal.ResolveActivityMethod(method.Plan, current, new Tick(taskTick), participants, ct);
                    _advanced = true;
                    continue;
                }
                var needed = ForecastServiceInvestments.ReviewServiceActivityNeed(method.Batch, method.Needed, read);
                var purpose = InterventionPurposes.Ended(method.Batch, p => InterventionPurposes.AtContinuation(p, method.Batch, read.Needs));
                var releaseReason = purpose == PurposeCondition.Open ? "" : InterventionPurposes.PurposeEndedReason;
                var next = CoordinatedActivitiesRules.ContinueCoordinatedActivity(method.Batch, needed, read, r => releaseReason = r);
                ActivityContinuationAdmission? admission = null;
                if (next is not null)
                {
                    var id = PlanIds.MintPlanId();
                    admission = _player.Journal.ContinueActivityMethod(method.Plan, current, new Tick(taskTick), participants,
                        new PlanSpec(id, new PlanRevision(1), [DomainAction.NewPawnActivitiesAction(new ActionId($"{id}-0"), next)]), ct);
                    _advanced |= admission == ActivityContinuationAdmission.Admitted;
                }
                if (method.Batch.Orders.Any(PawnActivities.Intervention))
                    Tel.Decide(new Decision { Kind = "coupled_observed", Component = "worker", Target = method.Method.Value,
                        Verdict = admission == ActivityContinuationAdmission.ResourcesUnavailable ? "deferred"
                            : admission == ActivityContinuationAdmission.Admitted ? "transition" : "continue",
                        Reason = admission == ActivityContinuationAdmission.ResourcesUnavailable
                            ? "work_resource_conflict_or_unknown" : "native_task_evidence",
                        Attrs = new Dictionary<string, object?> { ["observed_tick"] = taskTick,
                            ["satisfied_tasks"] = read.SatisfiedTasks.ToArray(),
                            ["remaining_tasks"] = method.Batch.Tasks.Select(t => t.Id).ToArray(),
                            ["native_states"] = participants.Select(p => p.Pawn + ":" + p.State).ToArray(),
                            ["observed_outcomes"] = InterventionPurposes.Outcomes(method.Batch, participants),
                            ["purposes"] = InterventionPurposes.Purposes(method.Batch).Select(p => p.Aim).ToArray(),
                            ["purpose_condition"] = Wire.Name(purpose),
                            ["release_reason"] = releaseReason } });
                continue;
            }
            var decision = ActivityContinuationRules.ReviewActivityOwnership(method.Batch, method.Needed, tick, ownership);
            if (decision.OwnershipEnded)
            {
                _player.Journal.ResolveActivityMethod(method.Plan, current, new Tick(tick), ownership, ct);
                _advanced = true;
            }
            else if (decision.Release is { } release)
            {
                var id = PlanIds.MintPlanId();
                var action = DomainAction.NewPawnActivitiesAction(new ActionId($"{id}-0"), release);
                var admission = _player.Journal.ContinueActivityMethod(method.Plan, current, new Tick(tick), ownership,
                    new PlanSpec(id, new PlanRevision(1), [action]), ct);
                _advanced |= admission == ActivityContinuationAdmission.Admitted;
            }
        }
    }
}
