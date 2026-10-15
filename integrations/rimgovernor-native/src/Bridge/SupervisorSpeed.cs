#nullable enable

using System;

namespace HomeBridge.BridgeTools
{
    /// <summary>Marks the supervisor's own CurTimeSpeed assignments, so the
    /// player speed-key block (PlayerSpeedKeyBlock) lets them through.
    /// Main-thread only.</summary>
    internal static class SupervisorSpeed
    {
        private static int _owned;

        internal static bool IsOwned => _owned > 0;

        /// Runs a supervisor speed assignment, which is never the player's.
        internal static void Owned(Action assign)
        {
            _owned++;
            try { assign(); }
            finally { _owned--; }
        }
    }
}
