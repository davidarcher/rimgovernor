#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        }
        private static TypedEpoch? pendingTyped;
        private static bool typedSpeedCall;
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
            if (result.Success && TypedHooksReady()) return false;
            var reason = result.Snapshot.Reason;
            var kind = reason == NativeControlRevocationReason.IdentityChanged ? "session_changed"
                : reason == NativeControlRevocationReason.HooksUnavailable || reason == NativeControlRevocationReason.GenerationExhausted
                    || reason == NativeControlRevocationReason.ClockUnavailable || !TypedHooksReady() ? "unavailable" : "external_pause";
            Stop(s, kind, "Authorizing native authority stopped: " + reason + "; " + result.Error, true, null);
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
                return null;
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
                        (int)policy.InjuryStopCooldownMs, (int)request.MaxTicks, ResolveIds(ProtoBoundary.LoadedMap(context), policy.SurgicalRecoveryIds), false, ResolveIds(ProtoBoundary.LoadedMap(context), policy.MedicalRestIds));
                    if (_state == null || !ReferenceEquals(_state.Typed, metadata)) throw new InvalidOperationException("Native start did not create the admitted epoch");
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
        internal static Clock.Status TypedSpeed(Clock.SpeedRequest request, Common.ObservationContext context)
        {
            lock (Gate)
            {
                if (ValidateTypedGrant(request.Authority) != null) throw new InvalidOperationException("Clock epoch authority grant changed");
                try
                {
                    typedSpeedCall = true;
                    Speed(request.Epoch.Owner.ControllerSessionId, request.Epoch.Owner.Epoch, NativeSpeed(request.Speed));
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
                return TypedStatus(context);
            }
        }

        internal static Clock.Status TypedStatus(Common.ObservationContext context)
        {
            lock (Gate)
            {
                EnsurePatched();
                var result = new Clock.Status { Context = context.Clone(), ActualPaused = Find.TickManager.Paused,
                    ObservedSpeed = ObservedSpeed(Find.TickManager.CurTimeSpeed), NativeTickBoundary = TypedHooksReady(),
                    EvidenceCompleteness = new Common.PageInfo { Complete = true },
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
                if (_patchError != null) result.WatcherError = Text(_patchError);
                return result;
            }
        }
        // Typed starts always carry a tick budget, so a typed epoch always has a deadline.
        private static long Deadline(State s) => s.TickDeadline ?? throw new InvalidOperationException("Typed epoch has no tick deadline.");
        private static Clock.Epoch Epoch(State s) => new Clock.Epoch { Owner = TypedOf(s).Owner.Clone(), Origin = TypedOf(s).Origin.Clone(),
            RequestedSpeed = WireSpeed(s.RequestedSpeed), Policy = TypedOf(s).Policy.Clone(), StartTick = s.StartTick,
            TickDeadline = Deadline(s), LastTick = s.LastTick, LeaseRemainingMs = s.Active ? (uint)Math.Min(30000, Math.Max(0, s.LeaseExpiresMs - LeaseNow(s))) : 0 };

        internal static Clock.EventsReply TypedEvents(Clock.EventsRequest request, Common.ObservationContext context)
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
                        object? encoded;
                        if (!row.TryGetValue("canonicalClockEvent", out encoded) || !(encoded is string))
                            return new Clock.EventsReply { Failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Retained legacy event lacks canonical ownership and original observation context.") };
                        var observed = Clock.Event.Parser.ParseJson((string)encoded);
                        if (!ValidStoredEvent(observed) || observed.Cursor <= previous
                            || observed.Cursor != Convert.ToInt64(row["cursor"])) throw new InvalidOperationException("Event identity mismatch");
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
                || value.Owner == null || !value.Owner.HasControllerSessionId || !ProtoBoundary.IsIdentifier(value.Owner.ControllerSessionId)
                || !value.Owner.HasEpoch || value.Owner.Epoch <= 0 || !value.HasObservedAtUnixMs || value.ObservedAtUnixMs < 0
                || value.EventCase == Clock.Event.EventOneofCase.None) return false;
            if (value.Stopped != null) return ValidStoredStop(value.Stopped);
            if (value.PauseFailed != null) return value.PauseFailed.Pending != null && ValidStoredStop(value.PauseFailed.Pending);
            if (value.Started != null) return value.Started.Epoch != null && value.Started.Epoch.Owner != null
                && value.Started.Epoch.Owner.Equals(value.Owner) && value.Started.Epoch.Origin != null;
            if (value.SpeedChanged != null) return value.SpeedChanged.HasSpeed && OrdinarySpeed(value.SpeedChanged.Speed);
            if (value.Notification != null) return value.Notification.SourceCase != Clock.Notification.SourceOneofCase.None;
            return true;
        }
        private static bool ValidStoredStop(Clock.StopEvent value) => value.HasReason && value.Reason != Clock.StopReason.Unspecified
            && Enum.IsDefined(typeof(Clock.StopReason), value.Reason) && value.EvidenceCase != Clock.StopEvent.EvidenceOneofCase.None;
        internal static bool OrdinarySpeed(Clock.Speed speed) => speed == Clock.Speed.Normal || speed == Clock.Speed.Fast || speed == Clock.Speed.Superfast;
        private static TimeSpeed NativeSpeed(Clock.Speed speed) => speed == Clock.Speed.Normal ? TimeSpeed.Normal : speed == Clock.Speed.Fast ? TimeSpeed.Fast : speed == Clock.Speed.Superfast ? TimeSpeed.Superfast : throw new ArgumentOutOfRangeException(nameof(speed));
        private static Clock.Speed WireSpeed(TimeSpeed speed) => speed == TimeSpeed.Normal ? Clock.Speed.Normal : speed == TimeSpeed.Fast ? Clock.Speed.Fast : speed == TimeSpeed.Superfast ? Clock.Speed.Superfast : throw new InvalidOperationException("Nonordinary owned epoch");
        internal static Clock.ObservedSpeed ObservedSpeed(TimeSpeed speed) => speed == TimeSpeed.Paused ? Clock.ObservedSpeed.Paused : speed == TimeSpeed.Normal ? Clock.ObservedSpeed.Normal : speed == TimeSpeed.Fast ? Clock.ObservedSpeed.Fast : speed == TimeSpeed.Superfast ? Clock.ObservedSpeed.Superfast : speed == TimeSpeed.Ultrafast ? Clock.ObservedSpeed.Ultrafast : throw new InvalidOperationException("Unknown native speed");
        internal static bool TypedHooksReady()
        {
            return new[] { Tuple.Create("TickManagerUpdate", nameof(OnUpdate)), Tuple.Create("DoSingleTick", nameof(OnTick)) }.All(pair =>
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
                && PolicyIds(policy).All(ids => ids.Count() <= 256 && ids.All(ProtoBoundary.IsIdentifier) && ids.Distinct(StringComparer.Ordinal).Count() == ids.Count());
        }
        private static bool Fraction(float value) => !float.IsNaN(value) && value >= 0.01f && value <= 1;
        private static Common.Unavailable Unavailable(string detail) => new Common.Unavailable { Reason = Common.UnavailableReason.NotObserved, Detail = detail };
        private static string Text(string? text) => NativeClockEventProjection.Text(text ?? "");
        private static Clock.StopReason StopReason(string? kind) => NativeClockEventProjection.StopReason(kind);
    }
}
