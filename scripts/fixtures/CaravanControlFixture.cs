using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only. Forms a real two-colonist player
    // caravan at the home tile (CaravanMaker.MakeCaravan, the same native
    // mechanism QuestFulfillFixture and NativeCaravanOperations use) and
    // reports one reachable neighboring destination tile, so
    // caravancontrolaccept (G01.08) can exercise TravelCaravan's move/
    // return-home/stop dispositions against an actual native path follower
    // without needing a settlement or quest.
    public sealed class CaravanControlFixture
    {
        private static Game preparedGame;
        private static Map preparedMap;
        private static Caravan preparedCaravan;

        [Tool("test/caravan_control_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: form a real player caravan (two existing colonists) at the home tile and report one reachable neighboring destination tile, for TravelCaravan acceptance.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                var colonists = map.mapPawns.FreeColonistsSpawned
                    .Where(p => !p.Dead && !p.Downed && !p.Drafted && !p.InMentalState)
                    .OrderBy(p => p.thingIDNumber).Take(2).ToList();
                if (colonists.Count < 2 || map.mapPawns.FreeColonistsSpawned.Count <= colonists.Count)
                    return Refuse("At least two existing colonists, with one remaining home, are required.");
                var neighbors = new List<PlanetTile>();
                Find.WorldGrid.GetTileNeighbors(map.Tile, neighbors);
                foreach (var colonist in colonists) colonist.DeSpawn();
                var caravan = CaravanMaker.MakeCaravan(colonists, player, map.Tile, true);
                PlanetTile? destination = null;
                foreach (var candidate in neighbors)
                {
                    if (candidate.Valid && candidate != map.Tile && candidate.tileId < Find.WorldGrid.TilesCount && caravan.CanReach(candidate))
                    { destination = candidate; break; }
                }
                if (destination == null) return Refuse("No reachable neighboring destination tile.");

                preparedGame = Current.Game; preparedMap = map; preparedCaravan = caravan;
                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new
                {
                    success = true,
                    colonyId = identity?.ColonyId,
                    loadToken = identity?.LoadToken,
                    mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame,
                    homeTile = map.Tile.tileId,
                    caravanId = caravan.GetUniqueLoadID(),
                    destinationTile = destination.Value.tileId,
                    pawnIds = colonists.Select(p => p.GetUniqueLoadID()).ToArray(),
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
