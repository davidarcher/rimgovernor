using System;
using RimGovernor.Native;

// The player's in-game speed request (ClockSpeedRequest.cs, #3607): RequestSpeed records the speed and bumps a serial of
// the loaded game, and Status reports both; another loaded game starts with none, and an undefined speed throws.
internal static class NativeClockSpeedRequestProbe
{
    private static int checks;
    private static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }

    internal static void Invoke()
    {
        var request = new ClockSpeedRequest();
        var game = new object();
        var status = new RimGovernor.Brain.GameFacts.Clock.Status
        {
            Context = new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 0,
                NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.UnclaimedCase() },
            State = RimGovernor.Brain.GameFacts.Clock.ClockState.Held, Hold = new RimGovernor.Brain.GameFacts.Clock.HoldState.NeverRunCase(), LastRun = new RimGovernor.Brain.GameFacts.Clock.RunHistory.NeverRunCase(),
            Pacing = new RimGovernor.Brain.GameFacts.Clock.Pacing.StoppedCase(), HoldSerial = 3, PlayerRequest = new RimGovernor.Brain.GameFacts.Clock.PlayerSpeedRequest.NoneCase(),
            NativeTickBoundary = true, ActualPaused = true, EffectiveTicksPerSecond = 0
        };

        var none = request.Report(status, game);
        Check(none.PlayerRequest is RimGovernor.Brain.GameFacts.Clock.PlayerSpeedRequest.NoneCase, "a game with no request reports none");

        Check(request.Request(game, RimGovernor.Brain.GameFacts.Clock.Speed.Fast, null) == 1, "the first request is serial 1");
        var first = request.Report(status, game);
        Check(first.PlayerRequest is RimGovernor.Brain.GameFacts.Clock.PlayerSpeedRequest.RequestedCase { Speed: RimGovernor.Brain.GameFacts.Clock.Speed.Fast, Serial: 1 }, "Status reports the speed and the serial");
        Check(first.HoldSerial == 3, "the report keeps the rest of the status");

        // The same speed again is a new request: the brain adopts every serial, not every change.
        Check(request.Request(game, RimGovernor.Brain.GameFacts.Clock.Speed.Fast, null) == 2, "a repeated speed still bumps the serial");
        Check(request.Request(game, RimGovernor.Brain.GameFacts.Clock.Speed.Hyperfast, null) == 3, "every speed through Hyperfast is a request");
        var third = request.Report(status, game);
        Check(third.PlayerRequest is RimGovernor.Brain.GameFacts.Clock.PlayerSpeedRequest.RequestedCase { Speed: RimGovernor.Brain.GameFacts.Clock.Speed.Hyperfast, Serial: 3 }, "Status reports the latest request");

        var threw = false;
        try { request.Request(game, (RimGovernor.Brain.GameFacts.Clock.Speed)0, null); }
        catch (ArgumentOutOfRangeException) { threw = true; }
        Check(threw, "an unspecified speed throws");
        Check(request.Report(status, game).PlayerRequest is RimGovernor.Brain.GameFacts.Clock.PlayerSpeedRequest.RequestedCase { Serial: 3 }, "a refused request leaves the serial");

        // Another loaded game has none of this one's request and starts its serial again.
        var loaded = new object();
        var other = request.Report(status, loaded);
        Check(other.PlayerRequest is RimGovernor.Brain.GameFacts.Clock.PlayerSpeedRequest.NoneCase, "another game reports no request");
        Check(request.Request(loaded, RimGovernor.Brain.GameFacts.Clock.Speed.Normal, null) == 1, "the serial resets with the loaded game");
        Check(request.Report(status, null).PlayerRequest is RimGovernor.Brain.GameFacts.Clock.PlayerSpeedRequest.NoneCase, "no game reports no request");

        Console.WriteLine("native-clock-speed-request: " + checks + " checks passed");
    }
}
