using System;
using HomeBridge.BridgeTools;

internal static class NativeClockFaceProbe
{
    private static int checks;
    private static void Check(string actual, string expected, string name) { checks++; if (actual != expected) throw new Exception($"{name}: {actual} != {expected}"); }
    internal static void Invoke()
    {
        Check(ClockFace.Format(0), "12:00:00 AM", "midnight is 12 AM");
        Check(ClockFace.Format(1), "12:00:01 AM", "a tick is 1.44 s, floored");
        Check(ClockFace.Format(42), "12:01:00 AM", "42 ticks is 60.48 s");
        Check(ClockFace.Format(2500), "1:00:00 AM", "one game hour (2500 ticks) is 1 AM");
        Check(ClockFace.Format(30000), "12:00:00 PM", "half a day is noon");
        Check(ClockFace.Format(32500), "1:00:00 PM", "13h is 1 PM");
        Check(ClockFace.Format(59999), "11:59:58 PM", "the last tick of the day");
        Check(ClockFace.Format(60000), "12:00:00 AM", "wraps at the day");
        Check(ClockFace.Format(-2500), "11:00:00 PM", "a negative tick wraps back into the day");
        Console.WriteLine("Clock face passed " + checks + " assertions; pure formatting, the DateReadout prefix is live-only.");
    }
}
