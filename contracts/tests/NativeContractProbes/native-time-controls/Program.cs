using System;
using RimGovernor.Native;
using Verse;

// The six-button time controls' pure decisions (TimeControlsPolicy.cs, #3608): the click route, the button visibility and
// the highlight, with the pending request it reads (ClockSpeedRequest.Pending).
internal static class NativeTimeControlsProbe
{
    private static int checks;
    private static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }

    internal static void Invoke()
    {
        Check(TimeControlsPolicy.Buttons.Length == 6 && TimeControlsPolicy.Buttons[0] == TimeButton.Pause
            && TimeControlsPolicy.Buttons[5] == TimeButton.Hyperfast, "six buttons, Pause through Hyperfast");
        foreach (var button in TimeControlsPolicy.Buttons)
            if (button != TimeButton.Pause) Check(TimeControlsPolicy.Speed(button).ToString() == button.ToString(), "a button's speed is its own: " + button);

        // Route: connected asks the brain, not connected sets the game's speed.
        Check(TimeControlsPolicy.Requests(connected: true), "a click while the bot is connected is a request");
        Check(!TimeControlsPolicy.Requests(connected: false), "a click without the bot sets the game's speed");

        // Visibility: every button for every player; Hyperfast without the bot needs the boost.
        foreach (var button in TimeControlsPolicy.Buttons)
        {
            Check(TimeControlsPolicy.Visible(button, connected: true, boostAvailable: false), "connected shows " + button);
            Check(TimeControlsPolicy.Visible(button, connected: false, boostAvailable: true), "with the boost shows " + button);
        }
        Check(!TimeControlsPolicy.Visible(TimeButton.Hyperfast, connected: false, boostAvailable: false), "no boost and no bot hides Hyperfast");
        Check(TimeControlsPolicy.Visible(TimeButton.Ultrafast, connected: false, boostAvailable: false), "no boost still shows Ultrafast");

        // Not connected: the game's own speed, Ultrafast split by the boost, Pause while paused.
        Check(Lit(false, false, null, null, TimeSpeed.Ultrafast, boost: false) == TimeButton.Ultrafast, "unboosted Ultrafast lights Ultrafast");
        Check(Lit(false, false, null, null, TimeSpeed.Ultrafast, boost: true) == TimeButton.Hyperfast, "boosted Ultrafast lights Hyperfast");
        Check(Lit(false, false, null, null, TimeSpeed.Superfast, boost: true) == TimeButton.Superfast, "the boost does not move Superfast");
        Check(Lit(false, true, null, null, TimeSpeed.Paused, false) == TimeButton.Pause, "a paused game lights Pause");
        Check(Lit(false, true, null, null, TimeSpeed.Fast, false) == TimeButton.Pause, "a forced pause lights Pause");
        Check(Lit(false, false, RimGovernor.Brain.GameFacts.Clock.Speed.Fast, RimGovernor.Brain.GameFacts.Clock.Speed.Normal, TimeSpeed.Superfast, false) == TimeButton.Superfast,
            "without the bot neither a request nor a Run moves the highlight");

        // Connected: the pending click, then the last Run's speed, then the game's own before any Run.
        Check(Lit(true, false, RimGovernor.Brain.GameFacts.Clock.Speed.Superfast, RimGovernor.Brain.GameFacts.Clock.Speed.Hyperfast, TimeSpeed.Ultrafast, true) == TimeButton.Superfast, "a pending click lights first");
        Check(Lit(true, true, RimGovernor.Brain.GameFacts.Clock.Speed.Superfast, RimGovernor.Brain.GameFacts.Clock.Speed.Hyperfast, TimeSpeed.Paused, true) == TimeButton.Superfast, "a pending click lights over a pause");
        Check(Lit(true, false, null, RimGovernor.Brain.GameFacts.Clock.Speed.Fast, TimeSpeed.Fast, false) == TimeButton.Fast, "the Run's speed lights once answered");
        Check(Lit(true, true, null, RimGovernor.Brain.GameFacts.Clock.Speed.Superfast, TimeSpeed.Paused, false) == TimeButton.Pause, "a held clock lights Pause");
        Check(Lit(true, false, null, null, TimeSpeed.Normal, false) == TimeButton.Normal, "before any Run the game's own speed lights");

        // The pending request: a Run at that speed answers it; a Run at another (a combat fallback, a pinned speed) does not.
        var request = new ClockSpeedRequest();
        var game = new object();
        Check(request.Pending(game) is null, "no request is not pending");
        request.Request(game, RimGovernor.Brain.GameFacts.Clock.Speed.Superfast, RimGovernor.Brain.GameFacts.Clock.Speed.Ultrafast);
        Check(request.Pending(game) == RimGovernor.Brain.GameFacts.Clock.Speed.Superfast, "a new request is pending");
        Check(request.Pending(new object()) is null && request.Pending(null) is null, "another game has nothing pending");
        request.Ran(game, RimGovernor.Brain.GameFacts.Clock.Speed.Fast);
        Check(request.Pending(game) == RimGovernor.Brain.GameFacts.Clock.Speed.Superfast, "a Run at another speed leaves it pending");
        request.Ran(new object(), RimGovernor.Brain.GameFacts.Clock.Speed.Superfast);
        Check(request.Pending(game) == RimGovernor.Brain.GameFacts.Clock.Speed.Superfast, "another game's Run leaves it pending");
        request.Ran(game, RimGovernor.Brain.GameFacts.Clock.Speed.Superfast);
        Check(request.Pending(game) is null, "a Run at that speed answers it");
        request.Request(game, RimGovernor.Brain.GameFacts.Clock.Speed.Superfast, RimGovernor.Brain.GameFacts.Clock.Speed.Superfast);
        Check(request.Pending(game) is null, "a request for the running speed is answered at once");

        // The faster/slower keys while connected step the chosen speed between Normal and Superfast.
        Check(TimeControlsPolicy.Step(RimGovernor.Brain.GameFacts.Clock.Speed.Normal, faster: true) == RimGovernor.Brain.GameFacts.Clock.Speed.Fast, "faster steps up");
        Check(TimeControlsPolicy.Step(RimGovernor.Brain.GameFacts.Clock.Speed.Superfast, faster: true) is null, "faster stops at Superfast");
        Check(TimeControlsPolicy.Step(RimGovernor.Brain.GameFacts.Clock.Speed.Hyperfast, faster: false) == RimGovernor.Brain.GameFacts.Clock.Speed.Ultrafast, "slower steps down");
        Check(TimeControlsPolicy.Step(RimGovernor.Brain.GameFacts.Clock.Speed.Normal, faster: false) is null, "slower stops at Normal");
        Check(TimeControlsPolicy.Step(null, faster: true) is null, "nothing chosen steps nowhere");

        Console.WriteLine("native-time-controls: " + checks + " checks passed");
    }

    private static TimeButton? Lit(bool connected, bool paused, RimGovernor.Brain.GameFacts.Clock.Speed? pending, RimGovernor.Brain.GameFacts.Clock.Speed? run, TimeSpeed game, bool boost) =>
        TimeControlsPolicy.Highlight(connected, paused, pending, run, game, boost);
}
