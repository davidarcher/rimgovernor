using System;
using HomeBridge.BridgeTools;

internal static class NativeSpeedKeyBlockProbe
{
    private static int checks;
    private static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }
    internal static void Invoke()
    {
        Check(SpeedKeyBlockPolicy.Swallow(inPlayerInput: true, botHoldsClock: true, supervisorAssignment: false), "player input while the bot holds the clock is swallowed");
        Check(!SpeedKeyBlockPolicy.Swallow(true, false, false), "player input applies when the bot is off");
        Check(!SpeedKeyBlockPolicy.Swallow(false, true, false), "a forced pause or game-side change outside player input is not swallowed");
        Check(!SpeedKeyBlockPolicy.Swallow(true, true, true), "the supervisor's own assignment passes");
        Check(!SpeedKeyBlockPolicy.Swallow(false, false, false), "nothing is swallowed with no bot and no player input");
        Console.WriteLine("Speed key block passed " + checks + " assertions; pure policy, the Harmony scope is live-only.");
    }
}
