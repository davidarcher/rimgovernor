#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Google.Protobuf;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Clock = RimGovernor.Protocol.Clock;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // Native action watches: an epoch armed with watched attempts stops at the
    // tick boundary on which any of them reaches a terminal outcome, instead
    // of playing out its whole tick budget. A key is watchable when it names
    // a tracked construction or haul record under the requesting identity
    // (the families whose records observe their own terminal outcome);
    // otherwise the start is refused.
    internal static partial class Supervisor
    {
        internal const int MaxWatchedAttempts = 16;
        private sealed class ArmedWatch
        {
            internal readonly Common.AttemptKey Key;
            internal readonly Func<Common.AttemptKey, Common.ObservationContext, Receipts.Progress> Observe;
            // Unknown is terminal only once the record has been seen pending:
            // a record that was never observed pending cannot have regressed.
            internal readonly bool WasPending;
            internal ArmedWatch(Common.AttemptKey key, Func<Common.AttemptKey, Common.ObservationContext, Receipts.Progress> observe, bool wasPending) { Key = key; Observe = observe; WasPending = wasPending; }
        }
        // WatchedRecord resolves a key to the observer of its tracked record.
        private static Func<Common.AttemptKey, Common.ObservationContext, Receipts.Progress>? WatchedRecord(NativeOperationState state, Common.AttemptKey key)
        {
            if (state.Construction.TryGetValue(key, out var construction)) return construction.Observe;
            if (state.Hauls.TryGetValue(key, out var haul)) return haul.Observe;
            return null;
        }
        private static Common.Failure? ValidWatchedAttempts(Clock.WatchPolicy policy, Common.Identity identity)
        {
            if (policy.WatchedAttempts.Count == 0) return null;
            if (!NativeOperationState.TryGet(identity, out var state))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Watched attempts name no admitted native operation under this identity.");
            foreach (var key in policy.WatchedAttempts)
                if (WatchedRecord(state, key) == null)
                    return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Watched attempt " + key.ActionId + "/" + key.AttemptId + " is not a tracked construction or haul operation.");
            return null;
        }
        // Caller holds Gate; the epoch has just started. An attempt that is
        // already terminal is reported once and never armed.
        private static void ArmWatches(State s, Common.ObservationContext context)
        {
            var typed = TypedOf(s);
            if (typed.Policy.WatchedAttempts.Count == 0) return;
            if (!NativeOperationState.TryGet(typed.Origin.Identity, out var state)) throw new InvalidOperationException("Watched attempts lost their operation state");
            foreach (var key in typed.Policy.WatchedAttempts)
            {
                var observe = WatchedRecord(state, key) ?? throw new InvalidOperationException("Watched attempt lost its operation record");
                var progress = observe(key, typed.LastObservation);
                var outcome = Terminal(key, progress, false, s.LastTick);
                if (outcome != null) { Add("operation_outcome", "Watched attempt was already terminal at start.", s, OutcomePayload(outcome)); continue; }
                typed.Watches.Add(new ArmedWatch(key, observe, progress.Pending != null));
            }
        }
        // Caller holds Gate at a tick boundary. The outcome row precedes the
        // stop row on the same tick, so one events page carries both.
        private static bool CheckWatches(State s)
        {
            var typed = s.Typed;
            if (typed == null || typed.Watches.Count == 0) return false;
            foreach (var watch in typed.Watches)
            {
                Receipts.Progress progress;
                try { progress = watch.Observe(watch.Key, typed.LastObservation); }
                catch (Exception error) { Stop(s, "watcher_error", "Watched attempt could not be observed: " + error.GetType().Name, true, null); return true; }
                var outcome = Terminal(watch.Key, progress, watch.WasPending, s.LastTick);
                if (outcome == null) continue;
                typed.Watches.Remove(watch);
                Add("operation_outcome", "Watched attempt reached a terminal outcome.", s, OutcomePayload(outcome));
                if (!s.Active) return true;
                var payload = OutcomePayload(outcome);
                payload["tickDeadline"] = Deadline(s);
                payload["latchedTick"] = (long)s.LastTick;
                Stop(s, "watch_latched", "Watched attempt " + watch.Key.ActionId + "/" + watch.Key.AttemptId + " latched a terminal outcome.", true, payload);
                return true;
            }
            return false;
        }
        private static Clock.OperationOutcome? Terminal(Common.AttemptKey key, Receipts.Progress progress, bool wasPending, long tick)
        {
            var outcome = new Clock.OperationOutcome { Attempt = key.Clone(), LatchedTick = tick };
            switch (progress.EffectCase)
            {
                case Receipts.Progress.EffectOneofCase.Completed: outcome.Completed = progress.Completed.Clone(); return outcome;
                case Receipts.Progress.EffectOneofCase.Unsuccessful: outcome.Unsuccessful = progress.Unsuccessful.Clone(); return outcome;
                case Receipts.Progress.EffectOneofCase.Absent: outcome.Absent = progress.Absent.Clone(); return outcome;
                case Receipts.Progress.EffectOneofCase.Unknown: if (!wasPending) return null; outcome.Unknown = progress.Unknown.Clone(); return outcome;
                default: return null;
            }
        }
        private static Dictionary<string, object?> OutcomePayload(Clock.OperationOutcome outcome)
            => new Dictionary<string, object?> { { "outcome", JsonFormatter.Default.Format(outcome) }, { "attemptId", (long)outcome.Attempt.AttemptId }, { "actionId", outcome.Attempt.ActionId } };
    }
}
