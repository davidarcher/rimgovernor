using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using HomeBridge.BridgeTools;

// Live Harmony inventory and method identity for NativeConstructionHookSet (#935), restored from
// the native-operation-envelope probe deleted in a7d8a122d.
internal static class NativeHookSetProbe
{
    private static int checks;
    private static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }

    private static int Main()
    {
        try
        {
            HookChecks();
            MethodChecks();
            Console.WriteLine("NativeConstructionHookSet passed " + checks + " assertions against real Harmony.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL after " + checks + " checks: " + e.Message); return 1; }
    }

    private static void HookChecks()
    {
        const string owner = "rimgovernor.tests.construction-health";
        var harmony = new Harmony(owner);
        var target = AccessTools.Method(typeof(NativeHookSetProbe), nameof(Target));
        var before = AccessTools.Method(typeof(NativeHookSetProbe), nameof(Before));
        var after = AccessTools.Method(typeof(NativeHookSetProbe), nameof(After));
        var wrong = AccessTools.Method(typeof(NativeHookSetProbe), nameof(Wrong));
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

    private static void MethodChecks()
    {
        var inherited = AccessTools.Method(typeof(InheritsDestroy), nameof(DeclaresDestroy.Destroy));
        var declared = AccessTools.DeclaredMethod(typeof(DeclaresDestroy), nameof(DeclaresDestroy.Destroy));
        Check(inherited.ReflectedType != declared.ReflectedType, "fixture reproduces distinct inherited reflection views");
        Check(inherited != declared, "raw method comparison rejects inherited implementation");
        Check(NativeConstructionHookSet.SameMethod(inherited, declared), "inherited outermost implementation matches native hook metadata");
        Check(!NativeConstructionHookSet.SameMethod(AccessTools.Method(typeof(OverridesDestroy), nameof(DeclaresDestroy.Destroy)), declared), "derived override cannot be confirmed by successful base callback");
        Check(!NativeConstructionHookSet.SameMethod(null, declared), "missing method cannot establish cancellation");
    }

    [MethodImpl(MethodImplOptions.NoInlining)] private static void Target() { }
    private static void Before() { }
    private static void After() { }
    private static void Wrong() { }
    private class DeclaresDestroy { public virtual void Destroy() { } }
    private sealed class InheritsDestroy : DeclaresDestroy { }
    private sealed class OverridesDestroy : DeclaresDestroy { public override void Destroy() { base.Destroy(); throw new Exception(); } }
}
