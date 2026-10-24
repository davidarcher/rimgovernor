namespace RimGovernor.Brain.BuildingRuntime;

// The native clock boundary of a session: Run, Hold and Heartbeat writes and the status read. A native refusal is a
// NativeFailureException; a Run or Hold the clock answered is its validated status.

/// <summary>The native clock reads the session makes.</summary>
public interface IClockNative
{
    Task<PbLifecycle.LoadedIdentity> IdentityAsync(CancellationToken ct);
}

/// <summary>The native clock writes the session makes.</summary>
public interface IClockWriter
{
    /// <summary>
    /// Runs the clock at the request's speed under its watch policy: resumes a held clock or changes speed and
    /// policy in place. A refusal (a stale generation, a game-forced pause) is a <see cref="NativeFailureException"/>.
    /// </summary>
    Task<PbClock.Status> RunAsync(PbClock.RunRequest request, CancellationToken ct);

    /// <summary>Holds the clock for a brain reason; a clock already held keeps its first reason.</summary>
    Task<PbClock.Status> HoldAsync(PbClock.HoldRequest request, CancellationToken ct);

    /// <summary>The brain's contact for the clock ceiling's heartbeat.</summary>
    Task<PbClock.HeartbeatAck> HeartbeatAsync(PbClock.HeartbeatRequest request, CancellationToken ct);
}

/// <summary>
/// Gates the complete clock boundary. Constructing a session never resumes time: only the clock driver's Run does.
/// </summary>
public sealed record ClockCapabilities(IClockNative Native, IClockWriter Writer);
