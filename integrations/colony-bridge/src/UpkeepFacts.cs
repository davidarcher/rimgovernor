using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Read-only upkeep evidence. Missing sections remain unknown, never empty.
    internal static class UpkeepFacts
    {
        internal static object Read(Map map, List<Pawn> people, List<Thing> things)
        {
            var errors = new Dictionary<string, string>();
            Func<string, Func<object>, object> read = (key, query) => {
                try { return query(); }
                catch (Exception error) { errors[key] = error.GetType().Name + ": " + error.Message; return null; }
            };
            var workers = people.Where(p => !p.Downed && !p.Drafted && !p.InMentalState).ToList();
            var buildings = things.OfType<Building>().Where(b => b.Faction == Faction.OfPlayerSilentFail).ToList();
            var items = things.Where(t => t.def.category == ThingCategory.Item
                && (t.Faction == null || t.Faction == Faction.OfPlayerSilentFail)).ToList();
            var occupied = new HashSet<IntVec3>(buildings.SelectMany(b => b.OccupiedRect()));
            foreach (var item in items.Where(t => t.IsInValidStorage())) occupied.Add(item.Position);
            return new {
                version = 1, tick = Find.TickManager.TicksGame,
                items = read("items", () => items.OrderBy(t => t.thingIDNumber).Select(t => {
                    var rot = t.TryGetComp<CompRottable>();
                    return new { id = t.GetUniqueLoadID(), defName = t.def.defName, count = t.stackCount,
                        x = t.Position.x, z = t.Position.z, hitPoints = t.HitPoints, maxHitPoints = t.MaxHitPoints,
                        roofed = t.Position.Roofed(map), inStorage = t.IsInValidStorage(),
                        deteriorationRate = t.GetStatValue(StatDefOf.DeteriorationRate),
                        rotTicks = rot != null && rot.Active ? (int?)Math.Max(0, rot.TicksUntilRotAtCurrentTemp) : null,
                        perishable = rot != null && rot.Active, temperature = t.AmbientTemperature,
                        forbidden = t.IsForbidden(Faction.OfPlayerSilentFail), medicine = t.def.IsMedicine,
                        nutrition = t.def.IsNutritionGivingIngestible, burning = t.IsBurning() };
                }).ToList()),
                beds = read("beds", () => buildings.OfType<Building_Bed>().Select(b => new {
                    id = b.GetUniqueLoadID(), defName = b.def.defName, slots = b.SleepingSlotsCount,
                    medical = b.Medical, prisoners = b.ForPrisoners,
                    owners = b.OwnersForReading.Select(p => p.GetUniqueLoadID()).ToList(),
                    users = people.Where(p => p.CurrentBed() == b).Select(p => p.GetUniqueLoadID()).ToList(),
                    accessibleTo = people.Where(p => !b.IsForbidden(p) && p.CanReach(b, PathEndMode.OnCell, Danger.None))
                        .Select(p => p.GetUniqueLoadID()).ToList(),
                    roofed = b.OccupiedRect().All(c => c.Roofed(map)), temperature = b.AmbientTemperature
                }).ToList()),
                storageCells = read("storageCells", () => map.AllCells.Where(c => c.GetSlotGroup(map) != null)
                    .Select(c => new { x = c.x, z = c.z, roofed = c.Roofed(map),
                        occupied = c.GetThingList(map).Any(t => t.def.category == ThingCategory.Item) }).ToList()),
                structures = read("structures", () => buildings.Select(b => new {
                    id = b.GetUniqueLoadID(), defName = b.def.defName, x = b.Position.x, z = b.Position.z,
                    hitPoints = b.HitPoints, maxHitPoints = b.MaxHitPoints, burning = b.IsBurning(),
                    home = b.OccupiedRect().All(c => map.areaManager.Home[c]),
                    holdsRoof = b.def.holdsRoof, stuff = b.Stuff?.defName,
                    roofed = b.OccupiedRect().All(c => c.Roofed(map))
                }).ToList()),
                fires = read("fires", () => things.OfType<Fire>().Select(f => new {
                    id = f.GetUniqueLoadID(), x = f.Position.x, z = f.Position.z, size = f.fireSize,
                    home = map.areaManager.Home[f.Position],
                    safeWorkers = workers.Where(p => p.CanReach(f, PathEndMode.Touch, Danger.None))
                        .Select(p => p.GetUniqueLoadID()).ToList()
                }).ToList()),
                filth = read("filth", () => things.OfType<Filth>()
                    .Select(f => new { id = f.GetUniqueLoadID(), x = f.Position.x, z = f.Position.z,
                        home = map.areaManager.Home[f.Position], thickness = f.thickness, room = f.GetRoom()?.Role?.defName,
                        cleanable = workers.Any(p => new WorkGiver_CleanFilth().HasJobOnThing(p, f)) }).ToList()),
                home = read("home", () => new {
                    protectedCells = occupied.OrderBy(c => c.x).ThenBy(c => c.z).Select(c => new {
                        x = c.x, z = c.z, home = map.areaManager.Home[c], roofed = c.Roofed(map)
                    }).ToList()
                }),
                people = read("people", () => people.Select(p => new {
                    id = p.GetUniqueLoadID(), rest = p.needs?.rest?.CurLevelPercentage,
                    recreation = p.needs?.joy?.CurLevelPercentage, mood = p.needs?.mood?.CurLevelPercentage,
                    comfortableMin = p.GetStatValue(StatDefOf.ComfyTemperatureMin),
                    comfortableMax = p.GetStatValue(StatDefOf.ComfyTemperatureMax),
                    temperature = p.AmbientTemperature, job = p.CurJob?.def?.defName,
                    playerForcedJob = p.CurJob?.playerForced ?? false,
                    apparel = p.apparel?.WornApparel.Select(a => new { id = a.GetUniqueLoadID(),
                        defName = a.def.defName, hitPoints = a.HitPoints, maxHitPoints = a.MaxHitPoints }).ToList()
                }).ToList()),
                animals = read("animals", () => map.mapPawns.AllPawnsSpawned.Where(p => !p.Dead && p.RaceProps.Animal
                    && p.Faction == Faction.OfPlayerSilentFail).Select(p => {
                        var needsPen = AnimalPenUtility.NeedsToBeManagedByRope(p);
                        var pen = needsPen ? AnimalPenUtility.GetCurrentPenOf(p, false) : null;
                        var suitable = needsPen ? AnimalPenUtility.ClosestSuitablePen(p, false) : null;
                        return new {
                        id = p.GetUniqueLoadID(), defName = p.def.defName, x = p.Position.x, z = p.Position.z,
                        food = p.needs?.food?.CurLevelPercentage, diet = p.RaceProps.foodType.ToString(),
                        requiresPen = needsPen, contained = needsPen ? (bool?)(pen != null) : null,
                        pen = pen?.parent.GetUniqueLoadID(), suitablePen = suitable?.parent.GetUniqueLoadID(),
                        reachableStoredFeed = items.Where(t => t.def.IsNutritionGivingIngestible
                            && !t.def.IsDrug && t.IngestibleNow && p.WillEat(t)
                            && !t.IsForbidden(p) && p.CanReach(t, PathEndMode.Touch, Danger.None)
                            && (p.playerSettings?.AreaRestrictionInPawnCurrentMap == null
                                || p.playerSettings.AreaRestrictionInPawnCurrentMap[t.Position]))
                            .Select(t => new { id = t.GetUniqueLoadID(), count = t.stackCount,
                                nutrition = FoodUtility.NutritionForEater(p, t) * t.stackCount }).ToList()
                    }; }).ToList()),
                errors
            };
        }
    }
}
