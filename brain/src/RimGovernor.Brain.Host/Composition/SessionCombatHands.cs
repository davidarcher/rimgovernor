using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Executor;

namespace RimGovernor.Brain.Host;

/// <summary>
/// The combat planner's executor hands over the session (Go passed the <c>*Session</c> itself, whose <c>RunBatch</c>
/// satisfied <c>combatHands</c>). The C# session dispatches asynchronously while the planner's hands are synchronous,
/// so this waits on the dispatch; the planner runs on a scheduler-wave thread, not on a context that would deadlock.
/// </summary>
internal sealed class SessionCombatHands(Session session) : ICombatHands
{
    /// <inheritdoc />
    public IReadOnlyList<BatchItem> RunBatch(PlanId plan, IReadOnlyList<ActionId> actions, CancellationToken ct) =>
        session.RunBatchAsync(plan, actions, ct).GetAwaiter().GetResult();
}
