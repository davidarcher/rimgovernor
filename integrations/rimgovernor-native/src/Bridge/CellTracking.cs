#nullable enable

using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Per-map change hooks behind the planning-window view's change ledger
    // (#652): the map events and patches below number a cell's tile in
    // Ledger, in O(1), whenever a fact a planning row derives can change.
    // A tracker is created on a map's first view capture or zone read
    // (ZoneTracking starts at Since).
    //
    // Hooked (map.events, RimWorld 1.6): terrain, roof, fog, path cost and
    // thing spawn/despawn for anything but pawns, motes, filth and
    // projectiles. Patched (no event): the zone grid (ZoneManager.AddZoneGridCell
    // / ClearZoneGridCell, which Zone.AddCell, RemoveCell, Delete and the
    // load-time rebuild all go through) and RoofGrid.RemoveRoofUnsafe. The
    // ledger additionally counts room rebuilds (RegionsRoomsChanged) so the
    // view never reuses a stale room_id or indoors.
    internal sealed class CellTracking
    {
        private static readonly ConditionalWeakTable<Map, CellTracking> Maps = new ConditionalWeakTable<Map, CellTracking>();
        private static bool installed;

        private readonly Map map;
        // The planning-window view's change ledger (#652): every bump below
        // also numbers the cell's tile there, in O(1).
        internal readonly PlanningViewLedger Ledger;

        // Since is the tick before the tracker's creation.
        internal readonly int Since;

        internal static CellTracking For(Map map)
        {
            Install();
            return Maps.GetValue(map, m => new CellTracking(m));
        }

        private CellTracking(Map map)
        {
            this.map = map;
            Since = Find.TickManager.TicksGame - 1;
            ZoneTracking.Initialize(map, Since);
            Ledger = new PlanningViewLedger(map.Size.x, map.Size.z);
            var events = map.events;
            events.TerrainChanged += Bump;
            events.RoofChanged += Bump;
            events.PathCostRecalculate += Bump;
            events.CellFogChanged += (cell, _) => Bump(cell);
            events.MapFogged += BumpAll;
            events.ThingSpawned += Thing;
            events.ThingDespawned += Thing;
            events.RegionsRoomsChanged += Ledger.MarkTopology;
        }

        // Indoors is CellState.indoors as the cells read computes it.
        internal static bool Indoors(Room? room) => room != null && room.ProperRoom && !room.PsychologicallyOutdoors;

        private void Bump(IntVec3 cell)
        {
            if (!cell.InBounds(map)) return;
            Ledger.Mark(cell.x, cell.z);
        }

        private void Bump(int index)
        {
            if (index < 0 || index >= map.cellIndices.NumGridCells) return;
            var cell = map.cellIndices.IndexToCell(index);
            Ledger.Mark(cell.x, cell.z);
        }

        private void BumpAll() => Ledger.MarkBroad();

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
