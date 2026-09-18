using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimBridgeServer.Sdk;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Disposable test setup only (issue #178). Reopens the initial, one-shot
    // faction/settlement naming dialog (Dialog_NamePlayerFactionAndSettlement)
    // on the current player settlement, exactly the window a fresh colony
    // start leaves force-pausing the game, so the ConfirmColonyNames routine
    // family can be proven end to end on a save that has long since been
    // named. The suggestions are the dialog's own generated names.
    public sealed class NamingFixture
    {
        private static Dialog_NamePlayerFactionAndSettlement opened;

        [Tool("test/open_colony_naming", Description = "UNSAFE FOR MODEL EXECUTION. Disposable test setup: open the force-pausing faction/settlement naming dialog on the current player settlement with generated suggestions (action=open), or report its state and the live names (action=read).")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "open or read.")] string action = "open")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (Current.Game == null || Find.CurrentMap == null) throw new InvalidOperationException("A loaded game is required.");
                var settlement = Find.CurrentMap.Parent as Settlement;
                if (settlement == null || settlement.Faction != Faction.OfPlayer) throw new InvalidOperationException("The current map is not a player settlement.");
                if (action == "read") return Read(settlement);
                if (Find.WindowStack.Windows.Any(w => w.forcePause)) throw new InvalidOperationException("A force-pausing window is already open.");
                opened = new Dialog_NamePlayerFactionAndSettlement(settlement);
                // Dialog_GiveName generates its suggestions on first draw; a
                // headless game never draws, so generate them the same way.
                var giveName = typeof(Dialog_GiveName);
                if (AccessTools.Field(giveName, "curName").GetValue(opened) == null)
                    AccessTools.Field(giveName, "curName").SetValue(opened, ((Func<string>)AccessTools.Field(giveName, "nameGenerator").GetValue(opened))());
                if (AccessTools.Field(giveName, "curSecondName").GetValue(opened) == null)
                    AccessTools.Field(giveName, "curSecondName").SetValue(opened, ((Func<string>)AccessTools.Field(giveName, "secondNameGenerator").GetValue(opened))());
                Find.WindowStack.Add(opened);
                return Read(settlement);
            }, cancellationToken).ConfigureAwait(false);
        }

        private static string Field(string name) =>
            (AccessTools.Field(typeof(Dialog_GiveName), name)?.GetValue(opened) as string)?.Trim();

        private static object Read(Settlement settlement)
        {
            var open = opened != null && Find.WindowStack.Windows.Contains(opened);
            return new { success = true, windowId = opened?.ID, windowOpen = open,
                factionName = opened == null ? null : Field("curName"), settlementName = opened == null ? null : Field("curSecondName"),
                currentFactionName = Faction.OfPlayer.Name, currentSettlementName = settlement.Name,
                forcePaused = Find.TickManager.ForcePaused, paused = Find.TickManager.Paused,
                forcePausingWindows = Find.WindowStack.Windows.Where(w => w.forcePause).Select(w => w.GetType().FullName).ToArray(),
                tick = Find.TickManager.TicksGame };
        }
    }
}
