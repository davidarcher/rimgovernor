
namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// A trusted checkpoint save was refused: no attempt was made, or native reported an uncertain/failed outcome.
/// It is never silently treated as success; the caller keeps whatever save state it already had.
/// </summary>
public sealed class CheckpointException : Exception
{
    public CheckpointException() : base("checkpoint refused") { }
    public CheckpointException(Exception inner) : base("checkpoint refused", inner) { }
}

/// <summary>The trusted single-save native capability, held separately from ordinary reads the same way INativeAuthority is.</summary>
public interface ILifecycleSaver
{
    Task<(LifecyclePb.SaveReply Reply, BridgeResult Result)> SaveAsync(LifecyclePb.SaveRequest request, CancellationToken ct);
}

/// <summary>
/// Names the caller's expected live world and the native save name. ExpectedTick is optional; when present it is
/// asserted against the currently observed tick before native persists anything.
/// </summary>
public sealed record CheckpointRequest(GenerationSnapshot Expected, string SaveName, Tick ExpectedTick = default, bool HasExpectedTick = false);

/// <summary>The durable evidence of one trusted, pause-gated save. It never means load/reconnect happened.</summary>
public sealed record CheckpointResult(GenerationSnapshot Snapshot, Tick Tick, string SaveName, bool Paused);

public sealed partial class Control
{
    /// <summary>The fixed player_direction the controller sends on lifecycle requests (Go <c>bridge.LifecycleDirection</c>, lifecycle_save.go).</summary>
    private const ulong LifecycleDirection = 1;

    /// <summary>
    /// Drains owned writers under the control gate, refuses a foreign colony/load/map, requests one native save
    /// and validates the reply before returning success. Authority is never revoked and the profile lock is never
    /// released: play continues in Manual after a checkpoint. An uncertain native outcome is always reported as
    /// a refusal (<see cref="CheckpointException"/>), never silently accepted.
    /// </summary>
    public async Task<CheckpointResult> CheckpointAsync(ILifecycleSaver? save, CheckpointRequest request, CancellationToken ct)
    {
        if (save is null || !request.Expected.IsValid || !Boundary.ValidId(request.SaveName))
        {
            throw new CheckpointException();
        }
        GenerationSnapshot current;
        CancellationToken epoch;
        lock (_mu)
        {
            if (_closing || !LiveLocked())
            {
                throw new CheckpointException();
            }
            current = _snapshot;
            epoch = _epoch;
        }
        if (!Boundary.World(current, request.Expected))
        {
            throw new CheckpointException();
        }
        try
        {
            using var entry = await EnterAsync(ct, epoch).ConfigureAwait(false);
            var call = entry.Token;
            if (Config.CleanupWrites is { } cleanup)
            {
                await cleanup(call).ConfigureAwait(false);
            }
            call.ThrowIfCancellationRequested();
            var req = new LifecyclePb.SaveRequest
            {
                Player = new LifecyclePb.PlayerLifecycleContext
                {
                    Identity = ControlIdentity(current),
                    PlayerDirection = LifecycleDirection,
                    RequestId = string.Create(CultureInfo.InvariantCulture, $"checkpoint-{Namespace.Value}-{(DateTime.UtcNow - DateTime.UnixEpoch).Ticks * 100}"),
                },
                SaveName = request.SaveName,
            };
            if (request.HasExpectedTick)
            {
                req = req with { ExpectedTick = (long)request.ExpectedTick.Value };
            }
            var (reply, _) = await save.SaveAsync(req, call).ConfigureAwait(false);
            var completed = reply.Outcome?.Completed;
            if (completed is null)
            {
                throw new CheckpointException();
            }
            return new CheckpointResult(current, new Tick(completed.Context?.Tick ?? 0), (completed.SaveName ?? ""), completed.Paused.GetValueOrDefault());
        }
        catch (Exception e) when (e is not CheckpointException)
        {
            throw new CheckpointException(e);
        }
    }
}
