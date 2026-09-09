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
    public sealed class ColonyNamingTools
    {
        internal static Dialog_NamePlayerFactionAndSettlement Pending()
        {
            var windows = Find.WindowStack?.Windows.Where(w => w.forcePause).ToList();
            return windows != null && windows.Count == 1
                ? windows[0] as Dialog_NamePlayerFactionAndSettlement : null;
        }

        private static string Name(Dialog_GiveName dialog, string field) =>
            (AccessTools.Field(typeof(Dialog_GiveName), field)?.GetValue(dialog) as string)?.Trim();

        internal static object Snapshot()
        {
            var dialog = Pending();
            return dialog == null ? null : new { windowId = dialog.ID,
                factionName = Name(dialog, "curName"), settlementName = Name(dialog, "curSecondName") };
        }

        [Tool("home/confirm_colony_names", Title = "Accept generated colony names",
            Description = "Confirm the exact observed suggestions in the initial faction-and-settlement naming dialog. Uses native name validators and naming callbacks. Refuses stale text/window identities and other dialogs. dryRun defaults to true.")]
        public async Task<object> Confirm(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Exact observed naming window ID.")] int windowId,
            [ToolParameter(Description = "Exact observed faction name suggestion.")] string factionName,
            [ToolParameter(Description = "Exact observed settlement name suggestion.")] string settlementName,
            [ToolParameter(Description = "Preview without changing any names or closing the dialog.", DefaultValue = true)] bool dryRun = true)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var dialog = Pending();
                if (dialog == null || dialog.ID != windowId || Name(dialog, "curName") != factionName
                    || Name(dialog, "curSecondName") != settlementName)
                    return new { success = false, error = "Naming window or suggestions changed; inspect again." };
                var settlement = AccessTools.Field(typeof(Dialog_NamePlayerFactionAndSettlement), "settlement")
                    ?.GetValue(dialog) as Settlement;
                if (settlement == null || settlement.Map != Find.CurrentMap)
                    return new { success = false, error = "Naming target is not the current settlement." };
                var type = typeof(Dialog_NamePlayerFactionAndSettlement);
                if (!(bool)AccessTools.Method(type, "IsValidName").Invoke(dialog, new object[] { factionName })
                    || !(bool)AccessTools.Method(type, "IsValidSecondName").Invoke(dialog, new object[] { settlementName }))
                    return new { success = false, error = "Native naming validation refused the suggestions." };
                if (!dryRun)
                {
                    // The same callbacks and order used by Dialog_GiveName's OK
                    // branch; OnAcceptKeyPressed does not implement that branch.
                    AccessTools.Method(type, "Named").Invoke(dialog, new object[] { factionName });
                    AccessTools.Method(type, "NamedSecond").Invoke(dialog, new object[] { settlementName });
                    Messages.Message("PlayerFactionAndBaseGainsName".Translate(factionName, settlementName),
                        MessageTypeDefOf.TaskCompletion, historical: false);
                    Find.WindowStack.TryRemove(dialog);
                }
                var verified = dryRun || (Faction.OfPlayer.Name == factionName && settlement.Name == settlementName
                    && !Find.WindowStack.Windows.Contains(dialog));
                return new { success = verified, dryRun, accepted = !dryRun && verified,
                    factionName = dryRun ? factionName : Faction.OfPlayer.Name,
                    settlementName = dryRun ? settlementName : settlement.Name };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
