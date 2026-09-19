#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    /// Read-only food accounting; inventory belongs to its holder until hauled.
    internal static class FoodSupplyFacts
    {
        internal static Snapshot Read(List<Pawn> people, List<Thing> shared)
        {
            var stocks = new List<StockFacts>();
            var seen = new HashSet<int>();
            var consumers = people.Where(p => p.needs?.food != null).Select(p => new ConsumerFacts {
                id = p.GetUniqueLoadID(),
                nutritionPerDay = p.needs.food.FoodFallPerTickAssumingCategory(HungerCategory.Fed, true) * 60000f
            }).ToList();
            foreach (var thing in shared)
            {
                // Kibble is animal feed: a humanlike pawn only eats it when nothing
                // better is reachable, so it is no planned colonist food (the held
                // and colony-stock reads exclude it the same way) and counting
                // colonists as its eaters diluted a pet's share seventeen-fold
                // under a delivered feed stockpile (#311).
                var kibble = thing.def.ingestible != null && (thing.def.ingestible.foodType & FoodTypeFlags.Kibble) != 0;
                var eaters = people.Where(p => p.needs?.food != null && !p.Downed && !p.InMentalState
                    && !(kibble && p.RaceProps.Humanlike)
                    && p.WillEat(thing) && PolicyAllows(p, thing) && !thing.IsForbidden(p)
                    && p.CanReach(thing, PathEndMode.Touch, Danger.None)
                    && (p.playerSettings?.AreaRestrictionInPawnCurrentMap == null
                        || p.playerSettings.AreaRestrictionInPawnCurrentMap[thing.Position])).ToList();
                if (eaters.Count > 0 && seen.Add(thing.thingIDNumber)) stocks.Add(Stock(thing, eaters, null));
            }
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
                        && thing.IngestibleNow && pawn.WillEat(thing) && PolicyAllows(pawn, thing) && seen.Add(thing.thingIDNumber))
                        stocks.Add(Stock(thing, new List<Pawn> { pawn }, pawn.GetUniqueLoadID()));
                }
            }
            return new Snapshot { readable = true, consumers = consumers, stocks = stocks,
                assumptions = new[] {
                    "Stock is apportioned only among observed eligible eaters by fed demand. Downed consumers need assistance; held food feeds only its holder.",
                    "Rot deadlines assume the current native ambient temperature; frozen food can thaw. Future harvest, animal feed and future access are not guaranteed.",
                    "Consumption uses native fed demand and minimum native nutrition per eater, not a definition-name table." } };
        }

        private static bool PolicyAllows(Pawn pawn, Thing food)
        {
            return pawn.foodRestriction?.GetCurrentRespectedRestriction(pawn)?.filter.Allows(food) != false;
        }

        private static StockFacts Stock(Thing thing, List<Pawn> eaters, string? holder)
        {
            var rot = thing.TryGetComp<CompRottable>();
            var perishable = rot != null && rot.Active;
            return new StockFacts { id = thing.GetUniqueLoadID(), defName = thing.def.defName, count = thing.stackCount,
                holder = holder, nutrition = thing.stackCount * eaters.Min(p => FoodUtility.NutritionForEater(p, thing)),
                eaters = eaters.Select(p => p.GetUniqueLoadID()).ToList(),
                perishable = perishable, rotTicks = rot != null && perishable ? (int?)Math.Max(0, rot.TicksUntilRotAtCurrentTemp) : null,
                temperature = thing.AmbientTemperature,
                roofed = thing.Spawned ? (bool?)thing.Position.Roofed(thing.Map) : null,
                roomId = thing.Spawned ? thing.Position.GetRoom(thing.Map)?.ID.ToString(System.Globalization.CultureInfo.InvariantCulture) : null };
        }

        // Shared typed source for the compatibility JSON and protobuf projections.
        internal sealed class Snapshot {
            public bool readable { get; set; }
            public List<ConsumerFacts> consumers { get; set; } = new List<ConsumerFacts>();
            public List<StockFacts> stocks { get; set; } = new List<StockFacts>();
            public string[] assumptions { get; set; } = Array.Empty<string>();
        }
        internal sealed class ConsumerFacts {
            public string? id { get; set; }
            public float nutritionPerDay { get; set; }
        }
        internal sealed class StockFacts {
            public string? id { get; set; }
            public string? defName { get; set; }
            public int count { get; set; }
            public string? holder { get; set; }
            public float nutrition { get; set; }
            public List<string>? eaters { get; set; }
            public bool perishable { get; set; }
            public int? rotTicks { get; set; }
            public float temperature { get; set; }
            public bool? roofed { get; set; }
            public string? roomId { get; set; }
        }
    }
}
