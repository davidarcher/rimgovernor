using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only. Ends the loaded game the way the
    // player's "quit to main menu" does (GenScene.GoToMainMenu: Game.Dispose
    // now, then a queued MemoryUtility.ClearAllMapsAndWorld and a null
    // Current.Game), so the lifecycle/shutdown acceptance case can observe native authority's
    // Shutdown revocation (#88) through authority_read_status once no game
    // is loaded. The process stays up; nothing here saves, orders or
    // changes game state.
    public sealed class ShutdownFixture
    {
        [Tool("test/shutdown_unload", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: unload the current game to the main menu without saving, exactly as the player's quit-to-menu does.")]
        public async Task<object> Unload(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (Current.Game == null) return new { success = false, message = "No game is loaded." };
                var tick = Find.TickManager != null ? Find.TickManager.TicksGame : -1;
                GenScene.GoToMainMenu();
                return new { success = true, tick, queued = LongEventHandler.AnyEventNowOrWaiting };
            }, cancellationToken);
        }
    }
}
