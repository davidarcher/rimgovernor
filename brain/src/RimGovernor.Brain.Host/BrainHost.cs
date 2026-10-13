using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.NativeFacade;
using RimGovernor.Brain.PlayerApi;
using RimGovernor.Brain.Telemetry;
using RimGovernor.Brain.Telemetry.Otlp;

namespace RimGovernor.Brain.Host;

/// <summary>Where the host is in its life.</summary>
public enum BrainHostState
{
    Running,
    Stopping,
    Stopped,
}

/// <summary>A point-in-time account of the host, for diagnostics.</summary>
public sealed record BrainHostStatus(BrainHostState State, SchedulerStatus Scheduler, IReadOnlyList<PlannerHealth> Planners, bool Enabled, bool ObservationKnown);

/// <summary>
/// The brain, hosted in the mod's process (#3082). One host is the controller that <c>cmd/rimgovernor serve</c>
/// was: it opens the state database and the telemetry files, builds the session over the native facade, runs the
/// scheduler on its own thread under a watchdog, serves the player routes, keeps the save's governor state
/// flushed, and shuts all of it down in the order that leaves nothing half-written. The lease, blind-tick and
/// reconnect machinery of the Go controller is not carried: the brain and the game share a process, so a link
/// cannot drop under it.
/// </summary>
/// <remarks>
/// Fault isolation: nothing the brain does reaches the game thread, and nothing it throws leaves the host. A step
/// that faults or stalls is logged and counted; enough in a row suspend authority until the player resumes. Hazard
/// stops stay native and never wait on a planner. Build one with <see cref="StartAsync"/>; stop it with
/// <see cref="StopAsync"/> or <see cref="DisposeAsync"/>.
/// </remarks>
public sealed class BrainHost : IAsyncDisposable
{
    private readonly BrainHostOptions _options;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly StepSupervisor _steps;
    private readonly PlannerSupervisor _planners;
    private readonly object _mu = new();
    private int _state = (int)BrainHostState.Running;
    private Task? _stopping;

    private OtlpFileWriter? _writer;
    private TelemetryEmitter _emitter = new(NullTelemetrySink.Instance);
    private HostLog _log;
    private StoreJournal? _journal;
    private Session? _session;
    private Player? _player;
    private Worker? _worker;
    private PlannerParts? _plannerParts;
    private SchedulerLoop? _loop;
    private IDisposable? _routes;
    private WorldRebuild? _rebuild;
    private Task? _saveLoop;
    private Task? _resumer;

    private BrainHost(BrainHostOptions options)
    {
        _options = options;
        _steps = new StepSupervisor(options.Watchdog.FaultLimit);
        _log = new HostLog(_emitter, "brain-host");
        _planners = new PlannerSupervisor(options.Watchdog.PlannerMissLimit, _log);
    }

    /// <summary>The state the host is in.</summary>
    public BrainHostState State => (BrainHostState)Volatile.Read(ref _state);

    /// <summary>The suspension the watchdog latched, null while the brain may write.</summary>
    public HostSuspension? Suspension => _steps.Suspension;

    /// <summary>The scheduler's counters and the planners that keep missing the wave cutoff.</summary>
    public BrainHostStatus Status()
    {
        var control = _player?.State() ?? default;
        return new BrainHostStatus(State, _steps.Status(), _planners.Health(), control.Enabled, control.ObservationKnown);
    }

    /// <summary>
    /// Starts the host: telemetry first (every later step logs through it), then the database, the session, the
    /// player, the planners and the Worker, then the loops. A failure at any step unwinds what was built and throws;
    /// no thread is left running.
    /// </summary>
    public static async Task<BrainHost> StartAsync(BrainHostOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var host = new BrainHost(options);
        try
        {
            await host.StartCoreAsync(cancellationToken).ConfigureAwait(false);
            return host;
        }
        catch
        {
            try
            {
                await host.StopAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // the start failure is the one the caller needs
            }
            throw;
        }
    }

    private async Task StartCoreAsync(CancellationToken startup)
    {
        var options = _options;
        var facade = options.Facade ?? NativeFacadeRegistry.Require();
        StartTelemetry();
        // The Go init() made explicit: no kind is intent-mode until this runs, and it must precede any progress.
        BridgeFunctions.RegisterIntentKinds();

        var (journal, movedAside) = await StoreJournal.OpenOrReplaceAsync(options.StatePath, startup).ConfigureAwait(false);
        _journal = journal;
        if (movedAside is not null)
        {
            _log.Warn("state_reset", "state database from another schema version moved aside; starting fresh", ("path", options.StatePath), ("aside", movedAside));
        }

        var timings = options.Timings;
        var parts = options.Composition.ComposeSession(facade, _emitter);
        var worlds = new FacadeWorldSource(facade);
        var session = await Session.CreateAsync(
            new SessionConfig
            {
                RoundsMethods = options.RoundsMethods,
                Control = new ControlConfig { CallTimeout = timings.CallTimeout, StopWrites = static _ => Task.CompletedTask, Worlds = worlds },
                Executor = timings.Executor,
                Clock = parts.Clock,
                Haul = parts.Haul,
                Movement = parts.Movement,
                Trade = parts.Trade,
                Telemetry = _emitter,
            },
            journal,
            parts.Native,
            new FacadeAuthority(facade),
            parts.Writer,
            SystemClock.Instance,
            _lifetime.Token).ConfigureAwait(false);
        _session = session;

        var player = Player.Create(new PlayerConfig(timings.PlayerCallTimeout, timings.JournalTimeout), journal, session, worlds, _lifetime.Token);
        _player = player;

        var governorState = new FacadeGovernorState(facade);
        var rebuild = new WorldRebuild(journal, governorState, parts.Orphans, _log);
        _rebuild = rebuild;
        var wake = new WakeSignal();
        var planners = options.Composition.ComposePlanners(new PlannerContext
        {
            Player = player,
            Session = session,
            Journal = journal,
            Facade = facade,
            Telemetry = _emitter,
            Wake = wake,
            Sections = new FactStore(),
            WorldReady = rebuild.SchedulerGate(),
            // The Worker's advance steps the scheduler at once (Go ClockWorker.Nudge): a wake without evidence.
            Nudge = () => wake.Notify([], false),
        });
        _plannerParts = planners;
        if (planners.OpenFrames is { } openFrames)
        {
            rebuild.Rebuilt = openFrames;
            await openFrames(startup).ConfigureAwait(false);
        }

        _worker = Worker.Create(
            new WorkerConfig
            {
                BreakSource = planners.BreakSource,
                Pawns = planners.Pawns,
                Moves = planners.Moves,
                RoundsMethods = options.RoundsMethods,
                StepInterval = timings.WorkerStepInterval,
                MaxBackoff = timings.WorkerMaxBackoff,
                StepTimeout = timings.WorkerStepTimeout,
                RenewInterval = timings.WorkerRenewInterval,
                RenewTimeout = timings.WorkerRenewTimeout,
                Wake = wake,
                Advanced = () => wake.Notify([], false),
                Store = planners.Scheduler.Config.Store,
                WindowRunning = () => planners.Scheduler.WindowRunning,
                Validity = () => planners.Scheduler.Validity is { } validity ? (validity, true) : (default, false),
                Flush = planners.Flush,
                Placement = planners.Placement,
            },
            player,
            session,
            _lifetime.Token);

        var flusher = new StateFlusher(governorState, worlds, journal, rebuild);
        if (options.Routes is { } routes)
        {
            _routes = PlayerRoutes.Register(
                routes,
                new HostPlayerControl(player, journal, _steps, _log),
                new HostControlReader(journal),
                new HostClockReview(journal, options.ProfileDirectory));
        }

        var loop = new SchedulerLoop(player, session, planners.Scheduler, planners.Windows, wake, timings, options.Watchdog, _planners, _steps, _log, _lifetime.Token);
        _loop = loop;
        loop.Start();
        _saveLoop = Task.Run(() => new SaveSignalLoop(facade, flusher, _log).RunAsync(_lifetime.Token), CancellationToken.None);
        if (options.AutoResume)
        {
            var resumer = new AutoResumer(worlds, player, journal, _steps, _log);
            _resumer = Task.Run(() => resumer.RunAsync(timings.RefreshInterval, _lifetime.Token), CancellationToken.None);
        }
        _log.Info("brain_started", "the in-process brain started", ("run_id", options.RunId), ("auto_resume", options.AutoResume));
    }

    private void StartTelemetry()
    {
        // Telemetry never stops the brain: a failure to start it is reported and the rows go nowhere.
        try
        {
            _writer = OtlpFileWriter.Start(new OtlpFileConfig
            {
                Dir = _options.OtlpDirectory,
                Service = _options.OtlpService,
                RunId = _options.RunId,
                Warn = message => _log.Warn("telemetry_drop", message),
            });
            _emitter = new TelemetryEmitter(_writer);
            _log = new HostLog(_emitter, "brain-host");
        }
        catch (Exception e)
        {
            _writer = null;
            System.Diagnostics.Trace.TraceWarning("RimGovernor brain telemetry did not start: " + e.Message);
        }
    }

    /// <summary>
    /// Stops the host and everything it started, in the order that leaves nothing half-written: loops end, the
    /// Worker drains (which closes the player and the session), the database closes, the telemetry flushes. A
    /// second call joins the first. Faults on the way are collected, never raised before the rest is done; they
    /// are thrown together at the end.
    /// </summary>
    public Task StopAsync()
    {
        lock (_mu)
        {
            _stopping ??= StopCoreAsync();
            return _stopping;
        }
    }

    private async Task StopCoreAsync()
    {
        _ = Interlocked.Exchange(ref _state, (int)BrainHostState.Stopping);
        var errors = new List<Exception>();
        var timings = _options.Timings;
        _log.Info("brain_stopping", "the in-process brain is stopping");
        // Cancelling the lifetime invalidates the control (writes stop at once) and ends every loop's wait.
        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_loop is { } loop && !loop.Join(timings.ShutdownAttemptTimeout))
        {
            _log.Error("scheduler_not_joined", "the scheduler thread did not end; continuing with shutdown");
        }
        foreach (var task in new[] { _saveLoop, _resumer })
        {
            if (task is null)
            {
                continue;
            }
            try
            {
                await task.WaitAsync(timings.ShutdownAttemptTimeout).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                errors.Add(e);
            }
        }
        if (_plannerParts?.Windows is { } windows)
        {
            await Attempt(errors, "window driver", ct => windows.StopAsync(ct)).ConfigureAwait(false);
        }
        _routes?.Dispose();

        // A failed join retains the database: Close itself observes uncertain cleanup, and these retries never
        // repeat an execution command (Go drainBuilding).
        var drained = false;
        for (var attempt = 1; attempt <= timings.ShutdownAttempts && !drained; attempt++)
        {
            try
            {
                using var bound = new CancellationTokenSource(timings.ShutdownAttemptTimeout);
                if (_worker is { } worker)
                {
                    await worker.Close(bound.Token).ConfigureAwait(false);
                }
                else if (_player is { } player)
                {
                    await player.CloseAsync(bound.Token).ConfigureAwait(false);
                }
                else if (_session is { } session)
                {
                    await session.CloseAsync(bound.Token).ConfigureAwait(false);
                }
                drained = true;
            }
            catch (Exception e)
            {
                if (attempt == timings.ShutdownAttempts)
                {
                    errors.Add(e);
                }
                else
                {
                    _log.Warn("drain_retry", "draining the session failed; retrying", ("attempt", attempt), ("error", e.Message));
                    await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
                }
            }
        }
        _player?.Dispose();
        _rebuild?.Dispose();
        if (_journal is { } journal)
        {
            if (drained)
            {
                await Attempt(errors, "state database", _ => journal.DisposeAsync().AsTask()).ConfigureAwait(false);
            }
            else
            {
                // The session may still write: leaving the database open is safer than closing it under a write.
                _log.Error("database_retained", "the session did not drain; the state database stays open");
            }
        }
        _log.Info("brain_stopped", "the in-process brain stopped", ("drained", drained));
        if (_writer is { } writer)
        {
            await Attempt(errors, "telemetry", ct => writer.ShutdownAsync(ct), timings.TelemetryFlushTimeout).ConfigureAwait(false);
        }
        _lifetime.Dispose();
        _ = Interlocked.Exchange(ref _state, (int)BrainHostState.Stopped);
        if (errors.Count == 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
        }
        if (errors.Count > 1)
        {
            throw new AggregateException(errors);
        }
    }

    private static async Task Attempt(List<Exception> errors, string what, Func<CancellationToken, Task> run, TimeSpan? bound = null)
    {
        try
        {
            using var cancel = new CancellationTokenSource(bound ?? TimeSpan.FromSeconds(10));
            await run(cancel.Token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            errors.Add(new InvalidOperationException($"{what}: {e.Message}", e));
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // A dispose runs where the game cannot act on a failure; the stop already logged what it could.
            System.Diagnostics.Trace.TraceWarning("RimGovernor brain host stopped with errors: " + e.Message);
        }
    }
}
