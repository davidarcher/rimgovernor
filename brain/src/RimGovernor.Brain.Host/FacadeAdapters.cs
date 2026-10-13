using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.NativeFacade;
using RimGovernor.Brain.Store;

namespace RimGovernor.Brain.Host;

/// <summary>What a reply that is neither the loaded arm nor a sentinel turns into: the exceptions the bridge reads throw.</summary>
internal static class FacadeReplies
{
    /// <summary>The refusal a reply's failure arm is (Go <c>NativeFailure</c>).</summary>
    public static BridgeRefusedException Refused(string call, PbCommon.Failure? failure) =>
        new(failure is null
            ? $"{call}: native reply carried no outcome"
            : failure.HasDetail && failure.Detail.Length != 0
                ? $"{call}: native read refused: {failure.Code}: {failure.Detail}"
                : $"{call}: native read refused: {failure.Code}");

    /// <summary>The unavailability a reply's unavailable arm is (Go <c>NativeUnavailable</c>).</summary>
    public static BridgeUnavailableException Unavailable(string call, PbCommon.Unavailable? unavailable) =>
        new(unavailable is null
            ? $"{call}: native observation unavailable"
            : unavailable.HasDetail && unavailable.Detail.Length != 0
                ? $"{call}: native read unavailable: {unavailable.Reason}: {unavailable.Detail}"
                : $"{call}: native read unavailable: {unavailable.Reason}");
}

/// <summary>
/// The control's native authority over the facade (Go <c>bridge.AuthorityControl</c> plus the status read). The
/// facade's replies are the wire replies, so this only wraps the two calls into the requests they take.
/// </summary>
internal sealed class FacadeAuthority(INativeFacade facade) : INativeAuthority
{
    public Task<PbAuthority.StatusReply> ReadAuthorityAsync(PbCommon.Identity identity, CancellationToken ct) =>
        facade.ReadAuthorityStatusAsync(new PbAuthority.StatusRequest { Identity = identity }, ct);

    public Task<PbAuthority.ControlReply> SetModeAsync(PbAuthority.SetMode request, CancellationToken ct) =>
        facade.ControlAuthorityAsync(new PbAuthority.ControlRequest { SetMode = request }, ct);

    public Task<PbAuthority.ControlReply> RevokeAsync(PbAuthority.Revoke request, CancellationToken ct) =>
        facade.ControlAuthorityAsync(new PbAuthority.ControlRequest { Revoke = request }, ct);
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
        var reply = await facade.ReadTickAsync(new PbLifecycle.TickRequest(), ct).ConfigureAwait(false);
        switch (reply.OutcomeCase)
        {
            case PbLifecycle.TickReply.OutcomeOneofCase.Loaded:
                var context = reply.Loaded.Context;
                BridgeFunctions.ValidateContext(context);
                return context;
            case PbLifecycle.TickReply.OutcomeOneofCase.Unavailable:
                throw FacadeReplies.Unavailable("ReadTick", reply.Unavailable);
            case PbLifecycle.TickReply.OutcomeOneofCase.Failure:
                throw FacadeReplies.Refused("ReadTick", reply.Failure);
            default:
                throw new BridgeContractException("ReadTick reply carried no outcome");
        }
    }

    public async Task<World> ReadWorldAsync(CancellationToken ct)
    {
        var context = await ReadContextAsync(ct).ConfigureAwait(false);
        var id = context.Identity;
        var world = new World(new ColonyId(id.ColonyId), new LoadId(id.LoadToken), new MapId(id.MapId));
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
        var reply = await facade.ReadGovernorStateAsync(new PbLifecycle.GovernorStateRequest(), ct).ConfigureAwait(false);
        return reply.OutcomeCase switch
        {
            PbLifecycle.GovernorStateReply.OutcomeOneofCase.Loaded => new Dictionary<string, string>(reply.Loaded.Blobs, StringComparer.Ordinal),
            PbLifecycle.GovernorStateReply.OutcomeOneofCase.Unavailable => throw FacadeReplies.Unavailable("GovernorState", reply.Unavailable),
            PbLifecycle.GovernorStateReply.OutcomeOneofCase.Failure => throw FacadeReplies.Refused("GovernorState", reply.Failure),
            _ => throw new BridgeContractException("GovernorState reply carried no outcome"),
        };
    }

    public async Task PutAsync(IReadOnlyDictionary<string, string> blobs, CancellationToken ct)
    {
        var request = new PbLifecycle.PutGovernorStateBatchRequest();
        foreach (var (key, value) in blobs)
        {
            request.Blobs.Add(key, value);
        }
        var reply = await facade.PutGovernorStateBatchAsync(request, ct).ConfigureAwait(false);
        switch (reply.OutcomeCase)
        {
            case PbLifecycle.GovernorStateReply.OutcomeOneofCase.Loaded:
                return;
            case PbLifecycle.GovernorStateReply.OutcomeOneofCase.Unavailable:
                throw FacadeReplies.Unavailable("PutGovernorStateBatch", reply.Unavailable);
            case PbLifecycle.GovernorStateReply.OutcomeOneofCase.Failure:
                throw FacadeReplies.Refused("PutGovernorStateBatch", reply.Failure);
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
