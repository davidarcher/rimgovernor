using RimGovernor.Brain.BuildingRuntime;

namespace RimGovernor.Brain.Host;

/// <summary>
/// The scheduler thread: one dedicated thread that waits for a reason to plan, runs the scheduler's step on the
/// thread pool, and watches it. The loop is the watchdog: it waits for each step with a bound, so a step that
/// stalls (a planner that never returns, a native call that never completes) is seen by a thread that cannot
/// itself be in the step. A fault or a stall never ends the thread and never reaches the game: it is logged and
/// counted, and enough of them in a row suspend authority.
/// </summary>
/// <remarks>
/// The thread never calls a game API. Every native read goes through the facade, which queues it to the game
/// thread and completes on the pool. Hazard stops stay native and independent of this thread: nothing here is on
/// the path of a stop, so a wedged scheduler cannot delay one.
/// The cadence ports the Go clock worker's step loop (clock_worker.go): the first step plans everything, later
/// steps are woken by the timer or by committed evidence, an unchanged outcome backs off to the longest
/// back-off, and evidence resets it.
/// </remarks>
internal sealed class SchedulerLoop
{
    private readonly Player _player;
    private readonly Session _session;
    private readonly ClockScheduler _scheduler;
    private readonly IClockWindowDriver? _windows;
    private readonly WakeSignal _wake;
    private readonly BrainTimings _timings;
    private readonly BrainWatchdog _watchdog;
    private readonly PlannerSupervisor _planners;
    private readonly StepSupervisor _steps;
    private readonly HostLog _log;
    private readonly CancellationToken _lifetime;
    private Thread? _thread;

    public SchedulerLoop(
        Player player,
        Session session,
        ClockScheduler scheduler,
        IClockWindowDriver? windows,
        WakeSignal wake,
        BrainTimings timings,
        BrainWatchdog watchdog,
        PlannerSupervisor planners,
        StepSupervisor steps,
        HostLog log,
        CancellationToken lifetime)
    {
        _player = player;
        _session = session;
        _scheduler = scheduler;
        _windows = windows;
        _wake = wake;
        _timings = timings;
        _watchdog = watchdog;
        _planners = planners;
        _steps = steps;
        _log = log;
        _lifetime = lifetime;
    }

    public void Start()
    {
        var thread = new Thread(Run) { Name = "rimgovernor-brain-scheduler", IsBackground = true };
        if (Interlocked.CompareExchange(ref _thread, thread, null) is not null)
        {
            throw new InvalidOperationException("the scheduler loop was already started");
        }
        thread.Start();
    }

    /// <summary>Waits for the thread to end; false when it is still running after <paramref name="timeout"/>.</summary>
    public bool Join(TimeSpan timeout) => _thread is null || _thread.Join(timeout);

    private enum Outcome
    {
        Done,
        Faulted,
        Stalled,
        Wedged,
        Cancelled,
    }

    private void Run()
    {
        try
        {
            Loop();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // shutdown
        }
        catch (Exception e)
        {
            // The loop's own control flow is the one thing that must not throw; if it did, authority goes with it.
            _log.Error("scheduler_loop_died", "the scheduler loop ended on an unexpected error; authority suspended", ("error", e.ToString()));
            Suspend(SuspensionKind.Fault, "scheduler loop died: " + e.Message);
        }
    }

    private void Loop()
    {
        var delay = _timings.StepInterval;
        // The first step plans everything; each later step's reason is what ended the wait before it.
        var reason = new StepReason { Cause = StepCause.Full };
        StepKey? previous = null;
        var settledOnce = false;
        var idle = true;
        while (!_lifetime.IsCancellationRequested)
        {
            var state = _player.State();
            if (!state.Enabled || !state.ObservationKnown || _steps.Suspension is not null)
            {
                // Nothing to plan for: no authority, or the watchdog withdrew it. Evidence stays on the signal
                // for the first step after authority returns, which plans everything.
                idle = true;
                if (!WaitOrWake(_timings.StepInterval, takeEvidence: false, out _))
                {
                    return;
                }
                continue;
            }
            if (idle)
            {
                idle = false;
                reason = new StepReason { Cause = StepCause.Full };
                previous = null;
                delay = _timings.StepInterval;
            }

            ClockSchedulerResult? result = null;
            var outcome = Guard("step", async ct =>
            {
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
                bounded.CancelAfter(_timings.StepTimeout);
                result = await _scheduler.StepWithReasonAsync(reason, bounded.Token).ConfigureAwait(false);
            }, out var error);
            if (outcome == Outcome.Cancelled)
            {
                return;
            }
            var settled = false;
            if (outcome == Outcome.Done && result is not null)
            {
                _planners.Observe(result);
                if (_windows is not null)
                {
                    bool? windowSettled = null;
                    var windowOutcome = Guard("window", async ct => windowSettled = await _windows.AfterStepAsync(result, ct).ConfigureAwait(false), out var windowError);
                    if (windowOutcome == Outcome.Cancelled)
                    {
                        return;
                    }
                    if (windowOutcome != Outcome.Done)
                    {
                        outcome = windowOutcome;
                        error = windowError;
                    }
                    settled = windowSettled == true;
                }
            }

            var faulted = outcome != Outcome.Done && !IsRetry(error, outcome);
            Report(outcome, error, result, previous, out var key);
            if (outcome == Outcome.Done)
            {
                _steps.Succeeded();
            }
            else if (faulted && (_steps.Faulted(stall: outcome == Outcome.Stalled) || outcome == Outcome.Wedged))
            {
                var kind = outcome switch
                {
                    Outcome.Wedged => SuspensionKind.Wedged,
                    Outcome.Stalled => SuspensionKind.Stall,
                    _ => SuspensionKind.Fault,
                };
                Suspend(kind, error?.Message ?? outcome.ToString());
            }

            if (faulted)
            {
                // A fault waits out the watchdog's back-off rather than hot-looping on the same failure.
                delay = _watchdog.FaultBackoff;
                previous = null;
                if (!WaitOrWake(delay, takeEvidence: true, out var woken, out var faultReason))
                {
                    return;
                }
                reason = woken ? faultReason : new StepReason { Cause = StepCause.Timer };
                continue;
            }

            // A deferred or unchanged step backs off; a changed outcome resets the cadence.
            delay = previous is { } p && p == key ? TimeSpan.FromTicks(Math.Min(_timings.WorkerMaxBackoff.Ticks, delay.Ticks * 2)) : _timings.StepInterval;
            previous = key;

            // A window that just ran has left the planners for the next step: run it now rather than idle a
            // step interval with the game paused. Never twice in a row: a window that keeps ending without
            // settling is a back-off case, not a hot loop.
            if (settled && !settledOnce)
            {
                settledOnce = true;
                reason = new StepReason { Cause = StepCause.Settled };
                continue;
            }
            settledOnce = false;
            if (!WaitOrWake(delay, takeEvidence: true, out var wokenByEvidence, out var next))
            {
                return;
            }
            reason = new StepReason { Cause = StepCause.Timer };
            if (wokenByEvidence)
            {
                delay = _timings.StepInterval;
                previous = null;
                reason = next;
            }
        }
    }

    private static bool IsRetry(Exception? error, Outcome outcome) =>
        outcome == Outcome.Faulted && error is RimGovernor.Brain.Executor.AuthorityException or RimGovernor.Brain.Observation.ObservationChangedException or ControlException or RimGovernor.Brain.Executor.HeldException;

    /// <summary>
    /// Runs <paramref name="work"/> on the pool and waits for it on this thread, bounded by the watchdog. A work
    /// that outlives <see cref="BrainWatchdog.StallAfter"/> is cancelled; one that outlives
    /// <see cref="BrainWatchdog.AbandonAfter"/> after that is abandoned to the pool.
    /// </summary>
    private Outcome Guard(string what, Func<CancellationToken, Task> work, out Exception? error)
    {
        error = null;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        var task = Task.Run(() => work(cancel.Token), CancellationToken.None);
        try
        {
            if (task.Wait(_watchdog.StallAfter, _lifetime))
            {
                return Outcome.Done;
            }
            _log.Warn("scheduler_stall", $"the scheduler {what} is stalled; cancelling it", ("what", what), ("stall_after_ms", (long)_watchdog.StallAfter.TotalMilliseconds));
            error = new TimeoutException($"scheduler {what} ran past {_watchdog.StallAfter}");
            cancel.Cancel();
            if (task.Wait(_watchdog.AbandonAfter, _lifetime))
            {
                return Outcome.Stalled;
            }
            _log.Error("scheduler_wedged", $"the scheduler {what} ignored its cancellation; abandoning it", ("what", what), ("abandon_after_ms", (long)_watchdog.AbandonAfter.TotalMilliseconds));
            _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return Outcome.Wedged;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return Outcome.Cancelled;
        }
        catch (AggregateException e)
        {
            error = e.InnerExceptions.Count == 1 ? e.InnerExceptions[0] : e;
            return Outcome.Faulted;
        }
    }

    /// <summary>Sleeps for <paramref name="delay"/> unless committed evidence wakes the loop first; false when the host is stopping.</summary>
    private bool WaitOrWake(TimeSpan delay, bool takeEvidence, out bool woken) => WaitOrWake(delay, takeEvidence, out woken, out _);

    private bool WaitOrWake(TimeSpan delay, bool takeEvidence, out bool woken, out StepReason reason)
    {
        woken = false;
        reason = new StepReason { Cause = StepCause.Timer };
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        var wakeTask = takeEvidence ? WakeSignal.Wait(_wake, cancel.Token) : Task.Delay(Timeout.Infinite, cancel.Token);
        var timer = Task.Delay(delay, cancel.Token);
        try
        {
            _ = Task.WaitAny([wakeTask, timer], _lifetime);
        }
        catch (OperationCanceledException)
        {
            cancel.Cancel();
            return false;
        }
        cancel.Cancel();
        // Let the wake's read finish either way: a read that took the signal's token as the cancellation landed
        // must still wake the step, or its evidence would wait for the next notification.
        try
        {
            wakeTask.Wait();
        }
        catch (Exception e) when (e is OperationCanceledException or AggregateException)
        {
            // cancelled: no wake
        }
        if (takeEvidence && wakeTask.IsCompletedSuccessfully)
        {
            woken = true;
            reason = _wake.TakeInvalidated();
        }
        return !_lifetime.IsCancellationRequested;
    }

    private void Suspend(SuspensionKind kind, string detail)
    {
        if (!_steps.Suspend(new HostSuspension(kind, detail, DateTimeOffset.UtcNow)))
        {
            return;
        }
        _log.Error("brain_suspended", "the brain's authority is suspended until the player resumes", ("kind", kind.ToString()), ("detail", detail));
        try
        {
            // Local and synchronous: queued and active writes are invalidated before this returns. The native
            // grant is left to the player's Manual/Resume, which the suspension makes necessary.
            _session.Disable();
        }
        catch (Exception e)
        {
            _log.Error("brain_suspend_failed", "disabling the session failed", ("error", e.Message));
        }
    }

    private void Report(Outcome outcome, Exception? error, ClockSchedulerResult? result, StepKey? previous, out StepKey key)
    {
        key = StepKey.Of(outcome == Outcome.Done, error, result);
        if (previous is { } p && p == key)
        {
            return;
        }
        if (outcome != Outcome.Done)
        {
            if (IsRetry(error, outcome))
            {
                _log.Info("step_retry", "scheduler step waiting on authority or observation", ("error", error?.Message ?? ""));
            }
            else
            {
                _log.Warn("step_failed", "scheduler step failed", ("outcome", outcome.ToString()), ("error", error?.ToString() ?? ""));
            }
            return;
        }
        if (result is { PlannerFailures.Count: > 0 })
        {
            _log.Warn("planner_failures", "planners failed in the step; the step stands", ("failures", result.PlannerFailures.Select(f => f.Message).ToArray()));
        }
    }

    /// <summary>What distinguishes one step's outcome from the last, so a sustained outcome logs and backs off once (Go <c>clockStepKey</c>).</summary>
    private readonly record struct StepKey(bool Failed, string Failure, bool Running, bool Deferred, uint WindowTicks)
    {
        public static StepKey Of(bool done, Exception? error, ClockSchedulerResult? result)
        {
            var failure = error?.Message ?? "";
            if (result is { PlannerFailures.Count: > 0 })
            {
                failure += "; " + string.Join("; ", result.PlannerFailures.Select(f => f.Message));
            }
            return new StepKey(!done, failure, result?.Running ?? false, result?.Deferred ?? false, result?.Window.Ticks ?? 0);
        }
    }
}
