using Clock = RimGovernor.Contracts.Wire.Clock;
using Common = RimGovernor.Contracts.Wire.Common;
using Lifecycle = RimGovernor.Contracts.Wire.Lifecycle;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// One round of <see cref="TypedSession.RunClockUntilHeldAsync(string, int, ClockRunPlan, ClockRound, CancellationToken)"/>
/// while the clock runs, given that round's status; true ends the wait without a hold.
/// </summary>
public delegate Task<bool> ClockRound(Clock.Status running, CancellationToken cancellationToken);

/// <summary>A harness run's policy before the current clock supplies its hold serial and review tick.</summary>
public sealed record ClockRunPlan
{
    public required ulong ExpectedGeneration { get; init; }
    public required Clock.Speed Speed { get; init; }
    public required Clock.WatchPolicy Policy { get; init; }
}

/// <summary>A new-colony start's terminal outcome (<see cref="TypedSession.NewColonyAsync"/>).</summary>
public sealed record NewColonyResult
{
    /// <summary>The phases its pending replies reported, in wire order and each once.</summary>
    public IReadOnlyList<Lifecycle.NewColonyPhase> Phases { get; init; } = [];

    /// <summary>The completed reply, null when the start failed.</summary>
    public Lifecycle.NewColonyCompleted? Completed { get; init; }

    /// <summary>The failure the start ended with, null when it completed.</summary>
    public Common.Failure? Failure { get; init; }
}
