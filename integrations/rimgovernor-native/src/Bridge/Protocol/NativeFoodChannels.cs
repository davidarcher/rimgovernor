#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;
using Verse.AI;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    internal static class NativeFoodChannels
    {
        private static readonly FieldInfo? EggProgress = BridgeCommon.PrivateInstanceField(typeof(CompEggLayer), "eggProgress");
        private static readonly PropertyInfo? Resource = typeof(CompHasGatherableBodyResource).GetProperty("ResourceDef", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly PropertyInfo? GatherActive = typeof(CompHasGatherableBodyResource).GetProperty("Active", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? EggFertilization = BridgeCommon.PrivateInstanceField(typeof(CompEggLayer), "fertilizationCount");
        private static readonly PropertyInfo? EggActive = typeof(CompEggLayer).GetProperty("Active", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly PropertyInfo? EggStopped = typeof(CompEggLayer).GetProperty("ProgressStoppedBecauseUnfertilized", BindingFlags.Instance | BindingFlags.NonPublic);

        // The food source census, built whole; a read that cannot be
        // produced throws and fails the colony read.
        internal static Obs.FoodChannelsFacts Read(Map map, IntVec3 center, List<Pawn> workers, Func<ThingDef, bool> humanFood)
        {
            var pens = new HashSet<CompAnimalPenMarker>();
            var slaughterRows = new List<Obs.FoodSlaughterAnimal>();
            var gatherRows = new List<Obs.GatherableAnimal>();
            var eggRows = new List<Obs.EggLayerAnimal>();
            var handlers = workers.Where(p => p.workSettings != null && p.workSettings.WorkIsActive(WorkTypeDefOf.Handling)).ToList();
            foreach (var animal in map.mapPawns.AllPawnsSpawned.Where(p => p.RaceProps.Animal && p.Faction == Faction.OfPlayerSilentFail && !p.Dead).OrderBy(p => p.thingIDNumber))
            {
                var id = animal.GetUniqueLoadID();
                var race = animal.def.defName;
                var reachable = handlers.Any(p => (p.playerSettings?.AreaRestrictionInPawnCurrentMap == null || p.playerSettings.AreaRestrictionInPawnCurrentMap[animal.Position])
                    && !animal.IsForbidden(p) && p.CanReach(animal, PathEndMode.Touch, Danger.None));
                var comps = animal.AllComps.OfType<CompHasGatherableBodyResource>().ToList();
                var pen = AnimalPenUtility.GetCurrentPenOf(animal, false);
                if (pen != null) pens.Add(pen);
                // The game's computed MeatAmount of this animal, sent only when the
                // colony eats the meat; the brain derives nutrition, feed and reproduction.
                var meat = animal.RaceProps.meatDef != null && humanFood(animal.RaceProps.meatDef) ? Finite(animal.GetStatValue(StatDefOf.MeatAmount)) : (double?)null;
                slaughterRows.Add(new Obs.FoodSlaughterAnimal { PawnId = id, Race = race, MeatAmount = meat });
                if (comps.Count == 0) gatherRows.Add(new Obs.GatherableAnimal { PawnId = id, Race = race, HandlerReachable = reachable, Active = false });
                foreach (var comp in comps)
                {
                    var row = new Obs.GatherableAnimal { PawnId = id, Race = race, HandlerReachable = reachable, Fullness = Finite(comp.Fullness) };
                    if (Resource?.GetValue(comp) is ThingDef resource) row = row with { Resource = resource.defName };
                    if (!(comp is CompMilkable)) row = row with { Active = false };
                    if (comp is CompMilkable milk && GatherActive?.GetValue(comp) is bool active)
                    {
                        row = row with { Active = active && humanFood(milk.Props.milkDef) };
                        var speed = PawnUtility.BodyResourceGrowthSpeed(animal);
                        if (milk.Props.milkIntervalDays > 0 && speed > 0)
                        {
                            var days = milk.Props.milkIntervalDays / (double)speed;
                            row = row with { NutritionPerDay = row.Active.GetValueOrDefault() ? Finite(milk.Props.milkAmount * milk.Props.milkDef.GetStatValueAbstract(StatDefOf.Nutrition) / days) : 0 };
                            // JobDriver_Milk.WorkTotal, in native work units. Travel is not included.
                            row = row with { WorkPerDay = row.Active.GetValueOrDefault() ? 400 / days : 0 };
                            row = row with { LeadDays = Finite((1 - comp.Fullness) * days) };
                        }
                    }
                    gatherRows.Add(row);
                }
                var egg = animal.GetComp<CompEggLayer>();
                var eggRow = new Obs.EggLayerAnimal { PawnId = id, Race = race };
                if (egg == null) eggRow = eggRow with { Active = false };
                if (egg != null)
                {
                    eggRow = eggRow with { CanLayNow = egg.CanLayNow };
                    if (EggProgress?.GetValue(egg) is float progress) eggRow = eggRow with { Progress = Finite(progress) };
                    // The next egg is the fertilized def exactly when NextEggType picks it.
                    if (EggFertilization?.GetValue(egg) is int fertilization) eggRow = eggRow with { Fertilized = fertilization > 0 };
                    if (EggActive?.GetValue(egg) is bool active && EggStopped?.GetValue(egg) is bool stopped)
                    {
                        var resource = egg.NextEggType();
                        eggRow = eggRow with { Active = active && !stopped && resource != null && humanFood(resource) };
                        var speed = PawnUtility.BodyResourceGrowthSpeed(animal);
                        if (egg.Props.eggLayIntervalDays > 0 && speed > 0 && resource != null)
                        {
                            var days = egg.Props.eggLayIntervalDays / (double)speed;
                            eggRow = eggRow with { NutritionPerDay = eggRow.Active.GetValueOrDefault() ? Finite((egg.Props.eggCountRange.min + egg.Props.eggCountRange.max) * 0.5 * resource.GetStatValueAbstract(StatDefOf.Nutrition) / days) : 0 };
                            if (eggRow.Progress is not null) eggRow = eggRow with { LeadDays = Finite((1 - eggRow.Progress.GetValueOrDefault()) * days) };
                        }
                    }
                }
                eggRows.Add(eggRow);
            }
            var grazing = new List<Obs.PenGrazing>();
            foreach (var pen in pens.OrderBy(p => p.parent.thingIDNumber))
            {
                // A private calculator avoids modifying the marker's cached UI state.
                var food = new PenFoodCalculator();
                food.ResetAndProcessPen(pen);
                var pasture = Enumerable.Range(0, 4).Min(q => food.nutritionPerDayPerQuadrum.ForQuadrum((Quadrum)q));
                grazing.Add(new Obs.PenGrazing { DemandPerDay = Finite(food.SumNutritionConsumptionPerDay),
                    PasturePerDay = Finite(pasture), StoredNutrition = Finite(food.sumStockpiledNutritionAvailableNow) });
            }
            // The pasture a cell of each terrain grows in the worst quadrum: the
            // per-cell term PenFoodCalculator sums over a pen, so the brain can size a
            // pen before it exists.
            var terrainPasture = new MapPastureNutritionCalculator();
            terrainPasture.Reset(map);
            var pastureTerrain = new List<Obs.PastureTerrain>();
            foreach (var terrain in DefDatabase<TerrainDef>.AllDefsListForReading.Where(t => t.fertility > 0f).OrderBy(t => t.defName, StringComparer.Ordinal))
            {
                var perQuadrum = terrainPasture.CalculateAverageNutritionPerDay(terrain);
                pastureTerrain.Add(new Obs.PastureTerrain { TerrainDef = terrain.defName,
                    WorstQuadrumPerDay = Finite(Enumerable.Range(0, 4).Min(q => perQuadrum.ForQuadrum((Quadrum)q))) });
            }
            var paste = new List<Obs.PasteDispenser>();
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
                paste.Add(new Obs.PasteDispenser { Powered = powered, HopperNutrition = Finite(nutrition) });
            }
            var forage = map.Biome.AllWildPlants.Where(d => d.plant?.harvestedThingDef != null && humanFood(d.plant.harvestedThingDef)).OrderBy(d => d.defName, StringComparer.Ordinal)
                .Select(d => new Obs.ForagePlant { DefName = d.defName }).ToList();
            return new Obs.FoodChannelsFacts
            {
                FishableWater = ModsConfig.OdysseyActive ? Water(map, center, workers, humanFood) : null,
                Gatherable = [.. gatherRows],
                EggLayer = [.. eggRows],
                PasteDispenser = [.. paste],
                Forage = [.. forage],
                Grazing = [.. grazing],
                Slaughter = [.. slaughterRows],
                PastureTerrain = [.. pastureTerrain],
            };
        }

        // Odyssey's fishable water: one row per fish-bearing body with visible
        // passable cells, the colony's fishers and the Fishing research.
        private static Obs.FishableWater Water(Map map, IntVec3 center, List<Pawn> workers, Func<ThingDef, bool> humanFood)
        {
            var fishers = workers.Where(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Fishing) && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)).ToList();
            var regions = new List<Obs.FishableRegion>();
            foreach (var body in map.waterBodyTracker.Bodies.Where(b => b.HasFish).OrderBy(b => b.rootCell.x).ThenBy(b => b.rootCell.z))
            {
                var cells = body.cells.Where(c => !c.Fogged(map) && c.GetTerrain(map).passability != Traversability.Impassable).ToList();
                if (cells.Count == 0) continue;
                var zones = map.zoneManager.AllZones.OfType<Zone_Fishing>().Where(z => z.Cells.Any(c => body.cells.Contains(c))).ToList();
                var row = new Obs.FishableRegion { Root = new RimGovernor.Brain.Domain.Cell(body.rootCell.x, body.rootCell.z),
                    Population = Finite(body.Population), MaxPopulation = Finite(body.MaxPopulation), CellCount = (uint)cells.Count,
                    Zoned = zones.Count > 0, Frozen = cells.All(c => c.GetTerrain(map) == TerrainDefOf.ThinIce),
                    Reachable = fishers.Any(p => cells.Any(c => !c.IsForbidden(p) && p.CanReach(c, PathEndMode.Touch, Danger.None))),
                    YieldCurveValue = Finite(FishingUtility.PopulationToFishYieldCurve.Evaluate(body.Population)),
                    NearestDistanceSquared = cells.Min(c => c.DistanceToSquared(center)),
                    Zones = [.. zones.Select(z => new Obs.FishingZoneFacts { Allowed = z.Allowed, DoForever = z.repeatMode == FishRepeatMode.DoForever, HasFishableCells = z.HasAnyFishableCells })],
                    // Vanilla's fishing-zone cell rule; policy chooses the footprint.
                    // Built once: appending per cell copies the array each time, quadratic on a lake.
                    Cells = [.. cells.Where(c => NativeZoneCreation.FishableCell(c, map, body)).OrderBy(c => c.x).ThenBy(c => c.z)
                        .Select(c => new Obs.FishableCell { Cell = new RimGovernor.Brain.Domain.Cell(c.x, c.z), DistanceSquared = c.DistanceToSquared(center),
                            Reachable = fishers.Any(p => !c.IsForbidden(p) && p.CanReach(c, PathEndMode.Touch, Danger.None)) })] };
                var fish = body.CommonFishIncludingExtras.Concat(body.UncommonFish).Distinct().ToList();
                if (fish.Count > 0 && fish.All(humanFood)) row = row with { NutritionPerFish = Finite(fish.Min(d => d.GetStatValueAbstract(StatDefOf.Nutrition))) };
                if (fishers.Count > 0)
                {
                    row = row with { FishPerBatch = Math.Max(1, Math.Round(FishingUtility.PopulationToFishYieldCurve.Evaluate(body.Population) * fishers.Min(p => p.GetStatValue(StatDefOf.FishingYield)))) };
                    var speed = fishers.Min(p => p.GetStatValue(StatDefOf.FishingSpeed));
                    if (speed > 0) row = row with { WorkTicksPerBatch = Finite(FishingUtility.BaseFishingDurationTicks / speed) };
                }
                regions.Add(row);
            }
            var research = ResearchProjectDefOf.Fishing;
            var water = new Obs.FishableWater
            {
                Regions = [.. regions],
                Fishers = [.. fishers.Select(p => new Obs.FisherFacts { FishingYield = Finite(p.GetStatValue(StatDefOf.FishingYield)), FishingSpeed = Finite(p.GetStatValue(StatDefOf.FishingSpeed)) })],
                ResearcherSpeeds = research != null ? [.. workers.Where(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Research)).Select(p => Finite(p.GetStatValue(StatDefOf.ResearchSpeed)))] : [],
                BaseFishingDurationTicks = Finite(FishingUtility.BaseFishingDurationTicks),
            };
            if (research != null)
            {
                water = water with { FishingResearched = research.IsFinished, ResearchBaseCost = Finite(research.baseCost),
                    ResearchProgress = Finite(Find.ResearchManager.GetProgress(research)), ResearchCostFactor = Finite(research.CostFactor(Faction.OfPlayer.def.techLevel)),
                    ResearchPointsPerWorkTick = Finite(ResearchManager.ResearchPointsPerWorkTick), ResearchDifficultySpeedFactor = Finite(Find.Storyteller.difficulty.researchSpeedFactor) };
            }
            return water;
        }

        private static double Finite(double value) { if (double.IsNaN(value) || double.IsInfinity(value)) throw new InvalidOperationException(); return value; }
    }
}
