namespace RimGovernor.Brain.BuildingRuntime;

// Port of the window side of internal/buildingruntime/clock_worker.go (the poll loop and Stop) and of the admission
// tail of ClockScheduler.StepWithReason. The Go scheduler planned and admitted in one step; the C# step only plans
// (ClockScheduler), so this driver runs after each step, takes the player gate itself and admits the window.

/// <summary>What the window driver reads natively beyond the scheduler's own reads: the journal page and the emergency census.</summary>
public interface IClockDriverNative : IClockEventNative
{
    /// <summary>The emergency census of the world <paramref name="identity"/> names.</summary>
    Task<EmergencyObservation> ReadEmergencyAsync(PbCommon.Identity identity, CancellationToken ct);
}

/// <summary>
/// Admits and starts the clock windows of a session, polls the native journal for what stopped them, re-takes the
/// clock from the player and backs the pace off (Go <c>ClockWorker</c> poll loop and <c>ClockScheduler</c> admission).
/// The host calls <see cref="AfterStepAsync"/> after every scheduler step and <see cref="StopAsync"/> on shutdown.
/// </summary>
public sealed partial class ClockWindowDriver : IClockWorkerStopper, IDisposable
{
    private static readonly TimeSpan PollBackoffMin = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PollBackoffMax = TimeSpan.FromSeconds(2);

    private readonly Player _player;
    private readonly Session _session;
    private readonly ClockScheduler _scheduler;
    private readonly IClockDriverNative _native;
    private readonly ClockWindowConfig _config;
    private readonly IClock _clock;
    private readonly WakeSignal? _wake;
    private readonly ClockEventPoll _poll;
    private readonly CombatStopMetrics _combatStops = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly SemaphoreSlim _pollWake = new(0, 1);
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private Task _pollTask = Task.CompletedTask;
    private bool _stopped;

    // Whether a window this driver owed was stopped and its readmission has not been dispatched yet (Go <c>readmitOwed</c>).
    private bool _readmitOwed;

    private ClockWindowDriver(Player player, Session session, ClockScheduler scheduler, IClockDriverNative native, ClockWindowConfig config, IClock clock, WakeSignal? wake)
    {
        _player = player;
        _session = session;
        _scheduler = scheduler;
        _native = native;
        _config = config;
        _clock = clock;
        _wake = wake;
        _poll = new ClockEventPoll(player, session, native, scheduler.Latched, config, clock);
    }

    /// <summary>
    /// Builds the driver for <paramref name="scheduler"/>, binds the profile's native journal, attaches the driver to the
    /// session's clock worker slot and starts the poll loop (Go <c>NewClockScheduler</c>'s profile and start validation
    /// and <c>NewClockWorker</c>).
    /// </summary>
    public static async Task<ClockWindowDriver> CreateAsync(
        Player player,
        Session session,
        ClockScheduler scheduler,
        IClockDriverNative native,
        ClockWindowConfig config,
        IClock clock,
        WakeSignal? wake,
        CancellationToken lifetime)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(clock);
        if (!session.HasClock)
        {
            throw new ControlException("NewClockWindowDriver: the session has no clock capability");
        }
        var start = Validated(config, scheduler.Config);
        var inbox = await player.Journal.BindClockInboxAsync(config.Profile, lifetime).ConfigureAwait(false);
        var driver = new ClockWindowDriver(player, session, scheduler, native, config with { Start = start, Profile = inbox.Profile }, clock, wake);
        if (start.PlayerAccelerated || config.FollowPlayerSpeed)
        {
            scheduler.Backoff = new PaceBackoff(new Tick(config.PaceHorizonTicks), clock.Now, driver.RequestCeilingAsync);
        }
        session.AttachClockWorker(driver);
        driver._pollTask = Task.Run(() => driver.PollLoopAsync(lifetime), lifetime);
        return driver;
    }

    // Checks the window the driver starts from the way NewClockScheduler checked it: a colony window with nothing
    // acknowledged, a combat bound within the window, a command the bridge validator accepts and a profile directory.
    private static ClockStart Validated(ClockWindowConfig config, ClockSchedulerConfig scheduler)
    {
        var start = config.Start with { Policy = config.Start.Policy?.Clone() ?? new PbClock.WatchPolicy() };
        var p = start.Policy!;
        if (p.Mode != PbClock.WatchMode.Colony
            || p.AcknowledgedHostileIds.Count + p.AcknowledgedDownedColonistIds.Count + p.AcknowledgedInjuredColonistIds.Count + p.SurgicalRecoveryIds.Count + p.MedicalRestIds.Count != 0
            || p.InjuryStopCooldownMs != 0)
        {
            throw new ControlException("NewClockWindowDriver: the start policy is not a plain colony window");
        }
        if (scheduler.CombatMaxTicks > start.MaxTicks)
        {
            throw new ControlException("NewClockWindowDriver: CombatMaxTicks exceeds the start's MaxTicks");
        }
        // These fixed validation identities carry no runtime permission.
        BridgeApi.ValidateClockExpectation(new ClockExpectation(
            new PbCommon.Identity { ColonyId = "validation", LoadToken = "validation", MapId = 0 },
            new PbCommon.AttemptKey { ControllerSessionId = "validation", ActionId = "validation", AttemptId = 1 },
            1,
            new ClockCommand(Start: start)));
        if (!Directory.Exists(config.Profile))
        {
            throw new ControlException("NewClockWindowDriver: the profile is not a directory");
        }
        return start;
    }

    /// <summary>The native profile whose journal the driver polls, canonical after the inbox bound it.</summary>
    public string Profile => _config.Profile;

    // Changes the running epoch's tick ceiling: the pace backoff's request. It names the running epoch the last
    // watched wave saw, keeping its speed and carrying the ceiling (Go requestCeiling).
    private async Task RequestCeilingAsync(PbClock.Epoch original, uint ceiling, CancellationToken ct)
    {
        var state = _session.State();
        if (!state.Enabled || !state.ObservationKnown)
        {
            throw new AuthorityException();
        }
        var sequence = await _player.Journal.ReadClockSequenceAsync(ct).ConfigureAwait(false);
        var intent = new ClockIntent(sequence.NextRequestId(), "", state.Snapshot, new ClockCommand(Speed: new ClockSpeed(original.Clone(), original.RequestedSpeed, ceiling)));
        var (attempt, error) = await _session.CommandClock(intent, ct).ConfigureAwait(false);
        if (error is not null)
        {
            throw error;
        }
        if (attempt?.Phase != ClockPhase.Applied)
        {
            throw new HeldException("pace ceiling request not applied");
        }
    }

    // Reads the native journal: one unheld read per round, answered at once. A read that captured evidence is followed
    // by the next at once; a failed call backs off from 250 ms to 2 s; otherwise the loop waits the cadence
    // (RunningPollInterval while a window runs) or until a completed step releases it. The cadence is a safety
    // bound: a blocked step must not hide player input or an authority interruption from this independent loop.
    private async Task PollLoopAsync(CancellationToken lifetime)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, _cancel.Token);
        var ct = linked.Token;
        var backoff = TimeSpan.Zero;
        while (!ct.IsCancellationRequested)
        {
            var running = _scheduler.WindowRunning;
            var interval = running && _config.RunningPollInterval > TimeSpan.Zero ? _config.RunningPollInterval : _config.PollInterval;
            ClockPollResult? result = null;
            Exception? failure = null;
            try
            {
                _poll.Running = running;
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
                bounded.CancelAfter(_config.CallTimeout);
                result = await _poll.PollAsync(bounded.Token).ConfigureAwait(false);
                _scheduler.SetRunning(_poll.Running);
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
                // The Worker alone acts on a stop: the step loop reacts to the same page by settling the epoch, and
                // the stop is not a decision input of its own; its step carries the stop only to publish the
                // stop-to-step latency.
                _wake?.NotifySections(result.Wake, result.Invalidated, result.InvalidatedSections, result.AuthorityChanged, result.Stopped, result.StoppedAt);
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

    // Releases the between-window wait on every completed step: the poll reads again at once while no window runs.
    private void WakePoll()
    {
        if (_scheduler.WindowRunning)
        {
            return;
        }
        try
        {
            _pollWake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    /// <summary>
    /// Stops the poll loop, drops authority and retires the epochs the driver owes (Go <c>ClockWorker.Stop</c>). It can be
    /// retried after a deadline or cleanup failure. It does not take the player gate or close the player.
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
            try
            {
                await _pollTask.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cancel.IsCancellationRequested && !ct.IsCancellationRequested)
            {
            }
            Exception? cleaned = null;
            try
            {
                await _session.CleanupClock(ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                cleaned = e;
            }
            var error = ErrorJoin.Of(disabled, cleaned);
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

    /// <summary>Releases the driver's handles; <see cref="StopAsync"/> has joined the poll loop first.</summary>
    public void Dispose()
    {
        _cancel.Cancel();
        _poll.Dispose();
        _pollWake.Dispose();
        _stopGate.Dispose();
        _cancel.Dispose();
    }
}
