#nullable enable

using System;
using System.Diagnostics;

namespace HomeBridge.BridgeTools
{
    /// Wall time the supervisor's own stop/start transitions account for
    /// (issue #621): the paused time from each epoch's stop to the next
    /// start, gaps no controller observed included, and the running time of
    /// each epoch, both on the monotonic Stopwatch clock. Clock.Status
    /// reports them as paused_ms / running_ms; a throughput report takes the
    /// difference between its first and last status sample, so a stop the
    /// controller never polled still counts. A changed game session resets
    /// both: the reload between two staged runs is nobody's pause.
    internal static class ClockPauseAccounting
    {
        private static readonly object Gate = new object();
        private static object? _session;
        private static long _pausedTicks, _runningTicks;
        private static long? _stoppedAt, _startedAt;

        /// An epoch started for the given game session.
        internal static void Started(object? session)
        {
            lock (Gate)
            {
                var now = Stopwatch.GetTimestamp();
                if (!ReferenceEquals(session, _session)) { _session = session; _pausedTicks = 0; _runningTicks = 0; _stoppedAt = null; }
                else if (_stoppedAt.HasValue) _pausedTicks += Math.Max(0, now - _stoppedAt.Value);
                _stoppedAt = null; _startedAt = now;
            }
        }

        /// The active epoch stopped (went inactive; a pause still pending
        /// is not a stop).
        internal static void Stopped()
        {
            lock (Gate)
            {
                var now = Stopwatch.GetTimestamp();
                if (_startedAt.HasValue) _runningTicks += Math.Max(0, now - _startedAt.Value);
                _startedAt = null; _stoppedAt = now;
            }
        }

        /// Paused time so far for the session, the open gap since the last
        /// stop included.
        internal static ulong PausedMs(object? session)
        {
            lock (Gate)
            {
                if (!ReferenceEquals(session, _session)) return 0;
                var ticks = _pausedTicks;
                if (_stoppedAt.HasValue) ticks += Math.Max(0, Stopwatch.GetTimestamp() - _stoppedAt.Value);
                return Ms(ticks);
            }
        }

        /// Running time so far for the session, the open epoch included.
        internal static ulong RunningMs(object? session)
        {
            lock (Gate)
            {
                if (!ReferenceEquals(session, _session)) return 0;
                var ticks = _runningTicks;
                if (_startedAt.HasValue) ticks += Math.Max(0, Stopwatch.GetTimestamp() - _startedAt.Value);
                return Ms(ticks);
            }
        }

        private static ulong Ms(long stopwatchTicks) { return (ulong)(stopwatchTicks * 1000L / Stopwatch.Frequency); }
    }
}
