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

        internal static Obs.FoodChannelsSection Read(Map map, IntVec3 center, List<Pawn> workers, Func<ThingDef, bool> humanFood, int limit)
        {
            try
            {
                var result = new Obs.FoodChannelsFacts();
                var handlers = workers.Where(p => p.workSettings != null && p.workSettings.WorkIsActive(WorkTypeDefOf.Handling)).ToList();
                foreach (var animal in map.mapPawns.AllPawnsSpawned.Where(p => p.RaceProps.Animal && p.Faction == Faction.OfPlayerSilentFail && !p.Dead).OrderBy(p => p.thingIDNumber))
                {
                    var reachable = handlers.Any(p => (p.playerSettings?.AreaRestrictionInPawnCurrentMap == null || p.playerSettings.AreaRestrictionInPawnCurrentMap[animal.Position])
                        && !animal.IsForbidden(p) && p.CanReach(animal, PathEndMode.Touch, Danger.None));
                    var comps = animal.AllComps.OfType<CompHasGatherableBodyResource>().ToList();
                    if (comps.Count == 0) result.Gatherable.Add(new Obs.GatherableAnimal { PawnId = animal.GetUniqueLoadID(), Race = animal.def.defName, HandlerReachable = reachable });
                    foreach (var comp in comps)
                    {
                        var row = new Obs.GatherableAnimal { PawnId = animal.GetUniqueLoadID(), Race = animal.def.defName, HandlerReachable = reachable, Fullness = Finite(comp.Fullness) };
                        if (Resource?.GetValue(comp) is ThingDef resource) row.Resource = resource.defName;
                        result.Gatherable.Add(row);
                    }
                    var egg = animal.GetComp<CompEggLayer>();
                    var eggRow = new Obs.EggLayerAnimal { PawnId = animal.GetUniqueLoadID(), Race = animal.def.defName };
                    if (egg != null)
                    {
                        eggRow.CanLayNow = egg.CanLayNow;
                        if (EggProgress?.GetValue(egg) is float progress) eggRow.Progress = Finite(progress);
                    }
                    result.EggLayer.Add(eggRow);
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
                    if (research != null) water.FishingResearched = research.IsFinished;
                    foreach (var body in map.waterBodyTracker.Bodies.Where(b => b.HasFish).OrderBy(b => b.rootCell.x).ThenBy(b => b.rootCell.z))
                    {
                        var cells = body.cells.Where(c => !c.Fogged(map) && c.GetTerrain(map).passability != Traversability.Impassable).ToList();
                        if (cells.Count == 0) continue;
                        water.Regions.Add(new Obs.FishableRegion { Root = new Common.Cell { X = body.rootCell.x, Z = body.rootCell.z },
                            Population = Finite(body.Population), MaxPopulation = Finite(body.MaxPopulation), CellCount = (uint)cells.Count,
                            Zoned = cells.Any(c => map.zoneManager.ZoneAt(c) is Zone_Fishing),
                            Reachable = cells.Any(c => map.reachability.CanReach(center, c, PathEndMode.OnCell, TraverseParms.For(TraverseMode.PassDoors, Danger.None))) });
                    }
                    Bound(water.Regions.Count, limit);
                    result.FishableWater = water;
                }
                foreach (var count in new[] { result.Gatherable.Count, result.EggLayer.Count, result.PasteDispenser.Count, result.Forage.Count }) Bound(count, limit);
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
