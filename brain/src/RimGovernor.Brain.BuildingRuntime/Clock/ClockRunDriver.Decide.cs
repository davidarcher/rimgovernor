using System.Diagnostics;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.BuildingRuntime;

// The hold state machine, run after the planning step (the admission tail, reduced to the
// continuous clock). Native is Running at the target speed or Held for a named reason, and only a Run ends a hold.
// Every Run names the tick native holds at for the next review (ClockRunConfig.ReviewEveryTicks past the status
// tick), so a step normally starts from a held clock (#3556).
//
//   Running  (a step an authority change, a player speed request or lost events woke) the watch policy the facts imply is diffed against the
//            last one sent and resent when it differs (Run is idempotent and doubles as the speed change); a coupled
//            order newly ready, or an autosave that is due, takes a brain hold. A refused decision never stops a
//            running clock: native holds on what it sees.
//   Held     the autosave runs when one is due, and Run resumes the clock when the decision allows it. The Run quotes
//            the hold serial the step read, so native refuses it when a hold entered after that read. A ColonyLost
//            hold is terminal: it is filed (verdict lost) and never resumed.
//
// Because the step only plans, the status and the emergency census are read
// again here, and the step publishes its own clock_step row while this driver files its own "clock_run" row.

public sealed partial class ClockRunDriver
{
    // What one pass found, for the row it files.
    internal sealed class RunNote
    {
        public string Action { get; set; } = "";

        public string Reason { get; set; } = "";

        public bool Combat { get; set; }

        public bool Fallback { get; set; }

        public int Live { get; set; }

        public int CoupledOrders { get; set; }

        public ClockWatchDecision? Decision { get; set; }

        /// <summary>The fact the colony's first layout plan waits for; empty when it waits for none.</summary>
        public string FirstPlanUnread { get; set; } = "";

        public PbClock.HoldReason? HeldFor { get; set; }

        /// <summary>The review tick the Run named; null when no Run was sent.</summary>
        public long? HoldAt { get; set; }
    }

    /// <summary>
    /// Acts on one finished scheduler step: keeps the clock Running under the right watch, takes the brain's holds and
    /// resumes a held clock. Returns true when it changed the clock (a Run or a Hold) so the planners should work at
    /// once. A refused resume is a <see cref="HeldException"/>.
    /// </summary>
    public async Task<bool> AfterStepAsync(ClockSchedulerResult step, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (step.Deferred)
        {
            return false;
        }
        var note = new RunNote();
        var began = Stopwatch.GetTimestamp();
        Exception? failure = null;
        try
        {
            using var entry = await _player.EnterAsync("clock_run", false, cancellationToken).ConfigureAwait(false);
            return await DecideAsync(step, note, entry.Call, entry.Epoch).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            failure = e;
            throw;
        }
        finally
        {
            if (RunRow(step, note, failure, Stopwatch.GetElapsedTime(began)) is { } row)
            {
                Tel.Decide(row);
            }
        }
    }

    /// <summary>
    /// The clock_run row one pass files; null when it changed and refused nothing. A refused decision is a refused row
    /// even when the refusal held the clock by throwing its <see cref="HeldException"/>: only another failure is a
    /// run_error.
    /// </summary>
    internal static Decision? RunRow(ClockSchedulerResult step, RunNote note, Exception? failure, TimeSpan elapsed)
    {
        if (note.Action.Length == 0 && failure is null)
        {
            return null;
        }
        var attrs = new Dictionary<string, object?>(StringComparer.Ordinal) { ["running"] = step.Running };
        if (note.Live != 0)
        {
            attrs["unwatched"] = note.Live;
        }
        if (note.CoupledOrders > 0)
        {
            attrs["coupled_orders"] = note.CoupledOrders;
        }
        if (note.HeldFor is { } heldFor)
        {
            attrs["held_for"] = Wire.Name(heldFor);
        }
        if (note.HoldAt is { } holdAt)
        {
            attrs["hold_at_tick"] = holdAt;
        }
        if (note.Combat)
        {
            attrs["combat"] = true;
        }
        if (note.Fallback)
        {
            attrs["combat_fallback_speed"] = true;
        }
        var decision = new Decision { Kind = TelemetryKinds.ClockRunKind, Component = "clock-scheduler", Target = "clock", Dur = elapsed, Attrs = attrs };
        if (note.Action == "refused")
        {
            attrs["refused"] = note.Decision?.Refused.Select(r => Wire.Name(r)).ToList();
            decision = decision with { Verdict = "refused", Reason = note.Decision is { Refused.Count: > 0 } d ? Wire.Name(d.Refused[0]) : "run_refused" };
            if (note.Decision?.Refused.Contains(ClockWatchReason.FirstPlan) == true)
            {
                attrs[TelemetryKinds.ClockRunFirstPlanFactKey] = note.FirstPlanUnread;
            }
        }
        else if (failure is not null)
        {
            attrs["error"] = failure.Message;
            decision = decision with { Verdict = "failed", Reason = "run_error" };
        }
        else
        {
            decision = decision with { Verdict = note.Action, Reason = note.Reason };
        }
        return decision;
    }

    // Drops authority and joins the failure with the drop's own.
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

    // One bundle read with the clock status, validated as a step read is.
    private async Task<PbObservations.BundleSnapshot> ReadBundleAsync(CancellationToken call)
    {
        try
        {
            return await _native.ReadStepAsync(new StepRequest { ClockStatus = true }, call).ConfigureAwait(false)
                ?? throw new HeldException("step read returned nothing");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw Failed(e);
        }
    }

    private static bool SameWorldContext(PbObservations.BundleSnapshot loaded, ControlState state, GenerationSnapshot world) =>
        state.ObservationKnown
        && Boundary.World(state.Snapshot, world)
        && loaded.Context?.NativeGeneration is { } generation
        && generation == (ulong)state.Snapshot.Native.Value;

    private async Task<bool> DecideAsync(ClockSchedulerResult step, RunNote note, CancellationToken call, CancellationToken epoch)
    {
        var journal = _player.Journal;
        var started = _clock.Now();
        var loaded = await ReadBundleAsync(call).ConfigureAwait(false);
        var observedAt = _clock.Now();
        var loadedContext = loaded.Context ?? throw new BridgeContractException("step read carried no context");
        var state = _session.State();
        var id = loadedContext.Identity ?? throw new BridgeContractException("step read carried no identity");
        var world = new GenerationSnapshot { Colony = new ColonyId((id.ColonyId ?? "")), Load = new LoadId((id.LoadToken ?? "")), Map = new MapId(id.MapId) };
        if (!SameWorldContext(loaded, state, world))
        {
            ClockEvents.AuthorityLost(
                "observed scope does not match the held authority",
                ("known", state.ObservationKnown),
                ("held", state.Snapshot.ToString()),
                ("observed_world", world.ToString()),
                ("observed_generation", loadedContext.NativeGeneration.GetValueOrDefault()));
            throw Failed(new AuthorityException());
        }
        if (!_combatWorld.SameWorld(state.Snapshot))
        {
            _combatStops.Reset();
            _combatMarkedFor = long.MinValue;
            _combatWorld = state.Snapshot;
        }
        PbClock.Status status;
        try
        {
            status = loaded.ClockStatus ?? throw new InvalidOperationException("clock status required");
            Boundary.Context(status.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ClockEvents.AuthorityLost("bundle clock status failed", ("err", e.Message));
            throw Failed(e);
        }
        var statusContext = status.Context ?? throw new BridgeContractException("clock status carried no context");
        var statusTick = (long)statusContext.Tick;
        if (statusContext.Tick < loadedContext.Tick)
        {
            ClockEvents.AuthorityLost("clock status is behind the scope tick", ("status_tick", statusContext.Tick), ("scope_tick", loadedContext.Tick));
            throw Failed(new EvidenceException("clock status is behind the scope tick"));
        }
        if (status.State is not (PbClock.ClockState.Running or PbClock.ClockState.Held))
        {
            throw new HeldException("clock status is unavailable");
        }
        if (!state.Enabled)
        {
            throw new AuthorityException();
        }
        _player.Current(call, epoch);
        AdoptSpeedRequest(status, id.LoadToken ?? "");
        var tick = new Tick(statusTick);
        var running = status.State == PbClock.ClockState.Running;
        step.Running = running;
        step.ClockTick = statusTick;
        _scheduler.SetRunning(running);

        var (live, coupled) = await ClockRunWork.RunningAsync(journal, state.Snapshot, tick, call).ConfigureAwait(false);
        note.Live = live;
        note.CoupledOrders = coupled.Count;
        if (!_scheduler.Config.Worker)
        {
            coupled = [];
        }
        var newlyCoupled = coupled.Any(a => !_coupledHeld.Contains(a));
        _coupledHeld.IntersectWith(coupled);

        if (running && newlyCoupled)
        {
            // The brain's own holds come first: both end in a Run once the step that follows them has planned.
            _coupledHeld.UnionWith(coupled);
            return await HoldAsync(note, PbClock.HoldReason.CoupledOrder, "coupled_order", call).ConfigureAwait(false);
        }
        if (_config.Autosave is not null)
        {
            // The interval is counted on the game clock from the previous due point. A review that finds the save due
            // saves right there, with the clock already held and paused; a running step (rare) holds for it first.
            if (_autosaveMark == long.MinValue || statusTick < _autosaveMark)
            {
                _autosaveMark = statusTick;
                _autosaveDue = false;
            }
            else if (statusTick - _autosaveMark >= _config.AutosaveEveryTicks)
            {
                _autosaveMark = statusTick;
                _autosaveDue = true;
                if (running)
                {
                    return await HoldAsync(note, PbClock.HoldReason.Autosave, "autosave", call).ConfigureAwait(false);
                }
            }
        }
        var (combatPlan, fightOpen, huntPrey) = await ClockRunWork.CombatPlanAsync(journal, state.Snapshot, call).ConfigureAwait(false);
        if (!running)
        {
            note.HeldFor = status.Reason;
            // A lost colony is a result, not a hazard to answer (#2819): the driver files it and never sends Run, so the
            // clock stays held; nothing opens the game-over letter or creates new wanderers.
            if (status.Reason == PbClock.HoldReason.ColonyLost)
            {
                note.Action = "lost";
                note.Reason = "colony_lost";
                return false;
            }
            // The stop on an armed combat event is the open fight's next decision: the defense planner runs once more
            // against the held world before the clock resumes.
            if (status.Reason == PbClock.HoldReason.CombatEvent && _combatStops.Active && _combatMarkedFor != status.HeldAtUnixMs.GetValueOrDefault())
            {
                _combatMarkedFor = status.HeldAtUnixMs.GetValueOrDefault();
                _scheduler.Queue.CombatStopped();
                note.Action = "hold";
                note.Reason = "combat_event";
                return true;
            }
            // An open hunt's review hold is its next decision too: the hunt arms no event, so without it the formation
            // would be ordered only on the defense planner's own cadence.
            if (ClockCombatStops.HuntReview(status.Reason, fightOpen, huntPrey.Count, step.Defense is not null, status.HeldAtUnixMs.GetValueOrDefault(), _combatMarkedFor))
            {
                _combatMarkedFor = status.HeldAtUnixMs.GetValueOrDefault();
                _scheduler.Queue.CombatStopped();
                note.Action = "hold";
                note.Reason = "hunt_review";
                return true;
            }
            if (_autosaveDue && _config.Autosave is { } autosave && status.ActualPaused is not null && status.ActualPaused.GetValueOrDefault())
            {
                _autosaveDue = false;
                try
                {
                    await autosave(id, statusTick, call).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // A failed autosave never keeps the clock from running.
                    ClockEvents.Event("clock-scheduler", "autosave", "autosave before the run failed", ("err", e.Message));
                }
            }
        }

        // The decision's emergency census is the freshest the driver holds: file it so the store's section is the one
        // the run was decided against, not the step's earlier read of the same world.
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
            ClockScheduler.FactScope(loadedContext),
            FactSection.Emergency,
            new Held<EmergencyFacts> { Value = emergency.Facts, AsOf = (long)emergency.Context.Tick, Complete = colonistsKnown && colonistsComplete && emergency.Facts.DependentPatients.IsKnown, Source = "rimgovernor/observations_read_status" });

        // The defense planner's verdict at this step: only a reported no-squad answer lets a hostile building be
        // watched instead of held; any other outcome, or no planner, keeps the hold.
        var squadUnanswered = Unknown<bool>();
        if (step.Defense is { } defense && !defense.Verdict.IsZero)
        {
            squadUnanswered = Known(defense.Verdict == BuildingReasonNoSquad);
        }
        // A complete sheltering response lets the threat be waited out, watched like a fight's. A step between two
        // rounds reviews ran no rounder of its own (#3556); the scheduler's last review answers for it.
        var sheltered = ClockScheduler.ClockShelterHeld(step.Rounds ?? _scheduler.LastRounds);
        // The first plan's hold is the last ordinary review's: an immediate review derives no plan.
        note.FirstPlanUnread = _scheduler.LastRounds?.FirstPlanUnread ?? "";
        var facts = new ClockWatchFacts
        {
            Current = state.Snapshot,
            Tick = tick,
            SinceStarted = _clock.Now() - started,
            SinceObserved = _clock.Now() - observedAt,
            Emergency = emergencyFacts,
            EventsOpen = _events.Opened,
            Status = new ClockWatchStatus
            {
                Snapshot = state.Snapshot,
                Tick = tick,
                State = running ? ClockWatchState.Running : ClockWatchState.Held,
                NativeTickBoundary = Boundary.FactBool(status.NativeTickBoundary is not null, status.NativeTickBoundary.GetValueOrDefault()),
            },
            CombatPlan = Known(combatPlan),
            HuntPrey = huntPrey,
            SquadUnanswered = squadUnanswered,
            Sheltered = sheltered,
            AcknowledgedHungry = _sentPolicy is { } sent ? [.. sent.AcknowledgedHungryColonistIds.Select(id => new PawnId(id))] : [],
            FirstPlanUnread = note.FirstPlanUnread,
        };
        var decision = ClockWatchRules.EvaluateClockWatch(facts, new ClockWatchLimits { MaxAge = _scheduler.Config.MaxAge });
        note.Decision = decision;
        if (!decision.Run)
        {
            // A refused decision leaves a held clock held and never stops a running one.
            note.Action = "refused";
            if (running)
            {
                return false;
            }
            throw new HeldException($"clock run refused: {string.Join(',', decision.Refused.Items)} (decision {facts.SinceStarted.TotalMilliseconds:F0} ms, max age {_scheduler.Config.MaxAge.TotalMilliseconds:F0} ms)");
        }

        var policy = _config.Policy with
        {
            AcknowledgedDownedColonistIds = [.. decision.Downed.Select(d => d.Value)],
            AcknowledgedHungryColonistIds = [.. decision.Hungry.Select(h => h.Value)],
        };
        if (decision.Mode == ClockWatchMode.Combat)
        {
            // The native watcher holds on any unacknowledged hostile within HostileWithin; a combat watch acknowledges
            // exactly the live hostiles the decision admitted. Native holds on the tick an armed event happens; the
            // list is armed only while an admitted fight plan owns the combat: its hold is then that plan's next
            // decision and is benign. A watch with no owning fight is disarmed, so a combat hold never lands without a
            // plan to answer it.
            note.Combat = true;
            policy = policy with
            {
                Mode = PbClock.WatchMode.Combat,
                AcknowledgedHostileIds = [.. decision.Hostiles.Select(h => h.Value)],
                CombatStopEvents = [.. ClockRunWork.ArmedStops(fightOpen)],
            };
        }
        var speed = FightSpeed(fightOpen);
        if (running && _sentPolicy == policy && _sentSpeed == speed && status.TargetSpeed == speed)
        {
            note.Action = "";
            return false;
        }
        var resumed = !running;
        _combatStops.Admitted(resumed ? status : null, statusTick, _clock.Now(), note.Combat);
        // This resume's own latency may have tripped the gate: the Run that ends the slow hold already carries the fallback.
        speed = FightSpeed(fightOpen);
        note.Fallback = speed != _config.TargetSpeed.Speed;
        _player.Current(call, epoch);
        var current = _session.State();
        if (!current.Enabled || !current.ObservationKnown || current.Snapshot != state.Snapshot)
        {
            throw new AuthorityException();
        }
        // The Run quotes the hold serial of the step's own read, not this decision's later one: a hold native entered
        // after the step planned refuses the resume, and the next step re-plans against it.
        var plannedHoldSerial = step.HoldSerial ?? throw new HeldException("the step read no hold serial");
        // The next review: the cadence past the tick this decision read. A running clock may have passed it by the
        // time the Run lands; native then holds at its next tick, an early review that re-plans from a fresh read.
        var holdAt = statusTick + _config.ReviewEveryTicks;
        PbClock.Status after;
        try
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(call);
            bounded.CancelAfter(_config.CallTimeout);
            after = await _writer.RunAsync(
                new PbClock.RunRequest
                {
                    Identity = Boundary.Identity(state.Snapshot),
                    ExpectedGeneration = (ulong)state.Snapshot.Native.Value,
                    Speed = speed,
                    Policy = policy,
                    PlannedHoldSerial = plannedHoldSerial,
                    HoldAtTick = (ulong)holdAt,
                },
                bounded.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is NativeFailureException or NativeUnavailableException)
        {
            // A refusal (a game-forced pause, a generation that moved, a hold entered after the step's read) is
            // retried at the next step, not a fault.
            throw new HeldException("clock run refused: " + e.Message);
        }
        _sentPolicy = policy;
        _sentSpeed = speed;
        Interlocked.Exchange(ref _ran, 1);
        _scheduler.SetRunning(after.State == PbClock.ClockState.Running);
        note.HoldAt = holdAt;
        note.Action = resumed ? "run" : "adjust";
        note.Reason = resumed ? "resumed" : (status.TargetSpeed == speed ? "watch_changed" : "speed_changed");
        return true;
    }

    // A player speed request the driver has not adopted (a new serial, one of another load, or any pending at its first
    // read) becomes the player's target, as the player API's speed route sets it; the Run this decision sends then
    // carries it (speed_changed). Native reports a request only for the loaded game and starts its serial again with it.
    private void AdoptSpeedRequest(PbClock.Status status, string load)
    {
        if (status.PlayerSpeedRequestSerial is not { } serial || status.PlayerRequestedSpeed is not { } speed || _adoptedSpeedRequest == (load, serial))
        {
            return;
        }
        _config.PlayerSpeed.Set(speed);
        _adoptedSpeedRequest = (load, serial);
    }

    // The speed to ask for: the target, lowered to the fallback while a fight is open and the resume-latency gate has
    // tripped (holds are never added to make up for a slow resume; the fight just runs slower).
    private PbClock.Speed FightSpeed(bool fightOpen) =>
        ClockCombatStops.SpeedFor(_config.TargetSpeed.Speed, _config.CombatFallbackSpeed, fightOpen && _combatStops.Tripped);

    // Takes a brain hold and tells the loop to plan again against the held world.
    private async Task<bool> HoldAsync(RunNote note, PbClock.HoldReason reason, string detail, CancellationToken call)
    {
        try
        {
            await _session.HoldClockAsync(reason, detail, call).ConfigureAwait(false);
        }
        catch (Exception e) when (e is NativeFailureException or NativeUnavailableException)
        {
            throw new HeldException("clock hold refused: " + e.Message);
        }
        note.Action = "hold";
        note.Reason = detail;
        return true;
    }
}
