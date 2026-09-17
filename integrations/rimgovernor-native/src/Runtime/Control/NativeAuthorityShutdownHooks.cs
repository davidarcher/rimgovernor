#nullable enable
using System;
using HarmonyLib;
using Verse;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// Orderly end of the game (#88): a process exit (Root.Shutdown) or a
    /// game unload (Game.Dispose, which every return to the main menu and
    /// every load of another save calls synchronously on the main thread
    /// before the asynchronous MemoryUtility.ClearAllMapsAndWorld) revokes
    /// an Active authority as Shutdown and retains the final state, so a
    /// controller reading afterwards sees Inactive(SHUTDOWN) rather than
    /// the lease lapse (Disconnect) that a vanished bot would leave behind.
    /// These are not player-action hooks: they never gate authority health.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class NativeAuthorityShutdownHooks
    {
        public const string Owner = "rimgovernor.native.authority.shutdown";

        static NativeAuthorityShutdownHooks()
        {
            try
            {
                var harmony = new Harmony(Owner);
                harmony.Patch(AccessTools.Method(typeof(Root), nameof(Root.Shutdown), Type.EmptyTypes)
                        ?? throw new MissingMethodException("Root.Shutdown"),
                    postfix: new HarmonyMethod(typeof(NativeAuthorityShutdownHooks), nameof(Shutdown)));
                harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.Dispose), Type.EmptyTypes)
                        ?? throw new MissingMethodException("Game.Dispose"),
                    prefix: new HarmonyMethod(typeof(NativeAuthorityShutdownHooks), nameof(Unload)));
            }
            catch (Exception ex)
            {
                Log.Error("[RimGovernor] Authority shutdown hook installation failed: " + ex);
            }
        }

        private static void Shutdown() => End(Current.Game);
        private static void Unload(Game __instance) => End(__instance);

        // Runs while the game is still loaded and current, so the authority's
        // context is valid and the final tick is readable; only a game that
        // has an authority is touched (reads never create one).
        private static void End(Game? game)
        {
            try
            {
                if (game == null || !ReferenceEquals(game, Current.Game) || !UnityData.IsInMainThread) return;
                if (NativeControlAuthority.TryGetForGame(game, out var authority) && authority != null)
                    authority.RevokeShutdown(game.tickManager?.TicksGame ?? 0);
            }
            catch (Exception ex)
            {
                Log.Error("[RimGovernor] Authority shutdown revocation failed: " + ex);
            }
        }
    }
}
