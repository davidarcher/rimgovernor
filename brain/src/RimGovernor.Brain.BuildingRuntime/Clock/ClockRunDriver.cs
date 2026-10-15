namespace RimGovernor.Brain.BuildingRuntime;

// Port of the poll side of internal/buildingruntime/clock_worker.go (the poll loop and Stop). The Go scheduler planned
// and admitted windows in one step; the C# step only plans (ClockScheduler), and this driver runs after each step.
// The clock is Running at the target speed or Held; the driver is the hold state machine that keeps it so.

/// <summary>What the run driver reads natively beyond the scheduler's own reads: the journal page and the emergency census.</summary>
public interface IClockDriverNative : IClockEventNative
{
    /// <summary>The emergency census of the world <paramref name="identity"/> names.</summary>
    Task<EmergencyObservation> ReadEmergencyAsync(PbCommon.Identity identity, CancellationToken ct);
}

/// <summary>
/// Runs the native clock of a session: sends Run at the target speed under the watch policy the facts imply, takes
/// the brain's own holds (a coupled order, a review, an autosave), keeps the heartbeat, and polls the native journal
/// for what held it. The host calls <see cref="AfterStepAsync"/> after every scheduler step and
/// <see cref="StopAsync"/> on shutdown.
/// </summary>
public sealed partial class ClockRunDriver : IClockWorkerStopper, IDisposable
{
    private static readonly TimeSpan PollBackoffMin = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PollBackoffMax = TimeSpan.FromSeconds(2);

    private readonly Player _player;
    private readonly Session _session;
    private readonly ClockScheduler _scheduler;
    private readonly IClockDriverNative _native;
    private readonly IClockWriter _writer;
    private readonly ClockRunConfig _config;
    private readonly IClock _clock;
    private readonly WakeSignal? _wake;
    private readonly ClockEventPoll _poll;
    private readonly CombatStopMetrics _combatStops = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly SemaphoreSlim _pollWake = new(0);
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private Task _pollTask = Task.CompletedTask;
    private Task _heartbeatTask = Task.CompletedTask;
    private bool _stopped;

    // The run state the decision diffs against; touched only under the player gate.
    private PbClock.WatchPolicy? _sentPolicy;
    private PbClock.Speed _sentSpeed;
    private long _autosaveMark = long.MinValue;
    private long _combatMarkedFor = long.MinValue;
    private readonly HashSet<ActionId> _coupledHeld = [];

    // Whether a Run this driver sent may still be in effect, so losing authority must hold the clock.
    private int _ran;

    private ClockRunDriver(Player player, Session session, ClockScheduler scheduler, IClockDriverNative native, IClockWriter writer, ClockRunConfig config, IClock clock, WakeSignal? wake)
    {
        _player = player;
        _session = session;
        _scheduler = scheduler;
        _native = native;
        _writer = writer;
        _config = config;
        _clock = clock;
        _wake = wake;
        _poll = new ClockEventPoll(player, session, native, clock, config);
    }

    /// <summary>
    /// Builds the driver for <paramref name="scheduler"/>, binds the profile's native journal, attaches the driver to the
    /// session's clock worker slot and starts the poll and heartbeat loops (Go <c>NewClockScheduler</c>'s profile and
    /// policy validation and <c>NewClockWorker</c>).
    /// </summary>
    public static async Task<ClockRunDriver> CreateAsync(
        Player player,
        Session session,
        ClockScheduler scheduler,
        IClockDriverNative native,
        IClockWriter writer,
        ClockRunConfig config,
        IClock clock,
        WakeSignal? wake,
        CancellationToken lifetime)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(clock);
        if (!session.HasClock)
        {
            throw new ControlException("NewClockRunDriver: the session has no clock capability");
        }
        Validated(config);
        var inbox = await player.Journal.BindClockInboxAsync(config.Profile, lifetime).ConfigureAwait(false);
        var driver = new ClockRunDriver(player, session, scheduler, native, writer, config with { Profile = inbox.Profile }, clock, wake);
        session.AttachClockWorker(driver);
        driver._pollTask = Task.Run(() => driver.PollLoopAsync(lifetime), CancellationToken.None);
        driver._heartbeatTask = Task.Run(() => driver.HeartbeatLoopAsync(lifetime), CancellationToken.None);
        return driver;
    }

    // Checks the run the driver starts from: a colony watch with nothing acknowledged, a policy the bridge validator
    // accepts and a profile directory.
    private static void Validated(ClockRunConfig config)
    {
        var p = config.Policy;
        if (p.Mode.GetValueOrDefault() != PbClock.WatchMode.Colony
            || p.AcknowledgedHostileIds.Count + p.AcknowledgedDownedColonistIds.Count + p.AcknowledgedInjuredColonistIds.Count + p.SurgicalRecoveryIds.Count + p.MedicalRestIds.Count != 0
            || p.InjuryStopCooldownMs.GetValueOrDefault() != 0)
        {
            throw new ControlException("NewClockRunDriver: the base policy is not a plain colony watch");
        }
        BridgeApi.ValidateClockPolicy(p);
        if (config.HeartbeatInterval <= TimeSpan.Zero || config.HeartbeatInterval >= TimeSpan.FromSeconds(5))
        {
            throw new ControlException("NewClockRunDriver: the heartbeat interval must be under the native tolerance of five seconds");
        }
        if (!Directory.Exists(config.Profile))
        {
            throw new ControlException("NewClockRunDriver: the profile is not a directory");
        }
    }

    /// <summary>The native profile whose journal the driver polls, canonical after the inbox bound it.</summary>
    public string Profile => _config.Profile;

    // Reads the native journal: one unheld read per round, answered at once. A read that captured evidence is followed
    // by the next at once; a failed call backs off from 250 ms to 2 s; otherwise the loop waits the cadence or until a
    // completed step releases it. The cadence is a safety bound: a blocked step must not hide a hold from this
    // independent loop.
    private async Task PollLoopAsync(CancellationToken lifetime)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, _cancel.Token);
        var ct = linked.Token;
        var backoff = TimeSpan.Zero;
        while (!ct.IsCancellationRequested)
        {
            var interval = _config.PollInterval;
            ClockPollResult? result = null;
            Exception? failure = null;
            try
            {
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
                bounded.CancelAfter(_config.CallTimeout);
                result = await _poll.PollAsync(bounded.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                failure = e;
            }
            if (result is not null && (result.Captured || result.Wake.Count > 0 || result.Invalidated.Count > 0 || result.AuthorityChanged))
            {
                // The step loop reacts to the page: a hold native took wakes it, and the hold itself is not a decision
                // input beyond the review the poll just recorded.
                _wake?.NotifySections(result.Wake, result.Invalidated, result.InvalidatedSections, result.AuthorityChanged, result.Held, result.HeldAt);
            }
            if (result is not null && result.Captured)
            {
                backoff = TimeSpan.Zero;
                continue;
            }
            if (failure is not null)
            {
                backoff = TimeSpan.FromTicks(Math.Min(PollBackoffMax.Ticks, Math.Max(PollBackoffMin.Ticks, backoff.Ticks * 2)));
                interval = backoff;
            }
            else
            {
                backoff = TimeSpan.Zero;
            }
            try
            {
                await _pollWake.WaitAsync(interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // The brain's contact with the clock ceiling's heartbeat, independent of the poll and the steps so a slow step
    // never lowers the ceiling. A beat that fails is only logged: heartbeat loss is native's to handle, not a hold.
    // The same loop holds the clock when authority has ended under a Run this driver sent: a running clock with no
    // brain behind it must not outlive its authority.
    private async Task HeartbeatLoopAsync(CancellationToken lifetime)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, _cancel.Token);
        var ct = linked.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_config.HeartbeatInterval, ct).ConfigureAwait(false);
                var identity = _poll.Identity;
                if (identity is null)
                {
                    continue;
                }
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
                bounded.CancelAfter(_config.CallTimeout);
                if (_session.State().Enabled)
                {
                    _ = await _writer.HeartbeatAsync(new PbClock.HeartbeatRequest { Identity = identity }, bounded.Token).ConfigureAwait(false);
                }
                else if (Interlocked.Exchange(ref _ran, 0) != 0)
                {
                    await _session.HoldClockAsync(PbClock.HoldReason.Review, "no_authority", bounded.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                ClockEvents.Event("clock-scheduler", "heartbeat", "clock heartbeat failed", ("err", e.Message));
            }
        }
    }

    // Releases the poll's wait on every completed step: the poll reads again at once.
    private void WakePoll() => _pollWake.Release();

    /// <summary>
    /// Stops the poll and heartbeat loops, drops authority and holds the clock (Go <c>ClockWorker.Stop</c>). It can be
    /// retried after a deadline or hold failure. It does not take the player gate or close the player.
    /// </summary>
    public async Task StopAsync(CancellationToken ct)
    {
        await _cancel.CancelAsync().ConfigureAwait(false);
        await _stopGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_stopped)
            {
                return;
            }
            Exception? disabled = null;
            try
            {
                _session.Disable();
            }
            catch (Exception e)
            {
                disabled = e;
            }
            // Both loops return on cancellation, so joining them only waits for a call in flight.
            await Task.WhenAll(_pollTask, _heartbeatTask).WaitAsync(ct).ConfigureAwait(false);
            Exception? held = null;
            try
            {
                await _session.HoldClockAsync(PbClock.HoldReason.Review, "stop", ct).ConfigureAwait(false);
                Interlocked.Exchange(ref _ran, 0);
            }
            catch (Exception e)
            {
                held = e;
            }
            var error = ErrorJoin.Of(disabled, held);
            if (error is not null)
            {
                throw error;
            }
            _stopped = true;
        }
        finally
        {
            _stopGate.Release();
        }
    }

    /// <summary>Releases the driver's handles; <see cref="StopAsync"/> has joined the loops first.</summary>
    public void Dispose()
    {
        _cancel.Cancel();
        _poll.Dispose();
        _pollWake.Dispose();
        _stopGate.Dispose();
        _cancel.Dispose();
    }
}
