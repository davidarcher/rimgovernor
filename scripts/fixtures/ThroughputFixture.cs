using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Test-only onset measurement. No pawn stats, simulation ticks or resources are edited.
    public sealed class ThroughputFixture
    {
        private static Game session;
        private static int deadline;
        private static int? onset;
        private static long? onsetAtMs;
        private static Pawn animal;
        private static string operation;
        private static bool applied;
        private static bool patched;

        [Tool("test/render_suspend", Description = "Private acceptance only: lease camera suspension for at most 30 seconds; zero releases. No game clock changes.")]
        public async Task<object> Render(IRimBridgeContext ctx, CancellationToken cancellationToken, int seconds = 0)
        {
            if (seconds < 0 || seconds > 30) throw new ArgumentOutOfRangeException(nameof(seconds));
            return await ctx.MainThread.InvokeAsync(() =>
            {
                RenderDemandDriver.TestSuspendUntil = seconds == 0 ? 0 : UnityEngine.Time.realtimeSinceStartup + seconds;
                return RenderDemandDriver.Lease(0);
            }, cancellationToken);
        }

        [Tool("test/throughput_event", Description = "Disposable tick-scheduled native pause, speed change or animal mental-state onset; excluded from production builds.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken,
            string op = "status", int afterTicks = 7)
        {
            return await ctx.MainThread.InvokeAsync(() =>
            {
                if (op != "status")
                {
                    if (op != "pause" && op != "speed" && op != "hostile") throw new ArgumentException("Unknown fixture event");
                    if (afterTicks < 1 || afterTicks > 6000) throw new ArgumentException("Invalid tick offset");
                    if (Current.Game == null || Find.CurrentMap == null || !Find.TickManager.Paused)
                        throw new InvalidOperationException("Requires a paused private game");
                    animal = null;
                    if (op == "hostile")
                    {
                        var colonists = Find.CurrentMap.mapPawns.FreeColonistsSpawned;
                        animal = Find.CurrentMap.mapPawns.AllPawnsSpawned.FirstOrDefault(p =>
                            p.RaceProps.Animal && p.Faction == null && !p.Downed && !p.Dead && !p.InMentalState
                            && colonists.Any(c => c.Position.DistanceTo(p.Position) <= 25));
                        if (animal == null) throw new InvalidOperationException("Fixture needs a conscious wild animal within 25 cells");
                    }
                    if (!patched)
                    {
                        new Harmony("rimgovernor.throughput-fixture").Patch(AccessTools.Method(typeof(TickManager), "DoSingleTick"),
                            postfix: new HarmonyMethod(typeof(ThroughputFixture), nameof(Tick)) { priority = Priority.First });
                        patched = true;
                    }
                    session = Current.Game; operation = op;
                    deadline = Find.TickManager.TicksGame + afterTicks; onset = null; onsetAtMs = null; applied = false;
                }
                return (object)new { success = true, eventKind = operation, deadline, onsetTick = onset, onsetAtMs, applied,
                    animal = animal?.GetUniqueLoadID(), tick = Find.TickManager.TicksGame };
            }, cancellationToken);
        }

        // Speed-matrix stage (issue #111): one staged colony the harness saves
        // once and reloads per clock speed, so every speed plays the same map,
        // pawns, stacks and wall run. Storage and construction only; needs are
        // frozen by test/freeze_needs after each reload (it is per game).
        [Tool("test/throughput_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: clear rock-chunk debris, register a legal Steel stockpile, spawn unforbidden Steel stacks outside it, pick one contiguous run of legal WoodLog Wall cells, un-forbid existing wood and put every capable colonist on Construct/Haul with a Work timetable. No spawned resources beyond the Steel, no skill or tick changes, no placement.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken, int itemCount = 4, int wallSegments = 6)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                if (itemCount < 1 || itemCount > 8) return Refuse("Use 1..8 items.");
                if (wallSegments < 1 || wallSegments > 12) return Refuse("Use 1..12 wall segments.");
                // Loose rock chunks trip the upkeep census row limit and starve
                // MaintainStorage of its items fact (see StorageHaulFixture).
                var chunks = map.listerThings.AllThings
                    .Where(t => t.def.category == ThingCategory.Item && t.def.defName.StartsWith("Chunk")).ToList();
                foreach (var chunk in chunks) { try { chunk.Destroy(DestroyMode.Vanish); } catch { } }
                var people = map.mapPawns.FreeColonistsSpawned
                    .Where(p => !p.Dead && !p.Downed && !p.Drafted && !p.InMentalState && p.workSettings != null && p.timetable != null && p.skills != null
                        && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction) && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling)
                        && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) && p.health.capacities.CapableOf(PawnCapacityDefOf.Moving)
                        && p.skills.GetSkill(SkillDefOf.Construction).Level >= ThingDefOf.Wall.constructionSkillPrerequisite)
                    .OrderBy(p => p.thingIDNumber).ToList();
                if (people.Count < 3) return Refuse("At least three healthy colonists capable of construction and hauling are required.");
                var anchor = people[0];
                var wood = map.listerThings.ThingsOfDef(ThingDefOf.WoodLog)
                    .Where(t => t.Spawned && t.stackCount > 0 && !t.Position.Fogged(map) && (t.Faction == null || t.Faction == player)
                        && anchor.CanReach(t, PathEndMode.Touch, Danger.None))
                    .OrderBy(t => t.thingIDNumber).ToList();
                var costs = ThingDefOf.Wall.CostListAdjusted(ThingDefOf.WoodLog, false);
                var woodCost = costs.Where(c => c.thingDef == ThingDefOf.WoodLog).Sum(c => c.count);
                if (woodCost <= 0 || wood.Sum(t => (long)t.stackCount) < (long)woodCost * wallSegments)
                    return Refuse("Existing reachable wood cannot cover the requested wall run; fixture will not spawn wood.");
                foreach (var stack in wood) stack.SetForbidden(false, false);
                bool ClearCell(IntVec3 c) => c.InBounds(map) && !c.Fogged(map) && c.Standable(map) && c.GetRoof(map) == null
                    && c.GetEdifice(map) == null && map.zoneManager.ZoneAt(c) == null
                    && c.GetThingList(map).All(t => t is Plant)
                    && anchor.CanReach(c, PathEndMode.Touch, Danger.None);
                bool WallCell(IntVec3 c) => ClearCell(c)
                    && !c.GetThingList(map).Any(t => t is Plant && t.def.plant != null && t.def.plant.IsTree)
                    && GenConstruct.CanPlaceBlueprintAt(ThingDefOf.Wall, c, Rot4.North, map, false, null, null, ThingDefOf.WoodLog).Accepted;
                // One contiguous run, east-west or north-south, within 14 cells
                // of the anchor pawn: every segment is a legal wall cell.
                var run = new List<IntVec3>();
                foreach (var start in GenRadial.RadialCellsAround(anchor.Position, 14, true).Take(1024)) {
                    foreach (var step in new[] { IntVec3.East, IntVec3.North }) {
                        var cells = Enumerable.Range(0, wallSegments).Select(i => start + step * i).ToList();
                        if (cells.All(WallCell)) { run = cells; break; }
                    }
                    if (run.Count > 0) break;
                }
                if (run.Count != wallSegments) return Refuse("Bounded nearby search found no contiguous run of legal wall cells.");
                var occupied = new HashSet<IntVec3>(run);
                // The stockpile and the stacks stay clear of the run and of each
                // other; each stack needs its own storage cell.
                var clear = GenRadial.RadialCellsAround(anchor.Position, 30, true)
                    .Where(c => ClearCell(c) && !occupied.Contains(c) && run.All(w => w.DistanceToSquared(c) >= 9)).ToList();
                var storageCells = clear.Take(itemCount).ToList();
                if (storageCells.Count != itemCount) return Refuse("Too few clear cells for the stockpile.");
                var itemCells = new List<IntVec3>();
                foreach (var c in clear) {
                    if (itemCells.Count == itemCount) break;
                    if (storageCells.Any(s => s.DistanceToSquared(c) < 16)) continue;
                    if (itemCells.Any(o => o.DistanceToSquared(c) < 4)) continue;
                    itemCells.Add(c);
                }
                if (itemCells.Count != itemCount) return Refuse("Too few well-separated clear cells for the Steel stacks.");
                var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                map.zoneManager.RegisterZone(zone);
                zone.GetStoreSettings().filter.SetDisallowAll();
                zone.GetStoreSettings().filter.SetAllow(ThingDefOf.Steel, true);
                zone.GetStoreSettings().Priority = StoragePriority.Normal;
                foreach (var c in storageCells) zone.AddCell(c);
                var items = new List<object>();
                foreach (var cell in itemCells) {
                    var stack = ThingMaker.MakeThing(ThingDefOf.Steel);
                    stack.stackCount = 25;
                    GenSpawn.Spawn(stack, cell, map);
                    stack.SetForbidden(false, false);
                    items.Add(new { id = stack.GetUniqueLoadID(), x = cell.x, z = cell.z, count = stack.stackCount });
                }
                foreach (var pawn in people) {
                    pawn.workSettings.SetPriority(WorkTypeDefOf.Construction, 1);
                    pawn.workSettings.SetPriority(WorkTypeDefOf.Hauling, 1);
                    for (var hour = 0; hour < 24; hour++) pawn.timetable.SetAssignment(hour, TimeAssignmentDefOf.Work);
                }
                map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new {
                    success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame, chunksCleared = chunks.Count,
                    pawns = people.Select(p => new { id = p.GetUniqueLoadID(), name = p.LabelShort, construction = p.skills.GetSkill(SkillDefOf.Construction).Level }).ToArray(),
                    resourceDefName = ThingDefOf.Steel.defName, stackSize = 25,
                    storageCells = storageCells.Select(c => new { x = c.x, z = c.z }).ToArray(),
                    items = items.ToArray(),
                    wood = wood.Select(t => new { id = t.GetUniqueLoadID(), count = t.stackCount, x = t.Position.x, z = t.Position.z }).ToArray(),
                    wallCost = costs.Select(c => new { defName = c.thingDef.defName, count = c.count }).ToArray(),
                    sites = run.Select(c => new { defName = "Wall", stuff = "WoodLog", rotation = "north", x = c.x, z = c.z }).ToArray(),
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        // Outcome read for the speed matrix. Cells are "x:z" lists so the same
        // read works after a reload, which mints a new game object; identity
        // is checked from the arguments instead of a prepared-game static.
        [Tool("test/throughput_control", Description = "UNSAFE FOR MODEL EXECUTION. Private staged-fixture read: Steel stacks and units on the given storage cells and elsewhere on the map, and built walls, frames and blueprints on the given wall cells. Never mutates.")]
        public async Task<object> Control(IRimBridgeContext ctx, CancellationToken cancellationToken,
            string colonyId, string loadToken, int mapId, string storageCells = "", string wallCells = "")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var identity = Current.Game?.GetComponent<ColonyIdentity>();
                if (map == null || identity == null || identity.ColonyId != colonyId || identity.LoadToken != loadToken || map.uniqueID != mapId)
                    return Refuse("Colony/load/map identity changed.");
                var storage = ParseCells(storageCells); var walls = ParseCells(wallCells);
                if (storage == null || walls == null) return Refuse("Cells must be x:z lists.");
                var stored = new HashSet<IntVec3>(storage);
                var steel = map.listerThings.ThingsOfDef(ThingDefOf.Steel).Where(t => t.Spawned).ToList();
                var inStorage = steel.Where(t => stored.Contains(t.Position)).ToList();
                var loose = steel.Where(t => !stored.Contains(t.Position)).ToList();
                int built = 0, frames = 0, blueprints = 0;
                var segments = new List<object>();
                foreach (var cell in walls) {
                    var things = cell.InBounds(map) ? cell.GetThingList(map) : new List<Thing>();
                    var wall = things.FirstOrDefault(t => t is Building && t.def == ThingDefOf.Wall);
                    var frame = things.OfType<Frame>().FirstOrDefault(f => f.def.entityDefToBuild == ThingDefOf.Wall);
                    var blueprint = things.OfType<Blueprint_Build>().FirstOrDefault(b => b.def.entityDefToBuild == ThingDefOf.Wall);
                    if (wall != null) built++; else if (frame != null) frames++; else if (blueprint != null) blueprints++;
                    segments.Add(new { x = cell.x, z = cell.z, built = wall != null, frame = frame != null, blueprint = blueprint != null,
                        workLeft = frame?.WorkLeft ?? 0f });
                }
                return new { success = true, tick = Find.TickManager.TicksGame, paused = Find.TickManager.Paused,
                    storedStacks = inStorage.Count, storedUnits = inStorage.Sum(t => t.stackCount),
                    looseStacks = loose.Count, looseUnits = loose.Sum(t => t.stackCount),
                    carriedUnits = map.mapPawns.FreeColonistsSpawned.Sum(p => p.carryTracker?.CarriedThing?.def == ThingDefOf.Steel ? p.carryTracker.CarriedThing.stackCount : 0),
                    wallsBuilt = built, wallFrames = frames, wallBlueprints = blueprints, segments = segments.ToArray() };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static List<IntVec3> ParseCells(string list)
        {
            var cells = new List<IntVec3>();
            if (string.IsNullOrWhiteSpace(list)) return cells;
            foreach (var part in list.Split(',')) {
                var xz = part.Trim().Split(':');
                if (xz.Length != 2 || !int.TryParse(xz[0], out var x) || !int.TryParse(xz[1], out var z)) return null;
                cells.Add(new IntVec3(x, 0, z));
            }
            return cells;
        }

        private static object Refuse(string reason) => new { success = false, reason };

        private static void Tick()
        {
            if (!ReferenceEquals(Current.Game, session) || onset.HasValue || Find.TickManager.TicksGame < deadline) return;
            onset = Find.TickManager.TicksGame;
            onsetAtMs = (DateTime.UtcNow.Ticks - 621355968000000000L) / TimeSpan.TicksPerMillisecond;
            if (operation == "hostile")
                applied = animal.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.Manhunter);
            else
            {
                Find.TickManager.CurTimeSpeed = operation == "pause" ? TimeSpeed.Paused : TimeSpeed.Fast;
                applied = true;
            }
        }
    }
}
