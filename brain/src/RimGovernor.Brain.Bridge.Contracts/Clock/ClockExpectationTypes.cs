using RimGovernor.Contracts.Wire.Clock;
using RimGovernor.Contracts.Wire.Common;

namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>
/// Go's <c>ClockCommand</c>: exactly one immutable clock intent (one arm set).
/// </summary>
public sealed record ClockCommand(ClockStart? Start = null, ClockSpeed? Speed = null);

/// <summary>Go's <c>ClockStart</c>.</summary>
/// <param name="Speed">The requested epoch speed.</param>
/// <param name="Policy">The watch policy, nullable only for a malformed request that validation rejects.</param>
/// <param name="MaxTicks">The tick budget.</param>
/// <param name="TestAcceleration">
/// Asks for the native tick boost; it requires SPEED_ULTRAFAST and a game launched with test acceleration
/// (headless acceptance profiles only), which native refuses otherwise.
/// </param>
public sealed record ClockStart(
    Speed Speed,
    WatchPolicy? Policy,
    uint MaxTicks,
    bool TestAcceleration = false);

/// <summary>Go's <c>ClockSpeed</c>.</summary>
public sealed record ClockSpeed(Epoch? Original, Speed Speed);

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
