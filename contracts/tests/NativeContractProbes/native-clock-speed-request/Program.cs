using System;
using HomeBridge.BridgeTools;
using Clock = RimGovernor.Contracts.Wire.Clock;

// The player's in-game speed request (ClockSpeedRequest.cs, #3607): RequestSpeed records the speed and bumps a serial of
// the loaded game, and Status reports both; another loaded game starts with none, and Unspecified throws.
internal static class NativeClockSpeedRequestProbe
{
    private static int checks;
    private static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }

    internal static void Invoke()
    {
        var request = new ClockSpeedRequest();
        var game = new object();
        var status = new Clock.Status { HoldSerial = 3 };

        var none = request.Report(status, game);
        Check(none.PlayerRequestedSpeed is null && none.PlayerSpeedRequestSerial is null, "a game with no request reports none");

        Check(request.Request(game, Clock.Speed.Fast) == 1, "the first request is serial 1");
        var first = request.Report(status, game);
        Check(first.PlayerRequestedSpeed == Clock.Speed.Fast && first.PlayerSpeedRequestSerial == 1, "Status reports the speed and the serial");
        Check(first.HoldSerial == 3, "the report keeps the rest of the status");

        // The same speed again is a new request: the brain adopts every serial, not every change.
        Check(request.Request(game, Clock.Speed.Fast) == 2, "a repeated speed still bumps the serial");
        Check(request.Request(game, Clock.Speed.Hyperfast) == 3, "every speed through Hyperfast is a request");
        var third = request.Report(status, game);
        Check(third.PlayerRequestedSpeed == Clock.Speed.Hyperfast && third.PlayerSpeedRequestSerial == 3, "Status reports the latest request");

        var threw = false;
        try { request.Request(game, Clock.Speed.Unspecified); }
        catch (ArgumentOutOfRangeException) { threw = true; }
        Check(threw, "an unspecified speed throws");
        Check(request.Report(status, game).PlayerSpeedRequestSerial == 3, "a refused request leaves the serial");

        // Another loaded game has none of this one's request and starts its serial again.
        var loaded = new object();
        var other = request.Report(status, loaded);
        Check(other.PlayerRequestedSpeed is null && other.PlayerSpeedRequestSerial is null, "another game reports no request");
        Check(request.Request(loaded, Clock.Speed.Normal) == 1, "the serial resets with the loaded game");
        Check(request.Report(status, null).PlayerSpeedRequestSerial is null, "no game reports no request");

        Console.WriteLine("native-clock-speed-request: " + checks + " checks passed");
    }
}
