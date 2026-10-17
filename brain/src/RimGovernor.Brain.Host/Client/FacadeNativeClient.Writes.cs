using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Host;

// Go authority.go (FocusMap) and lifecycle_save.go (Save,
// ReadSave): the writes that are methods of the client rather than of a writer class.
internal sealed partial class FacadeNativeClient
{

    /// <summary>
    /// Go <c>AuthorityControl.FocusMap</c>: changes only the viewed loaded map under the trusted control CAS. A
    /// typed refusal throws <see cref="NativeFailureException"/>; a facade call that throws
    /// surfaces as <see cref="FacadeAuthorityUncertainException"/>.
    /// </summary>
    internal async Task<PbAuthority.ControlReply> FocusMapAsync(PbAuthority.FocusMap request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        FacadeWriteChecks.WriteAuthorityRequest(request.Identity, request.ExpectedGeneration);
        BridgeFunctions.AuthorityIdentity(request.Target);
        if (!string.Equals(request.Identity!.ColonyId ?? "", request.Target!.ColonyId ?? "", StringComparison.Ordinal)
            || !string.Equals(request.Identity.LoadToken ?? "", request.Target.LoadToken ?? "", StringComparison.Ordinal))
        {
            throw BridgeFunctions.Contract("focus-map requires same colony and load");
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
    /// Go <c>LifecycleSave.Save</c>: requests one trusted checkpoint save. The request must carry a complete player
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
    /// Go <c>LifecycleSave.ReadSave</c>: re-reads a prior save request's exact outcome. It exists only to recover a
    /// reply the caller never received; an unknown or expired request id surfaces as a failure.
    /// </summary>
    internal async Task<PbLifecycle.SaveReply> ReadSaveAsync(string requestId, CancellationToken cancellationToken)
    {
        if (!BridgeFunctions.IsValidId(requestId ?? ""))
        {
            throw BridgeFunctions.Contract("read save requires a request id");
        }
        var reply = await Facade.PollSaveAsync(new PbLifecycle.RequestStatus { RequestId = requestId }, cancellationToken).ConfigureAwait(false);
        LifecycleSaveInterpret(reply);
        return reply;
    }

    /// <summary>Go <c>interpretSaveOutcome</c>: throws the typed error for an uncertain or failed save.</summary>
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

    /// <summary>Go <c>validateSaveRequest</c>.</summary>
    private static void LifecycleSaveValidateRequest(PbLifecycle.SaveRequest? request)
    {
        if (request is null || request.Player is null)
        {
            throw BridgeFunctions.Contract("save request requires a player lifecycle context");
        }
        var player = request.Player;
        if (player.RequestId is null || !BridgeFunctions.IsValidId(player.RequestId))
        {
            throw BridgeFunctions.Contract("save request requires a request id");
        }
        if (player.PlayerDirection is null || player.PlayerDirection.GetValueOrDefault() == 0)
        {
            throw BridgeFunctions.Contract("save request requires a nonzero player direction");
        }
        if (request.SaveName is null || !BridgeFunctions.IsValidId(request.SaveName))
        {
            throw BridgeFunctions.Contract("save request requires a save name");
        }
        if (request.ExpectedTick is not null && request.ExpectedTick.GetValueOrDefault() < 0)
        {
            throw BridgeFunctions.Contract("save request expected tick must be non-negative");
        }
    }
}
