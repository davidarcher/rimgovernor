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
    // speed-change watcher never sees it. What it applies is the epoch's one
    // ceiling (ClockCeiling.cs: frame budget plus brain heartbeat) and the
    // owner's max_ticks_per_second.
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
            __result = PacedMultiplier(s, __result);
            __result = ClockCeilingMultiplier(s, __result);
            var ceiling = s.MaxTicksPerSecond;
            if (ceiling <= 0) return;
            var multiplier = ceiling / (float)NormalTicksPerSecond;
            if (multiplier < __result) __result = multiplier;
        }

        // The brain made contact: a status, events or bundle read. It beats the
        // ceiling's heartbeat. Caller holds Gate.
        private static void NoteControllerRead(State? s)
        {
            if (s == null || !s.Active) return;
            s.Ceiling.Beat(NowMs());
        }
        internal static void NoteControllerRead()
        {
            lock (Gate) NoteControllerRead(_state);
        }
    }
}
