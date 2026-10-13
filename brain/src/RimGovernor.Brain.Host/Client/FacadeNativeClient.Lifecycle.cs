using RimGovernor.Brain.Bridge.Contracts;
using static RimGovernor.Brain.Bridge.Contracts.BridgeFunctions;

namespace RimGovernor.Brain.Host;

// Go lifecycle_load.go (LifecycleLoad.Load, ReadLoad) and lifecycle_new.go (LifecycleNewColony.NewColony,
// ReadNewColony). Save and ReadSave are in Writes.cs. Go returned a pending or superseded outcome as a typed error so
// a nil reply could not pass for success; the player routes map those arms themselves (202 and 409), so the core
// methods here return the reply for them and throw only for a failure arm or a malformed reply.
internal sealed partial class FacadeNativeClient
{
    /// <summary>
    /// Resolves the ideoligion a <c>governor_ideoligion</c> new-colony spec asks the governor to choose (Go
    /// <c>CreationCatalog</c> then <c>StartingIdeoligion</c>) from the scenario named. The creation-scoped catalog is built
    /// over the game's Defs, which the mod host installs with the catalog factory (#3116); until it does, a spec that
    /// asks for it is refused as unavailable.
    /// </summary>
    internal Func<string, CancellationToken, Task<PbCommon.IdeoligionDesign>>? StartingIdeoligion { get; set; }

    /// <summary>
    /// Go <c>LifecycleLoad.Load</c>: starts a native load of the named save and returns at once, with a pending outcome
    /// the caller polls with <see cref="ReadLoadAsync"/> in the ordinary case. It never waits for the load to finish.
    /// </summary>
    internal async Task<(PbLifecycle.LoadReply Reply, BridgeResult Result)> LoadAsync(PbLifecycle.LoadRequest request, CancellationToken cancellationToken)
    {
        LifecycleLoadValidateRequest(request);
        var reply = await Facade.StartLoadAsync(request, cancellationToken).ConfigureAwait(false);
        LifecycleLoadInterpret(reply, request.RequestId, request.SaveName);
        return (reply, NoReceipt);
    }

    /// <summary>Go <c>LifecycleLoad.ReadLoad</c>: polls for the outcome of a request a prior load started.</summary>
    internal async Task<(PbLifecycle.LoadReply Reply, BridgeResult Result)> ReadLoadAsync(string requestId, CancellationToken cancellationToken)
    {
        if (!IsValidId(requestId ?? ""))
        {
            throw Contract("read load requires a request id");
        }

        var reply = await Facade.PollLoadAsync(new PbLifecycle.RequestStatus { RequestId = requestId }, cancellationToken).ConfigureAwait(false);
        LifecycleLoadInterpret(reply, requestId, null);
        return (reply, NoReceipt);
    }

    /// <summary>
    /// Go <c>LifecycleNewColony.NewColony</c>: starts generating a colony from the request's spec and returns at once,
    /// with a pending outcome the caller polls with <see cref="ReadNewColonyAsync"/> in the ordinary case.
    /// </summary>
    internal async Task<(PbLifecycle.NewColonyReply Reply, BridgeResult Result)> NewColonyAsync(PbLifecycle.NewColonyRequest request, CancellationToken cancellationToken)
    {
        ValidateNewColonyRequest(request);
        var spec = request.Spec!;
        if (spec.GovernorIdeoligion.GetValueOrDefault() && spec.Ideoligion is null)
        {
            var resolve = StartingIdeoligion
                ?? throw new BridgeUnavailableException("new colony governor_ideoligion needs the creation catalog, which the mod host installs (#3116)");
            spec = spec with { Ideoligion = await resolve(spec.Scenario ?? "", cancellationToken).ConfigureAwait(false) };
        }

        request = request with { Spec = spec with { GovernorIdeoligion = null } };
        var reply = await Facade.StartNewColonyAsync(request, cancellationToken).ConfigureAwait(false);
        LifecycleNewColonyInterpret(reply, request.RequestId, request.Spec!.SaveName);
        return (reply, NoReceipt);
    }

    /// <summary>Go <c>LifecycleNewColony.ReadNewColony</c>: polls the outcome of a request a prior new colony started.</summary>
    internal async Task<(PbLifecycle.NewColonyReply Reply, BridgeResult Result)> ReadNewColonyAsync(string requestId, CancellationToken cancellationToken)
    {
        if (!IsValidId(requestId ?? ""))
        {
            throw Contract("read new colony requires a request id");
        }

        var reply = await Facade.PollNewColonyAsync(new PbLifecycle.RequestStatus { RequestId = requestId }, cancellationToken).ConfigureAwait(false);
        LifecycleNewColonyInterpret(reply, requestId, null);
        return (reply, NoReceipt);
    }

    /// <summary>Go <c>interpretLoadReply</c>: validates a load reply's shape; a failure arm throws.</summary>
    private static void LifecycleLoadInterpret(PbLifecycle.LoadReply reply, string? requestId, string? saveName)
    {
        switch (reply.Outcome)
        {
            case PbLifecycle.LoadReplyOutcome.CompletedCase { Value: var completed }:
                LifecycleLoadValidateCompleted(completed, requestId, saveName);
                break;
            case PbLifecycle.LoadReplyOutcome.PendingCase { Value: var pending }:
                if (pending.RequestId is null
                    || (requestId is not null && pending.RequestId != requestId)
                    || !Diagnostic(pending.Detail is not null, pending.Detail ?? ""))
                {
                    throw Contract("invalid load pending");
                }

                break;
            case PbLifecycle.LoadReplyOutcome.SupersededCase { Value: var superseded }:
                if (superseded.RequestId is null
                    || (requestId is not null && superseded.RequestId != requestId)
                    || !Diagnostic(superseded.Detail is not null, superseded.Detail ?? ""))
                {
                    throw Contract("invalid load superseded");
                }

                if (superseded.ObservedContext is not null)
                {
                    ValidateContext(superseded.ObservedContext);
                }

                break;
            case PbLifecycle.LoadReplyOutcome.FailureCase { Value: var failure }:
                throw Failure(failure);
            default:
                throw Contract("load outcome missing");
        }
    }

    /// <summary>Go <c>validateLoadRequest</c>.</summary>
    private static void LifecycleLoadValidateRequest(PbLifecycle.LoadRequest? request)
    {
        if (request is null)
        {
            throw Contract("load request required");
        }

        if (request.RequestId is null || !IsValidId(request.RequestId))
        {
            throw Contract("load request requires a request id");
        }

        if (request.SaveName is null || !IsValidId(request.SaveName))
        {
            throw Contract("load request requires a save name");
        }

        if (request.Readiness is { } readiness && !Enum.IsDefined(readiness))
        {
            throw Contract("load request readiness invalid");
        }

        if (request.ExpectedPlayer is { } player)
        {
            if (player.Identity is not null)
            {
                ValidateIdentity(player.Identity);
            }

            if (player.RequestId is not null && !IsValidId(player.RequestId))
            {
                throw Contract("load request expected player request id invalid");
            }
        }

        if (request.ExpectedInstanceId is not null && !IsValidId(request.ExpectedInstanceId))
        {
            throw Contract("load request expected instance id invalid");
        }
    }

    /// <summary>Go <c>validateLoadCompleted</c>.</summary>
    private static void LifecycleLoadValidateCompleted(PbLifecycle.LoadCompleted completed, string? requestId, string? saveName)
    {
        if (completed.Loaded is null)
        {
            throw Contract("missing completed load");
        }

        ValidateContext(completed.Loaded.Context);
        if (completed.RequestId is null || (requestId is not null && completed.RequestId != requestId))
        {
            throw Contract("completed load request id mismatch");
        }

        if (saveName is not null && (completed.SaveName is null || completed.SaveName != saveName))
        {
            throw Contract("completed load save name mismatch");
        }

        if (completed.Readiness is null)
        {
            throw Contract("completed load missing readiness");
        }

        var readiness = completed.Readiness.GetValueOrDefault();
        if (!Enum.IsDefined(readiness) || readiness == PbLifecycle.Readiness.Unspecified)
        {
            throw Contract("completed load readiness invalid");
        }
    }

    /// <summary>Go <c>interpretNewColonyReply</c>: validates a new colony reply's shape; a failure arm throws.</summary>
    private static void LifecycleNewColonyInterpret(PbLifecycle.NewColonyReply reply, string? requestId, string? saveName)
    {
        switch (reply.Outcome)
        {
            case PbLifecycle.NewColonyReplyOutcome.CompletedCase { Value: var completed }:
                LifecycleNewColonyValidateCompleted(completed, requestId, saveName);
                break;
            case PbLifecycle.NewColonyReplyOutcome.PendingCase { Value: var pending }:
                if (pending.RequestId is null
                    || (requestId is not null && pending.RequestId != requestId)
                    || !Diagnostic(pending.Detail is not null, pending.Detail ?? "")
                    || pending.Phase is not { } phase
                    || phase == PbLifecycle.NewColonyPhase.Unspecified
                    || !Enum.IsDefined(phase))
                {
                    throw Contract("invalid new colony pending");
                }

                break;
            case PbLifecycle.NewColonyReplyOutcome.SupersededCase { Value: var superseded }:
                if (superseded.RequestId is null
                    || (requestId is not null && superseded.RequestId != requestId)
                    || !Diagnostic(superseded.Detail is not null, superseded.Detail ?? ""))
                {
                    throw Contract("invalid new colony superseded");
                }

                break;
            case PbLifecycle.NewColonyReplyOutcome.FailureCase { Value: var failure }:
                throw Failure(failure);
            default:
                throw Contract("new colony outcome missing");
        }
    }

    /// <summary>Go <c>validateNewColonyCompleted</c>.</summary>
    private static void LifecycleNewColonyValidateCompleted(PbLifecycle.NewColonyCompleted completed, string? requestId, string? saveName)
    {
        ValidateContext(completed.Context);
        if (completed.RequestId is null || (requestId is not null && completed.RequestId != requestId))
        {
            throw Contract("completed new colony request id mismatch");
        }

        if (completed.SaveName is null || (saveName is not null && completed.SaveName != saveName))
        {
            throw Contract("completed new colony save name mismatch");
        }

        if (completed.Paused is null || completed.ByteLength is null || completed.Seed is null || !IsValidId(completed.Seed))
        {
            throw Contract("completed new colony missing paused, byte length or seed");
        }
    }
}
