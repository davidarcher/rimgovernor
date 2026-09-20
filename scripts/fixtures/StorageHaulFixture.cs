using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
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

        [Tool("test/storage_haul_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: register one legal Steel stockpile zone, spawn ordinary Steel stacks outside it (only the first unforbidden), and set exactly one existing colonist's Hauling work priority (all others disabled) so RoutineHaulPlanner's MaintainStorage deficit and its single eligible hauler are deterministic. No quest/travel simulation, no new resources beyond the spawned Steel.")]
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


        private static Thing lootStack;
        private static Thing lootTrap;
        private static Zone_Stockpile lootZone;

        [Tool("test/loot_drop", Description = "UNSAFE FOR MODEL EXECUTION. Spawn an allowed Steel stack more than 40 cells from the colony on a spike trap; extend the prepared stockpile. Tests autonomous safety forbidding of mid-run loot.")]
        public async Task<object> LootDrop(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || map != preparedMap || preparedHauler == null || !Find.TickManager.Paused)
                    return Refuse("Prepared paused storage fixture required.");
                var center = new IntVec3((int)preparedPeople.Average(p => p.Position.x), 0, (int)preparedPeople.Average(p => p.Position.z));
                var cells = map.AllCells.Where(c => !c.Fogged(map) && c.Standable(map) && c.GetEdifice(map) == null
                    && c.GetThingList(map).All(t => t is Plant) && map.zoneManager.ZoneAt(c) == null
                    && !map.areaManager.Home[c] && c.DistanceTo(center) > 45
                    && preparedHauler.CanReach(c, PathEndMode.Touch, Danger.None)).OrderBy(c => c.DistanceToSquared(center)).ToList();
                if (cells.Count == 0) return Refuse("No safe distant drop cell.");
                lootZone = map.zoneManager.AllZones.OfType<Zone_Stockpile>().First(z => z.GetStoreSettings().filter.Allows(ThingDefOf.Steel));
                foreach (var c in GenRadial.RadialCellsAround(lootZone.Cells[0], 6, true)
                    .Where(c => c.InBounds(map) && c.Standable(map) && c.GetEdifice(map) == null && map.zoneManager.ZoneAt(c) == null).Take(8))
                    lootZone.AddCell(c);
                lootTrap = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("TrapSpike"), ThingDefOf.Steel);
                lootTrap.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(lootTrap, cells[0], map);
                lootStack = ThingMaker.MakeThing(ThingDefOf.Steel);
                lootStack.stackCount = 25;
                GenSpawn.Spawn(lootStack, cells[0], map);
                lootStack.SetForbidden(false, false);
                return new { success = true, id = lootStack.GetUniqueLoadID(), distance = cells[0].DistanceTo(center), forbidden = false };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/loot_safety_control", Description = "UNSAFE FOR MODEL EXECUTION. Read the staged loot's forbidden/storage state, or remove only its staged trap and release the prepared haulers for the safe-haul phase.")]
        public async Task<object> LootSafetyControl(IRimBridgeContext ctx, CancellationToken cancellationToken, bool removeDanger = false)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (Current.Game != preparedGame || Find.CurrentMap != preparedMap || lootStack == null)
                    return Refuse("Prepared loot fixture required.");
                if (removeDanger) {
                    if (lootTrap != null && !lootTrap.Destroyed) lootTrap.Destroy(DestroyMode.Vanish);
                    // The drop sits outside the established extent, so the
                    // safe-haul phase also needs far resource reach (#522).
                    var readiness = RaiseReadiness(preparedMap);
                    if (readiness is string refusal) return Refuse(refusal);
                }
                var stored = lootZone.Cells.SelectMany(c => c.GetThingList(preparedMap))
                    .Where(t => t.def == ThingDefOf.Steel).Sum(t => t.stackCount);
                return new { success = true, forbidden = !lootStack.Destroyed && lootStack.IsForbidden(Faction.OfPlayer),
                    spawned = lootStack.Spawned, storedSteel = stored, hazard = lootTrap != null && !lootTrap.Destroyed,
                    inStockpile = lootStack.Spawned && lootZone.Cells.Contains(lootStack.Position) };
            }, cancellationToken).ConfigureAwait(false);
        }
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
        public async Task<object> LootRemoteDrop(IRimBridgeContext ctx, CancellationToken cancellationToken, int count = 75)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
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
                return new { success = true, id = remoteStack.GetUniqueLoadID(), x = cells[0].x, z = cells[0].z,
                    distance = cells[0].DistanceTo(center), edge = Edge(cells[0]), forbidden = true, count };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/loot_remote_control", Description = "UNSAFE FOR MODEL EXECUTION. Read the remote loot stack's forbidden/storage/Home state, or raise the colony's resource reach readiness (six armed colonists, every colonist hauling).")]
        public async Task<object> LootRemoteControl(IRimBridgeContext ctx, CancellationToken cancellationToken, bool raiseReadiness = false)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (Current.Game != preparedGame || Find.CurrentMap != preparedMap || remoteStack == null || lootZone == null)
                    return Refuse("Prepared remote loot fixture required.");
                object readiness = null;
                if (raiseReadiness) {
                    readiness = RaiseReadiness(preparedMap);
                    if (readiness is string refusal) return Refuse(refusal);
                }
                var stored = lootZone.Cells.SelectMany(c => c.GetThingList(preparedMap))
                    .Where(t => t.def == ThingDefOf.Steel).Sum(t => t.stackCount);
                return new { success = true, forbidden = !remoteStack.Destroyed && remoteStack.IsForbidden(Faction.OfPlayer),
                    spawned = remoteStack.Spawned, storedSteel = stored, readiness,
                    inStockpile = remoteStack.Spawned && lootZone.Cells.Contains(remoteStack.Position),
                    inHome = remoteStack.Spawned && preparedMap.areaManager.Home[remoteStack.Position],
                    armed = preparedMap.mapPawns.FreeColonistsSpawned.Count(p => p.equipment?.Primary != null),
                    raidPoints = StorytellerUtility.DefaultThreatPointsNow(preparedMap) };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static Building salvageWall;
        private static IntVec3 salvageCell;
        private static int salvageHomeCount;
        private static int salvageStoredBefore;
        private static Pawn salvageThreat;
        [Tool("test/salvage_remote", Description = "UNSAFE FOR MODEL EXECUTION. Replace the prepared remote loot with a steel-rich ruin (a battery) and raise readiness, or audit ordinary deconstruction, delivered steel and unchanged Home. threat=spawn stages one hostile humanlike beside the ruin, holding position (a raid that fires after selection, #525); threat=clear removes it.")]
        public async Task<object> SalvageRemote(IRimBridgeContext ctx, CancellationToken cancellationToken, bool prepare = false, string threat = "")
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
                    foreach (var p in map.mapPawns.FreeColonistsSpawned)
                        if (!p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)) p.workSettings.SetPriority(WorkTypeDefOf.Construction, 1);
                    // The ruin must pass the same counterfactual roof-support
                    // check the census applies (#337): a wall the map's roofs
                    // lean on is a legitimate hold, not a salvage candidate.
                    salvageWall = null;
                    foreach (var cell in GenRadial.RadialCellsAround(salvageCell, 20, true)) {
                        if (!cell.InBounds(map) || !cell.Standable(map) || cell.GetEdifice(map) != null || cell.Roofed(map)
                            || map.areaManager.Home[cell] || map.zoneManager.ZoneAt(cell) != null || !preparedHauler.CanReach(cell, PathEndMode.Touch, Danger.None)) continue;
                        // A battery's 35 steel outranks the map seed's urns and doors
                        // under the Steel target; a steel wall's 2 never would.
                        var candidate = (Building)GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Battery")), cell, map);
                        if (RoofSupportSafety.Blocker(candidate, out _) == null) { salvageWall = candidate; salvageCell = cell; break; }
                        candidate.Destroy(DestroyMode.Vanish);
                    }
                    if (salvageWall == null) return Refuse("No roof-safe reachable cell for the salvage ruin near the remote cell.");
                    salvageWall.SetForbidden(false, false);
                    var baseCell = GenRadial.RadialCellsAround(lootZone.Cells[0], 8, true).First(c => c.InBounds(map) && c.Standable(map) && c.GetEdifice(map) == null && c.GetZone(map) == null);
                    var marker = ThingMaker.MakeThing(ThingDefOf.Table2x2c, ThingDefOf.WoodLog);
                    marker.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(marker, baseCell, map);
                    salvageHomeCount = map.areaManager.Home.ActiveCells.Count();
                    salvageStoredBefore = Stored();
                }
                if (salvageWall == null) return Refuse("No prepared salvage wall.");
                var designations = salvageWall.Spawned ? map.designationManager.AllDesignationsOn(salvageWall).Count(d => d.def == DesignationDefOf.Deconstruct) : 0;
                var hostiles = map.mapPawns.AllPawnsSpawned.Count(p => !p.Dead && !p.Downed && p.RaceProps.Humanlike && p.HostileTo(Faction.OfPlayer));
                return new { success = true, target = salvageWall.GetUniqueLoadID(), present = salvageWall.Spawned, delivered = Stored() - salvageStoredBefore,
                    designations, hostiles, threatPresent = salvageThreat != null && salvageThreat.Spawned && !salvageThreat.Dead,
                    threatId = salvageThreat?.GetUniqueLoadID(), threatCell = salvageThreat?.Spawned == true ? new { x = salvageThreat.Position.x, z = salvageThreat.Position.z } : null,
                    homeUnchanged = salvageHomeCount == map.areaManager.Home.ActiveCells.Count() && !map.areaManager.Home[salvageCell] };
            }, cancellationToken);

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
