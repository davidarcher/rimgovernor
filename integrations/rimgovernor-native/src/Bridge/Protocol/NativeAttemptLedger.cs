#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Common = RimGovernor.Contracts.Wire.Common;
using Authority = RimGovernor.Contracts.Wire.Authority;
using Receipts = RimGovernor.Contracts.Wire.Receipts;
using Clock = RimGovernor.Contracts.Wire.Clock;

namespace HomeBridge.BridgeTools
{
    // Unsaved owner for one colony/load, shared across maps. Only the owning game
    // thread may use it. Admission guards and native evidence remain adapter work.
    internal sealed class NativeAttemptLedger
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly string colonyId;
        private readonly string loadToken;
        private readonly int thread = Thread.CurrentThread.ManagedThreadId;
        private readonly Dictionary<Common.AttemptKey, Entry> entries = new Dictionary<Common.AttemptKey, Entry>();
        private readonly Dictionary<Admission, Entry> handles = new Dictionary<Admission, Entry>();
        internal enum DecisionKind { New, Admitted, Replay, InFlight, Refused }

        internal sealed class Admission { internal Admission() { } }

        internal sealed class ClockDecision
        {
            internal DecisionKind Kind { get; }
            internal Admission? Handle { get; }
            internal Admission AdmittedHandle => Handle ?? throw new InvalidOperationException("Decision was not admitted.");
            private readonly Clock.ControlReply? reply;
            internal Clock.ControlReply? Reply => reply;
            internal Clock.ControlReply DecidedReply => Reply ?? throw new InvalidOperationException("Decision carries no reply.");
            internal ClockDecision(DecisionKind kind, Admission? handle = null, Clock.ControlReply? reply = null)
            { Kind = kind; Handle = handle; this.reply = reply; }
        }

        private sealed class Entry
        {
            internal readonly string Method;
            internal readonly object Request;
            internal readonly Authority.WritePrecondition Precondition;
            internal readonly Common.ObservationContext Context;
            internal Clock.ControlReceipt? ClockReceipt;
            internal Entry(string method, object request, Authority.WritePrecondition precondition,
                Common.ObservationContext context)
            {
                Method = method; Precondition = precondition; Context = context;
                // The accepted union is deliberately closed to official request types.
                switch (request)
                {
                    case Clock.StartRequest start: Request = start; break;
                    case Clock.RenewRequest renew: Request = renew; break;
                    case Clock.SpeedRequest speed: Request = speed; break;
                    default: throw new ArgumentException("Unsupported admitted request type.", nameof(request));
                }
            }
        }

        internal NativeAttemptLedger(Common.Identity identity)
        {
            if (!ValidIdentity(identity)) throw new ArgumentException("A valid load identity is required.", nameof(identity));
            colonyId = identity.ColonyId ?? "";
            loadToken = identity.LoadToken ?? "";
        }

        internal int Count { get { RequireThread(); return entries.Count; } }

        internal Receipts.LookupReply Lookup(Common.AttemptKey attempt, Common.ObservationContext current)
        {
            RequireThread();
            if (!ValidAttempt(attempt) || !ValidContext(current))
                return new Receipts.LookupReply { Outcome = Failure(Common.FailureCode.InvalidRequest, "A valid attempt and current context are required.") };
            if (!SameLoad(current.Identity))
                return new Receipts.LookupReply { Outcome = Failure(Common.FailureCode.StaleIdentity, "Ledger belongs to another colony or load.") };
            if (!entries.TryGetValue(attempt, out var entry))
                return new Receipts.LookupReply { Outcome = new Receipts.UnknownAttempt { Context = current } };
            if (!Equals(current.Identity, entry.Context.Identity))
                return new Receipts.LookupReply { Outcome = Failure(Common.FailureCode.StaleIdentity, "Attempt belongs to another map.") };
            return new Receipts.LookupReply { Outcome = Failure(Common.FailureCode.AttemptConflict, "Attempt belongs to clock control.") };
        }

        internal ClockDecision InspectClock(string method, object request)
        {
            RequireThread();
            var pre = ClockPrecondition(method, request);
            var invalid = ValidateClockRequest(request, pre);
            if (invalid != null) return ClockRefused(invalid.Value, "Invalid or stale clock attempt envelope.");
            if (entries.TryGetValue(pre!.Attempt ?? throw new InvalidOperationException("Validated precondition has no attempt."), out var entry))
            {
                if (!string.Equals(method, entry.Method, StringComparison.Ordinal) || !SameMessage(request, entry.Request))
                    return ClockRefused(Common.FailureCode.AttemptConflict, "Attempt key already identifies a different request or original precondition.");
                return entry.ClockReceipt != null
                    ? new ClockDecision(DecisionKind.Replay, reply: new Clock.ControlReply { Outcome = new Clock.ControlReplyOutcome.ReceiptCase(entry.ClockReceipt) })
                    : new ClockDecision(DecisionKind.InFlight, reply: new Clock.ControlReply { Outcome = ClockInFlight(entry) });
            }
            return new ClockDecision(DecisionKind.New);
        }

        internal ClockDecision AdmitClock(string method, object request, Common.ObservationContext context)
        {
            var prior = InspectClock(method, request);
            if (prior.Kind != DecisionKind.New) return prior;
            var pre = ClockPrecondition(method, request)!;
            if (!ValidContext(context) || !Equals(context.Identity, pre.Identity)
                || context.NativeGeneration is null || context.NativeGeneration.GetValueOrDefault() != pre.ExpectedGeneration.GetValueOrDefault())
                return ClockRefused(Common.FailureCode.InvalidRequest, "Admission context must match the validated request.");
            var entry = new Entry(method, request, pre, context);
            var handle = new Admission();
            entries.Add(pre.Attempt ?? throw new InvalidOperationException("Validated precondition has no attempt."), entry);
            handles.Add(handle, entry);
            return new ClockDecision(DecisionKind.Admitted, handle);
        }

        internal Clock.ControlReceipt FinishClockApplied(Admission handle, Clock.Status observed)
        {
            var entry = ClockEntry(handle);
            RequireClockEvidence(observed, entry);
            return FinishClock(entry, new Clock.ControlReceipt { Outcome = new Clock.AppliedControl { Status = observed } });
        }

        internal Clock.ControlReceipt FinishClockUncertain(Admission handle, Clock.Status? lastObserved, string detail)
        {
            var entry = ClockEntry(handle);
            if (lastObserved != null) RequireClockEvidence(lastObserved, entry);
            return FinishClock(entry, new Clock.ControlReceipt { Outcome = new Clock.UncertainControl
                { LastObserved = lastObserved, Detail = Diagnostic(detail) } });
        }

        private Entry ClockEntry(Admission handle)
        {
            RequireThread();
            if (handle == null || !handles.TryGetValue(handle, out var entry))
                throw new ArgumentException("Admission does not belong to this ledger.", nameof(handle));
            if (entry.ClockReceipt != null) throw new InvalidOperationException("An admitted receipt is immutable once recorded.");
            return entry;
        }

        private static Clock.ControlReceipt FinishClock(Entry entry, Clock.ControlReceipt receipt)
        {
            receipt = receipt with { Attempt = entry.Precondition.Attempt };
            receipt = receipt with { AdmittedContext = entry.Context };
            entry.ClockReceipt = receipt;
            return receipt;
        }

        private static void RequireClockEvidence(Clock.Status status, Entry entry)
        {
            if (status == null || status.State is null || !ValidContext(status.Context)
                || !Equals(status.Context?.Identity, entry.Context.Identity) || status.Context?.Tick.GetValueOrDefault() < entry.Context.Tick.GetValueOrDefault())
                throw new ArgumentException("Clock evidence requires an explicit state and fresh admission-scoped context.", nameof(status));
        }

        internal Clock.AttemptReply LookupClock(Common.AttemptKey attempt, Common.ObservationContext current)
        {
            RequireThread();
            if (!ValidAttempt(attempt) || !ValidContext(current))
                return new Clock.AttemptReply { Outcome = Failure(Common.FailureCode.InvalidRequest, "A valid attempt and current context are required.") };
            if (!SameLoad(current.Identity))
                return new Clock.AttemptReply { Outcome = Failure(Common.FailureCode.StaleIdentity, "Ledger belongs to another colony or load.") };
            if (!entries.TryGetValue(attempt, out var entry)) return new Clock.AttemptReply { Outcome = new Clock.AttemptUnknown() };
            if (!Equals(current.Identity, entry.Context.Identity))
                return new Clock.AttemptReply { Outcome = Failure(Common.FailureCode.StaleIdentity, "Attempt belongs to another map.") };
            return new Clock.AttemptReply { Outcome = new Clock.AttemptReplyOutcome.ReceiptCase(entry.ClockReceipt ?? ClockInFlight(entry)) };
        }

        private static Clock.ControlReceipt ClockInFlight(Entry entry) => new Clock.ControlReceipt
        {
            Attempt = entry.Precondition.Attempt, AdmittedContext = entry.Context,
            Outcome = new Clock.UncertainControl { Detail = "Attempt is admitted and still in flight; no new effect was dispatched." }
        };

        private static Authority.WritePrecondition? ClockPrecondition(string method, object request)
        {
            switch (request)
            {
                case Clock.StartRequest start when method == "rimgovernor.clock.v1.Clock/Start": return start.Authority;
                case Clock.RenewRequest renew when method == "rimgovernor.clock.v1.Clock/Renew": return renew.Authority;
                case Clock.SpeedRequest speed when method == "rimgovernor.clock.v1.Clock/ChangeSpeed": return speed.Authority;
                default: return null;
            }
        }

        private Common.FailureCode? ValidateClockRequest(object request, Authority.WritePrecondition? pre)
        {
            if (pre == null || !ValidIdentity(pre.Identity) || !ValidAttempt(pre.Attempt)
                || pre.ExpectedGeneration is null || pre.ExpectedGeneration.GetValueOrDefault() == 0)
                return Common.FailureCode.InvalidRequest;
            return SameLoad(pre.Identity) ? (Common.FailureCode?)null : Common.FailureCode.StaleIdentity;
        }

        // Wire records compare by value and keep optional-scalar presence (null is unset)
        // and list order, which is exactly what admission identity requires.
        private static bool SameMessage(object left, object right) => Equals(left, right);
        private bool SameLoad(Common.Identity? identity) => identity is not null && (identity.ColonyId ?? "") == colonyId && (identity.LoadToken ?? "") == loadToken;
        private static bool ValidIdentity(Common.Identity? identity) => identity != null && identity.ColonyId is not null && identity.LoadToken is not null
            && identity.MapId is not null && Identifier((identity.ColonyId ?? "")) && Identifier((identity.LoadToken ?? "")) && identity.MapId.GetValueOrDefault() >= 0;
        private static bool ValidContext(Common.ObservationContext? context) => context != null && ValidIdentity(context.Identity)
            && context.Tick is not null && context.Tick.GetValueOrDefault() >= 0;
        private static bool ValidAttempt(Common.AttemptKey? attempt) => attempt != null && attempt.ControllerSessionId is not null
            && attempt.ActionId is not null && attempt.AttemptId is not null && Identifier((attempt.ControllerSessionId ?? ""))
            && Identifier((attempt.ActionId ?? "")) && attempt.AttemptId.GetValueOrDefault() > 0;
        private static bool Identifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.IndexOf('\0') >= 0) return false;
            try { return Utf8.GetByteCount(value) <= 256; } catch (EncoderFallbackException) { return false; }
        }
        private static string Diagnostic(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            Utf8.GetByteCount(text);
            int end = 0;
            for (int count = 0; end < text.Length && count < 4096; count++, end++)
                if (char.IsHighSurrogate(text[end])) end++;
            return text.Substring(0, end);
        }
        private static Common.Failure Failure(Common.FailureCode code, string detail) => new Common.Failure { Code = code, Detail = detail };
        private static ClockDecision ClockRefused(Common.FailureCode code, string detail) => new ClockDecision(DecisionKind.Refused,
            reply: new Clock.ControlReply { Outcome = Failure(code, detail) });
        private void RequireThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != thread) throw new InvalidOperationException("Attempt ledger requires its owning game thread.");
        }
    }
}
