using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimGovernor.Native
{
    internal static class NativeColonyObservationTools
    {
        // On the game thread: the typed core of the tool, shared with the in-process facade.
        internal static RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot HandleReadColonyFacts(RimGovernor.Brain.GameOrders.Observations.ColonyFactsRequest parsed)
        {
            if (!Validate(parsed, out var invalidRequest)) throw NativeFaults.Refused(invalidRequest);
            if (!GameThread.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out var invalid))
                throw NativeFaults.Refused(invalid);
            try { return Read(map, parsed, context); }
            catch (Exception error) { ModLog.Error("observe", "Colony facts read failed: " + error); throw NativeFaults.Unavailable(Unavailable(RimGovernor.Brain.GameFacts.Common.UnavailableReason.ReadFailed, PlacementPreviewOperation.Diagnostic("Native colony facts could not be read completely: " + error.GetType().Name + ": " + error.Message))); }
        }

        internal static bool Validate(RimGovernor.Brain.GameOrders.Observations.ColonyFactsRequest request, out RimGovernor.Brain.GameFacts.Common.Failure failure)
        {
            failure = GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Identity required.");
            return request?.Scope?.ExpectedIdentity != null;
        }

        // The colony facts as a frame section: a read that throws fails the frame.
        internal static RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot Read(Map map, RimGovernor.Brain.GameOrders.Observations.ColonyFactsRequest request, RimGovernor.Brain.GameFacts.Common.ObservationContext context, uint? routePathBudgetMs = null)
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
            // Temperature recovery observes every eligible sleeping room, even when danger makes its beds unreachable.
            // Capacity above remains the reachable sleeping capacity; temperature must not hide a cold room.
            var sleepingRooms = things.OfType<Building_Bed>()
                .Where(b => b.Faction == player && b.def.building.bed_humanlike && !b.ForPrisoners && !b.Medical)
                .Select(b => b.GetRoom() ?? throw new InvalidOperationException("Sleeping bed stands in no room."))
                .Distinct().ToList();
            Span("cf.beds");
            var nutrition = items.Where(t => humanFood(t.def) && t.IngestibleNow && NativeFoodPolicy.Eaters(people, t.def).All(p => p.WillEat(t)))
                .Sum(t => (double)t.stackCount * NativeFoodPolicy.Eaters(people, t.def).Min(p => FoodUtility.NutritionForEater(p, t)));
            // Raw native demand/runway remains distinct from the controller's
            // diet, held-food, rot and competing-animal forecast.
            var demand = people.Sum(p => p.needs?.food == null ? 0.0 : GameTime.PerDay((double)p.needs.food.FoodFallPerTickAssumingCategory(HungerCategory.Fed, true)));
            Span("cf.nutrition");
            var foodSupply = Food(FoodSupplyFacts.Read(people, things.Where(FoodSupplyFacts.SharedFood).ToList()));
            Span("cf.foodSupply");
            // The forecast is the cf.fc.* spans: needs, facts, the coupled read's own sub-read spans, then its assembly.
            var needs = NativeNeedsForecast.Read(map, people);
            Span("cf.fc.needs");
            var forecastFacts = ForecastFacts.Read(map, people, things);
            Span("cf.fc.facts");
            var coupled = NativeCoupledForecast.Read(map, context, needs);
            mark = System.Diagnostics.Stopwatch.GetTimestamp();
            var forecast = Forecast(forecastFacts, needs, coupled);
            Span("cf.fc.wrap");
            var upkeep = ReadUpkeep(map, things, routePathBudgetMs);
            Span("cf.upkeep");
            var threat = ReadThreat(map);
            var development = new RimGovernor.Brain.GameFacts.Observations.DevelopmentSection { Outcome = ReadPower(map) };
            Span("cf.power");
            var foodChannels = NativeFoodChannels.Read(map, center, workers, humanFood);
            Span("cf.foodChannels");
            var deepResources = NativeDeepResources.Read(map);
            var policies = NativePolicyFacts.Read(map);
            var biotech = NativeBiotechColony.Read(map);
            var royalty = NativeRoyaltyColony.Read();
            var anomaly = NativeAnomalyColony.Read();
            Span("cf.deep");
            // No eligible indoor sleeping place is a named state, not a missing read.
            RimGovernor.Brain.GameFacts.Observations.SleepingTemperature sleeping = new RimGovernor.Brain.GameFacts.Observations.SleepingTemperature.NoIndoorBed();
            if (sleepingRooms.Count > 0 && sleepingRooms.All(room => room.ProperRoom && !room.IsDoorway
                && !room.UsesOutdoorTemperature && !room.PsychologicallyOutdoors && !room.TouchesMapEdge && room.OpenRoofCount == 0))
            {
                var temperatures = sleepingRooms.Select(room => Finite(room.Temperature)).ToList();
                sleeping = new RimGovernor.Brain.GameFacts.Observations.SleepingTemperature.Indoor(temperatures.Min(), temperatures.Max());
            }
            var resources = stock.Select(group => new RimGovernor.Brain.GameFacts.Observations.Quantity { DefName = group.Key.defName, Units = group.Sum(t => (long)t.stackCount) }).ToList();
            Span("cf.resources");
            var workResources = RimGovernor.Brain.Domain.EquatableArray.From(map.mapPawns.AllPawnsSpawned
                .OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).Select(p =>
                {
                    var execution = NativePawnActivities.Read(p);
                    var carried = p.carryTracker?.CarriedThing;
                    return new RimGovernor.Brain.GameFacts.Observations.WorkResourcePawn(p.GetUniqueLoadID(), execution?.Assignment ?? "", execution?.Revision ?? 0,
                        execution?.State ?? "", carried?.GetUniqueLoadID() ?? "", carried?.def.defName ?? "", carried?.stackCount ?? 0)
                        { LocalHour = GenLocalDate.HourFloat(map), Bill = p.CurJob?.bill?.GetUniqueLoadID() ?? "",
                            Target = p.CurJob?.targetA.Thing?.GetUniqueLoadID() ?? "",
                            Unfinished = (p.CurJob?.targetB.Thing as UnfinishedThing)?.GetUniqueLoadID() ?? "" };
                }));
            var eventLoot = EventLootFacts.Read(map, things, reachable);
            Span("cf.eventLoot");
            var productionBenches = ReadProduction(context, map, things, reachable, humanFood);
            Span("cf.production");
            var joiners = NativeJoinerLetters.Snapshot();
            var dialog = ChoiceDialogTools.Pending();
            var conditions = new List<GameCondition>();
            map.gameConditionManager.GetAllGameConditionsAffectingMap(map, conditions);
            Span("cf.dialogs");
            var recovery = NativeRecoveryFacts.Read(map, context);
            Span("cf.recovery");
            var waste = HomeWasteTools.Project(map);
            Span("cf.waste");
            RimGovernor.Brain.GameFacts.Observations.FoodClimate foodClimate;
            {
                var (season, dayOfYear) = GrowingCalendar.Calendar(map);
                var year = GrowingCalendar.GrowingYear(map);
                var remaining = GrowingCalendar.Until(year, 0, false);
                foodClimate = new RimGovernor.Brain.GameFacts.Observations.FoodClimate { GrowingYear = [.. year], GrowingDaysRemaining = remaining,
                GrowingDaysUntil = GrowingCalendar.Until(year, 0, true), NonGrowingDays = remaining == year.Length ? 0 : GrowingCalendar.Until(year, remaining, true), Season = season, DayOfYear = dayOfYear,
                SowingNow = !OutdoorsPermanentlyDark(map) && DefDatabase<ThingDef>.AllDefsListForReading.Any(d => d.plant != null && d.plant.Sowable && d.plant.harvestedThingDef?.IsNutritionGivingIngestible == true
                    && (d.plant.sowResearchPrerequisites == null || d.plant.sowResearchPrerequisites.All(r => r.IsFinished)) && PlantUtility.GrowthSeasonNow(map, d)),
                GrowingDays = GenTemperature.TwelfthsInAverageTemperatureRange(map.Tile,Plant.DefaultMinOptimalGrowthTemperature,Plant.DefaultMaxOptimalGrowthTemperature).Count * GenDate.DaysPerTwelfth }; }
            Span("cf.climate");
            // The pending harvest, wood and hunt values are required: a read that cannot make them fails the colony read.
            var acquisition = NativePlantAcquisition.Read(map, center, humanFood, request, context);
            Span("cf.acquisition");
            var gear = NativeGearFacts.Read(map);
            Span("cf.gear");
            var environment = Environment(map);
            Span("cf.environment");
            return new RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot { Context = context, Gear = gear, ColonistCount = (uint)people.Count,
                ResourceThings = RimGovernor.Brain.Domain.EquatableArray.From(items.Select(t => t.GetUniqueLoadID()).OrderBy(id => id, StringComparer.Ordinal)),
                WorkResources = workResources, Crops = [.. Crops(map)],
                WorkerCount = (uint)workers.Count, MapSize = Size(map), Biome = map.Biome.defName,
                PlayerTechLevel = (RimGovernor.Brain.GameFacts.Defs.TechLevel)(int)player.def.techLevel,
                FermentingBarrels = (uint)things.OfType<Building_FermentingBarrel>().Count(b => b.Faction == player && !b.IsForbidden(player) && reachable(b)),
                BedCapacity = checked((uint)beds.Sum(b => b.SleepingSlotsCount)), IndoorSleepingCapacity = checked((uint)indoorBeds.Sum(b => b.SleepingSlotsCount)),
                FoodNutrition = Finite(nutrition), NutritionPerDay = Finite(demand), OutdoorTemperatureC = Finite(map.mapTemperature.OutdoorTemp),
                FoodRunway = demand > 0 ? new RimGovernor.Brain.GameFacts.Observations.FoodRunway.Days { Value = Finite(nutrition / demand) } : new RimGovernor.Brain.GameFacts.Observations.FoodRunway.NoDemand(),
                SleepingTemperature = sleeping,
                DeliveryLedger = NativeDeliveryLedger.Read(),
                FoodSupply = foodSupply, Forecast = forecast, Upkeep = upkeep, Threat = threat, Development = development,
                FoodChannels = foodChannels, DeepResources = deepResources, Policies = policies,
                Biotech = biotech, Royalty = royalty, Anomaly = anomaly,
                Resources = [.. resources], EventLoot = eventLoot, ProductionBenches = [.. productionBenches],
                JoinerLetters = [.. joiners], Dialog = dialog != null ? ChoiceDialogTools.Snapshot(dialog) : null,
                Environment = [.. conditions.Select(EnvironmentCondition)],
                Recovery = recovery, Waste = waste, Blight = NativeBlightCensus.Read(map, things), FoodClimate = foodClimate, ControlledEnvironment = environment,

                Acquisition = [.. acquisition.Rows], PendingFoodNutrition = acquisition.PendingFoodNutrition,
                PendingHunts = acquisition.PendingHunts, HuntCensus = acquisition.HuntCensus,
                };
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
        // the native remaining duration so the brain can plan for the length of a
        // fallout or volcanic winter. A permanent condition is untimed;
        // its class and label are the GameConditionDef row's.
        internal static RimGovernor.Brain.GameFacts.Observations.EnvironmentCondition EnvironmentCondition(GameCondition condition) =>
            new() { Id = condition.uniqueID.ToString(System.Globalization.CultureInfo.InvariantCulture), DefName = condition.def.defName,
                Duration = condition.Permanent ? new RimGovernor.Brain.GameFacts.Observations.ConditionDuration.Permanent() : new RimGovernor.Brain.GameFacts.Observations.ConditionDuration.Timed { Ticks = Math.Max(0, condition.TicksLeft) } };

        // Colony wealth and raid points: the WealthWatcher split (its
        // own lazy recount; never ForceRecount on a read path), the wealth
        // the storyteller scales by and the points a default threat incident
        // would draw for this map now, with the adaptation and difficulty
        // factors already inside that figure. A failure fails the census.
        internal static RimGovernor.Brain.GameFacts.Observations.ThreatSection ReadThreat(Map map)
        {
            var wealth = map.wealthWatcher;
            var facts = new RimGovernor.Brain.GameFacts.Observations.ThreatFacts {
                WealthItems = Finite(wealth.WealthItems), WealthBuildings = Finite(wealth.WealthBuildings), WealthPawns = Finite(wealth.WealthPawns),
                WealthTotal = Finite(wealth.WealthTotal),
                RaidPoints = Finite(StorytellerUtility.DefaultThreatPointsNow(map)),
                HostileFactions = [.. HostileRaidFactions()], Raids = [.. NativeDefenseObservationTools.RaidTracks(map)],
                Storyteller = Find.Storyteller.def.defName, DaysPassed = Finite(GenDate.DaysPassedFloat),
                Disease = NativeDiseaseFacts.Read(map),
                Trade = NativeTradeVisitFacts.Read(map),
            };
            return new RimGovernor.Brain.GameFacts.Observations.ThreatSection { Observed = facts };
        }

        // The def names of the factions a raid can come from: visible, undefeated, hostile to the player, and with a Combat pawn group maker.
        private static IEnumerable<string> HostileRaidFactions() =>
            NativeThreatClassifier.HostileRaidFactions(Find.FactionManager.AllFactionsVisible, f => new RaidFactionFacts {
                Def = f.def.defName, Player = f.IsPlayer, Defeated = f.defeated, Hostile = f.HostileTo(Faction.OfPlayer),
                Combat = f.def.pawnGroupMakers != null && f.def.pawnGroupMakers.Any(maker => maker.kindDef == PawnGroupKindDefOf.Combat) });

        private static RimGovernor.Brain.GameFacts.Observations.UpkeepFacts ReadUpkeep(Map map, List<Thing> things, uint? routePathBudgetMs)
        {
            var began = System.Diagnostics.Stopwatch.GetTimestamp();
            var home = NativeUpkeepFacts.ReadHomeCoverage(map);
            ObservationWork.Detail("cf.upkeep.home_coverage", System.Diagnostics.Stopwatch.GetTimestamp() - began);
            began = System.Diagnostics.Stopwatch.GetTimestamp();
            var comfort = ComfortFacts.ReadProtocol(map);
            ObservationWork.Detail("cf.upkeep.comfort", System.Diagnostics.Stopwatch.GetTimestamp() - began);
            var sets = NativeUpkeepFacts.ThingSets.Of(map, things);
            began = System.Diagnostics.Stopwatch.GetTimestamp();
            var lighting = NativeUpkeepFacts.ReadLighting(map, sets);
            ObservationWork.Detail("cf.upkeep.lighting", System.Diagnostics.Stopwatch.GetTimestamp() - began);
            return NativeUpkeepFacts.Read(map, sets, home, comfort, lighting, routePathBudgetMs);
        }

        private static RimGovernor.Brain.GameFacts.Observations.DevelopmentFacts ReadPower(Map map)
        {
            var nets = map.powerNetManager.AllNetsListForReading.OrderBy(n => n.GetHashCode()).ToList();
            var geysers = map.listerThings.ThingsOfDef(ThingDefOf.SteamGeyser).OfType<Building_SteamGeyser>().OrderBy(g => g.thingIDNumber).ToList();
            var networks = new List<RimGovernor.Brain.GameFacts.Observations.PowerNetwork>(nets.Count);
            var geyserRows = new List<RimGovernor.Brain.GameFacts.Observations.SteamGeyser>(geysers.Count);
            // Archived letters survive dismissal and saves. Use the game's own
            // translated label rather than matching English message prose.
            var shortLabel = "LetterLabelShortCircuit".Translate().CapitalizeFirst().ToString();
            var incidents = Find.Archive.ArchivablesListForReading.OfType<Letter>()
                .Concat(Find.LetterStack.LettersListForReading)
                .Where(l => l.Label.ToString() == shortLabel && l.lookTargets != null
                    && l.lookTargets.targets.Any(t => t.Map == map))
                .Select(l => l.arrivalTick).ToList();
            RimGovernor.Brain.GameFacts.Observations.ObservedShortCircuit shortCircuit = incidents.Count > 0 ? new RimGovernor.Brain.GameFacts.Observations.ObservedShortCircuit.Seen { Tick = incidents.Max() } : new RimGovernor.Brain.GameFacts.Observations.ObservedShortCircuit.NoneSeen();
            foreach (var net in nets) {
                var generation = net.powerComps.Where(p => p.PowerOn && p.PowerOutput > 0).Sum(p => (double)p.PowerOutput);
                var consumption = net.powerComps.Where(p => p.PowerOn && p.PowerOutput < 0).Sum(p => (double)-p.PowerOutput);
                networks.Add(new RimGovernor.Brain.GameFacts.Observations.PowerNetwork { Id = NetId(net),
                    GenerationW = Finite(generation), ConsumptionW = Finite(consumption),
                    StoredWD = Finite(net.CurrentStoredEnergy()) });
            }
            foreach (var geyser in geysers) {
                // A geyser is free for a geothermal generator only while no
                // harvester, blueprint, frame or other building stands on it.
                var cells = geyser.OccupiedRect().Cells.ToList();
                var occupied = geyser.harvester != null || cells.Any(c => c.GetThingList(map).Any(t => t != geyser && (t.def.category == ThingCategory.Building || t.def.IsBlueprint || t.def.IsFrame)));
                geyserRows.Add(new RimGovernor.Brain.GameFacts.Observations.SteamGeyser { Id = geyser.GetUniqueLoadID(), Cell = new RimGovernor.Brain.Domain.Cell(geyser.Position.x, geyser.Position.z), Occupied = occupied, Cells = [.. cells.Select(c => new RimGovernor.Brain.Domain.Cell(c.x, c.z))] });
            }
            return new RimGovernor.Brain.GameFacts.Observations.DevelopmentFacts { ShortCircuit = shortCircuit, Networks = [.. networks], Geysers = [.. geyserRows] };
        }

        private static string NetId(PowerNet net) => NativeBuildingObservationTools.NetId(net);

        private static List<RimGovernor.Brain.GameFacts.Observations.ProductionBenchFacts> ReadProduction(RimGovernor.Brain.GameFacts.Common.ObservationContext context, Map map,
            List<Thing> things, Func<Thing, bool> reachable, Func<ThingDef, bool> humanFood)
        {
            var rows = new List<RimGovernor.Brain.GameFacts.Observations.ProductionBenchFacts>();
            var haulers = NativeResourceSourcesTool.Haulers(map);
            foreach (var bench in things.Where(t => t.Faction == Faction.OfPlayer && t is IBillGiver && reachable(t)).OrderBy(t => t.thingIDNumber))
            {
                var cookingRecipes = bench is Building_WorkTable
                    ? bench.def.AllRecipes.Where(r => r.products.Any(p => humanFood(p.thingDef))).OrderBy(r => r.defName).ToList()
                    : new List<RecipeDef>();
                var butcherRecipes = bench.def.AllRecipes.Where(NativeRecipeRoles.ButcherFlesh).OrderBy(r => r.defName).ToList();
                if (cookingRecipes.Count == 0 && butcherRecipes.Count == 0) continue;
                var row = new RimGovernor.Brain.GameFacts.Observations.ProductionBenchFacts
                {
                    Bench = NativeBillsObservationTools.Stack(bench, context),
                    Cooking = new RimGovernor.Brain.GameFacts.Observations.CookingProduction.NotSupported(),
                    Butchering = new RimGovernor.Brain.GameFacts.Observations.ButcheringProduction.NotSupported(),
                };
                if (cookingRecipes.Count > 0)
                {
                    var cooking = new RimGovernor.Brain.GameFacts.Observations.CookingProduction.Supported
                    {
                        Usable = !bench.IsBurning()
                            && (bench.TryGetComp<CompPowerTrader>() == null || bench.TryGetComp<CompPowerTrader>().PowerOn)
                            && (bench.TryGetComp<CompRefuelable>() == null || bench.TryGetComp<CompRefuelable>().HasFuel),
                        Recipes = [], Production = [],
                    };
                    foreach (var recipe in cookingRecipes)
                    {
                        cooking = cooking with { Recipes = [.. cooking.Recipes, NativeProductionBills.RecipeRow(bench, recipe)] };
                        var products = new List<RimGovernor.Brain.GameFacts.Observations.FoodProduct>();
                        foreach (var product in recipe.products)
                        {
                            var capacity = NativeResourceSourcesTool.Capacity(map, product.thingDef, haulers, out var stored);
                            products.Add(new RimGovernor.Brain.GameFacts.Observations.FoodProduct { DefName = product.thingDef.defName, Edible = humanFood(product.thingDef), Storable = capacity + stored });
                        }
                        cooking = cooking with { Production = [.. cooking.Production, new RimGovernor.Brain.GameFacts.Observations.FoodProduction
                        {
                            Recipe = recipe.defName, Available = NativeProductionBills.Recipe(bench, recipe), Products = [.. products],
                        }] };
                    }
                    row = row with { Cooking = cooking };
                }
                if (butcherRecipes.Count > 0)
                {
                    var butchering = new RimGovernor.Brain.GameFacts.Observations.ButcheringProduction.Supported
                    {
                        Usable = NativeProductionBills.Usable(bench),
                        Recipes = [.. butcherRecipes.Select(recipe => NativeProductionBills.RecipeRow(bench, recipe))],
                        HumanButchers = [], HumanCorpseMeat = [],
                    };
                    row = row with { Butchering = HumanFoodFacts.Fill(butchering, bench) };
                }
                rows.Add(row);
            }
            return rows;
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

        internal static List<RimGovernor.Brain.GameFacts.Observations.EdibleCrop> Crops(Map map)
        {
            var result = new List<RimGovernor.Brain.GameFacts.Observations.EdibleCrop>();
            var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead).ToList();
            var demand = people.Sum(p => p.needs?.food == null ? 0f : GameTime.PerDay(p.needs.food.FoodFallPerTickAssumingCategory(HungerCategory.Fed, true)));
            var animals = map.mapPawns.AllPawnsSpawned.Where(p => !p.Dead && p.RaceProps.Animal
                && p.Faction == Faction.OfPlayerSilentFail && p.needs?.food != null).ToList();
            foreach (var def in CropDefs.Value) {
                var product = def.plant.harvestedThingDef;
                if (product == null || !NativeFoodPolicy.IsFood(product)) continue;
                var dietAllowed = !product.IsFungus || !ModsConfig.IdeologyActive || !people.Any(p =>
                    p.Ideo != null && p.Ideo.PreceptsListForReading.Any(precept => precept.def.comps
                        .OfType<PreceptComp_SelfTookMemoryThought>().Any(comp =>
                            (comp.eventDef == HistoryEventDefOf.AteFungus || comp.eventDef == HistoryEventDefOf.AteFungusAsIngredient)
                            && comp.thought.stages.Any(stage => stage.baseMoodEffect < 0))));
                var nutritionDemand = Finite(demand + animals.Where(p => p.RaceProps.CanEverEat(product)
                    && p.foodRestriction?.GetCurrentRespectedRestriction(p)?.filter.Allows(product) != false)
                    .Sum(p => GameTime.PerDay(p.needs.food.FoodFallPerTickAssumingCategory(HungerCategory.Fed, true))));
                result.Add(new RimGovernor.Brain.GameFacts.Observations.EdibleCrop { DefName = def.defName, DietAllowed = dietAllowed, NutritionDemandPerDay = nutritionDemand });
            }
            return result;
        }

        // Sun lamps, plant growers and rooms inside the planning region plus every
        // power network's headroom split by source. Lamp growth cells are the
        // native Building_SunLamp radius, not the glow radius, so the controller
        // never plants where the game would not grow.
        // The native sun lamp class is internal; its def names the class and carries the growth radius as specialDisplayRadius.
        private static bool IsSunLamp(Building b) => b.def.thingClass?.Name == "Building_SunLamp" && b.def.specialDisplayRadius > 0f;
        private static RimGovernor.Brain.GameFacts.Observations.ControlledEnvironment Environment(Map map)
        {
            string? NetId(CompPowerTrader? power) => power?.PowerNet == null ? null : power.PowerNet.GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var rooms = new Dictionary<int, Room>();
            void Note(Room? room) { if (room != null && !room.PsychologicallyOutdoors && room.ProperRoom) rooms[room.ID] = room; }
            var lights = new List<RimGovernor.Brain.GameFacts.Observations.GrowLight>();
            var growers = new List<RimGovernor.Brain.GameFacts.Observations.PlantGrower>();
            var buildings = map.listerBuildings.allBuildingsColonist.OrderBy(b => b.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
            foreach (var building in buildings) {
                var power = building.TryGetComp<CompPowerTrader>();
                var room = building.GetRoom();
                if (IsSunLamp(building)) { var lamp = building;
                    var schedule = lamp.TryGetComp<CompSchedule>();
                    lights.Add(new RimGovernor.Brain.GameFacts.Observations.GrowLight {
                        Id = lamp.GetUniqueLoadID(),
                        Room = room == null ? null : NativeRef.Room(room).Id,
                        GrowthCells = [.. GenRadial.RadialCellsAround(lamp.Position, lamp.def.specialDisplayRadius, true).Where(c => c.InBounds(map)).Select(c => new RimGovernor.Brain.Domain.Cell(c.x, c.z))],
                        LitNow = (power == null || power.PowerOn) && (schedule == null || schedule.Allowed),
                        Network = NetId(power) });
                    Note(room);
                } else if (building is Building_PlantGrower grower) {
                    growers.Add(new RimGovernor.Brain.GameFacts.Observations.PlantGrower { Id = grower.GetUniqueLoadID(), Fertility = grower.def.fertility >= 0 ? new RimGovernor.Brain.GameFacts.Observations.GrowerFertility.Fixed(Finite(grower.def.fertility)) : new RimGovernor.Brain.GameFacts.Observations.GrowerFertility.Ground(), SowTag = string.IsNullOrEmpty(grower.def.building.sowTag) ? null : grower.def.building.sowTag,
                        Crop = grower.GetPlantDefToGrow()?.defName, CanSow = grower.CanAcceptSowNow() });
                    Note(room);
                }
            }
            foreach (var room in map.regionGrid.AllRooms) Note(room);
            var grown = new List<RimGovernor.Brain.GameFacts.Observations.GrowRoom>();
            foreach (var room in rooms.Values.OrderBy(r => r.ID)) {
                // A room whose every cell is fogged has no key in the cell grid and is not listed.
                var held = room.Cells.Where(c => !c.Fogged(map)).ToList();
                if (held.Count == 0) continue;
                grown.Add(new RimGovernor.Brain.GameFacts.Observations.GrowRoom { Id = NativeRef.Room(room).Id, GridRoom = held.Min(c => c.z * map.Size.x + c.x), TemperatureC = Finite(room.Temperature) });
            }
            var networks = new List<RimGovernor.Brain.GameFacts.Observations.PowerHeadroom>();
            var nets = map.powerNetManager?.AllNetsListForReading ?? new List<PowerNet>();
            foreach (var net in nets.OrderBy(n => n.GetHashCode())) {
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
                networks.Add(new RimGovernor.Brain.GameFacts.Observations.PowerHeadroom { Id = net.GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture), ActiveSource = net.HasActivePowerSource,
                    GenerationW = generation, SolarW = solar, WindW = wind, ConsumptionW = consumption, CapacityWattDays = capacity });
            }
            return new RimGovernor.Brain.GameFacts.Observations.ControlledEnvironment { OutdoorTemperatureC = Finite(map.mapTemperature.OutdoorTemp), Lights = [.. lights], Growers = [.. growers], Rooms = [.. grown], Networks = [.. networks] };
        }
        private static RimGovernor.Brain.Domain.Cell Cell(IntVec3 c) => new RimGovernor.Brain.Domain.Cell { X = c.x, Z = c.z };
        private static RimGovernor.Brain.GameFacts.Observations.MapSize Size(Map map) => new RimGovernor.Brain.GameFacts.Observations.MapSize { Width = (uint)map.Size.x, Height = (uint)map.Size.z };
        private static double Finite(double v) => double.IsNaN(v) || double.IsInfinity(v) ? throw new InvalidOperationException("Nonfinite fact.") : v;
        private static RimGovernor.Brain.GameFacts.Observations.ForecastFacts Forecast(ForecastFacts.Snapshot source, RimGovernor.Brain.GameFacts.Observations.NeedsForecastRead needs, RimGovernor.Brain.GameFacts.Observations.CoupledForecastRead coupled)
        {
            var patients = new List<RimGovernor.Brain.GameFacts.Observations.PatientForecast>();
            foreach (var patient in source.patients)
                patients.Add(new RimGovernor.Brain.GameFacts.Observations.PatientForecast { PawnId = patient.id, MoodTarget = Finite(patient.moodTarget) });
            return new RimGovernor.Brain.GameFacts.Observations.ForecastFacts { Needs = needs, Coupled = coupled, CombinedFoodSupply = Food(source.combinedFoodSupply), Patients = [.. patients] };
        }
        private static RimGovernor.Brain.GameFacts.Observations.FoodSupplyFacts Food(FoodSupplyFacts.Snapshot source)
        {
            var corpses = source.larder.Corpses.Select(corpse => new RimGovernor.Brain.GameFacts.Observations.CorpseHandling {
                Id = corpse.ID, Cell = new RimGovernor.Brain.Domain.Cell(corpse.Cell.x, corpse.Cell.z),
                Hauler = new RimGovernor.Brain.Domain.PawnId(corpse.Hauler ?? string.Empty), FrozenDestination = corpse.FrozenDestination,
            }).ToArray();
            var consumers = source.consumers.Select(consumer => new RimGovernor.Brain.GameFacts.Observations.FoodConsumer {
                Id = new RimGovernor.Brain.Domain.PawnId(consumer.id ?? throw new InvalidOperationException("Food consumer has no pawn identity.")),
                NutritionPerDay = Finite(consumer.nutritionPerDay), HumanMeatAcceptable = consumer.humanMeatAcceptable,
            }).ToArray();
            var stocks = new List<RimGovernor.Brain.GameFacts.Observations.FoodStock>();
            foreach (var stock in source.stocks) {
                var thing = stock.thing ?? throw new InvalidOperationException("Food stock has no native thing.");
                var eaters = stock.eaters ?? throw new InvalidOperationException("Food stock has no eater census.");
                RimGovernor.Brain.GameFacts.Observations.FoodForm form = thing is Corpse remains
                    ? new RimGovernor.Brain.GameFacts.Observations.FoodForm.Corpse(Finite(Math.Max(0, remains.InnerPawn.GetStatValue(StatDefOf.MeatAmount))), Finite(remains.InnerPawn.BodySize))
                    : new RimGovernor.Brain.GameFacts.Observations.FoodForm.Item();
                RimGovernor.Brain.GameFacts.Observations.FoodPlace place = new RimGovernor.Brain.GameFacts.Observations.FoodPlace.Carried();
                if (thing.Spawned) {
                    var room = thing.Position.GetRoom(thing.Map);
                    place = new RimGovernor.Brain.GameFacts.Observations.FoodPlace.Stands(thing.Position.Roofed(thing.Map), room != null ? NativeRef.Room(room).Id : string.Empty);
                }
                var rot = thing.TryGetComp<CompRottable>();
                stocks.Add(new RimGovernor.Brain.GameFacts.Observations.FoodStock {
                    Id = thing.GetUniqueLoadID(), DefName = thing.def.defName,
                    IsHumanMeat = HumanFoodFacts.ContainsHumanMeat(thing), RawMeat = thing.def.IsMeat,
                    IsHumanlike = thing is Corpse corpse && corpse.InnerPawn.RaceProps.Humanlike,
                    Vegetable = thing.def.ingestible != null && thing.def.ingestible.foodType.HasFlag(FoodTypeFlags.VegetableOrFruit),
                    RawClass = RimGovernor.Brain.Bridge.DefinitionCatalog.RawFoodClass(thing.def),
                    Holder = new RimGovernor.Brain.Domain.PawnId(stock.holder ?? string.Empty), Nutrition = Finite(stock.nutrition),
                    Eaters = eaters.Select(id => new RimGovernor.Brain.Domain.PawnId(id)).ToArray(),
                    Rot = rot != null && rot.Active ? new RimGovernor.Brain.GameFacts.Observations.FoodRot.Rots(Math.Max(0, rot.TicksUntilRotAtCurrentTemp)) : new RimGovernor.Brain.GameFacts.Observations.FoodRot.Keeps(),
                    Place = place, TemperatureC = Finite(thing.AmbientTemperature), Form = form,
                    Count = thing.stackCount, UnitMass = Finite(thing.GetStatValue(StatDefOf.Mass)), Forbidden = thing.IsForbidden(Faction.OfPlayer),
                });
            }
            return new RimGovernor.Brain.GameFacts.Observations.FoodSupplyFacts {
                Consumers = [.. consumers], Stocks = [.. stocks],
                Larder = new RimGovernor.Brain.GameFacts.Observations.FoodLarderFacts {
                    RawMeatNutrition = Finite(source.larder.RawMeatNutrition), CookDemandNutrition = Finite(source.larder.CookDemandNutrition), Corpses = [.. corpses],
                },
            };
        }
        private static RimGovernor.Brain.GameFacts.Common.Unavailable Unavailable(RimGovernor.Brain.GameFacts.Common.UnavailableReason reason, string detail) => new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = reason, Detail = detail };
    }
}
