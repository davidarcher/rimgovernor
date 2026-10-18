#nullable enable
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
using RimGovernor.Contracts.Wire;
using Common = RimGovernor.Contracts.Wire.Common;
using Authority = RimGovernor.Contracts.Wire.Authority;
using Clock = RimGovernor.Contracts.Wire.Clock;

namespace HomeBridge.BridgeTools
{
    internal static partial class Supervisor
    {
        /// What a run segment carries beyond the hazard state: the observation it
        /// last published under, the authority generation Run named, the policy
        /// it was last sent, and the hold's pause facts.
        private sealed class TypedEpoch
        {
            internal Common.ObservationContext LastObservation;
            internal ulong ExpectedGeneration;
            internal Clock.WatchPolicy Policy;
            internal TypedEpoch(Common.ObservationContext origin, ulong expectedGeneration, Clock.WatchPolicy policy)
            { LastObservation = origin; ExpectedGeneration = expectedGeneration; Policy = policy; }
            internal bool StopPauseVerified;
            // The armed combat event that held this segment, else unset.
            internal Clock.CombatEvent CombatEvent { get; set; }
        }
        // The native dev tick boost behind a Run(Ultrafast) is driven up to the one
        // ceiling with no launch gate. The acceptance launch flag only reports
        // through the timing log; it is
        // read once at startup and never toggled at runtime.
        internal static readonly bool TestAccelerationLaunch = Array.IndexOf(Environment.GetCommandLineArgs(), "-rimgovernor-test-acceleration") >= 0;
        private static bool BoostAvailable => BoostField != null && BoostField.FieldType == typeof(bool);
        private static TypedEpoch TypedOf(State s) => s.Typed ?? throw new InvalidOperationException("Clock run segment is not typed.");
        private static bool StopInvalidTypedAuthority(State s)
        {
            var generation = TypedOf(s).ExpectedGeneration;
            if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
            { Stop(s, Clock.HoldReason.Unavailable, "Authorizing native authority is unavailable.", true); return true; }
            var result = authority.Check(generation);
            if (result.Success && TypedHooksReadyThisFrame()) return false;
            var reason = result.Snapshot.Reason;
            // Shutdown is the old game unloading under a load: the session
            // changed, not a pause (a small map's fast frames reach this
            // check before the swapped game does).
            var held = reason == NativeControlRevocationReason.IdentityChanged || reason == NativeControlRevocationReason.Shutdown ? Clock.HoldReason.SessionChanged
                : reason == NativeControlRevocationReason.HooksUnavailable || reason == NativeControlRevocationReason.GenerationExhausted
                    || reason == NativeControlRevocationReason.ClockUnavailable || !TypedHooksReady() ? Clock.HoldReason.Unavailable : Clock.HoldReason.ExternalPause;
            var detail = result.Snapshot.Detail;
            Stop(s, held, "Authorizing native authority stopped: " + reason + "; " + result.Error
                + (string.IsNullOrEmpty(detail) ? "" : "; " + detail), true);
            return true;
        }
        private static void CaptureTypedContext(State s)
        {
            if (ReferenceEquals(Current.Game, s.Session) && ReferenceEquals(Find.CurrentMap, s.Map)
                && ProtoBoundary.TryReadContext(s.Map, out var observed, out _)) TypedOf(s).LastObservation = observed;
        }

        internal static Common.Failure? ValidateTypedRun(Clock.RunRequest request)
        {
            if (request.Speed is null || !OrdinarySpeed(request.Speed.GetValueOrDefault()) || request.Policy is null
                || request.ExpectedGeneration is null || request.ExpectedGeneration.GetValueOrDefault() == 0 || !ValidPolicy(request.Policy)
                || request.PlannedHoldSerial is null)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Run requires an ordinary speed, the current authority generation, a complete bounded watch policy and the hold serial its step read.");
            if (request.Speed.GetValueOrDefault() == Clock.Speed.Ultrafast && !BoostAvailable)
                return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "The native tick boost behind Ultrafast is unavailable.");
            lock (Gate)
            {
                if (!GameWatchReads.CoreWatchersAvailable)
                    return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Native safety watchers are unavailable.");
                try
                {
                    EnsurePatched(); LetterPauseHook.EnsurePatched();
                    if (!TypedHooksReady()) return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Exact native clock hooks are unavailable.");
                    var map = ProtoBoundary.ResolveMap(request.Identity ?? throw new InvalidOperationException("Missing identity")) ?? throw new InvalidOperationException("The requested map is not loaded.");
                    ResolveHostiles(map, request.Policy.AcknowledgedHostileIds);
                    foreach (var ids in PolicyIds(request.Policy).Skip(1)) ResolveIds(map, ids);
                }
                catch (Exception error) { ModLog.Error("clock", "Clock run preconditions failed: " + error); return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Native watcher or exact policy pawn identity is unavailable."); }
                return null;
            }
        }

        /// Run: resumes a held clock (the first Run of a loaded game included) at
        /// the target speed, or, while running, changes the speed and the policy
        /// in place. Idempotent. Refused while a long event or a force-pausing
        /// window holds the game.
        internal static Clock.StatusReply TypedRun(Clock.RunRequest request, Common.ObservationContext context)
        {
            lock (Gate)
            {
                var typed = new TypedEpoch(context, request.ExpectedGeneration.GetValueOrDefault(), request.Policy ?? throw new InvalidOperationException("Missing policy"));
                var failure = RunClock(typed, NativeSpeed(request.Speed.GetValueOrDefault()), request.PlannedHoldSerial.GetValueOrDefault());
                return new Clock.StatusReply { Outcome = failure != null ? failure : (Clock.StatusReplyOutcome)TypedStatus(context) };
            }
        }

        private static Common.Failure? RunClock(TypedEpoch typed, TimeSpeed speed, ulong plannedHoldSerial)
        {
            if (Current.Game == null || Find.TickManager == null || Find.CurrentMap == null)
                return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "No playable map is loaded.");
            // The serial as the caller could have read it: retiring another
            // game's segment below enters a hold no status of this game shows.
            var refusal = HoldSerial.Refuses(plannedHoldSerial);
            var existing = _state;
            if (existing != null && existing.Active)
            {
                if (!ReferenceEquals(existing.Session, Current.Game) || !ReferenceEquals(existing.Map, Find.CurrentMap))
                    Stop(existing, Clock.HoldReason.SessionChanged, "Loaded game changed.", false);
                else if (existing.Pending != null)
                    return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "The clock is entering a hold and its pause has not taken yet.");
                else return ChangeRun(existing, typed, speed);
            }
            // Only a resume can release a hold, so only a resume is guarded:
            // a hold entered after the step's read refuses it and stays.
            if (refusal != null) return ProtoBoundary.Fail(Common.FailureCode.Unavailable, refusal);
            return ResumeRun(existing, typed, speed);
        }

        private static void SetBoost(State s, bool on)
        {
            if (on && !s.BoostOwned)
            {
                s.PriorBoost = (bool)BoostField.GetValue(null)!;
                s.BoostOwned = true;
                BoostField.SetValue(null, true);
            }
            else if (!on && s.BoostOwned)
            {
                BoostField.SetValue(null, s.PriorBoost);
                s.BoostOwned = false;
            }
            s.TestAcceleration = on;
        }

        /// Every policy-derived field of a run segment; re-applied whenever Run
        /// resends a changed policy.
        private static void ApplyPolicy(State s, Clock.WatchPolicy p)
        {
            var map = s.Map;
            s.Mode = p.Mode.GetValueOrDefault() == Clock.WatchMode.Colony ? "colony" : "combat";
            s.HostileWithin = ClampFloat(p.HostileWithin.GetValueOrDefault(), 1f, 250f);
            s.HealthDropFraction = ClampFloat(p.HealthDropFraction.GetValueOrDefault(), 0.01f, 1f);
            s.MinHealthFraction = ClampFloat(p.MinHealthFraction.GetValueOrDefault(), 0.01f, 1f);
            s.IgnoredHostiles = PawnIds(ResolveHostiles(map, p.AcknowledgedHostileIds));
            s.IgnoredDowned = PawnIds(ResolveIds(map, p.AcknowledgedDownedColonistIds));
            s.IgnoredInjured = PawnIds(ResolveIds(map, p.AcknowledgedInjuredColonistIds));
            s.SurgicalRecovery = PawnIds(ResolveIds(map, p.SurgicalRecoveryIds));
            s.MedicalRest = PawnIds(ResolveIds(map, p.MedicalRestIds));
            s.InjuryStopCooldownMs = Clamp((int)p.InjuryStopCooldownMs.GetValueOrDefault(), 0, 1800000);
            s.HungerHold = (HungerCategory)(int)p.HungerHoldCategory.GetValueOrDefault();
            s.IgnoredHungry = PawnIds(ResolveIds(map, p.AcknowledgedHungryColonistIds));
        }

        private static Common.Failure? ChangeRun(State s, TypedEpoch typed, TimeSpeed speed)
        {
            var boost = speed == TimeSpeed.Ultrafast;
            if (boost && !BoostAvailable) return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "The native tick boost behind Ultrafast is unavailable.");
            var running = TypedOf(s);
            running.ExpectedGeneration = typed.ExpectedGeneration;
            s.Ceiling.Beat(NowMs());
            if (!Equals(running.Policy, typed.Policy))
            {
                var modeChanged = running.Policy.Mode != typed.Policy.Mode;
                running.Policy = typed.Policy;
                ApplyPolicy(s, typed.Policy);
                if (modeChanged) ResetCombatStops(s);
            }
            if (s.RequestedSpeed == speed && Find.TickManager.CurTimeSpeed == speed) return null;
            var old = s.RequestedSpeed; var oldBoost = s.TestAcceleration;
            // Update expectation and game speed in the same main-thread
            // critical section, so our own change cannot look external.
            SetBoost(s, boost);
            s.RequestedSpeed = speed;
            SupervisorSpeed.Owned(() => Find.TickManager.CurTimeSpeed = speed);
            if (Find.TickManager.CurTimeSpeed != speed)
            {
                s.RequestedSpeed = old; SetBoost(s, oldBoost);
                return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "The requested speed did not take; the clock keeps its previous speed.");
            }
            return null;
        }

        private static Common.Failure? ResumeRun(State? previous, TypedEpoch typed, TimeSpeed speed)
        {
            if (!GameWatchReads.CoreWatchersAvailable)
                return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Alert or transient-message reflection watcher is unavailable; refusing blind play.");
            var boost = speed == TimeSpeed.Ultrafast;
            if (boost && !BoostAvailable) return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "The native tick boost behind Ultrafast is unavailable.");
            try { LetterPauseHook.EnsurePatched(); }
            catch (Exception error) { return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Pause source tracking unavailable: " + error.Message); }
            if (LongEventHandler.AnyEventNowOrWaiting)
                return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "A long event (autosave, map generation) is running or queued; nothing is open to dismiss. An autosave clears in about a second, so retry.");
            // Only a window a human can close refuses a Run. A merely paused
            // clock does not: the speed set below unpauses it.
            if (ColonyNamingTools.Pending() != null)
            {
                try { ColonyNamingTools.ConfirmSuggested(); } catch (Exception) { }
            }
            if (ForcePausingWindows().Count > 0)
                return ProtoBoundary.Fail(Common.FailureCode.Unavailable, ForcePauseDetail());
            var from = previous != null && ReferenceEquals(previous.Session, Current.Game) ? previous.StopReason ?? Clock.HoldReason.Unavailable : Clock.HoldReason.NeverRun;
            var tm = Find.TickManager;
            var (frames, overBudget, _) = FrameAccount();
            var s = new State(Current.Game, Find.CurrentMap) {
                Active = true, RequestedSpeed = speed, Typed = typed,
                StartTick = tm.TicksGame, LastTick = tm.TicksGame, ResumeMs = NowMs(),
                LastProbeTick = tm.TicksGame, LastProbeMs = NowMs(), LastDigestTick = tm.TicksGame,
                FramesAtResume = frames, OverBudgetAtResume = overBudget
            };
            ApplyPolicy(s, typed.Policy);
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
            SetBoost(s, boost);
            // Vanilla's forced slowdown (a danger signal holds the tick rate at 1x for
            // 800 ticks) would cap every fight at 60 TPS whatever speed the clock runs;
            // the brain owns danger handling, so no run segment accepts it.
            s.PriorNeverForceNormal = DebugViewSettings.neverForceNormalSpeed;
            s.NeverForceNormalOwned = true;
            DebugViewSettings.neverForceNormalSpeed = true;
            SupervisorSpeed.Owned(() => Find.TickManager.CurTimeSpeed = speed);
            if (Find.TickManager.CurTimeSpeed != speed)
            {
                Stop(s, Clock.HoldReason.RunRefused, "The requested speed did not take.", true);
                return null;
            }
            Publish(new Resumed(Origin(s), "Clock resumed at " + speed + ".", WireSpeed(speed), from));
            return null;
        }

        internal static Common.Failure? ValidateTypedHold(Clock.HoldRequest request)
        {
            var reason = request.Reason.GetValueOrDefault();
            if (reason != Clock.HoldReason.CoupledOrder && reason != Clock.HoldReason.Review
                && reason != Clock.HoldReason.Autosave && reason != Clock.HoldReason.GameForced)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A brain hold names coupled order, review, autosave or game forced.");
            if (request.Detail != null && request.Detail.Length > 512)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Hold detail is at most 512 characters.");
            return null;
        }

        /// Hold: pauses and publishes one HoldEntered for the brain's reason. While
        /// already held the first reason stands (nothing is published), but a
        /// game someone un-paused is paused again. Needs only the loaded identity.
        internal static Clock.Status TypedHold(Clock.HoldRequest request, Common.ObservationContext context)
        {
            lock (Gate)
            {
                var reason = request.Reason.GetValueOrDefault();
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
                            Active = true, RequestedSpeed = TimeSpeed.Normal,
                            Typed = new TypedEpoch(context, 0, new Clock.WatchPolicy()),
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
        internal static Clock.HeartbeatAck TypedHeartbeat()
        {
            lock (Gate)
            {
                SharedCeiling().Beat(NowMs());
                return new Clock.HeartbeatAck();
            }
        }

        internal static Clock.Status TypedStatus(Common.ObservationContext context)
        {
            lock (Gate)
            {
                EnsurePatched();
                var result = new Clock.Status { Context = context, ActualPaused = Find.TickManager.Paused,
                    NativeTickBoundary = TypedHooksReady(), HoldSerial = HoldSerial.Current };
                var s = _state;
                if (s == null || !ReferenceEquals(s.Session, Current.Game))
                    result = result with { State = Clock.ClockState.Held, Reason = Clock.HoldReason.NeverRun };
                else
                {
                    result = result with { TargetSpeed = WireSpeed(s.RequestedSpeed) };
                    if (s.Active && s.Pending == null)
                        result = result with { State = Clock.ClockState.Running, PacedTicksPerSecond = (uint)PacedTicksPerSecond(s),
                            PacingReason = PacingReasonOf(s), CeilingTicksPerSecond = (uint)CeilingTicksPerSecond(s) };
                    else
                    {
                        result = result with { State = Clock.ClockState.Held, Reason = s.Pending?.Reason ?? s.StopReason,
                            Detail = ProtoBoundary.WireText(s.Pending?.Detail ?? s.StopDetail ?? "") };
                        if (s.StopAtMs.HasValue && s.Pending == null) result = result with { HeldAtUnixMs = s.StopAtMs.Value };
                        if (TypedOf(s).CombatEvent != Clock.CombatEvent.Unspecified) result = result with { CombatEvent = TypedOf(s).CombatEvent };
                    }
                }
                return result with { EffectiveTicksPerSecond = EffectiveTicksPerSecond() };
            }
        }

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
                if (Current.Game == null || Find.CurrentMap == null || !ProtoBoundary.TryReadContext(Find.CurrentMap, out var observed, out _)) return;
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
                && ProtoBoundary.TryReadContext(s.Map, out var current, out _)) typed.LastObservation = current;
            return OriginOf(typed.LastObservation);
        }
        private static ColonyEventOrigin OriginOf(Common.ObservationContext context) =>
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
        internal static bool OrdinarySpeed(Clock.Speed speed) => speed == Clock.Speed.Normal || speed == Clock.Speed.Fast || speed == Clock.Speed.Superfast || speed == Clock.Speed.Ultrafast;
        private static TimeSpeed NativeSpeed(Clock.Speed speed) => speed == Clock.Speed.Normal ? TimeSpeed.Normal : speed == Clock.Speed.Fast ? TimeSpeed.Fast : speed == Clock.Speed.Superfast ? TimeSpeed.Superfast : speed == Clock.Speed.Ultrafast ? TimeSpeed.Ultrafast : throw new ArgumentOutOfRangeException(nameof(speed));
        private static Clock.Speed WireSpeed(TimeSpeed speed) => speed == TimeSpeed.Normal ? Clock.Speed.Normal : speed == TimeSpeed.Fast ? Clock.Speed.Fast : speed == TimeSpeed.Superfast ? Clock.Speed.Superfast : speed == TimeSpeed.Ultrafast ? Clock.Speed.Ultrafast : throw new InvalidOperationException("Nonordinary run speed");
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
        private static IEnumerable<IEnumerable<string>> PolicyIds(Clock.WatchPolicy policy)
        { yield return policy.AcknowledgedHostileIds; yield return policy.AcknowledgedDownedColonistIds; yield return policy.AcknowledgedInjuredColonistIds; yield return policy.SurgicalRecoveryIds; yield return policy.MedicalRestIds; yield return policy.AcknowledgedHungryColonistIds; }
        /// A watched hostile (a hunt's prey included) that has since died is no longer on the map and needs no acknowledging: the run goes on without it.
        private static string ResolveHostiles(Map map, IEnumerable<string> ids) => string.Join(",", ids.SelectMany(id => map.mapPawns.AllPawns.Where(p => p.GetUniqueLoadID() == id)).Select(p => p.thingIDNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        private static string ResolveIds(Map map, IEnumerable<string> ids) => string.Join(",", ids.Select(id => map.mapPawns.AllPawns.Single(p => p.GetUniqueLoadID() == id).thingIDNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        private static bool ValidPolicy(Clock.WatchPolicy policy)
        {
            return policy != null && policy.Mode is not null && (policy.Mode.GetValueOrDefault() == Clock.WatchMode.Colony || policy.Mode.GetValueOrDefault() == Clock.WatchMode.Combat)
                && policy.HealthDropFraction is not null && Fraction(policy.HealthDropFraction.GetValueOrDefault()) && policy.MinHealthFraction is not null && Fraction(policy.MinHealthFraction.GetValueOrDefault())
                && policy.HostileWithin is not null && !float.IsNaN(policy.HostileWithin.GetValueOrDefault()) && policy.HostileWithin.GetValueOrDefault() >= 1 && policy.HostileWithin.GetValueOrDefault() <= 250
                && policy.InjuryStopCooldownMs is not null && policy.InjuryStopCooldownMs.GetValueOrDefault() <= 1800000
                && policy.SeriousSingleHitDamage is not null && Positive(policy.SeriousSingleHitDamage.GetValueOrDefault())
                && policy.SeriousSummaryHealthFloor is not null && Positive(policy.SeriousSummaryHealthFloor.GetValueOrDefault())
                && policy.SeriousBleedRateFloor is not null && Positive(policy.SeriousBleedRateFloor.GetValueOrDefault())
                && policy.SeriousVitalPartFloor is not null && Positive(policy.SeriousVitalPartFloor.GetValueOrDefault())
                && policy.ExplosiveNearMarginCells is not null && Positive(policy.ExplosiveNearMarginCells.GetValueOrDefault())
                && policy.MeleeReachCells is not null && Positive(policy.MeleeReachCells.GetValueOrDefault())
                && policy.InjurySeverityFloorTicks is not null && policy.InjurySeverityFloorTicks.GetValueOrDefault() > 0 && policy.InjurySeverityFloorTicks.GetValueOrDefault() <= int.MaxValue
                && policy.HungerHoldCategory is { } hunger && hunger != RimGovernor.Contracts.Wire.Defs.HungerCategory.Fed && Enum.IsDefined(typeof(RimGovernor.Contracts.Wire.Defs.HungerCategory), hunger)
                && (policy.CombatStopEvents.Count == 0 || policy.Mode.GetValueOrDefault() == Clock.WatchMode.Combat)
                && policy.CombatStopEvents.All(e => e != Clock.CombatEvent.Unspecified && Enum.IsDefined(typeof(Clock.CombatEvent), e))
                && policy.CombatStopEvents.Distinct().Count() == policy.CombatStopEvents.Count
                && PolicyIds(policy).All(ids => ids.Count() <= 256 && ids.All(ProtoBoundary.IsIdentifier) && ids.Distinct(StringComparer.Ordinal).Count() == ids.Count());
        }
        private static bool Positive(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value > 0;
        private static bool Fraction(float value) => !float.IsNaN(value) && value >= 0.01f && value <= 1;
    }
}
