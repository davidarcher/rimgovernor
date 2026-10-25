using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Native
{
    /// <summary>
    /// Reads a rect of a map (the whole map for a snapshot frame) into a RimGovernor.Brain.GameFacts.Mirror.CellGrid on the game thread: one
    /// typed array per cell fact, indexed [z * width + x] within the rect. The grid is a copy that refers to no live
    /// game state: defs are held by name, zones by load id, rooms by key and things as thing records (ThingReader).
    /// A fogged cell is not held, and every other array keeps its default there.
    /// </summary>
    internal static class CellGridReader
    {
        /// Every cell of map, against the map's last whole read (ThingReader).
        internal static RimGovernor.Brain.GameFacts.Mirror.CellGrid Read(Map map) => Read(map, 0, 0, map.Size.x, map.Size.z, ThingReader.Whole(map));

        /// The w x h rect at (x0, z0), on the map.
        internal static RimGovernor.Brain.GameFacts.Mirror.CellGrid Read(Map map, int x0, int z0, int w, int h) => Read(map, x0, z0, w, h, new ThingReader(map));

        private static RimGovernor.Brain.GameFacts.Mirror.CellGrid Read(Map map, int x0, int z0, int w, int h, ThingReader things)
        {
            int n = w * h;
            bool[] held = new bool[n], walkable = new bool[n], indoors = new bool[n], doorway = new bool[n], polluted = new bool[n],
                inHome = new bool[n], topLayer = new bool[n], foundation = new bool[n];
            int[] stacks = new int[n], capacity = new int[n];
            float[] fertility = new float[n], glow = new float[n], snow = new float[n];
            string[] terrain = new string[n], baseTerrain = new string[n];
            string?[] roof = new string?[n], zoneId = new string?[n];
            var room = new int?[n];
            var cellThings = new P.Thing[]?[n];
            var biotech = ModsConfig.BiotechActive;
            var terrainGrid = map.terrainGrid;
            var home = map.areaManager.Home;
            var names = new Dictionary<Def, string>();
            string Name(Def def)
            {
                if (!names.TryGetValue(def, out var name)) names[def] = name = Identifier(def.defName);
                return name;
            }
            var zones = new Dictionary<Zone, string>();
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                {
                    var cell = new IntVec3(x0 + x, 0, z0 + z);
                    var j = z * w + x;
                    if (cell.Fogged(map)) continue;
                    held[j] = true;
                    walkable[j] = cell.Walkable(map);
                    CellThingFacts(map, cell, out doorway[j], out stacks[j], out capacity[j]);
                    terrain[j] = Name(terrainGrid.TerrainAt(cell));
                    baseTerrain[j] = Name(terrainGrid.BaseTerrainAt(cell));
                    if (map.roofGrid.RoofAt(cell) is { } roofDef) roof[j] = Name(roofDef);
                    if (map.zoneManager.ZoneAt(cell) is { } zone)
                    {
                        if (!zones.TryGetValue(zone, out var id)) zones[zone] = id = zone.GetUniqueLoadID();
                        zoneId[j] = id;
                    }
                    polluted[j] = biotech && map.pollutionGrid.IsPolluted(cell);
                    glow[j] = Finite(map.glowGrid.GroundGlowAt(cell, false, true));
                    inHome[j] = home[cell];
                    snow[j] = Finite(map.snowGrid.GetDepth(cell));
                    topLayer[j] = terrainGrid.CanRemoveTopLayerAt(cell);
                    foundation[j] = things.FoundationRemoval(cell);
                    cellThings[j] = things.At(cell, j);
                    fertility[j] = Finite(map.fertilityGrid.FertilityAt(cell));
                }
            StampRooms(map, x0, z0, w, h, held, room, indoors);
            things.Finish(cellThings);
            return new RimGovernor.Brain.GameFacts.Mirror.CellGrid
            {
                X = x0, Z = z0, Width = w, Height = h, Held = held, Walkable = walkable, Indoors = indoors, Doorway = doorway,
                Polluted = polluted, InHome = inHome, TopLayerRemovable = topLayer, FoundationRemoval = foundation,
                StorageStacks = stacks, StorageCapacity = capacity, Fertility = fertility, Glow = glow, SnowDepth = snow,
                Terrain = terrain, BaseTerrain = baseTerrain, Roof = roof, ZoneId = zoneId, Room = room, Things = cellThings,
            };
        }

        /// Stamps each held cell of the rect with its room's key and indoors-ness, walking the room graph once instead
        /// of a GetRoom per cell. A room's key is the map index (z * map width + x) of its first held cell on the map,
        /// not Room.ID: RimWorld regenerates rooms near any edifice change with fresh ids, and the first cell stays put
        /// unless it changes (the room census's GridRoom). Pending region and room changes are rebuilt first, as
        /// GetRoom would.
        internal static void StampRooms(Map map, int x0, int z0, int w, int h, bool[] held, int?[] rooms, bool[] indoors)
        {
            var updater = map.regionAndRoomUpdater;
            if (updater.Enabled) updater.TryRebuildDirtyRegionsAndRooms();
            var mapWidth = map.Size.x;
            var cells = new List<int>();
            foreach (var room in map.regionGrid.AllRooms)
            {
                if (room.Dereferenced) continue;
                cells.Clear();
                var key = int.MaxValue;
                foreach (var cell in room.Cells)
                {
                    var index = cell.z * mapWidth + cell.x;
                    cells.Add(index);
                    if (index < key && !cell.Fogged(map)) key = index;
                }
                if (key == int.MaxValue) continue;
                var inside = CellTracking.Indoors(room);
                foreach (var index in cells)
                {
                    int x = index % mapWidth - x0, z = index / mapWidth - z0;
                    if (x < 0 || z < 0 || x >= w || z >= h) continue;
                    var j = z * w + x;
                    if (!held[j]) continue;
                    rooms[j] = key;
                    indoors[j] = inside;
                }
            }
        }

        // NativeObservationTools.CellDoorway and the cell's storage stacks in
        // one pass over its thing list, without LINQ allocations. stacks counts
        // the item stacks on the cell; capacity is the stacks it can hold: a
        // storage building's maxItemsInCell (3 on a shelf), 0 under any other
        // edifice, 1 on open ground.
        internal static void CellThingFacts(Map map, IntVec3 cell, out bool doorway, out int stacks, out int capacity)
        {
            doorway = cell.GetDoor(map) != null; stacks = 0;
            var list = cell.GetThingList(map);
            for (int i = 0; i < list.Count; i++)
            {
                var t = list[i];
                if (t is Blueprint || t is Frame)
                {
                    if (!doorway && t.def.entityDefToBuild is ThingDef built && typeof(Building_Door).IsAssignableFrom(built.thingClass)) doorway = true;
                }
                else if (t.def.category == ThingCategory.Item) stacks++;
            }
            var edifice = cell.GetEdifice(map);
            capacity = edifice == null ? 1 : edifice is Building_Storage ? edifice.def.building.maxItemsInCell : 0;
        }

        private static string Identifier(string? value) => GameThread.IsIdentifier(value) ? value : throw new InvalidOperationException("Native identifier unavailable.");
        private static float Finite(float value) => float.IsNaN(value) || float.IsInfinity(value) ? throw new InvalidOperationException("Nonfinite native fact.") : value;
    }
}
