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

        var climate = new Obs.GearClimate { Temperatures = [-5, -3, 2, 8, 14, 20, 24, 22, 16, 9, 2, -4], DaylightHours = [8, 9.5, 12, 14, 15, 16, 15, 14, 12, 10, 8, 7],
            CurrentTwelfth = 3, TicksToNextTwelfth = 60000 };
        var back = WireJson.Deserialize<Obs.GearClimate>(WireJson.Serialize(climate));
        Require(back.DaylightHours.Count == 12 && back.DaylightHours[1] == 9.5, "daylight hours survive the wire");
        // The climate is whole (#3777): a read without its daylight hours does not decode.
        var rejected = false;
        try { WireJson.Deserialize<Obs.GearClimate>("{}"); } catch (System.Text.Json.JsonException) { rejected = true; }
        Require(rejected, "a climate without its daylight hours does not decode");
        Console.WriteLine("native-daylight-hours: hour counting, sampling and the wire field passed");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
