using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Disposable Core-only initial state. Cases add their channel after this
    // reset; nothing here advances time or suppresses ordinary simulation.
    public sealed class FoodChannelFixture
    {
        [Tool("test/food_channels_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Strip food stocks (including held food), crops, growing zones, animals, corpses and food-producing buildings from a paused disposable Core map. Seed exactly stockUnits of foodDef. Channel cases add only the source they prove afterwards.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken,
            int stockUnits = 0, string foodDef = "MealSurvivalPack")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var timer = Stopwatch.StartNew();
                var map = Find.CurrentMap;
                if (map == null || Current.Game == null || !Find.TickManager.Paused)
                    throw new InvalidOperationException("Paused disposable map required.");
                if (ModsConfig.ActiveModsInLoadOrder.Any(m => m.PackageId.StartsWith("ludeon.rimworld.", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Food channel fixture requires Core only.");
                if (stockUnits < 0 || stockUnits > 1000) throw new ArgumentException("stockUnits must be 0..1000.");
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(foodDef);
                var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead).ToList();
                if (people.Count == 0 || def == null || !def.IsNutritionGivingIngestible || def.IsDrug
                    || def.ingestible == null || (def.ingestible.foodType & (FoodTypeFlags.Corpse | FoodTypeFlags.Kibble)) != 0
                    || people.Any(p => !p.WillEat(def)))
                    throw new ArgumentException("Need colonists and human food all colonists can eat.");
                var anchor = new IntVec3((int)people.Average(p => p.Position.x), 0, (int)people.Average(p => p.Position.z));
                var cells = GenRadial.RadialCellsAround(anchor, 20, true).Where(c => c.InBounds(map) && !c.Fogged(map)
                    && c.Standable(map) && c.GetEdifice(map) == null
                    && people.Any(p => !p.Downed && p.CanReach(c, PathEndMode.OnCell, Danger.None))).Take(stockUnits).ToList();
                var stacks = (stockUnits + def.stackLimit - 1) / def.stackLimit;
                if (cells.Count < stacks) throw new InvalidOperationException("No reachable space for declared stock.");
                // Cancel jobs before removing carried ingredients and targets.
                foreach (var pawn in map.mapPawns.AllPawnsSpawned.ToList()) pawn.jobs?.StopAll();
                foreach (var zone in map.zoneManager.AllZones.OfType<Zone_Growing>().ToList()) zone.Delete();
                foreach (var thing in AllThings(map).Where(Remove).ToList())
                    if (!thing.Destroyed) thing.Destroy(DestroyMode.Vanish);
                var placed = new List<Thing>();
                for (var remaining = stockUnits; remaining > 0; remaining -= def.stackLimit)
                {
                    var food = ThingMaker.MakeThing(def);
                    food.stackCount = Math.Min(remaining, def.stackLimit);
                    GenSpawn.Spawn(food, cells[placed.Count], map);
                    food.SetForbidden(false, false);
                    placed.Add(food);
                }
                return new { success = true, stockUnits, foodDef,
                    declaredNutrition = placed.Sum(t => (double)t.stackCount * people.Min(p => FoodUtility.NutritionForEater(p, t))),
                    preparationMs = timer.ElapsedMilliseconds, anchor = new { x = anchor.x, z = anchor.z } };
            }, cancellationToken);
        }

        [Tool("test/food_channels_observe", Description = "Read remaining food channels and held food in the disposable fixture; no mutations.")]
        public async Task<object> Observe(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap ?? throw new InvalidOperationException("Loaded map required.");
                var things = AllThings(map).Where(t => !t.Destroyed).ToList();
                return new { success = true,
                    fields = map.zoneManager.AllZones.OfType<Zone_Growing>().Count(),
                    plants = things.OfType<Plant>().Count(FoodPlant),
                    animals = things.OfType<Pawn>().Count(p => p.RaceProps.Animal),
                    corpses = things.OfType<Corpse>().Count(),
                    producers = things.Count(Producer),
                    stock = things.Where(t => t.def.category == ThingCategory.Item && t.def.IsNutritionGivingIngestible)
                        .Select(t => new { defName = t.def.defName, units = t.stackCount, spawned = t.Spawned }).ToList() };
            }, cancellationToken);
        }

        private static bool FoodPlant(Plant p) => p.def.plant?.harvestedThingDef?.IsNutritionGivingIngestible == true;
        private static bool Producer(Thing t) => t is Building_PlantGrower || t is Building_NutrientPasteDispenser;
        private static bool Remove(Thing t) => t is Corpse || t is Pawn p && p.RaceProps.Animal
            || t is Plant plant && FoodPlant(plant) || Producer(t)
            || t.def.category == ThingCategory.Item && t.def.IsNutritionGivingIngestible;

        private static HashSet<Thing> AllThings(Map map)
        {
            var things = new HashSet<Thing>(map.listerThings.AllThings);
            var holders = new HashSet<IThingHolder>();
            void Visit(IThingHolder holder)
            {
                if (holder == null || !holders.Add(holder)) return;
                var direct = holder.GetDirectlyHeldThings();
                if (direct != null) foreach (var thing in direct) things.Add(thing);
                var children = new List<IThingHolder>();
                holder.GetChildHolders(children);
                foreach (var child in children) Visit(child);
            }
            foreach (var thing in things.ToArray()) if (thing is IThingHolder holder) Visit(holder);
            return things;
        }
    }
}
