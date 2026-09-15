using System;
using System.Text;
using Google.Protobuf;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // Check the complete wire reply before committing an immutable ledger outcome.
    internal static class NativeOperationEnvelope
    {
        internal static bool Fits(IMessage reply)
        {
            try { return new UTF8Encoding(false, true).GetByteCount(JsonFormatter.Default.Format(reply)) <= ProtoBoundary.MaximumEnvelopeBytes; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        internal static Receipts.Receipt Applied(NativeAttemptLedger ledger, NativeAttemptLedger.Admission handle,
            Common.AttemptKey attempt, Common.ObservationContext context, Receipts.EffectEvidence evidence)
        {
            var candidate = Header(attempt, context);
            candidate.Applied = new Receipts.Applied { Observed = evidence };
            if (Fits(new Operations.ExecuteReply { Receipt = candidate })) return ledger.FinishApplied(handle, evidence);
            return ledger.FinishUncertain(handle, null, "Observed construction evidence exceeds the reply envelope or cannot be encoded; inspect progress before any retry.");
        }

        internal static Receipts.Receipt Uncertain(NativeAttemptLedger ledger, NativeAttemptLedger.Admission handle,
            Common.AttemptKey attempt, Common.ObservationContext context, Receipts.EffectEvidence evidence, string detail)
        {
            var candidate = Header(attempt, context);
            candidate.Uncertain = new Receipts.Uncertain { LastObserved = evidence, Detail = detail };
            return Fits(new Operations.ExecuteReply { Receipt = candidate })
                ? ledger.FinishUncertain(handle, evidence, detail)
                : ledger.FinishUncertain(handle, null, "Admitted construction evidence cannot fit the reply envelope; inspect progress before any retry.");
        }

        internal static Operations.PreviewReply Preview(Operations.PreviewReply reply) => Fits(reply) ? reply
            : new Operations.PreviewReply { Failure = Capacity(reply.Evaluated?.Context) };

        internal static Receipts.ProgressReply Progress(Receipts.ProgressReply reply) => Fits(reply) ? reply
            : new Receipts.ProgressReply { Failure = Capacity(reply.Progress?.Context) };

        private static Receipts.Receipt Header(Common.AttemptKey attempt, Common.ObservationContext context) =>
            new Receipts.Receipt { Attempt = attempt, AdmittedContext = context };

        private static Common.Failure Capacity(Common.ObservationContext context) => new Common.Failure
            { Code = Common.FailureCode.CapacityExhausted, Detail = "Complete native reply cannot fit the one MiB envelope.", ObservedContext = context };
    }
}
