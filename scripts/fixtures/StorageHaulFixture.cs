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
    public sealed class StorageHaulFixture
    {
        private static Game preparedGame;
        private static Map preparedMap;

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
                preparedGame = Current.Game; preparedMap = map;
                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new {
                    success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame,
                    haulerId = hauler.GetUniqueLoadID(),
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

        [Tool("test/storage_haul_allow", Description = "UNSAFE FOR MODEL EXECUTION. Private prepared-fixture control: unforbid one prepared Steel stack (by itemId) so it becomes a MaintainStorage deficit the planner must renew for. Mutates only that stack's forbidden flag.")]
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
                thing.SetForbidden(false, false);
                return new { success = true, x = thing.Position.x, z = thing.Position.z, count = thing.stackCount,
                    forbidden = thing.IsForbidden(Faction.OfPlayerSilentFail) };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
