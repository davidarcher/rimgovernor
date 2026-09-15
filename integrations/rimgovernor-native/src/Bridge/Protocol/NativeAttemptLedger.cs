#nullable enable
using System;
using System.Collections.Generic;
using System.Collections;
using System.Text;
using System.Threading;
using Google.Protobuf;
using Common = RimGovernor.Protocol.Common;
using Authority = RimGovernor.Protocol.Authority;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;
using Clock = RimGovernor.Protocol.Clock;

namespace HomeBridge.BridgeTools
{
    // Unsaved owner for one colony/load, shared across maps. Only the owning game
    // thread may use it. Admission guards and native evidence remain adapter work.
    internal sealed class NativeAttemptLedger
    {
        internal const int Capacity = 4096;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly string colonyId;
        private readonly string loadToken;
        private readonly int thread = Thread.CurrentThread.ManagedThreadId;
        private readonly Dictionary<Common.AttemptKey, Entry> entries = new Dictionary<Common.AttemptKey, Entry>();
        private readonly Dictionary<Admission, Entry> handles = new Dictionary<Admission, Entry>();
        internal enum DecisionKind { New, Admitted, Replay, InFlight, Refused }

        internal sealed class Decision
        {
            internal DecisionKind Kind { get; }
            internal Admission? Handle { get; }
            private readonly Operations.ExecuteReply? reply;
            private readonly Receipts.InFlight? inFlight;
            internal Operations.ExecuteReply? Reply => reply?.Clone();
            internal Receipts.InFlight? InFlight => inFlight?.Clone();
            internal Decision(DecisionKind kind, Admission? handle = null,
                Operations.ExecuteReply? reply = null, Receipts.InFlight? inFlight = null)
            { Kind = kind; Handle = handle; this.reply = reply; this.inFlight = inFlight; }
        }

        internal sealed class Admission { internal Admission() { } }

        internal sealed class ClockDecision
        {
            internal DecisionKind Kind { get; }
            internal Admission? Handle { get; }
            private readonly Clock.ControlReply? reply;
            internal Clock.ControlReply? Reply => reply?.Clone();
            internal ClockDecision(DecisionKind kind, Admission? handle = null, Clock.ControlReply? reply = null)
            { Kind = kind; Handle = handle; this.reply = reply; }
        }

        private sealed class Entry
        {
            internal readonly string Method;
            internal readonly IMessage Request;
            internal readonly Authority.WritePrecondition Precondition;
            internal readonly Common.ObservationContext Context;
            internal Receipts.Receipt? Receipt;
            internal Clock.ControlReceipt? ClockReceipt;
            internal Entry(string method, IMessage request, Authority.WritePrecondition precondition,
                Common.ObservationContext context)
            {
                Method = method; Precondition = precondition.Clone(); Context = context.Clone();
                // The accepted union is deliberately closed to official request types.
                switch (request)
                {
                    case Operations.ExecuteRequest operation: Request = operation.Clone(); break;
                    case Clock.StartRequest start: Request = start.Clone(); break;
                    case Clock.RenewRequest renew: Request = renew.Clone(); break;
                    case Clock.SpeedRequest speed: Request = speed.Clone(); break;
                    default: throw new ArgumentException("Unsupported admitted request type.", nameof(request));
                }
            }
        }

        internal NativeAttemptLedger(Common.Identity identity)
        {
            if (!ValidIdentity(identity)) throw new ArgumentException("A valid load identity is required.", nameof(identity));
            colonyId = identity.ColonyId;
            loadToken = identity.LoadToken;
        }

        internal int Count { get { RequireThread(); return entries.Count; } }

        // Inspect before revalidating current authority: an admitted exact retry
        // may replay after lease expiry, but can never schedule another effect.
        internal Decision Inspect(string method, Operations.ExecuteRequest request)
        {
            RequireThread();
            var invalid = ValidateRequest(method, request);
            if (invalid != null) return Refused(invalid.Value, "Invalid or stale attempt envelope.");
            var key = request.Precondition.Attempt;
            if (entries.TryGetValue(key, out var entry))
            {
                if (!string.Equals(method, entry.Method, StringComparison.Ordinal) || !SameMessage(request, entry.Request))
                    return Refused(Common.FailureCode.AttemptConflict, "Attempt key already identifies a different request or original precondition.");
                if (entry.Receipt != null)
                    return new Decision(DecisionKind.Replay, reply: new Operations.ExecuteReply { Receipt = entry.Receipt.Clone() });
                return new Decision(DecisionKind.InFlight,
                    reply: new Operations.ExecuteReply { Receipt = new Receipts.Receipt
                        { Attempt = key.Clone(), AdmittedContext = entry.Context.Clone(),
                          Uncertain = new Receipts.Uncertain { Detail = "Attempt is admitted and still in flight; no new effect was dispatched." } } },
                    inFlight: new Receipts.InFlight
                    { Attempt = key.Clone(), AdmittedContext = entry.Context.Clone() });
            }
            return entries.Count == Capacity
                ? Refused(Common.FailureCode.CapacityExhausted, "The native attempt ledger is full; no attempt was admitted.")
                : new Decision(DecisionKind.New);
        }

        // Call only after all native guards succeed, immediately before effects.
        // The handle is the only route to terminal admitted outcomes.
        internal Decision Admit(string method, Operations.ExecuteRequest request,
            Common.ObservationContext context)
        {
            var prior = Inspect(method, request);
            if (prior.Kind != DecisionKind.New) return prior;
            if (!ValidContext(context) || !context.Identity.Equals(request.Precondition.Identity)
                || !context.HasNativeGeneration || context.NativeGeneration != request.Precondition.ExpectedGeneration)
                return Refused(Common.FailureCode.InvalidRequest, "Admission context must match the validated request.");
            var entry = new Entry(method, request, request.Precondition, context);
            var handle = new Admission();
            entries.Add(entry.Precondition.Attempt.Clone(), entry);
            handles.Add(handle, entry);
            return new Decision(DecisionKind.Admitted, handle);
        }

        internal Receipts.Receipt FinishApplied(Admission handle, Receipts.EffectEvidence observed)
        {
            RequireEvidence(observed);
            return Finish(handle, new Receipts.Receipt { Applied = new Receipts.Applied { Observed = observed.Clone() } });
        }
        internal Receipts.Receipt FinishNoChange(Admission handle, Receipts.EffectEvidence observed, string detail)
        {
            RequireEvidence(observed);
            return Finish(handle, new Receipts.Receipt { NoChange = new Receipts.NoChange { Observed = observed.Clone(), Detail = Diagnostic(detail) } });
        }
        internal Receipts.Receipt FinishUncertain(Admission handle, Receipts.EffectEvidence? lastObserved, string detail)
        {
            if (lastObserved != null) RequireEvidence(lastObserved);
            return Finish(handle, new Receipts.Receipt { Uncertain = new Receipts.Uncertain
                { LastObserved = lastObserved?.Clone(), Detail = Diagnostic(detail) } });
        }

        private Receipts.Receipt Finish(Admission handle, Receipts.Receipt receipt)
        {
            RequireThread();
            if (handle == null || !handles.TryGetValue(handle, out var entry))
                throw new ArgumentException("Admission does not belong to this ledger.", nameof(handle));
            if (!(entry.Request is Operations.ExecuteRequest)) throw new ArgumentException("Admission belongs to clock control.", nameof(handle));
            if (entry.Receipt != null) throw new InvalidOperationException("An admitted receipt is immutable once recorded.");
            receipt.Attempt = entry.Precondition.Attempt.Clone();
            receipt.AdmittedContext = entry.Context.Clone();
            entry.Receipt = receipt;
            return receipt.Clone();
        }

        internal Receipts.LookupReply Lookup(Common.AttemptKey attempt, Common.ObservationContext current)
        {
            RequireThread();
            if (!ValidAttempt(attempt) || !ValidContext(current))
                return new Receipts.LookupReply { Failure = Failure(Common.FailureCode.InvalidRequest, "A valid attempt and current context are required.") };
            if (!SameLoad(current.Identity))
                return new Receipts.LookupReply { Failure = Failure(Common.FailureCode.StaleIdentity, "Ledger belongs to another colony or load.") };
            if (!entries.TryGetValue(attempt, out var entry))
                return new Receipts.LookupReply { Unknown = new Receipts.UnknownAttempt { Context = current.Clone() } };
            if (!current.Identity.Equals(entry.Context.Identity))
                return new Receipts.LookupReply { Failure = Failure(Common.FailureCode.StaleIdentity, "Attempt belongs to another map.") };
            if (!(entry.Request is Operations.ExecuteRequest))
                return new Receipts.LookupReply { Failure = Failure(Common.FailureCode.AttemptConflict, "Attempt belongs to clock control.") };
            return entry.Receipt != null
                ? new Receipts.LookupReply { Receipt = entry.Receipt.Clone() }
                : new Receipts.LookupReply { InFlight = new Receipts.InFlight
                    { Attempt = attempt.Clone(), AdmittedContext = entry.Context.Clone() } };
        }

        internal ClockDecision InspectClock(string method, IMessage request)
        {
            RequireThread();
            var pre = ClockPrecondition(method, request);
            var invalid = ValidateClockRequest(request, pre);
            if (invalid != null) return ClockRefused(invalid.Value, "Invalid or stale clock attempt envelope.");
            if (entries.TryGetValue(pre!.Attempt, out var entry))
            {
                if (!string.Equals(method, entry.Method, StringComparison.Ordinal) || !SameMessage(request, entry.Request))
                    return ClockRefused(Common.FailureCode.AttemptConflict, "Attempt key already identifies a different request or original precondition.");
                return entry.ClockReceipt != null
                    ? new ClockDecision(DecisionKind.Replay, reply: new Clock.ControlReply { Receipt = entry.ClockReceipt.Clone() })
                    : new ClockDecision(DecisionKind.InFlight, reply: new Clock.ControlReply { Receipt = ClockInFlight(entry) });
            }
            return entries.Count == Capacity
                ? ClockRefused(Common.FailureCode.CapacityExhausted, "The native attempt ledger is full; no attempt was admitted.")
                : new ClockDecision(DecisionKind.New);
        }

        internal ClockDecision AdmitClock(string method, IMessage request, Common.ObservationContext context)
        {
            var prior = InspectClock(method, request);
            if (prior.Kind != DecisionKind.New) return prior;
            var pre = ClockPrecondition(method, request)!;
            if (!ValidContext(context) || !context.Identity.Equals(pre.Identity)
                || !context.HasNativeGeneration || context.NativeGeneration != pre.ExpectedGeneration)
                return ClockRefused(Common.FailureCode.InvalidRequest, "Admission context must match the validated request.");
            var entry = new Entry(method, request, pre, context);
            var handle = new Admission();
            entries.Add(entry.Precondition.Attempt.Clone(), entry);
            handles.Add(handle, entry);
            return new ClockDecision(DecisionKind.Admitted, handle);
        }

        internal Clock.ControlReceipt FinishClockApplied(Admission handle, Clock.Status observed)
        {
            var entry = ClockEntry(handle);
            RequireClockEvidence(observed, entry);
            return FinishClock(entry, new Clock.ControlReceipt { Applied = new Clock.AppliedControl { Status = observed.Clone() } });
        }

        internal Clock.ControlReceipt FinishClockUncertain(Admission handle, Clock.Status? lastObserved, string detail)
        {
            var entry = ClockEntry(handle);
            if (lastObserved != null) RequireClockEvidence(lastObserved, entry);
            return FinishClock(entry, new Clock.ControlReceipt { Uncertain = new Clock.UncertainControl
                { LastObserved = lastObserved?.Clone(), Detail = Diagnostic(detail) } });
        }

        private Entry ClockEntry(Admission handle)
        {
            RequireThread();
            if (handle == null || !handles.TryGetValue(handle, out var entry))
                throw new ArgumentException("Admission does not belong to this ledger.", nameof(handle));
            if (entry.Request is Operations.ExecuteRequest) throw new ArgumentException("Admission belongs to operations.", nameof(handle));
            if (entry.ClockReceipt != null) throw new InvalidOperationException("An admitted receipt is immutable once recorded.");
            return entry;
        }

        private static Clock.ControlReceipt FinishClock(Entry entry, Clock.ControlReceipt receipt)
        {
            receipt.Attempt = entry.Precondition.Attempt.Clone();
            receipt.AdmittedContext = entry.Context.Clone();
            entry.ClockReceipt = receipt;
            return receipt.Clone();
        }

        private static void RequireClockEvidence(Clock.Status status, Entry entry)
        {
            if (status == null || status.StateCase == Clock.Status.StateOneofCase.None || !ValidContext(status.Context)
                || !status.Context.Identity.Equals(entry.Context.Identity) || status.Context.Tick < entry.Context.Tick)
                throw new ArgumentException("Clock evidence requires an explicit state and fresh admission-scoped context.", nameof(status));
        }

        internal Clock.AttemptReply LookupClock(Common.AttemptKey attempt, Common.ObservationContext current)
        {
            RequireThread();
            if (!ValidAttempt(attempt) || !ValidContext(current))
                return new Clock.AttemptReply { Failure = Failure(Common.FailureCode.InvalidRequest, "A valid attempt and current context are required.") };
            if (!SameLoad(current.Identity))
                return new Clock.AttemptReply { Failure = Failure(Common.FailureCode.StaleIdentity, "Ledger belongs to another colony or load.") };
            if (!entries.TryGetValue(attempt, out var entry)) return new Clock.AttemptReply { Unknown = new Clock.AttemptUnknown() };
            if (!current.Identity.Equals(entry.Context.Identity))
                return new Clock.AttemptReply { Failure = Failure(Common.FailureCode.StaleIdentity, "Attempt belongs to another map.") };
            if (entry.Request is Operations.ExecuteRequest)
                return new Clock.AttemptReply { Failure = Failure(Common.FailureCode.AttemptConflict, "Attempt belongs to operations.") };
            return new Clock.AttemptReply { Receipt = entry.ClockReceipt?.Clone() ?? ClockInFlight(entry) };
        }

        private static Clock.ControlReceipt ClockInFlight(Entry entry) => new Clock.ControlReceipt
        {
            Attempt = entry.Precondition.Attempt.Clone(), AdmittedContext = entry.Context.Clone(),
            Uncertain = new Clock.UncertainControl { Detail = "Attempt is admitted and still in flight; no new effect was dispatched." }
        };

        private static Authority.WritePrecondition? ClockPrecondition(string method, IMessage request)
        {
            switch (request)
            {
                case Clock.StartRequest start when method == "rimgovernor.clock.v1.Clock/Start": return start.Authority;
                case Clock.RenewRequest renew when method == "rimgovernor.clock.v1.Clock/Renew": return renew.Authority;
                case Clock.SpeedRequest speed when method == "rimgovernor.clock.v1.Clock/ChangeSpeed": return speed.Authority;
                default: return null;
            }
        }

        private Common.FailureCode? ValidateClockRequest(IMessage request, Authority.WritePrecondition? pre)
        {
            if (pre == null || !ValidIdentity(pre.Identity) || !ValidAttempt(pre.Attempt)
                || !pre.HasExpectedGeneration || pre.ExpectedGeneration == 0)
                return Common.FailureCode.InvalidRequest;
            if (!request.Equals(request.Descriptor.Parser.WithDiscardUnknownFields(true).ParseFrom(request.ToByteArray())))
                return Common.FailureCode.InvalidRequest;
            return SameLoad(pre.Identity) ? (Common.FailureCode?)null : Common.FailureCode.StaleIdentity;
        }

        private Common.FailureCode? ValidateRequest(string method, Operations.ExecuteRequest request)
        {
            var pre = request?.Precondition;
            if (!Identifier(method) || pre == null || !ValidIdentity(pre.Identity) || !ValidAttempt(pre.Attempt)
                || !pre.HasExpectedGeneration || pre.ExpectedGeneration == 0
                || request!.Operation == null || request.Operation.CommandCase == Operations.Operation.CommandOneofCase.None)
                return Common.FailureCode.InvalidRequest;
            // Official binary parsing discards unknown fields recursively; equality
            // remains generated typed-field equality, never serialized-byte equality.
            if (!request.Equals(Operations.ExecuteRequest.Parser.WithDiscardUnknownFields(true).ParseFrom(request.ToByteArray())))
                return Common.FailureCode.InvalidRequest;
            return SameLoad(pre.Identity) ? (Common.FailureCode?)null : Common.FailureCode.StaleIdentity;
        }
        // Generated C# Equals compares optional scalar values but can ignore their
        // presence. Admission identity must also preserve presence and list order.
        private static bool SameMessage(IMessage left, IMessage right)
        {
            if (!ReferenceEquals(left.Descriptor, right.Descriptor)) return false;
            foreach (var field in left.Descriptor.Fields.InFieldNumberOrder())
            {
                if (field.IsRepeated)
                {
                    var a = ((IEnumerable)field.Accessor.GetValue(left)).GetEnumerator();
                    var b = ((IEnumerable)field.Accessor.GetValue(right)).GetEnumerator();
                    while (true)
                    {
                        var hasA = a.MoveNext();
                        var hasB = b.MoveNext();
                        if (hasA != hasB) return false;
                        if (!hasA) break;
                        if (!SameValue(a.Current, b.Current)) return false;
                    }
                }
                else
                {
                    if (field.HasPresence && field.Accessor.HasValue(left) != field.Accessor.HasValue(right)) return false;
                    if (!SameValue(field.Accessor.GetValue(left), field.Accessor.GetValue(right))) return false;
                }
            }
            return true;
        }
        private static bool SameValue(object? left, object? right)
        {
            if (left is IMessage a && right is IMessage b) return SameMessage(a, b);
            return Equals(left, right);
        }
        private bool SameLoad(Common.Identity identity) => identity.ColonyId == colonyId && identity.LoadToken == loadToken;
        private static bool ValidIdentity(Common.Identity? identity) => identity != null && identity.HasColonyId && identity.HasLoadToken
            && identity.HasMapId && Identifier(identity.ColonyId) && Identifier(identity.LoadToken) && identity.MapId >= 0;
        private static bool ValidContext(Common.ObservationContext? context) => context != null && ValidIdentity(context.Identity)
            && context.HasTick && context.Tick >= 0;
        private static bool ValidAttempt(Common.AttemptKey? attempt) => attempt != null && attempt.HasControllerSessionId
            && attempt.HasActionId && attempt.HasAttemptId && Identifier(attempt.ControllerSessionId)
            && Identifier(attempt.ActionId) && attempt.AttemptId > 0;
        private static bool Identifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.IndexOf('\0') >= 0) return false;
            try { return Utf8.GetByteCount(value) <= 256; } catch (EncoderFallbackException) { return false; }
        }
        private static void RequireEvidence(Receipts.EffectEvidence evidence)
        {
            if (evidence == null || evidence.EffectCase == Receipts.EffectEvidence.EffectOneofCase.None)
                throw new ArgumentException("Observed evidence requires an explicit concrete effect.", nameof(evidence));
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
        private static Decision Refused(Common.FailureCode code, string detail) => new Decision(DecisionKind.Refused,
            reply: new Operations.ExecuteReply { Failure = Failure(code, detail) });
        private static ClockDecision ClockRefused(Common.FailureCode code, string detail) => new ClockDecision(DecisionKind.Refused,
            reply: new Clock.ControlReply { Failure = Failure(code, detail) });
        private void RequireThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != thread) throw new InvalidOperationException("Attempt ledger requires its owning game thread.");
        }
    }
}
