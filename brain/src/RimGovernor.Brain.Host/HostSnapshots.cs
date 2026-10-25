using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.PlayerApi;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;

namespace RimGovernor.Brain.Host;

/// <summary>
/// The controller snapshot the state route and the lifecycle and presentation routes read:
/// the last good observation, refreshed serially on a cadence, with the player's control
/// state laid over it. Reading it never triggers inference or a native refresh; it owns no native mutation capability.
/// </summary>
internal sealed class HostSnapshots(string sessionId, IObservationSource source, IClock clock, TimeSpan maxAge, Player player, HostLog log) : ISnapshotProvider, IPlanReader, IDisposable
{
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly object _mu = new();
    private DateTimeOffset _started;
    private bool _gamePaused;
    private ControllerSnapshot _snapshot = new()
    {
        SessionId = sessionId,
        Mode = "manual",
        Status = "Waiting for game observations",
        Stale = true,
        World = new ControllerWorld.Unobserved(),
        Control = new ControllerControl.Unmatched(),
    };

    public void Dispose() => _refresh.Dispose();

    /// <summary>Whether the last good observation found the game paused; false until one succeeds.</summary>
    public bool GamePaused { get { lock (_mu) { return _gamePaused; } } }

    /// <summary>Plans are read from the journal the player writes; set by the host once the journal is open.</summary>
    public required StoreJournal Journal { get; init; }

    public Task<PlanState> LoadPlanAsync(PlanId id, CancellationToken cancellationToken) => Journal.LoadPlanAsync(id, cancellationToken);

    /// <summary>One tick observation; a failure marks the snapshot disconnected and stale.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObservationReading reading;
            bool paused;
            try
            {
                reading = await ObservationReads.Observe(source, clock, cancellationToken).ConfigureAwait(false);
                reading.CheckFresh(clock.Now(), maxAge);
                paused = reading.Identity.Paused;
            }
            catch (NotLoadedException e)
            {
                // No game is loaded (the main menu, between loads): the ended game's identity is gone, not merely
                // stale, so nothing keeps naming it (#3298).
                lock (_mu)
                {
                    _gamePaused = false;
                    _snapshot = _snapshot with
                    {
                        Connected = false,
                        Stale = true,
                        Status = "No game is loaded",
                        World = new ControllerWorld.Unobserved(),
                    };
                }

                throw new InvalidOperationException("observation refresh failed: " + e.Message, e);
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
                _started = reading.StartedAt;
                _gamePaused = paused;
                _snapshot = _snapshot with
                {
                    Connected = true,
                    Stale = false,
                    Status = "Read-only observation",
                    World = new ControllerWorld.Observed(reading.Identity, paused, reading.ObservedAt),
                };
            }
        }
        finally
        {
            _ = _refresh.Release();
        }
    }

    /// <summary>Runs after the initial refresh until the token ends.</summary>
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

    /// <summary>The cached observation with the control mode and generation overlaid.</summary>
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

            if (value.World is ControllerWorld.Observed seen && now < seen.ObservedAt)
            {
                value = value with { Stale = true };
            }
        }

        var control = player.State();
        value = value with { Mode = "manual", Control = new ControllerControl.Unmatched() };
        var matching = control.ObservationKnown && value.World is ControllerWorld.Observed { Identity: var identity }
            && identity.Colony == control.Snapshot.Colony && identity.Load == control.Snapshot.Load && identity.Map == control.Snapshot.Map;
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
            value = value with { Control = new ControllerControl.Matching(control.Snapshot) };
        }

        return Task.FromResult(value);
    }
}
