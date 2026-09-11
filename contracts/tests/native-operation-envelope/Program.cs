using System;
using System.Text;
using System.Runtime.CompilerServices;
using Google.Protobuf;
using HarmonyLib;
using HomeBridge.BridgeTools;
using Common = RimGovernor.Protocol.Common;
using Authority = RimGovernor.Protocol.Authority;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

// Only the boundary's transport limit is substituted; serialization, ledger,
// outcome selection and live Harmony verification use production code.
namespace HomeBridge.BridgeTools { internal static class ProtoBoundary { internal const int MaximumEnvelopeBytes = 1024 * 1024; } }

internal static class Program
{
    private static int checks;
    private static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }
    private static readonly Common.Identity Identity = new Common.Identity { ColonyId = "colony", LoadToken = "load", MapId = 0 };
    private static readonly Common.ObservationContext Context = new Common.ObservationContext { Identity = Identity, Tick = 10, NativeGeneration = 2 };
    private static readonly Authority.Owner Owner = new Authority.Owner { ControllerSessionId = "session", PlayerDirection = 1 };
    private static Operations.ExecuteRequest Request(ulong id) => new Operations.ExecuteRequest
    {
        Precondition = new Authority.WritePrecondition { Identity = Identity, ExpectedGeneration = 2, LeaseId = "lease",
            Attempt = new Common.AttemptKey { ControllerSessionId = "session", ActionId = "action", AttemptId = id } },
        Operation = new Operations.Operation { PlaceBuilding = new Operations.PlaceBuilding() }
    };
    private static void Main()
    {
        var exact = new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation { Context = Context, Reason = "" } };
        int overhead = Encoding.UTF8.GetByteCount(JsonFormatter.Default.Format(exact));
        exact.Evaluated.Reason = new string('a', ProtoBoundary.MaximumEnvelopeBytes - overhead);
        Check(NativeOperationEnvelope.Fits(exact), "exact one MiB accepted");
        Check(ReferenceEquals(NativeOperationEnvelope.Preview(exact), exact), "complete exact-limit preview retained");
        exact.Evaluated.Reason += "a";
        var refused = NativeOperationEnvelope.Preview(exact);
        Check(refused.Failure?.Code == Common.FailureCode.CapacityExhausted && refused.Failure.ObservedContext.Equals(Context), "one byte overflow becomes contextual typed refusal");
        Check(NativeOperationEnvelope.Fits(refused), "overflow refusal encodes");
        exact.Evaluated.Reason = new string('\u00e9', (ProtoBoundary.MaximumEnvelopeBytes - overhead) / 2 + 1);
        Check(!NativeOperationEnvelope.Fits(exact), "UTF8 byte size rather than character count enforced");
        exact.Evaluated.Reason = new string('\n', ProtoBoundary.MaximumEnvelopeBytes / 2);
        Check(!NativeOperationEnvelope.Fits(exact), "JSON escape expansion included");

        var ledger = new NativeAttemptLedger(Identity);
        var oversized = new Receipts.EffectEvidence { Construction = new Receipts.ConstructionEffect() };
        for (int i = 0; i < 4096; i++) oversized.Construction.WipedThingIds.Add(new string('x', 256));
        var request = Request(1);
        var admission = ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, Context, Owner);
        var receipt = NativeOperationEnvelope.Applied(ledger, admission.Handle, request.Precondition.Attempt, Context, Owner, oversized);
        Check(receipt.Uncertain != null && receipt.Uncertain.LastObserved == null, "oversized observed effects finalize uncertain without truncation");
        Check(receipt.AdmittedContext.Equals(Context) && receipt.AuthorizingOwner.Equals(Owner), "uncertainty retains admission and owner");
        var retry = ledger.Inspect("rimgovernor.operations.v1.Operations/Execute", request);
        Check(retry.Kind == NativeAttemptLedger.DecisionKind.Replay && retry.Reply.Receipt.Equals(receipt), "retry replays original uncertain receipt");
        Check(NativeOperationEnvelope.Fits(retry.Reply), "retry is encodable");
        var lookup = ledger.Lookup(request.Precondition.Attempt, Context);
        Check(lookup.Receipt.Equals(receipt) && NativeOperationEnvelope.Fits(lookup), "lookup is encodable with original receipt");
        var next = Request(2);
        var admitted = ledger.Admit("rimgovernor.operations.v1.Operations/Execute", next, Context, Owner);
        var failed = NativeOperationEnvelope.Uncertain(ledger, admitted.Handle, next.Precondition.Attempt, Context, Owner, oversized, "Partial write");
        Check(failed.Uncertain != null && failed.Uncertain.LastObserved == null && NativeOperationEnvelope.Fits(new Operations.ExecuteReply { Receipt = failed }), "exception path also bounds partial evidence");
        var small = new Receipts.EffectEvidence { Construction = new Receipts.ConstructionEffect { OriginThingId = "Wall1", CurrentThingId = "Wall1", Present = true } };
        next = Request(3); admitted = ledger.Admit("rimgovernor.operations.v1.Operations/Execute", next, Context, Owner);
        var applied = NativeOperationEnvelope.Applied(ledger, admitted.Handle, next.Precondition.Attempt, Context, Owner, small);
        Check(applied.Applied.Observed.Equals(small), "bounded applied evidence is preserved");
        var progress = NativeOperationEnvelope.Progress(new Receipts.ProgressReply { Progress = new Receipts.Progress
            { Context = Context, Attempt = request.Precondition.Attempt, CompleteInspection = true, Completed = new Receipts.CompletedEffect { Evidence = oversized } } });
        Check(progress.Failure?.Code == Common.FailureCode.CapacityExhausted && NativeOperationEnvelope.Fits(progress), "oversized progress cannot claim truncated completion");
        HookChecks();
        Console.WriteLine("Operation envelope and live hook metadata passed " + checks + " assertions; no gameplay.");
    }

    private static void HookChecks()
    {
        const string owner = "rimgovernor.tests.construction-health";
        var harmony = new Harmony(owner);
        var target = AccessTools.Method(typeof(Program), nameof(Target));
        var before = AccessTools.Method(typeof(Program), nameof(Before));
        var after = AccessTools.Method(typeof(Program), nameof(After));
        var wrong = AccessTools.Method(typeof(Program), nameof(Wrong));
        var hooks = new NativeConstructionHookSet(owner);
        hooks.Add(target, prefix: before, finalizer: after);
        Check(!hooks.Ready(1), "uninstalled required target fails closed");
        harmony.Patch(target, prefix: new HarmonyMethod(before), finalizer: new HarmonyMethod(after));
        Check(hooks.Ready(1), "exact required patches are available");
        Check(!hooks.Ready(2), "partial target inventory fails closed");
        harmony.Unpatch(target, after);
        Check(!hooks.Ready(1), "removed finalizer detected despite same-owner prefix");
        harmony.Patch(target, finalizer: new HarmonyMethod(wrong));
        Check(!hooks.Ready(1), "wrong same-owner finalizer cannot satisfy required hook");
        new Harmony(owner + ".other").Patch(target, finalizer: new HarmonyMethod(after));
        Check(!hooks.Ready(1), "correct patch under foreign owner cannot satisfy required hook");
        harmony.Patch(target, finalizer: new HarmonyMethod(after));
        Check(hooks.Ready(1), "restored exact patch recognized live");
        harmony.UnpatchAll(owner); harmony.UnpatchAll(owner + ".other");
        Check(!hooks.Ready(1), "full removal detected live");
    }
    [MethodImpl(MethodImplOptions.NoInlining)] private static void Target() { }
    private static void Before() { }
    private static void After() { }
    private static void Wrong() { }
}
