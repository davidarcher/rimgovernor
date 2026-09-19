using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only. Finds one clear 8x8 outdoor site
    // near a colonist and mutates named cells of it in place (a wall, a
    // roof, a growing zone, a loose stack, a floor) so cellsaccept can ask
    // rimgovernor/observations_get_cells with changed_since_tick (#357) and
    // check that exactly those cells come back. The mutations bypass
    // dispatch on purpose: the case is about the native change grid, not
    // about the operations that would normally cause the changes.
    public sealed class CellsFixture
    {
        private const int Size = 8;
        private static readonly List<Thing> spawned = new List<Thing>();
        private static readonly List<Zone> zones = new List<Zone>();
        private static readonly Dictionary<IntVec3, TerrainDef> terrain = new Dictionary<IntVec3, TerrainDef>();
        private static readonly List<IntVec3> roofed = new List<IntVec3>();

        [Tool("test/cells_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: find one clear, unroofed, unzoned 8x8 outdoor site near an existing colonist and report its origin.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                var pawn = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed).OrderBy(p => p.thingIDNumber).FirstOrDefault();
                if (pawn == null) return Refuse("No existing colonist to search near.");
                // Wild plants are allowed on the site (a wall spawned over one
                // wipes it; nothing else here minds them); anything else is not.
                var origin = GenRadial.RadialCellsAround(pawn.Position, 60, true).FirstOrDefault(c =>
                    new CellRect(c.x, c.z, Size, Size).Cells.All(cell => cell.InBounds(map) && !cell.Fogged(map)
                        && cell.Standable(map) && cell.GetEdifice(map) == null && cell.GetZone(map) == null && !cell.Roofed(map)
                        && cell.GetThingList(map).All(t => t is Plant)
                        && cell.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy)));
                if (origin == default) return Refuse("No open 8x8 area for the fixture site.");
                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new {
                    success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame, origin = new { x = origin.x, z = origin.z }, size = Size,
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        // Phases: "first" mutates five cells of the site; "second" advances
        // the game one tick and mutates four others; "cleanup" undoes both.
        // Each mutation reports the cells it touched.
        [Tool("test/cells_mutate", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: mutate named cells of the prepared site (phase first|second|cleanup) directly, bypassing dispatch, and report the cells touched.")]
        public async Task<object> Mutate(IRimBridgeContext ctx, CancellationToken cancellationToken, int originX, int originZ, string phase)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                var at = new System.Func<int, int, IntVec3>((x, z) => new IntVec3(originX + x, 0, originZ + z));
                var touched = new List<IntVec3>();
                switch (phase)
                {
                    case "first":
                        touched.Add(Wall(map, player, at(1, 1)));
                        touched.Add(Roof(map, at(3, 1)));
                        touched.AddRange(Zone(map, new[] { at(1, 3), at(2, 3) }));
                        touched.Add(Stack(map, at(4, 4)));
                        touched.Add(Floor(map, at(5, 5)));
                        break;
                    case "second":
                        Find.TickManager.DoSingleTick();
                        touched.Add(Wall(map, player, at(6, 1)));
                        touched.Add(Roof(map, at(6, 3)));
                        touched.Add(Stack(map, at(1, 6)));
                        touched.Add(Floor(map, at(3, 6)));
                        break;
                    case "cleanup":
                        foreach (var thing in spawned) if (thing.Spawned) thing.Destroy();
                        spawned.Clear();
                        foreach (var zone in zones) zone.Delete();
                        zones.Clear();
                        foreach (var pair in terrain) map.terrainGrid.SetTerrain(pair.Key, pair.Value);
                        terrain.Clear();
                        foreach (var cell in roofed) map.roofGrid.SetRoof(cell, null);
                        roofed.Clear();
                        break;
                    default:
                        return Refuse("Unknown phase " + phase + ".");
                }
                return new { success = true, tick = Find.TickManager.TicksGame, cells = touched.Select(c => new { x = c.x, z = c.z }).ToList() };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static IntVec3 Wall(Map map, Faction player, IntVec3 cell)
        {
            var wall = (Building)ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.WoodLog);
            wall.SetFaction(player);
            spawned.Add(GenSpawn.Spawn(wall, cell, map));
            return cell;
        }

        private static IntVec3 Roof(Map map, IntVec3 cell)
        {
            map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
            roofed.Add(cell);
            return cell;
        }

        private static IEnumerable<IntVec3> Zone(Map map, IntVec3[] cells)
        {
            var zone = new Zone_Growing(map.zoneManager);
            map.zoneManager.RegisterZone(zone);
            foreach (var cell in cells) zone.AddCell(cell);
            zones.Add(zone);
            return cells;
        }

        private static IntVec3 Stack(Map map, IntVec3 cell)
        {
            var steel = ThingMaker.MakeThing(ThingDefOf.Steel);
            steel.stackCount = 10;
            spawned.Add(GenSpawn.Spawn(steel, cell, map));
            return cell;
        }

        private static IntVec3 Floor(Map map, IntVec3 cell)
        {
            terrain[cell] = cell.GetTerrain(map);
            map.terrainGrid.SetTerrain(cell, TerrainDefOf.Concrete);
            return cell;
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
