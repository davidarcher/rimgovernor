using System;
using HomeBridge.BridgeTools;
using RimGovernor.Contracts.Wire;
using Obs = RimGovernor.Contracts.Wire.Observations;

// The daylight field of the gear census (#3526): the count of daytime hours over a sampled day, and the field's wire round trip.
internal static class NativeDaylightHoursProbe
{
    internal static void Invoke()
    {
        // A day with the sun up for hours 6..17 (12 daytime hours) of the 24 hour marks.
        float Glow(int ticks) => ticks / DaylightHours.TicksPerHour is >= 6 and < 18 ? 1f : 0f;
        Require(DaylightHours.Of(Glow, g => g > 0.6f) == 12f, "twelve sunlit hour marks count as twelve hours");
        Require(DaylightHours.Of(_ => 0f, g => g > 0.6f) == 0f, "polar night has no daylight");
        Require(DaylightHours.Of(_ => 1f, g => g > 0.6f) == 24f, "midnight sun fills the day");
        Require(DaylightHours.Of(_ => 0.6f, g => g > 0.6f) == 0f, "the game's threshold is exclusive");

        var sampled = 0;
        DaylightHours.Of(ticks => { sampled++; return ticks % DaylightHours.TicksPerHour == 0 && ticks < 24 * DaylightHours.TicksPerHour ? 0f : throw new Exception("sample off the day"); }, _ => false);
        Require(sampled == 24, "one sample per hour mark of the day");

        var snapshot = new Obs.GearSnapshot { DaylightHoursByTwelfth = [8f, 9.5f, 12f, 14f, 15f, 16f, 15f, 14f, 12f, 10f, 8f, 7f], CurrentTwelfth = 3 };
        var back = WireJson.Deserialize<Obs.GearSnapshot>(WireJson.Serialize(snapshot));
        Require(back.DaylightHoursByTwelfth.Count == 12 && back.DaylightHoursByTwelfth[1] == 9.5f, "daylight hours survive the wire");
        Require(WireJson.Deserialize<Obs.GearSnapshot>("{}").DaylightHoursByTwelfth.Count == 0, "an unread daylight field stays empty");
        Console.WriteLine("native-daylight-hours: hour counting, sampling and the wire field passed");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
