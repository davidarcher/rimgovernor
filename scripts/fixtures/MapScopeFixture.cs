using System;
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
    // Disposable fixture only: gives the loaded game a second player map and
    // switches which one the player is viewing, so map scoping can be
    // exercised without a real second settlement. Never compiled into
    // production builds (see RimGovernor.Bridge.csproj's MapScopeFixture-gated
    // Compile entry). Everything the typed tools do with the two maps is
    // production behavior, not this fixture's.
    public sealed class MapScopeFixture
    {
        [Tool("test/map_scope_generate", Description = "Disposable fixture: settles a neighbouring world tile for the player faction and generates its map (small), leaving the viewed map unchanged. Returns both map ids.")]
        public async Task<object> Generate(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() =>
            {
                var home = Find.CurrentMap;
                if (Current.Game == null || home == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused disposable colony required");
                var neighbours = new List<PlanetTile>();
                Find.WorldGrid.GetTileNeighbors(home.Tile, neighbours);
                var tile = neighbours.FirstOrDefault(t => !Find.World.Impassable(t) && !Find.WorldObjects.AnyMapParentAt(t));
                if (!tile.Valid) throw new InvalidOperationException("No settleable neighbouring tile");
                var settlement = (Settlement)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.Settlement);
                settlement.SetFaction(Faction.OfPlayer);
                settlement.Tile = tile;
                settlement.Name = "MapScopeFixture";
                Find.WorldObjects.Add(settlement);
                var second = GetOrGenerateMapUtility.GetOrGenerateMap(tile, new IntVec3(75, 1, 75), null);
                if (second == null || ReferenceEquals(second, home)) throw new InvalidOperationException("Second map was not generated");
                if (!ReferenceEquals(Find.CurrentMap, home)) Current.Game.CurrentMap = home;
                return new { success = true, homeMapId = home.uniqueID, secondMapId = second.uniqueID, tile = tile.tileId,
                    viewedMapId = Find.CurrentMap.uniqueID, loadedMaps = Find.Maps.Count };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/map_scope_view", Description = "Disposable fixture: makes the map with the given id the viewed (current) map through Game.CurrentMap, exactly as the player's map switch does.")]
        public async Task<object> View(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "uniqueID of a loaded map.")] int mapId)
        {
            return await ctx.MainThread.InvokeAsync<object>(() =>
            {
                if (Current.Game == null) throw new InvalidOperationException("No game");
                var map = Find.Maps.FirstOrDefault(m => m.uniqueID == mapId);
                if (map == null) throw new ArgumentException("No loaded map with id " + mapId);
                Current.Game.CurrentMap = map;
                return new { success = true, viewedMapId = Find.CurrentMap.uniqueID };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
