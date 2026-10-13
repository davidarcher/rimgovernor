using System.Diagnostics;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Telemetry;

namespace RimGovernor.Brain.Host;

/// <summary>Writes the host's own rows (Go's <c>slog</c> calls of the serve command) through the telemetry emitter the session installed.</summary>
internal sealed class HostLog(TelemetryEmitter emitter, string component)
{
    public void Info(string kind, string message, params (string Key, object? Value)[] attrs) => Write(TelemetryLevel.Info, kind, message, attrs);

    public void Warn(string kind, string message, params (string Key, object? Value)[] attrs) => Write(TelemetryLevel.Warn, kind, message, attrs);

    public void Error(string kind, string message, params (string Key, object? Value)[] attrs) => Write(TelemetryLevel.Error, kind, message, attrs);

    private void Write(TelemetryLevel level, string kind, string message, (string Key, object? Value)[] attrs) =>
        emitter.Event(level, component, kind, message, attrs.Select(a => new KeyValuePair<string, object?>(a.Key, a.Value)));
}

/// <summary>One loaded world and native generation: the same-world fields plus the generation, never the plan (Go <c>governorWorld</c>).</summary>
internal readonly record struct GovernorWorld(ColonyId Colony, MapId Map, LoadId Load, NativeGeneration Generation)
{
    public bool SameWorld(GovernorWorld other) => Colony == other.Colony && Map == other.Map && Load == other.Load;

    public static GovernorWorld Of(RimGovernor.Contracts.Wire.Common.ObservationContext context) =>
        new(new ColonyId((context.Identity?.ColonyId ?? "")), new MapId(context.Identity?.MapId ?? 0), new LoadId((context.Identity?.LoadToken ?? "")), new NativeGeneration(context.NativeGeneration.GetValueOrDefault()));
}

/// <summary>
/// Restores save-owned intent into the session store and clears derived Round state once per loaded world, before
/// review or save flushing (Go <c>worldRebuild</c>).
/// </summary>
internal sealed class WorldRebuild(StoreJournal database, FacadeGovernorState native, Action<IReadOnlyList<PlanState>, CancellationToken>? orphans, HostLog log) : IDisposable
{
    private readonly SemaphoreSlim _mu = new(1, 1);

    public void Dispose() => _mu.Dispose();

    private GovernorWorld _world;
    private ulong _count;

    /// <summary>Raised after a rebuild ran, so planners that hold a frame stream resubscribe in the new world.</summary>
    public Func<CancellationToken, Task>? Rebuilt { get; set; }

    /// <summary>
    /// Rebuilds the store for <paramref name="world"/> unless it was the last one rebuilt. The native generation is
    /// not a new world: authority toggles (resume, Manual, a reason=None bump) raise it with the save unchanged, and
    /// rebuilding on each one wiped every method and the review.
    /// </summary>
    public async Task EnsureAsync(GovernorWorld world, CancellationToken ct)
    {
        await _mu.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_count != 0 && _world.SameWorld(world))
            {
                return;
            }
            // Start and end are logged with each phase's wall time: the rebuild runs before the first review, so a
            // slow one reads as a hang.
            var began = Stopwatch.GetTimestamp();
            log.Info("world_rebuild_started", "world rebuild started", ("colony", world.Colony.Value), ("map", world.Map.Value), ("load", world.Load.Value));
            var saved = await native.ReadAsync(ct).ConfigureAwait(false);
            var read = Stopwatch.GetElapsedTime(began);
            try
            {
                await database.RebuildStandardsAsync(saved, orphans, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new InvalidOperationException("rebuild standards: " + e.Message, e);
            }
            var standards = Stopwatch.GetElapsedTime(began) - read;
            try
            {
                await database.RebuildFamiliesAsync(saved, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new InvalidOperationException("rebuild families: " + e.Message, e);
            }
            var families = Stopwatch.GetElapsedTime(began) - read - standards;
            try
            {
                await database.ResetRoundsAsync(ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new InvalidOperationException("reset rounds: " + e.Message, e);
            }
            _world = world;
            _count++;
            log.Info(
                "world_rebuild_done",
                "world rebuild done",
                ("keys", saved.Count),
                ("elapsed_ms", (long)Stopwatch.GetElapsedTime(began).TotalMilliseconds),
                ("read_ms", (long)read.TotalMilliseconds),
                ("standards_ms", (long)standards.TotalMilliseconds),
                ("families_ms", (long)families.TotalMilliseconds));
        }
        finally
        {
            _ = _mu.Release();
        }
        if (Rebuilt is { } rebuilt)
        {
            await rebuilt(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The scheduler's <c>WorldReady</c> hook (Go <c>workerGate</c>): it ensures the step's world is rebuilt and
    /// reports whether any rebuild ran since the scheduler's last step, so the step reviews again from the reset cache.
    /// </summary>
    public Func<RimGovernor.Contracts.Wire.Common.ObservationContext, CancellationToken, Task<bool>> SchedulerGate()
    {
        ulong seen = 0;
        return async (observed, ct) =>
        {
            await EnsureAsync(GovernorWorld.Of(observed), ct).ConfigureAwait(false);
            await _mu.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var reset = _count != seen;
                seen = _count;
                return reset;
            }
            finally
            {
                _ = _mu.Release();
            }
        };
    }
}

/// <summary>
/// Writes the store's governor state into the save's component: every blob in one batched put, no diffing. The
/// world is rebuilt from the save first when it is new, so a flush never replaces the save's blobs with a store
/// that has not read them yet (Go <c>stateFlusher</c>).
/// </summary>
internal sealed class StateFlusher(FacadeGovernorState native, FacadeWorldSource worlds, StoreJournal database, WorldRebuild rebuild)
{
    public async Task FlushAsync(CancellationToken ct)
    {
        PbCommon.ObservationContext context;
        try
        {
            context = await worlds.ReadContextAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new InvalidOperationException("no current world to flush governor state into: " + e.Message, e);
        }
        await rebuild.EnsureAsync(GovernorWorld.Of(context), ct).ConfigureAwait(false);
        var blobs = await database.GovernorStateBlobsAsync(ct).ConfigureAwait(false);
        await native.PutAsync(blobs, ct).ConfigureAwait(false);
    }
}
