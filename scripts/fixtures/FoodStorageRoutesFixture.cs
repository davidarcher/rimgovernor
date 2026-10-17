#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Brain.Domain;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools;

// Private disposable acceptance only: the forecast food read's cost on a colony with a stockpile, which once searched one
// path per (harvestable plant, empty accepting storage cell) pair, so ripe plants and a stockpile multiply. Forty
// ripe rice plants are sown beside a default stockpile grown through 5x5, 10x10 and 15x15; at each size the read
// runs for ten and for forty plants with a fresh leg cache. Nothing is ordered.
public sealed partial class PawnControlFixture
{
    [Tool("test/food_storage_routes", Description = "UNSAFE FOR MODEL EXECUTION. Sow forty ripe rice plants beside a default stockpile grown through 25, 100 and 225 cells, and time the forecast's food read at each size.")]
    public async Task<object> FoodStorageRoutes(IRimBridgeContext ctx, CancellationToken ct)
        => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            var map = Find.CurrentMap ?? throw new InvalidOperationException("Load a colony first.");
            var anchor = map.mapPawns.FreeColonistsSpawned.First();
            const int width = 32, height = 16;
            var origin = GenRadial.RadialCellsAround(anchor.Position, 50, true).FirstOrDefault(c =>
                new CellRect(c.x, c.z, width, height).Cells.All(cell => cell.InBounds(map) && !cell.Fogged(map)
                    && cell.GetEdifice(map) == null && cell.GetZone(map) == null && !cell.GetTerrain(map).IsWater
                    && cell.GetTerrain(map).passability != Traversability.Impassable
                    && !cell.GetThingList(map).Any(t => t.def.category == Verse.ThingCategory.Building || t is Pawn))
                && anchor.CanReach(c, PathEndMode.Touch, Danger.None));
            if (origin == default) throw new InvalidOperationException("No open reachable 32x16 patch.");
            foreach (var cell in new CellRect(origin.x, origin.z, width, height).Cells)
            {
                foreach (var thing in cell.GetThingList(map).Where(t => t is Plant || t.def.category is Verse.ThingCategory.Item or Verse.ThingCategory.Filth).ToList()) thing.Destroy();
                map.terrainGrid.SetTerrain(cell, TerrainDefOf.Soil);
            }
            // Only the staged rice is harvestable food: the map's own ripe plants would add their own pairs.
            foreach (var wild in map.listerThings.AllThings.OfType<Plant>().Where(p => p.HarvestableNow && p.def.plant.harvestedThingDef?.IsNutritionGivingIngestible == true).ToList()) wild.Destroy();
            // The rice: a 10x4 block twenty cells east of the stockpile's corner.
            var riceDef = DefDatabase<ThingDef>.GetNamed("Plant_Rice");
            var rice = new List<Plant>();
            for (var x = 0; x < 10; x++)
            for (var z = 0; z < 4; z++)
            {
                var plant = (Plant)GenSpawn.Spawn(ThingMaker.MakeThing(riceDef), new IntVec3(origin.x + 20 + x, 0, origin.z + 6 + z), map);
                plant.Growth = 1f;
                rice.Add(plant);
            }
            if (rice.Any(p => !p.HarvestableNow)) throw new InvalidOperationException("Staged rice is not harvestable.");
            foreach (var p in map.mapPawns.FreeColonistsSpawned.ToArray()) p.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait, 120000), JobCondition.InterruptForced);
            var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
            map.zoneManager.RegisterZone(zone);
            if (!zone.settings.filter.Allows(riceDef.plant.harvestedThingDef)) throw new InvalidOperationException("The default stockpile refuses " + riceDef.plant.harvestedThingDef.defName + ".");
            var identity = Context().Identity!;
            var rows = new List<object>();
            foreach (var side in new[] { 5, 10, 15 })
            {
                foreach (var cell in new CellRect(origin.x, origin.z, side, side).Cells.Where(c => zone.ContainsCell(c) == false)) zone.AddCell(cell);
                foreach (var plants in new[] { 10, 40 })
                {
                    var ids = EquatableArray.From(rice.Take(plants).Select(p => p.GetUniqueLoadID()));
                    var legs = new NativePathLegs(map);
                    var bytes = GC.GetAllocatedBytesForCurrentThread();
                    var watch = Stopwatch.StartNew();
                    var read = NativeFoodActivity.Read(new(identity, ids), legs);
                    var ms = Math.Round(watch.Elapsed.TotalMilliseconds, 2);
                    rows.Add(new { cells = zone.Cells.Count, plants, ms, bytes = GC.GetAllocatedBytesForCurrentThread() - bytes,
                        searches = legs.Searches, options = read.Options.Count, known = read.Known });
                }
            }
            return new
            {
                success = true,
                colonists = map.mapPawns.FreeColonistsSpawned.Count,
                origin = new { x = origin.x, z = origin.z },
                rows,
                limitations = "One lab map; the stockpile is a square at the patch corner and the rice twenty cells east, so route lengths are short and alike. Each row's leg cache is fresh, as one capture's is.",
            };
        }, ct).ConfigureAwait(false);
}
