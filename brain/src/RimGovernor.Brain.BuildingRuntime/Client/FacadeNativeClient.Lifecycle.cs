using RimGovernor.Brain.Bridge.Contracts;

namespace RimGovernor.Brain.BuildingRuntime;

// Go lifecycle_load.go (LifecycleLoad.Load, ReadLoad) and lifecycle_new.go (LifecycleNewColony.NewColony,
// ReadNewColony). Save and ReadSave are in Writes.cs. Go returned a pending or superseded outcome as a typed error so
// a nil reply could not pass for success; the player routes map those arms themselves (202 and 409), so the core
// methods here return the reply for them and throw only for a failure arm or a malformed reply.
public sealed partial class FacadeNativeClient
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
    internal async Task<PbLifecycle.LoadReply> LoadAsync(PbLifecycle.LoadRequest request, CancellationToken cancellationToken)
    {
        LifecycleLoadValidateRequest(request);
        var reply = await Facade.StartLoadAsync(request, cancellationToken).ConfigureAwait(false);
        LifecycleFailure(reply.Outcome.Failure);
        return reply;
    }

    /// <summary>Go <c>LifecycleLoad.ReadLoad</c>: polls for the outcome of a request a prior load started.</summary>
    internal async Task<PbLifecycle.LoadReply> ReadLoadAsync(string requestId, CancellationToken cancellationToken)
    {
        var reply = await Facade.PollLoadAsync(new PbLifecycle.RequestStatus { RequestId = requestId }, cancellationToken).ConfigureAwait(false);
        LifecycleFailure(reply.Outcome.Failure);
        return reply;
    }

    /// <summary>
    /// Go <c>LifecycleNewColony.NewColony</c>: starts generating a colony from the request's spec and returns at once,
    /// with a pending outcome the caller polls with <see cref="ReadNewColonyAsync"/> in the ordinary case.
    /// </summary>
    internal async Task<PbLifecycle.NewColonyReply> NewColonyAsync(PbLifecycle.NewColonyRequest request, CancellationToken cancellationToken)
    {
        NewColonyValidation.ValidateNewColonyRequest(request);
        var spec = request.Spec!;
        if (spec.GovernorIdeoligion.GetValueOrDefault() && spec.Ideoligion is null)
        {
            var resolve = StartingIdeoligion
                ?? throw new BridgeUnavailableException("new colony governor_ideoligion needs the creation catalog, which the mod host installs (#3116)");
            spec = spec with { Ideoligion = await resolve(spec.Scenario ?? "", cancellationToken).ConfigureAwait(false) };
        }

        request = request with { Spec = spec with { GovernorIdeoligion = null } };
        var reply = await Facade.StartNewColonyAsync(request, cancellationToken).ConfigureAwait(false);
        LifecycleFailure(reply.Outcome.Failure);
        return reply;
    }

    /// <summary>Go <c>LifecycleNewColony.ReadNewColony</c>: polls the outcome of a request a prior new colony started.</summary>
    internal async Task<PbLifecycle.NewColonyReply> ReadNewColonyAsync(string requestId, CancellationToken cancellationToken)
    {
        var reply = await Facade.PollNewColonyAsync(new PbLifecycle.RequestStatus { RequestId = requestId }, cancellationToken).ConfigureAwait(false);
        LifecycleFailure(reply.Outcome.Failure);
        return reply;
    }

    /// <summary>
    /// Starts the return to the main menu (#3298) and returns at once: completed when the game already is at a fresh
    /// main menu, otherwise pending, which the caller polls with <see cref="ReadMenuAsync"/>.
    /// </summary>
    internal async Task<PbLifecycle.MenuReply> MenuAsync(PbLifecycle.MenuRequest request, CancellationToken cancellationToken)
    {
        var reply = await Facade.StartMenuAsync(request, cancellationToken).ConfigureAwait(false);
        LifecycleFailure(reply.Outcome.Failure);
        return reply;
    }

    /// <summary>Polls the outcome of a request a prior <see cref="MenuAsync"/> started.</summary>
    internal async Task<PbLifecycle.MenuReply> ReadMenuAsync(string requestId, CancellationToken cancellationToken)
    {
        var reply = await Facade.PollMenuAsync(new PbLifecycle.RequestStatus { RequestId = requestId }, cancellationToken).ConfigureAwait(false);
        LifecycleFailure(reply.Outcome.Failure);
        return reply;
    }

    /// <summary>Go <c>validateLoadRequest</c>.</summary>
    private static void LifecycleLoadValidateRequest(PbLifecycle.LoadRequest? request)
    {
        if (request is null)
        {
            throw BridgeContractException.Contract("load request required");
        }

        if (request.Readiness is { } readiness && !Enum.IsDefined(readiness))
        {
            throw BridgeContractException.Contract("load request readiness invalid");
        }
    }

    /// <summary>A lifecycle reply's failure arm throws; pending, superseded and completed arms are the caller's.</summary>
    private static void LifecycleFailure(PbCommon.Failure? failure)
    {
        if (failure is not null)
        {
            throw Failure(failure);
        }
    }
}
