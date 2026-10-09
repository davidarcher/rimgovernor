#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Google.Protobuf;
using RimWorld;
using Verse;
using Mirror = RimGovernor.Protocol.Mirror;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// The whole-map cell grid every snapshot frame carries: one
    /// array per policy.SiteCell field, with the sentinels and string table
    /// the CellGrid comment (mirror.proto) defines. Read reads the map on
    /// the game thread; Attach encodes it on the encoder worker, a keyframe
    /// (every array) on a stream keyframe, a map change, a failed read or
    /// every KeyframeSeconds, else a delta carrying only the arrays that
    /// differ from that keyframe, each dense or sparse, whichever is
    /// smaller. Deltas are cumulative against the keyframe, so a reader
    /// that skips frames needs only the keyframe. A delta that would carry
    /// every array is sent as a new keyframe instead.
    ///
    /// Attach runs one frame at a time (SnapshotStream's pending gate), so
    /// its state needs no lock.
    /// </summary>
    internal static class CellGridEncoder
    {
        internal const long KeyframeSeconds = 30;

        internal enum Kind : byte { Code, Number, Index }

        // The CellGrid arrays in field order (cell first); Read fills its
        // columns by these positions.
        internal const int Cell = 0, Walkable = 1, Zone = 2, Roofed = 3, Indoors = 4, SupportsLight = 5,
            StorageEmpty = 6, Doorway = 7, Fertility = 8, Polluted = 9, Glow = 10, Roof = 11, ZoneId = 12,
            Room = 13, Terrain = 14, InHome = 15,
            FoundationAffordances = 16, SnowDepth = 17, TopLayerRemovable = 18;

        private static readonly (string Name, Kind Kind, Action<Mirror.CellGrid, Mirror.FieldArray> Set)[] Fields =
        {
            ("cell", Kind.Code, (g, a) => g.Cell = a),
            ("walkable", Kind.Code, (g, a) => g.Walkable = a),
            ("zone", Kind.Code, (g, a) => g.Zone = a),
            ("roofed", Kind.Code, (g, a) => g.Roofed = a),
            ("indoors", Kind.Code, (g, a) => g.Indoors = a),
            ("supports_light", Kind.Code, (g, a) => g.SupportsLight = a),
            ("storage_empty", Kind.Code, (g, a) => g.StorageEmpty = a),
            ("doorway", Kind.Code, (g, a) => g.Doorway = a),
            ("fertility", Kind.Number, (g, a) => g.Fertility = a),
            ("polluted", Kind.Code, (g, a) => g.Polluted = a),
            ("glow", Kind.Number, (g, a) => g.Glow = a),
            ("roof", Kind.Index, (g, a) => g.Roof = a),
            ("zone_id", Kind.Index, (g, a) => g.ZoneId = a),
            ("room", Kind.Index, (g, a) => g.Room = a),
            ("terrain", Kind.Index, (g, a) => g.Terrain = a),
            ("in_home", Kind.Code, (g, a) => g.InHome = a),
            ("foundation_affordances", Kind.Index, (g, a) => g.FoundationAffordances = a),
            ("snow_depth", Kind.Number, (g, a) => g.SnowDepth = a),
            ("top_layer_removable", Kind.Code, (g, a) => g.TopLayerRemovable = a),
        };

        internal static string FieldName(int field) => Fields[field].Name;
        internal static int FieldCount => Fields.Length;

        /// One field's values over the map: codes, numbers (NaN unknown) or
        /// strings (null unknown).
        internal sealed class Column
        {
            internal byte[]? Codes;
            internal double[]? Numbers;
            internal string?[]? Strings;

            internal bool Same(Column other, int j) =>
                Codes != null ? Codes[j] == other.Codes![j]
                : Numbers != null ? BitConverter.DoubleToInt64Bits(Numbers[j]) == BitConverter.DoubleToInt64Bits(other.Numbers![j])
                : string.Equals(Strings![j], other.Strings![j], StringComparison.Ordinal);

            internal bool IsSentinel(int j) =>
                Codes != null ? Codes[j] == 0 : Numbers != null ? double.IsNaN(Numbers[j]) : Strings![j] == null;
        }

        /// One read of a rect of the map (the whole map for a frame), as
        /// Encode encodes it.
        internal sealed class GridRead
        {
            internal int MapId, X, Z, Width, Height;
            internal double SkyGlow;
            internal Column[] Columns = Array.Empty<Column>();
            // The things on each cell (null for none), row-major like the columns.
            internal ThingRec[]?[] Things = Array.Empty<ThingRec[]?>();
            internal int Count => Width * Height;
        }

        // On the game thread: every cell of map. A fogged cell is not held
        // (every array at its sentinel); glow leaves the sky out.
        internal static GridRead Read(Map map) => Read(map, 0, 0, map.Size.x, map.Size.z, CellGridCache.For(map));

        // The whole map read from scratch, bypassing the cell cache: what the
        // cached read must equal.
        internal static GridRead ReadUncached(Map map) => Read(map, 0, 0, map.Size.x, map.Size.z, null);

        // On the game thread: the w x h rect at (x0, z0), on the map.
        internal static GridRead Read(Map map, int x0, int z0, int w, int h) => Read(map, x0, z0, w, h, null);

        // cache, when set, is the map's cell cache and the rect is the whole map.
        private static GridRead Read(Map map, int x0, int z0, int w, int h, CellGridCache? cache)
        {
            cache?.Sync();
            int n = w * h, mapWidth = map.Size.x;
            var read = new GridRead { MapId = map.uniqueID, X = x0, Z = z0, Width = w, Height = h, SkyGlow = Finite(map.skyManager.CurSkyGlow), Columns = new Column[Fields.Length], Things = new ThingRec[]?[n] };
            for (int i = 0; i < Fields.Length; i++)
            {
                var column = new Column();
                switch (Fields[i].Kind)
                {
                    case Kind.Code: column.Codes = new byte[n]; break;
                    case Kind.Number: column.Numbers = new double[n]; for (int j = 0; j < n; j++) column.Numbers[j] = double.NaN; break;
                    default: column.Strings = new string?[n]; break;
                }
                read.Columns[i] = column;
            }
            var c = read.Columns;
            byte B(bool value) => value ? (byte)2 : (byte)1;
            var player = Faction.OfPlayerSilentFail;
            var biotech = ModsConfig.BiotechActive;
            var things = new ThingReader(map, player);
            var terrainGrid = map.terrainGrid;
            var affordances = new Dictionary<TerrainDef, string>();
            var home = map.areaManager.Home;
            // A room's key is the whole-map index (row-major) of its first
            // held cell in the read, not Room.ID: RimWorld regenerates rooms
            // near any edifice change with fresh ids, which would make every
            // room cell differ from the keyframe; the first cell stays put
            // unless it changes.
            var roomKeys = new Dictionary<Room, string>();
            var roomIndoors = new Dictionary<Room, byte>();
            var lights = new Dictionary<TerrainDef, byte>();
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                {
                    var cell = new IntVec3(x0 + x, 0, z0 + z);
                    var j = z * w + x;
                    if (cell.Fogged(map)) continue;
                    c[Cell].Codes![j] = 1;
                    byte walkable, doorway, storageEmpty, light; string? terrainName, foundation; Room? room;
                    if (cache != null && cache.Valid[j])
                    {
                        walkable = cache.Walkable[j]; doorway = cache.Doorway[j]; storageEmpty = cache.StorageEmpty[j]; light = cache.Light[j];
                        terrainName = cache.Terrain[j]; foundation = cache.Foundation[j]; room = cache.Room[j];
                    }
                    else
                    {
                        walkable = B(cell.Walkable(map));
                        CellThingFlags(map, cell, out var isDoorway, out var isEmpty);
                        doorway = B(isDoorway); storageEmpty = B(isEmpty);
                        var terrain = terrainGrid.TerrainAt(cell);
                        if (!lights.TryGetValue(terrain, out light)) lights[terrain] = light = B(terrain.affordances.Contains(TerrainAffordanceDefOf.Light));
                        terrainName = Identifier(terrain.defName);
                        var baseTerrain = terrainGrid.BaseTerrainAt(cell);
                        if (!affordances.TryGetValue(baseTerrain, out foundation))
                            affordances[baseTerrain] = foundation = string.Join(",", baseTerrain.affordances.Select(a => Identifier(a.defName)).OrderBy(a => a, StringComparer.Ordinal));
                        room = cell.GetRoom(map);
                        if (cache != null)
                        {
                            cache.Walkable[j] = walkable; cache.Doorway[j] = doorway; cache.StorageEmpty[j] = storageEmpty; cache.Light[j] = light;
                            cache.Terrain[j] = terrainName; cache.Foundation[j] = foundation; cache.Room[j] = room; cache.Valid[j] = true;
                        }
                    }
                    c[Walkable].Codes![j] = walkable;
                    c[Doorway].Codes![j] = doorway;
                    c[SupportsLight].Codes![j] = light;
                    c[StorageEmpty].Codes![j] = storageEmpty;
                    c[Terrain].Strings![j] = terrainName;
                    c[FoundationAffordances].Strings![j] = foundation;
                    var roof = map.roofGrid.RoofAt(cell);
                    c[Roofed].Codes![j] = B(roof != null);
                    if (roof != null) c[Roof].Strings![j] = Identifier(roof.defName);
                    var zone = map.zoneManager.ZoneAt(cell);
                    c[Zone].Codes![j] = B(zone != null);
                    if (zone != null) c[ZoneId].Strings![j] = zone.GetUniqueLoadID();
                    byte indoors = 1;
                    if (room != null)
                    {
                        if (!roomKeys.TryGetValue(room, out var key)) { roomKeys[room] = key = (cell.z * mapWidth + cell.x).ToString(System.Globalization.CultureInfo.InvariantCulture); roomIndoors[room] = B(CellTracking.Indoors(room)); }
                        c[Room].Strings![j] = key;
                        indoors = roomIndoors[room];
                    }
                    else indoors = B(false);
                    c[Indoors].Codes![j] = indoors;
                    c[Polluted].Codes![j] = B(biotech && map.pollutionGrid.IsPolluted(cell));
                    c[Glow].Numbers![j] = Finite(map.glowGrid.GroundGlowAt(cell, false, true));
                    c[InHome].Codes![j] = B(home[cell]);
                    // Held to 1/100: snowfall would otherwise re-send the column every frame.
                    c[SnowDepth].Numbers![j] = Math.Round(Finite(map.snowGrid.GetDepth(cell)) * 100.0) / 100.0;
                    c[TopLayerRemovable].Codes![j] = B(terrainGrid.CanRemoveTopLayerAt(cell));
                    read.Things[j] = things.At(cell);
                    var fertility = map.fertilityGrid.FertilityAt(cell);
                    if (fertility > 0f) c[Fertility].Numbers![j] = Finite(fertility);
                }
            return read;
        }

        // NativeObservationTools.CellDoorway and NativeZoneCreation.StorageEmpty
        // in one pass over the cell's thing list, without their LINQ allocations.
        private static void CellThingFlags(Map map, IntVec3 cell, out bool doorway, out bool storageEmpty)
        {
            doorway = cell.GetDoor(map) != null; storageEmpty = true;
            var list = cell.GetThingList(map);
            for (int i = 0; i < list.Count; i++)
            {
                var t = list[i];
                if (t is Blueprint || t is Frame)
                {
                    storageEmpty = false;
                    if (!doorway && t.def.entityDefToBuild is ThingDef built && typeof(Building_Door).IsAssignableFrom(built.thingClass)) doorway = true;
                }
                else if (t is Plant || t is Building || t.def.category == ThingCategory.Item) storageEmpty = false;
            }
        }

        // Encoder worker state: the keyframe deltas are against.
        private static GridRead? keyframe;
        private static ulong keyframeSeq;
        private static long keyframeAt;

        /// On the encoder worker: adds read to frame as a keyframe or a
        /// delta against the held keyframe. A null read (the capture failed)
        /// adds nothing and makes the next grid a keyframe.
        internal static void Attach(Obs.BundleSnapshot frame, GridRead? read, bool streamKeyframe)
        {
            if (read == null) { keyframe = null; return; }
            var now = Stopwatch.GetTimestamp();
            var held = keyframe;
            Mirror.CellGrid? grid = null;
            if (!streamKeyframe && held != null && held.MapId == read.MapId && held.X == read.X && held.Z == read.Z && held.Width == read.Width && held.Height == read.Height
                && now - keyframeAt < KeyframeSeconds * Stopwatch.Frequency)
                grid = Encode(read, held);
            if (grid == null)
            {
                grid = Encode(read, null)!;
                keyframe = read;
                keyframeSeq++;
                keyframeAt = now;
            }
            frame.Grid = grid;
            frame.KeyframeSeq = keyframeSeq;
            frame.SkyGlow = read.SkyGlow;
        }

        /// read as a CellGrid: every array over the sentinel array when
        /// against is null (a keyframe), else only the arrays that differ
        /// from against's; null when that would be every array.
        internal static Mirror.CellGrid? Encode(GridRead read, GridRead? against)
        {
            var n = read.Count;
            var grid = new Mirror.CellGrid { Rect = new Mirror.CellRect { X = read.X, Z = read.Z, Width = read.Width, Height = read.Height } };
            var table = new Dictionary<string, uint>(StringComparer.Ordinal);
            var carried = 0;
            var changed = new List<int>();
            uint Put(string s)
            {
                if (!table.TryGetValue(s, out var k)) { grid.Strings.Add(s); k = (uint)grid.Strings.Count; table[s] = k; }
                return k;
            }
            for (int i = 0; i < Fields.Length; i++)
            {
                var column = read.Columns[i];
                var basis = against?.Columns[i];
                changed.Clear();
                for (int j = 0; j < n; j++)
                    if (basis != null ? !column.Same(basis, j) : !column.IsSentinel(j)) changed.Add(j);
                if (basis != null && changed.Count == 0) continue;
                carried++;
                Fields[i].Set(grid, EncodeArray(Fields[i].Kind, column, changed, n, grid, table));
            }
            // The things: the cells whose list differs from the keyframe's
            // (a keyframe lists every cell with any), each list replaced whole.
            changed.Clear();
            for (int j = 0; j < n; j++)
                if (against != null ? !ThingRec.SameList(read.Things[j], against.Things[j]) : read.Things[j] != null) changed.Add(j);
            if (against == null || changed.Count > 0)
            {
                carried++;
                var list = new Mirror.ThingList();
                uint offset = 0;
                list.Offsets.Add(0);
                foreach (var j in changed)
                {
                    list.Cells.Add((uint)j);
                    foreach (var thing in read.Things[j] ?? Array.Empty<ThingRec>()) { list.Things.Add(thing.Wire(Put)); offset++; }
                    list.Offsets.Add(offset);
                }
                grid.Things = list;
            }
            return against != null && carried == Fields.Length + 1 ? null : grid;
        }

        // One array, dense or sparse over changed, whichever encodes
        // smaller; a string's table index is assigned only for the form sent.
        private static Mirror.FieldArray EncodeArray(Kind kind, Column column, List<int> changed, int n, Mirror.CellGrid grid, Dictionary<string, uint> table)
        {
            long sparseBytes = 0, denseBytes;
            foreach (var j in changed) sparseBytes += CodedOutputStream.ComputeUInt32Size((uint)j);
            switch (kind)
            {
                case Kind.Code:
                    sparseBytes += changed.Count; denseBytes = n; break;
                case Kind.Number:
                    sparseBytes += 8L * changed.Count; denseBytes = 8L * n; break;
                default:
                {
                    // Sizes of each form's indexes as if it alone extended the table.
                    var next = (uint)grid.Strings.Count;
                    var seen = new Dictionary<string, uint>(StringComparer.Ordinal);
                    uint Index(string? s)
                    {
                        if (s == null) return 0;
                        if (table.TryGetValue(s, out var k) || seen.TryGetValue(s, out k)) return k;
                        seen[s] = k = ++next;
                        return k;
                    }
                    foreach (var j in changed) sparseBytes += CodedOutputStream.ComputeUInt32Size(Index(column.Strings![j]));
                    seen.Clear(); next = (uint)grid.Strings.Count;
                    denseBytes = 0;
                    for (int j = 0; j < n; j++) denseBytes += CodedOutputStream.ComputeUInt32Size(Index(column.Strings![j]));
                    break;
                }
            }
            uint Put(string? s)
            {
                if (s == null) return 0;
                if (!table.TryGetValue(s, out var k)) { grid.Strings.Add(s); k = (uint)grid.Strings.Count; table[s] = k; }
                return k;
            }
            if (sparseBytes < denseBytes)
            {
                var sparse = new Mirror.SparseArray();
                foreach (var j in changed)
                {
                    sparse.Index.Add((uint)j);
                    switch (kind)
                    {
                        case Kind.Code: sparse.Code.Add(column.Codes![j]); break;
                        case Kind.Number: sparse.Number.Add(column.Numbers![j]); break;
                        default: sparse.Code.Add(Put(column.Strings![j])); break;
                    }
                }
                return new Mirror.FieldArray { Sparse = sparse };
            }
            switch (kind)
            {
                case Kind.Code: return new Mirror.FieldArray { Codes = ByteString.CopyFrom(column.Codes) };
                case Kind.Number:
                {
                    var numbers = new Mirror.PackedDouble();
                    numbers.Values.AddRange(column.Numbers!);
                    return new Mirror.FieldArray { Numbers = numbers };
                }
                default:
                {
                    var indexes = new Mirror.PackedUint32();
                    for (int j = 0; j < n; j++) indexes.Values.Add(Put(column.Strings![j]));
                    return new Mirror.FieldArray { Indexes = indexes };
                }
            }
        }

        private static string Identifier(string? value) => ProtoBoundary.IsIdentifier(value!) ? value! : throw new InvalidOperationException("Native identifier unavailable.");
        private static double Finite(double value) => double.IsNaN(value) || double.IsInfinity(value) ? throw new InvalidOperationException("Nonfinite native fact.") : value;
    }
}
