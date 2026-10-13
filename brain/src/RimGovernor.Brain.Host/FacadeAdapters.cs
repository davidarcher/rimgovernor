using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.NativeFacade;
using RimGovernor.Brain.Store;
using Proto = RimGovernor.Protocol;

namespace RimGovernor.Brain.Host;

/// <summary>What a reply that is neither the loaded arm nor a sentinel turns into: the exceptions the bridge reads throw.</summary>
internal static class FacadeReplies
{
    /// <summary>The refusal a reply's failure arm is (Go <c>NativeFailure</c>).</summary>
    public static BridgeRefusedException Refused(string call, PbCommon.Failure? failure) =>
        new(failure is null
            ? $"{call}: native reply carried no outcome"
            : failure.Detail is not null && (failure.Detail ?? "").Length != 0
                ? $"{call}: native read refused: {failure.Code.GetValueOrDefault()}: {(failure.Detail ?? "")}"
                : $"{call}: native read refused: {failure.Code.GetValueOrDefault()}");

    /// <summary>The unavailability a reply's unavailable arm is (Go <c>NativeUnavailable</c>).</summary>
    public static BridgeUnavailableException Unavailable(string call, PbCommon.Unavailable? unavailable) =>
        new(unavailable is null
            ? $"{call}: native observation unavailable"
            : unavailable.Detail is not null && (unavailable.Detail ?? "").Length != 0
                ? $"{call}: native read unavailable: {unavailable.Reason.GetValueOrDefault()}: {(unavailable.Detail ?? "")}"
                : $"{call}: native read unavailable: {unavailable.Reason.GetValueOrDefault()}");
}

/// <summary>
/// The control's native authority over the facade (Go <c>bridge.AuthorityControl</c> plus the status read). The
/// facade's replies are the wire replies, so this only wraps the two calls into the requests they take.
/// </summary>
internal sealed class FacadeAuthority(INativeFacade facade) : INativeAuthority
{
    public async Task<PbAuthority.StatusReply> ReadAuthorityAsync(PbCommon.Identity identity, CancellationToken ct) =>
        ProtoRecordBridge.ToRecord<PbAuthority.StatusReply>(await facade.ReadAuthorityStatusAsync(ProtoRecordBridge.ToProto<Proto.Authority.StatusRequest>(new PbAuthority.StatusRequest { Identity = identity }), ct).ConfigureAwait(false));

    public async Task<PbAuthority.ControlReply> SetModeAsync(PbAuthority.SetMode request, CancellationToken ct) =>
        ProtoRecordBridge.ToRecord<PbAuthority.ControlReply>(await facade.ControlAuthorityAsync(ProtoRecordBridge.ToProto<Proto.Authority.ControlRequest>(new PbAuthority.ControlRequest { Operation = request }), ct).ConfigureAwait(false));

    public async Task<PbAuthority.ControlReply> RevokeAsync(PbAuthority.Revoke request, CancellationToken ct) =>
        ProtoRecordBridge.ToRecord<PbAuthority.ControlReply>(await facade.ControlAuthorityAsync(ProtoRecordBridge.ToProto<Proto.Authority.ControlRequest>(new PbAuthority.ControlRequest { Operation = request }), ct).ConfigureAwait(false));
}

/// <summary>
/// The actual native identity of the loaded world (Go <c>buildingWorldSource</c>): the bare tick read, so it
/// neither enters the control gate nor waits for capture work.
/// </summary>
internal sealed class FacadeWorldSource(INativeFacade facade) : IWorldSource
{
    /// <summary>The observation context of the loaded game: its identity, tick and native generation.</summary>
    public async Task<PbCommon.ObservationContext> ReadContextAsync(CancellationToken ct)
    {
        var reply = ProtoRecordBridge.ToRecord<PbLifecycle.TickReply>(await facade.ReadTickAsync(new Proto.Lifecycle.TickRequest(), ct).ConfigureAwait(false));
        switch (reply.Outcome)
        {
            case PbLifecycle.TickReplyOutcome.LoadedCase loaded:
                var context = loaded.Value.Context;
                BridgeFunctions.ValidateContext(context);
                return context!;
            case PbLifecycle.TickReplyOutcome.UnavailableCase unavailable:
                throw FacadeReplies.Unavailable("ReadTick", unavailable.Value);
            case PbLifecycle.TickReplyOutcome.FailureCase failure:
                throw FacadeReplies.Refused("ReadTick", failure.Value);
            default:
                throw new BridgeContractException("ReadTick reply carried no outcome");
        }
    }

    public async Task<World> ReadWorldAsync(CancellationToken ct)
    {
        var context = await ReadContextAsync(ct).ConfigureAwait(false);
        var id = context.Identity!;
        var world = new World(new ColonyId((id.ColonyId ?? "")), new LoadId((id.LoadToken ?? "")), new MapId(id.MapId.GetValueOrDefault()));
        world.Validate();
        ct.ThrowIfCancellationRequested();
        return world;
    }
}

/// <summary>
/// The save's governor state component (Go <c>governorStateNative</c>): read when a world changes, written whole
/// at save time.
/// </summary>
internal sealed class FacadeGovernorState(INativeFacade facade)
{
    public async Task<IReadOnlyDictionary<string, string>> ReadAsync(CancellationToken ct)
    {
        var reply = ProtoRecordBridge.ToRecord<PbLifecycle.GovernorStateReply>(await facade.ReadGovernorStateAsync(new Proto.Lifecycle.GovernorStateRequest(), ct).ConfigureAwait(false));
        return reply.Outcome switch
        {
            PbLifecycle.GovernorStateReplyOutcome.LoadedCase loaded => new Dictionary<string, string>(loaded.Value.Blobs, StringComparer.Ordinal),
            PbLifecycle.GovernorStateReplyOutcome.UnavailableCase unavailable => throw FacadeReplies.Unavailable("GovernorState", unavailable.Value),
            PbLifecycle.GovernorStateReplyOutcome.FailureCase failure => throw FacadeReplies.Refused("GovernorState", failure.Value),
            _ => throw new BridgeContractException("GovernorState reply carried no outcome"),
        };
    }

    public async Task PutAsync(IReadOnlyDictionary<string, string> blobs, CancellationToken ct)
    {
        var request = new PbLifecycle.PutGovernorStateBatchRequest { Blobs = new Dictionary<string, string>(blobs, StringComparer.Ordinal) };
        var reply = ProtoRecordBridge.ToRecord<PbLifecycle.GovernorStateReply>(await facade.PutGovernorStateBatchAsync(ProtoRecordBridge.ToProto<Proto.Lifecycle.PutGovernorStateBatchRequest>(request), ct).ConfigureAwait(false));
        switch (reply.Outcome)
        {
            case PbLifecycle.GovernorStateReplyOutcome.LoadedCase:
                return;
            case PbLifecycle.GovernorStateReplyOutcome.UnavailableCase unavailable:
                throw FacadeReplies.Unavailable("PutGovernorStateBatch", unavailable.Value);
            case PbLifecycle.GovernorStateReplyOutcome.FailureCase failure:
                throw FacadeReplies.Refused("PutGovernorStateBatch", failure.Value);
            default:
                throw new BridgeContractException("PutGovernorStateBatch reply carried no outcome");
        }
    }
}

/// <summary>The wall clock the executor and the observation layer read.</summary>
internal sealed class SystemClock : ExecutorClock, ObservationClock
{
    public static readonly SystemClock Instance = new();

    public DateTimeOffset Now() => DateTimeOffset.UtcNow;
}
