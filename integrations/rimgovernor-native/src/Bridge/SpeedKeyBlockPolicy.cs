namespace HomeBridge.BridgeTools
{
    // Pure decision for the player speed-key block (#3173). No Verse types, so the
    // contract probe compiles it directly.
    internal static class SpeedKeyBlockPolicy
    {
        // A clock change is swallowed only when it comes from the player's own input
        // (the time controls: keys and buttons), the bot holds native authority, and
        // it is not the supervisor's own assignment. Forced pauses from letters and
        // dialogs, and every other game-side assignment, never pass through player
        // input and are never swallowed.
        internal static bool Swallow(bool inPlayerInput, bool botHoldsClock, bool supervisorAssignment)
            => inPlayerInput && botHoldsClock && !supervisorAssignment;
    }
}
