using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Issue #230: the default research ladder walked on the Core tribal
    // baseline without a player target. Prepare seeds only the first rung a
    // few points short of done so the second rung is selected within a
    // minute-scale watch; the simple research bench is the service's own to
    // build (#254). The starter shell it furnishes is the fixture's: a
    // roofed wood hut with a sleeping spot per colonist (as
    // test/basic_comfort_prepare stages it) so the initial shelter is met
    // and the bench rung is not held behind a whole shell build. Audit
    // reads the live research state back.
    public sealed class ResearchLadderFixture
    {
        [Tool("test/research_ladder_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Disposable fixture: advance the named project (default Stonecutting) to 97% of its base cost (IsFinished compares real progress to baseCost; a tribal colony still owes the tech-level factor on the rest), build one roofed wood hut with a sleeping spot per colonist and wood and steel beside its door, and move every colonist inside. Spawns no research bench: the research ladder builds its own in the hut.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "ResearchProjectDef to advance (default Stonecutting).")] string project = "Stonecutting")
        {
            var name = string.IsNullOrEmpty(project) ? "Stonecutting" : project;
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused disposable colony required.");
                var player = Faction.OfPlayerSilentFail;
                if (player == null) throw new InvalidOperationException("No player faction.");
                var def = DefDatabase<ResearchProjectDef>.GetNamed(name);
                var manager = Find.ResearchManager;
                var progress = typeof(ResearchManager).GetField("progress", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(manager) as Dictionary<ResearchProjectDef, float>;
                if (progress == null) throw new InvalidOperationException("ResearchManager.progress unavailable.");
                progress[def] = def.baseCost * 0.97f;

                var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed)
                    .OrderBy(p => p.thingIDNumber).Take(8).ToList();
                if (people.Count < 1) throw new InvalidOperationException("No colonist to house.");
                var wallDef = DefDatabase<ThingDef>.GetNamedSilentFail("Wall");
                var doorDef = DefDatabase<ThingDef>.GetNamedSilentFail("Door");
                var spotDef = DefDatabase<ThingDef>.GetNamedSilentFail("SleepingSpot");
                if (wallDef == null || doorDef == null || spotDef == null)
                    throw new InvalidOperationException("Wall, Door or SleepingSpot def unavailable in this ruleset.");

                // One 9x9 ring (7x7 inside) with a single east door, sited on
                // heavy-affordance ground the colonists can reach; the middle
                // rows stay free for the bench.
                const int size = 9;
                var anchor = people[0].Position;
                var origin = GenRadial.RadialCellsAround(anchor, 30, true).FirstOrDefault(c =>
                    new CellRect(c.x, c.z, size, size).Cells.All(cell => cell.InBounds(map) && !cell.Fogged(map)
                        && cell.Standable(map) && cell.GetEdifice(map) == null && cell.GetZone(map) == null
                        && cell.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy))
                    && people.All(p => p.CanReach(c, PathEndMode.Touch, Danger.None)));
                if (origin == default) throw new InvalidOperationException("No open reachable area for the fixture hut.");
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
                // A sleeping spot is a 1x2 footprint: seven along the south
                // row (rows 1-2), the eighth at the west end of row 3 (rows
                // 3-4), so none overlaps and rows 5-7 stay free for the bench.
                var spotCells = interior.Take(7).Concat(new[] { interior[14] }).Take(people.Count).ToList();
                foreach (var spotCell in spotCells) {
                    var spot = ThingMaker.MakeThing(spotDef);
                    spot.SetFaction(player); GenSpawn.Spawn(spot, spotCell, map, Rot4.North, WipeMode.Vanish);
                    if (spot.Position != spotCell) throw new InvalidOperationException($"Sleeping spot landed at {spot.Position}, not {spotCell}.");
                }
                // The bench's materials: 75 stuff and 25 steel, which the
                // tribal baseline holds none of. Supply is another goal's
                // domain, so the fixture drops both beside the door.
                for (int i = 0; i < 4; i++) {
                    var wood = ThingMaker.MakeThing(ThingDefOf.WoodLog); wood.stackCount = wood.def.stackLimit;
                    GenPlace.TryPlaceThing(wood, door + IntVec3.East * 2, map, ThingPlaceMode.Near); wood.SetForbidden(false, false);
                }
                var steel = ThingMaker.MakeThing(ThingDefOf.Steel); steel.stackCount = steel.def.stackLimit;
                GenPlace.TryPlaceThing(steel, door + IntVec3.East * 2, map, ThingPlaceMode.Near); steel.SetForbidden(false, false);
                map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                var room = interior[interior.Count / 2].GetRoom(map);
                if (room == null || !room.ProperRoom || room.TouchesMapEdge || room.OpenRoofCount > 0)
                    throw new InvalidOperationException($"Fixture hut is not an enclosed roofed room: room={room?.ID} proper={room?.ProperRoom} edge={room?.TouchesMapEdge} openRoof={room?.OpenRoofCount} cells={room?.CellCount}");
                room.Temperature = 21f;
                var spots = map.listerBuildings.allBuildingsColonist.OfType<Building_Bed>().Count(b => b.GetRoom() == room);
                if (spots != people.Count) throw new InvalidOperationException($"{spots} sleeping spots stand in the hut, not {people.Count}.");
                var free = interior.Skip(interior.Count - people.Count).ToList();
                for (int i = 0; i < people.Count; i++) {
                    var pawn = people[i];
                    pawn.jobs?.StopAll();
                    pawn.Position = free[i]; pawn.Notify_Teleported(true, true);
                }

                return new { success = true, project = name, progress = def.ProgressPercent, finished = def.IsFinished,
                    current = manager.GetProject()?.defName, researchBenches = map.listerBuildings.allBuildingsColonist.OfType<Building_ResearchBench>().Count(),
                    hut = new { origin = origin.ToString(), door = door.ToString(), room = room.ID, cells = room.CellCount, sleepingSpots = spots },
                    tick = Find.TickManager.TicksGame };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/research_ladder_audit", Description = "Private read-only fixture: the current research project, every finished project, the named project's progress, research benches and researchers.")]
        public async Task<object> Audit(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "ResearchProjectDef to report progress for (default Stonecutting).")] string project = "Stonecutting")
        {
            var name = string.IsNullOrEmpty(project) ? "Stonecutting" : project;
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null) throw new InvalidOperationException("No current map.");
                var def = DefDatabase<ResearchProjectDef>.GetNamed(name);
                var finished = DefDatabase<ResearchProjectDef>.AllDefsListForReading.Where(p => p.IsFinished).Select(p => p.defName).OrderBy(p => p).ToArray();
                var researchers = map.mapPawns.FreeColonistsSpawned.Where(p => p.workSettings != null && p.workSettings.WorkIsActive(WorkTypeDefOf.Research)).Select(p => p.LabelShort).ToArray();
                var current = Find.ResearchManager.GetProject();
                return new { success = true, project = name, finished = def.IsFinished, progress = def.ProgressPercent,
                    current = current?.defName, currentProgress = current?.ProgressPercent, finishedProjects = finished, researchers,
                    researchBenches = map.listerBuildings.allBuildingsColonist.OfType<Building_ResearchBench>().Count(), tick = Find.TickManager.TicksGame };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
