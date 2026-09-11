using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace HomeBridge.BridgeTools
{
    public sealed class QuestFulfillmentTools
    {
        [Tool("home/fulfill_quest", Title = "Fulfill an observed settlement trade request",
            Description = "Preview or confirm the native fulfillment command for an ongoing trade quest at the caravan's current settlement. Uses actual eligible cargo and native reward callbacks; does not create items or set quest success.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken,
            string questId, string caravanId, string pawnIds, string colonyId, string loadToken, int mapId, bool dryRun = true)
        {
            return await ctx.MainThread.InvokeAsync(() =>
            {
                var identity = Current.Game?.GetComponent<ColonyIdentity>();
                if (identity == null || identity.ColonyId != colonyId || identity.LoadToken != loadToken ||
                    Find.CurrentMap?.uniqueID != mapId || !Find.TickManager.Paused)
                    return Refuse("Observe the current paused colony, load and map before fulfillment");
                var quest = Find.QuestManager.QuestsListForReading.SingleOrDefault(q => q.GetUniqueLoadID() == questId && !q.hidden && !q.hiddenInUI);
                var caravan = Find.WorldObjects.Caravans.SingleOrDefault(c => c.IsPlayerControlled && c.GetUniqueLoadID() == caravanId);
                if (quest == null || quest.State != QuestState.Ongoing || caravan == null)
                    return Refuse("An ongoing visible quest and current player caravan are required");
                if (pawnIds == null || !caravan.PawnsListForReading.Select(p => p.GetUniqueLoadID()).OrderBy(id => id)
                    .SequenceEqual(pawnIds.Split(',').OrderBy(id => id)))
                    return Refuse("Caravan membership changed; observe before fulfilling the request");
                var parts = quest.PartsListForReading.OfType<QuestPart_InitiateTradeRequest>().ToArray();
                if (parts.Length != 1) return Refuse("This quest does not have one native settlement trade objective");
                var part = parts[0];
                var settlement = CaravanVisitUtility.SettlementVisitedNow(caravan);
                if (settlement == null || settlement != part.settlement || settlement.Faction.HostileTo(Faction.OfPlayer))
                    return Refuse("Visit the exact nonhostile quest settlement first");
                var request = settlement.GetComponent<TradeRequestComp>();
                if (request == null || !request.ActiveRequest || request.requestThingDef != part.requestedThingDef || request.requestCount != part.requestedCount)
                    return Refuse("Native trade objective changed or expired");
                var command = request.GetCaravanGizmos(caravan).OfType<Command_Action>().SingleOrDefault();
                if (command == null || command.Disabled) return Refuse("Native fulfillment is disabled; inspect requested cargo quality, freshness and quantity");
                if (dryRun) return (object)new { success = true, accepted = true, dryRun,
                    resource = request.requestThingDef.defName, count = request.requestCount,
                    available = caravan.PawnsListForReading.SelectMany(p => p.inventory.innerContainer)
                        .Where(t => t.def == request.requestThingDef).Sum(t => t.stackCount),
                    settlementId = settlement.GetUniqueLoadID() };
                var windows = Find.WindowStack.Windows.ToArray();
                command.action();
                var confirmation = Find.WindowStack.Windows.OfType<Dialog_MessageBox>().SingleOrDefault(w => !windows.Contains(w));
                if (confirmation?.buttonAAction == null)
                    throw new InvalidOperationException("The native fulfillment confirmation was not observed; inspect before retrying");
                confirmation.buttonAAction();
                confirmation.Close();
                return (object)new { success = true, accepted = !request.ActiveRequest,
                    dryRun, questId, state = quest.State.ToString(), observation = WorldProgressionTools.ReadNow() };
            }, cancellationToken);
        }

        private static object Refuse(string reason) => new { success = true, accepted = false, reason };
    }
}
