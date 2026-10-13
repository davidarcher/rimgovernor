#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;

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
            if (!ProtoBoundary.TryParse<Obs.ColonyFactsRequest>(ctx, ToolName, request!, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Obs.ColonyFactsReply { Outcome = new Obs.ColonyFactsReplyOutcome.FailureCase(failure) });
            // Read on the game thread; delta and format on an
            // encoder worker, since the delta digests the whole reply.
            var lease = await ReplyEncoder.Reserve(cancellationToken).ConfigureAwait(false);
            if (lease == null) return ProtoBoundary.Encode(new Obs.ColonyFactsReply { Outcome = ProtoBoundary.Fail(Common.FailureCode.CapacityExhausted,
                "Colony facts reply encoders stayed saturated for " + ReplyEncoder.ReserveTimeoutMs + " ms; nothing was read.") });
            try
            {
                var captured = await ProtoBoundary.CaptureOnMainThread(ctx, () => HandleReadColonyFacts(parsed), cancellationToken).ConfigureAwait(false);
                var owned = lease; lease = null;
                return await ProtoBoundary.EncodeDetached(captured, owned, reply => EncodeFacts(reply, parsed), cancellationToken).ConfigureAwait(false);
            }
            finally { lease?.Dispose(); }
        }

        // On the game thread: the typed core of the tool, shared with the in-process facade.
        internal static Obs.ColonyFactsReply HandleReadColonyFacts(Obs.ColonyFactsRequest parsed)
        {
            if (!Validate(parsed, out var invalidRequest)) return new Obs.ColonyFactsReply { Outcome = invalidRequest };
            if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out var invalid))
                return new Obs.ColonyFactsReply { Outcome = invalid };
            try { return new Obs.ColonyFactsReply { Outcome = Read(map, parsed, context) }; }
            catch (Exception error) { ModLog.Error("observe", "Colony facts read failed: " + error); return new Obs.ColonyFactsReply { Outcome = Unavailable(Common.UnavailableReason.ReadFailed, PlacementPreviewOperation.Diagnostic("Native colony facts could not be read completely: " + error.GetType().Name + ": " + error.Message)) }; }
        }

        private static Dictionary<string, object?> EncodeFacts(Obs.ColonyFactsReply reply, Obs.ColonyFactsRequest parsed)
        {
            if (reply.Outcome?.Observed == null) return ProtoBoundary.Encode(reply);
            return ProtoBoundary.Encode(reply, compact: true);
        }

        internal static bool Validate(Obs.ColonyFactsRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity required.");
            return request?.Scope?.ExpectedIdentity != null;
        }

        // The colony facts as a frame section: a read that throws fails the frame.
        internal static Obs.ColonyFactsSnapshot Read(Map map, Obs.ColonyFactsRequest request, Common.ObservationContext context, uint? routePathBudgetMs = null)
        {
            // Each span below names where the read's game-thread time went
            // in a slow snapshot capture line.
            var mark = System.Diagnostics.Stopwatch.GetTimestamp();
            void Span(string name) { var now = System.Diagnostics.Stopwatch.GetTimestamp(); ObservationWork.Detail(name, now - mark); mark = now; }
            var player = Faction.OfPlayerSilentFail ?? throw new InvalidOperationException("Player faction unavailable.");
            var people = map.mapPawns.AllPawnsSpawned.Where(p => p.IsFreeColonist && !p.Dead).ToList();
            var workers = people.Where(p => !p.Downed && !p.InMentalState && !p.Drafted).ToList();
            // With no colonist spawned (the last one kidnapped or away) the colony
            // is still there: anchor on its buildings, else the map's middle.
            var anchors = people.Select(p => p.Position).ToList();
            if (anchors.Count == 0) anchors = map.listerBuildings.allBuildingsColonist.Select(b => b.Position).ToList();
            var center = anchors.Count > 0
                ? new IntVec3((int)anchors.Average(c => c.x), 0, (int)anchors.Average(c => c.z))
                : new IntVec3(map.Size.x / 2, 0, map.Size.z / 2);
            var things = map.listerThings.AllThings.Where(t => t.Spawned && !t.Position.Fogged(map)).ToList();
            Span("cf.things");
            // One read asks the same thing or def many times (items, beds,
            // benches, forbidden supplies, loot); each answer is fixed for
            // the read, so it is computed once.
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
            result = result with { FoodSupply = new Obs.FoodSupplySection { Outcome = Food(FoodSupplyFacts.Read(people,
                    things.Where(FoodSupplyFacts.SharedFood).ToList())) } };
            Span("cf.foodSupply");
            result = result with { Forecast = new Obs.ForecastSection { Outcome = Forecast(ForecastFacts.Read(map, people, things)) } };
            Span("cf.forecast");
            result = result with { Upkeep = ReadComfort(map, things, routePathBudgetMs) };
            Span("cf.upkeep");
            result = result with { Threat = ReadThreat(map, people.Count) };
            result = result with { Development = new Obs.DevelopmentSection { Outcome = ReadPower(map) } };
            Span("cf.power");
            result = result with { FoodChannels = NativeFoodChannels.Read(map, center, workers, humanFood) };
            result = result with { DeliveryLedger = NativeDeliveryLedger.Read() };
            Span("cf.foodChannels");
            result = result with { DeepResources = NativeDeepResources.Read(map) };
            result = result with { Policies = NativePolicyFacts.Read(map) };
            result = result with { Biotech = NativeBiotechColony.Read(map) };
            result = result with { Royalty = NativeRoyaltyColony.Read(map) };
            result = result with { Odyssey = NativeOdysseyColony.Read(map) };
            result = result with { Anomaly = NativeAnomalyColony.Read(map) };
            result = result with { TileMutators = [.. result.TileMutators, .. NativeOdysseyFacts.MapMutators(map)] };
            Span("cf.deep");
            if (demand > 0) result = result with { FoodRunwayDays = Finite(nutrition / demand) };
            else result = result with { Issues = [.. result.Issues, Issue("food_runway_days", Common.UnavailableReason.NotApplicable, "No observed nutrition demand.")] };
            if (temperatures.Count > 0) { result = result with { SleepingTemperatureMinC = temperatures.Min() }; result = result with { SleepingTemperatureMaxC = temperatures.Max() }; }
            else {
                result = result with { Issues = [.. result.Issues, Issue("sleeping_temperature_min_c", Common.UnavailableReason.NotApplicable, "No eligible indoor sleeping place.")] };
                result = result with { Issues = [.. result.Issues, Issue("sleeping_temperature_max_c", Common.UnavailableReason.NotApplicable, "No eligible indoor sleeping place.")] };
            }
            foreach (var group in stock) result = result with { Resources = [.. result.Resources, new Obs.Quantity { DefName = group.Key.defName, Units = group.Sum(t => (long)t.stackCount) }] };
            Span("cf.resources");
            result = result with { EventLoot = EventLootFacts.Read(map, things, reachable) };
            Span("cf.eventLoot");
            result = ReadProduction(result, map, people, things, reachable, humanFood);
            Span("cf.production");
            var joiners = NativeJoinerLetters.Snapshot();
            result = result with { JoinerLetters = [.. result.JoinerLetters, .. joiners] };
            var dialog = ChoiceDialogTools.Pending();
            if (dialog != null) result = result with { Dialog = ChoiceDialogTools.Snapshot(dialog) };
            var conditions = new List<GameCondition>();
            map.gameConditionManager.GetAllGameConditionsAffectingMap(map, conditions);
            foreach (var condition in conditions) result = result with { Environment = [.. result.Environment, EnvironmentCondition(condition)] };
            Span("cf.dialogs");
            result = result with { Recovery = NativeRecoveryFacts.Read(map, context) };
            Span("cf.recovery");
            result = result with { Waste = HomeWasteTools.Project(map, context) };
            Span("cf.waste");
            foreach (var field in new[] { "food_corpses" })
                result = result with { Issues = [.. result.Issues, Issue(field, Common.UnavailableReason.Unsupported, "Section is not yet projected.")] };
            try {
                var (season, dayOfYear) = GrowingCalendar.Calendar(map);
                result = result with { FoodClimate = new Obs.FoodClimate { GrowingDaysRemaining = GrowingCalendar.GrowingDaysRemaining(map),
                GrowingDaysUntil = GrowingCalendar.GrowingDaysUntil(map), NonGrowingDays = GrowingCalendar.NonGrowingDays(map), Season = season, DayOfYear = dayOfYear,
                SowingNow = !OutdoorsPermanentlyDark(map) && DefDatabase<ThingDef>.AllDefsListForReading.Any(d => d.plant != null && d.plant.Sowable && d.plant.harvestedThingDef?.IsNutritionGivingIngestible == true
                    && (d.plant.sowResearchPrerequisites == null || d.plant.sowResearchPrerequisites.All(r => r.IsFinished)) && PlantUtility.GrowthSeasonNow(map, d)),
                GrowingDays = GenTemperature.TwelfthsInAverageTemperatureRange(map.Tile,Plant.DefaultMinOptimalGrowthTemperature,Plant.DefaultMaxOptimalGrowthTemperature).Count * GenDate.DaysPerTwelfth } }; }
            catch (Exception error) { ModLog.Error("observe", "Colony facts section failed: " + error); result = result with { Issues = [.. result.Issues, Issue("food_climate", Common.UnavailableReason.ReadFailed, "Seasonal crop budget unavailable.")] }; }
            Span("cf.climate");
            try { result = NativePlantAcquisition.Read(result, map, center, humanFood, request); }
            catch (Exception error) { ModLog.Error("observe", "Colony facts section failed: " + error);
                result = result with { Acquisition = [], PendingFoodNutrition = null, PendingWoodUnits = null, PendingHunts = null };
                foreach (var field in new[] { "acquisition", "pending_food_nutrition", "pending_wood_units", "pending_hunts" })
                    result = result with { Issues = [.. result.Issues, Issue(field, Common.UnavailableReason.ReadFailed, "Complete safe acquisition facts are unavailable.")] };
            }
            Span("cf.acquisition");
            result = result with { Planning = request.Planning.GetValueOrDefault() ? new Obs.PlanningSection { Outcome = Planning(map, center, request, context) }
                : new Obs.PlanningSection { Outcome = Unavailable(Common.UnavailableReason.NotRequested, "Planning was not requested.") } };
            Span("cf.planning");
            return result;
        }

        // Whether the biome keeps the sky dark for the map's whole life:
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
        // its class and label are the GameConditionDef row's.
        internal static Obs.EnvironmentCondition EnvironmentCondition(GameCondition condition)
        {
            var row = new Obs.EnvironmentCondition { Id = condition.uniqueID.ToString(System.Globalization.CultureInfo.InvariantCulture),
                DefName = condition.def.defName, Permanent = condition.Permanent };
            if (!condition.Permanent) row = row with { TicksLeft = Math.Max(0, condition.TicksLeft) };
            return row;
        }

        // Colony wealth and raid points: the WealthWatcher split (its
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
                facts = facts with { Raids = [.. facts.Raids, .. NativeDefenseObservationTools.RaidTracks(map)] };
                return new Obs.ThreatSection { Outcome = facts };
            }
            catch (Exception error) { ModLog.Error("observe", "Colony facts section failed: " + error); return new Obs.ThreatSection { Outcome = Unavailable(Common.UnavailableReason.ReadFailed, "Colony wealth and raid points could not be read.") }; }
        }

        private static Obs.UpkeepSection ReadComfort(Map map, List<Thing> things, uint? routePathBudgetMs)
        {
            var result = new Obs.UpkeepFacts { };
            var began = System.Diagnostics.Stopwatch.GetTimestamp();
            try { result = result with { Comfort = new Obs.ComfortSection { Outcome = ComfortFacts.ReadProtocol(map) } }; }
            catch (Exception error) { ModLog.Error("observe", "Colony facts section failed: " + error); result = result with { Comfort = new Obs.ComfortSection { Outcome = Unsupported("Complete comfort facts are unavailable.") } }; }
            ObservationWork.Detail("cf.upkeep.comfort", System.Diagnostics.Stopwatch.GetTimestamp() - began);
            result = NativeUpkeepFacts.Populate(map, things, result, routePathBudgetMs);
            foreach (var field in new[] { "construction", "storage_cells", "storage_capacity", "protected_cells", "hauling", "wall_removal" })
                result = result with { Issues = [.. result.Issues, Issue(field, Common.UnavailableReason.Unsupported, "Upkeep section is not yet projected.")] };
            return new Obs.UpkeepSection { Outcome = result };
        }

        private static Obs.DevelopmentFacts ReadPower(Map map)
        {
            // Traders (consumers and generators) and batteries share one census so
            // Go's power topology sees every network member that matters for
            // coverage and reserve: batteries carry stored/capacity energy; the
            // base wattage is the def's (Go reads it from the catalog); each member's service state is its building table row.
            var traders = map.listerBuildings.allBuildingsColonist.Select(b => b.TryGetComp<CompPowerTrader>())
                .Where(p => p != null).OrderBy(p => p.parent.thingIDNumber).ToList();
            var batteries = map.listerBuildings.allBuildingsColonist.Select(b => b.TryGetComp<CompPowerBattery>())
                .Where(p => p != null).OrderBy(p => p.parent.thingIDNumber).ToList();
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
            if (incidents.Count > 0) result = result with { ShortCircuitTick = incidents.Max() };
            foreach (var power in traders) {
                var building = (Building)power.parent;
                var row = new Obs.DevelopmentPower { Building = NativeBuildingObservationTools.Ref(building),
                    Roofed = building.OccupiedRect().Cells.All(c => c.Roofed(map)) };
                // A turret's observed damage per second.
                var dps = NativeDefenseStats.TurretDps(building); if (dps.HasValue) row = row with { TurretDps = Finite(dps.Value) };
                result = result with { Power = [.. result.Power, row] };
            }
            foreach (var battery in batteries) {
                var building = (Building)battery.parent;
                result = result with { Power = [.. result.Power, new Obs.DevelopmentPower { Building = NativeBuildingObservationTools.Ref(building), Roofed = building.OccupiedRect().Cells.All(c => c.Roofed(map)),
                    StoredWattDays = Finite(battery.StoredEnergy) }] };
            }
            foreach (var net in nets) {
                var generation = net.powerComps.Where(p => p.PowerOn && p.PowerOutput > 0).Sum(p => (double)p.PowerOutput);
                var consumption = net.powerComps.Where(p => p.PowerOn && p.PowerOutput < 0).Sum(p => (double)-p.PowerOutput);
                result = result with { Networks = [.. result.Networks, new Obs.PowerNetwork { Id = NetId(net),
                    Producers = (uint)net.powerComps.Count(p => p.Props.PowerConsumption < 0),
                    Consumers = (uint)net.powerComps.Count(p => p.Props.PowerConsumption > 0),
                    Batteries = (uint)net.batteryComps.Count, Transmitters = (uint)net.transmitters.Count, Connectors = (uint)net.connectors.Count,
                    GenerationW = Finite(generation), ConsumptionW = Finite(consumption), NetW = Finite(generation - consumption),
                    StoredWattDays = Finite(net.CurrentStoredEnergy()),
                    CapacityWattDays = Finite(net.batteryComps.Sum(b => (double)b.Props.storedEnergyMax)),
                    HasSource = net.powerComps.Any(p => p.Props.PowerConsumption < 0),
                    HasActiveSource = net.powerComps.Any(p => p.PowerOn && p.PowerOutput > 0),
                    }] };
            }
            foreach (var geyser in geysers) {
                // A geyser is free for a geothermal generator only while no
                // harvester, blueprint, frame or other building stands on it.
                var cells = geyser.OccupiedRect().Cells.ToList();
                var occupied = geyser.harvester != null || cells.Any(c => c.GetThingList(map).Any(t => t != geyser && (t.def.category == ThingCategory.Building || t.def.IsBlueprint || t.def.IsFrame)));
                var row = new Obs.SteamGeyser { Geyser = NativeRef.Thing(geyser), Occupied = occupied };
                foreach (var cell in cells) row = row with { Cells = [.. row.Cells, Cell(cell)] };
                result = result with { Geysers = [.. result.Geysers, row] };
            }
            return result;
        }

        private static string NetId(PowerNet net) => NativeBuildingObservationTools.NetId(net);

        private static Obs.ColonyFactsSnapshot ReadProduction(Obs.ColonyFactsSnapshot result, Map map, List<Pawn> people,
            List<Thing> things, Func<Thing, bool> reachable, Func<ThingDef, bool> humanFood)
        {
            var benches = things.OfType<Building_WorkTable>().Where(b => b.Faction == Faction.OfPlayer && reachable(b)
                && b.def.AllRecipes.Any(r => r.products.Any(p => humanFood(p.thingDef)))).OrderBy(b => b.thingIDNumber).ToList();
            var haulers = NativeResourceSourcesTool.Haulers(map);
            foreach (var bench in benches) {
                var row = new Obs.CookingFacts { Bench = NativeBuildingObservationTools.Ref(bench), BenchSnapshot = NativeProductionBills.Snapshot(bench,bench,result.Context ?? new Common.ObservationContext()),
                    Usable = !bench.IsBurning() && (bench.TryGetComp<CompPowerTrader>() == null || bench.TryGetComp<CompPowerTrader>().PowerOn)
                        && (bench.TryGetComp<CompRefuelable>() == null || bench.TryGetComp<CompRefuelable>().HasFuel) };
                var recipes = bench.def.AllRecipes.Where(r => r.products.Any(p => humanFood(p.thingDef))).OrderBy(r => r.defName).ToList();
                foreach (var recipe in recipes) {
                    row = row with { Recipes = [.. row.Recipes, NativeProductionBills.RecipeRow(bench,recipe)] };
                    var production = new Obs.FoodProduction { Recipe = recipe.defName, Available = NativeProductionBills.Recipe(bench,recipe) };
                    foreach(var product in recipe.products){
                        var food=new Obs.FoodProduct{DefName=product.thingDef.defName,Count=product.count,Edible=humanFood(product.thingDef),NutritionDemandPerDay=result.NutritionPerDay};
                        food = food with { Storable = NativeResourceSourcesTool.Capacity(map,product.thingDef,haulers,out var stored)+stored };
                        production = production with { Products = [.. production.Products, food] };
                    }
                    row = row with { Production = [.. row.Production, production] };
                }
                for(var index=0;index<bench.BillStack.Count;index++)row = row with { Bills = [.. row.Bills, NativeProductionBills.BillRow(bench.BillStack.Bills[index],index)] };
                var cookingRoom = bench.GetRoom();
                if (cookingRoom != null) row = row with { Room = NativeRef.Room(cookingRoom) };
                if (NativeAutoRefuel.Comp(bench) is CompRefuelable refuel) row = row with { AutoRefuel = refuel.allowAutoRefuel };
                result = result with { Cooking = [.. result.Cooking, row] };
            }
            foreach(var bench in things.Where(t=>t.Faction==Faction.OfPlayer&&t is IBillGiver&&reachable(t)&&t.def.AllRecipes.Any(NativeRecipeRoles.ButcherFlesh)).OrderBy(t=>t.thingIDNumber)){
                var giver=(IBillGiver)bench;
                var row=new Obs.ButcheringFacts{Bench=NativeBuildingObservationTools.Ref(bench),BenchSnapshot=NativeProductionBills.Snapshot(bench,giver,result.Context ?? new Common.ObservationContext()),Usable=NativeProductionBills.Usable(bench)};
                row = HumanFoodFacts.Fill(row,bench);
                foreach(var recipe in bench.def.AllRecipes.Where(NativeRecipeRoles.ButcherFlesh))row = row with { Recipes = [.. row.Recipes, NativeProductionBills.RecipeRow(bench,recipe)] };
                for(var index=0;index<giver.BillStack.Count;index++)row = row with { Bills = [.. row.Bills, NativeProductionBills.BillRow(giver.BillStack.Bills[index],index)] };
                var butcherRoom = bench.GetRoom();
                if (butcherRoom != null) row = row with { Room = NativeRef.Room(butcherRoom) };
                result = result with { Butchering = [.. result.Butchering, row] };
            }
            return result;
        }

        private static Obs.PlanningFacts Planning(Map map, IntVec3 center, Obs.ColonyFactsRequest request, Common.ObservationContext context)
        {
            var result = new Obs.PlanningFacts { };
            var began = System.Diagnostics.Stopwatch.GetTimestamp();
            try { result = result with { Gear = NativeGearFacts.Read(map, context) }; }
            catch (Exception error) { ModLog.Error("observe", "Colony facts section failed: " + error); result = result with { Issues = [.. result.Issues, Issue("gear", Common.UnavailableReason.ReadFailed, "Complete native loadout upkeep is unavailable.")] }; }
            ObservationWork.Detail("cf.gear", System.Diagnostics.Stopwatch.GetTimestamp() - began);
            began = System.Diagnostics.Stopwatch.GetTimestamp();
            result = result with { Crops = [.. result.Crops, .. Crops(map)] };
            ObservationWork.Detail("cf.crops", System.Diagnostics.Stopwatch.GetTimestamp() - began);
            try { result = result with { Environment = Environment(map) }; }
            catch (Exception error) { ModLog.Error("observe", "Colony facts section failed: " + error); result = result with { Issues = [.. result.Issues, Issue("environment", Common.UnavailableReason.ReadFailed, "Controlled-environment growing facts are unavailable.")] }; }
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
                row = row with { DietAllowed = !product.IsFungus || !ModsConfig.IdeologyActive || !people.Any(p =>
                    p.Ideo != null && p.Ideo.PreceptsListForReading.Any(precept => precept.def.comps
                        .OfType<PreceptComp_SelfTookMemoryThought>().Any(comp =>
                            (comp.eventDef == HistoryEventDefOf.AteFungus || comp.eventDef == HistoryEventDefOf.AteFungusAsIngredient)
                            && comp.thought.stages.Any(stage => stage.baseMoodEffect < 0)))) };
                row = row with { NutritionDemandPerDay = Finite(demand + animals.Where(p => p.RaceProps.CanEverEat(product)
                    && p.foodRestriction?.GetCurrentRespectedRestriction(p)?.filter.Allows(product) != false)
                    .Sum(p => GameTime.PerDay(p.needs.food.FoodFallPerTickAssumingCategory(HungerCategory.Fed, true)))) };
                result.Add(row);
            }
            return result;
        }

        // Sun lamps, plant growers and rooms inside the planning region plus every
        // power network's headroom split by source. Lamp growth cells are the
        // native Building_SunLamp radius, not the glow radius, so the controller
        // never plants where the game would not grow.
        // The native sun lamp class is internal; its def names the class and carries the growth radius as specialDisplayRadius.
        private static bool IsSunLamp(Building b) => b.def.thingClass?.Name == "Building_SunLamp" && b.def.specialDisplayRadius > 0f;
        private static Obs.ControlledEnvironment Environment(Map map)
        {
            var result = new Obs.ControlledEnvironment { Daylight = GenCelestial.CurCelestialSunGlow(map) >= 0.3f };
            var weather = map.weatherManager?.curWeather?.defName;
            if (!string.IsNullOrEmpty(weather)) result = result with { Weather = weather };
            string? NetId(CompPowerTrader? power) => power?.PowerNet == null ? null : power.PowerNet.GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var rooms = new Dictionary<int, Room>();
            void Note(Room? room) { if (room != null && !room.PsychologicallyOutdoors && room.ProperRoom) rooms[room.ID] = room; }
            var buildings = map.listerBuildings.allBuildingsColonist.OrderBy(b => b.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
            foreach (var building in buildings) {
                var power = building.TryGetComp<CompPowerTrader>();
                var room = building.GetRoom();
                if (IsSunLamp(building)) { var lamp = building;
                    var row = new Obs.GrowLight { Building = NativeBuildingObservationTools.Ref(lamp) };
                    if (power != null) { row = row with { Powered = power.PowerOn }; var id = NetId(power); if (id != null) row = row with { PowerNetId = id }; }
                    else row = row with { Issues = [.. row.Issues, Issue("powered", Common.UnavailableReason.NotApplicable, "Lamp has no power trader.")] };
                    var schedule = lamp.TryGetComp<CompSchedule>();
                    row = row with { LitNow = (power == null || power.PowerOn) && (schedule == null || schedule.Allowed) };
                    foreach (var c in GenRadial.RadialCellsAround(lamp.Position, lamp.def.specialDisplayRadius, true).Where(c => c.InBounds(map))) row = row with { GrowthCells = [.. row.GrowthCells, Cell(c)] };
                    if (room != null) { row = row with { Room = NativeRef.Room(room) }; Note(room); }
                    result = result with { Lights = [.. result.Lights, row] };
                } else if (building is Building_PlantGrower grower) {
                    var row = new Obs.PlantGrower { Building = NativeBuildingObservationTools.Ref(grower),
                        CanSow = grower.CanAcceptSowNow() };
                    var crop = grower.GetPlantDefToGrow();
                    if (crop != null) row = row with { CropDefName = crop.defName };
                    if (power != null) { row = row with { Powered = power.PowerOn }; var id = NetId(power); if (id != null) row = row with { PowerNetId = id }; }
                    foreach (var c in ((IPlantToGrowSettable)grower).Cells) row = row with { PlantCells = [.. row.PlantCells, Cell(c)] };
                    if (room != null) { row = row with { Room = NativeRef.Room(room) }; Note(room); }
                    result = result with { Growers = [.. result.Growers, row] };
                }
            }
            foreach (var room in map.regionGrid.AllRooms) if (room.Cells.Any(c => !c.Fogged(map))) Note(room);
            foreach (var room in rooms.Values.OrderBy(r => r.ID)) {
                var row = new Obs.GrowRoom { Room = NativeRef.Room(room), TemperatureC = Finite(room.Temperature), CellCount = (uint)room.CellCount,
                    OpenRoofCount = (uint)room.OpenRoofCount, ProperRoom = room.ProperRoom, PsychologicallyOutdoors = room.PsychologicallyOutdoors };
                uint lit = 0;
                foreach (var c in room.Cells) if (map.glowGrid.GroundGlowAt(c) >= 0.3f) lit++;
                row = row with { LitCells = lit };
                result = result with { Rooms = [.. result.Rooms, row] };
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
                row = row with { GenerationW = generation }; row = row with { SolarW = solar }; row = row with { WindW = wind }; row = row with { ConsumptionW = consumption };
                row = row with { StoredWattDays = Finite(net.CurrentStoredEnergy()) }; row = row with { CapacityWattDays = capacity };
                result = result with { Networks = [.. result.Networks, row] };
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
            result = result with { AnimalIds = [.. result.AnimalIds, .. source.animalIds] };
            foreach (var crop in source.crops) {
                var row = new Obs.CropForecast { Zone = NativeRef.Of(crop.id.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
                if (crop.crop != null) row = row with { Crop = crop.crop };
                if (crop.sowWork.HasValue) row = row with { SowWork = Finite(crop.sowWork.Value) };
                if (crop.harvestWork.HasValue) row = row with { HarvestWork = Finite(crop.harvestWork.Value) };
                if (crop.maturePlants.HasValue) row = row with { MaturePlants = checked((uint)crop.maturePlants.Value) };
                if (crop.stalledPlants.HasValue) row = row with { StalledPlants = checked((uint)crop.stalledPlants.Value) };
                if (crop.standingYield.HasValue) row = row with { StandingYield = Finite(crop.standingYield.Value) };
                if (crop.product != null) row = row with { Product = crop.product };
                result = result with { Crops = [.. result.Crops, row] };
            }
            foreach (var patient in source.patients) {
                var row = new Obs.PatientForecast { PawnId = patient.id };
                if (patient.bleedRatePerDay.HasValue) row = row with { BleedRatePerDay = Finite(patient.bleedRatePerDay.Value) };
                if (patient.hoursUntilDeathFromBloodLoss.HasValue) row = row with { HoursUntilDeathFromBloodLoss = Finite(patient.hoursUntilDeathFromBloodLoss.Value) };
                if (patient.mood.HasValue) row = row with { Mood = Finite(patient.mood.Value) };
                if (patient.moodTarget.HasValue) row = row with { MoodTarget = Finite(patient.moodTarget.Value) };
                if (patient.minorBreakThreshold.HasValue) row = row with { MinorBreakThreshold = Finite(patient.minorBreakThreshold.Value) };
                if (patient.majorBreakThreshold.HasValue) row = row with { MajorBreakThreshold = Finite(patient.majorBreakThreshold.Value) };
                if (patient.extremeBreakThreshold.HasValue) row = row with { ExtremeBreakThreshold = Finite(patient.extremeBreakThreshold.Value) };
                result = result with { Patients = [.. result.Patients, row] };
            }
            return result;
        }
        private static Obs.FoodSupplyFacts Food(FoodSupplyFacts.Snapshot source)
        {
            var result = new Obs.FoodSupplyFacts { };
            if (source.larder != null) {
                var corpses = new List<Obs.CorpseHandling>();
                foreach (var corpse in source.larder.Corpses) {
                    var row = new Obs.CorpseHandling { StockId = corpse.ID, Cell = Cell(corpse.Cell), FrozenDestination = corpse.FrozenDestination, InStorage = corpse.InStorage };
                    if (corpse.Hauler != null) row = row with { HaulerId = corpse.Hauler };
                    corpses.Add(row);
                }
                result = result with { Larder = new Obs.FoodLarderFacts { RawMeatNutrition = Finite(source.larder.RawMeatNutrition), CookDemandNutrition = Finite(source.larder.CookDemandNutrition), Corpses = [.. corpses] } };
            }
            foreach (var consumer in source.consumers)
                result = result with { Consumers = [.. result.Consumers, new Obs.FoodConsumer { PawnId = consumer.id, NutritionPerDay = Finite(consumer.nutritionPerDay), HumanMeatAcceptable = consumer.humanMeatAcceptable }] };
            foreach (var stock in source.stocks) {
                // The stock's thing is a things table row.
                var row = new Obs.FoodStock { Item = NativeRef.Thing(stock.thing!), Nutrition = Finite(stock.nutrition) };
                row = row with { Eaters = [.. row.Eaters, .. NativeRef.All(stock.eaters!)] };
                if (stock.holder != null) row = row with { Holder = NativeRef.Of(stock.holder) };
                result = result with { Stocks = [.. result.Stocks, row] };
            }
            return result;
        }
        private static Common.Unavailable Unavailable(Common.UnavailableReason reason, string detail) => new Common.Unavailable { Reason = reason, Detail = detail };
        private static Common.Unavailable Unsupported(string detail) => Unavailable(Common.UnavailableReason.Unsupported, detail);
        private static Obs.ReadIssue Issue(string field, Common.UnavailableReason reason, string detail) => new Obs.ReadIssue { Field = field, Unavailable = Unavailable(reason, detail) };
    }
}
