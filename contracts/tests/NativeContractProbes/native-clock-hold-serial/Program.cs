using System;
using RimGovernor.Native;

// The hold serial (ClockHoldSerial.cs, #3414) behind a fake held clock: Run resumes only when its step's serial is
// current, the way RunClock refuses before ResumeRun.
internal static class NativeClockHoldSerialProbe
{
    private static int checks;
    private static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }

    private sealed class Rig
    {
        public readonly ClockHoldSerial Serial = new ClockHoldSerial();
        public long Tick = 1000;
        public bool Held;
        public void Hold() { Serial.Entered(); Held = true; }
        // A step's read: the serial a Run planned from it quotes.
        public ulong Read() => Serial.Current;
        public string? Run(ulong planned)
        {
            var refusal = Serial.Refuses(planned);
            if (refusal == null) { Held = false; Tick++; }
            return refusal;
        }
    }

    internal static void Invoke()
    {
        // A hold entered after the planned read refuses the Run and leaves the game held.
        var rig = new Rig();
        var planned = rig.Read();
        rig.Hold();
        var refusal = rig.Run(planned);
        Check(refusal != null, "a hold after the planned read refuses the Run");
        Check(rig.Held, "a refused Run leaves the game held");
        Check(rig.Serial.Current == planned + 1, "a refusal does not move the serial");

        // A Run planned from a read of the current hold resumes.
        Check(rig.Run(rig.Read()) == null && !rig.Held, "a matching serial resumes");

        // Two holds in the same game tick: the tick does not move while held, the serial does, so a read between
        // them still refuses.
        rig.Hold();
        var tick = rig.Tick;
        var between = rig.Read();
        rig.Hold();
        Check(rig.Tick == tick, "both holds land in one tick");
        Check(rig.Run(between) != null && rig.Held, "a read between two same-tick holds still refuses");
        Check(rig.Run(rig.Read()) == null && !rig.Held, "a read after both holds resumes");

        Console.WriteLine("native-clock-hold-serial: " + checks + " checks passed");
    }
}
