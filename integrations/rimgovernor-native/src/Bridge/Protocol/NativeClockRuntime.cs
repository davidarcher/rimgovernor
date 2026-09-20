#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using System.Linq;
using Google.Protobuf;
using HarmonyLib;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Authority = RimGovernor.Protocol.Authority;
using Clock = RimGovernor.Protocol.Clock;

namespace HomeBridge.BridgeTools
{
    internal static partial class Supervisor
    {
        private sealed class TypedEpoch
        {
            internal readonly Clock.EpochOwner Owner;
            internal readonly Common.ObservationContext Origin;
            internal Common.ObservationContext LastObservation;
            internal readonly Authority.WritePrecondition Authority;
            internal readonly Clock.WatchPolicy Policy;
            internal readonly int LeaseMs;
            internal TypedEpoch(Clock.EpochOwner owner, Common.ObservationContext origin, Authority.WritePrecondition authority, Clock.WatchPolicy policy, int leaseMs)
            { Owner = owner; Origin = origin; LastObservation = origin.Clone(); Authority = authority; Policy = policy; LeaseMs = leaseMs; }
            internal bool PauseRequested;
            internal bool StopPauseVerified;
            internal readonly List<ArmedWatch> Watches = new List<ArmedWatch>();
        }
        private static TypedEpoch? pendingTyped;
        private static bool typedSpeedCall;
        // Test acceleration (the native dev tick boost behind an Ultrafast
        // epoch) is admitted only for a game launched with this argument, which
        // nativeaccept's Prepare and PrepareRendered add and no player launch
        // does. Read once at startup; never toggled at runtime.
        internal static readonly bool TestAccelerationLaunch = Array.IndexOf(Environment.GetCommandLineArgs(), "-rimgovernor-test-acceleration") >= 0;
        internal static bool TestAccelerationAvailable => TestAccelerationLaunch && BoostField != null && BoostField.FieldType == typeof(bool);
        private static readonly Stopwatch TypedClock = Stopwatch.StartNew();
        private static TypedEpoch TypedOf(State s) => s.Typed ?? throw new InvalidOperationException("Clock epoch is not typed.");
        private static long LeaseNow(State s) => s.Typed == null ? NowMs() : TypedClock.ElapsedMilliseconds;
        private static void AttachTypedEpoch(State s)
        {
            if (pendingTyped == null) return;
            s.Typed = pendingTyped;
            s.Typed.Owner.Epoch = s.Epoch;
            s.LeaseExpiresMs = checked(LeaseNow(s) + s.Typed.LeaseMs);
        }
        private static bool StopInvalidTypedAuthority(State s)
        {
            if (s.Typed == null) return false;
            if (LeaseNow(s) >= s.LeaseExpiresMs) { Stop(s, "lease_expired", "Owned clock lease expired.", true, null); return true; }
            var pre = s.Typed.Authority;
            if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
            { Stop(s, "unavailable", "Authorizing native authority is unavailable.", true, null); return true; }
            var result = authority.Check(pre.ExpectedGeneration);
            if (result.Success && TypedHooksReadyThisFrame()) return false;
            var reason = result.Snapshot.Reason;
            var kind = reason == NativeControlRevocationReason.IdentityChanged ? "session_changed"
                : reason == NativeControlRevocationReason.HooksUnavailable || reason == NativeControlRevocationReason.GenerationExhausted
                    || reason == NativeControlRevocationReason.ClockUnavailable || !TypedHooksReady() ? "unavailable" : "external_pause";
            var detail = result.Snapshot.Detail;
            Stop(s, kind, "Authorizing native authority stopped: " + reason + "; " + result.Error
                + (string.IsNullOrEmpty(detail) ? "" : "; " + detail), true, null);
            return true;
        }
        // A typed epoch's lease lapsing is the only native-observable sign that
        // the bot process is gone: it renews every live epoch well inside the
        // lease, so silence past it means a crash, hang or dropped transport.
        // Authority follows the clock down as Disconnect, so a reconnecting or
        // restarted controller observes Inactive and grants Auto afresh instead
        // of finding an Auto it cannot prove it owns. Legacy (untyped) epochs
        // carry no authority precondition and are left alone.
        private static void RevokeDisconnected(State s)
        {
            if (s.Typed == null || !ReferenceEquals(Current.Game, s.Session)) return;
            if (NativeControlAuthority.TryGetForGame(Current.Game, out var authority) && authority != null)
                authority.RevokeExternal(NativeControlRevocationReason.Disconnect);
        }
        private static void CaptureTypedContext(State s)
        {
            if (s.Typed == null) return;
            if (ReferenceEquals(Current.Game, s.Session) && ReferenceEquals(Find.CurrentMap, s.Map)
                && ProtoBoundary.TryReadContext(s.Map, out var observed, out _)) s.Typed.LastObservation = observed;
        }

        internal static Common.Failure? ValidateTypedStart(Clock.StartRequest request)
        {
            if (!request.HasSpeed || !OrdinarySpeed(request.Speed) || !request.HasLeaseMs || request.LeaseMs < 1000 || request.LeaseMs > 30000
                || !request.HasMaxTicks || request.MaxTicks < 1 || request.MaxTicks > 1800000 || !ValidPolicy(request.Policy, request.MaxTicks))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Start requires an ordinary speed, lease1000..30000, tick budget1..1800000 and a complete bounded watch policy.");
            if (request.TestAcceleration && request.Speed != Clock.Speed.Ultrafast)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Test acceleration requires SPEED_ULTRAFAST.");
            if (request.TestAcceleration && !TestAccelerationAvailable)
                return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Test acceleration is admitted only for a game launched with -rimgovernor-test-acceleration.");
            if (!ValidCeiling(request.HasBlindTickBudget, request.BlindTickBudget, request.HasMaxTicksPerSecond, request.MaxTicksPerSecond))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A blind tick budget is 1..1800000 and a tick rate ceiling 1..60000 ticks per second.");
            lock (Gate)
            {
                if (_state != null && _state.Active) return ProtoBoundary.Fail(Common.FailureCode.OwnerConflict, "A clock epoch is already active.");
                if (HomePlayUntilEventTools.ShortGuardRunning || !HomePlayUntilEventTools.CoreWatchersAvailable)
                    return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Native safety watchers are unavailable or another short guard is running.");
                if (LongEventHandler.AnyEventNowOrWaiting || ForcePausingWindows().Count > 0)
                    return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Native long event or modal pause prevents starting owned play.");
                try
                {
                    EnsurePatched(); LetterPauseHook.EnsurePatched();
                    if (!TypedHooksReady()) return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Exact native clock hooks are unavailable.");
                    EnsureJournal();
                    if (_epoch == long.MaxValue || _cursor == long.MaxValue) return ProtoBoundary.Fail(Common.FailureCode.CapacityExhausted, "Native clock epoch or cursor is exhausted.");
                    foreach (var ids in PolicyIds(request.Policy)) ResolveIds(ProtoBoundary.ResolveMap(request.Authority.Identity) ?? throw new InvalidOperationException("The requested map is not loaded."), ids);
                }
                catch (Exception) { return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Native watcher, journal or exact policy pawn identity is unavailable."); }
                return ValidWatchedAttempts(request.Policy, request.Authority.Identity);
            }
        }

        internal static Clock.Status TypedStart(Clock.StartRequest request, Common.ObservationContext context)
        {
            lock (Gate)
            {
                if (pendingTyped != null) throw new InvalidOperationException("Reentrant clock start");
                var policy = request.Policy;
                pendingTyped = new TypedEpoch(new Clock.EpochOwner { ControllerSessionId = request.Authority.Attempt.ControllerSessionId },
                    context.Clone(), request.Authority.Clone(), policy.Clone(), (int)request.LeaseMs);
                var metadata = pendingTyped;
                try
                {
                    Start(metadata.Owner.ControllerSessionId, NativeSpeed(request.Speed), (int)request.LeaseMs,
                        policy.Mode == Clock.WatchMode.Colony ? "colony" : "combat", policy.HealthDropFraction,
                        policy.MinHealthFraction, policy.HostileWithin, ResolveIds(ProtoBoundary.LoadedMap(context), policy.AcknowledgedHostileIds),
                        ResolveIds(ProtoBoundary.LoadedMap(context), policy.AcknowledgedDownedColonistIds), ResolveIds(ProtoBoundary.LoadedMap(context), policy.AcknowledgedInjuredColonistIds),
                        (int)policy.InjuryStopCooldownMs, (int)request.MaxTicks, ResolveIds(ProtoBoundary.LoadedMap(context), policy.SurgicalRecoveryIds), request.TestAcceleration, ResolveIds(ProtoBoundary.LoadedMap(context), policy.MedicalRestIds),
                        (int)request.BlindTickBudget, (int)request.MaxTicksPerSecond);
                    if (_state == null || !ReferenceEquals(_state.Typed, metadata)) throw new InvalidOperationException("Native start did not create the admitted epoch");
                    ArmWatches(_state, context);
                    return TypedStatus(context);
                }
                finally { pendingTyped = null; }
            }
        }

        internal static Common.Failure? ValidateTypedOwner(Clock.OwnedRequest request, bool active)
        {
            lock (Gate)
            {
                if (request?.Owner == null || !request.Owner.HasControllerSessionId || !ProtoBoundary.IsIdentifier(request.Owner.ControllerSessionId)
                    || !request.Owner.HasEpoch || request.Owner.Epoch <= 0)
                    return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "An exact controller and positive epoch are required.");
                var s = _state;
                if (s?.Typed == null || !request.Identity.Equals(s.Typed.Origin.Identity) || !request.Owner.Equals(s.Typed.Owner)
                    || !ReferenceEquals(s.Session, Current.Game) || !ReferenceEquals(s.Map, Find.CurrentMap))
                    return ProtoBoundary.Fail(Common.FailureCode.OwnerConflict, "No matching canonical owned clock epoch exists.");
                if (active && (!s.Active || s.PendingKind != null || LeaseNow(s) >= s.LeaseExpiresMs))
                    return ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "The owned clock epoch is stopped, stopping or expired.");
                return null;
            }
        }
        internal static Clock.Status TypedRenew(Clock.RenewRequest request, Common.ObservationContext context)
        {
            lock (Gate)
            {
                if (ValidateTypedGrant(request.Authority) != null) throw new InvalidOperationException("Clock epoch authority grant changed");
                var s = ActiveState;
                s.LeaseExpiresMs = checked(LeaseNow(s) + request.LeaseMs);
                return TypedStatus(context);
            }
        }
        // An accelerated epoch owns the boost for its whole life: it pauses or
        // lowers its ceiling, it never changes speed.
        internal static Common.Failure? ValidateTypedSpeedChange(Clock.SpeedRequest request)
        {
            lock (Gate)
            {
                var s = _state;
                if (s != null && s.Active && s.TestAcceleration && request.Speed != Clock.Speed.Ultrafast)
                    return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "An accelerated epoch cannot change speed; pause it or change its ceiling instead.");
                if (!ValidCeiling(false, 0, request.HasMaxTicksPerSecond, request.MaxTicksPerSecond))
                    return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A tick rate ceiling is 1..60000 ticks per second.");
                return null;
            }
        }
        private static bool ValidCeiling(bool hasBudget, uint budget, bool hasCeiling, uint ceiling)
            => (!hasBudget || (budget >= 1 && budget <= 1800000)) && (!hasCeiling || (ceiling >= 1 && ceiling <= 60000));
        internal static Clock.Status TypedSpeed(Clock.SpeedRequest request, Common.ObservationContext context)
        {
            lock (Gate)
            {
                if (ValidateTypedGrant(request.Authority) != null) throw new InvalidOperationException("Clock epoch authority grant changed");
                if (ActiveState.TestAcceleration && request.Speed != Clock.Speed.Ultrafast) throw new InvalidOperationException("An accelerated epoch cannot change speed; pause it instead.");
                try
                {
                    typedSpeedCall = true;
                    Speed(request.Epoch.Owner.ControllerSessionId, request.Epoch.Owner.Epoch, NativeSpeed(request.Speed), request.HasMaxTicksPerSecond ? (int?)request.MaxTicksPerSecond : null);
                }
                finally { typedSpeedCall = false; }
                if (ActiveState.RequestedSpeed != NativeSpeed(request.Speed) || Find.TickManager.CurTimeSpeed != NativeSpeed(request.Speed))
                    throw new InvalidOperationException("Native speed did not match admitted change");
                return TypedStatus(context);
            }
        }
        // An epoch belongs to the grant which started it. Reacquisition, even by
        // the same controller before the next watcher tick, cannot adopt old work.
        internal static Common.Failure? ValidateTypedGrant(Authority.WritePrecondition? requested)
        {
            lock (Gate)
            {
                var state = _state; var original = state?.Typed?.Authority;
                if (state == null || original == null || requested == null)
                    return ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "The epoch has no matching authorizing grant.");
                if (!state.Active || state.PendingKind != null || LeaseNow(state) >= state.LeaseExpiresMs)
                    return ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "The original epoch is stopped, stopping or expired.");
                try
                {
                    if (!TypedHooksReady()) return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Required native clock enforcement hooks are unavailable.");
                }
                catch (Exception) { return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Native clock enforcement hooks could not be verified."); }
                if (requested.ExpectedGeneration != original.ExpectedGeneration)
                    return ProtoBoundary.Fail(Common.FailureCode.StaleGeneration, "A replacement authority generation cannot adopt an existing epoch.");
                if (requested.Attempt == null
                    || requested.Attempt.ControllerSessionId != original.Attempt.ControllerSessionId
                    || !original.Identity.Equals(requested.Identity))
                    return ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Clock control must retain the epoch's original authority grant.");
                return null;
            }
        }
        internal static Clock.Status TypedPause(Common.ObservationContext context)
        {
            lock (Gate)
            {
                var s = _state;
                if (s != null && s.Active)
                {
                    TypedOf(s).PauseRequested = true;
                    Stop(s, "requested_pause", "Paused by the canonical epoch owner.", true, null);
                }
                else if (s != null && s.Typed != null && ReferenceEquals(s.Session, Current.Game) && ReferenceEquals(s.Map, Find.CurrentMap)
                    && Find.TickManager != null && Find.TickManager.CurTimeSpeed != TimeSpeed.Paused)
                {
                    // The epoch is stopped but the game runs: the player un-paused
                    // after an external pause and drove the speed by hand. The
                    // owner re-takes the clock before admitting the next window
                    // (#601): pause here, so the stopped status it reads next
                    // verifies the pause a start needs.
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    var paused = Find.TickManager.CurTimeSpeed == TimeSpeed.Paused;
                    s.PauseVerified = paused;
                    s.Typed.PauseRequested = true;
                    s.Typed.StopPauseVerified = paused;
                }
                return TypedStatus(context);
            }
        }

        internal static Clock.Status TypedStatus(Common.ObservationContext context)
        {
            lock (Gate)
            {
                EnsurePatched();
                NoteControllerRead(_state);
                var result = new Clock.Status { Context = context.Clone(), ActualPaused = Find.TickManager.Paused,
                    ObservedSpeed = ObservedSpeed(Find.TickManager.CurTimeSpeed), NativeTickBoundary = TypedHooksReady(),
                    EvidenceCompleteness = new Common.PageInfo { Complete = true }, TestAccelerationAvailable = TestAccelerationAvailable,
                    DurableEvents = Journal != null && _state?.StopReason != "event_journal_error" && _state?.PendingKind != "event_journal_error" };
                if (Journal != null) result.NewestCursor = Journal.Newest;
                var s = _state;
                if (s == null) result.NeverStarted = new Clock.NeverStarted();
                else if (s.Typed == null) result.Unavailable = Unavailable("Legacy supervisor has no canonical epoch owner or admission origin.");
                else
                {
                    var epoch = Epoch(s);
                    if (s.Active && s.PendingKind != null) result.Stopping = new Clock.Stopping { Epoch = epoch, PendingReason = StopReason(s.PendingKind), Detail = Text(s.PendingDetail), ActualPaused = Find.TickManager.Paused };
                    else if (s.Active) result.Running = new Clock.Running { Epoch = epoch };
                    else
                    {
                        result.Stopped = new Clock.Stopped { Epoch = epoch, Reason = StopReason(s.StopReason), Detail = Text(s.StopDetail),
                            ActualPaused = Find.TickManager.Paused, PauseRequested = TypedOf(s).PauseRequested, PauseVerified = TypedOf(s).StopPauseVerified };
                        if (s.StopAtMs.HasValue) result.Stopped.StoppedAtUnixMs = s.StopAtMs.Value;
                    }
                    result.MaxProbeTickGap = s.MaxProbeTickGap; result.ProbeCount = checked((ulong)s.ProbeCount);
                    if (s.ForcePauseSinceMs != 0) { result.ForcePauseWaitingMs = checked((ulong)Math.Max(0, NowMs() - s.ForcePauseSinceMs)); result.ForcePauseKind = Text(s.ForcePauseKind); }
                    if (s.BaselineAlerts.Count > 256 || s.SuppressedInjuries.Count > 256) throw new InvalidOperationException("Clock evidence exceeds bounded collection");
                    foreach (var row in s.BaselineAlerts) result.BaselineAlerts.Add(NativeClockEventProjection.Alert(row));
                    // Existing suppression baselines lack full injury before/after; report incomplete evidence, never fabricate it.
                    result.EvidenceCompleteness = new Common.PageInfo { Complete = s.SuppressedInjuries.Count == 0 };
                }
                result.PausedMs = ClockPauseAccounting.PausedMs(Current.Game); result.RunningMs = ClockPauseAccounting.RunningMs(Current.Game);
                if (_patchError != null) result.WatcherError = Text(_patchError);
                return result;
            }
        }
        // Typed starts always carry a tick budget, so a typed epoch always has a deadline.
        private static long Deadline(State s) => s.TickDeadline ?? throw new InvalidOperationException("Typed epoch has no tick deadline.");
        private static Clock.Epoch Epoch(State s) => new Clock.Epoch { Owner = TypedOf(s).Owner.Clone(), Origin = TypedOf(s).Origin.Clone(),
            RequestedSpeed = WireSpeed(s.RequestedSpeed), Policy = TypedOf(s).Policy.Clone(), StartTick = s.StartTick,
            TickDeadline = Deadline(s), LastTick = s.LastTick, TestAcceleration = s.TestAcceleration, LeaseRemainingMs = s.Active ? (uint)Math.Min(30000, Math.Max(0, s.LeaseExpiresMs - LeaseNow(s))) : 0,
            BlindTickBudget = (uint)s.BlindTickBudget, MaxTicksPerSecond = (uint)s.MaxTicksPerSecond, RegulatedTicksPerSecond = (uint)s.RegulatedTicksPerSecond };

        internal static Clock.EventsReply TypedEvents(Clock.EventsRequest request, Common.ObservationContext context)
            => TypedEvents(request, context, 0, out _);
        // A long poll registers its waiter in the same Gate section that found
        // the page empty; the caller awaits `wake` off the main thread and then
        // reads again without waiting. A page with rows or loss never waits.
        internal static Clock.EventsReply TypedEvents(Clock.EventsRequest request, Common.ObservationContext context, int waitMs, out Task<bool>? wake)
        {
            wake = null;
            lock (Gate)
            {
                AcknowledgeRows(_state, request.AfterCursor);
                var reply = TypedEventsPage(request, context);
                if (waitMs > 0 && reply.Page != null && reply.Page.Events.Count == 0 && !reply.Page.Gap
                    && TryRegisterWaiter(request.AfterCursor, out var registered)) wake = registered;
                return reply;
            }
        }
        private static Clock.EventsReply TypedEventsPage(Clock.EventsRequest request, Common.ObservationContext context)
        {
            lock (Gate)
            {
                try
                {
                    var journal = EnsureJournal();
                    if (request.AfterCursor > journal.Newest) return new Clock.EventsReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Cursor exceeds the retained native journal.") };
                    var window = journal.ReadWindow(request.AfterCursor, (int)request.Limit);
                    var page = new Clock.EventsPage { Context = context.Clone(), NewestCursor = journal.Newest,
                        NextCursor = window.Next, Gap = window.Lost != 0, LostCount = window.Lost };
                    long previous = request.AfterCursor;
                    foreach (var row in window.Rows)
                    {
                        if (!row.TryGetValue("canonicalClockEvent", out var encoded) || !(encoded is string canonical))
                            return new Clock.EventsReply { Failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Retained legacy event lacks canonical ownership and original observation context.") };
                        var observed = Clock.Event.Parser.ParseJson(canonical);
                        if (!ValidStoredEvent(observed) || observed.Cursor <= previous
                            || observed.Cursor != Convert.ToInt64(row["cursor"])) throw new InvalidOperationException("Event identity mismatch");
                        // Native's own clock on both sides: the unobserved age of
                        // the row when this page was composed (#621).
                        if (observed.HasObservedAtUnixMs) observed.AgeAtReplyMs = (ulong)Math.Max(0, NowMs() - observed.ObservedAtUnixMs);
                        page.Events.Add(observed); previous = observed.Cursor;
                    }
                    // Only a read beginning at zero establishes the earliest retained row.
                    if (request.AfterCursor == 0 && page.Events.Count != 0) page.OldestCursor = page.Events[0].Cursor;
                    else if (journal.Newest == 0) page.OldestCursor = 0;
                    return new Clock.EventsReply { Page = page };
                }
                catch (Exception) { return new Clock.EventsReply { Failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Clock event journal could not establish complete cursor continuity.") }; }
            }
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
        // Epoch-less rows carry no owner and take their context from the
        // current map. False means no complete context exists to publish under.
        private static bool AttachOwnerlessEvent(Dictionary<string, object?> row, string kind, string? detail, Dictionary<string, object?>? payload)
        {
            if (!ProtoBoundary.TryReadContext(Find.CurrentMap, out var observed, out _)) return false;
            var result = NativeClockEventProjection.Event(kind, detail, payload, observed, null, checked(_cursor + 1), NowMs(), null,
                number => throw new InvalidOperationException("Ownerless clock events carry no pawn evidence"));
            if (!ValidStoredEvent(result)) throw new InvalidOperationException("Ownerless clock event is incomplete");
            var encoded = JsonFormatter.Default.Format(result);
            if (new System.Text.UTF8Encoding(false, true).GetByteCount(encoded) > ProtoBoundary.MaximumEnvelopeBytes)
                throw new InvalidOperationException("Canonical clock event exceeds the bounded envelope");
            row["canonicalClockEvent"] = encoded;
            return true;
        }
        private static void AttachTypedEvent(Dictionary<string, object?> row, string kind, string? detail, State s, Dictionary<string, object?>? payload)
        {
            var typed = s.Typed;
            if (typed == null) return;
            var observed = typed.LastObservation.Clone();
            // The old epoch's last observed tick stays attached to its old identity during replacement.
            if (ReferenceEquals(Current.Game, s.Session) && ReferenceEquals(Find.CurrentMap, s.Map))
            {
                if (!ProtoBoundary.TryReadContext(s.Map, out var current, out _)) throw new InvalidOperationException("Clock event context unavailable");
                observed = current;
            }
            typed.LastObservation = observed.Clone();
            var result = NativeClockEventProjection.Event(kind, detail, payload, observed, typed.Owner, checked(_cursor + 1), NowMs(),
                kind == "started" ? Epoch(s) : null, number => s.Map.mapPawns.AllPawns.Single(p => p.thingIDNumber == number).GetUniqueLoadID());
            if (kind == "pause_failed") result.PauseFailed.Pending.Reason = StopReason(s.PendingKind);
            if (kind == "tick_budget") result.Stopped.Budget = new Clock.BudgetReached { StartTick = s.StartTick, TickDeadline = Deadline(s), ActualTick = s.LastTick };
            if (result.Stopped != null && result.Stopped.EvidenceCase == Clock.StopEvent.EvidenceOneofCase.Unavailable
                && ReferenceEquals(Current.Game, s.Session) && ReferenceEquals(Find.CurrentMap, s.Map))
            {
                result.Stopped.Pause = new Clock.PauseEvidence { ActualPaused = Find.TickManager.Paused, PauseRequested = typed.PauseRequested,
                    PauseVerified = typed.StopPauseVerified, RequestedSpeed = WireSpeed(s.RequestedSpeed), ActualSpeed = ObservedSpeed(Find.TickManager.CurTimeSpeed),
                    LongEventPending = LongEventHandler.AnyEventNowOrWaiting };
                var windows = ForcePausingWindows();
                if (windows.Count > 256) throw new InvalidOperationException("Clock pause evidence exceeds window bound");
                if (windows.Any(window => !ProtoBoundary.IsIdentifier(window))) throw new InvalidOperationException("Invalid pause window identity");
                result.Stopped.Pause.ForcePausingWindowIds.Add(windows);
                if (kind == "dialog_pause")
                {
                    var dialog = ChoiceDialogTools.Pending();
                    if (dialog == null) throw new InvalidOperationException("Dialog pause lacks its force-pausing choice dialog");
                    result.Stopped.Pause.Dialog = new Clock.DialogPause { WindowId = dialog.ID, WindowType = Text(dialog.GetType().FullName), Title = Text(ChoiceDialogTools.Title(dialog)) };
                }
                if (kind == "letter_pause")
                {
                    object? source, id;
                    if (payload == null || !payload.TryGetValue("source", out source) || !Equals(source, "LetterStack.ReceiveLetter")
                        || !payload.TryGetValue("letterId", out id) || !(id is string) || !ProtoBoundary.IsIdentifier((string)id))
                        throw new InvalidOperationException("Letter pause lacks exact native callback attribution");
                    result.Stopped.Pause.Letter = new Clock.Letter { Id = (string)id };
                    var letter = HomePlayUntilEventTools.Letters().SingleOrDefault(value => value.GetUniqueLoadID() == (string)id);
                    if (letter != null)
                    {
                        result.Stopped.Pause.Letter.Label = Text(letter.Label.ToString());
                        if (letter.def != null && ProtoBoundary.IsIdentifier(letter.def.defName)) result.Stopped.Pause.Letter.DefName = letter.def.defName;
                    }
                }
            }
            var encoded = JsonFormatter.Default.Format(result);
            if (new System.Text.UTF8Encoding(false, true).GetByteCount(encoded) > ProtoBoundary.MaximumEnvelopeBytes)
                throw new InvalidOperationException("Canonical clock event exceeds the bounded envelope");
            row["canonicalClockEvent"] = encoded;
        }
        private static bool ValidStoredEvent(Clock.Event value)
        {
            var identity = value.Context?.Identity;
            if (!value.HasCursor || value.Cursor <= 0 || value.Context == null || identity == null
                || !identity.HasColonyId || !ProtoBoundary.IsIdentifier(identity.ColonyId)
                || !identity.HasLoadToken || !ProtoBoundary.IsIdentifier(identity.LoadToken)
                || !identity.HasMapId || identity.MapId < 0 || !value.Context.HasTick || value.Context.Tick < 0
                || !value.Context.HasNativeGeneration || value.Context.NativeGeneration == 0
                || !value.HasObservedAtUnixMs || value.ObservedAtUnixMs < 0
                || value.EventCase == Clock.Event.EventOneofCase.None) return false;
            if (value.Owner == null ? value.AuthorityChanged == null
                : !value.Owner.HasControllerSessionId || !ProtoBoundary.IsIdentifier(value.Owner.ControllerSessionId) || !value.Owner.HasEpoch || value.Owner.Epoch <= 0) return false;
            if (value.AuthorityChanged != null) return value.AuthorityChanged.HasGeneration && value.AuthorityChanged.Generation > 0
                && (!value.AuthorityChanged.HasPreviousGeneration || value.AuthorityChanged.PreviousGeneration < value.AuthorityChanged.Generation);
            if (value.OperationOutcome != null) return ValidStoredOutcome(value.OperationOutcome);
            if (value.Stopped != null) return ValidStoredStop(value.Stopped);
            if (value.PauseFailed != null) return value.PauseFailed.Pending != null && ValidStoredStop(value.PauseFailed.Pending);
            if (value.Started != null) return value.Started.Epoch != null && value.Started.Epoch.Owner != null
                && value.Started.Epoch.Owner.Equals(value.Owner) && value.Started.Epoch.Origin != null;
            if (value.SpeedChanged != null) return value.SpeedChanged.HasSpeed && OrdinarySpeed(value.SpeedChanged.Speed);
            if (value.Notification != null) return value.Notification.SourceCase != Clock.Notification.SourceOneofCase.None;
            if (value.ObservationInvalidated != null) return value.ObservationInvalidated.Families.Count > 0 && value.ObservationInvalidated.Families.Count <= 8
                && value.ObservationInvalidated.Families.All(family => family != Clock.FactFamily.Unspecified && Enum.IsDefined(typeof(Clock.FactFamily), family))
                && ValidInvalidationScope(value.ObservationInvalidated);
            return true;
        }
        // The narrowing of an invalidation (#359): at most 64 distinct entity
        // ids and one rectangle with present, nonnegative, ordered corners.
        internal const int InvalidationEntitiesMax = 64;
        private static bool ValidInvalidationScope(Clock.ObservationInvalidated value)
        {
            if (value.EntityIds.Count > InvalidationEntitiesMax || value.EntityIds.Distinct(StringComparer.Ordinal).Count() != value.EntityIds.Count
                || !value.EntityIds.All(ProtoBoundary.IsIdentifier)) return false;
            var cells = value.Cells;
            if (cells == null) return true;
            return cells.Minimum != null && cells.Maximum != null && cells.Minimum.HasX && cells.Minimum.HasZ && cells.Maximum.HasX && cells.Maximum.HasZ
                && cells.Minimum.X >= 0 && cells.Minimum.Z >= 0 && cells.Minimum.X <= cells.Maximum.X && cells.Minimum.Z <= cells.Maximum.Z;
        }
        private static bool ValidStoredStop(Clock.StopEvent value) => value.HasReason && value.Reason != Clock.StopReason.Unspecified
            && Enum.IsDefined(typeof(Clock.StopReason), value.Reason) && value.EvidenceCase != Clock.StopEvent.EvidenceOneofCase.None
            && (value.Watch == null || value.Watch.Outcome != null && ValidStoredOutcome(value.Watch.Outcome) && value.Watch.HasTickDeadline && value.Watch.TickDeadline > 0);
        private static bool ValidStoredOutcome(Clock.OperationOutcome value) => ValidWatchKey(value.Attempt) && value.HasLatchedTick && value.LatchedTick >= 0
            && value.OutcomeCase != Clock.OperationOutcome.OutcomeOneofCase.None;
        private static bool ValidWatchKey([NotNullWhen(true)] Common.AttemptKey? key) => key != null && key.HasControllerSessionId && ProtoBoundary.IsIdentifier(key.ControllerSessionId)
            && key.HasActionId && ProtoBoundary.IsIdentifier(key.ActionId) && key.HasAttemptId && key.AttemptId > 0;
        internal static bool OrdinarySpeed(Clock.Speed speed) => speed == Clock.Speed.Normal || speed == Clock.Speed.Fast || speed == Clock.Speed.Superfast || speed == Clock.Speed.Ultrafast;
        private static TimeSpeed NativeSpeed(Clock.Speed speed) => speed == Clock.Speed.Normal ? TimeSpeed.Normal : speed == Clock.Speed.Fast ? TimeSpeed.Fast : speed == Clock.Speed.Superfast ? TimeSpeed.Superfast : speed == Clock.Speed.Ultrafast ? TimeSpeed.Ultrafast : throw new ArgumentOutOfRangeException(nameof(speed));
        private static Clock.Speed WireSpeed(TimeSpeed speed) => speed == TimeSpeed.Normal ? Clock.Speed.Normal : speed == TimeSpeed.Fast ? Clock.Speed.Fast : speed == TimeSpeed.Superfast ? Clock.Speed.Superfast : speed == TimeSpeed.Ultrafast ? Clock.Speed.Ultrafast : throw new InvalidOperationException("Nonordinary owned epoch");
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
        private static bool ValidPolicy(Clock.WatchPolicy policy, uint maxTicks)
        {
            return policy != null && policy.HasMode && (policy.Mode == Clock.WatchMode.Colony || policy.Mode == Clock.WatchMode.Combat)
                && policy.HasHealthDropFraction && Fraction(policy.HealthDropFraction) && policy.HasMinHealthFraction && Fraction(policy.MinHealthFraction)
                && policy.HasHostileWithin && !float.IsNaN(policy.HostileWithin) && policy.HostileWithin >= 1 && policy.HostileWithin <= 250
                && policy.HasInjuryStopCooldownMs && policy.InjuryStopCooldownMs <= 1800000
                && (policy.MedicalRestIds.Count == 0 || maxTicks <= 600)
                && policy.WatchedAttempts.Count <= MaxWatchedAttempts && policy.WatchedAttempts.All(ValidWatchKey)
                && policy.WatchedAttempts.Distinct().Count() == policy.WatchedAttempts.Count
                && PolicyIds(policy).All(ids => ids.Count() <= 256 && ids.All(ProtoBoundary.IsIdentifier) && ids.Distinct(StringComparer.Ordinal).Count() == ids.Count());
        }
        private static bool Fraction(float value) => !float.IsNaN(value) && value >= 0.01f && value <= 1;
        private static Common.Unavailable Unavailable(string detail) => new Common.Unavailable { Reason = Common.UnavailableReason.NotObserved, Detail = detail };
        private static string Text(string? text) => NativeClockEventProjection.Text(text ?? "");
        private static Clock.StopReason StopReason(string? kind) => NativeClockEventProjection.StopReason(kind);
    }
}
