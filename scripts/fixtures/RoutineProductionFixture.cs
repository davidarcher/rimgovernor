using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    public sealed class RoutineProductionFixture
    {
        [Tool("test/routine_production_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Disposable read fixture: create a planted rice field and fueled campfire with a normal food bill. No harvested or cooked food is created.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused disposable colony required.");
                var pawn = map.mapPawns.FreeColonistsSpawned.First();
                var crop = ThingDef.Named("Plant_Rice");
                var cells = GenRadial.RadialCellsAround(pawn.Position, 18, true).Where(c => c.InBounds(map) && !c.Fogged(map)
                    && c.Standable(map) && !c.Roofed(map) && map.zoneManager.ZoneAt(c) == null
                    && map.fertilityGrid.FertilityAt(c) >= crop.plant.fertilityMin && c.GetThingList(map).All(t => t is Plant)).Take(31).ToList();
                if (cells.Count != 31) throw new InvalidOperationException("No bounded fertile fixture site.");
                foreach (var cell in cells) foreach (var plant in cell.GetThingList(map).OfType<Plant>().ToList()) plant.Destroy(DestroyMode.Vanish);
                var zone = new Zone_Growing(map.zoneManager);
                map.zoneManager.RegisterZone(zone); zone.SetPlantDefToGrow(crop);
                foreach (var cell in cells.Take(30)) {
                    zone.AddCell(cell);
                    var plant = (Plant)ThingMaker.MakeThing(crop); plant.Growth = .5f;
                    GenSpawn.Spawn(plant, cell, map);
                }
                var bench = (Building_WorkTable)ThingMaker.MakeThing(ThingDef.Named("Campfire"));
                bench.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(bench, cells[30], map);
                bench.TryGetComp<CompRefuelable>().Refuel(10);
                var recipe = DefDatabase<RecipeDef>.GetNamed("CookMealSimple");
                bench.BillStack.AddBill(recipe.MakeNewBill());
                return new { success = true, zoneId = zone.ID, bench = bench.GetUniqueLoadID(), plantedCells = 30, foodCreated = 0, tick = Find.TickManager.TicksGame };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
