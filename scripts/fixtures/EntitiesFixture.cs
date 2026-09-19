using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only. Finds one clear 6x6 outdoor site
    // near a colonist, plants a stockpile zone and a crafting spot with one
    // bill on it, then mutates and removes them in named phases so
    // entities/changed-since can ask the zones, buildings and bills list
    // reads with changed_since_tick (#358) and check that exactly those
    // entities come back, that the removed ones are named in removed_ids
    // and that an ask older than the tombstone window is refused. The
    // mutations bypass dispatch on purpose: the case is about the native
    // entity tracker, not about the operations that would cause the changes.
    public sealed class EntitiesFixture
    {
        private const int Size = 6;
        private static Zone_Stockpile zone;
        private static Building_WorkTable bench;
        private static Building wall;

        [Tool("test/entities_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: find one clear 6x6 outdoor site near an existing colonist, plant a stockpile zone and a crafting spot with one bill on it, and report their ids.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                var pawn = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed).OrderBy(p => p.thingIDNumber).FirstOrDefault();
                if (pawn == null) return Refuse("No existing colonist to search near.");
                var origin = GenRadial.RadialCellsAround(pawn.Position, 60, true).FirstOrDefault(c =>
                    new CellRect(c.x, c.z, Size, Size).Cells.All(cell => cell.InBounds(map) && !cell.Fogged(map)
                        && cell.Standable(map) && cell.GetEdifice(map) == null && cell.GetZone(map) == null && !cell.Roofed(map)
                        && cell.GetThingList(map).All(t => t is Plant)
                        && cell.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy)));
                if (origin == default) return Refuse("No open 6x6 area for the fixture site.");
                var spot = DefDatabase<ThingDef>.GetNamedSilentFail("CraftingSpot");
                var recipe = spot?.AllRecipes?.FirstOrDefault();
                if (spot == null || recipe == null) return Refuse("CraftingSpot or its recipes are unavailable.");

                zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                map.zoneManager.RegisterZone(zone);
                zone.AddCell(origin + new IntVec3(1, 0, 1));
                zone.AddCell(origin + new IntVec3(2, 0, 1));
                bench = (Building_WorkTable)ThingMaker.MakeThing(spot);
                bench.SetFaction(player);
                GenSpawn.Spawn(bench, origin + new IntVec3(4, 0, 4), map);
                bench.BillStack.AddBill(BillUtility.MakeNewBill(recipe));
                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new {
                    success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame, origin = new { x = origin.x, z = origin.z }, size = Size,
                    zoneId = zone.GetUniqueLoadID(), benchId = bench.GetUniqueLoadID(),
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        // Phases: "first" changes the zone (label, a cell), the bench's stack
        // (a second bill) and spawns a wall; "second" deletes the zone and
        // destroys the bench and the wall; "expire" moves the game tick past
        // the tombstone window; "cleanup" destroys whatever remains. Each
        // phase reports the ids it touched and the game tick.
        [Tool("test/entities_mutate", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: mutate the prepared entities (phase first|second|expire|cleanup) directly, bypassing dispatch, and report the ids touched.")]
        public async Task<object> Mutate(IRimBridgeContext ctx, CancellationToken cancellationToken, int originX, int originZ, string phase)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                var origin = new IntVec3(originX, 0, originZ);
                var touched = new List<string>();
                switch (phase)
                {
                    case "first":
                        if (zone == null || bench == null || !bench.Spawned) return Refuse("The fixture is not prepared.");
                        zone.label = "entities-fixture";
                        zone.AddCell(origin + new IntVec3(3, 0, 1));
                        touched.Add(zone.GetUniqueLoadID());
                        bench.BillStack.AddBill(BillUtility.MakeNewBill(bench.def.AllRecipes.First()));
                        touched.Add(bench.GetUniqueLoadID());
                        wall = (Building)ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.WoodLog);
                        wall.SetFaction(player);
                        GenSpawn.Spawn(wall, origin + new IntVec3(1, 0, 4), map);
                        touched.Add(wall.GetUniqueLoadID());
                        break;
                    case "second":
                        if (zone != null) { touched.Add(zone.GetUniqueLoadID()); zone.Delete(); zone = null; }
                        if (bench != null) { touched.Add(bench.GetUniqueLoadID()); if (bench.Spawned) bench.Destroy(); bench = null; }
                        if (wall != null) { touched.Add(wall.GetUniqueLoadID()); if (wall.Spawned) wall.Destroy(); wall = null; }
                        break;
                    case "expire":
                        Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + 2501);
                        break;
                    case "cleanup":
                        if (zone != null) { zone.Delete(); zone = null; }
                        if (bench != null) { if (bench.Spawned) bench.Destroy(); bench = null; }
                        if (wall != null) { if (wall.Spawned) wall.Destroy(); wall = null; }
                        break;
                    default:
                        return Refuse("Unknown phase " + phase + ".");
                }
                return new { success = true, tick = Find.TickManager.TicksGame, ids = touched };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
