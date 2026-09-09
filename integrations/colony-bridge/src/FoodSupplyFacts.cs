using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    /// Read-only food accounting; inventory belongs to its holder until hauled.
    internal static class FoodSupplyFacts
    {
        internal static object Read(List<Pawn> people, List<Thing> shared)
        {
            var stocks = new List<object>();
            var seen = new HashSet<int>();
            var consumers = people.Where(p => p.needs?.food != null).Select(p => new {
                id = p.GetUniqueLoadID(),
                nutritionPerDay = p.needs.food.FoodFallPerTickAssumingCategory(HungerCategory.Fed, true) * 60000f
            }).ToList();
            foreach (var thing in shared)
                if (seen.Add(thing.thingIDNumber)) stocks.Add(Stock(thing, people, null));
            foreach (var pawn in people.Where(p => !p.Downed && !p.InMentalState))
            {
                var carried = new List<Thing>();
                if (pawn.inventory?.innerContainer != null) carried.AddRange(pawn.inventory.innerContainer);
                if (pawn.carryTracker?.CarriedThing != null) carried.Add(pawn.carryTracker.CarriedThing);
                foreach (var thing in carried)
                {
                    var def = thing.def;
                    if (!thing.Spawned && def.IsNutritionGivingIngestible && !def.IsDrug && def.ingestible != null
                        && (def.ingestible.foodType & (FoodTypeFlags.Corpse | FoodTypeFlags.Kibble)) == 0
                        && thing.IngestibleNow && pawn.WillEat(thing) && seen.Add(thing.thingIDNumber))
                        stocks.Add(Stock(thing, new List<Pawn> { pawn }, pawn.GetUniqueLoadID()));
                }
            }
            return new { readable = true, consumers, stocks,
                assumptions = new[] {
                    "Shared food is accessible current shared-diet stock. Held food can feed only its observed holder until hauled.",
                    "Rot deadlines assume the current native ambient temperature; frozen food can thaw. Future harvest, animal feed and future access are not guaranteed.",
                    "Consumption uses native fed demand and minimum native nutrition per eater, not a definition-name table." } };
        }

        private static object Stock(Thing thing, List<Pawn> eaters, string holder)
        {
            var rot = thing.TryGetComp<CompRottable>();
            var perishable = rot != null && rot.Active;
            return new { id = thing.GetUniqueLoadID(), defName = thing.def.defName, count = thing.stackCount,
                holder, nutrition = thing.stackCount * eaters.Min(p => FoodUtility.NutritionForEater(p, thing)),
                perishable, rotTicks = perishable ? (int?)Math.Max(0, rot.TicksUntilRotAtCurrentTemp) : null,
                temperature = thing.AmbientTemperature,
                roofed = thing.Spawned ? (bool?)thing.Position.Roofed(thing.Map) : null };
        }
    }
}
