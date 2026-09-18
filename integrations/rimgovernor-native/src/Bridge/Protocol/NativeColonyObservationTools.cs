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
            "PassiveCooler", "Cooler", "WoodFiredGenerator", "SolarGenerator", "ChemfuelPoweredGenerator", "Battery", "PowerConduit", "Sandbags", "Barricade",
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
            return await ProtoBoundary.OnMainThread(ctx, () => {
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out failure))
                    return ProtoBoundary.Encode(new Obs.ColonyFactsReply { Failure = failure });
                try {
                    var reply = new Obs.ColonyFactsReply { Observed = Read(map, parsed, context) };
                    if (Encoding.UTF8.GetByteCount(ProtoBoundary.Format(reply, compact: true)) > ProtoBoundary.MaximumEnvelopeBytes) throw new ReadLimit("Colony facts exceed1MiB; largest sections (wire bytes): " + LargestSections(reply.Observed) + ".");
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
                PlayerTechLevel = player.def.techLevel.ToString(),
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
            var forbidden = StartingSupplyFacts.Forbidden(things, center, reachable);
            Bound(forbidden.Count, limit);
            foreach (var t in forbidden)
                result.ForbiddenSupplies.Add(new Obs.EntityRef { Id = t.GetUniqueLoadID(), DefName = t.def.defName, MapId = map.uniqueID, Position = Cell(t.Position) });
            ReadProduction(result, map, people, things, reachable, humanFood, limit);
            var naming = ColonyNamingTools.Pending();
            if (naming != null) result.Naming = new Obs.ColonyNaming { WindowId = naming.ID,
                FactionName = ColonyNamingTools.Name(naming, "curName"), SettlementName = ColonyNamingTools.Name(naming, "curSecondName") };
            else if (Find.WindowStack == null || Find.WindowStack.Windows.OfType<Dialog_NamePlayerFactionAndSettlement>().Any())
                result.Issues.Add(Issue("naming", Common.UnavailableReason.Unsupported, "Naming window census is unavailable or obstructed by another paused dialog."));
            else result.Issues.Add(Issue("naming", Common.UnavailableReason.NotApplicable, "No pending colony naming dialog."));
            var dialog = ChoiceDialogTools.Pending();
            if (dialog != null) result.Dialog = ChoiceDialogTools.Snapshot(dialog);
            var conditions = new List<GameCondition>();
            map.gameConditionManager.GetAllGameConditionsAffectingMap(map, conditions);
            Bound(conditions.Count, limit);
            foreach (var condition in conditions)
                result.Environment.Add(new Obs.EnvironmentCondition { Id = condition.uniqueID.ToString(System.Globalization.CultureInfo.InvariantCulture), DefName = condition.def.defName });
            result.Recovery = NativeRecoveryFacts.Read(map, context, limit);
            foreach (var field in new[] { "policy_resources", "food_corpses", "waste" })
                result.Issues.Add(Issue(field, Common.UnavailableReason.Unsupported, "Section is not yet projected."));
            try { result.FoodClimate = new Obs.FoodClimate { GrowingDaysRemaining = ColonyFactsTools.GrowingDaysRemaining(map),
                SowingNow = new[] { "Plant_Rice", "Plant_Potato", "Plant_Corn" }.Select(DefDatabase<ThingDef>.GetNamedSilentFail).Any(d => d != null && PlantUtility.GrowthSeasonNow(map,d)),
                GrowingDays = GenTemperature.TwelfthsInAverageTemperatureRange(map.Tile,Plant.DefaultMinOptimalGrowthTemperature,Plant.DefaultMaxOptimalGrowthTemperature).Count * GenDate.DaysPerTwelfth }; }
            catch (Exception) { result.Issues.Add(Issue("food_climate", Common.UnavailableReason.ReadFailed, "Seasonal crop budget unavailable.")); }
            try { NativePlantAcquisition.Read(result, map, center, humanFood, limit); }
            catch (Exception) {
                result.Acquisition.Clear(); result.ClearPendingFoodNutrition(); result.ClearPendingWoodUnits(); result.ClearPendingHunts();
                foreach (var field in new[] { "acquisition", "pending_food_nutrition", "pending_wood_units", "pending_hunts" })
                    result.Issues.Add(Issue(field, Common.UnavailableReason.ReadFailed, "Complete safe acquisition facts are unavailable."));
            }
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
            foreach (var field in new[] { "construction", "storage_cells", "storage_capacity", "protected_cells", "feed_definitions", "hauling", "wall_removal" })
                result.Issues.Add(Issue(field, Common.UnavailableReason.Unsupported, "Upkeep section is not yet projected."));
            return new Obs.UpkeepSection { Observed = result };
        }

        private static Obs.DevelopmentFacts ReadPower(Map map, int limit)
        {
            // Traders (consumers and generators) and batteries share one census so
            // Go's power topology sees every network member that matters for
            // coverage and reserve: batteries carry base_w = 0 plus stored/capacity
            // energy, generators carry their refuelable and breakdown service facts.
            var traders = map.listerBuildings.allBuildingsColonist.Select(b => b.TryGetComp<CompPowerTrader>())
                .Where(p => p != null).OrderBy(p => p.parent.thingIDNumber).ToList();
            var batteries = map.listerBuildings.allBuildingsColonist.Select(b => b.TryGetComp<CompPowerBattery>())
                .Where(p => p != null).OrderBy(p => p.parent.thingIDNumber).ToList();
            var conduits = map.listerBuildings.allBuildingsColonist.Where(b => b.def.defName == "PowerConduit")
                .OrderBy(b => b.thingIDNumber).ToList();
            var nets = map.powerNetManager.AllNetsListForReading.OrderBy(n => n.GetHashCode()).ToList();
            Bound(traders.Count + batteries.Count + conduits.Count + nets.Count, limit);
            var result = new Obs.DevelopmentFacts { Completeness = Complete(traders.Count + batteries.Count + conduits.Count) };
            foreach (var power in traders) {
                var building = (Building)power.parent;
                var service = new Obs.BuildingServiceState { Connected = power.PowerNet != null, PowerOn = power.PowerOn,
                    PowerOutputW = Finite(power.PowerOutput), SwitchedOn = building.TryGetComp<CompFlickable>()?.SwitchIsOn ?? true };
                if (power.PowerNet != null) service.PowerNetId = NetId(power.PowerNet);
                Service(building, service);
                result.Power.Add(new Obs.DevelopmentPower { BaseW = Finite(-power.Props.PowerConsumption), Building = PowerState(map, building, service) });
            }
            foreach (var battery in batteries) {
                var building = (Building)battery.parent;
                var service = new Obs.BuildingServiceState { Connected = battery.PowerNet != null, PowerOn = battery.PowerNet != null,
                    PowerOutputW = 0, SwitchedOn = building.TryGetComp<CompFlickable>()?.SwitchIsOn ?? true };
                if (battery.PowerNet != null) service.PowerNetId = NetId(battery.PowerNet);
                Service(building, service);
                result.Power.Add(new Obs.DevelopmentPower { BaseW = 0, Building = PowerState(map, building, service),
                    StoredWattDays = Finite(battery.StoredEnergy), CapacityWattDays = Finite(battery.Props.storedEnergyMax) });
            }
            foreach (var conduit in conduits)
                result.Furniture.Add(new Obs.DevelopmentFurniture { Building = new Obs.EntityRef { Id = conduit.GetUniqueLoadID(),
                    MapId = map.uniqueID, DefName = conduit.def.defName, Position = Cell(conduit.Position) } });
            foreach (var net in nets) {
                var generation = net.powerComps.Where(p => p.PowerOn && p.PowerOutput > 0).Sum(p => (double)p.PowerOutput);
                var consumption = net.powerComps.Where(p => p.PowerOn && p.PowerOutput < 0).Sum(p => (double)-p.PowerOutput);
                result.Networks.Add(new Obs.PowerNetwork { Id = NetId(net),
                    Producers = (uint)net.powerComps.Count(p => p.Props.PowerConsumption < 0),
                    Consumers = (uint)net.powerComps.Count(p => p.Props.PowerConsumption > 0),
                    Batteries = (uint)net.batteryComps.Count, Transmitters = (uint)net.transmitters.Count, Connectors = (uint)net.connectors.Count,
                    GenerationW = Finite(generation), ConsumptionW = Finite(consumption), NetW = Finite(generation - consumption),
                    StoredWattDays = Finite(net.CurrentStoredEnergy()),
                    CapacityWattDays = Finite(net.batteryComps.Sum(b => (double)b.Props.storedEnergyMax)),
                    HasSource = net.powerComps.Any(p => p.Props.PowerConsumption < 0),
                    HasActiveSource = net.powerComps.Any(p => p.PowerOn && p.PowerOutput > 0),
                    Completeness = Complete(net.powerComps.Count + net.batteryComps.Count) });
            }
            return result;
        }

        private static string NetId(PowerNet net) => net.GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Refuelable and breakdown service facts mirror NativeRecoveryFacts so the
        // power planner can distinguish "waiting for ordinary refueling/repair"
        // from "no producer" without a second census.
        private static void Service(Building building, Obs.BuildingServiceState service)
        {
            service.BrokenDown = building.TryGetComp<CompBreakdownable>()?.BrokenDown ?? false;
            var fuel = building.TryGetComp<CompRefuelable>();
            if (fuel == null) return;
            service.Fuel = Finite(fuel.Fuel); service.TargetFuel = Finite(fuel.TargetFuelLevel); service.OutOfFuel = !fuel.HasFuel;
            var defs = fuel.Props.fuelFilter.AllowedThingDefs.Select(d => d.defName).OrderBy(d => d, StringComparer.Ordinal).ToList();
            Bound(defs.Count, 256);
            service.AllowedFuelDefs.Add(defs);
        }

        private static Obs.BuildingState PowerState(Map map, Building building, Obs.BuildingServiceState service)
        {
            var state = new Obs.BuildingState { Building = new Obs.EntityRef { Id = building.GetUniqueLoadID(), MapId = map.uniqueID,
                    DefName = building.def.defName, Position = Cell(building.Position) },
                Service = service, Settings = new Obs.BuildingSettings { Forbidden = building.IsForbidden(Faction.OfPlayer) } };
            var occupied = building.OccupiedRect().Cells.ToList();
            Bound(occupied.Count, 4096);
            foreach (var cell in occupied) state.OccupiedCells.Add(Cell(cell));
            return state;
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
                    MapId = map.uniqueID, Position = Cell(bench.Position), Snapshot = NativeProductionBills.Snapshot(bench,bench,result.Context) },
                    Usable = !bench.IsBurning() && (bench.TryGetComp<CompPowerTrader>() == null || bench.TryGetComp<CompPowerTrader>().PowerOn)
                        && (bench.TryGetComp<CompRefuelable>() == null || bench.TryGetComp<CompRefuelable>().HasFuel) };
                var recipes = bench.def.AllRecipes.Where(r => r.products.Any(p => humanFood(p.thingDef))).OrderBy(r => r.defName).ToList();
                Bound(recipes.Count, limit); Bound(bench.BillStack.Bills.Count, limit);
                foreach (var recipe in recipes) {
                    row.Recipes.Add(NativeProductionBills.RecipeRow(bench,recipe));
                    var production = new Obs.FoodProduction { Recipe = recipe.defName, Available = NativeProductionBills.Recipe(bench,recipe) };
                    foreach(var product in recipe.products){
                        var rot=product.thingDef.GetCompProperties<CompProperties_Rottable>();
                        var food=new Obs.FoodProduct{DefName=product.thingDef.defName,Count=product.count,Edible=humanFood(product.thingDef),Nutrition=product.thingDef.GetStatValueAbstract(StatDefOf.Nutrition),NutritionDemandPerDay=result.NutritionPerDay,Perishable=rot!=null};
                        if(rot!=null)food.RotDays=rot.daysToRotStart;
                        production.Products.Add(food);
                    }
                    row.Production.Add(production);
                }
                for(var index=0;index<bench.BillStack.Count;index++)row.Bills.Add(NativeProductionBills.BillRow(bench.BillStack.Bills[index],index));
                var cookingRoom = bench.GetRoom();
                if (cookingRoom != null) row.RoomId = cookingRoom.ID.ToString(System.Globalization.CultureInfo.InvariantCulture);
                result.Cooking.Add(row);
            }
            foreach(var bench in things.Where(t=>t.Faction==Faction.OfPlayer&&t is IBillGiver&&reachable(t)&&t.def.AllRecipes.Any(r=>r.defName=="ButcherCorpseFlesh")).OrderBy(t=>t.thingIDNumber)){
                if(result.Butchering.Count>=limit)throw new ReadLimit("Butcher census bound.");var giver=(IBillGiver)bench;
                var row=new Obs.ButcheringFacts{Bench=new Obs.EntityRef{Id=bench.GetUniqueLoadID(),DefName=bench.def.defName,MapId=map.uniqueID,Position=Cell(bench.Position),Snapshot=NativeProductionBills.Snapshot(bench,giver,result.Context)},Usable=NativeProductionBills.Usable(bench)};
                foreach(var recipe in bench.def.AllRecipes.Where(r=>r.defName=="ButcherCorpseFlesh"))row.Recipes.Add(NativeProductionBills.RecipeRow(bench,recipe));
                for(var index=0;index<giver.BillStack.Count;index++)row.Bills.Add(NativeProductionBills.BillRow(giver.BillStack.Bills[index],index));
                var butcherRoom = bench.GetRoom();
                if (butcherRoom != null) row.RoomId = butcherRoom.ID.ToString(System.Globalization.CultureInfo.InvariantCulture);
                result.Butchering.Add(row);
            }

        }

        private static Obs.PlanningFacts Planning(Map map, IntVec3 center, Obs.ColonyFactsRequest request, Common.ObservationContext context, int limit)
        {
            var names = request.RequestedDefinitionNames.Count == 0 ? StarterDefinitions : request.RequestedDefinitionNames.ToArray();
            Bound(names.Length, limit);
            var result = new Obs.PlanningFacts { Completeness = Complete(names.Length) };
            try { result.ZoneMapSnapshot = NativeZoneCreation.MapSnapshot(map,context); }
            catch (Exception) { result.Issues.Add(Issue("zone_map_snapshot", Common.UnavailableReason.ReadFailed, "Zone occupancy snapshot unavailable.")); }
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
                if (def == null && Terrain(row, DefDatabase<TerrainDef>.GetNamedSilentFail(name))) continue;
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
                row.NeedsPower = def.GetCompProperties<CompProperties_Power>()?.PowerConsumption > 0;
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
                var powerProps = def.GetCompProperties<CompProperties_Power>();
                if (powerProps != null) row.PowerW = Finite(powerProps.PowerConsumption);
                var glowProps = def.GetCompProperties<CompProperties_Glower>();
                if (glowProps != null) row.GlowRadius = Finite(glowProps.glowRadius);
                if (def.building?.sowTag != null) { row.SowTag = def.building.sowTag; if (def.fertility >= 0f) row.GrowerFertility = Finite(def.fertility); }
                if (def.plant != null) {
                    row.GrowDays = Finite(def.plant.growDays); row.FertilityMin = Finite(def.plant.fertilityMin); row.FertilitySensitivity = Finite(def.plant.fertilitySensitivity);
                    row.GrowMinGlow = Finite(def.plant.growMinGlow); row.SowTags.Add(def.plant.sowTags ?? new List<string>());
                    var product = def.plant.harvestedThingDef;
                    if (product != null) {
                        row.Edible = product.IsNutritionGivingIngestible && !product.IsDrug;
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
                    Doorway = c.GetDoor(map) != null || c.GetThingList(map).Any(t => (t is Blueprint || t is Frame)
                        && t.def.entityDefToBuild is ThingDef built && typeof(Building_Door).IsAssignableFrom(built.thingClass)),
                    Indoors = room != null && room.ProperRoom && !room.PsychologicallyOutdoors,
                    StorageEmpty = !c.GetThingList(map).Any(t => t is Plant || t is Building || t is Blueprint || t is Frame || t.def.category == ThingCategory.Item) };
                // Absent roof/zone/room are expressed by the applied field being
                // set with no value: AppliedFields declares Roof/Zone/Room were
                // read, so a missing value is a known absence, not an unread
                // field. Per-cell "not applicable" issue rows said the same
                // thing at ~290 JSON bytes per cell, which put a 45x45 planning
                // window alone at the 1 MiB envelope bound on ordinary maps
                // (issue #2: the routine review then failed every step once a
                // few fogged cells were revealed, holding the clock forever).
                if (roof != null) row.Roof = roof.defName;
                if (zone != null) row.ZoneId = zone.ID.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (room != null) { row.RoomId = room.ID.ToString(System.Globalization.CultureInfo.InvariantCulture); row.TemperatureC = Finite(room.Temperature); }
                cells.Cells.Add(row);
            }
            cells.Completeness = Complete(cells.Cells.Count, fogged);
            result.Cells = cells;
            try { result.Environment = Environment(map, min, max, limit); }
            catch (ReadLimit) { throw; }
            catch (Exception) { result.Issues.Add(Issue("environment", Common.UnavailableReason.ReadFailed, "Controlled-environment growing facts are unavailable.")); }
            return result;
        }
        // A floor definition: research availability, its cost list and the
        // abstract stats a laid floor carries (MaintainFlooring scores these).
        // TerrainDefs are never made from stuff.
        private static bool Terrain(Obs.PlanningDefinition row, TerrainDef? def)
        {
            if (def == null) return false;
            row.Definition.Label = PlacementPreviewOperation.Diagnostic(def.label);
            row.Terrain = true;
            row.Available = def.BuildableByPlayer && (def.researchPrerequisites == null || def.researchPrerequisites.All(r => r.IsFinished));
            row.ResearchPrerequisites.Add((def.researchPrerequisites ?? new List<ResearchProjectDef>()).Select(r => r.defName));
            row.ConstructionSkill = def.constructionSkillPrerequisite;
            row.Size = new Obs.MapSize { Width = 1, Height = 1 };
            var costs = def.CostListAdjusted(null, false); Bound(costs.Count, 256);
            foreach (var cost in costs) row.Costs.Add(new Obs.Quantity { DefName = cost.thingDef.defName, Units = cost.count });
            row.Cleanliness = Finite(def.GetStatValueAbstract(StatDefOf.Cleanliness));
            row.PathCost = def.pathCost;
            row.Beauty = Finite(def.GetStatValueAbstract(StatDefOf.Beauty));
            row.Flammability = Finite(def.GetStatValueAbstract(StatDefOf.Flammability));
            return true;
        }
        // Sun lamps, plant growers and rooms inside the planning region plus every
        // power network's headroom split by source. Lamp growth cells are the
        // native Building_SunLamp radius, not the glow radius, so the controller
        // never plants where the game would not grow.
        // The native sun lamp class is internal; its def names the class and carries the growth radius as specialDisplayRadius.
        private static bool IsSunLamp(Building b) => b.def.thingClass?.Name == "Building_SunLamp" && b.def.specialDisplayRadius > 0f;
        private static Obs.ControlledEnvironment Environment(Map map, IntVec3 min, IntVec3 max, int limit)
        {
            var result = new Obs.ControlledEnvironment { OutdoorTemperatureC = Finite(map.mapTemperature.OutdoorTemp), Daylight = GenCelestial.CurCelestialSunGlow(map) >= 0.3f };
            bool Inside(IntVec3 c) => c.x >= min.x && c.x <= max.x && c.z >= min.z && c.z <= max.z;
            string? NetId(CompPowerTrader? power) => power?.PowerNet == null ? null : power.PowerNet.GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var rooms = new Dictionary<int, Room>();
            void Note(Room? room) { if (room != null && !room.PsychologicallyOutdoors && room.ProperRoom) rooms[room.ID] = room; }
            var buildings = map.listerBuildings.allBuildingsColonist.Where(b => Inside(b.Position)).OrderBy(b => b.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
            Bound(buildings.Count(b => IsSunLamp(b) || b is Building_PlantGrower), limit);
            foreach (var building in buildings) {
                var power = building.TryGetComp<CompPowerTrader>();
                var room = building.GetRoom();
                if (IsSunLamp(building)) { var lamp = building;
                    var row = new Obs.GrowLight { Building = new Obs.EntityRef { Id = lamp.GetUniqueLoadID(), MapId = map.uniqueID, DefName = lamp.def.defName, Position = Cell(lamp.Position) } };
                    if (power != null) { row.Powered = power.PowerOn; row.PowerW = Finite(power.Props.PowerConsumption); var id = NetId(power); if (id != null) row.PowerNetId = id; }
                    else row.Issues.Add(Issue("powered", Common.UnavailableReason.NotApplicable, "Lamp has no power trader."));
                    var schedule = lamp.TryGetComp<CompSchedule>();
                    row.LitNow = (power == null || power.PowerOn) && (schedule == null || schedule.Allowed);
                    foreach (var c in GenRadial.RadialCellsAround(lamp.Position, lamp.def.specialDisplayRadius, true).Where(c => c.InBounds(map))) row.GrowthCells.Add(Cell(c));
                    if (room != null) { row.RoomId = room.ID.ToString(System.Globalization.CultureInfo.InvariantCulture); Note(room); }
                    result.Lights.Add(row);
                } else if (building is Building_PlantGrower grower) {
                    var row = new Obs.PlantGrower { Building = new Obs.EntityRef { Id = grower.GetUniqueLoadID(), MapId = map.uniqueID, DefName = grower.def.defName, Position = Cell(grower.Position) },
                        CanSow = grower.CanAcceptSowNow() };
                    if (grower.def.fertility >= 0f) row.Fertility = Finite(grower.def.fertility);
                    if (grower.def.building?.sowTag != null) row.SowTag = grower.def.building.sowTag;
                    var crop = grower.GetPlantDefToGrow();
                    if (crop != null) row.CropDefName = crop.defName;
                    if (power != null) { row.Powered = power.PowerOn; row.PowerW = Finite(power.Props.PowerConsumption); var id = NetId(power); if (id != null) row.PowerNetId = id; }
                    foreach (var c in ((IPlantToGrowSettable)grower).Cells) row.PlantCells.Add(Cell(c));
                    if (room != null) { row.RoomId = room.ID.ToString(System.Globalization.CultureInfo.InvariantCulture); Note(room); }
                    result.Growers.Add(row);
                }
            }
            for (int z = min.z; z <= max.z; z++) for (int x = min.x; x <= max.x; x++) {
                var c = new IntVec3(x, 0, z);
                if (!c.Fogged(map)) Note(c.GetRoom(map));
            }
            Bound(rooms.Count, limit);
            foreach (var room in rooms.Values.OrderBy(r => r.ID)) {
                var row = new Obs.GrowRoom { RoomId = room.ID.ToString(System.Globalization.CultureInfo.InvariantCulture), TemperatureC = Finite(room.Temperature), CellCount = (uint)room.CellCount,
                    OpenRoofCount = (uint)room.OpenRoofCount, ProperRoom = room.ProperRoom, PsychologicallyOutdoors = room.PsychologicallyOutdoors };
                uint lit = 0;
                foreach (var c in room.Cells) if (map.glowGrid.GroundGlowAt(c) >= 0.3f) lit++;
                row.LitCells = lit;
                result.Rooms.Add(row);
            }
            var nets = map.powerNetManager?.AllNetsListForReading ?? new List<PowerNet>();
            Bound(nets.Count, limit);
            foreach (var net in nets.OrderBy(n => n.GetHashCode())) {
                var row = new Obs.PowerHeadroom { Id = net.GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture), HasActiveSource = net.HasActivePowerSource };
                double generation = 0, solar = 0, wind = 0, consumption = 0;
                foreach (var trader in net.powerComps ?? new List<CompPowerTrader>()) {
                    if (trader == null) continue;
                    var output = Finite(trader.PowerOutput);
                    if (trader.Props != null && trader.Props.PowerConsumption < 0f) {
                        if (output <= 0) continue;
                        generation += output;
                        if (trader.parent.TryGetComp<CompPowerPlantSolar>() != null) solar += output;
                        else if (trader.parent.TryGetComp<CompPowerPlantWind>() != null) wind += output;
                    } else if (output < 0) consumption -= output;
                }
                double capacity = 0;
                foreach (var battery in net.batteryComps ?? new List<CompPowerBattery>()) if (battery?.Props != null) capacity += Finite(battery.Props.storedEnergyMax);
                row.GenerationW = generation; row.SolarW = solar; row.WindW = wind; row.ConsumptionW = consumption;
                row.StoredWattDays = Finite(net.CurrentStoredEnergy()); row.CapacityWattDays = capacity;
                result.Networks.Add(row);
            }
            result.Completeness = Complete(result.Lights.Count + result.Growers.Count + result.Rooms.Count + result.Networks.Count);
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
                if (stock.roomId != null) row.RoomId = stock.roomId;
                result.Stocks.Add(row);
            }
            return result;
        }
        private static Obs.Completeness Complete(int count, int filtered = 0) => new Obs.Completeness { Page = new Common.PageInfo { Complete = true }, Matched = (ulong)count, Returned = (ulong)count, Filtered = (ulong)filtered, Unreadable = 0 };
        private static Common.Unavailable Unavailable(Common.UnavailableReason reason, string detail) => new Common.Unavailable { Reason = reason, Detail = detail };
        private static Common.Unavailable Unsupported(string detail) => Unavailable(Common.UnavailableReason.Unsupported, detail);
        private static Obs.ReadIssue Issue(string field, Common.UnavailableReason reason, string detail) => new Obs.ReadIssue { Field = field, Unavailable = Unavailable(reason, detail) };
        // Names the sections that dominate an oversized reply so a limit
        // failure says what to bound instead of only that the bound broke.
        internal static string LargestSections(IMessage message, string prefix = "", int depth = 0)
        {
            var sizes = new List<KeyValuePair<string, int>>();
            foreach (var field in message.Descriptor.Fields.InDeclarationOrder()) {
                var value = field.Accessor.GetValue(message);
                var size = 0;
                if (value is IMessage nested) size = nested.CalculateSize();
                else if (value is System.Collections.IEnumerable items && !(value is string)) foreach (var item in items) size += item is IMessage m ? m.CalculateSize() : 8;
                else continue;
                if (size > 0) sizes.Add(new KeyValuePair<string, int>(prefix + field.Name, size));
            }
            var parts = new List<string>();
            foreach (var pair in sizes.OrderByDescending(pair => pair.Value).Take(4)) {
                parts.Add(pair.Key + "=" + pair.Value);
                if (depth < 2 && message.Descriptor.FindFieldByName(pair.Key.Substring(prefix.Length))?.Accessor.GetValue(message) is IMessage nested && pair.Value > 65536)
                    parts.Add(LargestSections(nested, pair.Key + ".", depth + 1));
            }
            return string.Join(" ", parts);
        }

        private sealed class ReadLimit : Exception { internal ReadLimit(string message) : base(message) {} }
    }
}
