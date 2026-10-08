#nullable enable
using System;
using System.Runtime.CompilerServices;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// The grid read's cached per-cell facts (#1575): walkability, doorway,
    /// empty storage, terrain light, terrain and foundation names and the
    /// cell's room, held per map and recomputed only for a cell the game
    /// reported changed. A cell is dirtied by a non-pawn thing spawning or
    /// despawning on it, a terrain change or a path-cost recalculation at it;
    /// any region and room rebuild or roof change drops every cell, since a
    /// room's cells and its indoors-ness move together. The columns that vary
    /// per tick (roof, zone, pollution, glow, snow, fertility, home, things)
    /// are never cached. Game thread only.
    /// </summary>
    internal sealed class CellGridCache
    {
        private static readonly ConditionalWeakTable<Map, CellGridCache> Caches = new ConditionalWeakTable<Map, CellGridCache>();

        private readonly Map map;
        internal readonly bool[] Valid;
        internal readonly byte[] Walkable, Doorway, StorageEmpty, Light;
        internal readonly string?[] Terrain, Foundation;
        internal readonly Room?[] Room;
        private int epoch, seen;

        // The cache for map, subscribed to its events on first use.
        internal static CellGridCache For(Map map)
        {
            if (!Caches.TryGetValue(map, out var cache))
            {
                cache = new CellGridCache(map);
                Caches.Add(map, cache);
            }
            return cache;
        }

        private CellGridCache(Map map)
        {
            this.map = map;
            var n = map.Size.x * map.Size.z;
            Valid = new bool[n]; Walkable = new byte[n]; Doorway = new byte[n]; StorageEmpty = new byte[n]; Light = new byte[n];
            Terrain = new string?[n]; Foundation = new string?[n]; Room = new Room?[n];
            var events = map.events;
            events.ThingSpawned += OnThing;
            events.ThingDespawned += OnThing;
            events.TerrainChanged += Dirty;
            events.PathCostRecalculate += Dirty;
            events.RoofChanged += _ => epoch++;
            events.RegionsRoomsChanged += () => epoch++;
        }

        // Rebuilds pending regions and rooms (what GetRoom would do cell by
        // cell), then drops every cell if rooms or roofs changed since the
        // last read.
        internal void Sync()
        {
            var updater = map.regionAndRoomUpdater;
            if (updater.Enabled) updater.TryRebuildDirtyRegionsAndRooms();
            if (seen != epoch) { Array.Clear(Valid, 0, Valid.Length); seen = epoch; }
        }

        private void Dirty(IntVec3 cell)
        {
            if (cell.InBounds(map)) Valid[map.cellIndices.CellToIndex(cell)] = false;
        }

        // Pawns, motes and projectiles feed none of the cached columns.
        private void OnThing(Thing thing)
        {
            var category = thing.def.category;
            if (category == ThingCategory.Pawn || category == ThingCategory.Mote || category == ThingCategory.Projectile) return;
            if (thing.def.size.x == 1 && thing.def.size.z == 1) { Dirty(thing.Position); return; }
            foreach (var cell in thing.OccupiedRect()) Dirty(cell);
        }
    }
}
