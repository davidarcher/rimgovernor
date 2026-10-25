using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;

namespace RimGovernor.Brain.Runtime;

public sealed class WorkerConfig
{
    public GameClient? BreakSource { get; set; }

    /// <summary>
    /// An action depending on a completed move waits until its pawn reaches the destination.
    /// An idle drafted mover's order is reasserted through the guarded session and journal.
    /// </summary>
    public IArrivalPawns? Pawns { get; set; }

    public bool ReviewMethods { get; set; }
    public TimeSpan StepInterval { get; set; }
    public TimeSpan MaxBackoff { get; set; }
    public TimeSpan StepTimeout { get; set; }
    public TimeSpan RenewInterval { get; set; }
    public TimeSpan RenewTimeout { get; set; }

    /// <summary>
    /// Bounds the actions one step runs before it yields the player gate; zero means no count bound: the step
    /// budget (StepTimeout, and the longest dispatch so far) ends a step, not a number. Every eligible
    /// independent action is dispatched in the step that finds it. A dependent action still waits for its
    /// prerequisite's outcome, which Hands enforces.
    /// </summary>
    public int MaxDispatches { get; set; }

    /// <summary>Names attempts whose outcome the native clock latched; the next step reconciles them first and ignores their backoff. Null keeps the ticker cadence.</summary>
    public WakeSignal? Wake { get; set; }

    /// <summary>
    /// The scheduler's decoded state store. A dispatch native refuses for a map-consuming kind (a placement or
    /// zone whose CAS token no longer matches) asks it to resync the planning window in full on its next
    /// refresh instead of trusting a delta; null asks nothing.
    /// </summary>
    public FactStore? Store { get; set; }

    /// <summary>
    /// Reports the scheduler's hint that its run is in progress; each native call the worker issues is recorded
    /// with it as a "dispatch" row, so a run can count the dispatches made live and the fraction native refused.
    /// </summary>
    public Func<bool>? ClockRunning { get; set; }

    /// <summary>
    /// The span context of the scheduler's latest step (ClockWorker.Trace). Each worker step is a span under it
    /// and each dispatch a span under the step. Null starts a trace per worker step.
    /// </summary>
    public Func<ActivityContext>? Trace { get; set; }

    /// <summary>
    /// The read validity of the scheduler's latest step (PlannerScheduler.Validity): each dispatch runs under it,
    /// so its boundaries judge their reads by age class instead of the global drift. Null leaves the dispatches
    /// on the compatibility shim.
    /// </summary>
    public Func<(ReadValidity Validity, bool Ok)>? Validity { get; set; }

    /// <summary>
    /// Asks native to capture the snapshot frame the step's writes deferred (Bridge.FlushSnapshot): a step's
    /// dispatches send defer_snapshot and the step flushes once at its end. Null leaves every dispatch
    /// capturing its own frame.
    /// </summary>
    public Func<CancellationToken, Task>? Flush { get; set; }
}

/// <summary>What a worker needs of a session beyond the player's: batch dispatch and read-only target attachment.</summary>
internal interface IWorkerSession : IPlayerSession, IExecutionSession
{
    new ControlState State();
}

/// <summary>
/// Owns Player's lifecycle, but its caller retains bridge and store handles until Close succeeds. Background
/// work cannot acquire player authority.
/// </summary>
public sealed partial class Worker
{
    /// <summary>Bounds the steps one wake or advance runs back to back.</summary>
    private const int BurstMax = 16;

    private readonly Player _player;
    private readonly IWorkerSession _session;
    private readonly WorkerConfig _config;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationTokenRegistration _stopPlayer;
    private readonly CancellationTokenRegistration _stopContext;
    private readonly Task _done;

    private readonly ActionAdvancement _hands;

    // Policy continuation can admit a successor Method independently of action advancement.
    private bool _advanced;

    // The finished tasks already recorded for a review (#3624), so each finished task is one review however often it is read.
    private readonly HashSet<string> _finishedTasks = new(StringComparer.Ordinal);

    private Worker(Player player, IWorkerSession session, WorkerConfig config, CancellationTokenSource lifetime)
    {
        _hands = new ActionAdvancement(new ActionAdvancementConfig
        {
            Journal = player.Journal, ReviewScope = new PlanAuthorizer(player.Journal, config.ReviewMethods),
            Session = session, Telemetry = () => Tel.Emitter, RecordOutcome = Metrics.RecordDispatchOutcome,
            Written = kind =>
            {
                var (everything, families) = PlannerScheduler.OperationFamilies(kind, true);
                if (everything) config.Store?.InvalidateAll();
                else config.Store?.InvalidateFamily([.. families]);
            },
            BreakSource = config.BreakSource is { } source ? source.ReadEmergencyAsync : null,
            BreakPawns = config.BreakSource,
            Pawns = config.Pawns, ReviewMethods = config.ReviewMethods,
            StepInterval = config.StepInterval, MaxBackoff = config.MaxBackoff, MaxDispatches = config.MaxDispatches,
            ClockRunning = config.ClockRunning, Flush = config.Flush,
        });
        _player = player;
        _session = session;
        _config = config;
        _lifetime = lifetime;
        _stopPlayer = player.Lifetime.Register(() => lifetime.Cancel());
        _stopContext = lifetime.Token.Register(() => { try { Stop(); } catch (Exception) { /* the error is discarded */ } });
        _done = Task.Run(async () =>
        {
            await Steps().ConfigureAwait(false);
            try { Stop(); } catch (Exception) { /* the error is discarded */ }
        });
    }


    public static Worker Create(WorkerConfig config, Player? player, Session? session, CancellationToken ct)
    {
        if (session is null || player is null || !ReferenceEquals(player.Session, session) || !ReferenceEquals(player.Journal, session.Journal))
        {
            throw new ControlException("NewWorker: session == nil || player == nil || player.session != session || player.journal != session.journal");
        }
        if (config.ReviewMethods && !session.ReviewMethodsEnabled())
        {
            throw new ControlException("NewWorker: config.ReviewMethods && !session.roundsMethods");
        }
        return CreateInternal(config, player, session, ct);
    }

    internal static Worker CreateInternal(WorkerConfig config, Player? player, IWorkerSession? session, CancellationToken ct)
    {
        if (player is null || session is null || !ReferenceEquals(player.Session, session) || config.StepInterval <= TimeSpan.Zero || config.MaxBackoff < config.StepInterval || config.MaxBackoff > TimeSpan.FromMinutes(1) || config.StepTimeout <= TimeSpan.Zero || config.StepTimeout > player.Config.CallTimeout)
        {
            throw new ControlException("newWorker: player == nil || session == nil || player.session != session || config.StepInterval <= 0 || config.MaxBacko");
        }
        lock (player.Mu)
        {
            if (player.WorkerAttached || player.Closing || player.Closed || player.Lifetime.IsCancellationRequested)
            {
                throw new ControlException("newWorker: player.workerAttached || player.closing || player.closed || player.lifetime.Err() != nil");
            }
            ct.ThrowIfCancellationRequested();
            var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
            player.WorkerAttached = true;
            return new Worker(player, session, config, lifetime);
        }
    }

    /// <summary>Rejects new player requests before waiting for a possibly slow native call.</summary>
    private void Stop()
    {
        var p = _player;
        lock (p.Mu)
        {
            if (p.Closed || p.Closing)
            {
                return;
            }
            p.Closing = true;
            p.CancelEpoch();
            p.Session.Disable();
        }
    }

    public async Task Close(CancellationToken ct)
    {
        _lifetime.Cancel();
        Exception? disableErr = null;
        try
        {
            Stop();
        }
        catch (Exception e)
        {
            disableErr = e;
        }
        try
        {
            await _done.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException oce)
        {
            throw Join(disableErr, oce)!;
        }
        // Player.Close already serializes concurrent closers through its gate.
        _stopPlayer.Dispose();
        _stopContext.Dispose();
        Exception? closeErr = null;
        try
        {
            await _player.CloseAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            closeErr = e;
        }
        var joined = Join(disableErr, closeErr);
        if (joined is not null)
        {
            ExceptionDispatchInfo.Capture(joined).Throw();
        }
    }

    private static Exception? Join(params Exception?[] errors)
    {
        var present = errors.Where(e => e is not null).Select(e => e!).ToList();
        return present.Count switch
        {
            0 => null,
            1 => present[0],
            _ => new AggregateException(present),
        };
    }

    private async Task Steps()
    {
        var ct = _lifetime.Token;
        var burst = 0;
        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }
            try
            {
                await Step(DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The step loop discards the step's error; the dispatch rows and the telemetry carry it.
            }
            burst++;
            // A wake steps at once; while focused actions remain, keep stepping so each of them is reconciled
            // without waiting a StepInterval, and a step that advanced an action steps again so the successor
            // it unblocked dispatches before the clock runs again rather than a StepInterval or its own
            // backoff later.
            if ((_hands.NeedsAdvance || _advanced) && burst < BurstMax)
            {
                continue;
            }
            burst = 0;
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                await Task.WhenAny(Task.Delay(_config.StepInterval, wait.Token), WakeSignal.Wait(_config.Wake, wait.Token)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            wait.Cancel();
            if (ct.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>One Worker step as a span under the scheduler's latest step (a trace root of its own when there is none).</summary>
    internal async Task Step(DateTimeOffset now, CancellationToken ct)
    {
        var parent = _config.Trace is { } trace ? trace() : default;
        using var span = Tel.StartSpan("buildingruntime", "worker.step", parent);
        try
        {
            await RunStep(now, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Tel.EndSpan(span, e);
            throw;
        }
        Tel.EndSpan(span, null);
    }

    private async Task RunStep(DateTimeOffset now, CancellationToken ct)
    {
        _advanced = false;
        var cycleBegan = Stopwatch.GetTimestamp();
        var stages = new StepStages();
        // StepTimeout budgets the dispatches, not the wait for the player gate: a scheduler step holds the gate
        // for its whole planner wave, and a budget that started before the wait left the step 0-3 s for its
        // actions and killed the last one at the deadline.
        using var gate = await _player.EnterAsync("worker", false, ct).ConfigureAwait(false);
        stages.Lap("gate");
        var epoch = gate.Epoch;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(gate.Call);
        budget.CancelAfter(_config.StepTimeout);
        var call = budget.Token;
        var deadline = DateTimeOffset.UtcNow + _config.StepTimeout;
        IDisposable? validityScope = null;
        if (_config.Validity is { } validityFn && validityFn() is { Ok: true } validityRead)
        {
            validityScope = ReadValidityScope.With(validityRead.Validity);
        }
        using var _validity = validityScope;
        World world = default;
        Exception? worldErr = null;
        try
        {
            world = await _player.Worlds.ReadWorldAsync(call).ConfigureAwait(false);
            world.Validate();
        }
        catch (Exception e)
        {
            worldErr = e;
        }
        if (worldErr is not null)
        {
            // A read that ran out of the step's budget (a scheduler step held the gate for most of it) or
            // failed in transport says nothing about the world: the step fails and the next one reads again.
            // Only a world that reads back unavailable or different is evidence against the authority.
            if (call.IsCancellationRequested || worldErr is OperationCanceledException or TimeoutException or System.Net.Http.HttpRequestException or System.IO.IOException)
            {
                ExceptionDispatchInfo.Capture(worldErr).Throw();
            }
            try
            {
                _session.Disable();
            }
            catch (Exception e)
            {
                throw Join(worldErr, e)!;
            }
            // No colony map yet (the game is at its main menu or between loads) is an expected state: the step
            // does nothing, and records nothing, until a map loads.
            if (worldErr is NotLoadedException)
            {
                return;
            }
        }
        _player.Current(call, epoch);
        var scope = _session.State();
        // Identity is a prerequisite for retaining permission, not just dispatch. Keep the private cleanup
        // scope, but stop renewal of an unavailable or different world before considering any read-only
        // reconciliation target.
        if ((scope.ObservationKnown && Player.PlayerWorld(scope.Snapshot) != world) || (scope.Enabled && !scope.ObservationKnown))
        {
            _session.Disable();
            scope = _session.State();
        }
        if (_config.ReviewMethods && scope.Enabled && scope.ObservationKnown)
            await ContinueActivityMethodsAsync(scope.Snapshot, call).ConfigureAwait(false);
        if (worldErr is not null) ExceptionDispatchInfo.Capture(worldErr).Throw();
        await _hands.AdvanceAsync(now, scope, world, deadline, cycleBegan, stages,
            () => _player.Current(call, epoch), call).ConfigureAwait(false);
    }
}
