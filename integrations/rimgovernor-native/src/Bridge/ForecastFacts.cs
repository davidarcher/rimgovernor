using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Current-condition inputs only. No work orders or simulated future yields.
    internal static class ForecastFacts
    {
        internal static object Read(Map map, List<Pawn> people, List<Thing> things)
        {
            var animals = map.mapPawns.AllPawnsSpawned.Where(p => !p.Dead && p.RaceProps.Animal
                && p.Faction == Faction.OfPlayerSilentFail && p.needs?.food != null).ToList();
            var food = things.Where(t => t.def.category == ThingCategory.Item
                && t.def.IsNutritionGivingIngestible && !t.def.IsDrug && t.IngestibleNow
                && (t.Faction == null || t.Faction.IsPlayer)).ToList();
            var crops = new List<object>();
            foreach (var zone in map.zoneManager.AllZones.OfType<Zone_Growing>())
            {
                var def = BridgeCommon.PrivateInstanceField(typeof(Zone_Growing), "plantDefToGrow")?.GetValue(zone) as ThingDef;
                if (def?.plant == null)
                {
                    crops.Add(new { id = zone.ID, crop = def?.defName,
                        sowWork = (float?)null, harvestWork = (float?)null,
                        reason = "Native crop definition unavailable" });
                    continue;
                }
                var cells = map.AllCells.Where(c => map.zoneManager.ZoneAt(c) == zone && !c.Fogged(map)).ToList();
                var plants = cells.Select(c => c.GetPlant(map)).Where(p => p != null && p.def == def).ToList();
                crops.Add(new { id = zone.ID, crop = def.defName,
                    sowWork = ReadNumber(() => cells.Count(c => c.GetPlant(map) == null
                        && map.fertilityGrid.FertilityAt(c) >= def.plant.fertilityMin) * def.plant.sowWork),
                    harvestWork = ReadNumber(() => plants.Count(p => p.HarvestableNow) * def.plant.harvestWork),
                    maturePlants = plants.Count(p => p.HarvestableNow),
                    stalledPlants = plants.Count(p => p.GrowthRate <= 0),
                    standingYield = ReadNumber(() => plants.Sum(p => (float)p.YieldNow())),
                    product = def.plant.harvestedThingDef?.defName });
            }
            var patients = people.Select(p => new {
                id = p.GetUniqueLoadID(),
                bleedRatePerDay = ReadNumber(() => p.health.hediffSet.BleedRateTotal),
                hoursUntilDeathFromBloodLoss = ReadNumber(() => {
                    var ticks = HealthUtility.TicksUntilDeathDueToBloodLoss(p);
                    return ticks == int.MaxValue ? (float?)null : ticks / 2500f;
                }),
                mood = ReadNumber(() => p.needs.mood?.CurLevelPercentage),
                moodTarget = ReadNumber(() => p.needs.mood?.CurInstantLevel),
                minorBreakThreshold = ReadNumber(() => p.mindState.mentalBreaker?.BreakThresholdMinor),
                majorBreakThreshold = ReadNumber(() => p.mindState.mentalBreaker?.BreakThresholdMajor),
                extremeBreakThreshold = ReadNumber(() => p.mindState.mentalBreaker?.BreakThresholdExtreme)
            }).ToList();
            return new { readable = true, tick = Find.TickManager.TicksGame,
                animalIds = animals.Select(p => p.GetUniqueLoadID()).ToList(),
                combinedFoodSupply = FoodSupplyFacts.Read(people.Concat(animals).ToList(), food),
                crops, patients,
                assumptions = new[] {
                    "Animal feed shares item stocks with colonists; no grazing, future haul, hunting or future harvest is credited.",
                    "Crop labor is raw native sow/harvest work for current visible growing-zone cells, not pawn-hours or scheduled production.",
                    "Standing yield is current native yield, not guaranteed output. Seasons, growth stalls, labor, skill, access and crop destruction can delay or remove it.",
                    "Medical bleed-out uses the native untreated estimate. Mood target and break thresholds describe current conditions, not a break probability or deadline." } };
        }

        private static float? ReadNumber(Func<float?> read)
        {
            try { var value = read(); return value.HasValue && !float.IsNaN(value.Value)
                && !float.IsInfinity(value.Value) ? value : null; }
            catch { return null; }
        }
    }
}
