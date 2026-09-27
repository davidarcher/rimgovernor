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

// Serialization, ledger, outcome selection and live Harmony verification use production code.
internal static class NativeOperationEnvelopeProbe
{
    private static int checks;
    private static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }
    private static readonly Common.Identity Identity = new Common.Identity { ColonyId = "colony", LoadToken = "load", MapId = 0 };
    private static readonly Common.ObservationContext Context = new Common.ObservationContext { Identity = Identity, Tick = 10, NativeGeneration = 2 };
    private static Operations.ExecuteRequest Request(ulong id) => new Operations.ExecuteRequest
    {
        Precondition = new Authority.WritePrecondition { Identity = Identity, ExpectedGeneration = 2,
            Attempt = new Common.AttemptKey { ControllerSessionId = "session", ActionId = "action", AttemptId = id } },
        Operation = new Operations.Operation { PlaceBuilding = new Operations.PlaceBuilding() }
    };
    internal static void Invoke()
    {
        var ledger = new NativeAttemptLedger(Identity);
        var request = Request(1);
        var small = new Receipts.EffectEvidence { Construction = new Receipts.ConstructionEffect { OriginThingId = "Wall1", CurrentThingId = "Wall1", Present = true } };
        var next = Request(3); var admitted = ledger.Admit("rimgovernor.operations.v1.Operations/Execute", next, Context);
        var applied = NativeOperationEnvelope.Applied(ledger, admitted.Handle, next.Precondition.Attempt, Context, small);
        Check(applied.Applied.Observed.Equals(small), "applied evidence is preserved");
        next = Request(4); admitted = ledger.Admit("rimgovernor.operations.v1.Operations/Execute", next, Context);
        var uncertainSmall = NativeOperationEnvelope.Uncertain(ledger, admitted.Handle, next.Precondition.Attempt, Context, small, "Partial write, still observable");
        Check(uncertainSmall.Uncertain.LastObserved != null && uncertainSmall.Uncertain.LastObserved.Equals(small) && uncertainSmall.Uncertain.Detail == "Partial write, still observable", "uncertain evidence is preserved");
        var boundedProgressReply = new Receipts.ProgressReply { Progress = new Receipts.Progress
            { Context = Context, Attempt = request.Precondition.Attempt, CompleteInspection = true, Completed = new Receipts.CompletedEffect { Evidence = small } } };
        var boundedProgress = NativeOperationEnvelope.Progress(boundedProgressReply);
        Check(ReferenceEquals(boundedProgress, boundedProgressReply) && boundedProgress.Failure == null, "progress reply passes through unchanged");
        HookChecks();
        var inherited = AccessTools.Method(typeof(InheritsDestroy), nameof(DeclaresDestroy.Destroy));
        var declared = AccessTools.DeclaredMethod(typeof(DeclaresDestroy), nameof(DeclaresDestroy.Destroy));
        Check(inherited.ReflectedType != declared.ReflectedType, "fixture reproduces distinct inherited reflection views");
        Check(inherited != declared, "raw method comparison rejects inherited implementation");
        Check(NativeConstructionHookSet.SameMethod(inherited, declared), "inherited outermost implementation matches native hook metadata");
        Check(!NativeConstructionHookSet.SameMethod(AccessTools.Method(typeof(OverridesDestroy), nameof(DeclaresDestroy.Destroy)), declared), "derived override cannot be confirmed by successful base callback");
        Check(!NativeConstructionHookSet.SameMethod(null, declared), "missing method cannot establish cancellation");
        Console.WriteLine("Operation envelope and live hook metadata passed " + checks + " assertions; no gameplay.");
    }

    private static void HookChecks()
    {
        const string owner = "rimgovernor.tests.construction-health";
        var harmony = new Harmony(owner);
        var target = AccessTools.Method(typeof(NativeOperationEnvelopeProbe), nameof(Target));
        var before = AccessTools.Method(typeof(NativeOperationEnvelopeProbe), nameof(Before));
        var after = AccessTools.Method(typeof(NativeOperationEnvelopeProbe), nameof(After));
        var wrong = AccessTools.Method(typeof(NativeOperationEnvelopeProbe), nameof(Wrong));
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
    private class DeclaresDestroy { public virtual void Destroy() { } }
    private sealed class InheritsDestroy : DeclaresDestroy { }
    private sealed class OverridesDestroy : DeclaresDestroy { public override void Destroy() { base.Destroy(); throw new Exception(); } }
}
