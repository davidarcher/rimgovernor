#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace HomeBridge.BridgeTools
{
    /// Where the supervisor's main-thread time goes and how far apart its
    /// hazard probes ran (#626), cumulative for the loaded game session like
    /// ClockPauseAccounting: the hazard probe (Probe: letters, messages,
    /// alerts, pawns) and the fact-change digests (PublishFactChanges:
    /// research, world, conditions, zones) are timed apart, so a throughput
    /// report can take the digest share of the supervisor's cost between its
    /// first and last status sample. The probe gap is the widest tick
    /// distance between consecutive probes any epoch of the session saw; per
    /// hazard class it is the widest window in which that class could have
    /// gone undetected (the probe gap for a polled class, hook to probe for a
    /// class with a direct game hook). A changed game session resets all of
    /// it: the reload between two staged runs is nobody's probe.
    internal static class ClockProbeAccounting
    {
        private static readonly object Gate = new object();
        private static object? _session;
        private static long _probeTicks, _digestTicks;
        private static ulong _probes, _digests;
        private static long _maxProbeTickGap;
        private static readonly Dictionary<string, long> HazardGaps = new Dictionary<string, long>(StringComparer.Ordinal);

        /// An epoch started for the given game session.
        internal static void Started(object? session)
        {
            lock (Gate)
            {
                if (ReferenceEquals(session, _session)) return;
                _session = session; _probeTicks = 0; _digestTicks = 0; _probes = 0; _digests = 0; _maxProbeTickGap = 0;
                HazardGaps.Clear();
            }
        }

        /// One hazard probe pass took stopwatchTicks.
        internal static void Probed(long stopwatchTicks) { lock (Gate) { _probes++; _probeTicks += Math.Max(0, stopwatchTicks); } }

        /// One digest pass took stopwatchTicks.
        internal static void Digested(long stopwatchTicks) { lock (Gate) { _digests++; _digestTicks += Math.Max(0, stopwatchTicks); } }

        /// Consecutive probes ran tickGap game ticks apart.
        internal static void ProbeGap(long tickGap) { lock (Gate) { if (tickGap > _maxProbeTickGap) _maxProbeTickGap = tickGap; } }

        /// A hazard class went at most tickGap ticks between its last
        /// evaluation and this one.
        internal static void HazardGap(string hazardClass, long tickGap)
        {
            lock (Gate)
            {
                long known;
                if (!HazardGaps.TryGetValue(hazardClass, out known) || tickGap > known) HazardGaps[hazardClass] = tickGap;
            }
        }

        internal static double ProbeMs(object? session) { lock (Gate) return ReferenceEquals(session, _session) ? Ms(_probeTicks) : 0; }
        internal static double DigestMs(object? session) { lock (Gate) return ReferenceEquals(session, _session) ? Ms(_digestTicks) : 0; }
        internal static ulong Probes(object? session) { lock (Gate) return ReferenceEquals(session, _session) ? _probes : 0; }
        internal static ulong Digests(object? session) { lock (Gate) return ReferenceEquals(session, _session) ? _digests : 0; }
        internal static long MaxProbeTickGap(object? session) { lock (Gate) return ReferenceEquals(session, _session) ? _maxProbeTickGap : 0; }

        /// The widest observed gap per hazard class, sorted by class; a class
        /// not yet evaluated this session reads 0.
        internal static List<KeyValuePair<string, long>> HazardGapsFor(object? session)
        {
            lock (Gate)
            {
                var result = new List<KeyValuePair<string, long>>();
                if (!ReferenceEquals(session, _session)) return result;
                foreach (var pair in HazardGaps) result.Add(pair);
                result.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
                return result;
            }
        }

        private static double Ms(long stopwatchTicks) { return stopwatchTicks * 1000.0 / Stopwatch.Frequency; }
    }
}
