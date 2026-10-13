using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.PlayerApi;
using RimGovernor.Brain.Store;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Host;

/// <summary>
/// The controller snapshot the state route and the lifecycle and presentation routes read (Go <c>readState</c> and
/// <c>buildingSnapshots</c>): the last good observation, refreshed serially on a cadence, with the player's control
/// state laid over it. Reading it never triggers inference or a native refresh; it owns no native mutation capability.
/// </summary>
internal sealed class HostSnapshots(string sessionId, ISource source, ObservationClock clock, TimeSpan maxAge, Player player, HostLog log) : ISnapshotProvider, IPlanReader, IDisposable
{
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly object _mu = new();
    private DateTimeOffset _started;
    private ControllerSnapshot _snapshot = new() { SessionId = sessionId, Mode = "manual", Status = "Waiting for game observations", Stale = true };

    public void Dispose() => _refresh.Dispose();

    /// <summary>Plans are read from the journal the player writes; set by the host once the journal is open.</summary>
    public required StoreJournal Journal { get; init; }

    public Task<PlanState> LoadPlanAsync(PlanId id, CancellationToken cancellationToken) => Journal.LoadPlanAsync(id, cancellationToken);

    /// <summary>Go <c>readState.Refresh</c>: one tick observation; a failure marks the snapshot disconnected and stale.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Reading reading;
            try
            {
                reading = await ObservationPackage.Observe(source, clock, cancellationToken).ConfigureAwait(false);
                reading.Snapshot.CheckFresh(clock.Now(), maxAge, reading.Snapshot.After);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                lock (_mu)
                {
                    _snapshot = _snapshot with { Connected = false, Stale = true, Status = "Game observations are unavailable" };
                }

                throw new InvalidOperationException("observation refresh failed: " + e.Message, e);
            }

            lock (_mu)
            {
                _started = reading.Snapshot.StartedAt;
                _snapshot = _snapshot with
                {
                    Connected = true,
                    Stale = false,
                    Status = "Read-only observation",
                    Identity = Known(reading.Snapshot.After),
                    Tick = Known(reading.Snapshot.After.Tick),
                    Paused = reading.Snapshot.Status.Paused,
                    ObservedAt = Known(reading.Snapshot.ObservedAt),
                };
            }
        }
        finally
        {
            _ = _refresh.Release();
        }
    }

    /// <summary>Go <c>readState.Poll</c>: runs after the initial refresh until the token ends.</summary>
    public async Task PollAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        try
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.Warn("snapshot_refresh_failed", "the first observation refresh failed", ("error", e.Message));
        }

        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await RefreshAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // the snapshot already reads disconnected and stale; the next tick tries again
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Go <c>buildingSnapshots.Snapshot</c> over <c>readState.Snapshot</c>: the cached observation with the control mode and generation overlaid.</summary>
    public Task<ControllerSnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ControllerSnapshot value;
        lock (_mu)
        {
            value = _snapshot;
            var now = clock.Now();
            if (_started == default || now < _started || now - _started > maxAge)
            {
                value = value with { Stale = true };
            }

            if (value.ObservedAt.TryGetValue(out var observedAt) && now < observedAt)
            {
                value = value with { Stale = true };
            }
        }

        var control = player.State();
        value = value with { Mode = "manual", Generation = Unknown<GenerationSnapshot>(), ActivePlanId = Unknown<PlanId>() };
        var known = value.Identity.TryGetValue(out var identity);
        var matching = control.ObservationKnown && known
            && identity!.Colony == control.Snapshot.Colony && identity.Load == control.Snapshot.Load && identity.Map == control.Snapshot.Map;
        if (control.Enabled && matching && value.Connected && !value.Stale)
        {
            value = value with { Mode = "automate", Status = "Player execution enabled" };
        }
        else if (control.Enabled)
        {
            value = value with { Status = "Player control is waiting for current observations" };
        }
        else if (value.Connected && !value.Stale)
        {
            value = value with { Status = "Manual; explicit player controls available" };
        }

        if (matching)
        {
            value = value with { Generation = Known(control.Snapshot), ActivePlanId = Known(control.Snapshot.Plan) };
        }

        return Task.FromResult(value);
    }
}
