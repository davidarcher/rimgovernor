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
        [Tool("test/bill_configuration_diagnostics", Description = "UNSAFE FOR MODEL EXECUTION. Disposable Kibble bill: check strict progress diagnostics and reproduce native save-time ingredient pruning.")]
        public async Task<object> BillDiagnostics(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused disposable colony required");
                if (!ProtoBoundary.TryReadContext(map, out var context, out var failure)) throw new InvalidOperationException("No native context: " + failure);
                var cell = GenRadial.RadialCellsAround(map.mapPawns.FreeColonistsSpawned.First().Position, 20, true)
                    .First(c => c.InBounds(map) && !c.Fogged(map) && c.Standable(map) && c.GetEdifice(map) == null);
                var bench = ThingMaker.MakeThing(ThingDef.Named("ButcherSpot"));
                bench.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(bench, cell, map);
                var giver = (IBillGiver)bench;
                var recipe = DefDatabase<RecipeDef>.GetNamed("Make_Kibble");
                var bill = (Bill_Production)recipe.MakeNewBill();
                bill.repeatMode = BillRepeatModeDefOf.TargetCount; bill.targetCount = 65;
                bill.unpauseWhenYouHave = 32; bill.pauseWhenSatisfied = true;
                bill.ingredientSearchRadius = 40; bill.SetStoreMode(BillStoreModeDefOf.DropOnFloor, null);
                foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading.Where(HumanFoodFacts.IsHumanMeat))
                    bill.ingredientFilter.SetAllow(def, recipe.ingredients.Any(i => i.filter.Allows(def)));
                giver.BillStack.AddBill(bill);
                var record = new NativeProductionRecord(bench, giver, bill, "fixture"); record.Capture();
                var attempt = new RimGovernor.Protocol.Common.AttemptKey();
                if (!record.Evidence(context).ConfigurationMatches) throw new InvalidOperationException("Initial configuration mismatch");
                bill.paused = true;
                if (!record.Evidence(context).ConfigurationMatches) throw new InvalidOperationException("Native pause changed configuration");
                bill.paused = false; bill.targetCount++;
                var scalar = record.Observe(attempt, context);
                if (scalar.Unsuccessful == null || !scalar.Unsuccessful.Detail.Contains("targetCount=65 -> 66")) throw new InvalidOperationException("Changed target was not strictly rejected with values");
                bill.targetCount--;
                var other = recipe.MakeNewBill(); giver.BillStack.AddBill(other);
                giver.BillStack.Bills.Reverse();
                var reordered = record.Observe(attempt, context);
                if (reordered.Unsuccessful == null || !reordered.Unsuccessful.Detail.Contains("index=0 -> 1")) throw new InvalidOperationException("Reorder was not rejected");
                giver.BillStack.Bills.Reverse(); giver.BillStack.Delete(other);
                var before = bill.ingredientFilter.AllowedThingDefs.Select(d => d.defName).OrderBy(d => d, StringComparer.Ordinal).ToArray();
                GameDataSaveLoader.SaveGame("RimGovernor-bill-diagnostics");
                var after = bill.ingredientFilter.AllowedThingDefs.Select(d => d.defName).OrderBy(d => d, StringComparer.Ordinal).ToArray();
                var saved = record.Observe(attempt, context);
                var removed = before.Except(after).ToArray();
                if (saved.Unsuccessful == null || !saved.Unsuccessful.Detail.Contains("ingredients.defs=")) throw new InvalidOperationException("Save did not reproduce ingredient configuration flip");
                if (removed.Length == 0 || removed.Any(name => recipe.fixedIngredientFilter.Allows(ThingDef.Named(name)))) throw new InvalidOperationException("Save changed something other than fixed-filter exclusions");
                return new { success = true, source = "RimWorld.Bill.ExposeData saving: recipe.fixedIngredientFilter pruning",
                    beforeCount = before.Length, afterCount = after.Length, removed = removed.Take(16).ToArray(), removedOmitted = Math.Max(0, removed.Length - 16),
                    detail = saved.Unsuccessful.Detail, scalarDetail = scalar.Unsuccessful.Detail, orderDetail = reordered.Unsuccessful.Detail };
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
