using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Bridge;

namespace RimGovernor.Brain.Runtime;

/// <summary>
/// The clock's mutation capability over the facade, separate from the clock reads: Run, Hold and Heartbeat. Run and
/// Hold are idempotent and carry no attempt key, so a call that fails is simply made again at the next decision.
/// A typed refusal throws <see cref="NativeFailureException"/>; a status the clock could not read completely throws
/// <see cref="NativeUnavailableException"/>.
/// </summary>
internal sealed class FacadeClockWriter(IGameApi facade) : IClockWriter
{
    private readonly IGameApi _facade = facade ?? throw new ArgumentNullException(nameof(facade));

    /// <inheritdoc />
    public async Task<RimGovernor.Brain.GameFacts.Clock.Status> RunAsync(RimGovernor.Brain.GameOrders.Clock.RunRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ExpectedGeneration == 0)
        {
            throw BridgeContractException.Contract("clock run authority generation");
        }
        if (!Enum.IsDefined(request.Speed))
        {
            throw BridgeContractException.Contract("clock run speed");
        }
        ClockValidation.ValidateClockPolicy(request.Policy);
        return await _facade.RunClockAsync(request, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RimGovernor.Brain.GameFacts.Clock.Status> HoldAsync(RimGovernor.Brain.GameOrders.Clock.HoldRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await _facade.HoldClockAsync(request, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RimGovernor.Brain.GameFacts.Clock.HeartbeatAck> HeartbeatAsync(RimGovernor.Brain.GameOrders.Clock.HeartbeatRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await _facade.HeartbeatClockAsync(request, ct).ConfigureAwait(false);
    }
}
