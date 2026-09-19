using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only. Stages the blight responder's
    // precondition (issue #245): one small growing zone of grown rice near
    // the colonists with a few of its plants blighted. Blight is a plant
    // state (Plant.Blighted, a Blight thing on the cell), not a map
    // condition, so nothing is registered; the crop-blight census in the
    // colony read is what the service opens on. Nothing here designates a
    // cut: that is the service's own write.
    public sealed class BlightFixture
    {
        [Tool("test/blight_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: sow one small rice growing zone near the colonists, blight `blighted` of its plants, and report the blighted plant ids with their read-time cut snapshot tokens.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken, int blighted = 3)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                if (!ProtoBoundary.TryReadContext(map, out var context, out var unavailable))
                    return Refuse("Native context unavailable: " + unavailable.Detail);
                if (blighted < 1 || blighted > 16) return Refuse("blighted must be between 1 and 16.");
                var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead).ToList();
                if (people.Count == 0) return Refuse("No free colonists on the map.");
                var cutter = people.FirstOrDefault(p => !p.Downed && !p.Drafted && !p.InMentalState && !p.WorkTypeIsDisabled(WorkTypeDefOf.PlantCutting));
                if (cutter == null) return Refuse("No colonist able to cut plants.");
                if (cutter.workSettings != null && cutter.workSettings.GetPriority(WorkTypeDefOf.PlantCutting) == 0)
                    cutter.workSettings.SetPriority(WorkTypeDefOf.PlantCutting, 1);
                var riceDef = DefDatabase<ThingDef>.GetNamedSilentFail("Plant_Rice");
                if (riceDef?.plant == null) return Refuse("Plant_Rice unavailable in this ruleset.");

                // A 4x4 soil plot inside the planning region around the
                // colonist centroid, open and reachable, becomes the zone.
                const int size = 4;
                var center = new IntVec3((int)people.Average(p => p.Position.x), 0, (int)people.Average(p => p.Position.z));
                var origin = GenRadial.RadialCellsAround(center, 12, true).FirstOrDefault(c =>
                    c.x >= center.x - 18 && c.x + size - 1 <= center.x + 18 && c.z >= center.z - 18 && c.z + size - 1 <= center.z + 18
                    && new CellRect(c.x, c.z, size, size).Cells.All(cell => cell.InBounds(map) && !cell.Fogged(map)
                        && cell.GetEdifice(map) == null && cell.GetZone(map) == null && !map.roofGrid.Roofed(cell)
                        && cell.GetTerrain(map).passability != Traversability.Impassable && !cell.GetTerrain(map).IsWater
                        && !cell.GetThingList(map).Any(t => t.def.category == ThingCategory.Pawn || t.def.category == ThingCategory.Building || t is Blueprint || t is Frame))
                    && cutter.CanReach(c, Verse.AI.PathEndMode.Touch, Danger.None));
                if (origin == default) return Refuse("No open reachable 4x4 plot near the colonist centroid for the fixture zone.");
                // A known fertile buffer offers the second field a separated
                // site without waiting for clearing work or terrain discovery.
                foreach (var old in map.zoneManager.AllZones.OfType<Zone_Growing>().ToList()) old.Delete();
                foreach (var cell in new CellRect(origin.x - 4, origin.z - 4, 12, 12).Cells.Where(c => c.InBounds(map))) {
                    if (!cell.Walkable(map) || cell.GetEdifice(map) != null || cell.GetZone(map) != null || cell.Roofed(map)) continue;
                    foreach (var plant in cell.GetThingList(map).OfType<Plant>().ToList()) plant.Destroy();
                    map.terrainGrid.SetTerrain(cell, TerrainDefOf.Soil);
                }
                var zone = new Zone_Growing(map.zoneManager) { label = "Blight fixture rice" };
                BridgeCommon.PrivateInstanceField(typeof(Zone_Growing), "plantDefToGrow").SetValue(zone, riceDef);
                map.zoneManager.RegisterZone(zone);
                var plants = new List<Plant>();
                foreach (var c in new CellRect(origin.x, origin.z, size, size).Cells)
                {
                    foreach (var thing in c.GetThingList(map).Where(t => t is Plant || t.def.category == ThingCategory.Item || t.def.category == ThingCategory.Filth).ToList()) thing.Destroy();
                    map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
                    zone.AddCell(c);
                    var rice = (Plant)GenSpawn.Spawn(ThingMaker.MakeThing(riceDef), c, map);
                    rice.Growth = 0.5f;
                    plants.Add(rice);
                }
                if (zone.Cells.Count != size * size) return Refuse("Fixture zone did not take its cells.");
                var infected = plants.OrderBy(p => p.thingIDNumber).Take(blighted).ToList();
                foreach (var plant in infected) plant.CropBlighted();
                if (infected.Any(p => !p.Blighted)) return Refuse("Blight did not take on the fixture plants.");
                if (infected.Any(p => !NativeCutPlant.Eligible(p))) return Refuse("A blighted fixture plant is not census-eligible.");

                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new {
                    success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame, zoneId = zone.ID, cutter = cutter.GetUniqueLoadID(),
                    plants = infected.Select(p => new { id = p.GetUniqueLoadID(), token = NativeCutPlant.Snapshot(p, context).Token,
                        cell = new { x = p.Position.x, z = p.Position.z } }).ToList(),
                    healthy = plants.Count - infected.Count,
                    zoneCells = zone.Cells.Select(c => new { x = c.x, z = c.z }).ToList(),
                    setup = "Test-only sown rice zone with blighted plants; no designation, cut or condition injected.",
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/blight_census", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: report every blighted plant on the map with its CutPlant designation state.")]
        public async Task<object> Census(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null) return Refuse("No current map.");
                var rows = map.listerThings.AllThings.OfType<Plant>().Where(p => p.Spawned && p.Blighted).OrderBy(p => p.thingIDNumber)
                    .Select(p => new { id = p.GetUniqueLoadID(), designated = NativeCutPlant.Designated(p), inColony = NativeCutPlant.InColony(p, map),
                        cell = new { x = p.Position.x, z = p.Position.z } }).ToList();
                var zones = map.zoneManager.AllZones.OfType<Zone_Growing>().Select(zone => new {
                    id = zone.ID,
                    cells = zone.Cells.Select(c => new { x = c.x, z = c.z }).ToList(),
                    planted = zone.Cells.Count(c => c.GetPlant(map)?.def == zone.GetPlantDefToGrow())
                }).ToList();
                return new { success = true, tick = Find.TickManager.TicksGame, blighted = rows, zones };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
