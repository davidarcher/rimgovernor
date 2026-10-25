using System;
using Verse;

namespace RimGovernor.Native
{
    // How a Run's wire speed drives the game's clock (#3604). Five wire speeds
    // share four TimeSpeeds: Ultrafast and Hyperfast are both TimeSpeed.Ultrafast
    // and differ only in TickManager.UltraSpeedBoost, so a run segment records
    // the wire speed, not the TimeSpeed.
    //   Normal, Fast, Superfast  the game's own multiplier; the boost is left as found.
    //   Ultrafast                vanilla's 15x: the boost is owned as false, because
    //                            the host tools may have set it true globally.
    //   Hyperfast                the boost owned as true, which the tick-rate clamp
    //                            caps at 30x (HyperfastCap.cs).
    // Pure, with no game reads, so the contract probe compiles it directly.
    internal static class ClockRunSpeed
    {
        internal static bool Ordinary(RimGovernor.Brain.GameFacts.Clock.Speed speed) => speed == RimGovernor.Brain.GameFacts.Clock.Speed.Normal || speed == RimGovernor.Brain.GameFacts.Clock.Speed.Fast
            || speed == RimGovernor.Brain.GameFacts.Clock.Speed.Superfast || speed == RimGovernor.Brain.GameFacts.Clock.Speed.Ultrafast || speed == RimGovernor.Brain.GameFacts.Clock.Speed.Hyperfast;

        internal static TimeSpeed Native(RimGovernor.Brain.GameFacts.Clock.Speed speed) => speed == RimGovernor.Brain.GameFacts.Clock.Speed.Normal ? TimeSpeed.Normal
            : speed == RimGovernor.Brain.GameFacts.Clock.Speed.Fast ? TimeSpeed.Fast
            : speed == RimGovernor.Brain.GameFacts.Clock.Speed.Superfast ? TimeSpeed.Superfast
            : speed == RimGovernor.Brain.GameFacts.Clock.Speed.Ultrafast || speed == RimGovernor.Brain.GameFacts.Clock.Speed.Hyperfast ? TimeSpeed.Ultrafast
            : throw new ArgumentOutOfRangeException(nameof(speed));

        // The UltraSpeedBoost value a run at this speed owns; null leaves the flag as the run found it.
        internal static bool? Boost(RimGovernor.Brain.GameFacts.Clock.Speed speed) => speed == RimGovernor.Brain.GameFacts.Clock.Speed.Hyperfast ? true
            : speed == RimGovernor.Brain.GameFacts.Clock.Speed.Ultrafast ? false : (bool?)null;
    }

    // A run segment's ownership of the static TickManager.UltraSpeedBoost: the
    // first Own records the value found, a later Own (a speed change in place)
    // keeps that record, and Release puts it back.
    internal sealed class BoostOwnership
    {
        private bool _prior;
        internal bool Owned { get; private set; }

        // Owns the value a speed asks for (ClockRunSpeed.Boost), or releases when it asks for none.
        internal void For(bool? value, Func<bool> read, Action<bool> write)
        {
            if (value is not bool owned) { Release(write); return; }
            if (!Owned) { _prior = read(); Owned = true; }
            write(owned);
        }

        internal void Release(Action<bool> write)
        {
            if (!Owned) return;
            write(_prior);
            Owned = false;
        }
    }
}
