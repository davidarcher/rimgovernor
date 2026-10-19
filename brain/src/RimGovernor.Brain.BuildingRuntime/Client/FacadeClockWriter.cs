using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Bridge;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The clock's mutation capability over the facade, separate from the clock reads: Run, Hold and Heartbeat. Run and
/// Hold are idempotent and carry no attempt key, so a call that fails is simply made again at the next decision.
/// A typed refusal throws <see cref="NativeFailureException"/>; a status the clock could not read completely throws
/// <see cref="NativeUnavailableException"/>.
/// </summary>
internal sealed class FacadeClockWriter(INativeFacade facade) : IClockWriter
{
    private readonly INativeFacade _facade = facade ?? throw new ArgumentNullException(nameof(facade));

    /// <inheritdoc />
    public async Task<PbClock.Status> RunAsync(PbClock.RunRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ExpectedGeneration is null || request.ExpectedGeneration.GetValueOrDefault() == 0)
        {
            throw BridgeContractException.Contract("clock run authority generation");
        }
        if (request.Speed is not { } speed || speed == PbClock.Speed.Unspecified || !Enum.IsDefined(speed))
        {
            throw BridgeContractException.Contract("clock run speed");
        }
        if (request.PlannedHoldSerial is null)
        {
            throw BridgeContractException.Contract("clock run planned hold serial");
        }
        ClockValidation.ValidateClockPolicy(request.Policy);
        var reply = await _facade.RunClockAsync(request, ct).ConfigureAwait(false);
        return FacadeWriteChecks.ClockStatusReply(reply);
    }

    /// <inheritdoc />
    public async Task<PbClock.Status> HoldAsync(PbClock.HoldRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Reason is null)
        {
            throw BridgeContractException.Contract("clock hold reason");
        }
        var reply = await _facade.HoldClockAsync(request, ct).ConfigureAwait(false);
        return FacadeWriteChecks.ClockStatusReply(reply);
    }

    /// <inheritdoc />
    public async Task<PbClock.HeartbeatAck> HeartbeatAsync(PbClock.HeartbeatRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reply = await _facade.HeartbeatClockAsync(request, ct).ConfigureAwait(false);
        if (reply.Outcome.Failure is { } failure)
        {
            throw FacadeNativeClient.Failure(failure);
        }
        return reply.Outcome.Ack!;
    }
}
