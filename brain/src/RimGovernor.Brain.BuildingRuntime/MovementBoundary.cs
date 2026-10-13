using Receipt = RimGovernor.Brain.Executor.Receipt;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Sends move intents through Actions/Apply; native validates each against live state (alive, spawned, drafted, reachable) and treats an order that already matches as applied.</summary>
public sealed record MovementCapabilities(IActionsWriter Writer);

internal sealed class MovementBoundary(IActionsWriter writer) : IMovementBoundary
{
    public Task<Receipt> WriteMovementAsync(Placement placement, CancellationToken ct) =>
        Boundary.DispatchIntentAsync(placement, writer, ct);
}
