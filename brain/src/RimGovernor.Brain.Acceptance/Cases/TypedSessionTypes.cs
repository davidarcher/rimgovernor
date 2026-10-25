
namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// One round of <see cref="TypedSession.RunClockUntilHeldAsync(string, int, ClockRunPlan, PlannerRound, CancellationToken)"/>
/// while the clock runs, given that round's status; true ends the wait without a hold.
/// </summary>
public delegate Task<bool> PlannerRound(RimGovernor.Brain.GameFacts.Clock.Status running, CancellationToken cancellationToken);

/// <summary>A harness run's policy before the current clock supplies its hold serial and review tick.</summary>
public sealed record ClockRunPlan
{
    public required ulong ExpectedGeneration { get; init; }
    public required RimGovernor.Brain.GameFacts.Clock.Speed Speed { get; init; }
    public required RimGovernor.Brain.GameOrders.Clock.WatchPolicy Policy { get; init; }
}

/// <summary>A new-colony start's terminal outcome (<see cref="TypedSession.NewColonyAsync"/>).</summary>
public sealed record NewColonyResult
{
    /// <summary>The phases its pending replies reported, in wire order and each once.</summary>
    public IReadOnlyList<RimGovernor.Brain.GameFacts.Lifecycle.NewColonyPhase> Phases { get; init; } = [];

    /// <summary>The completed reply, null when the start failed.</summary>
    public RimGovernor.Brain.GameFacts.Lifecycle.NewColonyCompleted? Completed { get; init; }

    /// <summary>The failure the start ended with, null when it completed.</summary>
    public RimGovernor.Brain.GameFacts.Common.Failure? Failure { get; init; }
}
