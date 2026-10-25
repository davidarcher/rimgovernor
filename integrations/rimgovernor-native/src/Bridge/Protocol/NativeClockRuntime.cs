using RimGovernor.Host.Sdk;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;

namespace RimGovernor.Native
{
    internal static partial class Supervisor
    {
        /// What a run segment carries beyond the hazard state: the observation it
        /// last published under, the authority generation Run named, the policy
        /// it was last sent, and the hold's pause facts.
        private sealed class TypedEpoch
        {
            internal RimGovernor.Brain.GameFacts.Common.ObservationContext LastObservation;
            internal ulong ExpectedGeneration;
            internal RimGovernor.Brain.GameOrders.Clock.WatchPolicy? Policy;
            internal RimGovernor.Brain.GameOrders.Clock.WatchPolicy RunPolicy => Policy ?? throw new InvalidOperationException("A held clock has no run policy.");
            internal TypedEpoch(RimGovernor.Brain.GameFacts.Common.ObservationContext origin, ulong expectedGeneration, RimGovernor.Brain.GameOrders.Clock.WatchPolicy? policy)
            { LastObservation = origin; ExpectedGeneration = expectedGeneration; Policy = policy; }
            internal bool StopPauseVerified;
            // The armed combat event that held this segment, else unset.
            internal RimGovernor.Brain.GameFacts.Clock.CombatEvent? CombatEvent;
        }
        // The native dev tick boost behind a Run(Hyperfast) is driven up to its
        // 30x cap (HyperfastCap.cs) and the one ceiling, with no launch gate; a
        // Run(Ultrafast) owns it as off. The acceptance launch flag only reports
        // through the timing log; it is
        // read once at startup and never toggled at runtime.
        internal static readonly bool TestAccelerationLaunch = Array.IndexOf(Environment.GetCommandLineArgs(), "-rimgovernor-test-acceleration") >= 0;
        internal static bool BoostAvailable => BoostField != null && BoostField.FieldType == typeof(bool);
        private static TypedEpoch TypedOf(State s) => s.Typed ?? throw new InvalidOperationException("Clock run segment is not typed.");
        private static bool StopInvalidTypedAuthority(State s)
        {
            var generation = TypedOf(s).ExpectedGeneration;
            if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
            { Stop(s, RimGovernor.Brain.GameFacts.Clock.HoldReason.Unavailable, "Authorizing native authority is unavailable.", true); return true; }
            var result = authority.Check(generation);
            if (result.Success && TypedHooksReadyThisFrame()) return false;
            var reason = result.Snapshot.Reason;
            // Shutdown is the old game unloading under a load: the session
            // changed, not a pause (a small map's fast frames reach this
            // check before the swapped game does).
            var held = reason == NativeControlRevocationReason.IdentityChanged || reason == NativeControlRevocationReason.Shutdown ? RimGovernor.Brain.GameFacts.Clock.HoldReason.SessionChanged
                : reason == NativeControlRevocationReason.HooksUnavailable || reason == NativeControlRevocationReason.GenerationExhausted
                    || reason == NativeControlRevocationReason.ClockUnavailable || !TypedHooksReady() ? RimGovernor.Brain.GameFacts.Clock.HoldReason.Unavailable : RimGovernor.Brain.GameFacts.Clock.HoldReason.ExternalPause;
            var detail = result.Snapshot.Detail;
            Stop(s, held, "Authorizing native authority stopped: " + reason + "; " + result.Error
                + (string.IsNullOrEmpty(detail) ? "" : "; " + detail), true);
            return true;
        }
        private static void CaptureTypedContext(State s)
        {
            if (ReferenceEquals(Current.Game, s.Session) && ReferenceEquals(Find.CurrentMap, s.Map)
                && GameThread.TryReadContext(s.Map, out var observed, out _)) TypedOf(s).LastObservation = observed;
        }

        internal static RimGovernor.Brain.GameFacts.Common.Failure? ValidateTypedRun(RimGovernor.Brain.GameOrders.Clock.RunRequest request)
        {
            if (!ClockRunSpeed.Ordinary(request.Speed) || request.Policy is null
                || request.ExpectedGeneration == 0 || !ValidPolicy(request.Policy)
                || !Enum.IsDefined(request.Speed))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Run requires an ordinary speed, the current authority generation, a complete bounded watch policy, the hold serial its step read and the tick to hold at for review.");
            if (BoostUnavailable(request.Speed) is { } boostless) return boostless;
            lock (Gate)
            {
                if (!GameWatchReads.CoreWatchersAvailable)
                    return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable, "Native safety watchers are unavailable.");
                try
                {
                    EnsurePatched(); LetterPauseHook.EnsurePatched();
                    if (!TypedHooksReady()) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable, "Exact native clock hooks are unavailable.");
                    var map = GameThread.ResolveMap(request.Identity ?? throw new InvalidOperationException("Missing identity")) ?? throw new InvalidOperationException("The requested map is not loaded.");
                    ResolveHostiles(map, request.Policy.AcknowledgedHostileIds);
                    foreach (var ids in PolicyIds(request.Policy).Skip(1)) ResolveIds(map, ids);
                }
                catch (Exception error) { ModLog.Error("clock", "Clock run preconditions failed: " + error); return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable, "Native watcher or exact policy pawn identity is unavailable."); }
                return null;
            }
        }

        /// Run: resumes a held clock (the first Run of a loaded game included) at
        /// the target speed, or, while running, changes the speed, the policy and
        /// the review tick in place. Idempotent. Refused while a long event or a
        /// force-pausing window holds the game. The segment holds for Review on
        /// the first tick at or past the request's hold tick (TickBody).
        internal static RimGovernor.Brain.GameFacts.Clock.Status TypedRun(RimGovernor.Brain.GameOrders.Clock.RunRequest request, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            lock (Gate)
            {
                var typed = new TypedEpoch(context, request.ExpectedGeneration, request.Policy ?? throw new InvalidOperationException("Missing policy"));
                var holdAt = (int)Math.Min(request.HoldAtTick, int.MaxValue);
                var failure = RunClock(typed, request.Speed, request.PlannedHoldSerial, holdAt);
                if (failure != null) throw NativeFaults.Refused(failure);
                return TypedStatus(context);
            }
        }

        private static RimGovernor.Brain.GameFacts.Common.Failure? RunClock(TypedEpoch typed, RimGovernor.Brain.GameFacts.Clock.Speed speed, ulong plannedHoldSerial, int holdAtTick)
        {
            if (Current.Game == null || Find.TickManager == null || Find.CurrentMap == null)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable, "No playable map is loaded.");
            // The serial as the caller could have read it: retiring another
            // game's segment below enters a hold no status of this game shows.
            var refusal = HoldSerial.Refuses(plannedHoldSerial);
            var existing = _state;
            if (existing != null && existing.Active)
            {
                if (!ReferenceEquals(existing.Session, Current.Game) || !ReferenceEquals(existing.Map, Find.CurrentMap))
                    Stop(existing, RimGovernor.Brain.GameFacts.Clock.HoldReason.SessionChanged, "Loaded game changed.", false);
                else if (existing.Pending != null)
                    return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable, "The clock is entering a hold and its pause has not taken yet.");
                else return ChangeRun(existing, typed, speed, holdAtTick);
            }
            // Only a resume can release a hold, so only a resume is guarded:
            // a hold entered after the step's read refuses it and stays.
            if (refusal != null) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable, refusal);
            return ResumeRun(existing, typed, speed, holdAtTick);
        }

        // A Hyperfast Run needs the boost; every other speed runs without it.
        private static RimGovernor.Brain.GameFacts.Common.Failure? BoostUnavailable(RimGovernor.Brain.GameFacts.Clock.Speed speed) =>
            speed == RimGovernor.Brain.GameFacts.Clock.Speed.Hyperfast && !BoostAvailable
                ? GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable, "The native tick boost behind Hyperfast is unavailable.") : null;

        // The boost the speed owns (ClockRunSpeed.Boost): Hyperfast on, Ultrafast
        // off, the others the value the segment found. Without the field there is
        // no boost to own, and only a Hyperfast Run (refused above) needs one.
        private static void SetBoost(State s, RimGovernor.Brain.GameFacts.Clock.Speed speed)
        {
            if (!BoostAvailable) return;
            s.Boost.For(ClockRunSpeed.Boost(speed), () => (bool)(BoostField.GetValue(null) ?? throw new System.InvalidOperationException("Required native reflection value is unavailable.")), value => BoostField.SetValue(null, value));
        }

        /// Every policy-derived field of a run segment; re-applied whenever Run
        /// resends a changed policy.
        private static void ApplyPolicy(State s, RimGovernor.Brain.GameOrders.Clock.WatchPolicy p)
        {
            var map = s.Map;
            s.Mode = p.Mode == RimGovernor.Brain.GameOrders.Clock.WatchMode.Colony ? "colony" : "combat";
            s.HostileWithin = ClampFloat(p.HostileWithin, 1f, 250f);
            s.HealthDropFraction = ClampFloat(p.HealthDropFraction, 0.01f, 1f);
            s.MinHealthFraction = ClampFloat(p.MinHealthFraction, 0.01f, 1f);
            s.IgnoredHostiles = PawnIds(ResolveHostiles(map, p.AcknowledgedHostileIds));
            s.IgnoredDowned = PawnIds(ResolveIds(map, p.AcknowledgedDownedColonistIds));
            s.IgnoredInjured = PawnIds(ResolveIds(map, p.AcknowledgedInjuredColonistIds));
            s.SurgicalRecovery = PawnIds(ResolveIds(map, p.SurgicalRecoveryIds));
            s.MedicalRest = PawnIds(ResolveIds(map, p.MedicalRestIds));
            s.InjuryStopCooldownMs = Clamp((int)p.InjuryStopCooldownMs, 0, 1800000);
            s.HungerHold = (HungerCategory)(int)p.HungerHoldCategory;
            s.IgnoredHungry = PawnIds(ResolveIds(map, p.AcknowledgedHungryColonistIds));
        }

        private static RimGovernor.Brain.GameFacts.Common.Failure? ChangeRun(State s, TypedEpoch typed, RimGovernor.Brain.GameFacts.Clock.Speed speed, int holdAtTick)
        {
            if (BoostUnavailable(speed) is { } boostless) return boostless;
            var running = TypedOf(s);
            running.ExpectedGeneration = typed.ExpectedGeneration;
            // The review moves with the Run: a tick already passed holds at the next tick (TickBody).
            s.HoldAtTick = holdAtTick;
            s.Ceiling.Beat(NowMs());
            if (!Equals(running.Policy, typed.Policy))
            {
                var modeChanged = running.RunPolicy.Mode != typed.RunPolicy.Mode;
                running.Policy = typed.Policy;
                ApplyPolicy(s, typed.RunPolicy);
                if (modeChanged) ResetCombatStops(s);
            }
            var native = ClockRunSpeed.Native(speed);
            if (s.Speed == speed && Find.TickManager.CurTimeSpeed == native) { SpeedRequest.Ran(s.Session, speed); return null; }
            var old = s.Speed;
            // Update expectation and game speed in the same main-thread
            // critical section, so our own change cannot look external.
            // Ultrafast and Hyperfast share a TimeSpeed: between them only the boost changes.
            SetBoost(s, speed);
            s.Speed = speed;
            Find.TickManager.CurTimeSpeed = native;
            if (Find.TickManager.CurTimeSpeed != native)
            {
                s.Speed = old; SetBoost(s, old);
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable, "The requested speed did not take; the clock keeps its previous speed.");
            }
            SpeedRequest.Ran(s.Session, speed);
            return null;
        }

        private static RimGovernor.Brain.GameFacts.Common.Failure? ResumeRun(State? previous, TypedEpoch typed, RimGovernor.Brain.GameFacts.Clock.Speed speed, int holdAtTick)
        {
            if (!GameWatchReads.CoreWatchersAvailable)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable, "Alert or transient-message reflection watcher is unavailable; refusing blind play.");
            if (BoostUnavailable(speed) is { } boostless) return boostless;
            try { LetterPauseHook.EnsurePatched(); }
            catch (Exception error) { return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable, "Pause source tracking unavailable: " + error.Message); }
            if (LongEventHandler.AnyEventNowOrWaiting)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable, "A long event (autosave, map generation) is running or queued; nothing is open to dismiss. An autosave clears in about a second, so retry.");
            // Only a window a human can close refuses a Run. A merely paused
            // clock does not: the speed set below unpauses it.
            if (ColonyNamingTools.Pending() != null)
            {
                try { ColonyNamingTools.ConfirmSuggested(); } catch (Exception) { }
            }
            if (ForcePausingWindows().Count > 0)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable, ForcePauseDetail());
            var from = previous != null && ReferenceEquals(previous.Session, Current.Game) ? previous.StopReason ?? RimGovernor.Brain.GameFacts.Clock.HoldReason.Unavailable : RimGovernor.Brain.GameFacts.Clock.HoldReason.NeverRun;
            var tm = Find.TickManager;
            var (frames, overBudget, _) = FrameAccount();
            var s = new State(Current.Game, Find.CurrentMap) {
                Active = true, Speed = speed, Typed = typed, HoldAtTick = holdAtTick,
                StartTick = tm.TicksGame, LastTick = tm.TicksGame, ResumeMs = NowMs(),
                LastProbeTick = tm.TicksGame, LastProbeMs = NowMs(), LastDigestTick = tm.TicksGame,
                FramesAtResume = frames, OverBudgetAtResume = overBudget
            };
            ApplyPolicy(s, typed.RunPolicy);
            s.Ceiling.Beat(NowMs());
            // Pawn IDs belong to a save; another colony must not inherit
            // this one's injury-stop memory.
            if (!ReferenceEquals(_injuryStopsSession, Current.Game))
            { InjuryStops.Clear(); _priorEpochConsciousHostiles = 0; _injuryStopsSession = Current.Game; }
            // Alerts are baselined like letters and messages, by the stable
            // key, so a count suffix moving cannot look like a new alert.
            foreach (var a in GameWatchReads.ActiveAlertKeys(AlertPriority.High, null))
                s.AlertKeys.Add(a.Key);
            foreach (var l in GameWatchReads.Letters()) s.Letters.Add(GameWatchReads.SafeLetterId(l));
            foreach (var m in GameWatchReads.LiveMessages()) s.Messages.Add(GameWatchReads.SafeMessageId(m));
            foreach (var p in GameWatchReads.SpawnedPawns(Find.CurrentMap).Where(GameWatchReads.SafeIsColonist))
                s.Injuries[p.thingIDNumber] = InjurySnapshot.Capture(p);
            _state = s;
            ClockPauseAccounting.Started(s.Session);
            ClockProbeAccounting.Started(s.Session);
            ResetHazardHooks(s);
            DigestDirty = false;
            var digestBegan = System.Diagnostics.Stopwatch.GetTimestamp();
            PublishFactChanges(s);
            var probeBegan = System.Diagnostics.Stopwatch.GetTimestamp();
            s.Timing.DigestElapsed += probeBegan - digestBegan; s.Timing.Digests++;
            ClockProbeAccounting.Digested(probeBegan - digestBegan);
            var hit = Probe(s);
            var probeTook = System.Diagnostics.Stopwatch.GetTimestamp() - probeBegan;
            s.Timing.ProbeElapsed += probeTook;
            ClockProbeAccounting.Probed(probeTook);
            if (hit != null)
            {
                // Running is itself a request for safety. If the initial probe
                // finds danger, do not leave a running game unattended merely
                // because no speed change was needed: the hold is the answer.
                Stop(s, hit, true);
                return null;
            }
            SetBoost(s, speed);
            // Vanilla's forced slowdown (a danger signal holds the tick rate at 1x for
            // 800 ticks) would cap every fight at 60 TPS whatever speed the clock runs;
            // the brain owns danger handling, so no run segment accepts it.
            s.PriorNeverForceNormal = DebugViewSettings.neverForceNormalSpeed;
            s.NeverForceNormalOwned = true;
            DebugViewSettings.neverForceNormalSpeed = true;
            var native = ClockRunSpeed.Native(speed);
            Find.TickManager.CurTimeSpeed = native;
            if (Find.TickManager.CurTimeSpeed != native)
            {
                Stop(s, RimGovernor.Brain.GameFacts.Clock.HoldReason.RunRefused, "The requested speed did not take.", true);
                return null;
            }
            SpeedRequest.Ran(s.Session, speed);
            Publish(new Resumed(Origin(s), "Clock resumed at " + speed + ".", speed, from));
            return null;
        }

        internal static RimGovernor.Brain.GameFacts.Common.Failure? ValidateTypedHold(RimGovernor.Brain.GameOrders.Clock.HoldRequest request)
        {
            var reason = request.Reason;
            if (reason != RimGovernor.Brain.GameFacts.Clock.HoldReason.CoupledOrder && reason != RimGovernor.Brain.GameFacts.Clock.HoldReason.Review
                && reason != RimGovernor.Brain.GameFacts.Clock.HoldReason.Autosave && reason != RimGovernor.Brain.GameFacts.Clock.HoldReason.GameForced)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "A brain hold names coupled order, review, autosave or game forced.");
            if (request.Detail != null && request.Detail.Length > 512)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Hold detail is at most 512 characters.");
            return null;
        }

        /// Hold: pauses and publishes one HoldEntered for the brain's reason. While
        /// already held the first reason stands (nothing is published), but a
        /// game someone un-paused is paused again. Needs only the loaded identity.
        internal static RimGovernor.Brain.GameFacts.Clock.Status TypedHold(RimGovernor.Brain.GameOrders.Clock.HoldRequest request, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            lock (Gate)
            {
                var reason = request.Reason;
                var detail = string.IsNullOrWhiteSpace(request.Detail) ? "Held by the brain: " + reason + "." : request.Detail;
                var s = _state;
                var sameSession = s != null && ReferenceEquals(s.Session, Current.Game) && ReferenceEquals(s.Map, Find.CurrentMap);
                if (s != null && s.Active && sameSession && s.Pending == null)
                {
                    Stop(s, reason, detail, true);
                }
                else if (s == null || !sameSession)
                {
                    // Never run (or another game's state): the hold still pauses and publishes.
                    if (Current.Game != null && Find.TickManager != null && Find.CurrentMap != null)
                    {
                        var shell = new State(Current.Game, Find.CurrentMap) {
                            Active = true, Speed = RimGovernor.Brain.GameFacts.Clock.Speed.Normal,
                            Typed = new TypedEpoch(context, 0, null),
                            StartTick = Find.TickManager.TicksGame, LastTick = Find.TickManager.TicksGame,
                            LastProbeTick = Find.TickManager.TicksGame, LastProbeMs = NowMs(), LastDigestTick = Find.TickManager.TicksGame,
                            Mode = "colony"
                        };
                        _state = shell;
                        ClockPauseAccounting.Started(shell.Session);
                        ClockProbeAccounting.Started(shell.Session);
                        Stop(shell, reason, detail, true);
                    }
                }
                else if (Find.TickManager != null && Find.TickManager.CurTimeSpeed != TimeSpeed.Paused)
                {
                    // Held but the game runs: someone resumed by hand. The first
                    // reason stands; the pause is retaken so the hold is real.
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    var paused = Find.TickManager.CurTimeSpeed == TimeSpeed.Paused;
                    s.PausedAtStop = paused; s.PauseVerified = paused;
                    TypedOf(s).StopPauseVerified = paused;
                }
                return TypedStatus(context);
            }
        }

        /// Heartbeat: the brain's contact for the one ceiling. Not published and
        /// not a hold; silence only lowers the ceiling. Reads are not contact.
        internal static RimGovernor.Brain.GameFacts.Clock.HeartbeatAck TypedHeartbeat()
        {
            lock (Gate)
            {
                SharedCeiling().Beat(NowMs());
                return new RimGovernor.Brain.GameFacts.Clock.HeartbeatAck();
            }
        }

        internal static RimGovernor.Brain.GameFacts.Clock.Status TypedStatus(RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            lock (Gate)
            {
                EnsurePatched();
                var s = _state;
                RimGovernor.Brain.GameFacts.Clock.HoldState hold = new RimGovernor.Brain.GameFacts.Clock.HoldState.NeverRunCase();
                RimGovernor.Brain.GameFacts.Clock.RunHistory lastRun = new RimGovernor.Brain.GameFacts.Clock.RunHistory.NeverRunCase();
                RimGovernor.Brain.GameFacts.Clock.Pacing pacing = new RimGovernor.Brain.GameFacts.Clock.Pacing.StoppedCase();
                var state = RimGovernor.Brain.GameFacts.Clock.ClockState.Held;
                if (s != null && ReferenceEquals(s.Session, Current.Game))
                {
                    if (TypedOf(s).Policy != null) lastRun = new RimGovernor.Brain.GameFacts.Clock.RunHistory.RanCase(s.Speed);
                    if (s.Active && s.Pending == null)
                    {
                        state = RimGovernor.Brain.GameFacts.Clock.ClockState.Running;
                        hold = new RimGovernor.Brain.GameFacts.Clock.HoldState.RunningCase();
                        pacing = new RimGovernor.Brain.GameFacts.Clock.Pacing.RunningCase((uint)PacedTicksPerSecond(s), PacingReasonOf(s), (uint)CeilingTicksPerSecond(s));
                    }
                    else if (s.Pending is { } pending)
                        hold = new RimGovernor.Brain.GameFacts.Clock.HoldState.PendingCase(pending.Reason);
                    else
                    {
                        var reason = s.StopReason ?? throw new InvalidOperationException("A held clock has no hold reason.");
                        var at = s.StopAtMs ?? throw new InvalidOperationException("A held clock has no hold timestamp.");
                        RimGovernor.Brain.GameFacts.Clock.HoldTrigger trigger = reason == RimGovernor.Brain.GameFacts.Clock.HoldReason.CombatEvent
                            ? new RimGovernor.Brain.GameFacts.Clock.HoldTrigger.CombatCase(TypedOf(s).CombatEvent ?? throw new InvalidOperationException("A combat hold has no event."))
                            : new RimGovernor.Brain.GameFacts.Clock.HoldTrigger.OrdinaryCase();
                        hold = new RimGovernor.Brain.GameFacts.Clock.HoldState.HeldCase(reason, at, trigger);
                    }
                }
                return SpeedRequest.Report(new RimGovernor.Brain.GameFacts.Clock.Status
                {
                    Context = context, State = state, Hold = hold, LastRun = lastRun, Pacing = pacing,
                    HoldSerial = HoldSerial.Current, PlayerRequest = new RimGovernor.Brain.GameFacts.Clock.PlayerSpeedRequest.NoneCase(),
                    ActualPaused = Find.TickManager.Paused, NativeTickBoundary = TypedHooksReady(),
                    EffectiveTicksPerSecond = EffectiveTicksPerSecond()
                }, Current.Game);
            }
        }

        /// The player asked for <paramref name="speed"/> in game (the time
        /// controls call it, on the game thread). It records the speed and bumps
        /// the request serial Status reports, then publishes SpeedRequested to
        /// wake the brain, which adopts the speed and sends a Run; native never
        /// changes its own running speed for it. Normal through Hyperfast only.
        internal static void RequestSpeed(RimGovernor.Brain.GameFacts.Clock.Speed speed)
        {
            lock (Gate)
            {
                var game = Current.Game ?? throw new InvalidOperationException("No game is loaded.");
                var serial = SpeedRequest.Request(game, speed, RunSpeedOf(game));
                // A request with no map to observe it under is still on Status: the next read picks it up.
                if (Find.CurrentMap == null || !GameThread.TryReadContext(Find.CurrentMap, out var observed, out _)) return;
                Publish(new SpeedRequested(OriginOf(observed), "Player requested " + speed + " (request " + serial + ").", speed));
            }
        }

        // The last Run's speed in this game (Status.LastRun), else null.
        private static RimGovernor.Brain.GameFacts.Clock.Speed? RunSpeedOf(object? game)
        {
            var s = _state;
            return game != null && s != null && ReferenceEquals(s.Session, game) ? s.Speed : (RimGovernor.Brain.GameFacts.Clock.Speed?)null;
        }

        /// What the time controls read from the clock, on the game thread: the
        /// player's request no Run at that speed has answered yet, and the last
        /// Run's speed, both of the loaded game.
        internal static (RimGovernor.Brain.GameFacts.Clock.Speed? Pending, RimGovernor.Brain.GameFacts.Clock.Speed? Run) ShownSpeeds()
        {
            lock (Gate)
            {
                var game = Current.Game;
                return (SpeedRequest.Pending(game), RunSpeedOf(game));
            }
        }

        /// The game's own speed, set by the time controls while the bot is not
        /// connected (game thread). The boost follows the run speeds' rule
        /// (ClockRunSpeed.Boost): Ultrafast turns it off, so a leftover boost
        /// cannot run vanilla's Ultrafast at 30x, and Hyperfast turns it on.
        internal static void SetGameSpeed(RimGovernor.Brain.GameFacts.Clock.Speed speed)
        {
            var native = ClockRunSpeed.Native(speed);
            Find.TickManager.CurTimeSpeed = native;
            if (Find.TickManager.CurTimeSpeed == native && BoostAvailable && ClockRunSpeed.Boost(speed) is bool boost)
                BoostField.SetValue(null, boost);
        }

        /// The boost's current value; false where the field does not exist.
        internal static bool BoostOn => BoostAvailable && (bool)(BoostField.GetValue(null) ?? throw new System.InvalidOperationException("Required native reflection value is unavailable."));

        /// The acceptance-only clock accounting (#3416), cumulative for the loaded game session: the pause/run split,
        /// the probe and digest work, the session's widest probe gap, the frame account and, per hazard
        /// class, the declared bound beside the widest gap observed. The harness reads it through
        /// acceptance_read_clock_accounting; production reads none of it. Runs on the game thread.
        internal static string ClockAccountingJson()
        {
            lock (Gate)
            {
                var game = Current.Game;
                var gaps = new System.Text.Json.Nodes.JsonArray();
                foreach (var gap in ClockProbeAccounting.HazardGapsFor(game))
                    gaps.Add(new System.Text.Json.Nodes.JsonObject { ["hazardClass"] = gap.Key, ["boundTicks"] = HazardBoundTicks(gap.Key),
                        ["maxTickGap"] = gap.Value, ["hooked"] = HazardHooked(gap.Key) && HazardHooksInstalled });
                var (frames, overBudget, maxFrameMs) = FrameAccount();
                return new System.Text.Json.Nodes.JsonObject {
                    ["pausedMs"] = ClockPauseAccounting.PausedMs(game), ["runningMs"] = ClockPauseAccounting.RunningMs(game),
                    ["probeElapsedMs"] = ClockProbeAccounting.ProbeMs(game), ["digestElapsedMs"] = ClockProbeAccounting.DigestMs(game),
                    ["probeTotal"] = ClockProbeAccounting.Probes(game), ["digestTotal"] = ClockProbeAccounting.Digests(game),
                    ["sessionMaxProbeTickGap"] = ClockProbeAccounting.MaxProbeTickGap(game),
                    ["pacedFrames"] = frames, ["pacedFramesOverBudget"] = overBudget, ["maxPacedFrameMs"] = maxFrameMs,
                    ["hazardGaps"] = gaps }.ToJsonString();
            }
        }

        private static ulong _publishedGeneration;
        private static void OnAuthorityChanged(NativeControlAuthority authority, NativeControlSnapshot snapshot, ulong previous)
        {
            lock (Gate)
            {
                // Coalesce: the newest published event already names this generation.
                if (snapshot.Generation == _publishedGeneration) return;
                _publishedGeneration = snapshot.Generation;
                // Observed whether or not a run segment exists: the current map supplies the origin, and with no
                // loaded map there is no world to publish under.
                if (Current.Game == null || Find.CurrentMap == null || !GameThread.TryReadContext(Find.CurrentMap, out var observed, out _)) return;
                Publish(new AuthorityChanged(OriginOf(observed), "Native authority generation " + previous + " -> " + snapshot.Generation + " (" + snapshot.Reason + ").",
                    snapshot.Generation, snapshot.Reason.ToString(), snapshot.Active));
            }
        }

        // The only colony-event producer. An event is queued after its context
        // read, so a publication that read causes (a revoke through the
        // authority seam) is queued first. The queue never blocks: a full one
        // drops the event and counts the loss (ColonyEventChannel). Each
        // event is also one OTLP row (ColonyEventRows), lost or not.
        private static void Publish(ColonyEvent value)
        {
            ColonyEvents.Publish(value);
            EmitRow(value);
        }

        // Where a run segment's event was observed: the current context while
        // its game is loaded, else the last one it observed (the old segment's
        // last tick stays attached to its old identity during replacement).
        private static ColonyEventOrigin Origin(State s)
        {
            var typed = TypedOf(s);
            if (ReferenceEquals(Current.Game, s.Session) && ReferenceEquals(Find.CurrentMap, s.Map)
                && GameThread.TryReadContext(s.Map, out var current, out _)) typed.LastObservation = current;
            return OriginOf(typed.LastObservation);
        }
        private static ColonyEventOrigin OriginOf(RimGovernor.Brain.GameFacts.Common.ObservationContext context) =>
            new ColonyEventOrigin(context.Identity.ColonyId, context.Identity.LoadToken, context.Identity.MapId, context.Tick, NowMs());

        // The brain's emitter, attached once BrainHost has started; before it
        // (or without the brain) events have no row.
        private static RimGovernor.Brain.Telemetry.TelemetryEmitter? _rows;
        private static bool _rowFailureLogged;
        internal static void AttachRows(RimGovernor.Brain.Telemetry.TelemetryEmitter emitter)
        {
            lock (Gate) _rows = emitter;
        }
        // Telemetry never fails the publication: a row that cannot be written is logged once and dropped.
        private static void EmitRow(ColonyEvent value)
        {
            if (_rows is not { } emitter) return;
            try { ColonyEventRows.Write(emitter, value); }
            catch (Exception error)
            {
                if (_rowFailureLogged) return;
                _rowFailureLogged = true;
                ModLog.Warn("clock", "Colony event row was not written: " + error);
            }
        }
        // The narrowing of an invalidation: at most 64 entity ids, past which
        // the event names the family alone.
        // Kept (docs/developers/contracts/kept-constants.md).
        internal const int InvalidationEntitiesMax = 64;
        // The tick-boundary hook runs StopInvalidTypedAuthority on every
        // tick, and TypedHooksReady is reflection plus a Harmony patch-info
        // lookup (~1 ms; #265). The hooks cannot come and go within a frame,
        // so the hook path checks them once per frame; the bridge calls keep
        // the exact check.
        private static long frameSerial, hooksCheckedSerial = -1;
        private static bool hooksReadyThisFrame;
        internal static void MarkFrame() => frameSerial++;
        private static bool TypedHooksReadyThisFrame()
        {
            if (frameSerial == 0 || hooksCheckedSerial != frameSerial) { hooksReadyThisFrame = TypedHooksReady(); hooksCheckedSerial = frameSerial; }
            return hooksReadyThisFrame;
        }
        internal static bool TypedHooksReady()
        {
            return new[] { Tuple.Create("TickManagerUpdate", nameof(OnFrame)), Tuple.Create("DoSingleTick", nameof(OnTick)) }.All(pair =>
            {
                var target = AccessTools.Method(typeof(TickManager), pair.Item1);
                var patch = AccessTools.Method(typeof(Supervisor), pair.Item2);
                return target != null && Harmony.GetPatchInfo(target)?.Postfixes.Any(p => p.owner == "homebridge.supervised-play" && p.PatchMethod == patch) == true;
            });
        }
        private static IEnumerable<IEnumerable<string>> PolicyIds(RimGovernor.Brain.GameOrders.Clock.WatchPolicy policy)
        { yield return policy.AcknowledgedHostileIds; yield return policy.AcknowledgedDownedColonistIds; yield return policy.AcknowledgedInjuredColonistIds; yield return policy.SurgicalRecoveryIds; yield return policy.MedicalRestIds; yield return policy.AcknowledgedHungryColonistIds; }
        /// A watched hostile (a hunt's prey included) that has since died is no longer on the map and needs no acknowledging: the run goes on without it.
        private static string ResolveHostiles(Map map, IEnumerable<string> ids) => string.Join(",", ids.SelectMany(id => map.mapPawns.AllPawns.Where(p => p.GetUniqueLoadID() == id)).Select(p => p.thingIDNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        private static string ResolveIds(Map map, IEnumerable<string> ids) => string.Join(",", ids.Select(id => map.mapPawns.AllPawns.Single(p => p.GetUniqueLoadID() == id).thingIDNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        private static bool ValidPolicy(RimGovernor.Brain.GameOrders.Clock.WatchPolicy policy)
        {
            return policy != null && (policy.Mode == RimGovernor.Brain.GameOrders.Clock.WatchMode.Colony || policy.Mode == RimGovernor.Brain.GameOrders.Clock.WatchMode.Combat)
                && Fraction(policy.HealthDropFraction) && Fraction(policy.MinHealthFraction)
                && !float.IsNaN(policy.HostileWithin) && policy.HostileWithin >= 1 && policy.HostileWithin <= 250
                && policy.InjuryStopCooldownMs <= 1800000
                && Positive(policy.SeriousSingleHitDamage)
                && Positive(policy.SeriousSummaryHealthFloor)
                && Positive(policy.SeriousBleedRateFloor)
                && Positive(policy.SeriousVitalPartFloor)
                && Positive(policy.ExplosiveNearMarginCells)
                && Positive(policy.MeleeReachCells)
                && policy.InjurySeverityFloorTicks > 0 && policy.InjurySeverityFloorTicks <= int.MaxValue
                && policy.HungerHoldCategory is { } hunger && hunger != RimGovernor.Brain.GameFacts.Defs.HungerCategory.Fed && Enum.IsDefined(typeof(RimGovernor.Brain.GameFacts.Defs.HungerCategory), hunger)
                && (policy.CombatStopEvents.Count == 0 || policy.Mode == RimGovernor.Brain.GameOrders.Clock.WatchMode.Combat)
                && policy.CombatStopEvents.All(e => Enum.IsDefined(typeof(RimGovernor.Brain.GameFacts.Clock.CombatEvent), e))
                && policy.CombatStopEvents.Distinct().Count() == policy.CombatStopEvents.Count
                && PolicyIds(policy).All(ids => ids.Count() <= 256 && ids.All(GameThread.IsIdentifier) && ids.Distinct(StringComparer.Ordinal).Count() == ids.Count());
        }
        private static bool Positive(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value > 0;
        private static bool Fraction(float value) => !float.IsNaN(value) && value >= 0.01f && value <= 1;
    }
}
