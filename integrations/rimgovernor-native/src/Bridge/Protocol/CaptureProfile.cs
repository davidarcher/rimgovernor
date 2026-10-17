#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using RimWorld;
using Verse;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// Read-only measurement of the snapshot frame capture for the acceptance probe: wall time and managed
    /// allocation of the whole capture, each of its sections, the cell grid read (cached and uncached), each game
    /// API the grid read calls over every cell on its own, and the off-thread grid encode. Nothing is published
    /// or kept; the stream's own schedule is untouched.
    /// </summary>
    internal static class CaptureProfile
    {
        /// One measured span's samples.
        internal sealed class Samples
        {
            internal readonly List<double> Ms = new List<double>();
            internal readonly List<long> Bytes = new List<long>();
        }

        internal sealed class Recorder
        {
            internal readonly Dictionary<string, Samples> Spans = new Dictionary<string, Samples>(StringComparer.Ordinal);

            internal void Add(string name, double ms, long bytes)
            {
                if (!Spans.TryGetValue(name, out var s)) Spans[name] = s = new Samples();
                s.Ms.Add(ms);
                if (bytes >= 0) s.Bytes.Add(bytes);
            }

            internal T Time<T>(string name, Func<T> body)
            {
                var bytes = GC.GetAllocatedBytesForCurrentThread();
                var started = Stopwatch.GetTimestamp();
                var result = body();
                Add(name, Ms(Stopwatch.GetTimestamp() - started), GC.GetAllocatedBytesForCurrentThread() - bytes);
                return result;
            }

            internal void Time(string name, Action body) => Time(name, () => { body(); return 0; });
        }

        /// The game-thread half: the map's size and the frame and grid read the last iteration captured, for the encode
        /// and decode halves to run on.
        internal sealed class GameSide
        {
            internal int Width, Height, Held, Things;
            internal Obs.BundleSnapshot? Frame;
            internal CellGridEncoder.GridRead? Grid;
        }

        internal static double Ms(long stopwatchTicks) => stopwatchTicks * 1000.0 / Stopwatch.Frequency;

        /// On the game thread: iterations captures after one warm-up, each followed by the grid reads and the
        /// per-column API passes.
        internal static GameSide Capture(Recorder rec, int iterations)
        {
            var map = Find.CurrentMap ?? throw new InvalidOperationException("no current map");
            var shape = SnapshotStream.Subscription;
            var side = new GameSide { Width = map.Size.x, Height = map.Size.z };
            SnapshotFrames.Capture(map, shape, out _);
            for (int i = 0; i < iterations; i++)
            {
                ObservationWork.BeginCapture();
                CellGridEncoder.GridRead? grid = null;
                Obs.BundleSnapshot? frame;
                try { frame = rec.Time("capture.total", () => SnapshotFrames.Capture(map, shape, out grid)); }
                finally
                {
                    var hop = ObservationWork.End();
                    if (hop != null)
                        foreach (var s in hop.Sections) rec.Add((s.Detail ? "capture.detail." : "capture.section.") + s.Name, Ms(s.Ticks), -1);
                }
                side.Frame = frame; side.Grid = grid;
                rec.Time("grid.read.cached", () => CellGridEncoder.Read(map));
                rec.Time("grid.read.uncached", () => CellGridEncoder.ReadUncached(map));
                Columns(map, rec);
            }
            if (side.Grid != null)
            {
                var cells = side.Grid.Columns[CellGridEncoder.Cell].Codes!;
                side.Held = cells.Count(c => c == 1);
                side.Things = side.Grid.Things.Sum(t => t?.Length ?? 0);
            }
            return side;
        }

        // Each game API the grid read calls, over every cell on its own, into one reused buffer: the floor of reading
        // that column every frame with no allocation of ours.
        private static void Columns(Map map, Recorder rec)
        {
            int w = map.Size.x, h = map.Size.z, n = w * h;
            var cells = new IntVec3[n];
            for (int z = 0; z < h; z++) for (int x = 0; x < w; x++) cells[z * w + x] = new IntVec3(x, 0, z);
            var flags = new bool[n];
            var numbers = new double[n];
            var refs = new object?[n];
            var ints = new int[n];
            var terrainGrid = map.terrainGrid;
            var home = map.areaManager.Home;
            var biotech = ModsConfig.BiotechActive;
            var player = Faction.OfPlayerSilentFail;
            rec.Time("column.fogged", () => { for (int j = 0; j < n; j++) flags[j] = cells[j].Fogged(map); });
            rec.Time("column.walkable", () => { for (int j = 0; j < n; j++) flags[j] = cells[j].Walkable(map); });
            rec.Time("column.thingFacts", () => { for (int j = 0; j < n; j++) { CellGridEncoder.CellThingFacts(map, cells[j], out flags[j], out ints[j], out var cap); numbers[j] = cap; } });
            rec.Time("column.terrain", () => { for (int j = 0; j < n; j++) refs[j] = terrainGrid.TerrainAt(cells[j]); });
            rec.Time("column.baseTerrain", () => { for (int j = 0; j < n; j++) refs[j] = terrainGrid.BaseTerrainAt(cells[j]); });
            rec.Time("column.room", () => { for (int j = 0; j < n; j++) refs[j] = cells[j].GetRoom(map); });
            rec.Time("column.roof", () => { for (int j = 0; j < n; j++) refs[j] = map.roofGrid.RoofAt(cells[j]); });
            rec.Time("column.zone", () => { for (int j = 0; j < n; j++) refs[j] = map.zoneManager.ZoneAt(cells[j]); });
            rec.Time("column.polluted", () => { for (int j = 0; j < n; j++) flags[j] = biotech && map.pollutionGrid.IsPolluted(cells[j]); });
            rec.Time("column.glow", () => { for (int j = 0; j < n; j++) numbers[j] = map.glowGrid.GroundGlowAt(cells[j], false, true); });
            rec.Time("column.home", () => { for (int j = 0; j < n; j++) flags[j] = home[cells[j]]; });
            rec.Time("column.snow", () => { for (int j = 0; j < n; j++) numbers[j] = map.snowGrid.GetDepth(cells[j]); });
            rec.Time("column.topLayerRemovable", () => { for (int j = 0; j < n; j++) flags[j] = terrainGrid.CanRemoveTopLayerAt(cells[j]); });
            rec.Time("column.fertility", () => { for (int j = 0; j < n; j++) numbers[j] = map.fertilityGrid.FertilityAt(cells[j]); });
            // The thing reader as the grid read runs it: one instance per read, records cached per thing.
            rec.Time("column.things", () => { var things = new ThingReader(map, player); for (int j = 0; j < n; j++) refs[j] = things.At(cells[j]); });
            // The raw thing lists alone, without building records.
            rec.Time("column.thingLists", () => { for (int j = 0; j < n; j++) ints[j] = map.thingGrid.ThingsListAtFast(cells[j]).Count; });
            rec.Time("column.pathCost", () => { for (int j = 0; j < n; j++) ints[j] = map.pathing.Normal.pathGrid.Cost(cells[j]); });
        }

        /// Off the game thread: the whole-grid encode the stream's publish task runs.
        internal static void Encode(Recorder rec, GameSide side, int iterations)
        {
            if (side.Frame == null || side.Grid == null) return;
            for (int i = 0; i < iterations; i++)
                side.Frame = rec.Time("encode.attachWhole", () => CellGridEncoder.AttachWhole(side.Frame, side.Grid));
        }

        /// The recorder as JSON: the map, then per span the sample count, min/median/max ms and median bytes (-1 unmeasured).
        internal static string Summary(Recorder rec, GameSide side)
        {
            static double Median(List<double> v) { var s = v.OrderBy(x => x).ToList(); return s[s.Count / 2]; }
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            using (var w = new System.Text.Json.Utf8JsonWriter(buffer, new System.Text.Json.JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteNumber("map_width", side.Width);
                w.WriteNumber("map_height", side.Height);
                w.WriteNumber("held_cells", side.Held);
                w.WriteNumber("grid_things", side.Things);
                w.WriteString("runtime", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
                w.WriteStartObject("spans");
                foreach (var (name, s) in rec.Spans.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    w.WriteStartObject(name);
                    w.WriteNumber("n", s.Ms.Count);
                    w.WriteNumber("ms_min", Math.Round(s.Ms.Min(), 3));
                    w.WriteNumber("ms_median", Math.Round(Median(s.Ms), 3));
                    w.WriteNumber("ms_max", Math.Round(s.Ms.Max(), 3));
                    w.WriteNumber("bytes_median", s.Bytes.Count == 0 ? -1 : s.Bytes.OrderBy(x => x).ElementAt(s.Bytes.Count / 2));
                    w.WriteEndObject();
                }
                w.WriteEndObject();
                w.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
    }
}
