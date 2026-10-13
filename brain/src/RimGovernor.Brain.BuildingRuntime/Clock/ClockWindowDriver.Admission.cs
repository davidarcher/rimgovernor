using System.Diagnostics;

namespace RimGovernor.Brain.BuildingRuntime;

// The admission tail of ClockScheduler.StepWithReason (Go clock_scheduler.go), run after the planning step: reconcile
// a start in flight, settle owed epochs, leave a running window to its stops, re-take the clock from the player,
// then judge the facts (EvaluateClockWindow) and start a window for the step's native-work ask.
//
// Differences from Go, all consequences of the C# step planning only:
//   - the player re-take and the owed-epoch settlement run after the planners, not before them, so a step that
//     finds either has planned on facts read under a game that was running; the driver returns true and the next
//     (settled) step plans again at the paused tick;
//   - the status and the emergency census are read again here (Go read the bundle once, again only when planners ran);
//   - the step publishes its own clock_step row; the admission verdict is its own "admission" row.

public sealed partial class ClockWindowDriver
{
    // What one admission pass found, for the row it files and the readmission bookkeeping (Go StepWithReason's locals).
    private sealed class WindowRun
    {
        public TimeSpan Paused { get; set; }

        public DateTimeOffset? ControllerPausedAt { get; set; }

        public bool Readmit { get; set; }

        public bool Deferred { get; set; }

        public ClockAttempt? Attempt { get; set; }

        public bool Combat { get; set; }

        public bool Cleaned { get; set; }

        public bool Reconciled { get; set; }

        public bool Retaken { get; set; }

        public int Unwatched { get; set; }

        public int CoupledOrders { get; set; }

        public ClockWindowDecision? Decision { get; set; }

        public bool Coupled { get; set; }
    }

    /// <summary>
    /// Acts on one finished scheduler step: reconciles what native latched, settles or leaves the running window, and
    /// admits and starts a window for the step's native-work ask. Returns true when it left the planners work to do
    /// at once (a window started, a dispatch reconciled, an epoch settled or the clock re-taken). A refused admission
    /// is a <see cref="HeldException"/>.
    /// </summary>
    public async Task<bool> AfterStepAsync(ClockSchedulerResult step, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (step.Deferred)
        {
            WakePoll();
            return false;
        }
        var run = new WindowRun();
        var began = Stopwatch.GetTimestamp();
        Exception? failure = null;
        try
        {
            using var entry = await _player.EnterAsync("clock_window", false, cancellationToken).ConfigureAwait(false);
            return await AdmitAsync(step, run, entry.Call, entry.Epoch).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            failure = e;
            throw;
        }
        finally
        {
            Finish(step, run, failure, Stopwatch.GetElapsedTime(began));
            WakePoll();
        }
    }

    // The bookkeeping Go ran in StepWithReason's deferred row: the readmission pause, the latched hold reset and the
    // admission verdict.
    private void Finish(ClockSchedulerResult step, WindowRun run, Exception? failure, TimeSpan elapsed)
    {
        if (run.Deferred)
        {
            _readmitOwed = _readmitOwed || run.Readmit;
        }
        else
        {
            _scheduler.Latched.Released();
            if (run.Attempt is not null)
            {
                run.Readmit = run.Readmit || _readmitOwed;
                _readmitOwed = false;
            }
        }
        if (run.Attempt is null && !run.Cleaned && !run.Reconciled && run.CoupledOrders == 0 && run.ControllerPausedAt is null)
        {
            return;
        }
        var attrs = new Dictionary<string, object?>(StringComparer.Ordinal) { ["running"] = step.Running };
        if (run.Unwatched != 0)
        {
            attrs["unwatched"] = run.Unwatched;
        }
        // The coupled orders ready under the window, and whether the step ended the window for them: the stop-reason
        // breakdown counts coupled stops from these fields, since the stop native journals for one is the
        // controller's own cleanup.
        if (run.CoupledOrders > 0)
        {
            attrs["coupled_orders"] = run.CoupledOrders;
            if (run.Coupled && run.Cleaned)
            {
                attrs["coupled_stop"] = true;
            }
        }
        // The stop-to-readmit pause this pass closed: the wall time from the stop of a window this driver owed to the
        // admission it dispatched. Absent when nothing was admitted or the stop was not one of its own windows.
        if (run.Paused > TimeSpan.Zero && run.Readmit && run.Attempt is not null && run.Attempt.Phase != ClockPhase.Refused)
        {
            attrs["stop_pause_s"] = run.Paused.TotalSeconds;
        }
        if (run.ControllerPausedAt is { } pausedAt)
        {
            attrs["controller_pause_ms"] = (_clock.Now() - pausedAt).TotalMilliseconds;
        }
        if (run.Combat)
        {
            attrs["combat"] = true;
        }
        if (run.Retaken)
        {
            attrs["retaken"] = true;
        }
        if (run.Reconciled)
        {
            attrs["reconciled"] = true;
        }
        if (run.Cleaned)
        {
            attrs["cleaned"] = true;
        }
        var decision = new Decision { Kind = "admission", Component = "clock-scheduler", Target = "window", Dur = elapsed, Attrs = attrs };
        if (failure is not null)
        {
            attrs["error"] = failure.Message;
            decision = decision with { Verdict = "failed", Reason = "window_error" };
        }
        else if (run.Attempt is { Phase: ClockPhase.Refused })
        {
            decision = decision with { Verdict = "refused", Reason = run.Decision is { Refused.Count: > 0 } d ? Wire.Name(d.Refused[0]) : "window_refused" };
        }
        else if (run.Attempt is not null)
        {
            attrs["window_ticks"] = step.Window.Ticks;
            decision = decision with { Verdict = "admitted", Reason = "window_admitted" };
        }
        else
        {
            decision = decision with { Verdict = "settled", Reason = run.Reconciled ? "reconciled" : run.Cleaned ? "cleaned" : "running" };
        }
        Tel.Decide(decision);
    }

    // Drops authority and joins the failure with the drop's own (Go errors.Join(err, s.session.Disable())).
    private Exception Failed(Exception cause)
    {
        try
        {
            _session.Disable();
            return cause;
        }
        catch (Exception disabled)
        {
            return ErrorJoin.Of(cause, disabled) ?? cause;
        }
    }

    // Drops authority and cleans the owned clock up, joining the failures (Go errors.Join(s.session.Disable(), s.session.CleanupClock(call))).
    private async Task DisableAndCleanupAsync(CancellationToken call)
    {
        Exception? disabled = null;
        try
        {
            _session.Disable();
        }
        catch (Exception e)
        {
            disabled = e;
        }
        Exception? cleaned = null;
        try
        {
            await _session.CleanupClock(call).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            cleaned = e;
        }
        var error = ErrorJoin.Of(disabled, cleaned);
        if (error is not null)
        {
            throw error;
        }
    }

    // One bundle read with the owned clock status, validated as a step read is (Go ReadStep and ValidateContext).
    private async Task<PbObservations.BundleSnapshot> ReadBundleAsync(CancellationToken call)
    {
        PbObservations.BundleSnapshot loaded;
        try
        {
            loaded = await _native.ReadStepAsync(new BridgeApi.StepRequest { ClockStatus = true }, call).ConfigureAwait(false)
                ?? throw new HeldException("step read returned nothing");
            BridgeApi.ValidateContext(loaded.Context);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw Failed(e);
        }
        return loaded;
    }

    // Validates a step read's clock status section under the enabled snapshot (Go bundleClockStatus).
    private static PbClock.Status BundleClockStatus(PbObservations.BundleSnapshot loaded, GenerationSnapshot snapshot)
    {
        var status = loaded.ClockStatus;
        BridgeApi.ValidateClockStatus(status, loaded.Context.Identity);
        Boundary.Context(status.Context, snapshot);
        return status;
    }

    private static bool SameWorldContext(PbObservations.BundleSnapshot loaded, ControlState state, GenerationSnapshot world) =>
        state.ObservationKnown
        && Boundary.World(state.Snapshot, world)
        && loaded.Context.HasNativeGeneration
        && loaded.Context.NativeGeneration == (ulong)state.Snapshot.Native.Value;

    // Whether the status shows the player running the game under a stopped clock this process no longer owes: the
    // status is stopped and not actually paused, the tick moved on since the step's, no epoch is owed, and authority
    // stands so the re-take's review can admit. A player who paused and stays paused never trips it (Go playerDriven).
    private bool PlayerDriven(PbClock.Status status, IReadOnlyList<ClockEpochObligation> epochs, ControlState state)
    {
        if (!ClockScheduler.PlayerRunning(status) || !_scheduler.LastTickKnown || (long)status.Context.Tick <= _scheduler.LastTick)
        {
            return false;
        }
        return epochs.All(owned => ClockCoordinator.Terminal(owned.Stage)) && state.Enabled;
    }

    private async Task<bool> AdmitAsync(ClockSchedulerResult step, WindowRun run, CancellationToken call, CancellationToken epoch)
    {
        var journal = _player.Journal;
        var attempts = await journal.LoadClockAttemptsAsync(4096, call).ConfigureAwait(false);
        var epochs = await journal.LoadClockEpochsAsync(4096, call).ConfigureAwait(false);
        var started = _clock.Now();
        var loaded = await ReadBundleAsync(call).ConfigureAwait(false);

        // The player runs the game by hand under a stopped clock: every fact read while it runs is stale before the
        // review commits, and no window can be admitted against a running game. Re-take the clock once the player has
        // let go of the speed keys: pause natively; the next step plans at the paused tick.
        if (PlayerDriven(loaded.ClockStatus, epochs, _session.State()))
        {
            if (!_poll.PlayerQuiet().Quiet)
            {
                run.Deferred = true;
                return false;
            }
            PbClock.Status taken;
            try
            {
                taken = BundleClockStatus(loaded, _session.State().Snapshot);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                ClockEvents.AuthorityLost("bundle clock status failed while re-taking the clock", ("err", e.Message));
                throw Failed(e);
            }
            _scheduler.LivePace(taken, started);
            var repaused = await _session.RepauseClock(taken, call).ConfigureAwait(false);
            ClockEvents.Event(
                "clock-scheduler",
                "authority",
                "clock re-taken from the player",
                ("change", "retaken"),
                ("tick", repaused.Context.Tick),
                ("paused", repaused.HasActualPaused && repaused.ActualPaused),
                ("pace", _scheduler.Pace.PerSecond),
                ("stop_reason", taken.Stopped?.Reason.ToString() ?? ""));
            run.Retaken = true;
            _scheduler.SetLastTick((long)repaused.Context.Tick);
            return true;
        }

        var state = _session.State();
        var id = loaded.Context.Identity;
        var world = new GenerationSnapshot { Colony = new ColonyId(id.ColonyId), Load = new LoadId(id.LoadToken), Map = new MapId(id.MapId) };
        var obligations = epochs.Any(owned => !ClockCoordinator.Terminal(owned.Stage));
        foreach (var v in attempts)
        {
            if (v.SupersededAt is not null || v.Intent.Command.Start is null || v.Phase is not (ClockPhase.Dispatched or ClockPhase.Uncertain))
            {
                continue;
            }
            if (!state.Enabled || !Boundary.World(v.Intent.Snapshot, world))
            {
                ClockEvents.AuthorityLost("start dispatched under another world or without authority; cleaning up", ("request", v.Intent.RequestId), ("enabled", state.Enabled));
                run.Cleaned = true;
                await DisableAndCleanupAsync(call).ConfigureAwait(false);
                return true;
            }
            var (recovered, reconcileError) = await _session.ReconcileClock(v.Intent.RequestId, call).ConfigureAwait(false);
            run.Attempt = recovered;
            run.Reconciled = true;
            if (reconcileError is not null)
            {
                throw reconcileError;
            }
            return true;
        }
        if (obligations && (!state.Enabled || !SameWorldContext(loaded, state, world)))
        {
            ClockEvents.AuthorityLost(
                "owed epoch no longer matches the held authority; cleaning up",
                ("enabled", state.Enabled),
                ("known", state.ObservationKnown),
                ("held", state.Snapshot.ToString()),
                ("observed_world", world.ToString()),
                ("observed_generation", loaded.Context.HasNativeGeneration ? loaded.Context.NativeGeneration : 0));
            run.Cleaned = true;
            await DisableAndCleanupAsync(call).ConfigureAwait(false);
            return true;
        }
        if (!SameWorldContext(loaded, state, world))
        {
            ClockEvents.AuthorityLost(
                "observed scope does not match the held authority",
                ("known", state.ObservationKnown),
                ("held", state.Snapshot.ToString()),
                ("observed_world", world.ToString()),
                ("observed_generation", loaded.Context.HasNativeGeneration ? loaded.Context.NativeGeneration : 0));
            throw Failed(new AuthorityException());
        }
        PbClock.Status status;
        try
        {
            status = BundleClockStatus(loaded, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ClockEvents.AuthorityLost("bundle clock status failed", ("err", e.Message));
            throw Failed(e);
        }
        if (status.HasActualPaused && status.ActualPaused)
        {
            run.ControllerPausedAt = _clock.Now();
        }
        if (status.Context.Tick < loaded.Context.Tick)
        {
            ClockEvents.AuthorityLost("clock status is behind the scope tick", ("status_tick", status.Context.Tick), ("scope_tick", loaded.Context.Tick));
            throw Failed(new EvidenceException("clock status is behind the scope tick"));
        }
        var tick = new Tick((long)status.Context.Tick);

        if (status.StateCase is PbClock.Status.StateOneofCase.Running or PbClock.Status.StateOneofCase.Stopping)
        {
            return await RunningWindowAsync(step, run, status, state, attempts, epochs, obligations, call).ConfigureAwait(false);
        }

        if (obligations)
        {
            // The window still owed is already stopped: settle it from the status just read and, once nothing is
            // owed, admit in the next step rather than leave the game paused for a second bundle read.
            run.Readmit = true;
            run.Cleaned = true;
            await _session.CleanupClockObserved(status, call).ConfigureAwait(false);
            return true;
        }
        if (!state.Enabled)
        {
            throw new AuthorityException();
        }
        _player.Current(call, epoch);
        if (status.StateCase == PbClock.Status.StateOneofCase.Stopped && _combatStops.Active)
        {
            _scheduler.Queue.CombatStopped();
        }

        // The admission's emergency census is the freshest the driver holds: file it so the store's section is the one
        // the window was admitted against, not the review's earlier read of the same world.
        EmergencyObservation emergency;
        EmergencySnapshot emergencyFacts;
        try
        {
            emergency = await _native.ReadEmergencyAsync(Boundary.Identity(state.Snapshot), call).ConfigureAwait(false);
            Boundary.Context(emergency.Context, state.Snapshot);
            emergencyFacts = EmergencySnapshot.New(state.Snapshot, new Tick((long)emergency.Context!.Tick), emergency.Facts);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw Failed(e);
        }
        var colonistsKnown = emergency.Facts.ColonistsComplete.TryGetValue(out var colonistsComplete);
        FactStore.Put(
            _scheduler.Facts.Store,
            ClockScheduler.FactScope(loaded.Context),
            FactSection.Emergency,
            new Held<EmergencyFacts> { Value = emergency.Facts, AsOf = (long)emergency.Context.Tick, Complete = colonistsKnown && colonistsComplete, Source = "rimgovernor/observations_read_status" });
        var review = await journal.ReadClockReviewAsync(_config.Profile, call).ConfigureAwait(false);
        var plan = await journal.LoadPlanAsync(state.Snapshot.Plan, call).ConfigureAwait(false);
        var (work, fingerprint) = ClockWindowWork.Census(plan, state.Snapshot);
        var plans = await journal.LoadPlansAsync(call).ConfigureAwait(false);
        var (remaining, items) = await ClockWindowWork.RoundsWorkAsync(journal, _scheduler.Config.RoundsMethods, state.Snapshot, plans, call).ConfigureAwait(false);
        work |= remaining;
        fingerprint.AddRange(items);
        if (ClockScheduler.RulesDispatchTurn(_scheduler.Config.Worker, step.Rules))
        {
            run.Deferred = true;
            return false;
        }
        var pending = _scheduler.Latched.Pending(fingerprint);
        JournalHoldEscape();
        if (_scheduler.Config.Worker && pending.Count > 0)
        {
            // The window just stopped on a latched outcome the Worker has not reconciled: a window admitted now would
            // watch that attempt again (the native clock never re-latches a settled one) and run out its budget
            // before the successor it unblocks is dispatched.
            run.Deferred = true;
            return false;
        }
        var waiting = _scheduler.Latched.Undispatched(fingerprint);
        JournalHoldEscape();
        if (_scheduler.Config.Worker && waiting.Count > 0)
        {
            // Likewise while the successor is queued but not yet dispatched.
            run.Deferred = true;
            return false;
        }
        var (combatPlan, fightOpen, huntPrey) = await ClockWindowWork.CombatPlanAsync(journal, state.Snapshot, call).ConfigureAwait(false);
        // An open fight is work of its own once its drafts complete: its next decision waits on the next armed stop,
        // which needs ticks.
        work |= fightOpen;
        // The defense planner's verdict at this stop: only a reported no-squad answer lets a hostile building be
        // watched instead of held; any other outcome, or no planner, keeps the hold.
        var squadUnanswered = Unknown<bool>();
        if (step.Defense is { } defense && !defense.Verdict.IsZero)
        {
            squadUnanswered = Known(defense.Verdict == BuildingReasonNoSquad);
        }
        // A complete sheltering response lets the threat be waited out: the wait is work of its own, on game time. It
        // reads the review's facts, so it holds whether or not the recovery planner ran this step.
        var sheltered = BuildingRuntimePackage.ClockShelterHeld(step.Rounds);
        if (sheltered.TryGetValue(out var held) && held)
        {
            work = true;
        }
        var start = _config.Start;
        if (_config.FollowPlayerSpeed)
        {
            start = ClockPlayerSpeed.Follow(start, status);
        }
        // A routine window runs the whole budget; a native-work or combat bound may narrow it below.
        run.Paused = ClockWindowSizing.StopSpan(status, _clock.Now());
        step.Window = new ClockWindowSize(start.MaxTicks);
        var nativeWorkTicks = step.NativeWorkTicks;
        var scheduler = _scheduler.Config;
        if (!work && scheduler.RoundsMethods && nativeWorkTicks > 0)
        {
            work = true;
            if (step.IdleLent)
            {
                _scheduler.WarnIdleStall((long)status.Context.Tick);
            }
            start = start with { MaxTicks = Math.Min(Math.Min(start.MaxTicks, nativeWorkTicks), _scheduler.NativeWorkBudget()) };
        }
        // An admitted trade phase keeps the window short: the session's next phase lands in the stop after it, before
        // the caravan leaves.
        if (step.Trade is { NativeWorkTicks: > 0 } trade)
        {
            start = start with { MaxTicks = Math.Min(start.MaxTicks, trade.NativeWorkTicks) };
        }
        var clockState = status.StateCase switch
        {
            PbClock.Status.StateOneofCase.NeverStarted => ClockWindowState.NeverStarted,
            PbClock.Status.StateOneofCase.Stopped => ClockWindowState.Stopped,
            _ => ClockWindowState.None,
        };
        var facts = new ClockWindowFacts
        {
            Current = state.Snapshot,
            Tick = tick,
            StartedAt = started,
            ObservedAt = _clock.Now(),
            Emergency = emergencyFacts,
            Review = new ClockWindowReview
            {
                Revision = review.Revision,
                Captured = review.InboxCursor,
                Reviewed = review.ReviewedCursor,
                Acknowledged = review.AcknowledgedCursor,
                HasHolds = Known(review.Holds.Count > 0),
            },
            Status = new ClockWindowStatus
            {
                Snapshot = state.Snapshot,
                Tick = tick,
                State = clockState,
                ActualPaused = Boundary.FactBool(status.HasActualPaused, status.ActualPaused),
                NativeTickBoundary = Boundary.FactBool(status.HasNativeTickBoundary, status.NativeTickBoundary),
                DurableEvents = Boundary.FactBool(status.HasDurableEvents, status.DurableEvents),
                NewestCursor = status.HasNewestCursor ? Known((long)status.NewestCursor) : Unknown<long>(),
            },
            Obligations = new ClockWindowObligations { Complete = Known(true), OwnedEpochPending = Known(false), UnknownStartPending = Known(false) },
            WorkRemaining = Known(work),
            CombatPlan = Known(combatPlan),
            HuntPrey = huntPrey,
            SquadUnanswered = squadUnanswered,
            Sheltered = sheltered,
        };
        start = start with { MaxTicks = await _scheduler.RulesWindowAsync(state.Snapshot, tick, start.MaxTicks, call).ConfigureAwait(false) };
        var combatMaxTicks = Math.Min(scheduler.CombatMaxTicks, start.MaxTicks);
        var decision = EvaluateClockWindow(facts, new ClockWindowLimits { Now = _clock.Now(), MaxAge = scheduler.MaxAge, MaxTicks = start.MaxTicks, CombatMaxTicks = combatMaxTicks });
        run.Decision = decision;
        _scheduler.NoWork = !decision.Admitted && decision.Refused.Contains(ClockWindowReason.NoWork);
        _scheduler.IdleRefusals = _scheduler.NoWork ? _scheduler.IdleRefusals + 1 : 0;
        if (!decision.Admitted)
        {
            Tel.Decide(ClockScheduler.WindowRefusedDecision(
                [.. decision.Refused.Select(r => Wire.Name(r))],
                new Dictionary<string, object?>
                {
                    ["mode"] = Wire.Name(decision.Mode),
                    ["work"] = work,
                    ["combat_plan"] = combatPlan,
                    ["hostiles"] = decision.Hostiles.Count,
                    ["clock_state"] = Wire.Name(clockState),
                    ["window_ticks"] = step.Window.Ticks,
                }));
            throw new HeldException("clock window refused");
        }
        var policy = start.Policy!.Clone();
        start = start with { Policy = policy };
        // No window watches attempts: a completed order is not a reason to stop the clock, the event poll carries its
        // outcome to the Worker under the running window.
        _scheduler.Facts.Remember(fingerprint);
        // A colonist already known downed is acknowledged in either mode: the native watcher otherwise stops every
        // window at zero ticks on the same casualty and the rescue never gets the ticks it needs.
        policy.AcknowledgedDownedColonistIds.Clear();
        policy.AcknowledgedDownedColonistIds.AddRange(decision.Downed.Select(d => d.Value));
        if (decision.Mode == ClockWindowMode.Combat)
        {
            // The native watcher stops on any unacknowledged hostile within HostileWithin; a combat window
            // acknowledges exactly the live hostiles the policy admitted and runs under the combat budget.
            run.Combat = true;
            policy.Mode = PbClock.WatchMode.Combat;
            policy.AcknowledgedHostileIds.Clear();
            policy.AcknowledgedHostileIds.AddRange(decision.Hostiles.Select(h => h.Value));
            start = start with { MaxTicks = decision.MaxTicks };
            // Native stops the window on the tick an armed event happens; the combat budget is the backstop when
            // none does. The list is armed only while an admitted fight plan owns the combat: its stop is then that
            // plan's next decision and is benign. A window with no owning fight is disarmed, so a combat stop never
            // lands without a plan to answer it.
            policy.CombatStopEvents.Clear();
            policy.CombatStopEvents.AddRange(ClockWindowWork.ArmedStops(fightOpen));
        }
        _combatStops.Admitted(status.StateCase == PbClock.Status.StateOneofCase.Stopped ? status.Stopped : null, (long)status.Context.Tick, _clock.Now(), run.Combat);
        var admission = new ClockWindowAdmission(_config.Profile, state.Snapshot, tick, review.Revision, review.InboxCursor, start.MaxTicks);
        var key = ClockWindowWork.Key(admission, fingerprint, start);
        if (_scheduler.Backoff is { } backoff && start.PlayerAccelerated)
        {
            // The window resumes the rate the last critical waves earned.
            var ceiling = backoff.Ceiling;
            if (ceiling != 0 && (start.MaxTicksPerSecond == 0 || ceiling < start.MaxTicksPerSecond))
            {
                start = start with { MaxTicksPerSecond = ceiling };
            }
        }
        var requestId = "";
        // The store returns sequence order. Retain the latest exact logical window, including terminal refusals,
        // rather than allocating another native attempt.
        for (var i = attempts.Count - 1; i >= 0; i--)
        {
            var old = attempts[i].Intent;
            if (old.Key != key)
            {
                continue;
            }
            if (old.Snapshot != state.Snapshot || old.Window != admission || old.Command.Start is not { } oldStart || old.Command.Renew is not null || old.Command.Speed is not null
                || oldStart.Speed != start.Speed || oldStart.LeaseMs != start.LeaseMs || oldStart.MaxTicks != start.MaxTicks || !Equals(oldStart.Policy, start.Policy))
            {
                throw new EvidenceException("clock window key collides with a different journaled window");
            }
            requestId = old.RequestId;
            break;
        }
        if (requestId.Length == 0)
        {
            var sequence = await journal.ReadClockSequenceAsync(call).ConfigureAwait(false);
            requestId = sequence.NextRequestId();
        }
        var intent = new ClockIntent(requestId, key, state.Snapshot, new ClockCommand(Start: start), admission);
        _player.Current(call, epoch);
        var current = _session.State();
        if (!current.Enabled || !current.ObservationKnown || current.Snapshot != state.Snapshot)
        {
            throw new AuthorityException();
        }
        if (_config.Autosave is { } autosave && status.HasActualPaused && status.ActualPaused)
        {
            try
            {
                await autosave(loaded.Context.Identity, (long)status.Context.Tick, call).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // A failed autosave never keeps the window from starting (Go ignored the result).
                ClockEvents.Event("clock-scheduler", "autosave", "autosave before the window failed", ("err", e.Message));
            }
        }
        var (attempt, error) = await _session.CommandClockWindow(new ClockWindowRequest { Intent = intent, Facts = facts, MaxAge = scheduler.MaxAge, CombatMaxTicks = combatMaxTicks, Status = status, StatusAt = started }, call).ConfigureAwait(false);
        run.Attempt = attempt;
        if (error is ConflictException)
        {
            throw new HeldException(error);
        }
        _scheduler.SetRunning(error is null);
        if (error is not null)
        {
            throw error;
        }
        _poll.Running = true;
        _scheduler.SeedLiveDrift((long)status.Context.Tick);
        return true;
    }

    // A window this driver owns is running or stopping: settle it if it is no longer ours, count the work it carries
    // and end it for a hunt fight (Go StepWithReason's running branch, minus the planners the step already ran).
    private async Task<bool> RunningWindowAsync(
        ClockSchedulerResult step,
        WindowRun run,
        PbClock.Status status,
        ControlState state,
        IReadOnlyList<ClockAttempt> attempts,
        IReadOnlyList<ClockEpochObligation> epochs,
        bool obligations,
        CancellationToken call)
    {
        var running = status.StateCase == PbClock.Status.StateOneofCase.Running;
        var actual = running ? status.Running.Epoch : status.Stopping.Epoch;
        var ownedCurrent = false;
        foreach (var owned in epochs)
        {
            if (ClockCoordinator.Terminal(owned.Stage) || !ClockCoordinator.SameEpoch(owned.Epoch, actual))
            {
                continue;
            }
            foreach (var attempt in attempts)
            {
                if (attempt.Intent.RequestId == owned.StartRequestId && attempt.Intent.Snapshot == state.Snapshot)
                {
                    ownedCurrent = true;
                    run.Combat = attempt.Intent.Command.Start?.Policy is { } policy && policy.Mode == PbClock.WatchMode.Combat;
                }
            }
        }
        if ((!running || !ownedCurrent) && obligations)
        {
            run.Cleaned = true;
            await _session.CleanupClock(call).ConfigureAwait(false);
            return true;
        }
        if (!state.Enabled || !ownedCurrent)
        {
            run.Combat = false;
            throw new HeldException("a running window is not owned by the held authority");
        }
        if (running)
        {
            var (live, coupled) = await ClockWindowWork.RunningAsync(_player.Journal, state.Snapshot, new Tick((long)status.Context.Tick), call).ConfigureAwait(false);
            run.Unwatched = live;
            if (coupled.Count > 0)
            {
                // A coupled order is written against what its prerequisite produced, so it was prepared at a stop,
                // against a frozen read of that result. It no longer stops the epoch: the prerequisite's own
                // OperationOutcome row is the wake, the step plans live at once, and the native CAS evidence the
                // dependent's admission carries keeps the order honest against a world that moved since the read.
                run.Coupled = true;
                run.CoupledOrders = coupled.Count;
            }
        }
        step.Running = true;
        _scheduler.SetRunning(running);
        _poll.Running = running;
        if (!running || run.Combat)
        {
            return false;
        }
        // A hunt fight ends a running colony window: prey are not hostile, so the native watcher never stops the
        // window, and only a combat window re-decides the fight. The next step admits that window with the prey watched.
        var (_, fightOpen, prey) = await ClockWindowWork.CombatPlanAsync(_player.Journal, state.Snapshot, call).ConfigureAwait(false);
        if (!fightOpen || prey.Count == 0)
        {
            return false;
        }
        run.Cleaned = true;
        await _session.CleanupClock(call).ConfigureAwait(false);
        return true;
    }

    // Files, once, the actions whose latched hold bound forced the review through (Go journalHoldEscape).
    private void JournalHoldEscape()
    {
        var escapes = _scheduler.Latched.TakeEscapes();
        if (!_scheduler.Config.Worker || escapes.Count == 0)
        {
            return;
        }
        Tel.Decide(new Decision
        {
            Kind = "admission",
            Component = "clock-scheduler",
            Level = TelemetryLevel.Warn,
            Verdict = "admitted",
            Reason = "latched_hold_escape",
            Target = "window",
            Attrs = new Dictionary<string, object?> { ["actions"] = escapes.Select(a => a.Value).ToList(), ["hold_max"] = ClockLatched.HoldMax },
        });
    }
}
