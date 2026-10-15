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
using RimGovernor.Contracts.Wire;
using Common = RimGovernor.Contracts.Wire.Common;
using Authority = RimGovernor.Contracts.Wire.Authority;
using Clock = RimGovernor.Contracts.Wire.Clock;

namespace HomeBridge.BridgeTools
{
    internal static partial class Supervisor
    {
        /// What a run segment carries beyond the hazard state: the observation it
        /// last journalled under, the authority generation Run named, the policy
        /// it was last sent, and the hold's pause facts.
        private sealed class TypedEpoch
        {
            internal Common.ObservationContext LastObservation;
            internal ulong ExpectedGeneration;
            internal Clock.WatchPolicy Policy;
            internal TypedEpoch(Common.ObservationContext origin, ulong expectedGeneration, Clock.WatchPolicy policy)
            { LastObservation = origin; ExpectedGeneration = expectedGeneration; Policy = policy; }
            internal bool PauseRequested;
            internal bool StopPauseVerified;
            // The armed combat event that held this segment, else unset.
            internal Clock.CombatEvent CombatEvent { get; set; }
        }
        // The native dev tick boost behind a Run(Ultrafast) is driven up to the one
        // ceiling with no launch gate. The acceptance launch flag only reports
        // through Status.TestAccelerationAvailable and the timing log; it is
        // read once at startup and never toggled at runtime.
        internal static readonly bool TestAccelerationLaunch = Array.IndexOf(Environment.GetCommandLineArgs(), "-rimgovernor-test-acceleration") >= 0;
        internal static bool TestAccelerationAvailable => TestAccelerationLaunch && BoostAvailable;
        private static bool BoostAvailable => BoostField != null && BoostField.FieldType == typeof(bool);
        private static TypedEpoch TypedOf(State s) => s.Typed ?? throw new InvalidOperationException("Clock run segment is not typed.");
        private static bool StopInvalidTypedAuthority(State s)
        {
            var generation = TypedOf(s).ExpectedGeneration;
            if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
            { Stop(s, "unavailable", "Authorizing native authority is unavailable.", true, null); return true; }
            var result = authority.Check(generation);
            if (result.Success && TypedHooksReadyThisFrame()) return false;
            var reason = result.Snapshot.Reason;
            // Shutdown is the old game unloading under a load: the session
            // changed, not a pause (a small map's fast frames reach this
            // check before the swapped game does).
            var kind = reason == NativeControlRevocationReason.IdentityChanged || reason == NativeControlRevocationReason.Shutdown ? "session_changed"
                : reason == NativeControlRevocationReason.HooksUnavailable || reason == NativeControlRevocationReason.GenerationExhausted
                    || reason == NativeControlRevocationReason.ClockUnavailable || !TypedHooksReady() ? "unavailable" : "external_pause";
            var detail = result.Snapshot.Detail;
            Stop(s, kind, "Authorizing native authority stopped: " + reason + "; " + result.Error
                + (string.IsNullOrEmpty(detail) ? "" : "; " + detail), true, null);
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
                || request.ExpectedGeneration is null || request.ExpectedGeneration.GetValueOrDefault() == 0 || !ValidPolicy(request.Policy))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Run requires an ordinary speed, the current authority generation and a complete bounded watch policy.");
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
                    EnsureJournal();
                    if (_cursor == long.MaxValue) return ProtoBoundary.Fail(Common.FailureCode.CapacityExhausted, "Native clock cursor is exhausted.");
                    var map = ProtoBoundary.ResolveMap(request.Identity ?? throw new InvalidOperationException("Missing identity")) ?? throw new InvalidOperationException("The requested map is not loaded.");
                    foreach (var ids in PolicyIds(request.Policy)) ResolveIds(map, ids);
                }
                catch (Exception error) { ModLog.Error("clock", "Clock run preconditions failed: " + error); return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Native watcher, journal or exact policy pawn identity is unavailable."); }
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
                var failure = RunClock(typed, NativeSpeed(request.Speed.GetValueOrDefault()));
                return new Clock.StatusReply { Outcome = failure != null ? failure : (Clock.StatusReplyOutcome)TypedStatus(context) };
            }
        }

        private static Common.Failure? RunClock(TypedEpoch typed, TimeSpeed speed)
        {
            EnsureJournal();
            if (Current.Game == null || Find.TickManager == null || Find.CurrentMap == null)
                return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "No playable map is loaded.");
            var existing = _state;
            if (existing != null && existing.Active)
            {
                if (!ReferenceEquals(existing.Session, Current.Game) || !ReferenceEquals(existing.Map, Find.CurrentMap))
                    Stop(existing, "session_changed", "Loaded game changed.", false, null);
                else if (existing.PendingKind != null)
                    return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "The clock is entering a hold and its pause has not taken yet.");
                else return ChangeRun(existing, typed, speed);
            }
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
            s.IgnoredHostiles = PawnIds(ResolveIds(map, p.AcknowledgedHostileIds));
            s.IgnoredDowned = PawnIds(ResolveIds(map, p.AcknowledgedDownedColonistIds));
            s.IgnoredInjured = PawnIds(ResolveIds(map, p.AcknowledgedInjuredColonistIds));
            s.SurgicalRecovery = PawnIds(ResolveIds(map, p.SurgicalRecoveryIds));
            s.MedicalRest = PawnIds(ResolveIds(map, p.MedicalRestIds));
            s.InjuryStopCooldownMs = Clamp((int)p.InjuryStopCooldownMs.GetValueOrDefault(), 0, 1800000);
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
            var from = previous != null && ReferenceEquals(previous.Session, Current.Game) ? previous.StopReason ?? "unavailable" : "never_run";
            var tm = Find.TickManager;
            var s = new State(Current.Game, Find.CurrentMap) {
                Active = true, RequestedSpeed = speed, Typed = typed,
                StartTick = tm.TicksGame, LastTick = tm.TicksGame,
                LastProbeTick = tm.TicksGame, LastProbeMs = NowMs(), LastDigestTick = tm.TicksGame
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
                if (s.AlertKeys.Add(a.Key)) s.BaselineAlerts.Add(AlertRow(a));
            foreach (var l in GameWatchReads.Letters()) s.Letters.Add(GameWatchReads.SafeLetterId(l));
            foreach (var m in GameWatchReads.LiveMessages()) s.Messages.Add(GameWatchReads.SafeMessageId(m));
            foreach (var p in GameWatchReads.SpawnedPawns(Find.CurrentMap).Where(GameWatchReads.SafeIsColonist))
            {
                s.Injuries[p.thingIDNumber] = InjurySnapshot.Capture(p);
                var owed = InjuryCooldownRemainingMs(p.thingIDNumber, s.InjuryStopCooldownMs);
                var acked = s.IgnoredInjured.Contains(p.thingIDNumber);
                if (owed <= 0 && !acked) continue;
                s.SuppressedInjuries.Add(new Dictionary<string, object?> {
                    { "pawnId", p.thingIDNumber },
                    { "pawnName", GameWatchReads.SafeName(p) },
                    { "reason", owed > 0 ? "cooldown" : "acknowledged" },
                    { "secondsRemaining", (int)((owed + 999) / 1000) } });
            }
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
                Stop(s, hit.Kind, hit.Detail, true, hit.Payload);
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
                Stop(s, "run_refused", "The requested speed did not take.", true, null);
                return null;
            }
            Add("resumed", "Clock resumed at " + speed + ".", s,
                new Dictionary<string, object?> { { "speed", speed.ToString() }, { "from", from } });
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

        /// Hold: pauses and journals one HoldEntered for the brain's reason. While
        /// already held the first reason stands (nothing is journalled), but a
        /// game someone un-paused is paused again. Needs only the loaded identity.
        internal static Clock.Status TypedHold(Clock.HoldRequest request, Common.ObservationContext context)
        {
            lock (Gate)
            {
                EnsureJournal();
                var kind = HoldKind(request.Reason.GetValueOrDefault());
                var detail = Text(string.IsNullOrWhiteSpace(request.Detail) ? "Held by the brain: " + kind + "." : request.Detail);
                var s = _state;
                var sameSession = s != null && ReferenceEquals(s.Session, Current.Game) && ReferenceEquals(s.Map, Find.CurrentMap);
                if (s != null && s.Active && sameSession && s.PendingKind == null)
                {
                    TypedOf(s).PauseRequested = true;
                    Stop(s, kind, detail, true, null);
                }
                else if (s == null || !sameSession)
                {
                    // Never run (or another game's state): the hold still pauses and journals.
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
                        TypedOf(shell).PauseRequested = true;
                        Stop(shell, kind, detail, true, null);
                    }
                }
                else if (Find.TickManager != null && Find.TickManager.CurTimeSpeed != TimeSpeed.Paused)
                {
                    // Held but the game runs: someone resumed by hand. The first
                    // reason stands; the pause is retaken so the hold is real.
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    var paused = Find.TickManager.CurTimeSpeed == TimeSpeed.Paused;
                    s.PausedAtStop = paused; s.PauseVerified = paused;
                    TypedOf(s).PauseRequested = true; TypedOf(s).StopPauseVerified = paused;
                }
                return TypedStatus(context);
            }
        }

        private static string HoldKind(Clock.HoldReason reason)
        {
            switch (reason)
            {
                case Clock.HoldReason.CoupledOrder: return "coupled_order";
                case Clock.HoldReason.Review: return "review";
                case Clock.HoldReason.Autosave: return "autosave";
                case Clock.HoldReason.GameForced: return "force_paused";
                default: throw new ArgumentOutOfRangeException(nameof(reason));
            }
        }

        /// Heartbeat: the brain's contact for the one ceiling. Not journalled and
        /// not a hold; silence only lowers the ceiling. Reads are not contact.
        internal static Clock.HeartbeatAck TypedHeartbeat()
        {
            lock (Gate)
            {
                var ceiling = SharedCeiling();
                ceiling.Beat(NowMs());
                var s = _state;
                var live = s != null && ReferenceEquals(s.Session, Current.Game);
                var running = live && s!.Active && s.PendingKind == null;
                return new Clock.HeartbeatAck {
                    State = running ? Clock.ClockState.Running : Clock.ClockState.Held,
                    Reason = running ? null : live ? HoldReasonOf(s!.PendingKind ?? s.StopReason) : Clock.HoldReason.NeverRun,
                    CeilingTicksPerSecond = checked((uint)ceiling.TicksPerSecond) };
            }
        }

        internal static Clock.Status TypedStatus(Common.ObservationContext context)
        {
            lock (Gate)
            {
                EnsurePatched();
                var result = new Clock.Status { Context = context, ActualPaused = Find.TickManager.Paused,
                    ObservedSpeed = ObservedSpeed(Find.TickManager.CurTimeSpeed), NativeTickBoundary = TypedHooksReady(),
                    TestAccelerationAvailable = TestAccelerationAvailable,
                    DurableEvents = Journal != null && _state?.StopReason != "event_journal_error" && _state?.PendingKind != "event_journal_error" };
                if (Journal != null) result = result with { NewestCursor = Journal.Newest };
                var s = _state;
                if (s == null || !ReferenceEquals(s.Session, Current.Game))
                    result = result with { State = Clock.ClockState.Held, Reason = Clock.HoldReason.NeverRun };
                else
                {
                    result = result with { TargetSpeed = WireSpeed(s.RequestedSpeed) };
                    if (s.Active && s.PendingKind == null)
                        result = result with { State = Clock.ClockState.Running, PacedTicksPerSecond = (uint)PacedTicksPerSecond(s),
                            PacingReason = PacingReasonOf(s), CeilingTicksPerSecond = (uint)CeilingTicksPerSecond(s) };
                    else
                    {
                        var reasonKind = s.PendingKind ?? s.StopReason;
                        result = result with { State = Clock.ClockState.Held, Reason = HoldReasonOf(reasonKind),
                            Detail = Text(s.PendingKind != null ? s.PendingDetail : s.StopDetail), PauseVerified = TypedOf(s).StopPauseVerified };
                        if (s.StopAtMs.HasValue && s.PendingKind == null) result = result with { HeldAtUnixMs = s.StopAtMs.Value };
                        if (TypedOf(s).CombatEvent != Clock.CombatEvent.Unspecified) result = result with { CombatEvent = TypedOf(s).CombatEvent };
                    }
                    result = result with { MaxProbeTickGap = s.MaxProbeTickGap }; result = result with { ProbeCount = checked((ulong)s.ProbeCount) };
                    if (s.ForcePauseSinceMs != 0) { result = result with { ForcePauseWaitingMs = checked((ulong)Math.Max(0, NowMs() - s.ForcePauseSinceMs)) }; result = result with { ForcePauseKind = NativeEnums.ForcePause(s.ForcePauseKind) }; }
                    foreach (var row in s.BaselineAlerts) result = result with { BaselineAlerts = [.. result.BaselineAlerts, NativeClockEventProjection.Alert(row)] };
                    // Existing suppression baselines lack full injury before/after; report incomplete evidence, never fabricate it.
                }
                result = result with { PausedMs = ClockPauseAccounting.PausedMs(Current.Game) }; result = result with { RunningMs = ClockPauseAccounting.RunningMs(Current.Game) };
                result = result with { ProbeElapsedMs = ClockProbeAccounting.ProbeMs(Current.Game) }; result = result with { DigestElapsedMs = ClockProbeAccounting.DigestMs(Current.Game) };
                result = result with { ProbeTotal = ClockProbeAccounting.Probes(Current.Game) }; result = result with { DigestTotal = ClockProbeAccounting.Digests(Current.Game) };
                result = result with { SessionMaxProbeTickGap = ClockProbeAccounting.MaxProbeTickGap(Current.Game) };
                result = result with { EffectiveTicksPerSecond = EffectiveTicksPerSecond() };
                result = ReportFrames(result);
                foreach (var gap in ClockProbeAccounting.HazardGapsFor(Current.Game))
                    result = result with { HazardGaps = [.. result.HazardGaps, new Clock.HazardGap { HazardClass = gap.Key, MaxTickGap = gap.Value, BoundTicks = HazardBoundTicks(gap.Key), Hooked = HazardHooked(gap.Key) && HazardHooksInstalled }] };
                if (_patchError != null) result = result with { WatcherError = Text(_patchError) };
                return result;
            }
        }

        // The read never waits: the controller polls the page.
        internal static Clock.EventsReply TypedEvents(Clock.EventsRequest request, Common.ObservationContext context)
        {
            lock (Gate) return TypedEventsPage(request, context);
        }
        private static Clock.EventsReply TypedEventsPage(Clock.EventsRequest request, Common.ObservationContext context)
        {
            lock (Gate)
            {
                try
                {
                    var journal = EnsureJournal();
                    if (request.AfterCursor.GetValueOrDefault() > journal.Newest) return new Clock.EventsReply { Outcome = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Cursor exceeds the retained native journal.") };
                    var began = System.Diagnostics.Stopwatch.GetTimestamp();
                    var window = journal.ReadWindow(request.AfterCursor.GetValueOrDefault(), (int)request.Limit.GetValueOrDefault());
                    ObservationWork.Captured("clockEventsJournal", System.Diagnostics.Stopwatch.GetTimestamp() - began, window.Rows.Count, window.Rows.Count);
                    began = System.Diagnostics.Stopwatch.GetTimestamp();
                    var page = new Clock.EventsPage { Context = context, NewestCursor = journal.Newest,
                        NextCursor = window.Next, Gap = window.Lost != 0, LostCount = window.Lost };
                    var events = new List<Clock.ClockEvent>();
                    long previous = request.AfterCursor.GetValueOrDefault();
                    foreach (var row in window.Rows)
                    {
                        // A row whose canonical event does not parse reads as
                        // cursor loss: the page reports the gap and advances
                        // past it, so a controller holds once instead of every
                        // read across the row refusing.
                        if (!row.TryGetValue("canonicalClockEvent", out var encoded) || !(encoded is string canonical)
                            || !TryParseStoredEvent(canonical, out var observed))
                        {
                            page = page with { Gap = true };
                            page = page with { LostCount = checked(page.LostCount.GetValueOrDefault() + 1) };
                            previous = Convert.ToInt64(row["cursor"]);
                            continue;
                        }
                        var cursor = observed.Cursor.GetValueOrDefault();
                        if (!ValidStoredEvent(observed) || cursor <= previous
                            || cursor != Convert.ToInt64(row["cursor"])) throw new InvalidOperationException("Event identity mismatch");
                        // Native's own clock on both sides: the unobserved age of
                        // the row when this page was composed.
                        if (observed.ObservedAtUnixMs is not null) observed = observed with { AgeAtReplyMs = (ulong)Math.Max(0, NowMs() - observed.ObservedAtUnixMs.GetValueOrDefault()) };
                        events.Add(observed); previous = cursor;
                    }
                    ObservationWork.Captured("clockEventsParse", System.Diagnostics.Stopwatch.GetTimestamp() - began, events.Count, window.Rows.Count);
                    page = page with { Events = EquatableArray.From(events), OldestCursor = journal.Base + 1 };
                    return new Clock.EventsReply { Outcome = page };
                }
                catch (Exception error) { ModLog.Error("clock", "Clock event journal read failed: " + error); return new Clock.EventsReply { Outcome = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Clock event journal could not establish complete cursor continuity.") }; }
            }
        }
        private static bool TryParseStoredEvent(string canonical, out Clock.ClockEvent observed)
        {
            try { observed = WireJson.Deserialize<Clock.ClockEvent>(canonical); return true; }
            catch (System.Text.Json.JsonException) { observed = new Clock.ClockEvent(); return false; } // a stored row that is not valid wire JSON is the caller's reported cursor gap
        }
        private static ulong _publishedGeneration;
        private static void OnAuthorityChanged(NativeControlAuthority authority, NativeControlSnapshot snapshot, ulong previous)
        {
            lock (Gate)
            {
                // Coalesce: the newest retained row already names this generation.
                if (snapshot.Generation == _publishedGeneration) return;
                _publishedGeneration = snapshot.Generation;
                Publish("authority_changed", "Native authority generation " + previous + " -> " + snapshot.Generation + " (" + snapshot.Reason + ").",
                    new Dictionary<string, object?> { { "generation", checked((long)snapshot.Generation) }, { "previousGeneration", checked((long)previous) },
                        { "reason", snapshot.Reason.ToString() }, { "active", snapshot.Active } });
            }
        }
        // Rows published outside a run segment take their context from the
        // current map. False means no complete context exists to publish under.
        private static bool AttachOwnerlessEvent(Dictionary<string, object?> row, string kind, string? detail, Dictionary<string, object?>? payload)
        {
            if (!ProtoBoundary.TryReadContext(Find.CurrentMap, out var observed, out _)) return false;
            var result = NativeClockEventProjection.Event(kind, detail, payload, observed, checked(_cursor + 1), NowMs(),
                number => throw new InvalidOperationException("Context-only clock events carry no pawn evidence"));
            if (!ValidStoredEvent(result)) throw new InvalidOperationException("Context-only clock event is incomplete");
            var encoded = WireJson.Serialize(result);
            row["canonicalClockEvent"] = encoded;
            return true;
        }
        private static void AttachTypedEvent(Dictionary<string, object?> row, string kind, string? detail, State s, Dictionary<string, object?>? payload)
        {
            var typed = s.Typed;
            if (typed == null) return;
            var observed = typed.LastObservation;
            // The old segment's last observed tick stays attached to its old identity during replacement.
            if (ReferenceEquals(Current.Game, s.Session) && ReferenceEquals(Find.CurrentMap, s.Map))
            {
                if (!ProtoBoundary.TryReadContext(s.Map, out var current, out _)) throw new InvalidOperationException("Clock event context unavailable");
                observed = current;
            }
            typed.LastObservation = observed;
            var result = NativeClockEventProjection.Event(kind, detail, payload, observed, checked(_cursor + 1), NowMs(),
                number => s.Map.mapPawns.AllPawns.Single(p => p.thingIDNumber == number).GetUniqueLoadID());
            if (kind == "pause_failed" && result.Event?.PauseFailed?.Pending is { } pending)
                result = result with { Event = new Clock.PauseFailed { Pending = pending with { Reason = HoldReasonOf(s.PendingKind) } } };
            if (result.Event?.HoldEntered is { Evidence: Clock.HoldEvidence.UnavailableCase } entered
                && ReferenceEquals(Current.Game, s.Session) && ReferenceEquals(Find.CurrentMap, s.Map))
            {
                var windows = ForcePausingWindows();
                if (windows.Any(window => !ProtoBoundary.IsIdentifier(window))) throw new InvalidOperationException("Invalid pause window identity");
                var pause = new Clock.PauseEvidence { ActualPaused = Find.TickManager.Paused, PauseRequested = typed.PauseRequested,
                    PauseVerified = typed.StopPauseVerified, RequestedSpeed = WireSpeed(s.RequestedSpeed), ActualSpeed = ObservedSpeed(Find.TickManager.CurTimeSpeed),
                    LongEventPending = LongEventHandler.AnyEventNowOrWaiting, ForcePausingWindowIds = EquatableArray.From(windows) };
                if (kind == "dialog_pause")
                {
                    var dialog = ChoiceDialogTools.Pending();
                    if (dialog == null) throw new InvalidOperationException("Dialog pause lacks its force-pausing choice dialog");
                    pause = pause with { Dialog = new Clock.DialogPause { WindowId = dialog.ID, WindowType = Text(dialog.GetType().FullName), Title = Text(ChoiceDialogTools.Title(dialog)) } };
                }
                if (kind == "letter_pause")
                {
                    object? source, id;
                    if (payload == null || !payload.TryGetValue("source", out source) || !Equals(source, "LetterStack.ReceiveLetter")
                        || !payload.TryGetValue("letterId", out id) || !(id is string) || !ProtoBoundary.IsIdentifier((string)id))
                        throw new InvalidOperationException("Letter pause lacks exact native callback attribution");
                    var letterRow = new Clock.Letter { Id = (string)id };
                    var letter = GameWatchReads.Letters().SingleOrDefault(value => value.GetUniqueLoadID() == (string)id);
                    if (letter != null)
                    {
                        letterRow = letterRow with { Label = Text(letter.Label.ToString()) };
                        if (letter.def != null && ProtoBoundary.IsIdentifier(letter.def.defName)) letterRow = letterRow with { DefName = letter.def.defName };
                    }
                    pause = pause with { Letter = letterRow };
                }
                result = result with { Event = entered with { Evidence = new Clock.HoldEvidence.PauseCase(pause) } };
            }
            var encoded = WireJson.Serialize(result);
            row["canonicalClockEvent"] = encoded;
        }
        private static bool ValidStoredEvent(Clock.ClockEvent value)
        {
            var identity = value.Context?.Identity;
            if (value.Cursor is null || value.Cursor <= 0 || value.Context == null || identity == null
                || !ProtoBoundary.IsIdentifier(identity.ColonyId)
                || !ProtoBoundary.IsIdentifier(identity.LoadToken)
                || identity.MapId < 0 || value.Context.Tick < 0
                || value.Context.NativeGeneration is null || value.Context.NativeGeneration == 0
                || value.ObservedAtUnixMs is null || value.ObservedAtUnixMs < 0
                || value.Event is null) return false;
            var body = value.Event;
            if (body.AuthorityChanged != null) return body.AuthorityChanged.Generation is > 0
                && (body.AuthorityChanged.PreviousGeneration is null || body.AuthorityChanged.PreviousGeneration < body.AuthorityChanged.Generation);
            if (body.HoldEntered != null) return ValidStoredHold(body.HoldEntered);
            if (body.PauseFailed != null) return body.PauseFailed.Pending != null && ValidStoredHold(body.PauseFailed.Pending);
            if (body.Resumed != null) return body.Resumed.Speed is { } resumed && OrdinarySpeed(resumed)
                && body.Resumed.From is { } from && Enum.IsDefined(from) && from != Clock.HoldReason.Unspecified;
            if (body.Notification != null) return body.Notification.Source is not null;
            if (body.ObservationInvalidated != null) return body.ObservationInvalidated.Families.Count > 0 && body.ObservationInvalidated.Families.Count <= 8
                && body.ObservationInvalidated.Families.All(family => family != Clock.FactFamily.Unspecified && Enum.IsDefined(family))
                && ValidInvalidationScope(body.ObservationInvalidated);
            return true;
        }
        // The narrowing of an invalidation: at most 64 distinct entity
        // ids and one rectangle with present, nonnegative, ordered corners.
        // Kept (docs/developers/contracts/kept-constants.md).
        internal const int InvalidationEntitiesMax = 64;
        private static bool ValidInvalidationScope(Clock.ObservationInvalidated value)
        {
            if (value.EntityIds.Count > InvalidationEntitiesMax || value.EntityIds.Distinct(StringComparer.Ordinal).Count() != value.EntityIds.Count
                || !value.EntityIds.All(ProtoBoundary.IsIdentifier)) return false;
            var cells = value.Cells;
            if (cells == null) return true;
            return cells.Minimum != null && cells.Maximum != null && cells.Minimum.X is not null && cells.Minimum.Z is not null && cells.Maximum.X is not null && cells.Maximum.Z is not null
                && cells.Minimum.X.GetValueOrDefault() >= 0 && cells.Minimum.Z.GetValueOrDefault() >= 0 && cells.Minimum.X.GetValueOrDefault() <= cells.Maximum.X.GetValueOrDefault() && cells.Minimum.Z.GetValueOrDefault() <= cells.Maximum.Z.GetValueOrDefault();
        }
        private static bool ValidStoredHold(Clock.HoldEntered value) => value.Reason is not null && value.Reason.GetValueOrDefault() != Clock.HoldReason.Unspecified
            && Enum.IsDefined(value.Reason.GetValueOrDefault()) && value.Evidence is not null;
        internal static bool OrdinarySpeed(Clock.Speed speed) => speed == Clock.Speed.Normal || speed == Clock.Speed.Fast || speed == Clock.Speed.Superfast || speed == Clock.Speed.Ultrafast;
        private static TimeSpeed NativeSpeed(Clock.Speed speed) => speed == Clock.Speed.Normal ? TimeSpeed.Normal : speed == Clock.Speed.Fast ? TimeSpeed.Fast : speed == Clock.Speed.Superfast ? TimeSpeed.Superfast : speed == Clock.Speed.Ultrafast ? TimeSpeed.Ultrafast : throw new ArgumentOutOfRangeException(nameof(speed));
        private static Clock.Speed WireSpeed(TimeSpeed speed) => speed == TimeSpeed.Normal ? Clock.Speed.Normal : speed == TimeSpeed.Fast ? Clock.Speed.Fast : speed == TimeSpeed.Superfast ? Clock.Speed.Superfast : speed == TimeSpeed.Ultrafast ? Clock.Speed.Ultrafast : throw new InvalidOperationException("Nonordinary run speed");
        internal static Clock.ObservedSpeed ObservedSpeed(TimeSpeed speed) => speed == TimeSpeed.Paused ? Clock.ObservedSpeed.Paused : speed == TimeSpeed.Normal ? Clock.ObservedSpeed.Normal : speed == TimeSpeed.Fast ? Clock.ObservedSpeed.Fast : speed == TimeSpeed.Superfast ? Clock.ObservedSpeed.Superfast : speed == TimeSpeed.Ultrafast ? Clock.ObservedSpeed.Ultrafast : throw new InvalidOperationException("Unknown native speed");
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
        { yield return policy.AcknowledgedHostileIds; yield return policy.AcknowledgedDownedColonistIds; yield return policy.AcknowledgedInjuredColonistIds; yield return policy.SurgicalRecoveryIds; yield return policy.MedicalRestIds; }
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
                && policy.PredatorMarginCells is not null && Positive(policy.PredatorMarginCells.GetValueOrDefault())
                && (policy.CombatStopEvents.Count == 0 || policy.Mode.GetValueOrDefault() == Clock.WatchMode.Combat)
                && policy.CombatStopEvents.All(e => e != Clock.CombatEvent.Unspecified && Enum.IsDefined(typeof(Clock.CombatEvent), e))
                && policy.CombatStopEvents.Distinct().Count() == policy.CombatStopEvents.Count
                && PolicyIds(policy).All(ids => ids.Count() <= 256 && ids.All(ProtoBoundary.IsIdentifier) && ids.Distinct(StringComparer.Ordinal).Count() == ids.Count());
        }
        private static bool Positive(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value > 0;
        private static bool Fraction(float value) => !float.IsNaN(value) && value >= 0.01f && value <= 1;
        private static Common.Unavailable Unavailable(string detail) => new Common.Unavailable { Reason = Common.UnavailableReason.NotObserved, Detail = detail };
        private static string Text(string? text) => NativeClockEventProjection.Text(text ?? "");
        private static Clock.HoldReason HoldReasonOf(string? kind) => NativeClockEventProjection.HoldReason(kind);
    }
}
