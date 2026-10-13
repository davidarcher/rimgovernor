namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsDefensePlanner
{
    /// <summary>
    /// Retires the incident's combat batches the observed combat view shows done: a single-action plan whose batch
    /// the view confirms (<see cref="CombatBatchObserved"/>) records a completed observation and retires.
    /// </summary>
    internal async Task ReconcileCombatBatchesAsync(ControlState state, IncidentState incident, CombatView view, CancellationToken ct)
    {
        var journal = _reviewer.Player.Journal;
        foreach (var method in incident.Methods)
        {
            var plan = await journal.LoadPlanAsync(method.Plan, ct).ConfigureAwait(false);
            if (plan.Retired || plan.Progress.Count != 1)
            {
                continue;
            }
            var p = plan.Progress[0];
            if (p.Action.CombatBatch is not { } batch || !p.View.Unresolved || view.Tick <= p.View.Tick || !PolicyFunctions.CombatBatchObserved(batch, view))
            {
                continue;
            }
            var current = state.Snapshot with { Plan = plan.Spec.Id, Revision = plan.Spec.Revision };
            var observation = new Domain.Observation(p.Action.Id, p.View.Attempt, current, view.Tick, Effect.Completed);
            await journal.ObserveAsync(plan.Spec.Id, observation, current, ct).ConfigureAwait(false);
            journal.RetireCombatBatch(plan.Spec.Id, ct);
        }
    }
}
