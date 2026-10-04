#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
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
        internal const string ToolName = "rimgovernor/observations_read_colony_facts";

        [Tool(ToolName, Title = "Read typed routine colony facts", Description = "Native colony, accessible stock, sleeping capacity, temperature and storage facts. Optional gear, edible crop and growing-environment planning facts; definitions are the definition catalog's. Raw food runway is not a diet/rot forecast. Unported sections are explicitly unavailable. Read-only; no authority or orders.")]
        [ToolResponse("payload", "string", "Official ColonyFactsReply ProtoJSON.", Always = true)]
        public async Task<object> ReadColonyFacts(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Raw ColonyFactsRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, ToolName, request!, Obs.ColonyFactsRequest.Parser, out var parsed, out var failure)
                || !Validate(parsed, out failure)) return ProtoBoundary.Encode(new Obs.ColonyFactsReply { Failure = failure });
            // Read on the game thread; delta (#773) and format on an
            // encoder worker (#644), since the delta digests the whole reply.
            var lease = await ReplyEncoder.Reserve(cancellationToken).ConfigureAwait(false);
            if (lease == null) return ProtoBoundary.Encode(new Obs.ColonyFactsReply { Failure = ProtoBoundary.Fail(Common.FailureCode.CapacityExhausted,
                "Colony facts reply encoders stayed saturated for " + ReplyEncoder.ReserveTimeoutMs + " ms; nothing was read.") });
            try
            {
                var captured = await ProtoBoundary.CaptureOnMainThread(ctx, () => {
                    if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out var invalid))
                        return new Obs.ColonyFactsReply { Failure = invalid };
                    try { return new Obs.ColonyFactsReply { Observed = Read(map, parsed, context) }; }
                    catch (Exception) { return new Obs.ColonyFactsReply { Unavailable = Unavailable(Common.UnavailableReason.ReadFailed, "Native colony facts could not be read completely.") }; }
                }, cancellationToken).ConfigureAwait(false);
                var owned = lease; lease = null;
                return await ProtoBoundary.EncodeDetached(captured, owned, reply => EncodeFacts(reply, parsed), cancellationToken).ConfigureAwait(false);
            }
            finally { lease?.Dispose(); }
        }

        private static Dictionary<string, object?> EncodeFacts(Obs.ColonyFactsReply reply, Obs.ColonyFactsRequest parsed)
        {
            if (reply.Observed == null) return ProtoBoundary.Encode(reply);
            return ProtoBoundary.Encode(reply, compact: true);
        }

        // The colony facts as a bundle section (issue #180): the same facts the
        // tool answers, or false for any read failure the bundle then omits.
        internal static bool TryRead(Map map, Obs.ColonyFactsRequest request, Common.ObservationContext context, [NotNullWhen(true)] out Obs.ColonyFactsSnapshot? snapshot)
        {
            snapshot = null;
            try { snapshot = Read(map, request, context); return true; }
            catch (Exception) { return false; }
        }

        internal static bool Validate(Obs.ColonyFactsRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity required.");
            return request?.Scope?.ExpectedIdentity != null;
        }

        private static Obs.ColonyFactsSnapshot Read(Map map, Obs.ColonyFactsRequest request, Common.ObservationContext context)
        {
            // Each span below names where the read's game-thread time went
            // in a slow snapshot capture line (#1273).
            var mark = System.Diagnostics.Stopwatch.GetTimestamp();
            void Span(string name) { var now = System.Diagnostics.Stopwatch.GetTimestamp(); ObservationWork.Detail(name, now - mark); mark = now; }
            var player = Faction.OfPlayerSilentFail ?? throw new InvalidOperationException("Player faction unavailable.");
            var people = map.mapPawns.AllPawnsSpawned.Where(p => p.IsFreeColonist && !p.Dead).ToList();
            if (people.Count == 0) throw new InvalidOperationException("No colony anchor.");
            var workers = people.Where(p => !p.Downed && !p.InMentalState && !p.Drafted).ToList();
            var center = new IntVec3((int)people.Average(p => p.Position.x), 0, (int)people.Average(p => p.Position.z));
            var things = map.listerThings.AllThings.Where(t => t.Spawned && !t.Position.Fogged(map)).ToList();
            Span("cf.things");
            // One read asks the same thing or def many times (items, beds,
            // benches, forbidden supplies, loot); each answer is fixed for
            // the read, so it is computed once (#878).
            var reach = new Dictionary<Thing, bool>();
            // A raid drafts or downs every colonist; reach is then asked of them
            // all, or every stock, bed and bench would read as gone until it ends.
            var reachers = workers.Count > 0 ? workers : people;
            Func<Thing, bool> reachable = t => {
                if (reach.TryGetValue(t, out var known)) return known;
                return reach[t] = reachers.Any(p =>
                    (p.playerSettings?.AreaRestrictionInPawnCurrentMap == null || p.playerSettings.AreaRestrictionInPawnCurrentMap[t.Position])
                    && p.CanReach(t, PathEndMode.Touch, Danger.None));
            };
            var edible = new Dictionary<ThingDef, bool>();
            Func<ThingDef, bool> humanFood = d => {
                if (d == null) return false;
                if (edible.TryGetValue(d, out var known)) return known;
                return edible[d] = NativeFoodPolicy.IsFood(d) && (d.ingestible.foodType & (FoodTypeFlags.Corpse | FoodTypeFlags.Kibble)) == 0
                    && NativeFoodPolicy.EatenByAll(people, d);
            };
            var items = things.Where(t => t.def.category == ThingCategory.Item && (t.Faction == null || t.Faction.IsPlayer)
                && !t.IsForbidden(player) && reachable(t)).ToList();
            Span("cf.items");
            var stock = items.GroupBy(t => t.def).OrderBy(g => g.Key.defName, StringComparer.Ordinal).ToList();
            var beds = things.OfType<Building_Bed>().Where(b => b.Faction == player && !b.ForPrisoners && !b.Medical
                && !b.IsForbidden(player) && reachable(b)).ToList();
            var indoorBeds = beds.Where(b => b.GetRoom() != null && b.GetRoom().ProperRoom
                && !b.GetRoom().PsychologicallyOutdoors && b.GetRoom().OpenRoofCount == 0).ToList();
            var temperatures = indoorBeds.Select(b => Finite(b.GetRoom().Temperature)).ToList();
            Span("cf.beds");
            var nutrition = items.Where(t => humanFood(t.def) && t.IngestibleNow && NativeFoodPolicy.Eaters(people, t.def).All(p => p.WillEat(t)))
                .Sum(t => (double)t.stackCount * NativeFoodPolicy.Eaters(people, t.def).Min(p => FoodUtility.NutritionForEater(p, t)));
            // Raw native demand/runway remains distinct from the controller's
            // diet, held-food, rot and competing-animal forecast.
            var demand = people.Sum(p => p.needs?.food == null ? 0.0 : GameTime.PerDay((double)p.needs.food.FoodFallPerTickAssumingCategory(HungerCategory.Fed, true)));
            Span("cf.nutrition");
            var result = new Obs.ColonyFactsSnapshot { Context = context, ColonistCount = (uint)people.Count,
                WorkerCount = (uint)workers.Count, Center = Cell(center), MapSize = Size(map), Biome = map.Biome.defName,
                PlayerTechLevel = player.def.techLevel.ToString(),
                FermentingBarrels = (uint)things.OfType<Building_FermentingBarrel>().Count(b => b.Faction == player && !b.IsForbidden(player) && reachable(b)),
                BedCapacity = checked((uint)beds.Sum(b => b.SleepingSlotsCount)), IndoorSleepingCapacity = checked((uint)indoorBeds.Sum(b => b.SleepingSlotsCount)),
                FoodNutrition = Finite(nutrition), NutritionPerDay = Finite(demand), OutdoorTemperatureC = Finite(map.mapTemperature.OutdoorTemp) };
            result.FoodSupply = new Obs.FoodSupplySection { Observed = Food(FoodSupplyFacts.Read(people,
                    things.Where(FoodSupplyFacts.SharedFood).ToList())) };
            Span("cf.foodSupply");
            result.Forecast = new Obs.ForecastSection { Observed = Forecast(ForecastFacts.Read(map, people, things)) };
            Span("cf.forecast");
            result.Upkeep = ReadComfort(map, things);
            Span("cf.upkeep");
            result.Threat = ReadThreat(map, people.Count);
            result.Development = new Obs.DevelopmentSection { Observed = ReadPower(map) };
            Span("cf.power");
            result.FoodChannels = NativeFoodChannels.Read(map, center, workers, humanFood);
            Span("cf.foodChannels");
            result.DeepResources = NativeDeepResources.Read(map);
            result.Policies = NativePolicyFacts.Read(map);
            result.Biotech = NativeBiotechColony.Read(map);
            result.Odyssey = NativeOdysseyColony.Read(map);
            result.Anomaly = NativeAnomalyColony.Read(map);
            result.TileMutators.Add(NativeOdysseyFacts.MapMutators(map));
            Span("cf.deep");
            if (demand > 0) result.FoodRunwayDays = Finite(nutrition / demand);
            else result.Issues.Add(Issue("food_runway_days", Common.UnavailableReason.NotApplicable, "No observed nutrition demand."));
            if (temperatures.Count > 0) { result.SleepingTemperatureMinC = temperatures.Min(); result.SleepingTemperatureMaxC = temperatures.Max(); }
            else {
                result.Issues.Add(Issue("sleeping_temperature_min_c", Common.UnavailableReason.NotApplicable, "No eligible indoor sleeping place."));
                result.Issues.Add(Issue("sleeping_temperature_max_c", Common.UnavailableReason.NotApplicable, "No eligible indoor sleeping place."));
            }
            foreach (var group in stock) result.Resources.Add(new Obs.Quantity { DefName = group.Key.defName, Units = group.Sum(t => (long)t.stackCount) });
            Span("cf.resources");
            var forbidden = StartingSupplyFacts.Forbidden(things, center, reachable);
            foreach (var t in forbidden)
                result.ForbiddenSupplies.Add(NativeRef.Thing(t));
            Span("cf.forbidden");
            result.EventLoot = EventLootFacts.Read(map, things, reachable);
            Span("cf.eventLoot");
            ReadProduction(result, map, people, things, reachable, humanFood);
            Span("cf.production");
            var naming = ColonyNamingTools.Pending();
            if (naming != null) result.Naming = new Obs.ColonyNaming { WindowId = naming.ID,
                FactionName = ColonyNamingTools.Name(naming, "curName"), SettlementName = ColonyNamingTools.Name(naming, "curSecondName") };
            else if (Find.WindowStack == null || Find.WindowStack.Windows.OfType<Dialog_NamePlayerFactionAndSettlement>().Any())
                result.Issues.Add(Issue("naming", Common.UnavailableReason.Unsupported, "Naming window census is unavailable or obstructed by another paused dialog."));
            else result.Issues.Add(Issue("naming", Common.UnavailableReason.NotApplicable, "No pending colony naming dialog."));
            var joiners = NativeJoinerLetters.Snapshot();
            result.JoinerLetters.AddRange(joiners);
            var dialog = ChoiceDialogTools.Pending();
            if (dialog != null) result.Dialog = ChoiceDialogTools.Snapshot(dialog);
            var conditions = new List<GameCondition>();
            map.gameConditionManager.GetAllGameConditionsAffectingMap(map, conditions);
            foreach (var condition in conditions) result.Environment.Add(EnvironmentCondition(condition));
            Span("cf.dialogs");
            result.Recovery = NativeRecoveryFacts.Read(map, context);
            Span("cf.recovery");
            result.Waste = HomeWasteTools.Project(map, context);
            Span("cf.waste");
            foreach (var field in new[] { "policy_resources", "food_corpses" })
                result.Issues.Add(Issue(field, Common.UnavailableReason.Unsupported, "Section is not yet projected."));
            try {
                var (season, dayOfYear) = GrowingCalendar.Calendar(map);
                result.FoodClimate = new Obs.FoodClimate { GrowingDaysRemaining = GrowingCalendar.GrowingDaysRemaining(map),
                GrowingDaysUntil = GrowingCalendar.GrowingDaysUntil(map), NonGrowingDays = GrowingCalendar.NonGrowingDays(map), Season = season, DayOfYear = dayOfYear,
                SowingNow = !OutdoorsPermanentlyDark(map) && DefDatabase<ThingDef>.AllDefsListForReading.Any(d => d.plant != null && d.plant.Sowable && d.plant.harvestedThingDef?.IsNutritionGivingIngestible == true
                    && (d.plant.sowResearchPrerequisites == null || d.plant.sowResearchPrerequisites.All(r => r.IsFinished)) && PlantUtility.GrowthSeasonNow(map, d)),
                GrowingDays = GenTemperature.TwelfthsInAverageTemperatureRange(map.Tile,Plant.DefaultMinOptimalGrowthTemperature,Plant.DefaultMaxOptimalGrowthTemperature).Count * GenDate.DaysPerTwelfth }; }
            catch (Exception) { result.Issues.Add(Issue("food_climate", Common.UnavailableReason.ReadFailed, "Seasonal crop budget unavailable.")); }
            Span("cf.climate");
            try { NativePlantAcquisition.Read(result, map, center, humanFood); }
            catch (Exception) {
                result.Acquisition.Clear(); result.ClearPendingFoodNutrition(); result.ClearPendingWoodUnits(); result.ClearPendingHunts();
                foreach (var field in new[] { "acquisition", "pending_food_nutrition", "pending_wood_units", "pending_hunts" })
                    result.Issues.Add(Issue(field, Common.UnavailableReason.ReadFailed, "Complete safe acquisition facts are unavailable."));
            }
            Span("cf.acquisition");
            try { NativeCutPlant.Read(result, map, center); }
            catch (Exception) { result.BlightedPlants.Clear(); result.Issues.Add(Issue("blighted_plants", Common.UnavailableReason.ReadFailed, "Complete blighted plant census is unavailable.")); }
            Span("cf.cutPlant");
            result.Planning = request.Planning ? new Obs.PlanningSection { Observed = Planning(map, center, request, context) }
                : new Obs.PlanningSection { Unavailable = Unavailable(Common.UnavailableReason.NotRequested, "Planning was not requested.") };
            Span("cf.planning");
            return result;
        }

        // Whether the biome keeps the sky dark for the map's whole life (#1858):
        // one of its map conditions is a GameCondition_NoSunlight or a subclass,
        // read from the game defs with no name list. A biome or condition def
        // missing its facts throws, so the food_climate read fails loudly
        // instead of reporting light that may not exist.
        static bool OutdoorsPermanentlyDark(Map map)
        {
            var biome = map.Biome ?? throw new InvalidOperationException("map has no biome");
            foreach (var condition in biome.biomeMapConditions ?? new List<GameConditionDef>())
            {
                var conditionClass = condition?.conditionClass ?? throw new InvalidOperationException("biome " + biome.defName + " lists a map condition with no class");
                if (typeof(GameCondition_NoSunlight).IsAssignableFrom(conditionClass)) return true;
            }
            return false;
        }

        // The game-condition census: every condition affecting the map, with
        // the native remaining duration so Go can plan for the length of a
        // fallout or volcanic winter. A permanent condition has no ticks_left;
        // a label read that throws leaves the label unset.
        internal static Obs.EnvironmentCondition EnvironmentCondition(GameCondition condition)
        {
            var row = new Obs.EnvironmentCondition { Id = condition.uniqueID.ToString(System.Globalization.CultureInfo.InvariantCulture),
                DefName = condition.def.defName, Implementation = condition.GetType().FullName, Permanent = condition.Permanent };
            if (!condition.Permanent) row.TicksLeft = Math.Max(0, condition.TicksLeft);
            var label = condition.LabelCap;
            if (!string.IsNullOrEmpty(label)) row.Label = label;
            return row;
        }

        // Colony wealth and raid points (#395): the WealthWatcher split (its
        // own lazy recount; never ForceRecount on a read path), the wealth
        // the storyteller scales by and the points a default threat incident
        // would draw for this map now, with the adaptation and difficulty
        // factors already inside that figure. Any failure makes the section
        // unavailable rather than partial.
        internal static Obs.ThreatSection ReadThreat(Map map, int colonists)
        {
            try {
                var wealth = map.wealthWatcher;
                var facts = new Obs.ThreatFacts {
                    WealthItems = Finite(wealth.WealthItems), WealthBuildings = Finite(wealth.WealthBuildings), WealthPawns = Finite(wealth.WealthPawns),
                    WealthTotal = Finite(wealth.WealthTotal), StorytellerWealth = Finite(map.PlayerWealthForStoryteller),
                    RaidPoints = Finite(StorytellerUtility.DefaultThreatPointsNow(map)),
                    AdaptationFactor = Finite(Find.StoryWatcher.watcherAdaptation.TotalThreatPointsFactor),
                    DifficultyThreatScale = Finite(Find.Storyteller.difficulty.threatScale),
                    ColonistCount = checked((uint)colonists),
                };
                return new Obs.ThreatSection { Observed = facts };
            }
            catch (Exception) { return new Obs.ThreatSection { Unavailable = Unavailable(Common.UnavailableReason.ReadFailed, "Colony wealth and raid points could not be read.") }; }
        }

        private static Obs.UpkeepSection ReadComfort(Map map, List<Thing> things)
        {
            var result = new Obs.UpkeepFacts { };
            var began = System.Diagnostics.Stopwatch.GetTimestamp();
            try { result.Comfort = new Obs.ComfortSection { Observed = ComfortFacts.ReadProtocol(map) }; }
            catch (Exception) { result.Comfort = new Obs.ComfortSection { Unavailable = Unsupported("Complete comfort facts are unavailable.") }; }
            ObservationWork.Detail("cf.upkeep.comfort", System.Diagnostics.Stopwatch.GetTimestamp() - began);
            NativeUpkeepFacts.Populate(map, things, result);
            foreach (var field in new[] { "construction", "storage_cells", "storage_capacity", "protected_cells", "hauling", "wall_removal" })
                result.Issues.Add(Issue(field, Common.UnavailableReason.Unsupported, "Upkeep section is not yet projected."));
            return new Obs.UpkeepSection { Observed = result };
        }

        private static Obs.DevelopmentFacts ReadPower(Map map)
        {
            // Traders (consumers and generators) and batteries share one census so
            // Go's power topology sees every network member that matters for
            // coverage and reserve: batteries carry stored/capacity energy; the
            // base wattage is the def's (Go reads it from the catalog); each member's service state is its building table row (#1343).
            var traders = map.listerBuildings.allBuildingsColonist.Select(b => b.TryGetComp<CompPowerTrader>())
                .Where(p => p != null).OrderBy(p => p.parent.thingIDNumber).ToList();
            var batteries = map.listerBuildings.allBuildingsColonist.Select(b => b.TryGetComp<CompPowerBattery>())
                .Where(p => p != null).OrderBy(p => p.parent.thingIDNumber).ToList();
            var conduits = map.listerBuildings.allBuildingsColonist.Where(b => b.def.defName == "PowerConduit" || b.def.defName == "HiddenConduit" || b.def.defName == "WaterproofConduit")
                .OrderBy(b => b.thingIDNumber).ToList();
            var nets = map.powerNetManager.AllNetsListForReading.OrderBy(n => n.GetHashCode()).ToList();
            var geysers = map.listerThings.ThingsOfDef(ThingDefOf.SteamGeyser).OfType<Building_SteamGeyser>().OrderBy(g => g.thingIDNumber).ToList();
            var result = new Obs.DevelopmentFacts { };
            // Archived letters survive dismissal and saves. Use the game's own
            // translated label rather than matching English message prose.
            var shortLabel = "LetterLabelShortCircuit".Translate().CapitalizeFirst().ToString();
            var incidents = Find.Archive.ArchivablesListForReading.OfType<Letter>()
                .Concat(Find.LetterStack.LettersListForReading)
                .Where(l => l.Label.ToString() == shortLabel && l.lookTargets != null
                    && l.lookTargets.targets.Any(t => t.Map == map))
                .Select(l => l.arrivalTick).ToList();
            if (incidents.Count > 0) result.ShortCircuitTick = incidents.Max();
            foreach (var power in traders) {
                var building = (Building)power.parent;
                var row = new Obs.DevelopmentPower { Building = NativeBuildingObservationTools.Ref(building),
                    Roofed = building.OccupiedRect().Cells.All(c => c.Roofed(map)) };
                // A turret's observed damage per second (#1188).
                try { var dps = NativeDefenseStats.TurretDps(building); if (dps.HasValue) row.TurretDps = Finite(dps.Value); } catch { }
                result.Power.Add(row);
            }
            foreach (var battery in batteries) {
                var building = (Building)battery.parent;
                result.Power.Add(new Obs.DevelopmentPower { Building = NativeBuildingObservationTools.Ref(building), Roofed = building.OccupiedRect().Cells.All(c => c.Roofed(map)),
                    StoredWattDays = Finite(battery.StoredEnergy), CapacityWattDays = Finite(battery.Props.storedEnergyMax) });
            }
            foreach (var conduit in conduits)
                result.Furniture.Add(new Obs.DevelopmentFurniture { Building = NativeBuildingObservationTools.Ref(conduit) });
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
                    });
            }
            foreach (var geyser in geysers) {
                // A geyser is free for a geothermal generator only while no
                // harvester, blueprint, frame or other building stands on it.
                var cells = geyser.OccupiedRect().Cells.ToList();
                var occupied = geyser.harvester != null || cells.Any(c => c.GetThingList(map).Any(t => t != geyser && (t.def.category == ThingCategory.Building || t.def.IsBlueprint || t.def.IsFrame)));
                var row = new Obs.SteamGeyser { Geyser = NativeRef.Thing(geyser), Occupied = occupied };
                foreach (var cell in cells) row.Cells.Add(Cell(cell));
                result.Geysers.Add(row);
            }
            return result;
        }

        private static string NetId(PowerNet net) => NativeBuildingObservationTools.NetId(net);

        private static void ReadProduction(Obs.ColonyFactsSnapshot result, Map map, List<Pawn> people,
            List<Thing> things, Func<Thing, bool> reachable, Func<ThingDef, bool> humanFood)
        {
            var benches = things.OfType<Building_WorkTable>().Where(b => b.Faction == Faction.OfPlayer && reachable(b)
                && b.def.AllRecipes.Any(r => r.products.Any(p => humanFood(p.thingDef)))).OrderBy(b => b.thingIDNumber).ToList();
            var haulers = NativeResourceSourcesTool.Haulers(map);
            foreach (var bench in benches) {
                var row = new Obs.CookingFacts { Bench = NativeBuildingObservationTools.Ref(bench), BenchSnapshot = NativeProductionBills.Snapshot(bench,bench,result.Context),
                    Usable = !bench.IsBurning() && (bench.TryGetComp<CompPowerTrader>() == null || bench.TryGetComp<CompPowerTrader>().PowerOn)
                        && (bench.TryGetComp<CompRefuelable>() == null || bench.TryGetComp<CompRefuelable>().HasFuel) };
                var recipes = bench.def.AllRecipes.Where(r => r.products.Any(p => humanFood(p.thingDef))).OrderBy(r => r.defName).ToList();
                foreach (var recipe in recipes) {
                    row.Recipes.Add(NativeProductionBills.RecipeRow(bench,recipe));
                    var production = new Obs.FoodProduction { Recipe = recipe.defName, Available = NativeProductionBills.Recipe(bench,recipe) };
                    foreach(var product in recipe.products){
                        var food=new Obs.FoodProduct{DefName=product.thingDef.defName,Count=product.count,Edible=humanFood(product.thingDef),NutritionDemandPerDay=result.NutritionPerDay};
                        food.Storable=NativeResourceSourcesTool.Capacity(map,product.thingDef,haulers,out var stored)+stored;
                        production.Products.Add(food);
                    }
                    row.Production.Add(production);
                }
                for(var index=0;index<bench.BillStack.Count;index++)row.Bills.Add(NativeProductionBills.BillRow(bench.BillStack.Bills[index],index));
                var cookingRoom = bench.GetRoom();
                if (cookingRoom != null) row.Room = NativeRef.Room(cookingRoom);
                if (NativeAutoRefuel.Comp(bench) is CompRefuelable refuel) row.AutoRefuel = refuel.allowAutoRefuel;
                result.Cooking.Add(row);
            }
            foreach(var bench in things.Where(t=>t.Faction==Faction.OfPlayer&&t is IBillGiver&&reachable(t)&&t.def.AllRecipes.Any(NativeRecipeRoles.ButcherFlesh)).OrderBy(t=>t.thingIDNumber)){
                var giver=(IBillGiver)bench;
                var row=new Obs.ButcheringFacts{Bench=NativeBuildingObservationTools.Ref(bench),BenchSnapshot=NativeProductionBills.Snapshot(bench,giver,result.Context),Usable=NativeProductionBills.Usable(bench)};
                HumanFoodFacts.Fill(row,bench);
                foreach(var recipe in bench.def.AllRecipes.Where(NativeRecipeRoles.ButcherFlesh))row.Recipes.Add(NativeProductionBills.RecipeRow(bench,recipe));
                for(var index=0;index<giver.BillStack.Count;index++)row.Bills.Add(NativeProductionBills.BillRow(giver.BillStack.Bills[index],index));
                var butcherRoom = bench.GetRoom();
                if (butcherRoom != null) row.Room = NativeRef.Room(butcherRoom);
                result.Butchering.Add(row);
            }

        }

        private static Obs.PlanningFacts Planning(Map map, IntVec3 center, Obs.ColonyFactsRequest request, Common.ObservationContext context)
        {
            var result = new Obs.PlanningFacts { };
            var began = System.Diagnostics.Stopwatch.GetTimestamp();
            try { result.Gear = NativeGearFacts.Read(map, context); }
            catch (Exception) { result.Issues.Add(Issue("gear", Common.UnavailableReason.ReadFailed, "Complete native loadout upkeep is unavailable.")); }
            ObservationWork.Detail("cf.gear", System.Diagnostics.Stopwatch.GetTimestamp() - began);
            began = System.Diagnostics.Stopwatch.GetTimestamp();
            result.Crops.Add(Crops(map));
            ObservationWork.Detail("cf.crops", System.Diagnostics.Stopwatch.GetTimestamp() - began);
            // The planning window itself (the site cells around the centre)
            // is no longer carried here: the controller reads it on demand
            // through observations_get_cells (issue #356), and the
            // definitions are the definition catalog's (#1340). The window's
            // rect still bounds the environment census below.
            var min = new IntVec3(Math.Max(0, center.x - 22), 0, Math.Max(0, center.z - 22));
            var max = new IntVec3(Math.Min(map.Size.x - 1, center.x + 22), 0, Math.Min(map.Size.z - 1, center.z + 22));
            try { result.Environment = Environment(map, min, max); }
            catch (Exception) { result.Issues.Add(Issue("environment", Common.UnavailableReason.ReadFailed, "Controlled-environment growing facts are unavailable.")); }
            return result;
        }

        // The definitions the catalog carries: player-buildable or sowable
        // ThingDefs and player-buildable TerrainDefs.
        internal static bool Cataloged(ThingDef def) => def.BuildableByPlayer || def.plant?.Sowable == true;

        // A sowable crop whose product is edible: the per-map facts the
        // catalog row cannot carry, one row per crop sorted by name.
        // The crop defs, sorted by name: the defs are fixed for the process, so
        // they are scanned once rather than per frame.
        private static readonly Lazy<List<ThingDef>> CropDefs = new Lazy<List<ThingDef>>(() =>
            DefDatabase<ThingDef>.AllDefsListForReading.Where(d => Cataloged(d) && d.plant != null).OrderBy(d => d.defName, StringComparer.Ordinal).ToList());

        internal static List<Obs.EdibleCrop> Crops(Map map)
        {
            var result = new List<Obs.EdibleCrop>();
            var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead).ToList();
            var demand = people.Sum(p => p.needs?.food == null ? 0f : GameTime.PerDay(p.needs.food.FoodFallPerTickAssumingCategory(HungerCategory.Fed, true)));
            var animals = map.mapPawns.AllPawnsSpawned.Where(p => !p.Dead && p.RaceProps.Animal
                && p.Faction == Faction.OfPlayerSilentFail && p.needs?.food != null).ToList();
            foreach (var def in CropDefs.Value) {
                var product = def.plant.harvestedThingDef;
                if (product == null || !NativeFoodPolicy.IsFood(product)) continue;
                var row = new Obs.EdibleCrop { DefName = def.defName };
                row.DietAllowed = !product.IsFungus || !ModsConfig.IdeologyActive || !people.Any(p =>
                    p.Ideo != null && p.Ideo.PreceptsListForReading.Any(precept => precept.def.comps
                        .OfType<PreceptComp_SelfTookMemoryThought>().Any(comp =>
                            (comp.eventDef == HistoryEventDefOf.AteFungus || comp.eventDef == HistoryEventDefOf.AteFungusAsIngredient)
                            && comp.thought.stages.Any(stage => stage.baseMoodEffect < 0))));
                row.NutritionDemandPerDay = Finite(demand + animals.Where(p => p.RaceProps.CanEverEat(product)
                    && p.foodRestriction?.GetCurrentRespectedRestriction(p)?.filter.Allows(product) != false)
                    .Sum(p => GameTime.PerDay(p.needs.food.FoodFallPerTickAssumingCategory(HungerCategory.Fed, true))));
                result.Add(row);
            }
            return result;
        }

        // The roles the game's room-role workers score by ThingDefOf name (#1731),
        // sorted: the part of the room roles Go cannot derive from the def rows.
        internal static IEnumerable<string> GameRoomRoles(ThingDef def)
        {
            var roles = new List<string>();
            if (def == ThingDefOf.BabyDecoration) roles.Add("Decoration");
            if (def == ThingDefOf.Blackboard) roles.Add("Board");
            if (def == ThingDefOf.SchoolDesk) roles.Add("Desk");
            if (def == ThingDefOf.ToyBox) roles.Add("Toy");
            roles.Sort(StringComparer.Ordinal);
            return roles;
        }
        // Sun lamps, plant growers and rooms inside the planning region plus every
        // power network's headroom split by source. Lamp growth cells are the
        // native Building_SunLamp radius, not the glow radius, so the controller
        // never plants where the game would not grow.
        // The native sun lamp class is internal; its def names the class and carries the growth radius as specialDisplayRadius.
        private static bool IsSunLamp(Building b) => b.def.thingClass?.Name == "Building_SunLamp" && b.def.specialDisplayRadius > 0f;
        private static Obs.ControlledEnvironment Environment(Map map, IntVec3 min, IntVec3 max)
        {
            var result = new Obs.ControlledEnvironment { Daylight = GenCelestial.CurCelestialSunGlow(map) >= 0.3f };
            var weather = map.weatherManager?.curWeather?.defName;
            if (!string.IsNullOrEmpty(weather)) result.Weather = weather;
            bool Inside(IntVec3 c) => c.x >= min.x && c.x <= max.x && c.z >= min.z && c.z <= max.z;
            string? NetId(CompPowerTrader? power) => power?.PowerNet == null ? null : power.PowerNet.GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var rooms = new Dictionary<int, Room>();
            void Note(Room? room) { if (room != null && !room.PsychologicallyOutdoors && room.ProperRoom) rooms[room.ID] = room; }
            var buildings = map.listerBuildings.allBuildingsColonist.Where(b => Inside(b.Position)).OrderBy(b => b.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
            foreach (var building in buildings) {
                var power = building.TryGetComp<CompPowerTrader>();
                var room = building.GetRoom();
                if (IsSunLamp(building)) { var lamp = building;
                    var row = new Obs.GrowLight { Building = NativeBuildingObservationTools.Ref(lamp) };
                    if (power != null) { row.Powered = power.PowerOn; row.PowerW = Finite(power.Props.PowerConsumption); var id = NetId(power); if (id != null) row.PowerNetId = id; }
                    else row.Issues.Add(Issue("powered", Common.UnavailableReason.NotApplicable, "Lamp has no power trader."));
                    var schedule = lamp.TryGetComp<CompSchedule>();
                    row.LitNow = (power == null || power.PowerOn) && (schedule == null || schedule.Allowed);
                    foreach (var c in GenRadial.RadialCellsAround(lamp.Position, lamp.def.specialDisplayRadius, true).Where(c => c.InBounds(map))) row.GrowthCells.Add(Cell(c));
                    if (room != null) { row.Room = NativeRef.Room(room); Note(room); }
                    result.Lights.Add(row);
                } else if (building is Building_PlantGrower grower) {
                    var row = new Obs.PlantGrower { Building = NativeBuildingObservationTools.Ref(grower),
                        CanSow = grower.CanAcceptSowNow() };
                    if (grower.def.fertility >= 0f) row.Fertility = Finite(grower.def.fertility);
                    if (grower.def.building?.sowTag != null) row.SowTag = grower.def.building.sowTag;
                    var crop = grower.GetPlantDefToGrow();
                    if (crop != null) row.CropDefName = crop.defName;
                    if (power != null) { row.Powered = power.PowerOn; row.PowerW = Finite(power.Props.PowerConsumption); var id = NetId(power); if (id != null) row.PowerNetId = id; }
                    foreach (var c in ((IPlantToGrowSettable)grower).Cells) row.PlantCells.Add(Cell(c));
                    if (room != null) { row.Room = NativeRef.Room(room); Note(room); }
                    result.Growers.Add(row);
                }
            }
            for (int z = min.z; z <= max.z; z++) for (int x = min.x; x <= max.x; x++) {
                var c = new IntVec3(x, 0, z);
                if (!c.Fogged(map)) Note(c.GetRoom(map));
            }
            foreach (var room in rooms.Values.OrderBy(r => r.ID)) {
                var row = new Obs.GrowRoom { Room = NativeRef.Room(room), TemperatureC = Finite(room.Temperature), CellCount = (uint)room.CellCount,
                    OpenRoofCount = (uint)room.OpenRoofCount, ProperRoom = room.ProperRoom, PsychologicallyOutdoors = room.PsychologicallyOutdoors };
                uint lit = 0;
                foreach (var c in room.Cells) if (map.glowGrid.GroundGlowAt(c) >= 0.3f) lit++;
                row.LitCells = lit;
                result.Rooms.Add(row);
            }
            var nets = map.powerNetManager?.AllNetsListForReading ?? new List<PowerNet>();
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
            return result;
        }
        private static Common.Cell Cell(IntVec3 c) => new Common.Cell { X = c.x, Z = c.z };
        private static Obs.MapSize Size(Map map) => new Obs.MapSize { Width = (uint)map.Size.x, Height = (uint)map.Size.z };
        private static double Finite(double v) => double.IsNaN(v) || double.IsInfinity(v) ? throw new InvalidOperationException("Nonfinite fact.") : v;
        private static double Nonnegative(double v) => Finite(v) >= 0 ? v : throw new InvalidOperationException("Negative fact.");
        private static Obs.ForecastFacts Forecast(ForecastFacts.Snapshot source)
        {
            var result = new Obs.ForecastFacts { CombinedFoodSupply = Food(source.combinedFoodSupply),
                };
            result.AnimalIds.Add(source.animalIds);
            foreach (var crop in source.crops) {
                var row = new Obs.CropForecast { Zone = NativeRef.Of(crop.id.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
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
            var result = new Obs.FoodSupplyFacts { };
            if (source.larder != null) {
                result.Larder = new Obs.FoodLarderFacts { RawMeatNutrition = Finite(source.larder.RawMeatNutrition), CookDemandNutrition = Finite(source.larder.CookDemandNutrition) };
                foreach (var corpse in source.larder.Corpses) {
                    var row = new Obs.CorpseHandling { StockId = corpse.ID, Cell = Cell(corpse.Cell), FrozenDestination = corpse.FrozenDestination };
                    if (corpse.Hauler != null) row.HaulerId = corpse.Hauler;
                    result.Larder.Corpses.Add(row);
                }
                foreach (var cell in source.larder.ColdSites) result.Larder.ColdSites.Add(Cell(cell));
            }
            foreach (var consumer in source.consumers)
                result.Consumers.Add(new Obs.FoodConsumer { PawnId = consumer.id, NutritionPerDay = Finite(consumer.nutritionPerDay), HumanMeatAcceptable = consumer.humanMeatAcceptable });
            foreach (var stock in source.stocks) {
                // The stock's thing is a things table row (#1343).
                var row = new Obs.FoodStock { Item = NativeRef.Thing(stock.thing!), Nutrition = Finite(stock.nutrition) };
                row.Eaters.Add(NativeRef.All(stock.eaters!));
                if (stock.holder != null) row.Holder = NativeRef.Of(stock.holder);
                result.Stocks.Add(row);
            }
            return result;
        }
        private static Common.Unavailable Unavailable(Common.UnavailableReason reason, string detail) => new Common.Unavailable { Reason = reason, Detail = detail };
        private static Common.Unavailable Unsupported(string detail) => Unavailable(Common.UnavailableReason.Unsupported, detail);
        private static Obs.ReadIssue Issue(string field, Common.UnavailableReason reason, string detail) => new Obs.ReadIssue { Field = field, Unavailable = Unavailable(reason, detail) };
    }
}
