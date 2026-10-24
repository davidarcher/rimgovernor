using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Store;

namespace RimGovernor.Brain.Host;

/// <summary>The refusal a write reply's failure arm turns into.</summary>
internal static class FacadeReplies
{
    /// <summary>The refusal a reply's failure arm is.</summary>
    public static BridgeRefusedException Refused(string call, PbCommon.Failure? failure) =>
        new(failure is null
            ? $"{call}: native reply carried no outcome"
            : failure.Detail is not null && (failure.Detail ?? "").Length != 0
                ? $"{call}: native read refused: {failure.Code.GetValueOrDefault()}: {(failure.Detail ?? "")}"
                : $"{call}: native read refused: {failure.Code.GetValueOrDefault()}");
}

/// <summary>
/// The control's native authority over the facade. The
/// facade's replies are the wire replies, so this only wraps the two calls into the requests they take.
/// </summary>
internal sealed class FacadeAuthority(INativeFacade facade) : INativeAuthority
{
    public Task<PbAuthority.Status> ReadAuthorityAsync(PbCommon.Identity identity, CancellationToken ct) =>
        facade.ReadAuthorityStatusAsync(new PbAuthority.StatusRequest { Identity = identity }, ct);

    public Task<PbAuthority.ControlReply> SetModeAsync(PbAuthority.SetMode request, CancellationToken ct) =>
        facade.ControlAuthorityAsync(new PbAuthority.ControlRequest { Operation = request }, ct);

    public Task<PbAuthority.ControlReply> RevokeAsync(PbAuthority.Revoke request, CancellationToken ct) =>
        facade.ControlAuthorityAsync(new PbAuthority.ControlRequest { Operation = request }, ct);
}

/// <summary>
/// The actual native identity of the loaded world: the bare tick read, so it
/// neither enters the control gate nor waits for capture work.
/// </summary>
internal sealed class FacadeWorldSource(INativeFacade facade) : IWorldSource
{
    /// <summary>The observation context of the loaded game: its identity, tick and native generation.</summary>
    public async Task<PbCommon.ObservationContext> ReadContextAsync(CancellationToken ct)
    {
        var loaded = await facade.ReadTickAsync(new PbLifecycle.TickRequest(), ct).ConfigureAwait(false);
        return loaded.Context ?? throw new BridgeContractException("ReadTick reply carried no context");
    }

    public async Task<World> ReadWorldAsync(CancellationToken ct)
    {
        var context = await ReadContextAsync(ct).ConfigureAwait(false);
        var id = context.Identity!;
        var world = new World(new ColonyId((id.ColonyId ?? "")), new LoadId((id.LoadToken ?? "")), new MapId(id.MapId));
        world.Validate();
        ct.ThrowIfCancellationRequested();
        return world;
    }
}

/// <summary>
/// The save's governor state component: read when a world changes, written whole
/// at save time.
/// </summary>
internal sealed class FacadeGovernorState(INativeFacade facade)
{
    public async Task<IReadOnlyDictionary<string, string>> ReadAsync(CancellationToken ct)
    {
        var loaded = await facade.ReadGovernorStateAsync(new PbLifecycle.GovernorStateRequest(), ct).ConfigureAwait(false);
        return new Dictionary<string, string>(loaded.Blobs, StringComparer.Ordinal);
    }

    public async Task PutAsync(IReadOnlyDictionary<string, string> blobs, CancellationToken ct)
    {
        var request = new PbLifecycle.PutGovernorStateBatchRequest { Blobs = new Dictionary<string, string>(blobs, StringComparer.Ordinal) };
        var reply = await facade.PutGovernorStateBatchAsync(request, ct).ConfigureAwait(false);
        if (reply.Loaded is null)
        {
            throw new BridgeUnavailableException("PutGovernorStateBatch: native staged nothing");
        }
    }
}

/// <summary>The wall clock the executor and the observation layer read.</summary>
internal sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTimeOffset Now() => DateTimeOffset.UtcNow;
}
