#nullable enable

using System;
using System.Collections.Generic;

namespace HomeBridge.BridgeTools
{
    /// The hazard classes the supervisor's Probe covers and the detection
    /// bound each one declares (#626): the most game ticks a hazard of that
    /// class can exist before a probe evaluates it, at every production
    /// TimeSpeed. The probe is tick-paced (ProbeIntervalTicks, checked at the
    /// tick boundary) as well as wall-paced (ProbeIntervalMs, checked per
    /// frame), so the tick bound holds however many ticks a frame carries;
    /// a class with a direct game hook (a letter received, a colonist downed
    /// or killed) requests a probe at the next tick boundary instead of
    /// waiting for the interval. The digests (research, world, conditions,
    /// zones) are not hazards: they run on their own cadence
    /// (DigestIntervalTicks, or sooner when a game hook marks them dirty)
    /// and never inside the probe. docs/developers/architecture/
    /// hazard-detection-bounds.md carries the table this file declares.
    ///
    /// Verse-free on purpose: the native contract probe compiles it beside
    /// the typed clock runtime.
    internal static partial class Supervisor
    {
        /// Ticks between tick-paced hazard probes, at every speed.
        internal const int ProbeIntervalTicks = 30;
        /// Wall milliseconds between frame-paced hazard probes: at Normal
        /// speed (60 ticks/s) this fires first, every 6 ticks.
        internal const int ProbeIntervalMs = 100;
        /// Ticks between digest passes when no hook marked a digest dirty:
        /// the cadence the old wall-paced probe gave at Ultrafast.
        internal const int DigestIntervalTicks = 600;
        /// Bound for a class with a direct hook: the hook requests a probe at
        /// the next tick boundary.
        internal const int HookedBoundTicks = 1;

        private sealed class HazardClass
        {
            public readonly string Name; public readonly int BoundTicks; public readonly bool Hooked;
            public HazardClass(string name, int boundTicks, bool hooked) { Name = name; BoundTicks = boundTicks; Hooked = hooked; }
        }

        // Stop kinds Probe raises, with the bound each declares. A hooked
        // class whose bound is still the probe interval has a hook that
        // covers only part of it (a letter, not a transient message; a
        // hostile spawning, not one walking into range).
        private static readonly HazardClass[] HazardClasses = {
            new HazardClass("notification_batch", ProbeIntervalTicks, true),
            new HazardClass("hostile", ProbeIntervalTicks, true),
            new HazardClass("colonist_downed", HookedBoundTicks, true),
            new HazardClass("predator_hunt", ProbeIntervalTicks, false),
            new HazardClass("hunting_route_unsafe", ProbeIntervalTicks, false),
            new HazardClass("colonist_injury", ProbeIntervalTicks, false),
            new HazardClass("colonist_health", ProbeIntervalTicks, false),
            new HazardClass("medical_rest_changed", ProbeIntervalTicks, false),
        };

        /// The declared bound in ticks for a stop kind, 0 for a kind Probe
        /// does not raise.
        internal static int HazardBoundTicks(string hazardClass)
        {
            foreach (var h in HazardClasses) if (h.Name == hazardClass) return h.BoundTicks;
            return 0;
        }

        /// Whether the class has a direct game hook requesting a probe.
        internal static bool HazardHooked(string hazardClass)
        {
            foreach (var h in HazardClasses) if (h.Name == hazardClass) return h.Hooked;
            return false;
        }

        /// Every declared class name, in declaration order.
        internal static IEnumerable<string> HazardClassNames()
        {
            foreach (var h in HazardClasses) yield return h.Name;
        }

        /// Whether a hazard probe is due: a hook requested one, the tick
        /// interval elapsed, or the wall interval did. Pure so the contract
        /// probe can drive it through every speed's tick pattern.
        internal static bool ProbeDue(bool requested, long ticksSinceProbe, long msSinceProbe)
        {
            return requested || ticksSinceProbe >= ProbeIntervalTicks || msSinceProbe >= ProbeIntervalMs;
        }

        /// Whether a digest pass is due: a hook marked a digest dirty or the
        /// digest interval elapsed. Never a function of the probe.
        internal static bool DigestDue(bool dirty, long ticksSinceDigest)
        {
            return dirty || ticksSinceDigest >= DigestIntervalTicks;
        }
    }
}
