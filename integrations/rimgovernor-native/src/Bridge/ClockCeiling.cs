#nullable enable
using System;

namespace HomeBridge.BridgeTools
{
    // The one ceiling on an epoch's tick rate (#3171): the lower of two inputs.
    //   frame budget  a frame whose tick work ran over the budget lowers the ceiling
    //                 at once; recovery is one gradual step per ramp interval, after
    //                 a hold, and only while frames sit well under the budget, so the
    //                 rate does not oscillate across it.
    //   heartbeat     brain contact (the explicit Heartbeat call, a Run) refreshes the beat; silence past the tolerance
    //                 drops the ceiling to Normal, and once the brain is heard again
    //                 the ceiling doubles back every ramp interval.
    // The brain asks for a speed; native never exceeds this ceiling. Pure state with
    // no Verse types, so the contract probe compiles it directly. The numbers below
    // are conservative defaults, UNMEASURED: #3199 owns the baseline that tunes them.
    internal sealed class ClockCeiling
    {
        internal const int NormalTicksPerSecond = 60;
        // Where a ramp stops and the ceiling is released: Hyperfast's capped rate
        // (HyperfastCap.cs, 30x), the highest rate any epoch asks for.
        internal const int ReleaseTicksPerSecond = HyperfastCap.TicksPerSecond;
        // Frame budget as before (30 ms of tick work per frame).
        internal const int DefaultFrameBudgetMs = 30;
        internal const double FrameLower = 0.7;
        internal const double FrameRaise = 1.25;
        // Raise only while the frame's tick work is under this share of the budget;
        // 0.7 * 1.25 < 1 keeps a raised rate's expected cost inside the band.
        internal const double FrameHeadroom = 0.75;
        internal const long FrameRampMs = 250;
        // After a drop, hold this long before the first raise.
        internal const long FrameHoldMs = 1000;
        // The brain's clock poll runs about every second and backs off to 2 s, and its
        // call timeout is at most 7 s; five seconds tolerates a planning wave (1.5-2.5 s) plus a slow
        // poll and stays inside the timeout.
        internal const long HeartbeatToleranceMs = 5000;
        internal const long HeartbeatRampMs = 100;

        internal enum Input { None = 0, FrameBudget = 1, Heartbeat = 2 }

        private int _frameTps;     // 0 = not limiting
        private int _beatTps;      // 0 = not limiting
        private long _lastDropMs = long.MinValue / 2, _lastRaiseMs = long.MinValue / 2, _lastBeatRampMs, _lastBeatMs;

        internal ClockCeiling(long nowMs, int frameBudgetMs = DefaultFrameBudgetMs)
        {
            FrameBudgetMs = frameBudgetMs;
            _lastBeatMs = nowMs;
        }

        internal int FrameBudgetMs { get; }
        internal int FrameTicksPerSecond => _frameTps;
        internal int HeartbeatTicksPerSecond => _beatTps;

        // The ceiling in force, 0 = none.
        internal int TicksPerSecond
        {
            get
            {
                if (_frameTps > 0 && _beatTps > 0) return Math.Min(_frameTps, _beatTps);
                return _frameTps > 0 ? _frameTps : _beatTps;
            }
        }

        // Which input binds; the heartbeat wins a tie because it is the safety input.
        internal Input Binding
        {
            get
            {
                if (_beatTps > 0 && (_frameTps == 0 || _beatTps <= _frameTps)) return Input.Heartbeat;
                return _frameTps > 0 ? Input.FrameBudget : Input.None;
            }
        }

        internal void Beat(long nowMs)
        {
            if (nowMs > _lastBeatMs) _lastBeatMs = nowMs;
        }

        // One frame. ratePlayed: the ticks per second the frame ran at; ticks: how
        // many ticks the frame carried (a frame with none says nothing about tick cost).
        internal void Frame(long nowMs, double frameMs, int ticks, int ratePlayed)
        {
            UpdateHeartbeat(nowMs);
            if (ticks <= 0) return;
            if (frameMs > FrameBudgetMs)
            {
                var basis = _frameTps > 0 ? Math.Min(_frameTps, ratePlayed) : ratePlayed;
                var lowered = Math.Max(NormalTicksPerSecond, (int)(basis * FrameLower));
                if (_frameTps == 0 || lowered < _frameTps) _frameTps = lowered;
                _lastDropMs = nowMs;
                return;
            }
            if (_frameTps == 0 || frameMs > FrameBudgetMs * FrameHeadroom) return;
            if (nowMs - _lastDropMs < FrameHoldMs || nowMs - _lastRaiseMs < FrameRampMs) return;
            _lastRaiseMs = nowMs;
            var next = Math.Max(_frameTps + 1, (int)(_frameTps * FrameRaise));
            _frameTps = next >= ReleaseTicksPerSecond ? 0 : next;
        }

        private void UpdateHeartbeat(long nowMs)
        {
            if (nowMs - _lastBeatMs > HeartbeatToleranceMs)
            {
                _beatTps = NormalTicksPerSecond;
                _lastBeatRampMs = nowMs;
                return;
            }
            if (_beatTps == 0 || nowMs - _lastBeatRampMs < HeartbeatRampMs) return;
            _lastBeatRampMs = nowMs;
            var next = _beatTps * 2;
            _beatTps = next >= ReleaseTicksPerSecond ? 0 : next;
        }

        // The ceiling applied to a rate; 0 = no ceiling.
        internal static int Apply(int ceilingTps, int rateTps)
            => ceilingTps > 0 && ceilingTps < rateTps ? ceilingTps : rateTps;
    }
}
