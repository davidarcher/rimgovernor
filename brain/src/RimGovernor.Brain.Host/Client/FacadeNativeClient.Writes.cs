using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Host;

// Go rules.go (AttachReactions, ClearReactions, ReadReactions), authority.go (FocusMap) and lifecycle_save.go (Save,
// ReadSave): the writes that are methods of the client rather than of a writer class.
internal sealed partial class FacadeNativeClient
{
    /// <summary>
    /// Go <c>Client.AttachReactions</c>: replaces every active native rule with the accepted ones and sets the lease
    /// to <paramref name="expiresAtTick"/> (an absolute game tick). Refused rules come back on the reply with their reasons.
    /// </summary>
    internal async Task<(PbOperations.RulesAttached Value, BridgeResult Receipt)> AttachReactionsAsync(
        PbCommon.Identity identity,
        IReadOnlyList<PbOperations.Rule> rules,
        long expiresAtTick,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (!WritesIdentityIsValid(identity) || expiresAtTick <= 0)
        {
            throw BridgeFunctions.Contract("invalid rules attach");
        }
        var request = new PbOperations.RulesAttachRequest
        {
            Identity = identity,
            ExpiresAtTick = expiresAtTick,
            Rules = EquatableArray.From(rules),
        };
        var reply = await Facade.AttachRulesAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply.Outcome?.Failure is { } failure)
        {
            throw Failure(failure);
        }
        var attached = reply.Outcome?.Attached;
        if (attached is null
            || !WritesContextMatches(attached.Context, identity)
            || attached.AcceptedIds.Count + attached.Refused.Count != rules.Count)
        {
            throw BridgeFunctions.Contract("invalid rules attach evidence");
        }
        foreach (var refusal in attached.Refused)
        {
            if (refusal.Reason.GetValueOrDefault() == PbOperations.RuleRefusalReason.Unspecified)
            {
                throw BridgeFunctions.Contract("rule refusal reason missing");
            }
        }
        return (attached, NoReceipt);
    }

    /// <summary>Go <c>Client.ClearReactions</c>: deactivates every native rule and reports how many were active.</summary>
    internal async Task<(uint Value, BridgeResult Receipt)> ClearReactionsAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        if (!WritesIdentityIsValid(identity))
        {
            throw BridgeFunctions.Contract("invalid rules clear");
        }
        var reply = await Facade.ClearRulesAsync(new PbOperations.RulesClearRequest { Identity = identity }, cancellationToken).ConfigureAwait(false);
        if (reply.Outcome?.Failure is { } failure)
        {
            throw Failure(failure);
        }
        var cleared = reply.Outcome?.Cleared;
        if (cleared is null || !WritesContextMatches(cleared.Context, identity) || cleared.Cleared is null)
        {
            throw BridgeFunctions.Contract("invalid rules clear evidence");
        }
        return (cleared.Cleared.GetValueOrDefault(), NoReceipt);
    }

    /// <summary>Go <c>Client.ReadReactions</c>: reads the active native rules, their lease and firing history.</summary>
    internal async Task<(PbOperations.RulesStatus Value, BridgeResult Receipt)> ReadReactionsAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        if (!WritesIdentityIsValid(identity))
        {
            throw BridgeFunctions.Contract("invalid rules status read");
        }
        var reply = await Facade.ReadRulesStatusAsync(new PbOperations.RulesStatusRequest { Identity = identity }, cancellationToken).ConfigureAwait(false);
        if (reply.Outcome?.Failure is { } failure)
        {
            throw Failure(failure);
        }
        var status = reply.Outcome?.Status;
        if (status is null || !WritesContextMatches(status.Context, identity))
        {
            throw BridgeFunctions.Contract("invalid rules status evidence");
        }
        foreach (var rule in status.Rules)
        {
            if (!BridgeFunctions.IsValidId(rule.RuleId ?? ""))
            {
                throw BridgeFunctions.Contract("rules status rule id");
            }
        }
        return (status, NoReceipt);
    }

    /// <summary>
    /// Go <c>AuthorityControl.FocusMap</c>: changes only the viewed loaded map under the trusted control CAS. A
    /// typed refusal throws <see cref="NativeFailureException"/>; a reply that does not prove the focus throws
    /// <see cref="FacadeAuthorityUncertainException"/>.
    /// </summary>
    internal async Task<(PbAuthority.ControlReply Value, BridgeResult Receipt)> FocusMapAsync(PbAuthority.FocusMap request, CancellationToken cancellationToken)
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
        try
        {
            if (reply.Outcome is PbAuthority.ControlReplyOutcome.FailureCase { Value: var failure })
            {
                if (Failure(failure) is BridgeRefusedException refused)
                {
                    throw refused;
                }
            }
            else
            {
                var focused = reply.Outcome?.FocusedMap ?? throw BridgeFunctions.Contract("focus-map requires focused reply");
                BridgeFunctions.ValidateContext(focused.Context);
                var expected = request.ExpectedGeneration.GetValueOrDefault();
                if (!BridgeFunctions.SameIdentity(request.Identity, request.Target))
                {
                    expected++;
                }
                if (!BridgeFunctions.SameIdentity(focused.Context!.Identity, request.Target)
                    || focused.Context.NativeGeneration is null
                    || focused.Context.NativeGeneration.GetValueOrDefault() != expected)
                {
                    throw BridgeFunctions.Contract("focus-map context mismatch");
                }
            }
        }
        catch (BridgeRefusedException)
        {
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new FacadeAuthorityUncertainException(e, NoReceipt);
        }
        return (reply, NoReceipt);
    }

    /// <summary>
    /// Go <c>LifecycleSave.Save</c>: requests one trusted checkpoint save. The request must carry a complete player
    /// identity, direction and request id and a save name; the expected tick is optional but, when set, is asserted
    /// before native persists anything. An uncertain outcome throws <see cref="FacadeSaveUncertainException"/>.
    /// </summary>
    internal async Task<(PbLifecycle.SaveReply Reply, BridgeResult Result)> SaveAsync(PbLifecycle.SaveRequest request, CancellationToken cancellationToken)
    {
        LifecycleSaveValidateRequest(request);
        var reply = await Facade.SaveGameAsync(request, cancellationToken).ConfigureAwait(false);
        LifecycleSaveInterpret(reply, request.Player!.RequestId);
        if (reply.Outcome is PbLifecycle.SaveReplyOutcome.CompletedCase { Value: var completed })
        {
            LifecycleSaveValidateCompleted(completed, request);
        }
        return (reply, NoReceipt);
    }

    /// <summary>
    /// Go <c>LifecycleSave.ReadSave</c>: re-reads a prior save request's exact outcome. It exists only to recover a
    /// reply the caller never received; an unknown or expired request id surfaces as a failure.
    /// </summary>
    internal async Task<(PbLifecycle.SaveReply Reply, BridgeResult Result)> ReadSaveAsync(string requestId, CancellationToken cancellationToken)
    {
        if (!BridgeFunctions.IsValidId(requestId ?? ""))
        {
            throw BridgeFunctions.Contract("read save requires a request id");
        }
        var reply = await Facade.PollSaveAsync(new PbLifecycle.RequestStatus { RequestId = requestId }, cancellationToken).ConfigureAwait(false);
        LifecycleSaveInterpret(reply, requestId);
        return (reply, NoReceipt);
    }

    /// <summary>Go <c>interpretSaveOutcome</c>: validates a SaveReply's shape and throws the typed error for an uncertain or failed one.</summary>
    private static void LifecycleSaveInterpret(PbLifecycle.SaveReply reply, string? requestId)
    {
        switch (reply.Outcome)
        {
            case PbLifecycle.SaveReplyOutcome.CompletedCase { Value: var completed }:
                if (completed.RequestId is null || (requestId is not null && completed.RequestId != requestId))
                {
                    throw BridgeFunctions.Contract("completed save request id mismatch");
                }
                break;
            case PbLifecycle.SaveReplyOutcome.UncertainCase { Value: var uncertain }:
                if (uncertain.RequestId is null
                    || (requestId is not null && uncertain.RequestId != requestId)
                    || !BridgeFunctions.Diagnostic(uncertain.Detail is not null, uncertain.Detail ?? ""))
                {
                    throw BridgeFunctions.Contract("invalid save uncertain");
                }
                if (uncertain.ObservedContext is not null)
                {
                    BridgeFunctions.ValidateContext(uncertain.ObservedContext);
                }
                throw new FacadeSaveUncertainException(uncertain, NoReceipt);
            case PbLifecycle.SaveReplyOutcome.FailureCase { Value: var failure }:
                throw Failure(failure);
            default:
                throw BridgeFunctions.Contract("save outcome missing");
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
        BridgeFunctions.ValidateIdentity(player.Identity);
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

    /// <summary>Go <c>validateSaveCompleted</c>.</summary>
    private static void LifecycleSaveValidateCompleted(PbLifecycle.SaveCompleted completed, PbLifecycle.SaveRequest request)
    {
        BridgeFunctions.ValidateContext(completed.Context);
        if (!BridgeFunctions.SameIdentity(completed.Context!.Identity, request.Player!.Identity))
        {
            throw BridgeFunctions.Contract("completed save identity mismatch");
        }
        if (completed.Paused is null || !completed.Paused.GetValueOrDefault())
        {
            throw BridgeFunctions.Contract("completed save must report native paused");
        }
        if (completed.RequestId is null || completed.RequestId != (request.Player.RequestId ?? ""))
        {
            throw BridgeFunctions.Contract("completed save request id mismatch");
        }
        if (completed.SaveName is null || completed.SaveName != (request.SaveName ?? ""))
        {
            throw BridgeFunctions.Contract("completed save name mismatch");
        }
        if (completed.PlayerDirection is null || completed.PlayerDirection.GetValueOrDefault() != request.Player.PlayerDirection.GetValueOrDefault())
        {
            throw BridgeFunctions.Contract("completed save direction mismatch");
        }
        if (request.ExpectedTick is not null && completed.Context.Tick.GetValueOrDefault() != request.ExpectedTick.GetValueOrDefault())
        {
            throw BridgeFunctions.Contract("completed save tick moved");
        }
    }

    private static bool WritesIdentityIsValid(PbCommon.Identity? identity)
    {
        try
        {
            BridgeFunctions.ValidateIdentity(identity);
            return true;
        }
        catch (BridgeContractException)
        {
            return false;
        }
    }

    /// <summary>Go <c>buildingContext(value, identity, 0, false) == nil</c>: a valid context of the identity's world.</summary>
    private static bool WritesContextMatches(PbCommon.ObservationContext? context, PbCommon.Identity identity)
    {
        try
        {
            FacadeWriteChecks.WriteContext(context, identity, 0, false);
            return true;
        }
        catch (BridgeContractException)
        {
            return false;
        }
    }
}
