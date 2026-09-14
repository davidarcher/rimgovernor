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
    // Private disposable acceptance only. Builds one minimal ongoing quest
    // carrying exactly one native QuestPart_InitiateTradeRequest against an
    // existing non-hostile settlement and arms its TradeRequestComp directly
    // -- the same effect QuestPart_InitiateTradeRequest.Notify_QuestSignal
    // Received applies, without needing the full Script_TradeRequest QuestGen
    // node graph -- then forms a real player caravan (two existing
    // colonists, carrying the exact requested Silver) away from that
    // settlement via test/quest_fulfill_prepare, so
    // NativeQuestFulfillOperations.Prepare's "visit the exact settlement
    // first" branch is exercised honestly before test/quest_fulfill_control
    // teleports the caravan directly onto the settlement's own tile (no
    // travel simulation) for the real fulfillment path.
    public sealed class QuestFulfillFixture
    {
        private static Game preparedGame;
        private static Map preparedMap;
        private static Settlement preparedSettlement;
        private static Caravan preparedCaravan;

        [Tool("test/quest_fulfill_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: build one ongoing quest with a single native settlement trade-request objective against an existing non-hostile settlement, arm its TradeRequestComp directly, and form a real player caravan (two existing colonists, carrying the exact requested Silver) away from that settlement. No quest-script generation, no travel simulation.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken, int requestedCount = 40)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                if (requestedCount < 1 || requestedCount > 200) return Refuse("Use 1..200 requested Silver.");
                var settlement = Find.WorldObjects.SettlementBases
                    .Where(s => s.Faction != null && s.Faction != player && !s.Faction.HostileTo(player) && s.Visitable && s.Tile != map.Tile)
                    .OrderBy(s => s.GetUniqueLoadID())
                    .FirstOrDefault(s => { var c = s.GetComponent<TradeRequestComp>(); return c != null && !c.ActiveRequest; });
                if (settlement == null) return Refuse("No existing non-hostile visitable settlement with an inactive trade request component.");
                var colonists = map.mapPawns.FreeColonistsSpawned
                    .Where(p => !p.Dead && !p.Downed && !p.Drafted && !p.InMentalState)
                    .OrderBy(p => p.thingIDNumber).Take(2).ToList();
                if (colonists.Count < 2 || map.mapPawns.FreeColonistsSpawned.Count <= colonists.Count)
                    return Refuse("At least two existing colonists, with one remaining home, are required.");

                var resource = ThingDefOf.Silver;
                var comp = settlement.GetComponent<TradeRequestComp>();
                comp.requestThingDef = resource;
                comp.requestCount = requestedCount;
                comp.expiration = Find.TickManager.TicksGame + 600000;

                var quest = new Quest {
                    id = Find.UniqueIDsManager.GetNextQuestID(),
                    name = "Fixture trade request", description = "Private disposable fixture quest.",
                    acceptanceTick = Find.TickManager.TicksGame, acceptanceExpireTick = -1,
                };
                quest.AddPart(new QuestPart_InitiateTradeRequest {
                    settlement = settlement, requestedThingDef = resource, requestedCount = requestedCount, requestDuration = 600000,
                });
                Find.QuestManager.Add(quest);

                foreach (var colonist in colonists) colonist.DeSpawn();
                var caravan = CaravanMaker.MakeCaravan(colonists, player, map.Tile, true);
                var remaining = requestedCount;
                foreach (var colonist in colonists)
                {
                    if (remaining <= 0) break;
                    var give = Math.Min(remaining, resource.stackLimit);
                    var stack = ThingMaker.MakeThing(resource); stack.stackCount = give;
                    colonist.inventory.innerContainer.TryAdd(stack);
                    remaining -= give;
                }

                preparedGame = Current.Game; preparedMap = map; preparedSettlement = settlement; preparedCaravan = caravan;
                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new {
                    success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame,
                    questId = quest.GetUniqueLoadID(), settlementId = settlement.GetUniqueLoadID(), settlementTile = settlement.Tile.tileId,
                    homeTile = map.Tile.tileId, caravanId = caravan.GetUniqueLoadID(),
                    pawnIds = colonists.Select(p => p.GetUniqueLoadID()).ToArray(),
                    resourceDefName = resource.defName, resourceCount = requestedCount,
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/quest_fulfill_control", Description = "UNSAFE FOR MODEL EXECUTION. Private prepared-fixture control: teleport the prepared caravan directly onto the prepared settlement's own tile, or back to the original home tile. No travel simulation.")]
        public async Task<object> Control(IRimBridgeContext ctx, CancellationToken cancellationToken,
            string operation, string colonyId, string loadToken, int mapId)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var identity = Current.Game?.GetComponent<ColonyIdentity>();
                if (Current.Game != preparedGame || map != preparedMap || map == null || identity == null
                    || identity.ColonyId != colonyId || identity.LoadToken != loadToken || map.uniqueID != mapId || !Find.TickManager.Paused)
                    return Refuse("Prepared paused colony/load/map identity changed.");
                var caravan = preparedCaravan;
                if (caravan == null || !caravan.Spawned || caravan.pather.Moving) return Refuse("Exact prepared stationary caravan is unavailable.");
                if (operation == "teleport-settlement") { caravan.Tile = preparedSettlement.Tile; return new { success = true, operation, tile = caravan.Tile.tileId }; }
                if (operation == "teleport-home") { caravan.Tile = map.Tile; return new { success = true, operation, tile = caravan.Tile.tileId }; }
                return Refuse("Use teleport-settlement or teleport-home.");
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
