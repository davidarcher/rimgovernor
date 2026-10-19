#nullable enable
using System;
using HarmonyLib;
using Verse;

namespace HomeBridge.BridgeTools
{
    // The tick-rate clamp. The game reads TickManager.TickRateMultiplier once
    // per frame for the tick loop's per-frame cap and per-tick real time, so a
    // lower value is exactly a lower tick rate. The clamp lives in a postfix on
    // that getter, so the requested speed stays what it was and the external
    // speed-change watcher never sees it. It applies Hyperfast's 30x cap
    // (HyperfastCap.cs) whenever the boost is on, run segment or not (#3605), and
    // beneath it, inside a run segment, the one ceiling (ClockCeiling.cs: frame
    // budget plus brain heartbeat). NativeStartup installs it, so the cap holds
    // for anything that sets the boost; a run segment's EnsurePatched finds it installed.
    internal static partial class Supervisor
    {
        internal const int NormalTicksPerSecond = 60;

        private static bool _tickRateClampPatched;
        internal static void EnsureTickRateClampPatched()
        {
            if (_tickRateClampPatched) return;
            var getter = AccessTools.PropertyGetter(typeof(TickManager), "TickRateMultiplier");
            if (getter == null) throw new MissingMethodException("TickManager.TickRateMultiplier");
            new Harmony("homebridge.supervised-play").Patch(getter, postfix: new HarmonyMethod(typeof(Supervisor), nameof(ClampTickRate)));
            var info = Harmony.GetPatchInfo(getter);
            if (info == null || !info.Owners.Contains("homebridge.supervised-play"))
                throw new InvalidOperationException("Native tick rate clamp patch was not installed.");
            _tickRateClampPatched = true;
        }

        // TickRateMultiplier postfix: nothing else about the speed changes.
        internal static void ClampTickRate(ref float __result)
        {
            __result = HyperfastCap.Apply(BoostAvailable && (bool)BoostField.GetValue(null)!, __result);
            State? s;
            lock (Gate) s = _state;
            if (s == null || !s.Active) return;
            s.UnclampedMultiplier = __result;
            if (__result <= 0f) return;
            __result = ClockCeilingMultiplier(s, __result);
        }

    }
}
