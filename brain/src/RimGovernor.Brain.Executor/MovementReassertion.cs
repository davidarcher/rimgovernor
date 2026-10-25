using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Executor;

public sealed partial class Executor
{
    /// <summary>Reissues an accepted move whose pawn is idle before its dependent work. Every resend has a new journaled attempt.</summary>
    public async Task<BatchItem> ReassertMovementAsync(PlanId plan, ActionId id, Tick observed, CancellationToken ct)
    {
        var result = new ExecutorResult();
        Exception? error = null;
        try
        {
            await WithPlanAsync(plan, [id], async (state, authority, generation, run) =>
            {
                var (action, progress) = Find(state, id);
                if (_movement is null || action?.Movement is null || progress is null
                    || progress.View.Stage != Stage.Completed || progress.View.Unresolved)
                    throw new EvidenceException();
                result = result with { Progress = progress };
                var expected = authority.Snapshot with { Plan = plan, Revision = state.Spec.Revision };
                await GuardAsync(expected, generation, run).ConfigureAwait(false);
                var tick = progress.View.Tick > observed ? progress.View.Tick : observed;
                var next = await _journal.PrepareAsync(plan, id, expected, tick, run).ConfigureAwait(false);
                result = result with { Progress = next };
                (result, error) = await RunMovementAsync(action, next, authority, generation, run).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            error = failure;
        }
        return new(id, result, error);
    }
}
