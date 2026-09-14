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
    // Private disposable acceptance only. Forms a real player caravan (two
    // existing colonists, one of them carrying the exact requested Silver)
    // away from an existing non-hostile, currently-tradeable settlement via
    // test/settlement_gift_prepare -- so NativeSettlementGiftOperations.
    // Prepare's "visit the exact settlement first" branch (CaravanVisit
    // Utility.SettlementVisitedNow) is exercised honestly -- then
    // test/settlement_gift_control teleports the caravan directly onto the
    // settlement's own tile (no travel simulation), the same two-step
    // pattern QuestFulfillFixture uses.
    public sealed class SettlementGiftFixture
    {
        private static Game preparedGame;
        private static Map preparedMap;
        private static Settlement preparedSettlement;
        private static Caravan preparedCaravan;

        [Tool("test/settlement_gift_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: form a real player caravan (two existing colonists, one carrying the exact requested Silver) away from an existing non-hostile, currently-tradeable settlement. No travel simulation.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken, int silverCount = 100)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                if (silverCount < 1 || silverCount > 1000) return Refuse("Use 1..1000 requested Silver.");
                var settlement = Find.WorldObjects.SettlementBases
                    .Where(s => s.Faction != null && s.Faction != player && !s.Faction.HostileTo(player) && !s.Faction.IsPlayer
                        && s.Visitable && s.Tile != map.Tile && s.CanTradeNow && s.Faction.PlayerGoodwill < 100)
                    .OrderBy(s => s.GetUniqueLoadID())
                    .FirstOrDefault();
                if (settlement == null) return Refuse("No existing non-hostile, currently-tradeable settlement with goodwill headroom.");
                var colonists = map.mapPawns.FreeColonistsSpawned
                    .Where(p => !p.Dead && !p.Downed && !p.Drafted && !p.InMentalState && !p.WorkTagIsDisabled(WorkTags.Social))
                    .OrderBy(p => p.thingIDNumber).Take(2).ToList();
                if (colonists.Count < 2 || map.mapPawns.FreeColonistsSpawned.Count <= colonists.Count)
                    return Refuse("At least two existing eligible colonists, with one remaining home, are required.");

                var resource = ThingDefOf.Silver;
                foreach (var colonist in colonists) colonist.DeSpawn();
                var caravan = CaravanMaker.MakeCaravan(colonists, player, map.Tile, true);
                var stack = ThingMaker.MakeThing(resource); stack.stackCount = silverCount;
                colonists[0].inventory.innerContainer.TryAdd(stack);

                preparedGame = Current.Game; preparedMap = map; preparedSettlement = settlement; preparedCaravan = caravan;
                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new {
                    success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame,
                    homeTile = map.Tile.tileId, caravanId = caravan.GetUniqueLoadID(),
                    settlementTile = settlement.Tile.tileId, factionId = settlement.Faction.GetUniqueLoadID(),
                    pawnIds = colonists.Select(p => p.GetUniqueLoadID()).ToArray(),
                    silverCount,
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/settlement_gift_control", Description = "UNSAFE FOR MODEL EXECUTION. Private prepared-fixture control: teleport the prepared caravan directly onto the prepared settlement's own tile, or back to the original home tile. No travel simulation.")]
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
