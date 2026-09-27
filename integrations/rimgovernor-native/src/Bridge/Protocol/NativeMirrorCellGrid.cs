#nullable enable
using System;
using System.Collections.Generic;
using Google.Protobuf;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Mirror = RimGovernor.Protocol.Mirror;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// The planning_cells mirror section (#795): the planning window read
    /// through observations_get_cells' planning fields, held as one array
    /// per policy.SiteCell field (CellGrid in mirror.proto). The window is
    /// re-read whole on each due poll and compared array by array with the
    /// last one; an array that differs takes the next stamp. A delta after
    /// a watermark carries the arrays stamped after it, each whole or as
    /// (index, value) pairs over the array the client holds, whichever is
    /// smaller. The client's array is known when it is the current or the
    /// previous version; anything older is sent whole.
    /// </summary>
    internal static class NativeMirrorCellGrid
    {
        internal const int MaxCells = 65536;

        private enum Kind { Code, Number, Index }

        // CellGrid's FieldArray fields 3..21, in order.
        private static readonly Kind[] Kinds = {
            Kind.Code,   // cell
            Kind.Code,   // walkable
            Kind.Code,   // occupied
            Kind.Code,   // zone
            Kind.Code,   // roofed
            Kind.Code,   // indoors
            Kind.Code,   // supports_light
            Kind.Code,   // storage_empty
            Kind.Code,   // doorway
            Kind.Number, // fertility
            Kind.Code,   // polluted
            Kind.Number, // glow
            Kind.Index,  // roof
            Kind.Index,  // zone_id
            Kind.Code,   // natural_rock
            Kind.Code,   // ruin
            Kind.Index,  // player_edifice
            Kind.Index,  // claimable_ruin
            Kind.Index,  // ruin_hold
        };

        private sealed class Column
        {
            public byte[]? Codes; public double[]? Numbers; public string?[]? Strings;

            public static Column For(Kind kind, int n) => kind switch {
                Kind.Code => new Column { Codes = new byte[n] },
                Kind.Number => new Column { Numbers = Filled(n) },
                _ => new Column { Strings = new string?[n] },
            };

            private static double[] Filled(int n) { var a = new double[n]; for (var i = 0; i < n; i++) a[i] = double.NaN; return a; }

            // Same compares cell i of this column with base b (null: the sentinel).
            public bool Same(int i, Column? b)
            {
                if (Codes != null) return Codes[i] == (b?.Codes?[i] ?? 0);
                if (Numbers != null) return BitConverter.DoubleToInt64Bits(Numbers[i]) == BitConverter.DoubleToInt64Bits(b?.Numbers?[i] ?? double.NaN);
                return string.Equals(Strings![i], b?.Strings?[i], StringComparison.Ordinal);
            }

            public bool Equal(Column other)
            {
                var n = Codes?.Length ?? Numbers?.Length ?? Strings!.Length;
                for (var i = 0; i < n; i++) if (!Same(i, other)) return false;
                return true;
            }
        }

        private sealed class State
        {
            public int X, Z, W, H;
            public Column[] Cur = Array.Empty<Column>();
            public Column?[] Prev = Array.Empty<Column?>();
            public EntityTracking.Mark[] Changed = Array.Empty<EntityTracking.Mark>();
            public EntityTracking.Mark[] PrevChanged = Array.Empty<EntityTracking.Mark>();
            public EntityTracking.Mark Base;
        }

        // The last window read per map, by map uniqueID.
        private static readonly Dictionary<int, State> States = new Dictionary<int, State>();

        internal static bool ValidWindow(Mirror.CellRect? window) => window != null && window.HasX && window.HasZ && window.HasWidth && window.HasHeight
            && window.X >= 0 && window.Z >= 0 && window.Width >= 1 && window.Height >= 1 && (long)window.Width * window.Height <= MaxCells;

        // Read is the section page for window on map: a keyframe when since is
        // null or older than the window's first read, else the delta after
        // since, with the whole window beside it when resync is set (the
        // drift backstop). Null when the window is off the map or the read
        // failed.
        internal static Mirror.SectionPage? Read(Map map, Common.ObservationContext context, Mirror.CellRect window, EntityTracking.Mark? since, bool resync = false)
        {
            if (window.X + window.Width > map.Size.x || window.Z + window.Height > map.Size.z) return null;
            var cols = Snapshot(map, context, window);
            if (cols == null) return null;
            if (!States.TryGetValue(map.uniqueID, out var state) || state.X != window.X || state.Z != window.Z || state.W != window.Width || state.H != window.Height)
            {
                var mark = EntityTracking.Next();
                state = new State { X = window.X, Z = window.Z, W = window.Width, H = window.Height, Cur = cols, Prev = new Column?[cols.Length],
                    Changed = new EntityTracking.Mark[cols.Length], PrevChanged = new EntityTracking.Mark[cols.Length], Base = mark };
                for (var i = 0; i < cols.Length; i++) state.Changed[i] = mark;
                States[map.uniqueID] = state;
            }
            else
            {
                EntityTracking.Mark? mark = null;
                for (var i = 0; i < cols.Length; i++)
                {
                    if (cols[i].Equal(state.Cur[i])) continue;
                    mark ??= EntityTracking.Next();
                    state.Prev[i] = state.Cur[i]; state.PrevChanged[i] = state.Changed[i];
                    state.Cur[i] = cols[i]; state.Changed[i] = mark.Value;
                }
            }
            var page = new Mirror.SectionPage { Section = Mirror.Section.PlanningCells };
            var grid = new Mirror.CellGrid { Rect = window.Clone() };
            var table = new Dictionary<string, uint>(StringComparer.Ordinal);
            var keyframe = since == null || since.Value.CompareTo(state.Base) < 0;
            for (var i = 0; i < cols.Length; i++)
            {
                if (keyframe) { Set(grid, i, Encode(state.Cur[i], null, Kinds[i], grid, table)); continue; }
                if (state.Changed[i].CompareTo(since!.Value) <= 0) continue;
                var held = state.Prev[i] != null && state.PrevChanged[i].CompareTo(since.Value) <= 0 ? state.Prev[i] : null;
                Set(grid, i, Encode(state.Cur[i], held, Kinds[i], grid, table, dense: held == null));
            }
            if (keyframe) page.Keyframe = new Mirror.Keyframe { Cells = grid };
            else
            {
                page.Delta = new Mirror.Delta { From = new Mirror.Watermark { Tick = since!.Value.Tick, Seq = since.Value.Seq }, Cells = grid };
                if (resync)
                {
                    var whole = new Mirror.CellGrid { Rect = window.Clone() };
                    var strings = new Dictionary<string, uint>(StringComparer.Ordinal);
                    for (var i = 0; i < cols.Length; i++) Set(whole, i, Encode(state.Cur[i], null, Kinds[i], whole, strings));
                    page.Resync = new Mirror.Keyframe { Cells = whole };
                }
            }
            return page;
        }

        // Arrays counts a delta grid's arrays: zero is a delta with no change.
        internal static int Arrays(Mirror.CellGrid? grid)
        {
            if (grid == null) return 0;
            var n = 0;
            for (var i = 0; i < Kinds.Length; i++) if (Get(grid, i) != null) n++;
            return n;
        }

        // Encode is one column against base b (null: the sentinel array): the
        // dense form, or the sparse pairs where they are smaller. dense
        // forces the dense form (the client's array is not known).
        private static Mirror.FieldArray Encode(Column col, Column? b, Kind kind, Mirror.CellGrid grid, Dictionary<string, uint> table, bool dense = false)
        {
            uint Intern(string? value, int field)
            {
                if (value == null) return 0;
                if (!table.TryGetValue(value, out var k)) { grid.Strings.Add(value); k = (uint)grid.Strings.Count; table.Add(value, k); }
                return k;
            }
            var whole = new Mirror.FieldArray();
            var n = col.Codes?.Length ?? col.Numbers?.Length ?? col.Strings!.Length;
            switch (kind)
            {
                case Kind.Code: whole.Codes = ByteString.CopyFrom(col.Codes!); break;
                case Kind.Number: whole.Numbers = new Mirror.PackedDouble(); whole.Numbers.Values.AddRange(col.Numbers!); break;
                default:
                    whole.Indexes = new Mirror.PackedUint32();
                    for (var i = 0; i < n; i++) whole.Indexes.Values.Add(Intern(col.Strings![i], i));
                    break;
            }
            if (dense) return whole;
            var sparse = new Mirror.SparseArray();
            for (var i = 0; i < n; i++)
            {
                if (col.Same(i, b)) continue;
                sparse.Index.Add((uint)i);
                if (kind == Kind.Code) sparse.Code.Add(col.Codes![i]);
                else if (kind == Kind.Number) sparse.Number.Add(col.Numbers![i]);
                else sparse.Code.Add(Intern(col.Strings![i], i));
            }
            var pairs = new Mirror.FieldArray { Sparse = sparse };
            return pairs.CalculateSize() < whole.CalculateSize() ? pairs : whole;
        }

        private static void Set(Mirror.CellGrid g, int field, Mirror.FieldArray a)
        {
            switch (field)
            {
                case 0: g.Cell = a; break; case 1: g.Walkable = a; break; case 2: g.Occupied = a; break;
                case 3: g.Zone = a; break; case 4: g.Roofed = a; break; case 5: g.Indoors = a; break;
                case 6: g.SupportsLight = a; break; case 7: g.StorageEmpty = a; break; case 8: g.Doorway = a; break;
                case 9: g.Fertility = a; break; case 10: g.Polluted = a; break; case 11: g.Glow = a; break;
                case 12: g.Roof = a; break; case 13: g.ZoneId = a; break; case 14: g.NaturalRock = a; break;
                case 15: g.Ruin = a; break; case 16: g.PlayerEdifice = a; break; case 17: g.ClaimableRuin = a; break;
                case 18: g.RuinHold = a; break;
            }
        }

        private static Mirror.FieldArray? Get(Mirror.CellGrid g, int field) => field switch {
            0 => g.Cell, 1 => g.Walkable, 2 => g.Occupied, 3 => g.Zone, 4 => g.Roofed, 5 => g.Indoors,
            6 => g.SupportsLight, 7 => g.StorageEmpty, 8 => g.Doorway, 9 => g.Fertility, 10 => g.Polluted,
            11 => g.Glow, 12 => g.Roof, 13 => g.ZoneId, 14 => g.NaturalRock, 15 => g.Ruin,
            16 => g.PlayerEdifice, 17 => g.ClaimableRuin, 18 => g.RuinHold, _ => null,
        };

        private static byte Code(bool has, bool value) => has ? (value ? (byte)2 : (byte)1) : (byte)0;

        // Snapshot reads the window's planning fields (observations_get_cells,
        // the planning window's field selection) as the grid's columns, with
        // bridge.PlanningCells' SiteCell semantics: fogged cells not held, a
        // roof or zone applied without a value a known absence, an edifice
        // definition known empty on a row read with traversal. The native
        // knows no clearance census, so ruin_hold stays empty.
        private static Column[]? Snapshot(Map map, Common.ObservationContext context, Mirror.CellRect window)
        {
            var n = window.Width * window.Height;
            var request = new Obs.GetCellsRequest {
                Scope = new Obs.ReadScope { ExpectedIdentity = context.Identity.Clone() },
                Rectangle = new Obs.Rectangle { Minimum = new Common.Cell { X = window.X, Z = window.Z }, Maximum = new Common.Cell { X = window.X + window.Width - 1, Z = window.Z + window.Height - 1 } },
                Fields = new Obs.CellFields { Terrain = false, Roof = true, Visibility = true, Traversal = true, Zone = true, Areas = false, Things = false, Designations = false, Room = true, Growth = true },
                Page = new Common.PageRequest { Limit = (uint)n },
            };
            var reply = NativeObservationTools.ReadCells(map, request, context);
            if (reply.Observed == null || reply.Observed.Compact != null) return null;
            var cols = new Column[Kinds.Length];
            for (var i = 0; i < cols.Length; i++) cols[i] = Column.For(Kinds[i], n);
            foreach (var row in reply.Observed.Cells)
            {
                if (row.Fogged) continue;
                var at = (row.Cell.Z - window.Z) * window.Width + row.Cell.X - window.X;
                if (at < 0 || at >= n) continue;
                cols[0].Codes![at] = 1;
                cols[1].Codes![at] = Code(row.HasWalkable, row.Walkable);
                cols[2].Codes![at] = Code(row.HasOccupied, row.Occupied);
                cols[3].Codes![at] = row.HasZoneId ? (byte)2 : (byte)1;
                cols[4].Codes![at] = row.HasRoof ? (byte)2 : (byte)1;
                cols[5].Codes![at] = Code(row.HasIndoors, row.Indoors);
                cols[6].Codes![at] = Code(row.HasSupportsLight, row.SupportsLight);
                cols[7].Codes![at] = Code(row.HasStorageEmpty, row.StorageEmpty);
                cols[8].Codes![at] = Code(row.HasDoorway, row.Doorway);
                cols[9].Numbers![at] = row.HasFertility ? row.Fertility : double.NaN;
                cols[10].Codes![at] = Code(row.HasPolluted, row.Polluted);
                cols[11].Numbers![at] = row.HasGlow ? row.Glow : double.NaN;
                cols[12].Strings![at] = row.HasRoof ? row.Roof : null;
                cols[13].Strings![at] = row.HasZoneId ? row.ZoneId : null;
                cols[14].Codes![at] = Code(row.HasNaturalRock, row.NaturalRock);
                cols[15].Codes![at] = Code(row.HasRuin, row.Ruin);
                cols[16].Strings![at] = row.HasPlayerEdifice ? row.PlayerEdifice : row.HasWalkable ? "" : null;
                cols[17].Strings![at] = row.HasClaimableRuin ? row.ClaimableRuin : row.HasWalkable ? "" : null;
            }
            return cols;
        }
    }
}
