using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only (facility/basic-comfort). Stages the
    // state EnsureBasicComfort starts from: a roofed starter hut the colonists
    // sleep and eat in with no table, seat or recreation source anywhere, a
    // fresh "ate without table" memory on every colonist, meals and wood on
    // hand. The controller then furnishes the hut through its own planning;
    // the fixture only observes (BasicComfortFixture below) and re-seeds
    // hunger once a table and seat stand so the meal that proves the point
    // happens inside the watch window.
    public sealed class ComfortFixture
    {
        [Tool("test/basic_comfort_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Disposable foothold-comfort fixture: builds one roofed wood hut with a sleeping spot per colonist and a meal stockpile inside, moves every colonist into it, removes every table, seat and recreation building on the map, gives each colonist a fresh AteWithoutTable memory and mild hunger, drops wood beside the door and arms the observer that re-seeds hunger once a table and seat stand.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed)
                    .OrderBy(p => p.thingIDNumber).Take(8).ToList();
                if (people.Count < 1) return Refuse("No colonist to house.");
                var wallDef = DefDatabase<ThingDef>.GetNamedSilentFail("Wall");
                var doorDef = DefDatabase<ThingDef>.GetNamedSilentFail("Door");
                var spotDef = DefDatabase<ThingDef>.GetNamedSilentFail("SleepingSpot");
                var ateWithoutTable = DefDatabase<ThoughtDef>.GetNamedSilentFail("AteWithoutTable");
                if (wallDef == null || doorDef == null || spotDef == null || ateWithoutTable == null)
                    return Refuse("Wall, Door, SleepingSpot or AteWithoutTable def unavailable in this ruleset.");
                var requiredConstruction = new[] { "Table1x2c", "DiningChair", "HorseshoesPin" }
                    .Select(d => DefDatabase<ThingDef>.GetNamed(d).constructionSkillPrerequisite).Max();
                var construction = DefDatabase<WorkTypeDef>.GetNamed("Construction");
                var builder = people.Where(p => !p.WorkTypeIsDisabled(construction) && !p.skills.GetSkill(SkillDefOf.Construction).TotallyDisabled)
                    .OrderByDescending(p => p.skills.GetSkill(SkillDefOf.Construction).Level).FirstOrDefault();
                if (builder == null) return Refuse("Fixture needs a construction-capable colonist.");
                builder.skills.GetSkill(SkillDefOf.Construction).Level = Math.Max(requiredConstruction, builder.skills.GetSkill(SkillDefOf.Construction).Level);

                // No table, seat or recreation source may pre-exist: the goal
                // must build them. Debug starts spawn none, but a cached world
                // may carry ruins.
                foreach (var b in map.listerBuildings.allBuildingsColonist.Concat(map.listerBuildings.allBuildingsNonColonist)
                    .Where(b => b.def.surfaceType == SurfaceType.Eat || b.def.building.isSittable || b.def.building.joyKind != null).ToList())
                    b.Destroy(DestroyMode.Vanish);

                // One 9x9 ring (7x7 inside) with a single east door, sited on
                // heavy-affordance ground the colonists can reach.
                const int size = 9;
                var origin = FixtureHut.FindSite(map, people, size);
                if (origin == default) return Refuse("No open reachable area for the fixture hut.");
                var door = new IntVec3(origin.x + size - 1, 0, origin.z + size / 2);
                var rect = new CellRect(origin.x, origin.z, size, size);
                foreach (var c in rect.Cells) {
                    foreach (var t in c.GetThingList(map).Where(t => t is Plant || t.def.category == ThingCategory.Item).ToList()) t.Destroy(DestroyMode.Vanish);
                    if (c == door) {
                        var d = (Building)ThingMaker.MakeThing(doorDef, ThingDefOf.WoodLog);
                        d.SetFaction(player); GenSpawn.Spawn(d, c, map);
                    } else if (rect.IsOnEdge(c)) {
                        var w = (Building)ThingMaker.MakeThing(wallDef, ThingDefOf.WoodLog);
                        w.SetFaction(player); GenSpawn.Spawn(w, c, map);
                    }
                    map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                }
                var interior = rect.ContractedBy(1).Cells.OrderBy(c => c.z).ThenBy(c => c.x).ToList();
                var index = 0;
                // Meals along the south wall, sleeping spots on the row above:
                // the middle rows stay free for the table and seat.
                var spots = new List<string>();
                foreach (var pawn in people) {
                    var spot = ThingMaker.MakeThing(spotDef);
                    var spotCell = interior[7 + index++];
                    spot.SetFaction(player); GenSpawn.Spawn(spot, spotCell, map, Rot4.North, WipeMode.Vanish);
                    if (spot.Position != spotCell) return Refuse($"Sleeping spot landed at {spot.Position}, not {spotCell}.");
                    spots.Add(spot.GetUniqueLoadID());
                }
                var mealDef = ThingDefOf.MealSimple;
                var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                map.zoneManager.RegisterZone(zone); zone.GetStoreSettings().filter.SetDisallowAll();
                zone.GetStoreSettings().filter.SetAllow(mealDef, true);
                for (int i = 0; i < 7; i++) {
                    var cell = interior[i]; zone.AddCell(cell);
                    var meal = ThingMaker.MakeThing(mealDef); meal.stackCount = mealDef.stackLimit;
                    GenSpawn.Spawn(meal, cell, map); meal.SetForbidden(false, false);
                }
                for (int i = 0; i < 4; i++) {
                    var wood = ThingMaker.MakeThing(ThingDefOf.WoodLog); wood.stackCount = wood.def.stackLimit;
                    GenPlace.TryPlaceThing(wood, door + IntVec3.East * 2, map, ThingPlaceMode.Near); wood.SetForbidden(false, false);
                }
                foreach (var item in map.listerThings.AllThings.Where(t => t.def.category == ThingCategory.Item).ToList()) item.SetForbidden(false, false);

                map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                var room = interior[interior.Count / 2].GetRoom(map);
                if (room == null || !room.ProperRoom || room.TouchesMapEdge || room.OpenRoofCount > 0)
                    return Refuse($"Fixture hut is not an enclosed roofed room: room={room?.ID} proper={room?.ProperRoom} edge={room?.TouchesMapEdge} openRoof={room?.OpenRoofCount} cells={room?.CellCount}");
                room.Temperature = 21f;

                // Everyone starts inside, fed until the observer re-seeds hunger,
                // carrying the memory the table is meant to stop recurring.
                var free = interior.Skip(14).ToList();
                var teleported = 0;
                foreach (var pawn in people) {
                    foreach (var h in pawn.health.hediffSet.hediffs.Where(h => h.def.isBad).ToList()) pawn.health.RemoveHediff(h);
                    pawn.playerSettings.AreaRestrictionInPawnCurrentMap = null;
                    for (int hour = 0; hour < 24; hour++) pawn.timetable.SetAssignment(hour, TimeAssignmentDefOf.Anything);
                    pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    pawn.Position = free[teleported++ % free.Count];
                    pawn.Notify_Teleported(true, true);
                    pawn.needs.food.CurLevelPercentage = .9f;
                    pawn.needs.rest.CurLevelPercentage = .9f;
                    pawn.needs.joy.CurLevelPercentage = .6f;
                    pawn.needs.mood.thoughts.memories.TryGainMemory(ateWithoutTable);
                }
                var fixture = map.GetComponent<BasicComfortFixture>();
                if (fixture == null) {
                    fixture = new BasicComfortFixture(map);
                    map.components.Add(fixture);
                }
                fixture.Arm(people.Select(p => p.GetUniqueLoadID()).ToList());
                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new { success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame, colonists = people.Select(p => p.GetUniqueLoadID()).ToList(),
                    builder = builder.GetUniqueLoadID(), requiredConstruction, room = room.ID, sleepingSpots = spots,
                    door = new { x = door.x, z = door.z }, comfort = ComfortFacts.Read(map) };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/basic_comfort_audit", Description = "Read the disposable foothold-comfort fixture: whether hunger was re-seeded once a table and seat stood, which colonists have since eaten at a table, and the age of every AteWithoutTable memory. Changes nothing.")]
        public async Task<object> Audit(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var fixture = map?.GetComponent<BasicComfortFixture>();
                if (fixture == null) return Refuse("Fixture not armed on this map.");
                var ateWithoutTable = DefDatabase<ThoughtDef>.GetNamed("AteWithoutTable");
                var people = map.mapPawns.FreeColonistsSpawned.Where(p => fixture.People.Contains(p.GetUniqueLoadID())).ToList();
                return new { success = true, tick = Find.TickManager.TicksGame, fixture.Armed, fixture.TriggerTick,
                    colonists = people.Select(p => new {
                        id = p.GetUniqueLoadID(), food = p.needs.food.CurLevelPercentage, joy = p.needs.joy?.CurLevelPercentage,
                        ateAtTable = fixture.AteAtTable.Contains(p.GetUniqueLoadID()),
                        ateWithoutTableAges = p.needs.mood.thoughts.memories.Memories.Where(m => m.def == ateWithoutTable).Select(m => m.age).ToList()
                    }).ToList(),
                    comfort = ComfortFacts.Read(map) };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }

    // Test-only observer: once a table and a seat stand it re-seeds hunger so
    // the next meal happens inside the watch, then records which colonists
    // eat while standing at an eating surface. It orders no job.
    public sealed class BasicComfortFixture : MapComponent
    {
        public bool Armed { get; private set; }
        public int TriggerTick { get; private set; }
        public List<string> People { get; private set; } = new List<string>();
        public HashSet<string> AteAtTable { get; } = new HashSet<string>();
        // Hunger is re-seeded one colonist at a time: the hut has one seat,
        // and a colonist who finds it taken eats standing, which is the
        // memory the case asserts against.
        public Queue<string> Pending { get; } = new Queue<string>();
        public string Eating { get; private set; }
        public BasicComfortFixture(Map map) : base(map) { }
        public void Arm(List<string> people)
        {
            Armed = true; TriggerTick = 0; People = people; AteAtTable.Clear(); Pending.Clear(); Eating = null;
        }
        public override void MapComponentTick()
        {
            if (People.Count == 0) return;
            var colonists = map.mapPawns.FreeColonistsSpawned.Where(p => People.Contains(p.GetUniqueLoadID())).ToList();
            if (Armed) {
                var buildings = map.listerBuildings.allBuildingsColonist;
                if (!buildings.Any(b => b.def.defName == "Table1x2c") || !buildings.Any(b => b.def.defName == "DiningChair")) return;
                Armed = false;
                TriggerTick = Find.TickManager.TicksGame;
                foreach (var pawn in colonists) Pending.Enqueue(pawn.GetUniqueLoadID());
                return;
            }
            foreach (var pawn in colonists) {
                if (pawn.CurJob?.def != JobDefOf.Ingest || AteAtTable.Contains(pawn.GetUniqueLoadID())) continue;
                if (GenAdj.CardinalDirections.Any(d => (pawn.Position + d).InBounds(map) && (pawn.Position + d).GetEdifice(map)?.def.surfaceType == SurfaceType.Eat))
                    AteAtTable.Add(pawn.GetUniqueLoadID());
            }
            var eating = Eating == null ? null : colonists.FirstOrDefault(p => p.GetUniqueLoadID() == Eating);
            if (eating != null && !(AteAtTable.Contains(Eating) && eating.CurJob?.def != JobDefOf.Ingest)) return;
            if (Pending.Count == 0) { Eating = null; return; }
            Eating = Pending.Dequeue();
            var next = colonists.FirstOrDefault(p => p.GetUniqueLoadID() == Eating);
            if (next != null) next.needs.food.CurLevelPercentage = .1f;
        }
    }
}
