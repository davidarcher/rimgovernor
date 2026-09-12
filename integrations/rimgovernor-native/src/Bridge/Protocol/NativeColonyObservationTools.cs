#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    public sealed class NativeColonyObservationTools
    {
        private const string ToolName = "rimgovernor/observations_read_colony_facts";
        private static readonly string[] StarterDefinitions = {
            "Wall", "Door", "Bed", "SleepingSpot", "Campfire", "ButcherSpot", "FueledStove", "Heater",
            "PassiveCooler", "Cooler", "WoodFiredGenerator", "PowerConduit", "Sandbags", "Barricade",
            "StandingLamp", "SimpleResearchBench", "Table1x2c", "DiningChair", "HorseshoesPin",
            "Plant_Rice", "Plant_Potato", "Plant_Corn", "TableStonecutter", "Fence", "FenceGate", "PenMarker"
        };

        [Tool(ToolName, Title = "Read typed routine colony facts", Description = "Native colony, accessible stock, sleeping capacity, temperature and storage facts. Optional bounded starter geometry/definitions. Raw food runway is not a diet/rot forecast. Unported sections are explicitly unavailable. Read-only; no authority or orders.")]
        [ToolResponse("payload", "string", "Official ColonyFactsReply ProtoJSON, bounded to1MiB.", Always = true)]
        public async Task<object> ReadColonyFacts(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Raw ColonyFactsRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, ToolName, request!, Obs.ColonyFactsRequest.Parser, out var parsed, out var failure)
                || !Validate(parsed, out failure)) return ProtoBoundary.Encode(new Obs.ColonyFactsReply { Failure = failure });
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope.ExpectedIdentity, map, out var context, out failure))
                    return ProtoBoundary.Encode(new Obs.ColonyFactsReply { Failure = failure });
                try {
                    var reply = new Obs.ColonyFactsReply { Observed = Read(map, parsed, context) };
                    if (Encoding.UTF8.GetByteCount(ProtoBoundary.Format(reply, compact: true)) > ProtoBoundary.MaximumEnvelopeBytes) throw new ReadLimit("Colony facts exceed1MiB.");
                    return ProtoBoundary.Encode(reply, compact: true);
                }
                catch (ReadLimit e) { return ProtoBoundary.Encode(new Obs.ColonyFactsReply { Unavailable = Unavailable(Common.UnavailableReason.LimitExceeded, e.Message) }); }
                catch (Exception) { return ProtoBoundary.Encode(new Obs.ColonyFactsReply { Unavailable = Unavailable(Common.UnavailableReason.ReadFailed, "Native colony facts could not be read completely.") }); }
            }, cancellationToken).ConfigureAwait(false);
        }

        internal static bool Validate(Obs.ColonyFactsRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity, page1..256 and unique native definition names required; definitions require planning.");
            return request?.Scope?.ExpectedIdentity != null
                && (request.Page == null || (!request.Page.HasLimit || request.Page.Limit >= 1 && request.Page.Limit <= 256)
                    && (!request.Page.HasCursor || request.Page.Cursor.Length == 0))
                && request.RequestedDefinitionNames.Count <= 256
                && (request.Planning || request.RequestedDefinitionNames.Count == 0)
                && request.RequestedDefinitionNames.All(ProtoBoundary.IsIdentifier)
                && request.RequestedDefinitionNames.Distinct(StringComparer.Ordinal).Count() == request.RequestedDefinitionNames.Count;
        }

        private static Obs.ColonyFactsSnapshot Read(Map map, Obs.ColonyFactsRequest request, Common.ObservationContext context)
        {
            var player = Faction.OfPlayerSilentFail ?? throw new InvalidOperationException("Player faction unavailable.");
            var people = map.mapPawns.AllPawnsSpawned.Where(p => p.IsFreeColonist && !p.Dead).ToList();
            var limit = request.Page?.HasLimit == true ? (int)request.Page.Limit : 256;
            Bound(people.Count, limit);
            if (people.Count == 0) throw new InvalidOperationException("No colony anchor.");
            var workers = people.Where(p => !p.Downed && !p.InMentalState && !p.Drafted).ToList();
            var center = new IntVec3((int)people.Average(p => p.Position.x), 0, (int)people.Average(p => p.Position.z));
            var things = map.listerThings.AllThings.Where(t => t.Spawned && !t.Position.Fogged(map)).ToList();
            Func<Thing, bool> reachable = t => workers.Any(p =>
                (p.playerSettings?.AreaRestrictionInPawnCurrentMap == null || p.playerSettings.AreaRestrictionInPawnCurrentMap[t.Position])
                && p.CanReach(t, PathEndMode.Touch, Danger.None));
            Func<ThingDef, bool> humanFood = d => d != null && d.IsNutritionGivingIngestible && !d.IsDrug
                && d.ingestible != null && (d.ingestible.foodType & (FoodTypeFlags.Corpse | FoodTypeFlags.Kibble)) == 0
                && people.All(p => p.WillEat(d));
            var items = things.Where(t => t.def.category == ThingCategory.Item && (t.Faction == null || t.Faction.IsPlayer)
                && !t.IsForbidden(player) && reachable(t)).ToList();
            var stock = items.GroupBy(t => t.def).OrderBy(g => g.Key.defName, StringComparer.Ordinal).ToList();
            Bound(stock.Count, limit);
            var beds = things.OfType<Building_Bed>().Where(b => b.Faction == player && !b.ForPrisoners && !b.Medical
                && !b.IsForbidden(player) && reachable(b)).ToList();
            var indoorBeds = beds.Where(b => b.GetRoom() != null && b.GetRoom().ProperRoom
                && !b.GetRoom().PsychologicallyOutdoors && b.GetRoom().OpenRoofCount == 0).ToList();
            var temperatures = indoorBeds.Select(b => Finite(b.GetRoom().Temperature)).ToList();
            var nutrition = items.Where(t => humanFood(t.def) && t.IngestibleNow && people.All(p => p.WillEat(t)))
                .Sum(t => (double)t.stackCount * people.Min(p => FoodUtility.NutritionForEater(p, t)));
            // Raw native demand/runway remains distinct from the controller's
            // diet, held-food, rot and competing-animal forecast.
            var demand = people.Sum(p => p.needs?.food == null ? 0.0 : p.needs.food.FoodFallPerTickAssumingCategory(HungerCategory.Fed, true) * 60000.0);
            var result = new Obs.ColonyFactsSnapshot { Context = context, ColonistCount = (uint)people.Count,
                WorkerCount = (uint)workers.Count, Center = Cell(center), MapSize = Size(map), Biome = map.Biome.defName,
                BedCapacity = checked((uint)beds.Sum(b => b.SleepingSlotsCount)), IndoorSleepingCapacity = checked((uint)indoorBeds.Sum(b => b.SleepingSlotsCount)),
                FoodNutrition = Finite(nutrition), NutritionPerDay = Finite(demand), OutdoorTemperatureC = Finite(map.mapTemperature.OutdoorTemp),
                Completeness = Complete(1),
                FoodSupply = new Obs.FoodSupplySection { Observed = Food(FoodSupplyFacts.Read(people,
                    things.Where(t => t.def.category == ThingCategory.Item && t.def.IsNutritionGivingIngestible
                        && !t.def.IsDrug && t.IngestibleNow && (t.Faction == null || t.Faction.IsPlayer)).ToList())) },
                Forecast = new Obs.ForecastSection { Observed = Forecast(ForecastFacts.Read(map, people, things)) },
                Upkeep = ReadComfort(map),
                Development = new Obs.DevelopmentSection { Observed = ReadPower(map, limit) }
            };
            if (demand > 0) result.FoodRunwayDays = Finite(nutrition / demand);
            else result.Issues.Add(Issue("food_runway_days", Common.UnavailableReason.NotApplicable, "No observed nutrition demand."));
            if (temperatures.Count > 0) { result.SleepingTemperatureMinC = temperatures.Min(); result.SleepingTemperatureMaxC = temperatures.Max(); }
            else {
                result.Issues.Add(Issue("sleeping_temperature_min_c", Common.UnavailableReason.NotApplicable, "No eligible indoor sleeping place."));
                result.Issues.Add(Issue("sleeping_temperature_max_c", Common.UnavailableReason.NotApplicable, "No eligible indoor sleeping place."));
            }
            foreach (var group in stock) result.Resources.Add(new Obs.Quantity { DefName = group.Key.defName, Units = group.Sum(t => (long)t.stackCount) });
            result.FoodStorage = map.zoneManager.AllZones.OfType<Zone_Stockpile>().Any(zone =>
                zone.GetStoreSettings()?.filter != null && DefDatabase<ThingDef>.AllDefsListForReading.Any(d => humanFood(d) && zone.GetStoreSettings().filter.Allows(d))
                && map.AllCells.Count(c => map.zoneManager.ZoneAt(c) == zone && c.Roofed(map) && c.GetRoom(map) != null
                    && c.GetRoom(map).ProperRoom && !c.GetRoom(map).PsychologicallyOutdoors) >= 9);
            var forbidden = things.Where(t => t.def.category == ThingCategory.Item && (t.Faction == null || t.Faction.IsPlayer)
                && (t.def.IsNutritionGivingIngestible || t.def.IsWeapon || t.def.IsMedicine || t.def.IsStuff || t.def.defName == "Silver")
                && t.IsForbidden(player) && t.Position.DistanceTo(center) <= 20 && reachable(t)).Select(t => t.Position).Distinct().OrderBy(c => c.z).ThenBy(c => c.x).ToList();
            Bound(forbidden.Count, limit);
            foreach (var cell in forbidden) result.ForbiddenSupplies.Add(Cell(cell));
            ReadProduction(result, map, people, things, reachable, humanFood, limit);
            var naming = ColonyNamingTools.Pending();
            if (naming != null) result.Naming = new Obs.ColonyNaming { WindowId = naming.ID };
            else if (Find.WindowStack == null || Find.WindowStack.Windows.OfType<Dialog_NamePlayerFactionAndSettlement>().Any())
                result.Issues.Add(Issue("naming", Common.UnavailableReason.Unsupported, "Naming window census is unavailable or obstructed by another paused dialog."));
            else result.Issues.Add(Issue("naming", Common.UnavailableReason.NotApplicable, "No pending colony naming dialog."));
            foreach (var field in new[] { "policy_resources", "pending_food_nutrition", "environment", "food_climate", "acquisition", "butchering", "food_corpses", "recovery", "waste" })
                result.Issues.Add(Issue(field, Common.UnavailableReason.Unsupported, "Section is not yet projected."));
            result.Planning = request.Planning ? new Obs.PlanningSection { Observed = Planning(map, center, request, context, limit) }
                : new Obs.PlanningSection { Unavailable = Unavailable(Common.UnavailableReason.NotRequested, "Planning was not requested.") };
            return result;
        }

        private static Obs.UpkeepSection ReadComfort(Map map)
        {
            var result = new Obs.UpkeepFacts { Completeness = Complete(1) };
            try { result.Comfort = new Obs.ComfortSection { Observed = ComfortFacts.ReadProtocol(map) }; }
            catch (Exception) { result.Comfort = new Obs.ComfortSection { Unavailable = Unsupported("Complete comfort facts are unavailable.") }; }
            NativeUpkeepFacts.Populate(map, result);
            foreach (var field in new[] { "construction", "beds", "storage_cells", "storage_capacity", "protected_cells", "people", "feed_definitions", "hauling", "wall_removal", "home_coverage" })
                result.Issues.Add(Issue(field, Common.UnavailableReason.Unsupported, "Upkeep section is not yet projected."));
            return new Obs.UpkeepSection { Observed = result };
        }

        private static Obs.DevelopmentFacts ReadPower(Map map, int limit)
        {
            var traders = map.listerBuildings.allBuildingsColonist.Select(b => b.TryGetComp<CompPowerTrader>())
                .Where(p => p != null).OrderBy(p => p.parent.thingIDNumber).ToList();
            Bound(traders.Count, limit);
            var result = new Obs.DevelopmentFacts { Completeness = Complete(traders.Count) };
            foreach (var power in traders) {
                var building = power.parent;
                var service = new Obs.BuildingServiceState { Connected = power.PowerNet != null, PowerOn = power.PowerOn,
                    PowerOutputW = Finite(power.PowerOutput), SwitchedOn = building.TryGetComp<CompFlickable>()?.SwitchIsOn ?? true };
                if (power.PowerNet != null) service.PowerNetId = power.PowerNet.GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture);
                result.Power.Add(new Obs.DevelopmentPower { BaseW = Finite(-power.Props.PowerConsumption),
                    Building = new Obs.BuildingState { Building = new Obs.EntityRef { Id = building.GetUniqueLoadID(), MapId = map.uniqueID },
                        Service = service, Settings = new Obs.BuildingSettings { Forbidden = building.IsForbidden(Faction.OfPlayer) } } });
            }
            return result;
        }

        private static void ReadProduction(Obs.ColonyFactsSnapshot result, Map map, List<Pawn> people,
            List<Thing> things, Func<Thing, bool> reachable, Func<ThingDef, bool> humanFood, int limit)
        {
            var farms = map.zoneManager.AllZones.OfType<Zone_Growing>().OrderBy(z => z.ID).ToList();
            Bound(farms.Count, limit);
            var cropField = BridgeCommon.PrivateInstanceField(typeof(Zone_Growing), "plantDefToGrow");
            if (farms.Count > 0 && cropField == null) throw new InvalidOperationException("Native growing crop schema unavailable.");
            foreach (var zone in farms) {
                var crop = cropField!.GetValue(zone) as ThingDef;
                if (crop?.plant == null) throw new InvalidOperationException("Native growing crop unavailable.");
                var cells = map.AllCells.Where(c => map.zoneManager.ZoneAt(c) == zone && !c.Fogged(map)).ToList();
                var plants = cells.Select(c => c.GetPlant(map)).Where(p => p != null && p.def == crop).ToList();
                var product = crop.plant.harvestedThingDef;
                var edible = product != null && humanFood(product);
                var row = new Obs.FarmFacts { ZoneId = zone.ID.ToString(System.Globalization.CultureInfo.InvariantCulture), Crop = crop.defName,
                    UsableCells = (uint)cells.Count(c => map.fertilityGrid.FertilityAt(c) >= crop.plant.fertilityMin),
                    PlantedCells = (uint)plants.Count, GrowingCells = (uint)plants.Count(p => p.GrowthRateFactor_Temperature > 0 && p.GrowthRateFactor_Fertility > 0),
                    EdibleCrop = edible, NutritionPerHarvestCell = edible ? Finite(crop.plant.harvestYield * product.GetStatValueAbstract(StatDefOf.Nutrition)) : 0 };
                if (plants.Count > 0) row.HarvestLowerBoundDays = Finite(plants.Min(p => (1f - p.Growth) * crop.plant.growDays / Math.Max(.01f, p.GrowthRateFactor_Fertility)));
                result.Farms.Add(row);
            }
            var benches = things.OfType<Building_WorkTable>().Where(b => b.Faction == Faction.OfPlayer && reachable(b)
                && b.def.AllRecipes.Any(r => r.products.Any(p => humanFood(p.thingDef)))).OrderBy(b => b.thingIDNumber).ToList();
            Bound(benches.Count, limit);
            foreach (var bench in benches) {
                var row = new Obs.CookingFacts { Bench = new Obs.EntityRef { Id = bench.GetUniqueLoadID(), DefName = bench.def.defName,
                    MapId = map.uniqueID, Position = Cell(bench.Position) },
                    Usable = !bench.IsBurning() && (bench.TryGetComp<CompPowerTrader>() == null || bench.TryGetComp<CompPowerTrader>().PowerOn)
                        && (bench.TryGetComp<CompRefuelable>() == null || bench.TryGetComp<CompRefuelable>().HasFuel) };
                var recipes = bench.def.AllRecipes.Where(r => r.products.Any(p => humanFood(p.thingDef))).OrderBy(r => r.defName).ToList();
                Bound(recipes.Count, limit); Bound(bench.BillStack.Bills.Count, limit);
                foreach (var recipe in recipes) row.Recipes.Add(new Obs.RecipeState { Recipe = new Obs.DefinitionRef { DefName = recipe.defName } });
                foreach (var bill in bench.BillStack.Bills) row.Bills.Add(new Obs.BillState { Recipe = new Obs.DefinitionRef { DefName = bill.recipe.defName }, Suspended = bill.suspended });
                result.Cooking.Add(row);
            }
        }

        private static Obs.PlanningFacts Planning(Map map, IntVec3 center, Obs.ColonyFactsRequest request, Common.ObservationContext context, int limit)
        {
            var names = request.RequestedDefinitionNames.Count == 0 ? StarterDefinitions : request.RequestedDefinitionNames.ToArray();
            Bound(names.Length, limit);
            var result = new Obs.PlanningFacts { Completeness = Complete(names.Length) };
            try { result.Gear = NativeGearFacts.Read(map, context, limit); }
            catch (Exception) { result.Issues.Add(Issue("gear", Common.UnavailableReason.ReadFailed, "Complete native loadout upkeep is unavailable.")); }
            var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead).ToList();
            var demand = people.Sum(p => p.needs?.food == null ? 0f : p.needs.food.FoodFallPerTickAssumingCategory(HungerCategory.Fed, true) * 60000f);
            var animals = map.mapPawns.AllPawnsSpawned.Where(p => !p.Dead && p.RaceProps.Animal
                && p.Faction == Faction.OfPlayerSilentFail && p.needs?.food != null).ToList();
            Bound(animals.Count, limit);
            foreach (var name in names.OrderBy(n => n, StringComparer.Ordinal)) {
                var row = new Obs.PlanningDefinition { Definition = new Obs.DefinitionRef { DefName = name } };
                result.Definitions.Add(row);
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(name);
                if (def == null) {
                    row.Available = false;
                    row.Issues.Add(Issue("costs", Common.UnavailableReason.NotApplicable, "Native definition does not exist."));
                    row.Issues.Add(Issue("size", Common.UnavailableReason.NotApplicable, "Native definition does not exist."));
                    continue;
                }
                row.Definition.Label = PlacementPreviewOperation.Diagnostic(def.label);
                row.Available = def.researchPrerequisites == null || def.researchPrerequisites.All(r => r.IsFinished);
                row.ResearchPrerequisites.Add((def.researchPrerequisites ?? new List<ResearchProjectDef>()).Select(r => r.defName));
                row.ConstructionSkill = def.constructionSkillPrerequisite;
                row.Size = new Obs.MapSize { Width = (uint)def.size.x, Height = (uint)def.size.z };
                var wood = DefDatabase<ThingDef>.GetNamedSilentFail("WoodLog");
                var stuff = def.MadeFromStuff ? wood : null;
                if (def.MadeFromStuff && (stuff == null || !GenStuff.AllowedStuffsFor(def).Contains(stuff))) {
                    row.Issues.Add(Issue("costs", Common.UnavailableReason.NotApplicable, "Starter wood is not a native allowed material."));
                } else {
                    if (stuff != null) row.Stuff = stuff.defName;
                    var costs = def.CostListAdjusted(stuff, false); Bound(costs.Count, 256);
                    foreach (var cost in costs) row.Costs.Add(new Obs.Quantity { DefName = cost.thingDef.defName, Units = cost.count });
                    if (def.building?.bed_humanlike == true) row.RestEffectiveness = Finite(def.GetStatValueAbstract(StatDefOf.BedRestEffectiveness, stuff));
                }
                if (def.plant != null) {
                    row.GrowDays = Finite(def.plant.growDays); row.FertilityMin = Finite(def.plant.fertilityMin); row.FertilitySensitivity = Finite(def.plant.fertilitySensitivity);
                    var product = def.plant.harvestedThingDef;
                    if (product != null) {
                        row.HarvestNutrition = Finite(def.plant.harvestYield * product.GetStatValueAbstract(StatDefOf.Nutrition));
                        row.NutritionDemandPerDay = Finite(demand + animals.Where(p => p.RaceProps.CanEverEat(product)
                            && p.foodRestriction?.GetCurrentRespectedRestriction(p)?.filter.Allows(product) != false)
                            .Sum(p => p.needs.food.FoodFallPerTickAssumingCategory(HungerCategory.Fed, true) * 60000f));
                    }
                }
            }
            var min = new IntVec3(Math.Max(0, center.x - 22), 0, Math.Max(0, center.z - 22));
            var max = new IntVec3(Math.Min(map.Size.x - 1, center.x + 22), 0, Math.Min(map.Size.z - 1, center.z + 22));
            var cells = new Obs.CellsSnapshot { Context = context, MapSize = Size(map), Region = new Obs.Rectangle { Minimum = Cell(min), Maximum = Cell(max) },
                AppliedFields = new Obs.CellFields { Terrain = true, Roof = true, Visibility = true, Traversal = true, Zone = true, Room = true, Growth = true } };
            int fogged = 0;
            for (int z = min.z; z <= max.z; z++) for (int x = min.x; x <= max.x; x++) {
                var c = new IntVec3(x, 0, z);
                if (c.Fogged(map)) { fogged++; continue; }
                var terrain = c.GetTerrain(map); var room = c.GetRoom(map); var zone = map.zoneManager.ZoneAt(c); var roof = c.GetRoof(map);
                var row = new Obs.CellState { Cell = Cell(c), Terrain = terrain.defName, Fogged = false, Walkable = c.Walkable(map), Passable = !c.Impassable(map),
                    Fertility = Finite(map.fertilityGrid.FertilityAt(c)), SupportsLight = terrain.affordances.Contains(TerrainAffordanceDefOf.Light),
                    Occupied = c.GetEdifice(map) != null || c.GetThingList(map).Any(t => t is Blueprint || t is Frame),
                    Indoors = room != null && room.ProperRoom && !room.PsychologicallyOutdoors,
                    StorageEmpty = !c.GetThingList(map).Any(t => t is Plant || t is Building || t is Blueprint || t is Frame || t.def.category == ThingCategory.Item) };
                if (roof != null) row.Roof = roof.defName; else row.Issues.Add(Issue("roof", Common.UnavailableReason.NotApplicable, "No roof."));
                if (zone != null) row.ZoneId = zone.ID.ToString(System.Globalization.CultureInfo.InvariantCulture); else row.Issues.Add(Issue("zone_id", Common.UnavailableReason.NotApplicable, "No zone."));
                if (room != null) { row.RoomId = room.ID.ToString(System.Globalization.CultureInfo.InvariantCulture); row.TemperatureC = Finite(room.Temperature); }
                else row.Issues.Add(Issue("room_id", Common.UnavailableReason.NotApplicable, "No room."));
                cells.Cells.Add(row);
            }
            cells.Completeness = Complete(cells.Cells.Count, fogged);
            result.Cells = cells;
            return result;
        }
        private static Common.Cell Cell(IntVec3 c) => new Common.Cell { X = c.x, Z = c.z };
        private static Obs.MapSize Size(Map map) => new Obs.MapSize { Width = (uint)map.Size.x, Height = (uint)map.Size.z };
        private static double Finite(double v) => double.IsNaN(v) || double.IsInfinity(v) ? throw new InvalidOperationException("Nonfinite fact.") : v;
        private static void Bound(int count, int limit) { if (count > limit) throw new ReadLimit("Complete collection exceeds requested bound; frozen paging is unavailable."); }
        private static Obs.ForecastFacts Forecast(ForecastFacts.Snapshot source)
        {
            Bound(source.animalIds.Count, 256); Bound(source.crops.Count, 256); Bound(source.patients.Count, 256);
            var result = new Obs.ForecastFacts { CombinedFoodSupply = Food(source.combinedFoodSupply),
                Completeness = Complete(1 + source.animalIds.Count + source.crops.Count + source.patients.Count) };
            result.AnimalIds.Add(source.animalIds);
            foreach (var crop in source.crops) {
                var row = new Obs.CropForecast { ZoneId = crop.id.ToString(System.Globalization.CultureInfo.InvariantCulture) };
                if (crop.crop != null) row.Crop = crop.crop;
                if (crop.sowWork.HasValue) row.SowWork = Finite(crop.sowWork.Value);
                if (crop.harvestWork.HasValue) row.HarvestWork = Finite(crop.harvestWork.Value);
                if (crop.maturePlants.HasValue) row.MaturePlants = checked((uint)crop.maturePlants.Value);
                if (crop.stalledPlants.HasValue) row.StalledPlants = checked((uint)crop.stalledPlants.Value);
                if (crop.standingYield.HasValue) row.StandingYield = Finite(crop.standingYield.Value);
                if (crop.product != null) row.Product = crop.product;
                result.Crops.Add(row);
            }
            foreach (var patient in source.patients) {
                var row = new Obs.PatientForecast { PawnId = patient.id };
                if (patient.bleedRatePerDay.HasValue) row.BleedRatePerDay = Finite(patient.bleedRatePerDay.Value);
                if (patient.hoursUntilDeathFromBloodLoss.HasValue) row.HoursUntilDeathFromBloodLoss = Finite(patient.hoursUntilDeathFromBloodLoss.Value);
                if (patient.mood.HasValue) row.Mood = Finite(patient.mood.Value);
                if (patient.moodTarget.HasValue) row.MoodTarget = Finite(patient.moodTarget.Value);
                if (patient.minorBreakThreshold.HasValue) row.MinorBreakThreshold = Finite(patient.minorBreakThreshold.Value);
                if (patient.majorBreakThreshold.HasValue) row.MajorBreakThreshold = Finite(patient.majorBreakThreshold.Value);
                if (patient.extremeBreakThreshold.HasValue) row.ExtremeBreakThreshold = Finite(patient.extremeBreakThreshold.Value);
                result.Patients.Add(row);
            }
            return result;
        }
        private static Obs.FoodSupplyFacts Food(FoodSupplyFacts.Snapshot source)
        {
            Bound(source.consumers.Count, 256); Bound(source.stocks.Count, 4096);
            var result = new Obs.FoodSupplyFacts { Completeness = Complete(source.consumers.Count + source.stocks.Count) };
            foreach (var consumer in source.consumers)
                result.Consumers.Add(new Obs.FoodConsumer { PawnId = consumer.id, NutritionPerDay = Finite(consumer.nutritionPerDay) });
            foreach (var stock in source.stocks) {
                var row = new Obs.FoodStock { Item = new Obs.EntityRef { Id = stock.id, DefName = stock.defName },
                    Count = stock.count, Nutrition = Finite(stock.nutrition), Perishable = stock.perishable,
                    TemperatureC = Finite(stock.temperature) };
                row.EaterIds.Add(stock.eaters);
                if (stock.holder != null) row.HolderId = stock.holder;
                if (stock.rotTicks.HasValue) row.RotTicks = stock.rotTicks.Value;
                if (stock.roofed.HasValue) row.Roofed = stock.roofed.Value;
                result.Stocks.Add(row);
            }
            return result;
        }
        private static Obs.Completeness Complete(int count, int filtered = 0) => new Obs.Completeness { Page = new Common.PageInfo { Complete = true }, Matched = (ulong)count, Returned = (ulong)count, Filtered = (ulong)filtered, Unreadable = 0 };
        private static Common.Unavailable Unavailable(Common.UnavailableReason reason, string detail) => new Common.Unavailable { Reason = reason, Detail = detail };
        private static Common.Unavailable Unsupported(string detail) => Unavailable(Common.UnavailableReason.Unsupported, detail);
        private static Obs.ReadIssue Issue(string field, Common.UnavailableReason reason, string detail) => new Obs.ReadIssue { Field = field, Unavailable = Unavailable(reason, detail) };
        private sealed class ReadLimit : Exception { internal ReadLimit(string message) : base(message) {} }
    }
}
