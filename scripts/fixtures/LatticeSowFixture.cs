using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only. Stages two empty
    // growing zones on bare soil: a tree zone (Plant_TreeOak) and, a gap away,
    // a rice zone of the same size. The colonists' Growing priority is raised
    // so they sow both; nothing here sows. The census reports each zone's
    // cells and planted cells so the case can check that trees stand only on
    // the 2x2 lattice anchored on the zone's minimum corner and that rice
    // still fills every cell.
    public sealed class LatticeSowFixture
    {
        [Tool("test/lattice_sow_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: create an empty `width` x `height` oak growing zone and an equal empty rice zone on bare soil near the colonists and enable Growing for the colonists.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken, int width = 5, int height = 4)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || Current.Game == null || Faction.OfPlayerSilentFail == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                if (width < 1 || width > 12 || height < 1 || height > 12) return Refuse("width and height must be between 1 and 12.");
                var oak = DefDatabase<ThingDef>.GetNamedSilentFail("Plant_TreeOak");
                var rice = DefDatabase<ThingDef>.GetNamedSilentFail("Plant_Rice");
                if (oak?.plant == null || rice?.plant == null || !oak.plant.blockAdjacentSow || rice.plant.blockAdjacentSow)
                    return Refuse("Plant_TreeOak (blockAdjacentSow) and Plant_Rice are required.");
                var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && !p.WorkTypeIsDisabled(WorkTypeDefOf.Growing)).ToList();
                if (people.Count == 0) return Refuse("No colonist able to sow.");
                foreach (var person in people) person.workSettings?.SetPriority(WorkTypeDefOf.Growing, 1);

                // One open plot holds both zones, separated by a two-cell gap so
                // no oak neighbours a rice cell.
                int total = width * 2 + 2;
                var center = new IntVec3((int)people.Average(p => p.Position.x), 0, (int)people.Average(p => p.Position.z));
                var origin = GenRadial.RadialCellsAround(center, 14, true).FirstOrDefault(c =>
                    new CellRect(c.x, c.z, total, height).Cells.All(cell => cell.InBounds(map) && !cell.Fogged(map)
                        && cell.GetEdifice(map) == null && cell.GetZone(map) == null && !map.roofGrid.Roofed(cell)
                        && cell.GetTerrain(map).passability != Traversability.Impassable && !cell.GetTerrain(map).IsWater
                        && !cell.GetThingList(map).Any(t => t.def.category == ThingCategory.Pawn || t.def.category == ThingCategory.Building || t is Blueprint || t is Frame))
                    && people[0].CanReach(c, Verse.AI.PathEndMode.Touch, Danger.None));
                if (origin == default) return Refuse("No open reachable plot near the colonists for the lattice fixture.");
                foreach (var old in map.zoneManager.AllZones.OfType<Zone_Growing>().ToList()) old.Delete();
                foreach (var cell in new CellRect(origin.x - 1, origin.z - 1, total + 2, height + 2).Cells.Where(c => c.InBounds(map)))
                {
                    foreach (var thing in cell.GetThingList(map).Where(t => t is Plant || t.def.category == ThingCategory.Item || t.def.category == ThingCategory.Filth).ToList()) thing.Destroy();
                    if (cell.Walkable(map) && cell.GetEdifice(map) == null) map.terrainGrid.SetTerrain(cell, TerrainDefOf.Soil);
                }
                var oakZone = MakeZone(map, oak, new CellRect(origin.x, origin.z, width, height), "Lattice fixture oak");
                var riceZone = MakeZone(map, rice, new CellRect(origin.x + width + 2, origin.z, width, height), "Lattice fixture rice");
                if (oakZone.Cells.Count != width * height || riceZone.Cells.Count != width * height) return Refuse("A fixture zone did not take its cells.");
                if (!PlantUtility.GrowthSeasonNow(map, oak) || !PlantUtility.GrowthSeasonNow(map, rice))
                    return Refuse("Not a growth season for oak and rice on the fixture plot.");
                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new {
                    success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame, width, height,
                    oakZone = oakZone.ID, riceZone = riceZone.ID,
                    oakMin = new { x = origin.x, z = origin.z },
                    setup = "Test-only empty oak and rice growing zones; nothing sown.",
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/lattice_sow_census", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: report each growing zone's plant def, zone cells and the cells carrying a sown plant of that def.")]
        public async Task<object> Census(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null) return Refuse("No current map.");
                var zones = map.zoneManager.AllZones.OfType<Zone_Growing>().Select(zone => {
                    var def = zone.GetPlantDefToGrow();
                    return new {
                        id = zone.ID, plant = def?.defName, cells = zone.Cells.Count,
                        minX = zone.Cells.Min(c => c.x), minZ = zone.Cells.Min(c => c.z),
                        planted = zone.Cells.Where(c => c.GetPlant(map)?.def == def).Select(c => new { x = c.x, z = c.z }).ToList(),
                    };
                }).ToList();
                return new { success = true, tick = Find.TickManager.TicksGame, zones };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static Zone_Growing MakeZone(Map map, ThingDef plant, CellRect rect, string label)
        {
            var zone = new Zone_Growing(map.zoneManager) { label = label };
            BridgeCommon.PrivateInstanceField(typeof(Zone_Growing), "plantDefToGrow").SetValue(zone, plant);
            map.zoneManager.RegisterZone(zone);
            foreach (var c in rect.Cells) zone.AddCell(c);
            return zone;
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
