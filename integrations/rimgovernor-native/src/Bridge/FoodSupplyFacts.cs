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
                id = p.GetUniqueLoadID(), humanMeatAcceptable = HumanFoodFacts.AcceptsMeat(p),
                nutritionPerDay = p.needs.food.FoodFallPerTickAssumingCategory(HungerCategory.Fed, true) * 60000f
            }).ToList();
            foreach (var thing in shared)
            {
                if (thing is Corpse corpse)
                {
                    if (!FreshFoodCorpse(corpse)) continue;
                    var meat = corpse.InnerPawn.RaceProps.meatDef;
                    var meatEaters = people.Where(p => p.needs?.food != null && !p.Downed && !p.InMentalState
                        && CanUseMeat(p, meat) && (!corpse.InnerPawn.RaceProps.Humanlike || HasHumanButcher(corpse.Map))
                        && p.CanReach(corpse, PathEndMode.Touch, Danger.None)
                        && (p.playerSettings?.AreaRestrictionInPawnCurrentMap == null
                            || p.playerSettings.AreaRestrictionInPawnCurrentMap[corpse.Position])).ToList();
                    if (seen.Add(corpse.thingIDNumber))
                    {
                        var row = CorpseStock(corpse, meatEaters);
                        if (row.meatAmount > 0) stocks.Add(row);
                    }
                    continue;
                }
                // Kibble is animal feed: a humanlike pawn only eats it when nothing
                // better is reachable, so it is no planned colonist food (the held
                // and colony-stock reads exclude it the same way) and counting
                // colonists as its eaters diluted a pet's share seventeen-fold
                // under a delivered feed stockpile (#311).
                var kibble = thing.def.ingestible != null && (thing.def.ingestible.foodType & FoodTypeFlags.Kibble) != 0;
                var eaters = people.Where(p => p.needs?.food != null && !p.Downed && !p.InMentalState
                    && !(kibble && p.RaceProps.Humanlike)
                    && p.WillEat(thing) && PolicyAllows(p, thing) && (IsReserve(thing) || !thing.IsForbidden(p))
                    && p.CanReach(thing, PathEndMode.Touch, Danger.None)
                    && (p.playerSettings?.AreaRestrictionInPawnCurrentMap == null
                        || p.playerSettings.AreaRestrictionInPawnCurrentMap[thing.Position])).ToList();
                if ((eaters.Count > 0 || HumanFoodFacts.ContainsHumanMeat(thing)) && seen.Add(thing.thingIDNumber)) stocks.Add(Stock(thing, eaters, null));
            }
            foreach (var pawn in people.Where(p => !p.Downed && !p.InMentalState))
            {
                var carried = new List<Thing>();
                if (pawn.inventory?.innerContainer != null) carried.AddRange(pawn.inventory.innerContainer);
                if (pawn.carryTracker?.CarriedThing != null) carried.Add(pawn.carryTracker.CarriedThing);
                foreach (var thing in carried)
                {
                    // A butcher carrying a released corpse still funds the
                    // shared cooking window; it is not a private meal.
                    if (thing is Corpse carriedCorpse && FreshFoodCorpse(carriedCorpse) && seen.Add(thing.thingIDNumber))
                    {
                        var row = CorpseStock(carriedCorpse, people.Where(p => p.needs?.food != null
                            && !p.Downed && !p.InMentalState && CanUseMeat(p, carriedCorpse.InnerPawn.RaceProps.meatDef) && (!carriedCorpse.InnerPawn.RaceProps.Humanlike || HasHumanButcher(pawn.Map))
                            && p.CanReach(pawn, PathEndMode.Touch, Danger.None)).ToList());
                        if (row.meatAmount > 0) stocks.Add(row);
                        continue;
                    }
                    var def = thing.def;
                    if (!thing.Spawned && def.IsNutritionGivingIngestible && !def.IsDrug && def.ingestible != null
                        && (def.ingestible.foodType & (FoodTypeFlags.Corpse | FoodTypeFlags.Kibble)) == 0
                        && thing.IngestibleNow && pawn.WillEat(thing) && PolicyAllows(pawn, thing) && seen.Add(thing.thingIDNumber))
                        stocks.Add(Stock(thing, new List<Pawn> { pawn }, pawn.GetUniqueLoadID()));
                }
            }
            return new Snapshot { readable = true, consumers = consumers, stocks = stocks, larder = FoodLarderFacts.Read(people, shared, stocks),
                assumptions = new[] {
                    "Stock is apportioned only among observed eligible eaters by fed demand. Downed consumers need assistance; held food feeds only its holder.",
                    "Rot deadlines assume the current native ambient temperature; frozen food can thaw. Future harvest, animal feed and future access are not guaranteed.",
                    "Consumption uses native fed demand and minimum native nutrition per eater, not a definition-name table." } };
        }

        internal static bool IsReserve(Thing thing) => thing.Spawned && thing.IsForbidden(Faction.OfPlayer)
            && (thing.def.defName == "MealSurvivalPack" || thing.def.defName == "Pemmican");

        private static bool PolicyAllows(Pawn pawn, Thing food)
        {
            return pawn.foodRestriction?.GetCurrentRespectedRestriction(pawn)?.filter.Allows(food) != false;
        }

        private static bool CanUseMeat(Pawn pawn, ThingDef meat)
        {
            bool Allowed(ThingDef food) => pawn.WillEat(food)
                && pawn.foodRestriction?.GetCurrentRespectedRestriction(pawn)?.filter.Allows(food) != false;
            if (Allowed(meat)) return true;
            return pawn.Map.listerThings.AllThings.OfType<Building_WorkTable>().Where(b => b.Faction == Faction.OfPlayer)
                .SelectMany(b => b.BillStack.Bills).OfType<Bill_Production>().Any(b => !b.suspended
                    && b.ingredientFilter.Allows(meat) && b.recipe.ingredients.Any(i => i.filter.Allows(meat))
                    && b.recipe.products.Any(p => p.thingDef.IsNutritionGivingIngestible && Allowed(p.thingDef)));
        }

        internal static bool FreshAnimalCorpse(Thing thing) => thing is Corpse corpse
            && corpse.InnerPawn?.RaceProps.Animal == true && corpse.GetRotStage() == RotStage.Fresh
            && corpse.InnerPawn.RaceProps.meatDef?.IsNutritionGivingIngestible == true;

        internal static bool FreshFoodCorpse(Thing thing) => thing is Corpse corpse
            && (corpse.InnerPawn.RaceProps.Animal || corpse.InnerPawn.RaceProps.Humanlike)
            && corpse.GetRotStage()==RotStage.Fresh && corpse.InnerPawn.RaceProps.meatDef?.IsNutritionGivingIngestible==true;
        private static bool HasHumanButcher(Map map) => map.listerThings.AllThings.OfType<Building_WorkTable>()
            .Any(b=>b.Faction==Faction.OfPlayer && b.def.AllRecipes.Any(r=>r.defName=="ButcherCorpseFlesh")
                && map.mapPawns.FreeColonistsSpawned.Any(p=>HumanFoodFacts.CanButcher(p,b)));

        private static StockFacts CorpseStock(Corpse corpse, List<Pawn> eaters)
        {
            var row = Stock(corpse, eaters, null);
            row.corpse = true;
            row.isHumanlike = corpse.InnerPawn.RaceProps.Humanlike;
            row.forbidden = corpse.IsForbidden(Faction.OfPlayer);
            row.meatAmount = Math.Max(0, corpse.InnerPawn.GetStatValue(StatDefOf.MeatAmount));
            row.bodySize = corpse.InnerPawn.BodySize;
            row.tileFootprint = 1;
            row.nutrition = row.meatAmount.Value * corpse.InnerPawn.RaceProps.meatDef.GetStatValueAbstract(StatDefOf.Nutrition);
            return row;
        }

        internal static bool SharedFood(Thing thing) => FreshFoodCorpse(thing)
            || (!(thing is Corpse) && thing.def.category == ThingCategory.Item
                && thing.def.IsNutritionGivingIngestible && !thing.def.IsDrug && thing.IngestibleNow
                && (thing.Faction == null || thing.Faction.IsPlayer));

        private static StockFacts Stock(Thing thing, List<Pawn> eaters, string? holder)
        {
            var rot = thing.TryGetComp<CompRottable>();
            var perishable = rot != null && rot.Active;
            return new StockFacts { id = thing.GetUniqueLoadID(), defName = thing.def.defName, count = thing.stackCount,
                rawClass = NativeMealRecipeFacts.InCategory(thing.def, "MeatRaw") ? 1 : NativeMealRecipeFacts.InCategory(thing.def, "PlantFoodRaw") ? 2 : NativeMealRecipeFacts.InCategory(thing.def, "AnimalProductRaw") ? 3 : 0,
                holder = holder, reserve = IsReserve(thing), isHumanMeat = HumanFoodFacts.ContainsHumanMeat(thing), rawMeat = thing.def.IsMeat, vegetable = thing.def.ingestible != null && (thing.def.ingestible.foodType & FoodTypeFlags.VegetableOrFruit) != 0, nutrition = thing is Corpse ? 0 : thing.stackCount * (eaters.Count>0 ? eaters.Min(p => FoodUtility.NutritionForEater(p, thing)) : thing.def.GetStatValueAbstract(StatDefOf.Nutrition)),
                eaters = eaters.Select(p => p.GetUniqueLoadID()).ToList(),
                perishable = perishable, rotTicks = rot != null && perishable ? (int?)Math.Max(0, rot.TicksUntilRotAtCurrentTemp) : null,
                temperature = thing.AmbientTemperature,
                roofed = thing.Spawned ? (bool?)thing.Position.Roofed(thing.Map) : null,
                roomId = thing.Spawned ? thing.Position.GetRoom(thing.Map)?.ID.ToString(System.Globalization.CultureInfo.InvariantCulture) : null };
        }

        // Shared typed source for the compatibility JSON and protobuf projections.
        internal sealed class Snapshot {
            internal FoodLarderFacts.Snapshot? larder;
            public bool readable { get; set; }
            public List<ConsumerFacts> consumers { get; set; } = new List<ConsumerFacts>();
            public List<StockFacts> stocks { get; set; } = new List<StockFacts>();
            public string[] assumptions { get; set; } = Array.Empty<string>();
        }
        internal sealed class ConsumerFacts {
            public string? id { get; set; }
            public float nutritionPerDay { get; set; }
            public bool humanMeatAcceptable { get; set; }
        }
        internal sealed class StockFacts {
            public string? id { get; set; }
            public string? defName { get; set; }
            public int count { get; set; }
            public string? holder { get; set; }
            public float nutrition { get; set; }
            public List<string>? eaters { get; set; }
            public int rawClass { get; set; }
            public bool reserve { get; set; }
            public bool isHumanMeat { get; set; }
            public bool rawMeat { get; set; }
            public bool isHumanlike { get; set; }
            public bool vegetable { get; set; }
            public bool perishable { get; set; }
            public int? rotTicks { get; set; }
            public float temperature { get; set; }
            public bool? roofed { get; set; }
            public string? roomId { get; set; }
            public bool corpse { get; set; }
            public bool? forbidden { get; set; }
            public float? meatAmount { get; set; }
            public float? bodySize { get; set; }
            public int? tileFootprint { get; set; }
        }
    }
}
