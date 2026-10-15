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
    // speed-change watcher never sees it. What it applies is the run segment's one
    // ceiling (ClockCeiling.cs: frame budget plus brain heartbeat).
    internal static partial class Supervisor
    {
        internal const int NormalTicksPerSecond = 60;

        private static bool _ceilingPatched;
        private static void EnsureCeilingPatched()
        {
            if (_ceilingPatched) return;
            var getter = AccessTools.PropertyGetter(typeof(TickManager), "TickRateMultiplier");
            if (getter == null) throw new MissingMethodException("TickManager.TickRateMultiplier");
            new Harmony("homebridge.supervised-play").Patch(getter, postfix: new HarmonyMethod(typeof(Supervisor), nameof(ClampTickRate)));
            var info = Harmony.GetPatchInfo(getter);
            if (info == null || !info.Owners.Contains("homebridge.supervised-play"))
                throw new InvalidOperationException("Native tick rate ceiling patch was not installed.");
            _ceilingPatched = true;
        }

        // TickRateMultiplier postfix: nothing else about the speed changes.
        internal static void ClampTickRate(ref float __result)
        {
            if (__result <= 0f) return;
            State? s;
            lock (Gate) s = _state;
            if (s == null || !s.Active) return;
            __result = ClockCeilingMultiplier(s, __result);
        }

    }
}
