#nullable enable

using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Per-map last-changed tick grid behind GetCellsRequest.changed_since_tick
    // (issue #357): one int per cell, bumped in O(1) by the map events and
    // patches below whenever a fact a CellState row derives can change, so a
    // cells read can omit the cells unchanged since a tick the caller names.
    // The native keeps no per-client state: a tracker is created on a map's
    // first cells read with every cell stamped one tick before it, so an ask
    // for a tick the tracker never saw returns the whole selection.
    //
    // Hooked (map.events, RimWorld 1.6): terrain, roof, fog, path cost and
    // thing spawn/despawn for anything but pawns, motes, filth and
    // projectiles. Patched (no event): the zone grid (ZoneManager.AddZoneGridCell
    // / ClearZoneGridCell, which Zone.AddCell, RemoveCell, Delete and the
    // load-time rebuild all go through) and RoofGrid.RemoveRoofUnsafe.
    // Indoors has no per-cell event, so a read compares it against the
    // shadow it kept from its last visit and stamps the cell when it
    // differs. room_id is deliberately not a change: rooms are renumbered
    // whenever regions rebuild (one wall spawn gives the whole outdoors a
    // new id), so a cell omitted as unchanged may carry a stale room_id
    // and a reader that keys on it must read in full. The store's periodic
    // full resync (Go) is the backstop against anything this list misses.
    internal sealed class CellTracking
    {
        private static readonly ConditionalWeakTable<Map, CellTracking> Maps = new ConditionalWeakTable<Map, CellTracking>();
        private static bool installed;

        private readonly Map map;
        private readonly int[] lastChanged;
        private readonly bool[] indoors, roomSeen;
        private readonly bool[] polluted, growthSeen;
        private readonly float[] glow;

        // Since is the tick every cell was stamped with at creation.
        internal readonly int Since;

        internal static CellTracking For(Map map)
        {
            Install();
            return Maps.GetValue(map, m => new CellTracking(m));
        }

        private CellTracking(Map map)
        {
            this.map = map;
            int count = map.cellIndices.NumGridCells;
            Since = Find.TickManager.TicksGame - 1;
            ZoneTracking.Initialize(map, Since);
            lastChanged = new int[count];
            for (int i = 0; i < count; i++) lastChanged[i] = Since;
            indoors = new bool[count];
            roomSeen = new bool[count];
            polluted = new bool[count];
            growthSeen = new bool[count];
            glow = new float[count];
            var events = map.events;
            events.TerrainChanged += Bump;
            events.RoofChanged += Bump;
            events.PathCostRecalculate += Bump;
            events.CellFogChanged += (cell, _) => Bump(cell);
            events.MapFogged += BumpAll;
            events.ThingSpawned += Thing;
            events.ThingDespawned += Thing;
        }

        // Unchanged reports a cell whose row is the same as at tick since
        // (room_id aside): stamped before it and, for indoors, identical to
        // the shadow of the last visit. A differing indoors stamps the cell.
        internal bool Unchanged(IntVec3 cell, long since)
        {
            int index = map.cellIndices.CellToIndex(cell);
            if (lastChanged[index] >= since) return false;
            var room = cell.GetRoom(map);
            if (!roomSeen[index] || indoors[index] != Indoors(room)) { NoteRoom(index, room); lastChanged[index] = Find.TickManager.TicksGame; return false; }
            return true;
        }

        // NoteRoom records the indoors a read emitted for the cell.
        internal void NoteRoom(IntVec3 cell, Room? room) => NoteRoom(map.cellIndices.CellToIndex(cell), room);

        // Pollution and glow have no cell event in this tracker. Compare
        // their native values on a growth delta read before omitting a row.
        internal bool GrowthUnchanged(IntVec3 cell)
        {
            int index = map.cellIndices.CellToIndex(cell);
            bool dirty = !growthSeen[index] || polluted[index] != (ModsConfig.BiotechActive && map.pollutionGrid.IsPolluted(cell))
                || glow[index] != map.glowGrid.GroundGlowAt(cell);
            if (dirty) lastChanged[index] = Find.TickManager.TicksGame;
            return !dirty;
        }

        internal void NoteGrowth(IntVec3 cell, bool pollution, float groundGlow)
        {
            int index = map.cellIndices.CellToIndex(cell);
            growthSeen[index] = true;
            polluted[index] = pollution;
            glow[index] = groundGlow;
        }

        private void NoteRoom(int index, Room? room)
        {
            roomSeen[index] = true;
            indoors[index] = Indoors(room);
        }

        // Indoors is CellState.indoors as the cells read computes it.
        internal static bool Indoors(Room? room) => room != null && room.ProperRoom && !room.PsychologicallyOutdoors;

        private void Bump(IntVec3 cell)
        {
            if (cell.InBounds(map)) lastChanged[map.cellIndices.CellToIndex(cell)] = Find.TickManager.TicksGame;
        }

        private void Bump(int index)
        {
            if (index >= 0 && index < lastChanged.Length) lastChanged[index] = Find.TickManager.TicksGame;
        }

        private void BumpAll()
        {
            int now = Find.TickManager.TicksGame;
            for (int i = 0; i < lastChanged.Length; i++) lastChanged[i] = now;
        }

        private void Thing(Thing thing)
        {
            if (!Tracked(thing)) return;
            foreach (var cell in thing.OccupiedRect()) Bump(cell);
        }

        // Tracked lists the things whose presence a row reflects: an edifice,
        // blueprint, frame or plant (occupied, doorway, walkable, storage),
        // an item (storage_empty) and anything that costs or blocks a path.
        private static bool Tracked(Thing thing)
        {
            if (thing is Pawn || thing is Mote || thing is Filth || thing is Projectile) return false;
            return thing is Building || thing is Blueprint || thing is Frame || thing is Plant || thing.def.category == ThingCategory.Item
                || thing.def.passability != Traversability.Standable || thing.def.pathCost > 0;
        }

        private static void Install()
        {
            if (installed) return;
            installed = true;
            var harmony = new Harmony("rimgovernor.cell-tracking");
            harmony.Patch(AccessTools.Method(typeof(ZoneManager), "AddZoneGridCell", new[] { typeof(Zone), typeof(IntVec3) }),
                postfix: new HarmonyMethod(typeof(CellTracking), nameof(ZoneGridCell)));
            harmony.Patch(AccessTools.Method(typeof(ZoneManager), "ClearZoneGridCell", new[] { typeof(IntVec3) }),
                postfix: new HarmonyMethod(typeof(CellTracking), nameof(ZoneGridCell)));
            harmony.Patch(AccessTools.Method(typeof(RoofGrid), "RemoveRoofUnsafe", new[] { typeof(int) }),
                postfix: new HarmonyMethod(typeof(CellTracking), nameof(RoofRemoved)));
        }

        private static readonly AccessTools.FieldRef<RoofGrid, Map> roofGridMap = AccessTools.FieldRefAccess<RoofGrid, Map>("map");

        // Patches bump only a map already tracked; they never create a tracker.
        private static void ZoneGridCell(ZoneManager __instance, IntVec3 c)
        {
            if (__instance.map != null && Maps.TryGetValue(__instance.map, out var tracking)) tracking.Bump(c);
        }

        private static void RoofRemoved(RoofGrid __instance, int index)
        {
            var map = roofGridMap(__instance);
            if (map != null && Maps.TryGetValue(map, out var tracking)) tracking.Bump(index);
        }
    }
}
