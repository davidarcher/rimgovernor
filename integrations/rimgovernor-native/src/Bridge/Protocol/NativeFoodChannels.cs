using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimGovernor.Native
{
    internal static class NativeFoodChannels
    {
        private static readonly FieldInfo? EggProgress = BridgeCommon.PrivateInstanceField(typeof(CompEggLayer), "eggProgress");
        private static readonly PropertyInfo? Resource = typeof(CompHasGatherableBodyResource).GetProperty("ResourceDef", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly PropertyInfo? GatherActive = typeof(CompHasGatherableBodyResource).GetProperty("Active", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly PropertyInfo? EggActive = typeof(CompEggLayer).GetProperty("Active", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly PropertyInfo? EggStopped = typeof(CompEggLayer).GetProperty("ProgressStoppedBecauseUnfertilized", BindingFlags.Instance | BindingFlags.NonPublic);

        // The food source census, built whole; a read that cannot be
        // produced throws and fails the colony read.
        internal static RimGovernor.Brain.GameFacts.Observations.FoodChannelsFacts Read(Map map, IntVec3 center, List<Pawn> workers, Func<ThingDef, bool> humanFood)
        {
            var slaughterRows = new List<RimGovernor.Brain.GameFacts.Observations.FoodSlaughterAnimal>();
            var gatherRows = new List<RimGovernor.Brain.GameFacts.Observations.GatherableAnimal>();
            var eggRows = new List<RimGovernor.Brain.GameFacts.Observations.EggLayerAnimal>();
            var handlers = workers.Where(p => p.workSettings != null && p.workSettings.WorkIsActive(WorkTypeDefOf.Handling)).ToList();
            foreach (var animal in map.mapPawns.AllPawnsSpawned.Where(p => p.RaceProps.Animal && p.Faction == Faction.OfPlayerSilentFail && !p.Dead).OrderBy(p => p.thingIDNumber))
            {
                var id = animal.GetUniqueLoadID();
                var race = animal.def.defName;
                var reachable = handlers.Any(p => (p.playerSettings?.AreaRestrictionInPawnCurrentMap == null || p.playerSettings.AreaRestrictionInPawnCurrentMap[animal.Position])
                    && !animal.IsForbidden(p) && p.CanReach(animal, PathEndMode.Touch, Danger.None));
                var comps = animal.AllComps.OfType<CompHasGatherableBodyResource>().ToList();
                // The game's computed MeatAmount of an animal whose meat the colony
                // eats; the brain derives nutrition, feed and reproduction.
                if (animal.RaceProps.meatDef != null && humanFood(animal.RaceProps.meatDef))
                    slaughterRows.Add(new RimGovernor.Brain.GameFacts.Observations.FoodSlaughterAnimal { PawnId = id, Race = race, MeatAmount = Finite(animal.GetStatValue(StatDefOf.MeatAmount)) });
                var speed = PawnUtility.BodyResourceGrowthSpeed(animal);
                foreach (var comp in comps)
                {
                    var resource = Reflected<ThingDef>(Resource, comp, "ResourceDef");
                    RimGovernor.Brain.GameFacts.Observations.AnimalYield yield = new RimGovernor.Brain.GameFacts.Observations.AnimalYield.Inactive();
                    if (comp is CompMilkable milk && Reflected<bool>(GatherActive, comp, "Active") && humanFood(milk.Props.milkDef))
                    {
                        if (milk.Props.milkIntervalDays > 0 && speed > 0)
                        {
                            var days = milk.Props.milkIntervalDays / (double)speed;
                            // JobDriver_Milk.WorkTotal (400) per interval, in native work units. Travel is not included.
                            yield = new RimGovernor.Brain.GameFacts.Observations.AnimalYield.Producing(Finite(milk.Props.milkAmount * milk.Props.milkDef.GetStatValueAbstract(StatDefOf.Nutrition) / days), Finite(400 / days), Finite((1 - comp.Fullness) * days));
                        }
                        else yield = new RimGovernor.Brain.GameFacts.Observations.AnimalYield.Stalled();
                    }
                    gatherRows.Add(new RimGovernor.Brain.GameFacts.Observations.GatherableAnimal { PawnId = id, Race = race, HandlerReachable = reachable, Fullness = Finite(comp.Fullness), Resource = resource.defName, Yield = yield });
                }
                var egg = animal.GetComp<CompEggLayer>();
                if (egg != null)
                {
                    var next = egg.NextEggType();
                    RimGovernor.Brain.GameFacts.Observations.AnimalYield yield = new RimGovernor.Brain.GameFacts.Observations.AnimalYield.Inactive();
                    if (Reflected<bool>(EggActive, egg, "Active") && !Reflected<bool>(EggStopped, egg, "ProgressStoppedBecauseUnfertilized") && next != null && humanFood(next))
                    {
                        if (egg.Props.eggLayIntervalDays > 0 && speed > 0)
                        {
                            var days = egg.Props.eggLayIntervalDays / (double)speed;
                            var progress = Reflected<float>(EggProgress, egg, "eggProgress");
                            yield = new RimGovernor.Brain.GameFacts.Observations.AnimalYield.Producing(Finite((egg.Props.eggCountRange.min + egg.Props.eggCountRange.max) * 0.5 * next.GetStatValueAbstract(StatDefOf.Nutrition) / days), 0, Finite((1 - progress) * days));
                        }
                        else yield = new RimGovernor.Brain.GameFacts.Observations.AnimalYield.Stalled();
                    }
                    eggRows.Add(new RimGovernor.Brain.GameFacts.Observations.EggLayerAnimal { PawnId = id, Race = race, Yield = yield });
                }
            }
            // Every player pen marker, enclosed or not: an unenclosed marker reads Enclosed false with no districts.
            var grazing = new List<RimGovernor.Brain.GameFacts.Observations.PenGrazing>();
            var penned = new HashSet<Region>();
            foreach (var building in map.listerBuildings.allBuildingsAnimalPenMarkers.Where(b => b.Spawned && b.Faction == Faction.OfPlayerSilentFail).OrderBy(b => b.thingIDNumber))
            {
                var pen = building.GetComp<CompAnimalPenMarker>() ?? throw new InvalidOperationException("pen marker " + building.GetUniqueLoadID() + " has no CompAnimalPenMarker");
                var state = pen.PenState ?? throw new InvalidOperationException("pen marker " + building.GetUniqueLoadID() + " has no pen state");
                // A private calculator avoids modifying the marker's cached UI state.
                var food = new PenFoodCalculator();
                food.ResetAndProcessPen(pen);
                var pasture = Enumerable.Range(0, 4).Min(q => food.nutritionPerDayPerQuadrum.ForQuadrum((Quadrum)q));
                var shelters = new List<RimGovernor.Brain.GameFacts.Observations.PenShelterRoom>();
                var boundary = new SortedSet<string>(StringComparer.Ordinal);
                var animals = new SortedSet<string>(StringComparer.Ordinal);
                if (state.Enclosed)
                {
                    penned.UnionWith(state.ConnectedRegions);
                    // What bounds the enclosure: the edifice on each cell just outside its regions (fences, gates, walls)
                    // and the door of each doorway region it passes (animal flaps); the player animals inside it.
                    var inside = new HashSet<Region>(state.ConnectedRegions);
                    foreach (var region in inside)
                    {
                        if (region.door != null) boundary.Add(region.door.GetUniqueLoadID());
                        foreach (var cell in region.Cells)
                            foreach (var d in GenAdj.CardinalDirections)
                            {
                                var near = cell + d;
                                if (!near.InBounds(map)) continue;
                                var other = near.GetRegion(map, RegionType.Set_All);
                                if (other != null && inside.Contains(other)) continue;
                                var edifice = near.GetEdifice(map);
                                if (edifice != null) boundary.Add(edifice.GetUniqueLoadID());
                            }
                        foreach (var thing in region.ListerThings.ThingsInGroup(ThingRequestGroup.Pawn))
                            if (thing is Pawn roamer && roamer.Spawned && roamer.RaceProps.Animal && roamer.Faction == Faction.OfPlayerSilentFail) animals.Add(roamer.GetUniqueLoadID());
                    }
                    // The roofed, walled rooms the enclosure flood reaches: it crosses only doors the pen's roamers pass.
                    foreach (var room in state.ConnectedRegions.Select(r => r.Room).Where(r => r != null && !r.UsesOutdoorTemperature).Distinct().OrderBy(r => r.ID))
                        shelters.Add(new RimGovernor.Brain.GameFacts.Observations.PenShelterRoom { Room = room.ID, Cells = room.CellCount, TemperatureC = Finite(room.Temperature) });
                }
                grazing.Add(new RimGovernor.Brain.GameFacts.Observations.PenGrazing { Marker = building.GetUniqueLoadID(), Cell = new RimGovernor.Brain.Domain.Cell(building.Position.x, building.Position.z),
                    Enclosed = state.Enclosed, Cells = food.numCells, DemandPerDay = Finite(food.SumNutritionConsumptionPerDay), PastureTodayPerDay = Finite(food.NutritionPerDayToday),
                    PasturePerDay = Finite(pasture), StoredNutrition = Finite(food.sumStockpiledNutritionAvailableNow), Shelters = [.. shelters],
                    Boundary = [.. boundary], Animals = [.. animals] });
            }
            // Pen feed outside every enclosed pen: the stock a pen's feed store would be hauled from.
            var feed = new Dictionary<ThingDef, double>();
            foreach (var thing in map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree))
            {
                if (!thing.Spawned || thing.def.category != ThingCategory.Item || !thing.IngestibleNow || !MapPlantGrowthRateCalculator.IsEdibleByPastureAnimals(thing.def) || humanFood(thing.def)
                    || thing.IsForbidden(Faction.OfPlayer) || penned.Contains(thing.GetRegion())) continue;
                feed[thing.def] = feed.TryGetValue(thing.def, out var have) ? have + thing.stackCount * thing.GetStatValue(StatDefOf.Nutrition) : thing.stackCount * thing.GetStatValue(StatDefOf.Nutrition);
            }
            var penFeed = feed.OrderBy(f => f.Key.defName, StringComparer.Ordinal).Select(f => new RimGovernor.Brain.GameFacts.Observations.PenFeedStock { Def = f.Key.defName,
                Nutrition = Finite(f.Value), NutritionPerCell = Finite(f.Key.stackLimit * f.Key.GetStatValueAbstract(StatDefOf.Nutrition)) }).ToList();
            // The pasture a cell of each terrain grows in the worst quadrum: the
            // per-cell term PenFoodCalculator sums over a pen, so the brain can size a
            // pen before it exists.
            var terrainPasture = new MapPastureNutritionCalculator();
            terrainPasture.Reset(map);
            var pastureTerrain = new List<RimGovernor.Brain.GameFacts.Observations.PastureTerrain>();
            foreach (var terrain in DefDatabase<TerrainDef>.AllDefsListForReading.Where(t => t.fertility > 0f).OrderBy(t => t.defName, StringComparer.Ordinal))
            {
                var perQuadrum = terrainPasture.CalculateAverageNutritionPerDay(terrain);
                pastureTerrain.Add(new RimGovernor.Brain.GameFacts.Observations.PastureTerrain { TerrainDef = terrain.defName,
                    WorstQuadrumPerDay = Finite(Enumerable.Range(0, 4).Min(q => perQuadrum.ForQuadrum((Quadrum)q))) });
            }
            var paste = new List<RimGovernor.Brain.GameFacts.Observations.PasteDispenser>();
            foreach (var dispenser in map.listerThings.AllThings.OfType<Building_NutrientPasteDispenser>().Where(b => b.Spawned && b.Faction == Faction.OfPlayerSilentFail).OrderBy(b => b.thingIDNumber))
            {
                var powered = dispenser.powerComp?.PowerOn ?? throw new InvalidOperationException("nutrient paste dispenser " + dispenser.GetUniqueLoadID() + " has no power comp");
                // Same eligible feed stacks as HasEnoughFeedstockInHoppers,
                // summed without its early exit after one meal's nutrition.
                double nutrition = 0;
                foreach (var cell in dispenser.AdjCellsCardinalInBounds)
                {
                    var things = cell.GetThingList(map);
                    if (!things.Any(t => t.IsHopper())) continue;
                    var food = things.LastOrDefault(t => Building_NutrientPasteDispenser.IsAcceptableFeedstock(t.def));
                    if (food != null) nutrition += food.stackCount * food.GetStatValue(StatDefOf.Nutrition);
                }
                paste.Add(new RimGovernor.Brain.GameFacts.Observations.PasteDispenser { Powered = powered, HopperNutrition = Finite(nutrition) });
            }
            var forage = map.Biome.AllWildPlants.Where(d => d.plant?.harvestedThingDef != null && humanFood(d.plant.harvestedThingDef)).OrderBy(d => d.defName, StringComparer.Ordinal)
                .Select(d => new RimGovernor.Brain.GameFacts.Observations.ForagePlant { DefName = d.defName }).ToList();
            return new RimGovernor.Brain.GameFacts.Observations.FoodChannelsFacts
            {
                FishableWater = ModsConfig.OdysseyActive ? Water(map, center, workers, humanFood) : null,
                Gatherable = [.. gatherRows],
                EggLayer = [.. eggRows],
                PasteDispenser = [.. paste],
                Forage = [.. forage],
                Grazing = [.. grazing],
                PenFeed = [.. penFeed],
                AnimalDoors = [.. DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.IsDoor && d.building is { roamerCanOpen: true }).Select(d => d.defName).OrderBy(n => n, StringComparer.Ordinal)],
                Slaughter = [.. slaughterRows],
                PastureTerrain = [.. pastureTerrain],
            };
        }

        // Odyssey's fishable water: one row per fish-bearing body with visible
        // passable cells whose fish the colony eats, the colony's fishers and the
        // Fishing research.
        private static RimGovernor.Brain.GameFacts.Observations.FishableWater Water(Map map, IntVec3 center, List<Pawn> workers, Func<ThingDef, bool> humanFood)
        {
            var fishers = workers.Where(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Fishing) && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)).ToList();
            var regions = new List<RimGovernor.Brain.GameFacts.Observations.FishableRegion>();
            foreach (var body in map.waterBodyTracker.Bodies.Where(b => b.HasFish).OrderBy(b => b.rootCell.x).ThenBy(b => b.rootCell.z))
            {
                var cells = body.cells.Where(c => !c.Fogged(map) && c.GetTerrain(map).passability != Traversability.Impassable).ToList();
                if (cells.Count == 0) continue;
                var fish = body.CommonFishIncludingExtras.Concat(body.UncommonFish).Distinct().ToList();
                if (fish.Count == 0 || !fish.All(humanFood)) continue;
                var zones = map.zoneManager.AllZones.OfType<Zone_Fishing>().Where(z => z.Cells.Any(c => body.cells.Contains(c))).ToList();
                var row = new RimGovernor.Brain.GameFacts.Observations.FishableRegion { Root = new RimGovernor.Brain.Domain.Cell(body.rootCell.x, body.rootCell.z),
                    Population = Finite(body.Population), MaxPopulation = Finite(body.MaxPopulation),
                    NutritionPerFish = Finite(fish.Min(d => d.GetStatValueAbstract(StatDefOf.Nutrition))),
                    Zoned = zones.Count > 0, Frozen = cells.All(c => c.GetTerrain(map) == TerrainDefOf.ThinIce),
                    Reachable = fishers.Any(p => cells.Any(c => !c.IsForbidden(p) && p.CanReach(c, PathEndMode.Touch, Danger.None))),
                    YieldCurveValue = Finite(FishingUtility.PopulationToFishYieldCurve.Evaluate(body.Population)),
                    NearestDistanceSquared = cells.Min(c => c.DistanceToSquared(center)),
                    Zones = [.. zones.Select(z => new RimGovernor.Brain.GameFacts.Observations.FishingZoneFacts { Allowed = z.Allowed, DoForever = z.repeatMode == FishRepeatMode.DoForever, HasFishableCells = z.HasAnyFishableCells })],
                    // Vanilla's fishing-zone cell rule; policy chooses the footprint.
                    // Built once: appending per cell copies the array each time, quadratic on a lake.
                    Cells = [.. cells.Where(c => NativeZoneCreation.FishableCell(c, map, body)).OrderBy(c => c.x).ThenBy(c => c.z)
                        .Select(c => new RimGovernor.Brain.GameFacts.Observations.FishableCell { Cell = new RimGovernor.Brain.Domain.Cell(c.x, c.z), DistanceSquared = c.DistanceToSquared(center),
                            Reachable = fishers.Any(p => !c.IsForbidden(p) && p.CanReach(c, PathEndMode.Touch, Danger.None)) })] };
                regions.Add(row);
            }
            var research = ResearchProjectDefOf.Fishing ?? throw new InvalidOperationException("Odyssey is active but the Fishing research project is missing");
            return new RimGovernor.Brain.GameFacts.Observations.FishableWater
            {
                Regions = [.. regions],
                Fishers = [.. fishers.Select(p => new RimGovernor.Brain.GameFacts.Observations.FisherFacts { FishingYield = Finite(p.GetStatValue(StatDefOf.FishingYield)), FishingSpeed = Finite(p.GetStatValue(StatDefOf.FishingSpeed)) })],
                ResearcherSpeeds = [.. workers.Where(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Research)).Select(p => Finite(p.GetStatValue(StatDefOf.ResearchSpeed)))],
                BaseFishingDurationTicks = Finite(FishingUtility.BaseFishingDurationTicks),
                FishingResearched = research.IsFinished, ResearchBaseCost = Finite(research.baseCost),
                ResearchProgress = Finite(Find.ResearchManager.GetProgress(research)), ResearchCostFactor = Finite(research.CostFactor(Faction.OfPlayer.def.techLevel)),
                ResearchPointsPerWorkTick = Finite(ResearchManager.ResearchPointsPerWorkTick), ResearchDifficultySpeedFactor = Finite(Find.Storyteller.difficulty.researchSpeedFactor),
            };
        }

        // A private member the census reads; a renamed or retyped member throws and fails the colony read.
        private static T Reflected<T>(MemberInfo? member, object target, string name)
        {
            var value = member switch { FieldInfo f => f.GetValue(target), PropertyInfo p => p.GetValue(target), _ => null };
            return value is T typed ? typed : throw new InvalidOperationException(target.GetType().Name + "." + name + " is not readable as " + typeof(T).Name);
        }

        private static double Finite(double value) { if (double.IsNaN(value) || double.IsInfinity(value)) throw new InvalidOperationException(); return value; }
    }
}
