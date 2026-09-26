#nullable enable
using System;
using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace HomeBridge.BridgeTools
{
    // The blind-tick regulator (issue #583). Blind ticks are the ticks the
    // controller has not observed: those since its last read (a status,
    // events or bundle read) or, when a journal row it has not acknowledged
    // is older, those since that row. Past the epoch's budget the epoch is
    // throttled to Normal at the next frame; once the controller catches up
    // the ceiling doubles every ramp interval back to unlimited. Neither
    // transition ends the epoch: the requested speed stays what it was and
    // the clamp lives in a TickRateMultiplier postfix, so the external
    // speed-change watcher never sees it. The lease is the fault guard
    // (controller gone), the budget the liveness guard (controller slow).
    internal static partial class Supervisor
    {
        internal const int NormalTicksPerSecond = 60;
        // Doubling from Normal reaches the boosted rate (150 x 60) in eight
        // ramps; past it the regulator releases its ceiling entirely.
        private const int RegulatorReleaseTicksPerSecond = 150 * NormalTicksPerSecond;
        private const int RegulatorRampMs = 100;
        // Rows the controller has not acknowledged, oldest first; a backlog
        // this long is already held at Normal, so the tail need not be kept.
        private const int UnackedRowsKept = 256;

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

        // TickRateMultiplier postfix: the game reads it once per frame for the
        // tick loop's per-frame cap and per-tick real time, so a lower value
        // is exactly a lower tick rate; nothing else about the speed changes.
        internal static void ClampTickRate(ref float __result)
        {
            if (__result <= 0f) return;
            State? s;
            lock (Gate) s = _state;
            if (s == null || !s.Active) return;
            __result = PacedMultiplier(s, __result);
            var ceiling = EffectiveTicksPerSecond(s);
            if (ceiling <= 0) return;
            var multiplier = ceiling / (float)NormalTicksPerSecond;
            if (multiplier < __result) __result = multiplier;
        }

        // The owner's ceiling and the regulator's, whichever is lower; 0 = none.
        private static int EffectiveTicksPerSecond(State s)
        {
            if (s.MaxTicksPerSecond > 0 && s.RegulatedTicksPerSecond > 0) return Math.Min(s.MaxTicksPerSecond, s.RegulatedTicksPerSecond);
            return s.MaxTicksPerSecond > 0 ? s.MaxTicksPerSecond : s.RegulatedTicksPerSecond;
        }

        // The controller observed the world: a status or bundle read landed on
        // the main thread, so whatever it does next rests on this tick. An
        // events poll is not an observation (it fetches the journal, not
        // facts, and a held poll would otherwise refresh the anchor every
        // time a row, including the regulator's own, released it); it only
        // acknowledges rows, below. Caller holds Gate.
        private static void NoteControllerRead(State? s)
        {
            if (s == null || !s.Active || Find.TickManager == null) return;
            s.LastReadTick = Find.TickManager.TicksGame;
        }
        internal static void NoteControllerRead()
        {
            lock (Gate) NoteControllerRead(_state);
        }
        // An events read with after_cursor acknowledges every row at or before
        // it. Caller holds Gate.
        private static void AcknowledgeRows(State? s, long afterCursor)
        {
            if (s == null || !s.Active || afterCursor <= s.AckedCursor) return;
            s.AckedCursor = afterCursor;
            while (s.Unacked.Count != 0 && s.Unacked[0].Key <= s.AckedCursor) s.Unacked.RemoveAt(0);
        }

        // A row the controller must come and read. Regulator rows are not
        // news the controller has to react to, so they never anchor the budget
        // (a recovery row would otherwise re-throttle the epoch it released).
        private static void NoteUnackedRow(State s, string kind, long cursor, int tick)
        {
            if (s.BlindTickBudget <= 0 || kind == "regulated" || s.Unacked.Count >= UnackedRowsKept) return;
            s.Unacked.Add(new KeyValuePair<long, int>(cursor, tick));
        }

        // The oldest tick the controller has not seen.
        private static int BlindAnchor(State s)
            => s.Unacked.Count != 0 ? Math.Min(s.Unacked[0].Value, s.LastReadTick) : s.LastReadTick;

        private static int BlindTicks(State s)
            => s.BlindTickBudget > 0 ? Math.Max(0, s.LastTick - BlindAnchor(s)) : 0;

        // One frame of regulation. Caller holds Gate and has refreshed LastTick.
        private static void Regulate(State s)
        {
            if (s.BlindTickBudget <= 0) return;
            var blind = BlindTicks(s);
            if (blind > s.MaxBlindTicks) s.MaxBlindTicks = blind;
            var now = NowMs();
            if (blind > s.BlindTickBudget)
            {
                if (s.RegulatedTicksPerSecond == NormalTicksPerSecond) { s.LastRampMs = now; return; }
                var released = s.RegulatedTicksPerSecond == 0;
                s.RegulatedTicksPerSecond = NormalTicksPerSecond;
                s.LastRampMs = now;
                s.RegulatorThrottles++;
                if (released) AddRegulated(s, blind, "Blind ticks " + blind + " exceed the budget of " + s.BlindTickBudget + "; throttled to Normal.");
                return;
            }
            if (s.RegulatedTicksPerSecond == 0 || now - s.LastRampMs < RegulatorRampMs) return;
            s.LastRampMs = now;
            var next = s.RegulatedTicksPerSecond * 2;
            if (next >= RegulatorReleaseTicksPerSecond)
            {
                s.RegulatedTicksPerSecond = 0;
                AddRegulated(s, blind, "Blind ticks " + blind + " are within the budget of " + s.BlindTickBudget + "; ceiling released.");
                return;
            }
            s.RegulatedTicksPerSecond = next;
        }

        private static void AddRegulated(State s, int blind, string detail)
        {
            Add("regulated", detail, s, new Dictionary<string, object?> {
                { "speed", s.RequestedSpeed.ToString() }, { "maxTicksPerSecond", (long)s.MaxTicksPerSecond },
                { "regulatedTicksPerSecond", (long)s.RegulatedTicksPerSecond }, { "blindTicks", (long)blind } });
        }
    }
}
