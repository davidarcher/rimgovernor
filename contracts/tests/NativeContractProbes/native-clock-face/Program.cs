using System;
using HomeBridge.BridgeTools;

internal static class NativeClockFaceProbe
{
    private static int checks;
    private static void Check(string actual, string expected, string name) { checks++; if (actual != expected) throw new Exception($"{name}: {actual} != {expected}"); }
    internal static void Invoke()
    {
        Check(ClockFace.Format(0, twelveHour: true), "12:00:00 AM", "midnight is 12 AM");
        Check(ClockFace.Format(0, twelveHour: false), "00:00:00", "midnight is 00:00:00 in 24-hour form");
        Check(ClockFace.Format(1, twelveHour: true), "12:00:01 AM", "a tick is 1.44 s, floored");
        Check(ClockFace.Format(42, twelveHour: true), "12:01:00 AM", "42 ticks is 60.48 s");
        Check(ClockFace.Format(2500, twelveHour: true), "1:00:00 AM", "one game hour (2500 ticks) is 1 AM");
        Check(ClockFace.Format(2500, twelveHour: false), "01:00:00", "one game hour is 01:00:00 in 24-hour form");
        Check(ClockFace.Format(30000, twelveHour: true), "12:00:00 PM", "half a day is noon");
        Check(ClockFace.Format(32500, twelveHour: true), "1:00:00 PM", "13h is 1 PM");
        Check(ClockFace.Format(32500, twelveHour: false), "13:00:00", "13h is 13:00:00 in 24-hour form");
        Check(ClockFace.Format(59999, twelveHour: true), "11:59:58 PM", "the last tick of the day");
        Check(ClockFace.Format(59999, twelveHour: false), "23:59:58", "the last tick of the day in 24-hour form");
        Check(ClockFace.Format(60000, twelveHour: true), "12:00:00 AM", "wraps at the day");
        Check(ClockFace.Format(-2500, twelveHour: true), "11:00:00 PM", "a negative tick wraps back into the day");
        Console.WriteLine("Clock face passed " + checks + " assertions; pure formatting, the DateReadout prefix is live-only.");
    }
}
