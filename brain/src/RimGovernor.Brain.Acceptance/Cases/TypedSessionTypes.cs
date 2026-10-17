using Clock = RimGovernor.Contracts.Wire.Clock;
using Common = RimGovernor.Contracts.Wire.Common;
using Lifecycle = RimGovernor.Contracts.Wire.Lifecycle;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// One round of <see cref="TypedSession.RunClockUntilHeldAsync(string, int, Clock.RunRequest, ClockRound, CancellationToken)"/>
/// while the clock runs, given that round's status; true ends the wait without a hold.
/// </summary>
public delegate Task<bool> ClockRound(Clock.Status running, CancellationToken cancellationToken);

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
