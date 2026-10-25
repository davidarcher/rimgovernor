using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.PlayerApi;

namespace RimGovernor.Brain.Host;

/// <summary>
/// The brain's autosave, made while the clock run driver holds the clock for it (the cadence lives in
/// <see cref="Runtime.ClockRunConfig.AutosaveEveryTicks"/>, not here, now that native skips vanilla autosave).
/// The slot is runtime state, not persisted: a new process starts again at <c>Autosave-1</c>.
/// </summary>
internal sealed class Autosaver(Func<RimGovernor.Brain.GameOrders.Lifecycle.SaveRequest, CancellationToken, Task<RimGovernor.Brain.GameFacts.Lifecycle.SaveReply>> save, HostLog log)
{
    /// <summary>How many rotating <c>Autosave-N</c> files are kept.</summary>
    internal const int Slots = 5;

    // The one clock run driver step calls this, one hold at a time, so the rotation needs no lock.
    private int _slot;

    /// <summary>
    /// Saves into the next slot. A failed or uncertain save is logged and leaves the slot, so the next autosave hold
    /// reuses it; only a completed save advances the rotation. The driver calls this only while the clock is held.
    /// </summary>
    public async Task SaveAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, long tick, CancellationToken cancellationToken)
    {
        var name = $"Autosave-{(_slot % Slots) + 1}";
        var request = new RimGovernor.Brain.GameOrders.Lifecycle.SaveRequest
        {
            Player = new RimGovernor.Brain.GameFacts.Lifecycle.PlayerLifecycleContext
            {
                Identity = identity,
                PlayerDirection = NewColonyBounds.LifecycleDirection,
                RequestId = $"autosave-{(DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).Ticks * 100}",
            },
            SaveName = name,
            ExpectedTick = new RimGovernor.Brain.GameOrders.Lifecycle.ExpectedSaveTick.AnyCase(),
        };
        try
        {
            _ = await save(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.Warn("autosave_failed", "autosave failed; retrying at the next autosave hold", ("save", name), ("tick", tick), ("uncertain", e is FacadeSaveUncertainException), ("error", e.Message));
            return;
        }

        _slot++;
        log.Info("autosaved", "autosaved", ("save", name), ("tick", tick));
    }
}

/// <summary>
/// The lifecycle capability every save goes through: it flushes the governor state first,
/// so the file carries fresh blobs. A failed flush refuses the save. Load, new colony and the menu return pass straight through.
/// </summary>
internal sealed class FlushedLifecycle(ILifecycleWriter inner, Func<CancellationToken, Task> flush) : ILifecycleWriter
{
    public async Task<RimGovernor.Brain.GameFacts.Lifecycle.SaveReply> SaveAsync(RimGovernor.Brain.GameOrders.Lifecycle.SaveRequest request, CancellationToken cancellationToken)
    {
        await flush(cancellationToken).ConfigureAwait(false);
        return await inner.SaveAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public Task<RimGovernor.Brain.GameFacts.Lifecycle.SaveReply> ReadSaveAsync(string requestId, CancellationToken cancellationToken) => inner.ReadSaveAsync(requestId, cancellationToken);

    public Task<RimGovernor.Brain.GameFacts.Lifecycle.LoadReply> LoadAsync(RimGovernor.Brain.GameOrders.Lifecycle.LoadRequest request, CancellationToken cancellationToken) => inner.LoadAsync(request, cancellationToken);

    public Task<RimGovernor.Brain.GameFacts.Lifecycle.LoadReply> ReadLoadAsync(string requestId, CancellationToken cancellationToken) => inner.ReadLoadAsync(requestId, cancellationToken);

    public Task<RimGovernor.Brain.GameFacts.Lifecycle.NewColonyReply> NewColonyAsync(RimGovernor.Brain.GameOrders.Lifecycle.NewColonyRequest request, CancellationToken cancellationToken) => inner.NewColonyAsync(request, cancellationToken);

    public Task<RimGovernor.Brain.GameFacts.Lifecycle.NewColonyReply> ReadNewColonyAsync(string requestId, CancellationToken cancellationToken) => inner.ReadNewColonyAsync(requestId, cancellationToken);

    public Task<RimGovernor.Brain.GameFacts.Lifecycle.MenuReply> MenuAsync(RimGovernor.Brain.GameOrders.Lifecycle.MenuRequest request, CancellationToken cancellationToken) => inner.MenuAsync(request, cancellationToken);

    public Task<RimGovernor.Brain.GameFacts.Lifecycle.MenuReply> ReadMenuAsync(string requestId, CancellationToken cancellationToken) => inner.ReadMenuAsync(requestId, cancellationToken);
}
