using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.PlayerApi;

namespace RimGovernor.Brain.Host;

/// <summary>
/// The Go-owned autosave, run at the clock scheduler's stop between windows now that native skips vanilla autosave (Go
/// <c>goAutosaver</c>, serve_autosave.go). The last-save tick and the slot are runtime state, not persisted: the first
/// stop of a process or of a rewound tick (a load) only sets the baseline.
/// </summary>
internal sealed class Autosaver(Func<PbLifecycle.SaveRequest, CancellationToken, Task<PbLifecycle.SaveReply>> save, HostLog log) : IDisposable
{
    /// <summary>The game time between Go saves: one in-game day, the same span as a routine clock window.</summary>
    internal const long IntervalTicks = 60000;

    /// <summary>How many rotating <c>Autosave-N</c> files Go keeps.</summary>
    internal const int Slots = 5;

    private readonly SemaphoreSlim _mu = new(1, 1);
    private bool _known;
    private long _last;
    private int _slot;

    public void Dispose() => _mu.Dispose();

    /// <summary>
    /// Saves when the interval has elapsed since the last Go save. A failed or uncertain save is logged and leaves the
    /// mark, so the next stop (a fresh observation) tries again; only a completed save advances the mark and slot.
    /// </summary>
    public async Task AtStopAsync(PbCommon.Identity identity, long tick, CancellationToken cancellationToken)
    {
        await _mu.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_known || tick < _last)
            {
                _known = true;
                _last = tick;
                return;
            }

            if (tick - _last < IntervalTicks)
            {
                return;
            }

            var name = $"Autosave-{(_slot % Slots) + 1}";
            var request = new PbLifecycle.SaveRequest
            {
                Player = new PbLifecycle.PlayerLifecycleContext
                {
                    Identity = identity,
                    PlayerDirection = NewColonyBounds.LifecycleDirection,
                    RequestId = $"autosave-{(DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).Ticks * 100}",
                },
                SaveName = name,
            };
            try
            {
                _ = await save(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.Warn("autosave_failed", "autosave failed; retrying at the next stop", ("save", name), ("tick", tick), ("uncertain", e is FacadeSaveUncertainException), ("error", e.Message));
                return;
            }

            _last = tick;
            _slot++;
            log.Info("autosaved", "autosaved", ("save", name), ("tick", tick));
        }
        finally
        {
            _ = _mu.Release();
        }
    }
}

/// <summary>
/// The lifecycle capability every Go-made save goes through (Go <c>flushedSave</c>): it flushes the governor state first,
/// so the file carries fresh blobs. A failed flush refuses the save. Load and new colony pass straight through.
/// </summary>
internal sealed class FlushedLifecycle(ILifecycleWriter inner, Func<CancellationToken, Task> flush) : ILifecycleWriter
{
    public async Task<PbLifecycle.SaveReply> SaveAsync(PbLifecycle.SaveRequest request, CancellationToken cancellationToken)
    {
        await flush(cancellationToken).ConfigureAwait(false);
        return await inner.SaveAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public Task<PbLifecycle.SaveReply> ReadSaveAsync(string requestId, CancellationToken cancellationToken) => inner.ReadSaveAsync(requestId, cancellationToken);

    public Task<PbLifecycle.LoadReply> LoadAsync(PbLifecycle.LoadRequest request, CancellationToken cancellationToken) => inner.LoadAsync(request, cancellationToken);

    public Task<PbLifecycle.LoadReply> ReadLoadAsync(string requestId, CancellationToken cancellationToken) => inner.ReadLoadAsync(requestId, cancellationToken);

    public Task<PbLifecycle.NewColonyReply> NewColonyAsync(PbLifecycle.NewColonyRequest request, CancellationToken cancellationToken) => inner.NewColonyAsync(request, cancellationToken);

    public Task<PbLifecycle.NewColonyReply> ReadNewColonyAsync(string requestId, CancellationToken cancellationToken) => inner.ReadNewColonyAsync(requestId, cancellationToken);
}
