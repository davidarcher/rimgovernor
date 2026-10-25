using System;
using HomeBridge.BridgeTools;
using Verse;
using Clock = RimGovernor.Contracts.Wire.Clock;

// The run speeds (ClockRunSpeed.cs, HyperfastCap.cs, #3604, #3605) against vanilla's TickRateMultiplier for a loaded map
// (RimWorld 1.7, read with ilspycmd): Ultrafast is 15x, or 20000x while TickManager.UltraSpeedBoost is set. A run segment
// owns the boost through BoostOwnership. The TickRateMultiplier postfix applies HyperfastCap whenever the boost is on,
// run segment or not, and the one ceiling beneath it inside a segment.
internal static class NativeClockSpeedProbe
{
    private static int checks;
    private static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }

    private static bool boost;
    private static bool ReadBoost() => boost;
    private static void WriteBoost(bool value) => boost = value;

    private static float Vanilla(TimeSpeed speed) => speed switch
    {
        TimeSpeed.Paused => 0f,
        TimeSpeed.Normal => 1f,
        TimeSpeed.Fast => 3f,
        TimeSpeed.Superfast => 6f,
        TimeSpeed.Ultrafast => boost ? 20000f : 15f,
        _ => -1f,
    };

    // What the postfix makes of vanilla's multiplier at this game speed with the boost as it stands, before any ceiling.
    private static float Clamped(TimeSpeed speed) => HyperfastCap.Apply(boost, Vanilla(speed));

    // What one run segment at this speed ticks at: the boost it owns, then the clamped multiplier.
    private static float Played(BoostOwnership owned, Clock.Speed speed)
    {
        owned.For(ClockRunSpeed.Boost(speed), ReadBoost, WriteBoost);
        return Clamped(ClockRunSpeed.Native(speed));
    }

    internal static void Invoke()
    {
        foreach (var speed in new[] { Clock.Speed.Normal, Clock.Speed.Fast, Clock.Speed.Superfast, Clock.Speed.Ultrafast, Clock.Speed.Hyperfast })
            Check(ClockRunSpeed.Ordinary(speed), speed + " is an ordinary run speed");
        Check(!ClockRunSpeed.Ordinary((Clock.Speed)0), "an unspecified speed is not a run speed");
        Check(ClockRunSpeed.Native(Clock.Speed.Ultrafast) == TimeSpeed.Ultrafast && ClockRunSpeed.Native(Clock.Speed.Hyperfast) == TimeSpeed.Ultrafast,
            "Ultrafast and Hyperfast share the game's Ultrafast");

        // Ultrafast is vanilla's 15x with the boost off, even when the host tools set it before the Run; the stop restores it.
        boost = true;
        var ultrafast = new BoostOwnership();
        Check(Played(ultrafast, Clock.Speed.Ultrafast) == 15f && !boost && ultrafast.Owned, "Ultrafast runs at 15x with the boost forced off");
        ultrafast.Release(WriteBoost);
        Check(boost && !ultrafast.Owned, "the stop restores the boost the Run found");

        // Hyperfast's boost asks vanilla for 20000x; its own cap holds it at 30x.
        boost = false;
        var hyperfast = new BoostOwnership();
        Check(Played(hyperfast, Clock.Speed.Hyperfast) == HyperfastCap.Multiplier && boost, "Hyperfast is capped at 30x with the boost on");
        Check(HyperfastCap.Multiplier == 30f && HyperfastCap.TicksPerSecond == 1800, "the cap is 30x, 1800 ticks/s");
        Check(HyperfastCap.Apply(true, 12f) == 12f, "the cap never raises a slower rate");
        Check(HyperfastCap.Apply(false, 20000f) == 20000f, "an unboosted multiplier is never capped");

        // Outside any run segment (the host tools' Hyperfast, #3605) the boost is capped at 30x all the same.
        boost = true;
        Check(Clamped(TimeSpeed.Ultrafast) == HyperfastCap.Multiplier, "a boosted multiplier outside a segment is capped at 30x");
        Check(Clamped(TimeSpeed.Superfast) == 6f, "the boost leaves the other game speeds alone");
        boost = false;
        Check(Clamped(TimeSpeed.Ultrafast) == 15f, "unboosted Ultrafast outside a segment stays 15x");

        // A speed change in place keeps the first prior: Hyperfast, then Ultrafast, then Normal, then the stop.
        boost = false;
        var changed = new BoostOwnership();
        Check(Played(changed, Clock.Speed.Hyperfast) == 30f, "the segment starts at Hyperfast");
        Check(Played(changed, Clock.Speed.Ultrafast) == 15f && !boost, "Hyperfast to Ultrafast turns the boost off");
        Check(Played(changed, Clock.Speed.Hyperfast) == 30f && boost, "and back on");
        Check(Played(changed, Clock.Speed.Normal) == 1f && !boost && !changed.Owned, "Normal releases the boost to the value the segment found");
        changed.Release(WriteBoost);
        Check(!boost, "a stop with nothing owned writes nothing");

        // The one ceiling releases at Hyperfast's rate.
        Check(ClockCeiling.ReleaseTicksPerSecond == 1800, "the one ceiling releases at 1800 ticks/s");

        Console.WriteLine("Clock speed passed " + checks + " assertions; vanilla's multiplier modelled from RimWorld 1.7.");
    }
}
