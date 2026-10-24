using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.BuildingRuntime;

// Save and ReadSave: the writes that are methods of the client rather than of a writer class.
public sealed partial class FacadeNativeClient
{

    /// <summary>
    /// Requests one trusted checkpoint save. The request must carry a complete player
    /// identity, direction and request id and a save name; the expected tick is optional but, when set, is asserted
    /// before native persists anything. An uncertain outcome throws <see cref="FacadeSaveUncertainException"/>.
    /// </summary>
    internal async Task<PbLifecycle.SaveReply> SaveAsync(PbLifecycle.SaveRequest request, CancellationToken cancellationToken)
    {
        LifecycleSaveValidateRequest(request);
        var reply = await Facade.SaveGameAsync(request, cancellationToken).ConfigureAwait(false);
        LifecycleSaveInterpret(reply);
        return reply;
    }

    /// <summary>
    /// Re-reads a prior save request's exact outcome. It exists only to recover a
    /// reply the caller never received; an unknown or expired request id surfaces as a failure.
    /// </summary>
    internal async Task<PbLifecycle.SaveReply> ReadSaveAsync(string requestId, CancellationToken cancellationToken)
    {
        var reply = await Facade.PollSaveAsync(new PbLifecycle.RequestStatus { RequestId = requestId }, cancellationToken).ConfigureAwait(false);
        LifecycleSaveInterpret(reply);
        return reply;
    }

    /// <summary>Throws the typed error for an uncertain or failed save.</summary>
    private static void LifecycleSaveInterpret(PbLifecycle.SaveReply reply)
    {
        if (reply.Outcome.Uncertain is { } uncertain)
        {
            throw new FacadeSaveUncertainException(uncertain, NoReceipt);
        }
        if (reply.Outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }
    }

    private static void LifecycleSaveValidateRequest(PbLifecycle.SaveRequest? request)
    {
        if (request is null || request.Player is null)
        {
            throw BridgeContractException.Contract("save request requires a player lifecycle context");
        }
        var player = request.Player;
        if (player.PlayerDirection == 0)
        {
            throw BridgeContractException.Contract("save request requires a nonzero player direction");
        }
        if (request.ExpectedTick is PbLifecycle.ExpectedSaveTick.AtCase { Value: < 0 })
        {
            throw BridgeContractException.Contract("save request expected tick must be non-negative");
        }
    }
}
