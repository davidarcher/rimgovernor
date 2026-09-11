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

        private sealed class Entry
        {
            internal readonly string Method;
            internal readonly Operations.ExecuteRequest Request;
            internal readonly Common.ObservationContext Context;
            internal readonly Authority.Owner Owner;
            internal Receipts.Receipt? Receipt;
            internal Entry(string method, Operations.ExecuteRequest request,
                Common.ObservationContext context, Authority.Owner owner)
            { Method = method; Request = request.Clone(); Context = context.Clone(); Owner = owner.Clone(); }
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
                        { Attempt = key.Clone(), AdmittedContext = entry.Context.Clone(), AuthorizingOwner = entry.Owner.Clone(),
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
            Common.ObservationContext context, Authority.Owner owner)
        {
            var prior = Inspect(method, request);
            if (prior.Kind != DecisionKind.New) return prior;
            if (!ValidContext(context) || !context.Identity.Equals(request.Precondition.Identity)
                || !context.HasNativeGeneration || context.NativeGeneration != request.Precondition.ExpectedGeneration
                || owner == null || !owner.HasControllerSessionId || !Identifier(owner.ControllerSessionId)
                || owner.ControllerSessionId != request.Precondition.Attempt.ControllerSessionId
                || !owner.HasPlayerDirection || owner.PlayerDirection == 0)
                return Refused(Common.FailureCode.InvalidRequest, "Admission context and owner must match the validated request.");
            var entry = new Entry(method, request, context, owner);
            var handle = new Admission();
            entries.Add(entry.Request.Precondition.Attempt.Clone(), entry);
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
            if (entry.Receipt != null) throw new InvalidOperationException("An admitted receipt is immutable once recorded.");
            receipt.Attempt = entry.Request.Precondition.Attempt.Clone();
            receipt.AdmittedContext = entry.Context.Clone();
            receipt.AuthorizingOwner = entry.Owner.Clone();
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
            return entry.Receipt != null
                ? new Receipts.LookupReply { Receipt = entry.Receipt.Clone() }
                : new Receipts.LookupReply { InFlight = new Receipts.InFlight
                    { Attempt = attempt.Clone(), AdmittedContext = entry.Context.Clone() } };
        }

        private Common.FailureCode? ValidateRequest(string method, Operations.ExecuteRequest request)
        {
            var pre = request?.Precondition;
            if (!Identifier(method) || pre == null || !ValidIdentity(pre.Identity) || !ValidAttempt(pre.Attempt)
                || !pre.HasExpectedGeneration || pre.ExpectedGeneration == 0 || !pre.HasLeaseId || !Identifier(pre.LeaseId)
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
        private void RequireThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != thread) throw new InvalidOperationException("Attempt ledger requires its owning game thread.");
        }
    }
}
