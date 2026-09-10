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
    public sealed class CaravanGiftTools
    {
        [Tool("home/caravan_gift", Title = "Offer an explicit silver gift at a settlement",
            Description = "Use the normal native gift deal for an exact visiting caravan and faction. Refuses active player trades, unavailable negotiators and gifts without goodwill benefit. Never directly sets faction relations.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken,
            string caravanId, string factionId, string pawnIds, int silver,
            string colonyId, string loadToken, int mapId, bool dryRun = true)
        {
            return await ctx.MainThread.InvokeAsync(() =>
            {
                var identity = Current.Game?.GetComponent<ColonyIdentity>();
                if (identity == null || identity.ColonyId != colonyId || identity.LoadToken != loadToken ||
                    Find.CurrentMap?.uniqueID != mapId || !Find.TickManager.Paused || silver <= 0)
                    return Refuse("Require current paused scope and a positive silver gift");
                if (TradeSession.Active || Find.WindowStack.Windows.OfType<Dialog_Trade>().Any())
                    return Refuse("An existing trade belongs to its current owner");
                var caravan = Find.WorldObjects.Caravans.SingleOrDefault(c => c.IsPlayerControlled && c.GetUniqueLoadID() == caravanId);
                if (caravan == null || pawnIds == null || !caravan.PawnsListForReading.Select(p => p.GetUniqueLoadID()).OrderBy(id => id)
                        .SequenceEqual(pawnIds.Split(',').OrderBy(id => id)))
                    return Refuse("Caravan membership changed");
                var settlement = CaravanVisitUtility.SettlementVisitedNow(caravan);
                if (settlement == null || settlement.Faction?.GetUniqueLoadID() != factionId || !settlement.CanTradeNow || settlement.Faction.HostileTo(Faction.OfPlayer))
                    return Refuse("Visit the exact nonhostile trading settlement first");
                var command = CaravanVisitUtility.TradeCommand(caravan, settlement.Faction, settlement.TraderKind);
                var negotiator = BestCaravanPawnUtility.FindBestNegotiator(caravan, settlement.Faction, settlement.TraderKind);
                if (command.Disabled || negotiator == null) return Refuse("Native negotiation is unavailable");
                try
                {
                    TradeSession.SetupWith(settlement, negotiator, true);
                    var row = TradeSession.deal.AllTradeables.SingleOrDefault(t => t.ThingDef == ThingDefOf.Silver);
                    // Gift mode reverses the native transfer direction: positive gives
                    // colony goods to the settlement, unlike an ordinary sale.
                    if (row == null || !row.CanAdjustTo(silver).Accepted) return Refuse("Native caravan silver is insufficient");
                    row.AdjustTo(silver);
                    int available = row.CountHeldBy(Transactor.Colony);
                    int gain = FactionGiftUtility.GetGoodwillChange(TradeSession.deal.AllTradeables, settlement.Faction);
                    int before = settlement.Faction.PlayerGoodwill;
                    if (gain <= 0 || before >= 100) return Refuse("Native gift has no goodwill benefit");
                    if (dryRun) return (object)new { success = true, accepted = true, dryRun, available,
                        silver, goodwill = before, expectedGain = gain, factionId };
                    bool traded;
                    if (!TradeSession.deal.TryExecute(out traded) || !traded)
                        throw new InvalidOperationException("Native gift did not confirm execution; inspect before retrying");
                    caravan.RecacheInventory();
                    int after = CaravanInventoryUtility.AllInventoryItems(caravan).Where(t => t.def == ThingDefOf.Silver).Sum(t => t.stackCount);
                    return (object)new { success = true, accepted = after == available - silver,
                        dryRun, beforeSilver = available, afterSilver = after,
                        beforeGoodwill = before, afterGoodwill = settlement.Faction.PlayerGoodwill,
                        observation = WorldProgressionTools.ReadNow() };
                }
                finally { TradeSession.Close(); }
            }, cancellationToken);
        }

        private static object Refuse(string reason) => new { success = true, accepted = false, reason };
    }
}
