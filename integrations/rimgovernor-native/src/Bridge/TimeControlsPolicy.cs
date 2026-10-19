#nullable enable
using Verse;
using Clock = RimGovernor.Contracts.Wire.Clock;

namespace HomeBridge.BridgeTools
{
    // The six time-control buttons, left to right. A speed button's value is its
    // Clock.Speed's (Normal 1 through Hyperfast 5).
    internal enum TimeButton { Pause, Normal, Fast, Superfast, Ultrafast, Hyperfast }

    // The pure decisions of the six-button time controls (TimeControlsRow.cs, #3608).
    // No game reads, so the contract probe compiles it directly.
    internal static class TimeControlsPolicy
    {
        internal static readonly TimeButton[] Buttons =
        {
            TimeButton.Pause, TimeButton.Normal, TimeButton.Fast, TimeButton.Superfast, TimeButton.Ultrafast, TimeButton.Hyperfast,
        };

        internal static Clock.Speed Speed(TimeButton button) => (Clock.Speed)(int)button;

        // With the bot connected a speed click only asks the brain (Supervisor.RequestSpeed);
        // without it the click sets the game's own speed. Pause always goes to the game.
        internal static bool Requests(bool connected) => connected;

        // Every button shows for every player. Without the bot, Hyperfast runs the
        // boost directly, so it needs TickManager.UltraSpeedBoost to exist.
        internal static bool Visible(TimeButton button, bool connected, bool boostAvailable) =>
            button != TimeButton.Hyperfast || connected || boostAvailable;

        // The game's own speed as a button speed: Ultrafast is Hyperfast while the boost is on.
        internal static Clock.Speed? GameSpeed(TimeSpeed game, bool boost) => game switch
        {
            TimeSpeed.Normal => Clock.Speed.Normal,
            TimeSpeed.Fast => Clock.Speed.Fast,
            TimeSpeed.Superfast => Clock.Speed.Superfast,
            TimeSpeed.Ultrafast => boost ? Clock.Speed.Hyperfast : Clock.Speed.Ultrafast,
            _ => null,
        };

        // The speed the row has chosen, pause aside. Connected: the player's click the
        // brain has not answered with a Run at that speed, then the last Run's speed
        // (Status.TargetSpeed), so a combat fallback shows the slower speed it runs at;
        // before any Run, the game's own. Not connected: the game's own speed.
        internal static Clock.Speed? Chosen(bool connected, Clock.Speed? pending, Clock.Speed? run, TimeSpeed game, bool boost) =>
            connected ? pending ?? run ?? GameSpeed(game, boost) : GameSpeed(game, boost);

        // The lit button: a pending click first, then Pause while the game is paused
        // (vanilla's rule), then the chosen speed.
        internal static TimeButton? Highlight(bool connected, bool paused, Clock.Speed? pending, Clock.Speed? run, TimeSpeed game, bool boost)
        {
            if (connected && pending is { } asked) return (TimeButton)(int)asked;
            if (paused) return TimeButton.Pause;
            return Chosen(connected, pending, run, game, boost) is { } speed ? (TimeButton)(int)speed : (TimeButton?)null;
        }

        // The faster/slower keys with the bot connected step the chosen speed, between
        // Normal and vanilla's Faster ceiling (Superfast); null leaves it.
        internal static Clock.Speed? Step(Clock.Speed? chosen, bool faster)
        {
            if (chosen is not { } speed) return null;
            if (faster) return speed < Clock.Speed.Superfast ? speed + 1 : (Clock.Speed?)null;
            return speed > Clock.Speed.Normal ? speed - 1 : (Clock.Speed?)null;
        }
    }
}
