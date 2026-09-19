#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    internal static class NativeFoodChannels
    {
        private static readonly FieldInfo? EggProgress = BridgeCommon.PrivateInstanceField(typeof(CompEggLayer), "eggProgress");
        private static readonly PropertyInfo? Resource = typeof(CompHasGatherableBodyResource).GetProperty("ResourceDef", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly PropertyInfo? GatherActive = typeof(CompHasGatherableBodyResource).GetProperty("Active", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly PropertyInfo? EggActive = typeof(CompEggLayer).GetProperty("Active", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly PropertyInfo? EggStopped = typeof(CompEggLayer).GetProperty("ProgressStoppedBecauseUnfertilized", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static Obs.FoodChannelsSection Read(Map map, IntVec3 center, List<Pawn> workers, Func<ThingDef, bool> humanFood, int limit)
        {
            try
            {
                var result = new Obs.FoodChannelsFacts();
                var pens = new HashSet<CompAnimalPenMarker>();
                var handlers = workers.Where(p => p.workSettings != null && p.workSettings.WorkIsActive(WorkTypeDefOf.Handling)).ToList();
                foreach (var animal in map.mapPawns.AllPawnsSpawned.Where(p => p.RaceProps.Animal && p.Faction == Faction.OfPlayerSilentFail && !p.Dead).OrderBy(p => p.thingIDNumber))
                {
                    var reachable = handlers.Any(p => (p.playerSettings?.AreaRestrictionInPawnCurrentMap == null || p.playerSettings.AreaRestrictionInPawnCurrentMap[animal.Position])
                        && !animal.IsForbidden(p) && p.CanReach(animal, PathEndMode.Touch, Danger.None));
                    var comps = animal.AllComps.OfType<CompHasGatherableBodyResource>().ToList();
                    var pen = AnimalPenUtility.GetCurrentPenOf(animal, false);
                    if (pen != null) pens.Add(pen);
                    var slaughter = new Obs.FoodSlaughterAnimal { PawnId = animal.GetUniqueLoadID(), Race = animal.def.defName };
                    if (animal.RaceProps.meatDef != null && humanFood(animal.RaceProps.meatDef))
                    {
                        slaughter.MeatNutrition = Finite(animal.GetStatValue(StatDefOf.MeatAmount) * animal.RaceProps.meatDef.GetStatValueAbstract(StatDefOf.Nutrition));
                        if (animal.RaceProps.Eats(FoodTypeFlags.Plant))
                            slaughter.FeedPerDay = Finite(SimplifiedPastureNutritionSimulator.NutritionConsumedPerDay(animal.def, animal.ageTracker.CurLifeStage));
                        var reproduction = animal.RaceProps.gestationPeriodDays;
                        var eggProps = animal.GetComp<CompEggLayer>()?.Props;
                        if (eggProps != null) reproduction = eggProps.eggLayIntervalDays;
                        if (reproduction > 0) slaughter.ReproductionDays = Finite(reproduction);
                    }
                    result.Slaughter.Add(slaughter);
                    if (comps.Count == 0) result.Gatherable.Add(new Obs.GatherableAnimal { PawnId = animal.GetUniqueLoadID(), Race = animal.def.defName, HandlerReachable = reachable, Active = false });
                    foreach (var comp in comps)
                    {
                        var row = new Obs.GatherableAnimal { PawnId = animal.GetUniqueLoadID(), Race = animal.def.defName, HandlerReachable = reachable, Fullness = Finite(comp.Fullness) };
                        if (Resource?.GetValue(comp) is ThingDef resource) row.Resource = resource.defName;
                        if (!(comp is CompMilkable)) row.Active = false;
                        if (comp is CompMilkable milk && GatherActive?.GetValue(comp) is bool active)
                        {
                            row.Active = active && humanFood(milk.Props.milkDef);
                            var speed = PawnUtility.BodyResourceGrowthSpeed(animal);
                            if (milk.Props.milkIntervalDays > 0 && speed > 0)
                            {
                                var days = milk.Props.milkIntervalDays / (double)speed;
                                row.NutritionPerDay = row.Active ? Finite(milk.Props.milkAmount * milk.Props.milkDef.GetStatValueAbstract(StatDefOf.Nutrition) / days) : 0;
                                // JobDriver_Milk.WorkTotal, in native work units. Travel is not included.
                                row.WorkPerDay = row.Active ? 400 / days : 0;
                                row.LeadDays = Finite((1 - comp.Fullness) * days);
                            }
                        }
                        result.Gatherable.Add(row);
                    }
                    var egg = animal.GetComp<CompEggLayer>();
                    var eggRow = new Obs.EggLayerAnimal { PawnId = animal.GetUniqueLoadID(), Race = animal.def.defName };
                    if (egg == null) eggRow.Active = false;
                    if (egg != null)
                    {
                        eggRow.CanLayNow = egg.CanLayNow;
                        if (EggProgress?.GetValue(egg) is float progress) eggRow.Progress = Finite(progress);
                        if (EggActive?.GetValue(egg) is bool active && EggStopped?.GetValue(egg) is bool stopped)
                        {
                            var resource = egg.NextEggType();
                            eggRow.Active = active && !stopped && resource != null && humanFood(resource);
                            var speed = PawnUtility.BodyResourceGrowthSpeed(animal);
                            if (egg.Props.eggLayIntervalDays > 0 && speed > 0 && resource != null)
                            {
                                var days = egg.Props.eggLayIntervalDays / (double)speed;
                                eggRow.NutritionPerDay = eggRow.Active ? Finite((egg.Props.eggCountRange.min + egg.Props.eggCountRange.max) * 0.5 * resource.GetStatValueAbstract(StatDefOf.Nutrition) / days) : 0;
                                if (eggRow.HasProgress) eggRow.LeadDays = Finite((1 - eggRow.Progress) * days);
                            }
                        }
                    }
                    result.EggLayer.Add(eggRow);
                }
                foreach (var pen in pens.OrderBy(p => p.parent.thingIDNumber))
                {
                    // A private calculator avoids modifying the marker's cached UI state.
                    var food = new PenFoodCalculator();
                    food.ResetAndProcessPen(pen);
                    var pasture = Enumerable.Range(0, 4).Min(q => food.nutritionPerDayPerQuadrum.ForQuadrum((Quadrum)q));
                    result.Grazing.Add(new Obs.PenGrazing { PenId = pen.parent.GetUniqueLoadID(), DemandPerDay = Finite(food.SumNutritionConsumptionPerDay),
                        PasturePerDay = Finite(pasture), StoredNutrition = Finite(food.sumStockpiledNutritionAvailableNow) });
                }
                foreach (var dispenser in map.listerThings.AllThings.OfType<Building_NutrientPasteDispenser>().Where(b => b.Spawned && b.Faction == Faction.OfPlayerSilentFail).OrderBy(b => b.thingIDNumber))
                {
                    var row = new Obs.PasteDispenser { BuildingId = dispenser.GetUniqueLoadID() };
                    if (dispenser.powerComp != null) row.Powered = dispenser.powerComp.PowerOn;
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
                    row.HopperNutrition = Finite(nutrition);
                    var room = dispenser.InteractionCell.GetRoom(map);
                    if (room != null) row.AdjacentRoomId = room.ID.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    result.PasteDispenser.Add(row);
                }
                var window = CellRect.FromLimits(new IntVec3(Math.Max(0, center.x - 22), 0, Math.Max(0, center.z - 22)),
                    new IntVec3(Math.Min(map.Size.x - 1, center.x + 22), 0, Math.Min(map.Size.z - 1, center.z + 22)));
                result.PollutedCells = (uint)window.Cells.Count(c => map.pollutionGrid.IsPolluted(c));
                var twelfths = GenTemperature.TwelfthsInAverageTemperatureRange(map.Tile, Plant.DefaultMinOptimalGrowthTemperature, Plant.DefaultMaxOptimalGrowthTemperature);
                foreach (var plant in map.Biome.AllWildPlants.Where(d => d.plant?.harvestedThingDef != null && humanFood(d.plant.harvestedThingDef)).OrderBy(d => d.defName, StringComparer.Ordinal))
                {
                    var row = new Obs.ForagePlant { DefName = plant.defName, GrowingNow = PlantUtility.GrowthSeasonNow(map, plant) };
                    row.GrowingTwelfths.AddRange(twelfths.Select(t => (int)t));
                    result.Forage.Add(row);
                }
                if (ModsConfig.OdysseyActive)
                {
                    var water = new Obs.FishableWater();
                    var research = DefDatabase<ResearchProjectDef>.GetNamedSilentFail("Fishing");
                    if (research != null)
                    {
                        water.FishingResearched = research.IsFinished;
                        if (research.IsFinished) water.ResearchLeadDays = 0;
                        else
                        {
                            var speed = workers.Where(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Research))
                                .Select(p => p.GetStatValue(StatDefOf.ResearchSpeed)).DefaultIfEmpty(0).Max();
                            if (speed > 0) water.ResearchLeadDays = Finite(Math.Max(0, research.baseCost - Find.ResearchManager.GetProgress(research))
                                * research.CostFactor(Faction.OfPlayer.def.techLevel) / (speed * 0.00825 * 20000 * Find.Storyteller.difficulty.researchSpeedFactor));
                        }
                    }
                    var fishers = workers.Where(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Fishing) && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)).ToList();
                    foreach (var body in map.waterBodyTracker.Bodies.Where(b => b.HasFish).OrderBy(b => b.rootCell.x).ThenBy(b => b.rootCell.z))
                    {
                        var cells = body.cells.Where(c => !c.Fogged(map) && c.GetTerrain(map).passability != Traversability.Impassable).ToList();
                        if (cells.Count == 0) continue;
                        var zones = map.zoneManager.AllZones.OfType<Zone_Fishing>().Where(z => z.Cells.Any(c => body.cells.Contains(c))).ToList();
                        var row = new Obs.FishableRegion { Root = new Common.Cell { X = body.rootCell.x, Z = body.rootCell.z },
                            Population = Finite(body.Population), MaxPopulation = Finite(body.MaxPopulation), CellCount = (uint)cells.Count,
                            Zoned = zones.Count > 0, Frozen = cells.All(c => c.GetTerrain(map) == TerrainDefOf.ThinIce),
                            Delivering = zones.Any(z => z.Allowed && z.repeatMode == FishRepeatMode.DoForever && z.HasAnyFishableCells),
                            Reachable = fishers.Any(p => cells.Any(c => !c.IsForbidden(p) && p.CanReach(c, PathEndMode.Touch, Danger.None))) };
                        var fish = body.CommonFishIncludingExtras.Concat(body.UncommonFish).Distinct().ToList();
                        if (fish.Count > 0 && fish.All(humanFood)) row.NutritionPerFish = Finite(fish.Min(d => d.GetStatValueAbstract(StatDefOf.Nutrition)));
                        Bound(fishers.Count, 256);
                        row.ConcurrentFishers = (uint)fishers.Count;
                        if (fishers.Count > 0)
                        {
                            row.FishPerBatch = Math.Max(1, Math.Round(FishingUtility.PopulationToFishYieldCurve.Evaluate(body.Population) * fishers.Min(p => p.GetStatValue(StatDefOf.FishingYield))));
                            var speed = fishers.Min(p => p.GetStatValue(StatDefOf.FishingSpeed));
                            if (speed > 0) row.WorkTicksPerBatch = Finite(7500 / speed);
                        }
                        if (row.HasNutritionPerFish)
                            row.PawnFishWorkCapacity = Finite(fishers.Sum(p => 20000.0 * Math.Max(1, Math.Round(FishingUtility.PopulationToFishYieldCurve.Evaluate(body.Population)
                                * p.GetStatValue(StatDefOf.FishingYield))) * row.NutritionPerFish * p.GetStatValue(StatDefOf.FishingSpeed) / 7500));
                        // A connected spot per available fisher is access capacity,
                        // never a population or sustainable-yield multiplier.
                        var free = cells.Where(c => NativeZoneCreation.FishableCell(c, map, body)
                            && fishers.Any(p => !c.IsForbidden(p) && p.CanReach(c, PathEndMode.Touch, Danger.None)))
                            .OrderBy(c => c.DistanceToSquared(center)).ThenBy(c => c.x).ThenBy(c => c.z).ToList();
                        if (free.Count > 0)
                        {
                            var first = free[0];
                            var available = new HashSet<IntVec3>(free);
                            var pending = new Queue<IntVec3>(); pending.Enqueue(first); available.Remove(first);
                            while (pending.Count > 0 && row.ProposedCells.Count < fishers.Count)
                            {
                                var c = pending.Dequeue();
                                row.ProposedCells.Add(new Common.Cell { X = c.x, Z = c.z });
                                foreach (var delta in GenAdj.CardinalDirections) if (available.Remove(c + delta)) pending.Enqueue(c + delta);
                            }
                        }
                        if (zones.Count == 0 && row.ProposedCells.Count < fishers.Count) row.Reachable = false;
                        water.Regions.Add(row);
                    }
                    Bound(water.Regions.Count, limit);
                    result.FishableWater = water;
                }
                foreach (var count in new[] { result.Gatherable.Count, result.EggLayer.Count, result.PasteDispenser.Count, result.Forage.Count, result.Grazing.Count, result.Slaughter.Count }) Bound(count, limit);
                result.Completeness = new Obs.Completeness { Page = new Common.PageInfo { Complete = true }, Matched = 1, Returned = 1, Filtered = 0, Unreadable = 0 };
                return new Obs.FoodChannelsSection { Observed = result };
            }
            catch (Exception e)
            {
                return new Obs.FoodChannelsSection { Unavailable = new Common.Unavailable { Reason = e is OverflowException ? Common.UnavailableReason.LimitExceeded : Common.UnavailableReason.ReadFailed,
                    Detail = "Food source census unavailable." } };
            }
        }

        private static void Bound(int count, int limit) { if (count > limit) throw new OverflowException(); }
        private static double Finite(double value) { if (double.IsNaN(value) || double.IsInfinity(value)) throw new InvalidOperationException(); return value; }
    }
}
