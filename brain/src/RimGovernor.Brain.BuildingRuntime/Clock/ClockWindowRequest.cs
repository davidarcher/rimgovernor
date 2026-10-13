namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// Carries fresh policy facts through the serialized command (Go <c>ClockWindowRequest</c>).
/// <para>
/// <see cref="Status"/> is the validated owned clock status the admitting step read after its planners (the second
/// bundle) and <see cref="StatusAt"/> when it was read. When both are set and <see cref="StatusAt"/> is within
/// <see cref="MaxAge"/> of dispatch, the coordinator's pre-dispatch inspection checks that status instead of
/// reading the clock status again: the bundle was the step's last native round trip and every call to the game
/// host is serial, so a second read would answer with the same paused tick a few tens of milliseconds later. A
/// null status, or one older than <see cref="MaxAge"/>, keeps the native read.
/// </para>
/// </summary>
public sealed record ClockWindowRequest
{
    public required ClockIntent Intent { get; init; }

    public required ClockWindowFacts Facts { get; init; }

    public TimeSpan MaxAge { get; init; }

    public uint CombatMaxTicks { get; init; }

    public PbClock.Status? Status { get; init; }

    public DateTimeOffset StatusAt { get; init; }
}
