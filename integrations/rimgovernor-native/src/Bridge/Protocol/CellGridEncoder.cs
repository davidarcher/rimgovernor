#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Google.Protobuf;
using RimWorld;
using Verse;
using Mirror = RimGovernor.Protocol.Mirror;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// The whole-map cell grid every snapshot frame carries (#1345): one
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
        internal const int Cell = 0, Walkable = 1, Occupied = 2, Zone = 3, Roofed = 4, Indoors = 5, SupportsLight = 6,
            StorageEmpty = 7, Doorway = 8, Fertility = 9, Polluted = 10, Glow = 11, Roof = 12, ZoneId = 13, NaturalRock = 14,
            Ruin = 15, PlayerEdifice = 16, ClaimableRuin = 17, RuinHold = 18, Room = 19;

        private static readonly (string Name, Kind Kind, Action<Mirror.CellGrid, Mirror.FieldArray> Set)[] Fields =
        {
            ("cell", Kind.Code, (g, a) => g.Cell = a),
            ("walkable", Kind.Code, (g, a) => g.Walkable = a),
            ("occupied", Kind.Code, (g, a) => g.Occupied = a),
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
            ("natural_rock", Kind.Code, (g, a) => g.NaturalRock = a),
            ("ruin", Kind.Code, (g, a) => g.Ruin = a),
            ("player_edifice", Kind.Index, (g, a) => g.PlayerEdifice = a),
            ("claimable_ruin", Kind.Index, (g, a) => g.ClaimableRuin = a),
            ("ruin_hold", Kind.Index, (g, a) => g.RuinHold = a),
            ("room", Kind.Index, (g, a) => g.Room = a),
        };

        internal static string FieldName(int field) => Fields[field].Name;
        internal static int FieldCount => Fields.Length;

        /// One field's values over the map: codes, numbers (NaN unknown) or
        /// strings (null unknown; ruin_hold null is empty).
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

        /// One read of the whole map, as Attach encodes it.
        internal sealed class GridRead
        {
            internal int MapId, Width, Height;
            internal double SkyGlow;
            internal Column[] Columns = Array.Empty<Column>();
            internal int Count => Width * Height;
        }

        // On the game thread: every cell of map. A fogged cell is not held
        // (every array at its sentinel); a held cell reads as the get_cells
        // planning fields do (NativeObservationTools.ReadCells), glow with
        // the sky left out.
        internal static GridRead Read(Map map)
        {
            int w = map.Size.x, h = map.Size.z, n = w * h;
            var read = new GridRead { MapId = map.uniqueID, Width = w, Height = h, SkyGlow = Finite(map.skyManager.CurSkyGlow), Columns = new Column[Fields.Length] };
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
            List<RectTrigger>? triggers = null;
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                {
                    var cell = new IntVec3(x, 0, z);
                    var j = z * w + x;
                    if (cell.Fogged(map)) continue;
                    c[Cell].Codes![j] = 1;
                    c[Walkable].Codes![j] = B(cell.Walkable(map));
                    c[Occupied].Codes![j] = B(NativeObservationTools.CellOccupied(map, cell));
                    c[Doorway].Codes![j] = B(NativeObservationTools.CellDoorway(map, cell));
                    c[SupportsLight].Codes![j] = B(cell.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Light));
                    var roof = map.roofGrid.RoofAt(cell);
                    c[Roofed].Codes![j] = B(roof != null);
                    if (roof != null) c[Roof].Strings![j] = Identifier(roof.defName);
                    var edifice = map.edificeGrid[cell];
                    var naturalRock = edifice?.def.building?.isNaturalRock == true;
                    c[NaturalRock].Codes![j] = B(naturalRock);
                    var ruin = false;
                    if (edifice != null && player != null && edifice.Faction != player && !naturalRock && !edifice.def.mineable && edifice.DeconstructibleBy(player))
                        ruin = !NativeClearanceObservationTools.AncientDanger(map, edifice, player, triggers ??= NativeClearanceObservationTools.TempleTriggers(map));
                    c[Ruin].Codes![j] = B(ruin);
                    c[PlayerEdifice].Strings![j] = edifice != null && player != null && edifice.Faction == player ? Identifier(edifice.def.defName) : "";
                    c[ClaimableRuin].Strings![j] = ruin && edifice!.ClaimableBy(player) ? Identifier(edifice.def.defName) : "";
                    var zone = map.zoneManager.ZoneAt(cell);
                    c[Zone].Codes![j] = B(zone != null);
                    if (zone != null) c[ZoneId].Strings![j] = zone.GetUniqueLoadID();
                    c[StorageEmpty].Codes![j] = B(NativeZoneCreation.StorageEmpty(cell, map));
                    var room = cell.GetRoom(map);
                    if (room != null) c[Room].Strings![j] = room.ID.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    c[Indoors].Codes![j] = B(CellTracking.Indoors(room));
                    c[Polluted].Codes![j] = B(biotech && map.pollutionGrid.IsPolluted(cell));
                    c[Glow].Numbers![j] = Finite(map.glowGrid.GroundGlowAt(cell, false, true));
                    var fertility = map.fertilityGrid.FertilityAt(cell);
                    if (fertility > 0f) c[Fertility].Numbers![j] = Finite(fertility);
                }
            return read;
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
            if (!streamKeyframe && held != null && held.MapId == read.MapId && held.Width == read.Width && held.Height == read.Height
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
            var grid = new Mirror.CellGrid { Rect = new Mirror.CellRect { X = 0, Z = 0, Width = read.Width, Height = read.Height } };
            var table = new Dictionary<string, uint>(StringComparer.Ordinal);
            var carried = 0;
            var changed = new List<int>();
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
            return against != null && carried == Fields.Length ? null : grid;
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
