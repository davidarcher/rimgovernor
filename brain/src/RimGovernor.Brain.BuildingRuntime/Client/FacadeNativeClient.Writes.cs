using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.BuildingRuntime;

// Save and ReadSave: the writes that are methods of the client rather than of a writer class.
public sealed partial class FacadeNativeClient
{

    /// <summary>
    /// Changes only the viewed loaded map under the trusted control CAS. A
    /// typed refusal throws <see cref="NativeFailureException"/>; a facade call that throws
    /// surfaces as <see cref="FacadeAuthorityUncertainException"/>.
    /// </summary>
    internal async Task<PbAuthority.ControlReply> FocusMapAsync(PbAuthority.FocusMap request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        FacadeWriteChecks.WriteAuthorityRequest(request.Identity, request.ExpectedGeneration);
        WireValidation.AuthorityIdentity(request.Target);
        if (!string.Equals(request.Identity!.ColonyId ?? "", request.Target!.ColonyId ?? "", StringComparison.Ordinal)
            || !string.Equals(request.Identity.LoadToken ?? "", request.Target.LoadToken ?? "", StringComparison.Ordinal))
        {
            throw BridgeContractException.Contract("focus-map requires same colony and load");
        }
        cancellationToken.ThrowIfCancellationRequested();
        PbAuthority.ControlReply reply;
        try
        {
            reply = await Facade.ControlAuthorityAsync(new PbAuthority.ControlRequest { Operation = request }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new FacadeAuthorityUncertainException(e, NoReceipt);
        }
        if (reply.Outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }
        return reply;
    }

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
        if (player.PlayerDirection is null || player.PlayerDirection.GetValueOrDefault() == 0)
        {
            throw BridgeContractException.Contract("save request requires a nonzero player direction");
        }
        if (request.ExpectedTick is not null && request.ExpectedTick.GetValueOrDefault() < 0)
        {
            throw BridgeContractException.Contract("save request expected tick must be non-negative");
        }
    }
}
