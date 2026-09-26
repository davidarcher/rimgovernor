#nullable enable
using System;
using System.Collections.Generic;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Player acceleration (issue #627). A PACING_PLAYER_ACCELERATED epoch at
    // Ultrafast ticks faster than the game's own 15x by raising the
    // TickRateMultiplier the tick loop reads once per frame (the same postfix
    // the ceilings use, so no speed ever looks external and the boost static
    // is never touched). The multiplier adapts to the wall time the previous
    // frame's ticks took against the epoch's frame budget: over the budget it
    // drops at once, comfortably under it climbs one step per ramp interval
    // back toward the boosted rate. The game's forced slowdown returns 1 from
    // the getter and is left alone. Every tick of such an epoch runs the full
    // frame check (OnUpdate) first, as an accelerated epoch does: ownership,
    // lease, a player pause and the tick-bounded hazard probe are checked
    // between any two ticks of a frame, however many the frame carries.
    internal static partial class Supervisor
    {
        internal const int DefaultFrameBudgetMs = 30;
        internal const int MinFrameBudgetMs = 5;
        internal const int MaxFrameBudgetMs = 45;
        // Ultrafast's own multiplier is the floor: pacing never runs a
        // player slower than the speed they chose. The boosted rate (150x,
        // 9000 ticks/s) is the ceiling.
        internal const float PacedFloorMultiplier = 15f;
        internal const float PacedCeilingMultiplier = 150f;
        // Decrease immediately; increase by PacedRaise once per PacedRampMs
        // while the frame's tick work stays under PacedHeadroom of the
        // budget, so a frame near the budget holds its rate instead of
        // oscillating across it.
        private const float PacedLower = 0.7f;
        private const float PacedRaise = 1.25f;
        private const float PacedHeadroom = 0.75f;
        private const long PacedRampMs = 250;

        private static long _frameBeganTimestamp;

        // TickManagerUpdate prefix: the frame's tick work is timed from here
        // to OnFrame.
        internal static void OnFrameBegin() => _frameBeganTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();

        // The pacing multiplier the TickRateMultiplier postfix raises
        // Ultrafast to. Caller holds Gate.
        private static float PacedMultiplier(State s, float gameMultiplier)
        {
            if (!s.PlayerPaced || s.RequestedSpeed != TimeSpeed.Ultrafast) return gameMultiplier;
            // Forced slowdown (1) and pause (0) are the game's to keep.
            if (gameMultiplier < PacedFloorMultiplier) return gameMultiplier;
            return Math.Max(gameMultiplier, s.Paced);
        }

        // One frame of pacing, from the frame's tick work. Caller holds Gate.
        private static void Pace(State s, TickManager tm, double frameMs, int ticks)
        {
            if (!s.PlayerPaced || ticks == 0) return;
            NoteFrame(frameMs, frameMs > s.FrameBudgetMs);
            var now = NowMs();
            if (tm.slower.ForcedNormalSpeed || tm.CurTimeSpeed != TimeSpeed.Ultrafast) return;
            if (frameMs > s.FrameBudgetMs)
            {
                var lowered = Math.Max(PacedFloorMultiplier, s.Paced * PacedLower);
                if (lowered < s.Paced) { s.Paced = lowered; s.PaceLowered++; }
                s.LastPaceMs = now;
                return;
            }
            if (frameMs > s.FrameBudgetMs * PacedHeadroom || now - s.LastPaceMs < PacedRampMs) return;
            s.LastPaceMs = now;
            s.Paced = Math.Min(PacedCeilingMultiplier, s.Paced * PacedRaise);
        }

        // The tick rate the epoch holds now: the paced (or speed's own)
        // multiplier under the owner's and the regulator's ceilings.
        private static int PacedTicksPerSecond(State s)
        {
            var tm = Find.TickManager;
            if (tm == null) return 0;
            var rate = (int)Math.Round(tm.TickRateMultiplier * NormalTicksPerSecond);
            return Math.Max(0, rate);
        }

        // What bounds the epoch's rate, most binding first.
        private static RimGovernor.Protocol.Clock.PacingReason PacingReasonOf(State s)
        {
            var tm = Find.TickManager;
            if (tm != null && tm.slower.ForcedNormalSpeed && tm.CurTimeSpeed != TimeSpeed.Paused)
                return RimGovernor.Protocol.Clock.PacingReason.ForcedSlowdown;
            var unclamped = UnclampedTicksPerSecond(s);
            if (s.RegulatedTicksPerSecond > 0 && s.RegulatedTicksPerSecond < unclamped
                && (s.MaxTicksPerSecond == 0 || s.RegulatedTicksPerSecond <= s.MaxTicksPerSecond))
                return RimGovernor.Protocol.Clock.PacingReason.Regulated;
            if (s.MaxTicksPerSecond > 0 && s.MaxTicksPerSecond < unclamped)
                return RimGovernor.Protocol.Clock.PacingReason.Ceiling;
            if (!s.PlayerPaced || s.RequestedSpeed != TimeSpeed.Ultrafast) return RimGovernor.Protocol.Clock.PacingReason.Fixed;
            return s.Paced < PacedCeilingMultiplier
                ? RimGovernor.Protocol.Clock.PacingReason.FrameBudget
                : RimGovernor.Protocol.Clock.PacingReason.Accelerated;
        }

        private static int UnclampedTicksPerSecond(State s)
        {
            if (s.TestAcceleration) return (int)(PacedCeilingMultiplier * NormalTicksPerSecond);
            if (s.PlayerPaced && s.RequestedSpeed == TimeSpeed.Ultrafast) return (int)(s.Paced * NormalTicksPerSecond);
            switch (s.RequestedSpeed)
            {
                case TimeSpeed.Normal: return NormalTicksPerSecond;
                case TimeSpeed.Fast: return 3 * NormalTicksPerSecond;
                case TimeSpeed.Superfast: return 12 * NormalTicksPerSecond;
                case TimeSpeed.Ultrafast: return 15 * NormalTicksPerSecond;
            }
            return 0;
        }

        // Effective speed: game ticks per wall second over the last
        // EffectiveWindowMs of frames, paused frames included, sampled every
        // EffectiveSampleMs whether or not an epoch runs. Reset when the
        // loaded game changes.
        private const long EffectiveWindowMs = 10000;
        private const long EffectiveSampleMs = 250;
        private static readonly Queue<KeyValuePair<long, int>> EffectiveSamples = new Queue<KeyValuePair<long, int>>();
        private static object? _effectiveSession;

        // Caller holds Gate.
        private static void SampleEffective()
        {
            var tm = Find.TickManager;
            if (tm == null || Current.Game == null) return;
            if (!ReferenceEquals(_effectiveSession, Current.Game)) { EffectiveSamples.Clear(); _effectiveSession = Current.Game; }
            var now = NowMs();
            var tick = tm.TicksGame;
            KeyValuePair<long, int> last = default;
            foreach (var sample in EffectiveSamples) last = sample;
            if (EffectiveSamples.Count != 0 && (now - last.Key < EffectiveSampleMs || tick < last.Value))
            {
                if (tick < last.Value) EffectiveSamples.Clear(); else return;
            }
            EffectiveSamples.Enqueue(new KeyValuePair<long, int>(now, tick));
            while (EffectiveSamples.Count > 1 && now - EffectiveSamples.Peek().Key > EffectiveWindowMs) EffectiveSamples.Dequeue();
        }

        // Caller holds Gate.
        private static double EffectiveTicksPerSecond()
        {
            if (EffectiveSamples.Count < 2 || !ReferenceEquals(_effectiveSession, Current.Game)) return 0;
            var first = EffectiveSamples.Peek();
            KeyValuePair<long, int> last = default;
            foreach (var sample in EffectiveSamples) last = sample;
            var ms = last.Key - first.Key;
            return ms <= 0 ? 0 : (last.Value - first.Value) * 1000.0 / ms;
        }

        // The session's frame account, reset when the loaded game changes.
        private static object? _framesSession;
        private static ulong _pacedFrames, _pacedFramesOverBudget;
        private static double _maxPacedFrameMs;
        private static void FramesFor(object? game)
        {
            if (ReferenceEquals(_framesSession, game)) return;
            _framesSession = game; _pacedFrames = 0; _pacedFramesOverBudget = 0; _maxPacedFrameMs = 0;
        }
        // Caller holds Gate.
        private static void NoteFrame(double frameMs, bool over)
        {
            FramesFor(Current.Game);
            _pacedFrames++;
            if (over) _pacedFramesOverBudget++;
            if (frameMs > _maxPacedFrameMs) _maxPacedFrameMs = frameMs;
        }
        // Caller holds Gate.
        private static void ReportFrames(RimGovernor.Protocol.Clock.Status status)
        {
            FramesFor(Current.Game);
            status.PacedFrames = _pacedFrames; status.PacedFramesOverBudget = _pacedFramesOverBudget; status.MaxPacedFrameMs = _maxPacedFrameMs;
        }

        internal static int ClampFrameBudget(int ms) => ms == 0 ? DefaultFrameBudgetMs : Clamp(ms, MinFrameBudgetMs, MaxFrameBudgetMs);
    }
}
