using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    public sealed class RoundsProductionFixture
    {
        [Tool("test/unfinished_material_probe", Description = "UNSAFE FOR MODEL EXECUTION. Prepared native bill/item lifecycle read probes; does not prove pawn crafting completion.")]
        public async Task<object> UnfinishedMaterials(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused disposable colony required.");
                var pawn = map.mapPawns.FreeColonistsSpawned.First();
                var tableDef = ThingDef.Named("TableSculpting");
                var cell = GenRadial.RadialCellsAround(pawn.Position, 18, true).First(c =>
                    GenAdj.OccupiedRect(c, Rot4.North, tableDef.size).Cells.All(p => p.InBounds(map) && p.Standable(map) && p.GetEdifice(map) == null));
                var bench = (Building_WorkTable)ThingMaker.MakeThing(tableDef, tableDef.MadeFromStuff ? ThingDefOf.WoodLog : null);
                bench.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(bench, cell, map);
                var recipe = DefDatabase<RecipeDef>.GetNamed("Make_SculptureSmall");
                var bill = (Bill_ProductionWithUft)recipe.MakeNewBill();
                bill.repeatMode = BillRepeatModeDefOf.RepeatCount; bill.repeatCount = 3;
                bench.BillStack.AddBill(bill);
                var checks = 0;
                void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
                RimGovernor.Contracts.Wire.Observations.BillState Read() => NativeProductionBills.BillRow(bill, 0);
                NativeProductionTracking.Install();
                var record = new NativeProductionRecord(bench, bill);
                Check(NativeProductionTracking.Track(record), "Production tracking unavailable."); record.Capture();
                Check(Read().ManagedUnchanged == true, "Fixed non-meal batches must retain ownership.");
                var originalConfiguration = NativeProductionBills.Configuration(bill);
                Check(Read().UnfinishedWork?.Item == "", "Known absence must be explicit.");
                var units = recipe.ingredients.Sum(i => i.CountRequiredOfFor(ThingDefOf.WoodLog, recipe, bill));
                var input = ThingMaker.MakeThing(ThingDefOf.WoodLog); input.stackCount = units;
                var item = (UnfinishedThing)ThingMaker.MakeThing(recipe.unfinishedThingDef, ThingDefOf.WoodLog);
                item.Creator = pawn; item.BoundBill = bill; item.ingredients.Add(input);
                item.workLeft = bill.GetWorkAmount(item);
                GenSpawn.Spawn(item, bench.InteractionCell, map);
                var started = Read();
                Check(started.UnfinishedWork?.Item == item.GetUniqueLoadID(), "Missing exact unfinished identity.");
                Check(started.UnfinishedWork.Ingredients.Single().Units == units, "Embedded ingredients must be observed.");
                Check(started.Reservations.Count == 0 && !input.Spawned, "Embedded inputs are not loose reservations.");
                Check(bill.BoundUft == item, "Reading changed the native binding.");
                bill.suspended = true;
                Check(Read().Suspended == true && Read().UnfinishedWork?.Item == item.GetUniqueLoadID(), "Suspension must preserve embedded work.");
                bill.suspended = false;
                Check(Read().UnfinishedWork?.Item == item.GetUniqueLoadID(), "Resume must preserve identity.");
                item.workLeft = -10000f;
                Check(Read().UnfinishedWork == null, "Uninitialized work cannot grant ingredient credit.");
                item.workLeft = 1;
                bill.Notify_IterationCompleted(pawn, item.ingredients);
                var finished = Read();
                Check(finished.RepeatCount == 2 && finished.UnfinishedWork?.Item == "", "Completion must decrement count and clear the binding.");
                Check(finished.ManagedUnchanged == true, "Native progress must not erase ingredient ownership.");
                Check(NativeProductionBills.Configuration(bill) != originalConfiguration, "CAS configuration must still detect count changes.");
                var priorFilter = bill.ingredientFilter.Allows(ThingDefOf.WoodLog);
                bill.ingredientFilter.SetAllow(ThingDefOf.WoodLog, !priorFilter);
                Check(Read().ManagedUnchanged == false, "Ingredient edits must invalidate the original promise.");
                bill.ingredientFilter.SetAllow(ThingDefOf.WoodLog, priorFilter);
                item.Destroy(DestroyMode.Vanish);
                var cancelled = (UnfinishedThing)ThingMaker.MakeThing(recipe.unfinishedThingDef, ThingDefOf.WoodLog);
                cancelled.Creator = pawn; cancelled.BoundBill = bill; cancelled.workLeft = 1;
                var recovery = ThingMaker.MakeThing(ThingDefOf.WoodLog); recovery.stackCount = units;
                cancelled.ingredients.Add(recovery); GenSpawn.Spawn(cancelled, bench.InteractionCell, map);
                cancelled.Destroy(DestroyMode.Cancel);
                Check(Read().RepeatCount == 2 && Read().UnfinishedWork?.Item == "", "Item cancellation must not claim an iteration completed.");
                return new { success = true, checks, embeddedUnits = units, remaining = bill.repeatCount };
            }, cancellationToken).ConfigureAwait(false);
        }

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
