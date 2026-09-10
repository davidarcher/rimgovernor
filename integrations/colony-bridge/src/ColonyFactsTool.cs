using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    /// <summary>Read-only domain facts for deterministic starter control.</summary>
    public sealed class ColonyFactsTools
    {
        [Tool("home/colony_facts", Title = "Deterministic colony facts",
            Description = "Read native nutrition, eater diet/policy/access, held food, rot deadlines, competing animal feed, crop labor and medical/mood inputs alongside starter colony facts. No game orders. Forecasts assume current conditions; future harvest and grazing are not stored food.")]
        public async Task<object> Facts(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Include a bounded terrain grid and native starter definitions/costs.", DefaultValue = false)] bool planning = false)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => Read(planning), cancellationToken).ConfigureAwait(false);
        }

        private static object Read(bool planning)
        {
            var map = Find.CurrentMap;
            if (map == null) return new { success = false, error = "Load a colony first" };
            var people = map.mapPawns.AllPawnsSpawned.Where(p => p.IsFreeColonist && !p.Dead).ToList();
            var conditions = new List<GameCondition>();
            map.gameConditionManager.GetAllGameConditionsAffectingMap(map, conditions);
            if (people.Count == 0) return new { success = false, error = "No living colonists" };
            var workers = people.Where(p => !p.Downed && !p.InMentalState && !p.Drafted).ToList();
            var center = new IntVec3((int)people.Average(p => p.Position.x), 0, (int)people.Average(p => p.Position.z));
            var things = map.listerThings.AllThings.Where(t => t.Spawned && !t.Position.Fogged(map)).ToList();
            Func<Thing, bool> reachable = t => workers.Any(p => p.CanReach(t, PathEndMode.Touch, Danger.None));
            Func<ThingDef, bool> humanFood = d => d != null && d.IsNutritionGivingIngestible && !d.IsDrug
                && d.ingestible != null && (d.ingestible.foodType & (FoodTypeFlags.Corpse | FoodTypeFlags.Kibble)) == 0
                && people.All(p => p.WillEat(d));
            var food = things.Where(t => t.def.category == ThingCategory.Item && humanFood(t.def)
                && (t.Faction == null || t.Faction.IsPlayer) && !t.IsForbidden(Faction.OfPlayerSilentFail)
                && t.IngestibleNow && people.All(p => p.WillEat(t)) && reachable(t)).ToList();
            var demand = people.Sum(p => p.needs?.food == null ? 0f :
                p.needs.food.FoodFallPerTickAssumingCategory(HungerCategory.Fed, true) * 60000f);
            var nutrition = food.Sum(t => t.stackCount * people.Min(p => FoodUtility.NutritionForEater(p, t)));
            var supplies = things.Where(t => t.def.category == ThingCategory.Item
                && (t.Faction == null || t.Faction.IsPlayer) && !t.IsForbidden(Faction.OfPlayerSilentFail)
                && reachable(t)).GroupBy(t => t.def.defName).ToDictionary(g => g.Key, g => g.Sum(t => t.stackCount));
            var beds = things.OfType<Building_Bed>().Where(b => b.Faction != null && b.Faction.IsPlayer
                && !b.ForPrisoners && !b.Medical && !b.IsForbidden(Faction.OfPlayerSilentFail) && reachable(b)).ToList();
            Func<Building_Bed, bool> indoors = b => b.GetRoom() != null && b.GetRoom().ProperRoom
                && !b.GetRoom().PsychologicallyOutdoors && b.GetRoom().OpenRoofCount == 0;
            var indoorBeds = beds.Where(indoors).ToList();
            var temperatures = indoorBeds.Select(b => b.GetRoom().Temperature).ToList();
            var farms = map.zoneManager.AllZones.OfType<Zone_Growing>().Select(zone => {
                var crop = BridgeCommon.PrivateInstanceField(typeof(Zone_Growing), "plantDefToGrow")?.GetValue(zone) as ThingDef;
                var cells = map.AllCells.Where(c => map.zoneManager.ZoneAt(c) == zone && !c.Fogged(map)).ToList();
                var plants = cells.Select(c => c.GetPlant(map)).Where(p => p != null && p.def == crop).ToList();
                var edible = crop?.plant?.harvestedThingDef;
                return new { id = zone.ID, label = zone.label, crop = crop?.defName,
                    usableCells = cells.Count(c => map.fertilityGrid.FertilityAt(c) >= (crop?.plant?.fertilityMin ?? 100f)),
                    plantedCells = plants.Count,
                    growingCells = plants.Count(p => p.GrowthRateFactor_Temperature > 0 && p.GrowthRateFactor_Fertility > 0),
                    edible = humanFood(edible),
                    // A lower bound only: darkness, sowing/harvesting labor and weather can delay harvest.
                    harvestLowerBoundDays = plants.Count == 0 ? (float?)null : plants.Min(p =>
                        (1f - p.Growth) * p.def.plant.growDays / Math.Max(.01f, p.GrowthRateFactor_Fertility)),
                    nutritionPerHarvestCell = humanFood(edible) ? crop.plant.harvestYield * edible.GetStatValueAbstract(StatDefOf.Nutrition) : 0f };
            }).ToList();
            var cooking = things.OfType<Building_WorkTable>().Where(b => b.Faction != null && b.Faction.IsPlayer
                && reachable(b) && b.def.AllRecipes.Any(r => r.products.Any(p => humanFood(p.thingDef))))
                .Select(b => new { id = b.GetUniqueLoadID(), defName = b.def.defName,
                    position = new { x = b.Position.x, z = b.Position.z },
                    usable = !b.IsBurning() && (b.TryGetComp<CompPowerTrader>() == null || b.TryGetComp<CompPowerTrader>().PowerOn)
                        && (b.TryGetComp<CompRefuelable>() == null || b.TryGetComp<CompRefuelable>().HasFuel),
                    recipes = b.def.AllRecipes.Where(r => r.products.Any(p => humanFood(p.thingDef))).Select(r => r.defName).ToList(),
                    bills = b.BillStack.Bills.Select(bill => new { recipe = bill.recipe.defName, suspended = bill.suspended }).ToList() }).ToList();
            var butchering = things.OfType<Building_WorkTable>().Where(b => b.Faction != null && b.Faction.IsPlayer
                && reachable(b) && b.def.AllRecipes.Any(r => r.defName == "ButcherCorpseFlesh"))
                .Select(b => new { id = b.ThingID,
                    bills = b.BillStack.Bills.Select(bill => new { recipe = bill.recipe.defName, suspended = bill.suspended }).ToList() }).ToList();
            var acquisition = things.OfType<Plant>().Where(p => p.HarvestableNow && p.Position.DistanceTo(center) <= 35
                && reachable(p) && (p.def.plant.IsTree || (humanFood(p.def.plant.harvestedThingDef)
                    && !(map.zoneManager.ZoneAt(p.Position) is Zone_Growing))))
                .GroupBy(p => p.def.plant.IsTree)
                .SelectMany(g => g.OrderBy(p => p.Position.DistanceToSquared(center)).ThenBy(p => p.thingIDNumber).Take(40))
                .Select(p => new { id = p.GetUniqueLoadID(), x = p.Position.x, z = p.Position.z,
                    resource = p.def.plant.harvestedThingDef?.defName, tree = p.def.plant.IsTree,
                    food = humanFood(p.def.plant.harvestedThingDef), yield = p.YieldNow(),
                    nutritionYield = humanFood(p.def.plant.harvestedThingDef)
                        ? p.YieldNow() * p.def.plant.harvestedThingDef.GetStatValueAbstract(StatDefOf.Nutrition) : 0f,
                    designated = map.designationManager.DesignationOn(p) != null }).ToList();
            var allowedSupplies = things.Where(t => t.def.category == ThingCategory.Item && (t.Faction == null || t.Faction.IsPlayer)
                && (t.def.IsNutritionGivingIngestible || t.def.IsWeapon || t.def.IsMedicine || t.def.IsStuff || t.def.defName == "Silver")
                && t.IsForbidden(Faction.OfPlayerSilentFail) && t.Position.DistanceTo(center) <= 20 && reachable(t))
                .OrderBy(t => t.Position.DistanceToSquared(center)).ThenBy(t => t.thingIDNumber).Take(80)
                .Select(t => new { x = t.Position.x, z = t.Position.z }).Distinct().ToList();
            var foodStorage = map.zoneManager.AllZones.OfType<Zone_Stockpile>().Any(zone =>
                zone.GetStoreSettings()?.filter != null
                && DefDatabase<ThingDef>.AllDefsListForReading.Any(d => humanFood(d) && zone.GetStoreSettings().filter.Allows(d))
                && map.AllCells.Count(c => map.zoneManager.ZoneAt(c) == zone && c.Roofed(map)
                    && c.GetRoom(map) != null && c.GetRoom(map).ProperRoom && !c.GetRoom(map).PsychologicallyOutdoors) >= 9);
            var policyDefs = DefDatabase<ThingDef>.AllDefsListForReading
                .SelectMany(d => d.costList ?? new List<ThingDefCountClass>()).Select(c => c.thingDef)
                .Concat(DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.IsStuff || d.IsMedicine || supplies.ContainsKey(d.defName)))
                .Concat(DefDatabase<RecipeDef>.AllDefsListForReading.SelectMany(r => r.products ?? new List<ThingDefCountClass>()).Select(p => p.thingDef))
                .Where(d => d != null).Distinct().OrderBy(d => d.defName)
                .ToDictionary(d => d.defName, d => d.label);
            var result = new Dictionary<string, object> {
                ["success"] = true, ["tick"] = Find.TickManager.TicksGame, ["colonyNaming"] = ColonyNamingTools.Snapshot(),
                ["colonists"] = people.Count, ["workers"] = workers.Count, ["center"] = new { x = center.x, z = center.z },
                ["mapSize"] = new { width = map.Size.x, height = map.Size.z }, ["biome"] = map.Biome.defName,
                ["foodNutrition"] = nutrition, ["nutritionPerDay"] = demand,
                ["foodRunwayDays"] = demand > 0 ? (object)(nutrition / demand) : null,
                ["foodSupply"] = FoodSupplyFacts.Read(people, things.Where(t => t.def.category == ThingCategory.Item
                    && t.def.IsNutritionGivingIngestible && !t.def.IsDrug && t.IngestibleNow
                    && (t.Faction == null || t.Faction.IsPlayer)).ToList()),
                ["nativeForecastInputs"] = ForecastFacts.Read(map, people, things),
                ["gearUpkeep"] = planning ? GearUpkeepTools.Run(null, null, null, true) : null,
                ["pendingFoodNutrition"] = things.OfType<Plant>().Where(p => p.HarvestableNow
                    && humanFood(p.def.plant.harvestedThingDef) && !(map.zoneManager.ZoneAt(p.Position) is Zone_Growing)
                    && map.designationManager.DesignationOn(p, DesignationDefOf.HarvestPlant) != null)
                    .Sum(p => p.YieldNow() * p.def.plant.harvestedThingDef.GetStatValueAbstract(StatDefOf.Nutrition)),
                ["resources"] = supplies, ["policyResources"] = policyDefs, ["bedCapacity"] = beds.Sum(b => b.SleepingSlotsCount),
                ["indoorSleepingCapacity"] = indoorBeds.Sum(b => b.SleepingSlotsCount),
                ["sleepingTemperatureMin"] = temperatures.Count == 0 ? (object)null : temperatures.Min(),
                ["sleepingTemperatureMax"] = temperatures.Count == 0 ? (object)null : temperatures.Max(),
                ["outdoorTemperature"] = map.mapTemperature.OutdoorTemp,
                ["environment"] = new {
                    conditions = conditions.Select(c => new {
                        id = c.uniqueID, defName = c.def.defName, implementation = c.GetType().FullName,
                        label = c.LabelCap.ToString(), permanent = c.Permanent,
                        ticksLeft = c.Permanent ? (int?)null : c.TicksLeft
                    }).ToList()
                },
                ["farms"] = farms, ["cooking"] = cooking, ["acquisition"] = acquisition,
                ["butchering"] = butchering,
                ["foodStorage"] = foodStorage,
                ["waste"] = HomeWasteTools.Census("", ""),
                ["forbiddenSupplies"] = allowedSupplies,
                ["notes"] = new[] { "Raw runway is shared-diet accessible stock divided by fed consumption. foodSupply separately observes holder-owned inventory and native rot deadlines for the controller's per-colonist forecast; neither guarantees future temperature or access.",
                    "Harvest ETA is an optimistic lower bound; it cannot clear food risk. Growing cells exclude temperature/fertility failures but do not forecast seasons." }
            };
            if (planning) {
                var definitions = new Dictionary<string, object>();
                foreach (var name in new[] { "Wall", "Door", "Bed", "SleepingSpot", "Campfire", "ButcherSpot", "FueledStove", "Heater", "PassiveCooler", "Cooler", "WoodFiredGenerator", "PowerConduit", "Sandbags", "Barricade", "Plant_Rice" }) {
                    var def = DefDatabase<ThingDef>.GetNamedSilentFail(name);
                    if (def == null) continue;
                    var wood = DefDatabase<ThingDef>.GetNamedSilentFail("WoodLog");
                    var stuff = def.MadeFromStuff ? wood : null;
                    if (def.MadeFromStuff && (stuff == null || !GenStuff.AllowedStuffsFor(def).Contains(stuff))) continue;
                    definitions[name] = new { defName = name, stuff = stuff?.defName,
                        available = def.researchPrerequisites == null || def.researchPrerequisites.All(r => r.IsFinished),
                        width = def.size.x, height = def.size.z,
                        costs = def.CostListAdjusted(stuff, false).ToDictionary(c => c.thingDef.defName, c => c.count),
                        growDays = def.plant?.growDays, fertilityMin = def.plant?.fertilityMin,
                        harvestNutrition = def.plant?.harvestedThingDef == null ? (float?)null :
                            def.plant.harvestYield * def.plant.harvestedThingDef.GetStatValueAbstract(StatDefOf.Nutrition) };
                }
                var cells = new List<object>();
                for (int z = Math.Max(0, center.z - 22); z <= Math.Min(map.Size.z - 1, center.z + 22); z++)
                for (int x = Math.Max(0, center.x - 22); x <= Math.Min(map.Size.x - 1, center.x + 22); x++) {
                    var c = new IntVec3(x, 0, z);
                    if (c.Fogged(map)) continue;
                    cells.Add(new { x, z, walkable = c.Walkable(map), fertility = map.fertilityGrid.FertilityAt(c),
                        occupied = c.GetEdifice(map) != null || map.thingGrid.ThingsListAtFast(c).Any(t => t is Blueprint || t is Frame),
                        zone = map.zoneManager.ZoneAt(c) != null, roofed = c.Roofed(map),
                        supportsLight = c.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Light) });
                }
                result["definitions"] = definitions;
                result["cells"] = cells;
            }
            return result;
        }
    }
}
