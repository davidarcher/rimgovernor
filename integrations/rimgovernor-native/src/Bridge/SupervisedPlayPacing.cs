using System;
using System.Collections.Generic;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Tick-rate reporting and frame accounting. Native never raises a speed
    // beyond the one the brain's Run asked for: Normal, Fast, Superfast and
    // Ultrafast (15x, the boost forced off) tick at the game's own multiplier,
    // and Hyperfast drives the tick boost under its own cap (30x, 1800 ticks/s;
    // HyperfastCap.cs). The one ceiling (ClockCeiling.cs) is the only clamp
    // beneath that rate. The epoch holds neverForceNormalSpeed, so the game's forced slowdown
    // never applies. Every tick of a Hyperfast epoch runs the full
    // frame check (OnUpdate) first: ownership, a player pause and the
    // tick-bounded hazard probe are checked between any two ticks of a frame,
    // however many the frame carries.
    internal static partial class Supervisor
    {
        internal const int DefaultFrameBudgetMs = ClockCeiling.DefaultFrameBudgetMs;

        private static long _frameBeganTimestamp;

        // TickManagerUpdate prefix: the frame's tick work is timed from here
        // to OnFrame.
        internal static void OnFrameBegin() => _frameBeganTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();

        // The tick rate the epoch holds now: the multiplier the game ticks at,
        // under the one ceiling.
        private static int PacedTicksPerSecond(State s)
        {
            var tm = Find.TickManager;
            if (tm == null) return 0;
            var rate = (int)Math.Round(tm.TickRateMultiplier * NormalTicksPerSecond);
            return Math.Max(0, rate);
        }

        // The one ceiling (ClockCeiling.cs) applied to the multiplier the game ticks
        // at. Pause (0) is the game's to keep. Caller holds Gate.
        private static float ClockCeilingMultiplier(State s, float multiplier)
        {
            var ceiling = s.Ceiling.TicksPerSecond;
            if (ceiling <= 0) return multiplier;
            var capped = ceiling / (float)NormalTicksPerSecond;
            return capped < multiplier ? capped : multiplier;
        }

        private static int CeilingTicksPerSecond(State s) => s.Ceiling.TicksPerSecond;

        // What bounds the epoch's rate, most binding first.
        private static RimGovernor.Contracts.Wire.Clock.PacingReason PacingReasonOf(State s)
        {
            var unclamped = UnclampedTicksPerSecond(s);
            if (s.Ceiling.TicksPerSecond > 0 && s.Ceiling.TicksPerSecond < unclamped)
                return s.Ceiling.Binding == ClockCeiling.Input.Heartbeat
                    ? RimGovernor.Contracts.Wire.Clock.PacingReason.Heartbeat
                    : RimGovernor.Contracts.Wire.Clock.PacingReason.FrameBudget;
            return RimGovernor.Contracts.Wire.Clock.PacingReason.Fixed;
        }

        // The rate the speed asks for before the ceiling: what ClampTickRate saw
        // last frame, Hyperfast's cap applied. 0 while paused
        // or before the first frame.
        private static int UnclampedTicksPerSecond(State s) =>
            (int)Math.Round(s.UnclampedMultiplier * NormalTicksPerSecond);

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

        // The session's frame account (frames that carried ticks), reset when the loaded game changes.
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
        private static (ulong Frames, ulong OverBudget, double MaxFrameMs) FrameAccount()
        {
            FramesFor(Current.Game);
            return (_pacedFrames, _pacedFramesOverBudget, _maxPacedFrameMs);
        }
    }
}
