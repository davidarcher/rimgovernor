namespace RimGovernor.Brain.BuildingRuntime;

// Port of the interfaces and configuration of internal/buildingruntime/clock_coordinator.go and the
// ClockCapabilities of clock_session.go. Go's (reply, bridge.Result, error) returns become the reply, with a
// native refusal an exception.

/// <summary>The native clock reads the coordinator makes (Go <c>ClockNative</c>).</summary>
public interface IClockNative
{
    Task<PbLifecycle.IdentityReply> IdentityAsync(CancellationToken ct);

    Task<PbClock.StatusReply> ReadClockStatusAsync(PbCommon.Identity identity, CancellationToken ct);

    Task<PbClock.AttemptReply> ReadClockAttemptAsync(PbClock.AttemptRequest request, CancellationToken ct);
}

/// <summary>The native clock writes the coordinator makes (Go <c>ClockWriter</c>).</summary>
public interface IClockWriter
{
    Task<PbClock.StatusReply> OwnedPauseAsync(PbClock.OwnedRequest request, CancellationToken ct);

    Task<PbClock.ControlReply> StartAsync(PbClock.StartRequest request, CancellationToken ct);

    Task<PbClock.ControlReply> RenewAsync(PbClock.RenewRequest request, PbClock.Epoch original, CancellationToken ct);

    Task<PbClock.ControlReply> ChangeSpeedAsync(PbClock.SpeedRequest request, PbClock.Epoch original, CancellationToken ct);
}

/// <summary>
/// Gates the complete owned clock boundary (Go <c>ClockCapabilities</c>). Constructing a session never resumes time
/// or recovers a live lease from durable evidence.
/// </summary>
public sealed record ClockCapabilities(IClockNative Native, IClockWriter Writer);

/// <summary>The bounds of one coordinator call and of one of its journal writes (Go <c>ClockCoordinatorConfig</c>).</summary>
public sealed record ClockCoordinatorConfig
{
    public TimeSpan CallTimeout { get; init; }

    public TimeSpan JournalTimeout { get; init; }
}
