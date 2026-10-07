using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only. Native random colony generation cannot
    // reliably produce a MaintainStorage deficit (an ordinary, non-deteriorating
    // item sitting outside legal storage) with a deterministic single eligible
    // hauler, so this fixture builds one directly: a legal Steel stockpile zone,
    // Steel stacks outside it, and exactly one existing colonist enabled for
    // Hauling (every other colonist explicitly disabled) so revoking that one
    // pawn's own Hauling priority is a genuine, provable interruption. Only the
    // first stack spawns unforbidden: the hauler's own vanilla work scanner
    // would otherwise pick up every later stack the moment its ordered haul
    // ends, before the planner can renew; test/storage_haul_allow releases a
    // later stack when the case wants the deficit renewed.
    //
    // The same scanner races the planner for an unforbidden stack the moment
    // the clock runs: under a running window at boosted pace the worker's
    // dispatch lands hundreds of ticks after the window starts, and the idle
    // hauler has carried the stack off by then (thing_absent, #328). So the
    // colonists are held on ordinary (not player-forced) Wait jobs whenever
    // an unforbidden stack is waiting for the planner: prepare holds them
    // beside the first stack and allow holds them again beside the released
    // one. A waiting pawn never consults its work scanner, while the
    // planner's own order interrupts the hauler's wait as any ordered job
    // does, and the hauler reads as eligible throughout (no player-forced
    // job, Hauling still enabled). The other colonists are held too: vanilla
    // opportunistic hauling (Pawn_JobTracker.TryOpportunisticJob) ignores
    // the Hauling priority and lets a wandering colonist carry a stack it
    // passes into storage.
    public sealed class StorageHaulFixture
    {
        private static Game preparedGame;
        private static Map preparedMap;
        private static Pawn preparedHauler;
        private static readonly List<Pawn> preparedPeople = new List<Pawn>();

        // Two in-game days: longer than any run's clock windows before the
        // planner's order interrupts it, and finite so a disposable colony
        // that outlives the case still gets its pawn back.
        private const int HoldTicks = 120000;

        private static void Hold(IEnumerable<Pawn> pawns)
        {
            foreach (var pawn in pawns)
            {
                if (pawn == null || !pawn.Spawned || pawn.Dead || pawn.Downed || pawn.Drafted) continue;
                var wait = JobMaker.MakeJob(JobDefOf.Wait, HoldTicks);
                pawn.jobs.StartJob(wait, JobCondition.InterruptForced);
            }
        }

        [Tool("test/storage_haul_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: register one legal Steel stockpile zone, spawn ordinary Steel stacks outside it (only the first unforbidden), and set exactly one existing colonist's Hauling work priority (all others disabled) so RoundsHaulPlanner's MaintainStorage deficit and its single eligible hauler are deterministic. No quest/travel simulation, no new resources beyond the spawned Steel.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken, int itemCount = 2)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                if (itemCount < 1 || itemCount > 4) return Refuse("Use 1..4 items.");
                // Native map generation routinely scatters far more than 256 loose
                // items across a full colony map -- overwhelmingly mined rock
                // chunks (ChunkGranite/ChunkSlate/ChunkSandstone/etc, hundreds per
                // map on mountainous terrain) -- which trips the native upkeep
                // census's own row limit and makes the whole "items" section
                // permanently unavailable, silently starving MaintainStorage (and
                // every other upkeep-driven goal) of the fact it needs. Clear only
                // that rock-chunk debris: it has no role in this or any sibling
                // fixture, whereas indiscriminately destroying every loose item
                // (an earlier version of this fixture did that) also destroyed
                // resource stacks -- e.g. WoodLog -- that GuardedConstructionFixture
                // depends on finding already present when it prepares immediately
                // afterward in the same harness session.
                var looseBefore = map.listerThings.AllThings
                    .Where(t => t.def.category == ThingCategory.Item && t.def.defName.StartsWith("Chunk"))
                    .ToList();
                var looseBeforeCount = looseBefore.Count;
                var destroyAttempted = looseBefore.Count;
                var destroyExceptions = 0;
                foreach (var loose in looseBefore) {
                    try { loose.Destroy(DestroyMode.Vanish); } catch { destroyExceptions++; }
                }
                var looseAfterCount = map.listerThings.AllThings.Count(t => t.def.category == ThingCategory.Item && t.def.defName.StartsWith("Chunk"));
                var sampleDefNames = looseBefore.GroupBy(t => t.def.defName)
                    .OrderByDescending(g => g.Count()).Take(10)
                    .Select(g => new { defName = g.Key, count = g.Count() }).ToArray();
                var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && !p.Drafted && !p.InMentalState)
                    .OrderBy(p => p.thingIDNumber).ToList();
                if (people.Count < 1) return Refuse("At least one existing healthy colonist is required.");
                var hauler = people.FirstOrDefault(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling));
                if (hauler == null) return Refuse("No existing colonist can be enabled for Hauling.");
                // Only individual clear cells are required (one for the legal
                // storage zone, one per spawned item stack) -- not a whole clear
                // rectangle, which a real generated colony map rarely offers
                // near its starting colonists.
                bool ClearCell(IntVec3 c) => c.InBounds(map) && !c.Fogged(map) && c.Standable(map)
                    && c.GetEdifice(map) == null && map.zoneManager.ZoneAt(c) == null
                    && c.GetThingList(map).All(t => t is Plant)
                    && hauler.CanReach(c, PathEndMode.Touch, Danger.None);
                var clearCells = GenRadial.RadialCellsAround(hauler.Position, 40, true).Where(ClearCell).ToList();
                if (clearCells.Count < itemCount + 1) return Refuse("Too few clear cells reachable by the eligible hauler.");
                var storageCell = clearCells[0];
                var itemCells = new List<IntVec3>();
                foreach (var c in clearCells.Skip(1)) {
                    if (itemCells.Count == itemCount) break;
                    if (c.DistanceToSquared(storageCell) < 4) continue;
                    if (itemCells.Any(o => o.DistanceToSquared(c) < 4)) continue;
                    itemCells.Add(c);
                }
                if (itemCells.Count != itemCount) return Refuse("Too few well-separated clear cells reachable by the eligible hauler.");
                // Every colonist's Hauling priority is set explicitly (not just the
                // hauler's): a stray already-enabled second hauler would make the
                // later interruption proof (revoke the one hauler) non-deterministic.
                foreach (var pawn in people)
                    pawn.workSettings.SetPriority(WorkTypeDefOf.Hauling, pawn == hauler ? 1 : 0);
                var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                map.zoneManager.RegisterZone(zone);
                zone.GetStoreSettings().filter.SetDisallowAll();
                zone.GetStoreSettings().filter.SetAllow(ThingDefOf.Steel, true);
                zone.GetStoreSettings().Priority = StoragePriority.Normal;
                zone.AddCell(storageCell);
                var itemIds = new List<string>();
                var items = new List<object>();
                foreach (var cell in itemCells) {
                    var stack = ThingMaker.MakeThing(ThingDefOf.Steel);
                    stack.stackCount = 25;
                    GenSpawn.Spawn(stack, cell, map);
                    stack.SetForbidden(itemIds.Count > 0, false);
                    itemIds.Add(stack.GetUniqueLoadID());
                    items.Add(new { id = stack.GetUniqueLoadID(), x = cell.x, z = cell.z, count = stack.stackCount,
                        forbidden = stack.IsForbidden(player) });
                }
                map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                Hold(people);
                preparedGame = Current.Game; preparedMap = map; preparedHauler = hauler;
                preparedPeople.Clear(); preparedPeople.AddRange(people);
                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new {
                    success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame,
                    haulerId = hauler.GetUniqueLoadID(),
                    haulerJob = hauler.CurJobDef?.defName,
                    heldJobs = people.Select(p => p.CurJobDef?.defName).ToArray(),
                    itemIds = itemIds.ToArray(),
                    items = items.ToArray(),
                    looseItemsBefore = looseBeforeCount,
                    looseItemsAfter = looseAfterCount,
                    destroyAttempted,
                    destroyExceptions,
                    sampleDefNames,
                    resourceDefName = ThingDefOf.Steel.defName,
                    storageCell = new { x = storageCell.x, z = storageCell.z },
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/storage_haul_control", Description = "UNSAFE FOR MODEL EXECUTION. Private prepared-fixture control: read whether a prepared item is currently spawned on the prepared map and its exact cell (by itemId), or sum whatever Steel sits at an exact cell (by x/z, since a merged delivery destroys the incoming stack's own id). Never mutates.")]
        public async Task<object> Control(IRimBridgeContext ctx, CancellationToken cancellationToken,
            string colonyId, string loadToken, int mapId, string itemId = null, bool byCell = false, int x = 0, int z = 0)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var identity = Current.Game?.GetComponent<ColonyIdentity>();
                if (Current.Game != preparedGame || map != preparedMap || map == null || identity == null
                    || identity.ColonyId != colonyId || identity.LoadToken != loadToken || map.uniqueID != mapId)
                    return Refuse("Prepared paused colony/load/map identity changed.");
                if (byCell) {
                    var cell = new IntVec3(x, 0, z);
                    if (!cell.InBounds(map)) return Refuse("Cell out of bounds.");
                    var stacks = cell.GetThingList(map).Where(t => t.def == ThingDefOf.Steel).ToList();
                    var total = stacks.Sum(t => t.stackCount);
                    var anyForbidden = stacks.Any(t => t.IsForbidden(Faction.OfPlayerSilentFail));
                    var zone = map.zoneManager.ZoneAt(cell) as Zone_Stockpile;
                    return new { success = true, spawned = stacks.Count > 0, count = total, forbidden = anyForbidden,
                        inStockpile = zone != null, x = cell.x, z = cell.z };
                }
                var thing = map.listerThings.AllThings.FirstOrDefault(t => t.GetUniqueLoadID() == itemId);
                if (thing == null || !thing.Spawned) return new { success = true, spawned = false };
                return new { success = true, spawned = true, x = thing.Position.x, z = thing.Position.z,
                    count = thing.stackCount, forbidden = thing.IsForbidden(Faction.OfPlayerSilentFail) };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/storage_haul_allow", Description = "UNSAFE FOR MODEL EXECUTION. Private prepared-fixture control: unforbid one prepared Steel stack (by itemId) so it becomes a MaintainStorage deficit the planner must renew for, and hold the prepared colonists on Wait jobs so no work scanner or opportunistic haul takes the stack first. Mutates only that stack's forbidden flag and the prepared colonists' current jobs.")]
        public async Task<object> Allow(IRimBridgeContext ctx, CancellationToken cancellationToken,
            string colonyId, string loadToken, int mapId, string itemId)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var identity = Current.Game?.GetComponent<ColonyIdentity>();
                if (Current.Game != preparedGame || map != preparedMap || map == null || identity == null
                    || identity.ColonyId != colonyId || identity.LoadToken != loadToken || map.uniqueID != mapId)
                    return Refuse("Prepared paused colony/load/map identity changed.");
                var thing = map.listerThings.AllThings.FirstOrDefault(t => t.GetUniqueLoadID() == itemId);
                if (thing == null || !thing.Spawned || thing.def != ThingDefOf.Steel) return Refuse("Prepared Steel stack is not spawned.");
                var hauler = preparedHauler;
                if (hauler == null || !hauler.Spawned || hauler.Dead || hauler.Map != map) return Refuse("Prepared hauler is not spawned on the prepared map.");
                thing.SetForbidden(false, false);
                Hold(preparedPeople.Where(p => p.Map == map));
                return new { success = true, x = thing.Position.x, z = thing.Position.z, count = thing.stackCount,
                    forbidden = thing.IsForbidden(Faction.OfPlayerSilentFail),
                    haulerId = hauler.GetUniqueLoadID(), haulerJob = hauler.CurJobDef?.defName,
                    heldJobs = preparedPeople.Where(p => p.Map == map).Select(p => p.CurJobDef?.defName).ToArray() };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static Zone_Stockpile lootZone;

        // Far resource reach (#520) needs six armed colonists, two free
        // haulers and a quiet storyteller: every existing colonist takes a
        // rifle and Hauling, and generated colonists fill the count. Returns
        // a refusal string or the readiness summary.
        private static object RaiseReadiness(Map map)
        {
            var rifle = DefDatabase<ThingDef>.GetNamed("Gun_BoltActionRifle");
            var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed).ToList();
            var anchor = preparedHauler?.Position ?? people.First().Position;
            var generated = new List<string>();
            while (people.Count(p => p.equipment != null && !p.WorkTagIsDisabled(WorkTags.Violent)) < 6) {
                var pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                    forceGenerateNewPawn: true, colonistRelationChanceFactor: 0f, allowDead: false, allowDowned: false,
                    canGeneratePawnRelations: false, mustBeCapableOfViolence: true, fixedBiologicalAge: 30f, fixedChronologicalAge: 30f));
                var cell = CellFinder.RandomClosewalkCellNear(anchor, map, 6, c => c.Standable(map) && !c.Fogged(map));
                GenSpawn.Spawn(pawn, cell, map);
                pawn.needs.food.CurLevelPercentage = .95f;
                pawn.needs.rest.CurLevelPercentage = .95f;
                people.Add(pawn);
                generated.Add(pawn.GetUniqueLoadID());
                if (generated.Count > 8) return "Could not generate enough armed colonists.";
            }
            var armed = 0;
            foreach (var pawn in people) {
                if (pawn.equipment != null && !pawn.WorkTagIsDisabled(WorkTags.Violent)) {
                    if (pawn.equipment.Primary == null || pawn.equipment.Primary.def != rifle) {
                        var prior = pawn.equipment.Primary;
                        if (prior != null) pawn.equipment.TryDropEquipment(prior, out _, pawn.Position, false);
                        pawn.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(rifle));
                    }
                    if (pawn.equipment.Primary != null) armed++;
                }
                if (pawn.workSettings != null) {
                    if (!pawn.workSettings.Initialized) pawn.workSettings.EnableAndInitialize();
                    foreach (var work in DefDatabase<WorkTypeDef>.AllDefsListForReading)
                        if (!pawn.WorkTypeIsDisabled(work)) pawn.workSettings.SetPriority(work, work == WorkTypeDefOf.Hauling ? 1 : 0);
                }
                pawn.jobs?.EndCurrentJob(JobCondition.InterruptForced);
                if (!preparedPeople.Contains(pawn)) preparedPeople.Add(pawn);
            }
            if (armed < 6) return "Fewer than six colonists could be armed.";
            var haulers = people.Count(p => p.workSettings != null && p.workSettings.WorkIsActive(WorkTypeDefOf.Hauling));
            return new { armed, haulers, generated = generated.ToArray(), colonists = people.Count,
                raidPoints = StorytellerUtility.DefaultThreatPointsNow(map) };
        }

        private static Thing remoteStack;

        [Tool("test/loot_remote_drop", Description = "UNSAFE FOR MODEL EXECUTION. Spawn a forbidden Steel stack on a safe cell near the far map edge, reachable by the prepared hauler and outside Home; extend the prepared stockpile. Tests reach-staged remote loot recovery (#522).")]
        public async Task<object> LootRemoteDrop(IRimBridgeContext ctx, CancellationToken cancellationToken, int count = 75, bool covered = false, bool audit = false)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (audit) {
                    // covered loot (#2302): the stack left its far cell (hauled, merged or gone) and the zone gained its steel.
                    if (remoteStack == null || lootZone == null) return Refuse("No remote stack dropped.");
                    var moved = !remoteStack.Spawned || remoteStack.Destroyed || remoteStack.Position != remoteOrigin;
                    return new { success = true, moved, forbidden = remoteStack.Spawned && remoteStack.IsForbidden(Faction.OfPlayer),
                        delivered = StoredSteel(map) - lootStoredBefore, stock = lootStock };
                }
                if (map == null || map != preparedMap || preparedHauler == null || !Find.TickManager.Paused)
                    return Refuse("Prepared paused storage fixture required.");
                if (count < 1 || count > 75) return Refuse("Use 1..75 units.");
                var center = new IntVec3((int)preparedPeople.Average(p => p.Position.x), 0, (int)preparedPeople.Average(p => p.Position.z));
                int Edge(IntVec3 c) => Math.Min(Math.Min(c.x, map.Size.x - 1 - c.x), Math.Min(c.z, map.Size.z - 1 - c.z));
                var cells = map.AllCells.Where(c => Edge(c) <= 4 && !c.Fogged(map) && c.Standable(map) && c.GetEdifice(map) == null
                    && c.GetThingList(map).All(t => t is Plant) && map.zoneManager.ZoneAt(c) == null
                    && !map.areaManager.Home[c] && preparedHauler.CanReach(c, PathEndMode.Touch, Danger.None))
                    .OrderByDescending(c => c.DistanceToSquared(center)).ToList();
                if (cells.Count == 0) return Refuse("No safe reachable cell near the map edge.");
                lootZone = map.zoneManager.AllZones.OfType<Zone_Stockpile>().First(z => z.GetStoreSettings().filter.Allows(ThingDefOf.Steel));
                foreach (var c in GenRadial.RadialCellsAround(lootZone.Cells[0], 6, true)
                    .Where(c => c.InBounds(map) && c.Standable(map) && c.GetEdifice(map) == null && map.zoneManager.ZoneAt(c) == null).Take(8))
                    lootZone.AddCell(c);
                remoteStack = ThingMaker.MakeThing(ThingDefOf.Steel);
                remoteStack.stackCount = count;
                GenSpawn.Spawn(remoteStack, cells[0], map);
                remoteStack.SetForbidden(true, false);
                remoteOrigin = cells[0];
                lootStock = 0;
                if (covered) {
                    lootStock = StockCovered(map);
                    if (lootStock < 0) return Refuse("Too few free stockpile cells to cover Steel demand.");
                }
                lootStoredBefore = StoredSteel(map);
                return new { success = true, id = remoteStack.GetUniqueLoadID(), x = cells[0].x, z = cells[0].z,
                    distance = cells[0].DistanceTo(center), edge = Edge(cells[0]), forbidden = true, count, stock = lootStock };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static IntVec3 remoteOrigin;
        private static int lootStoredBefore;
        private static int lootStock;

        private static int StoredSteel(Map map) => lootZone.Cells.SelectMany(c => c.GetThingList(map)).Where(t => t.def == ThingDefOf.Steel).Sum(t => t.stackCount);

        // Covers Steel demand (the 200 default floor, #2302): unforbidden full
        // stacks fill the loot stockpile, leaving two cells for deliveries. The
        // count is -1 when the zone is too small to cover the floor.
        private static int StockCovered(Map map)
        {
            var free = lootZone.Cells.Where(c => c.Standable(map) && !c.GetThingList(map).Any(t => t.def.category == ThingCategory.Item)).ToList();
            var stacks = Math.Min(7, free.Count - 2);
            if (stacks < 3) return -1;
            for (var i = 0; i < stacks; i++) {
                var stack = ThingMaker.MakeThing(ThingDefOf.Steel);
                stack.stackCount = 75;
                GenSpawn.Spawn(stack, free[i], map);
                stack.SetForbidden(false, false);
            }
            return stacks * 75;
        }

        private static Building salvageWall;
        private static readonly List<Building> salvageCluster = new List<Building>();
        private static readonly List<IntVec3> salvagePatch = new List<IntVec3>();
        private static Plant salvagePlant;
        private static int salvageStock;
        private static ThingDef collapseRubble => DefDatabase<ThingDef>.GetNamedSilentFail("CollapsedRoofRubble");
        private static IntVec3 salvageCell;

        // A cluster of three foreign steel walls in a row under a thin roof
        // (#2302): the walls are the only roof holders in range, so removing
        // them first would collapse the roof; recovery takes the roof off,
        // then the walls. The patch is the roofed 5x3 rectangle.
        private static void SpawnWallCluster(Map map)
        {
            salvageCluster.Clear();
            salvagePatch.Clear();
            foreach (var origin in GenRadial.RadialCellsAround(salvageCell, 20, true)) {
                var patch = new List<IntVec3>();
                for (var dx = 0; dx < 5; dx++)
                    for (var dz = -1; dz <= 1; dz++) patch.Add(new IntVec3(origin.x + dx, 0, origin.z + dz));
                if (!patch.All(c => c.InBounds(map) && c.Standable(map) && c.GetEdifice(map) == null && !c.Roofed(map)
                        && !c.Fogged(map) && !map.areaManager.Home[c] && map.zoneManager.ZoneAt(c) == null)
                    || !preparedHauler.CanReach(origin, PathEndMode.Touch, Danger.None)) continue;
                var walls = new List<Building>();
                for (var dx = 1; dx <= 3; dx++) {
                    var wall = GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.Steel), new IntVec3(origin.x + dx, 0, origin.z), map) as Building;
                    if (wall == null) break;
                    wall.SetForbidden(false, false);
                    walls.Add(wall);
                }
                if (walls.Count != 3) { foreach (var w in walls) w.Destroy(DestroyMode.Vanish); continue; }
                foreach (var c in patch) map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                salvageCluster.AddRange(walls);
                salvagePatch.AddRange(patch);
                salvageWall = walls[0];
                salvageCell = walls[0].Position;
                return;
            }
        }

        private static int salvageHomeCount;
        private static int salvageStoredBefore;
        private static Pawn salvageThreat;
        [Tool("test/salvage_remote", Description = "UNSAFE FOR MODEL EXECUTION. Replace the prepared remote loot with a steel-rich ruin (a battery) and raise readiness, or audit ordinary deconstruction, delivered steel and unchanged Home. threat=spawn stages one hostile humanlike beside the ruin, holding position (a raid that fires after selection, #525); threat=clear removes it.")]
        public async Task<object> SalvageRemote(IRimBridgeContext ctx, CancellationToken cancellationToken, bool prepare = false, string threat = "", string scenario = "")
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                int Stored() => lootZone.Cells.SelectMany(c => c.GetThingList(map)).Where(t => t.def == ThingDefOf.Steel).Sum(t => t.stackCount);
                if (threat == "spawn") {
                    if (salvageWall == null || !Find.TickManager.Paused) return Refuse("Prepared salvage wall on a paused map required.");
                    if (salvageThreat != null && salvageThreat.Spawned && !salvageThreat.Dead) return Refuse("Fixture threat already on the map.");
                    var faction = Find.FactionManager.AllFactionsVisible.Where(f => f.HostileTo(Faction.OfPlayer) && !f.def.hidden && f.def.humanlikeFaction && !f.defeated)
                        .OrderBy(f => f.def.techLevel).FirstOrDefault();
                    if (faction == null) return Refuse("No hostile humanlike faction.");
                    var cell = GenRadial.RadialCellsAround(salvageCell, 6, false).FirstOrDefault(c => c.InBounds(map) && c.Standable(map) && !c.Fogged(map)
                        && c.DistanceTo(salvageCell) >= 3 && map.reachability.CanReach(c, salvageCell, PathEndMode.Touch, TraverseMode.PassDoors, Danger.Deadly));
                    if (!cell.IsValid) return Refuse("No standable cell beside the salvage wall.");
                    var kind = faction.RandomPawnKind();
                    var pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, faction, PawnGenerationContext.NonPlayer, -1, forceGenerateNewPawn: true, mustBeCapableOfViolence: true));
                    GenSpawn.Spawn(pawn, cell, map);
                    // The raider holds its ground beside the ruin: a threat on the
                    // route and at the target, not an assault on the colony.
                    LordMaker.MakeNewLord(faction, new LordJob_DefendPoint(cell), map, new[] { pawn });
                    salvageThreat = pawn;
                }
                if (threat == "clear") {
                    if (salvageThreat != null && !salvageThreat.Destroyed) salvageThreat.Destroy(DestroyMode.Vanish);
                    salvageThreat = null;
                }
                if (prepare) {
                    if (map != preparedMap || remoteStack?.Spawned != true || !Find.TickManager.Paused) return Refuse("Prepared remote loot required.");
                    salvageCell = remoteStack.Position;
                    salvageCluster.Clear();
                    salvagePatch.Clear();
                    remoteStack.Destroy(DestroyMode.Vanish);
                    // Every generated ruin competes for the one remote removal a
                    // census admits (nearer, lighter yields rank first), so the
                    // case measures one source: the map seed's visible ruins go.
                    // Ancient danger, caskets and anything fogged stay untouched.
                    foreach (var ruin in map.listerThings.AllThings.OfType<Building>().Where(b => b.Faction != Faction.OfPlayer && !b.def.mineable
                            && b.DeconstructibleBy(Faction.OfPlayer) && !b.Position.Fogged(map) && !(b is Building_AncientCryptosleepCasket)
                            && !NativeClearanceObservationTools.AncientDanger(map, b, Faction.OfPlayer)).ToList())
                        ruin.Destroy(DestroyMode.Vanish);
                    var readiness = RaiseReadiness(map);
                    if (readiness is string reason) return Refuse(reason);
                    // A random start may field a colonist with no work
                    // settings at all (#716), as RaiseReadiness already allows.
                    foreach (var p in map.mapPawns.FreeColonistsSpawned)
                        if (p.workSettings != null && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)) p.workSettings.SetPriority(WorkTypeDefOf.Construction, 1);
                    // The ruin must pass the same counterfactual roof-support
                    // check the census applies (#337): a wall the map's roofs
                    // lean on is a legitimate hold, not a salvage candidate.
                    salvageWall = null;
                    // A battery's 35 steel outranks the map seed's urns and doors
                    // under the Steel target; a steel wall's 2 never would.
                    var battery = DefDatabase<ThingDef>.GetNamed("Battery");
                    if (scenario == "cluster") SpawnWallCluster(map);
                    else foreach (var cell in GenRadial.RadialCellsAround(salvageCell, 20, true)) {
                        // The battery is 1x2 and the remote cell hugs the map edge:
                        // every footprint cell must qualify, or the spawn fails.
                        if (!GenAdj.OccupiedRect(cell, Rot4.North, battery.size).Cells.All(c => c.InBounds(map) && c.Standable(map)
                                && c.GetEdifice(map) == null && !c.Roofed(map) && !map.areaManager.Home[c] && map.zoneManager.ZoneAt(c) == null)
                            || !preparedHauler.CanReach(cell, PathEndMode.Touch, Danger.None)) continue;
                        var candidate = GenSpawn.Spawn(ThingMaker.MakeThing(battery), cell, map, Rot4.North) as Building;
                        if (candidate == null) continue;
                        if (RoofSupportSafety.Blocker(candidate, out _) == null) { salvageWall = candidate; salvageCell = cell; break; }
                        candidate.Destroy(DestroyMode.Vanish);
                    }
                    if (salvageWall == null) return Refuse("No roof-safe reachable cell for the salvage ruin near the remote cell.");
                    salvageWall.SetForbidden(false, false);
                    var baseCell = GenRadial.RadialCellsAround(lootZone.Cells[0], 8, true).First(c => c.InBounds(map) && c.Standable(map) && c.GetEdifice(map) == null && c.GetZone(map) == null);
                    var marker = ThingMaker.MakeThing(ThingDefOf.Table2x2c, ThingDefOf.WoodLog);
                    marker.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(marker, baseCell, map);
                    // No operator floor any more (#875): with the colony's steel
                    // gone, the default Steel floor (200) is the salvage demand.
                    foreach (var steel in map.listerThings.ThingsOfDef(ThingDefOf.Steel).ToList())
                        steel.Destroy(DestroyMode.Vanish);
                    // scenario=covered (#2302): Steel is above its floor, so the
                    // ruin is recovered although nothing is short.
                    salvageStock = 0;
                    if (scenario == "covered") {
                        salvageStock = StockCovered(map);
                        if (salvageStock < 0) return Refuse("Too few free stockpile cells to cover Steel demand.");
                    }
                    // scenario=probe (#2302): a wild bush beside the ruin, for
                    // the foreign cut by the Go-built id (#2293).
                    salvagePlant = null;
                    if (scenario == "probe") {
                        var bushDef = DefDatabase<ThingDef>.GetNamed("Plant_Bush");
                        var bushCell = GenRadial.RadialCellsAround(salvageCell, 6, false).FirstOrDefault(c => c.InBounds(map) && c.Standable(map)
                            && c.GetEdifice(map) == null && c.GetThingList(map).All(t => t is Plant || t.def.category != ThingCategory.Building)
                            && c.GetPlant(map) == null && !map.areaManager.Home[c] && map.zoneManager.ZoneAt(c) == null);
                        if (!bushCell.IsValid) return Refuse("No free cell for the probe bush.");
                        salvagePlant = GenSpawn.Spawn(ThingMaker.MakeThing(bushDef), bushCell, map) as Plant;
                        if (salvagePlant == null) return Refuse("Probe bush did not spawn.");
                    }
                    salvageHomeCount = map.areaManager.Home.ActiveCells.Count();
                    salvageStoredBefore = Stored();
                }
                if (salvageWall == null) return Refuse("No prepared salvage wall.");
                var designations = salvageWall.Spawned ? map.designationManager.AllDesignationsOn(salvageWall).Count(d => d.def == DesignationDefOf.Deconstruct) : 0;
                var hostiles = map.mapPawns.AllPawnsSpawned.Count(p => !p.Dead && !p.Downed && p.RaceProps.Humanlike && p.HostileTo(Faction.OfPlayer));
                return new { success = true, target = salvageWall.GetUniqueLoadID(), present = salvageWall.Spawned, delivered = Stored() - salvageStoredBefore,
                    designations, hostiles, threatPresent = salvageThreat != null && salvageThreat.Spawned && !salvageThreat.Dead,
                    threatId = salvageThreat?.GetUniqueLoadID(), threatCell = salvageThreat?.Spawned == true ? new { x = salvageThreat.Position.x, z = salvageThreat.Position.z } : null,
                    homeUnchanged = salvageHomeCount == map.areaManager.Home.ActiveCells.Count() && !map.areaManager.Home[salvageCell],
                    stock = salvageStock,
                    cluster = salvageCluster.Select(w => new { id = w.GetUniqueLoadID(), present = w.Spawned }).ToArray(),
                    // roofed patch cells left and collapse rubble in the patch (#2302): a removal that
                    // beat the roof off leaves rubble; a roof-first batch leaves none.
                    roofed = salvagePatch.Count(c => c.Roofed(map)),
                    rubble = collapseRubble == null ? 0 : salvagePatch.Sum(c => c.GetThingList(map).Count(t => t.def == collapseRubble)),
                    claimed = salvageWall.Spawned && salvageWall.Faction == Faction.OfPlayer,
                    plant = salvagePlant?.GetUniqueLoadID(), plantCell = salvagePlant?.Spawned == true ? new { x = salvagePlant.Position.x, z = salvagePlant.Position.z } : null,
                    plantCut = salvagePlant != null && salvagePlant.Spawned && map.designationManager.AllDesignationsOn(salvagePlant).Any(d => d.def == DesignationDefOf.CutPlant) };
            }, cancellationToken);

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
