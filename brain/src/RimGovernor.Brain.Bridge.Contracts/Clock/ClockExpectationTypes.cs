using RimGovernor.Protocol.Clock;
using RimGovernor.Protocol.Common;

namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>
/// Go's <c>ClockCommand</c>: exactly one immutable clock intent (one arm set). Requested lease durations are
/// command arguments, never restored permission; live lease tokens are absent.
/// </summary>
public sealed record ClockCommand(ClockStart? Start = null, ClockRenew? Renew = null, ClockSpeed? Speed = null);

/// <summary>Go's <c>ClockStart</c>.</summary>
/// <param name="Speed">The requested epoch speed.</param>
/// <param name="Policy">The watch policy, nullable only for a malformed request that validation rejects.</param>
/// <param name="LeaseMs">The requested lease duration in milliseconds.</param>
/// <param name="MaxTicks">The tick budget.</param>
/// <param name="TestAcceleration">
/// Asks for the native tick boost; it requires SPEED_ULTRAFAST and a game launched with test acceleration
/// (headless acceptance profiles only), which native refuses otherwise.
/// </param>
/// <param name="BlindTickBudget">
/// Arms the native blind-tick regulator: past this many ticks since the controller's last read or oldest
/// unacknowledged journal row, native throttles the epoch toward Normal and ramps back once the controller
/// catches up, journaling both as SpeedChanged. Zero leaves the epoch unregulated.
/// </param>
/// <param name="MaxTicksPerSecond">A continuous ceiling under the speed's own rate; zero is none.</param>
/// <param name="PlayerAccelerated">
/// Asks for player acceleration: an Ultrafast epoch, without test acceleration, whose ticks per frame native
/// adapts to its frame budget (30 ms) toward the boosted rate. Any launch admits it.
/// </param>
public sealed record ClockStart(
    Speed Speed,
    WatchPolicy? Policy,
    uint LeaseMs,
    uint MaxTicks,
    bool TestAcceleration = false,
    uint BlindTickBudget = 0,
    uint MaxTicksPerSecond = 0,
    bool PlayerAccelerated = false)
{
    /// <summary>The start's pacing as StartRequest carries it: absent (null) for fixed pacing.</summary>
    public Pacing? WirePacing => PlayerAccelerated ? Pacing.PlayerAccelerated : null;
}

/// <summary>Go's <c>ClockRenew</c>.</summary>
public sealed record ClockRenew(Epoch? Original, uint LeaseMs);

/// <summary>Go's <c>ClockSpeed</c>; <paramref name="MaxTicksPerSecond"/> is the ceiling the change carries, null keeps the epoch's.</summary>
public sealed record ClockSpeed(Epoch? Original, Speed Speed, uint? MaxTicksPerSecond = null);

/// <summary>
/// Go's <c>ClockExpectation</c>: retains original admission facts for receipt recovery. It cannot authorize a
/// call: the current runtime must supply a fresh live lease.
/// </summary>
public sealed record ClockExpectation(Identity? Identity, AttemptKey? Attempt, ulong NativeGeneration, ClockCommand Command);

/// <summary>Go's <c>ClockEpochState</c>: what fresh cleanup evidence says about an epoch.</summary>
public enum ClockEpochState
{
    [WireName("")]
    None = 0,
    Required,
    Uncertain,
    Paused,
    Retired,
    Superseded,
}
