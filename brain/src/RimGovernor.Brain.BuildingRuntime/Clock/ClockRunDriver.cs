namespace RimGovernor.Brain.BuildingRuntime;

// Port of the worker side of internal/buildingruntime/clock_worker.go (Stop). The Go scheduler planned and sent Run in
// one step; the C# step only plans (ClockScheduler), and this driver runs after each step. The clock is Running at the
// target speed or Held; the driver is the hold state machine that keeps it so.

/// <summary>What the run driver reads natively beyond the scheduler's own reads: the colony events and the emergency census.</summary>
public interface IClockDriverNative : IColonyEventNative
{
    /// <summary>The emergency census of the world <paramref name="identity"/> names.</summary>
    Task<EmergencyObservation> ReadEmergencyAsync(PbCommon.Identity identity, CancellationToken ct);
}

/// <summary>
/// Runs the native clock of a session: sends Run at the target speed under the watch policy the facts imply, takes
/// the brain's own holds (a coupled order, a review, an autosave), keeps the heartbeat, and consumes the colony events
/// native pushes, which say what held it. The host calls <see cref="AfterStepAsync"/> after every scheduler step and
/// <see cref="StopAsync"/> on shutdown.
/// </summary>
public sealed partial class ClockRunDriver : IClockWorkerStopper, IDisposable
{
    private readonly Player _player;
    private readonly Session _session;
    private readonly ClockScheduler _scheduler;
    private readonly IClockDriverNative _native;
    private readonly IClockWriter _writer;
    private readonly ClockRunConfig _config;
    private readonly IClock _clock;
    private readonly ColonyEventConsumer _events;
    private readonly CombatStopMetrics _combatStops;
    private readonly CancellationTokenSource _cancel = new();
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private Task _eventsTask = Task.CompletedTask;
    private Task _heartbeatTask = Task.CompletedTask;
    private bool _stopped;

    // The run state the decision diffs against; touched only under the player gate.
    private PbClock.WatchPolicy? _sentPolicy;
    private PbClock.Speed _sentSpeed;
    private long _autosaveMark = long.MinValue;

    // Set when the driver takes an autosave hold; the save runs at the first step that finds the clock held and paused
    // (the driver's hold, or a hazard hold that beat it), never while Running.
    private bool _autosaveDue;
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
        _combatStops = new CombatStopMetrics(config.CombatResumeBudget);
        _events = new ColonyEventConsumer(session, native, wake, config);
    }

    /// <summary>
    /// Builds the driver for <paramref name="scheduler"/>, attaches it to the session's clock worker slot and starts the
    /// colony event consumer and the heartbeat loop (Go <c>NewClockScheduler</c>'s policy validation and
    /// <c>NewClockWorker</c>). Each scheduler step then records the consumer's evidence count at its clock read.
    /// </summary>
    public static ClockRunDriver Create(
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
        var driver = new ClockRunDriver(player, session, scheduler, native, writer, config, clock, wake);
        session.AttachClockWorker(driver);
        scheduler.ColonyEventsSeen = () => driver._events.Seen;
        driver._eventsTask = Task.Run(() => driver.EventsLoopAsync(lifetime), CancellationToken.None);
        driver._heartbeatTask = Task.Run(() => driver.HeartbeatLoopAsync(lifetime), CancellationToken.None);
        return driver;
    }

    // Checks the run the driver starts from: a colony watch with nothing acknowledged, a policy the bridge validator
    // accepts, a real combat fallback and a heartbeat inside the native tolerance.
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
        if (config.CombatResumeBudget <= TimeSpan.Zero || !Enum.IsDefined(config.CombatFallbackSpeed) || config.CombatFallbackSpeed == PbClock.Speed.Unspecified)
        {
            throw new ControlException("NewClockRunDriver: the combat resume budget must be positive and the fallback speed a real speed");
        }
        if (config.HeartbeatInterval <= TimeSpan.Zero || config.HeartbeatInterval >= TimeSpan.FromSeconds(5))
        {
            throw new ControlException("NewClockRunDriver: the heartbeat interval must be under the native tolerance of five seconds");
        }
    }

    /// <summary>The colony event evidence the consumer has delivered (<see cref="ColonyEventConsumer.Seen"/>).</summary>
    internal long EventsSeen => _events.Seen;

    /// <summary>Whether the consumer has opened the native colony event queue.</summary>
    internal bool EventsOpened => _events.Opened;

    // Consumes the colony events until the driver stops; the consumer wakes the step loop itself.
    private async Task EventsLoopAsync(CancellationToken lifetime)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, _cancel.Token);
        await _events.RunAsync(linked.Token).ConfigureAwait(false);
    }

    // The brain's contact with the clock ceiling's heartbeat, independent of the events and the steps so a slow step
    // never lowers the ceiling. A beat that fails is only logged: heartbeat loss is native's to handle, not a hold; the
    // identity it named is read again for the next beat. The same loop holds the clock when authority has ended under a
    // Run this driver sent: a running clock with no brain behind it must not outlive its authority.
    private async Task HeartbeatLoopAsync(CancellationToken lifetime)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, _cancel.Token);
        var ct = linked.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_config.HeartbeatInterval, ct).ConfigureAwait(false);
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
                bounded.CancelAfter(_config.CallTimeout);
                if (_session.State().Enabled)
                {
                    var identity = await _events.IdentityAsync(bounded.Token).ConfigureAwait(false);
                    try
                    {
                        _ = await _writer.HeartbeatAsync(new PbClock.HeartbeatRequest { Identity = identity }, bounded.Token).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        _events.ForgetIdentity();
                        throw;
                    }
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

    /// <summary>
    /// Stops the event consumer and the heartbeat loop, drops authority and holds the clock (Go <c>ClockWorker.Stop</c>).
    /// It can be retried after a deadline or hold failure. It does not take the player gate or close the player.
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
            await Task.WhenAll(_eventsTask, _heartbeatTask).WaitAsync(ct).ConfigureAwait(false);
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
        _stopGate.Dispose();
        _cancel.Dispose();
    }
}
