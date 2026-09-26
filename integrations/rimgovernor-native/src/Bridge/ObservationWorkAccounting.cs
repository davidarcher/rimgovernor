#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace HomeBridge.BridgeTools
{
    /// Where one main-thread observation hop's time went (#642): reading game
    /// state (the capture spans a bundle's sections declare), ProtoJSON
    /// formatting and the UTF-8 size checks that precede it, how many
    /// formatting passes the hop paid for, the bytes it returned, the rows
    /// each section produced and how the hop ended. ProtoBoundary opens a
    /// scope per hop and reports the account beside queueMs/executeMs, so a
    /// slow bundle can be attributed to the colony read or to the encoding
    /// without any per-pawn logging.
    ///
    /// The scope is thread-static. A hop's capture runs to completion on the
    /// game thread; a detached reply (#644) is then encoded on one encoder
    /// worker, which resumes the same hop after the game thread has closed it,
    /// so the two never record at once and no locking is needed. Formatting
    /// on the game thread stays formatMs/formatPasses, the meaning it has
    /// always had; the worker's formatting and its queue wait are the
    /// separate encode block. A hop that declares nothing reports nothing:
    /// absent is not zero.
    internal static class ObservationWork
    {
        /// Bounds the section list so a malformed request cannot grow the
        /// account; a request asks for far fewer sections than this.
        internal const int MaxSections = 48;

        internal sealed class Section
        {
            internal readonly string Name;
            internal long Ticks;
            internal long Rows, Candidates;
            internal Section(string name) { Name = name; }
        }

        internal sealed class Hop
        {
            internal long CaptureTicks, FormatTicks;
            internal int FormatPasses;
            internal long PayloadBytes = -1;
            internal int DroppedSections;
            internal string? Outcome;
            internal readonly List<Section> Sections = new List<Section>();
            internal ulong Frame;
            // The detached encode (#644): set once a worker resumed the hop.
            internal bool Detached, Encoding;
            internal long EncodeQueueTicks, EncodeTicks, EncodeFormatTicks;
            internal int EncodeFormatPasses;
            internal long ThreatExamined = -1, ThreatCandidates, ThreatProjections, ThreatProximityChecks;
            // The planning-window view refresh (#652), when the hop captured one.
            internal PlanningViewRefreshStats? PlanningView;
            internal bool PlanningViewAvailable;
        }

        [ThreadStatic] private static Hop? _current;

        /// The open hop, or null outside one (a call the boundary did not
        /// wrap, or a background thread).
        internal static Hop? Current { get { return _current; } }

        /// Opens a scope for the hop about to run on the game thread,
        /// replacing any scope a previous hop failed to close.
        internal static Hop Begin()
        {
            var hop = new Hop { Frame = FrameAccounting.OpenFrame() };
            _current = hop;
            return hop;
        }

        /// Reopens a hop the game thread closed, on the encoder worker that now
        /// owns its detached reply; every format pass and size check until
        /// End is the worker's.
        internal static void Resume(Hop hop)
        {
            hop.Detached = true;
            hop.Encoding = true;
            _current = hop;
        }

        /// Closes the scope and returns the hop's account, or null when no
        /// scope was open.
        internal static Hop? End()
        {
            var hop = _current;
            _current = null;
            return hop;
        }

        /// One capture span: stopwatchTicks of main-thread game-state reading
        /// for the named section, returning rows of an optional candidates
        /// total (0 where the section's page info does not know one).
        internal static void Captured(string section, long stopwatchTicks, long rows = 0, long candidates = 0)
        {
            var hop = _current;
            if (hop == null) return;
            var ticks = Math.Max(0, stopwatchTicks);
            hop.CaptureTicks += ticks;
            Section entry = null!;
            foreach (var known in hop.Sections) if (string.Equals(known.Name, section, StringComparison.Ordinal)) { entry = known; break; }
            if (entry == null)
            {
                if (hop.Sections.Count >= MaxSections) return;
                entry = new Section(section);
                hop.Sections.Add(entry);
            }
            entry.Ticks += ticks;
            entry.Rows += Math.Max(0, rows);
            entry.Candidates += Math.Max(0, candidates);
        }

        /// One threat classification pass (#646): pawns examined, those kept
        /// as threat rows, full pawn projections paid for and nearest-colonist
        /// scans run. Summed across the hop's passes.
        internal static void ThreatScan(long examined, long candidates, long projections, long proximityChecks)
        {
            var hop = _current;
            if (hop == null) return;
            if (hop.ThreatExamined < 0) hop.ThreatExamined = 0;
            hop.ThreatExamined += examined; hop.ThreatCandidates += candidates;
            hop.ThreatProjections += projections; hop.ThreatProximityChecks += proximityChecks;
        }

        /// The hop's planning-window view refresh (#652): chunks reused,
        /// scanned or rebuilt, the cells and tiles that took, and why a
        /// resync rebuilt everything. available is false when no root was
        /// captured (the view is unavailable for the hop).
        internal static void PlanningViewRefreshed(PlanningViewRefreshStats stats, bool available)
        {
            var hop = _current;
            if (hop == null) return;
            hop.PlanningView = stats; hop.PlanningViewAvailable = available;
        }

        /// One ProtoJSON formatting pass took stopwatchTicks.
        internal static void Formatted(long stopwatchTicks)
        {
            var hop = _current;
            if (hop == null) return;
            if (hop.Encoding) { hop.EncodeFormatPasses++; hop.EncodeFormatTicks += Math.Max(0, stopwatchTicks); return; }
            hop.FormatPasses++;
            hop.FormatTicks += Math.Max(0, stopwatchTicks);
        }

        /// One UTF-8 size check took stopwatchTicks; it is formatting cost,
        /// not a formatting pass.
        internal static void SizeChecked(long stopwatchTicks)
        {
            var hop = _current;
            if (hop == null) return;
            if (hop.Encoding) hop.EncodeFormatTicks += Math.Max(0, stopwatchTicks);
            else hop.FormatTicks += Math.Max(0, stopwatchTicks);
        }

        /// The payload the hop is returning is bytes UTF-8 bytes long. The
        /// last envelope encoded wins, which is the one the caller receives.
        internal static void Payload(long bytes)
        {
            var hop = _current;
            if (hop != null) hop.PayloadBytes = Math.Max(0, bytes);
        }

        /// Sections the reply envelope bound forced the hop to drop.
        internal static void DroppedSections(int sections)
        {
            var hop = _current;
            if (hop != null) hop.DroppedSections += Math.Max(0, sections);
        }

        /// How the hop ended: "ok", "failure", "unavailable" or "error".
        internal static void Outcome(string outcome)
        {
            var hop = _current;
            if (hop != null) hop.Outcome = outcome;
        }

        /// The hop's account as the timing block carries it, or null when the
        /// hop declared nothing worth reporting.
        internal static Dictionary<string, object?>? Report(Hop? hop)
        {
            if (hop == null) return null;
            if (hop.CaptureTicks == 0 && hop.FormatPasses == 0 && hop.PayloadBytes < 0 && hop.Outcome == null && !hop.Detached && hop.ThreatExamined < 0 && hop.PlanningView == null) return null;
            var report = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["captureMs"] = Ms(hop.CaptureTicks),
                ["formatMs"] = Ms(hop.FormatTicks),
                ["formatPasses"] = hop.FormatPasses,
                ["frame"] = hop.Frame,
            };
            if (hop.PayloadBytes >= 0) report["payloadBytes"] = hop.PayloadBytes;
            if (hop.DroppedSections > 0) report["droppedSections"] = hop.DroppedSections;
            if (hop.Outcome != null) report["outcome"] = hop.Outcome;
            if (hop.Detached)
                report["encode"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["queueMs"] = Ms(hop.EncodeQueueTicks),
                    ["ms"] = Ms(hop.EncodeTicks),
                    ["formatMs"] = Ms(hop.EncodeFormatTicks),
                    ["formatPasses"] = hop.EncodeFormatPasses,
                };
            if (hop.ThreatExamined >= 0)
                report["threatScan"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["examined"] = hop.ThreatExamined, ["candidates"] = hop.ThreatCandidates,
                    ["projections"] = hop.ThreatProjections, ["proximityChecks"] = hop.ThreatProximityChecks,
                };
            if (hop.PlanningView is PlanningViewRefreshStats view)
            {
                var entry = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["available"] = hop.PlanningViewAvailable, ["chunks"] = view.Chunks, ["reused"] = view.Reused, ["validated"] = view.Validated,
                    ["rebuilt"] = view.Rebuilt, ["dirtyChunks"] = view.DirtyChunks, ["topologyChunks"] = view.TopologyChunks,
                    ["dirtyTiles"] = view.DirtyTiles, ["tilesScanned"] = view.TilesScanned, ["cellsScanned"] = view.CellsScanned,
                    ["cellsRead"] = view.CellsRead, ["ageTicks"] = view.AgeTicks, ["retainedBytes"] = view.RetainedBytes,
                };
                if (view.Resync != null) entry["resync"] = view.Resync;
                report["planningView"] = entry;
            }
            if (hop.Sections.Count > 0)
            {
                var sections = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var section in hop.Sections)
                {
                    var entry = new Dictionary<string, object?>(StringComparer.Ordinal) { ["ms"] = Ms(section.Ticks), ["rows"] = section.Rows };
                    if (section.Candidates > 0) entry["candidates"] = section.Candidates;
                    sections[section.Name] = entry;
                }
                report["sections"] = sections;
            }
            return report;
        }

        internal static double Ms(long stopwatchTicks)
            => stopwatchTicks <= 0 ? 0.0 : Math.Round(stopwatchTicks * 1000.0 / Stopwatch.Frequency, 3);
    }

    /// The game's update-to-update intervals and the observation work that
    /// ran inside them (#642), cumulative for the loaded game session like
    /// ClockProbeAccounting. ObservationFrameHook calls Update once per Unity
    /// update, from a prefix on the TickManagerUpdate boundary the game
    /// drives every update; everything here is bounded: four
    /// declared slow thresholds and a ring of the widest intervals the
    /// session saw, with no per-sample storage, no file or network I/O and no
    /// per-pawn record.
    ///
    /// These are update-to-update wall times on a monotonic clock, not GPU
    /// presentation intervals: a dropped or stalled present is not
    /// distinguishable here, and an unrendered batch-mode launch still
    /// updates. The recorder times itself (RecorderMs) so its own cost is
    /// reported rather than assumed negligible.
    internal static class FrameAccounting
    {
        /// The declared slow-update thresholds in milliseconds; an interval
        /// counts against every threshold it exceeds. 16.7 and 33.3 ms are
        /// 60 and 30 updates per second.
        internal static readonly double[] Thresholds = { 16.7, 33.3, 100.0, 250.0 };

        /// The widest intervals kept for the session.
        private const int WorstKept = 8;

        private static readonly object Gate = new object();
        private static object? _session;
        private static bool _hooked;
        private static long _last;
        private static ulong _updates;
        private static long _elapsedTicks, _observationTicks, _recorderTicks;
        private static double _maxIntervalMs;
        private static ulong _observations, _cancelled;
        private static readonly ulong[] Slow = new ulong[4];

        // The open interval: work charged to it and the most expensive
        // observation's trace, for the worst ring.
        private static long _openObservationTicks;
        private static string? _openTrace;
        private static long _openTraceTicks;
        private static int _openTick;

        private sealed class Worst
        {
            internal ulong Frame; internal double IntervalMs, ObservationMs; internal int Tick; internal string? Trace;
        }
        private static readonly List<Worst> WorstFrames = new List<Worst>();

        // The memoized timing block, rebuilt at most once per update: hops
        // are far rarer than updates, so consecutive hops in one update share
        // one immutable dictionary instead of allocating per reply.
        private static Dictionary<string, object?>? _report;
        private static ulong _reportUpdates = ulong.MaxValue;

        /// A new game session resets everything: the reload between two
        /// staged runs is nobody's frame.
        internal static void Started(object? session)
        {
            lock (Gate)
            {
                if (ReferenceEquals(session, _session)) return;
                _session = session; _last = 0; _updates = 0; _elapsedTicks = 0; _observationTicks = 0; _recorderTicks = 0;
                _maxIntervalMs = 0; _observations = 0; _cancelled = 0;
                for (var i = 0; i < Slow.Length; i++) Slow[i] = 0;
                _openObservationTicks = 0; _openTrace = null; _openTraceTicks = 0; _openTick = 0;
                WorstFrames.Clear();
                _report = null; _reportUpdates = ulong.MaxValue;
            }
        }

        /// One update of the named session ran at game tick tick. The
        /// interval it closes is the wall since the previous update.
        internal static void Update(object? session, int tick)
            => UpdateAt(session, tick, Stopwatch.GetTimestamp());

        /// Update with the monotonic timestamp supplied, so a probe can drive
        /// exact intervals; production passes Stopwatch.GetTimestamp().
        internal static void UpdateAt(object? session, int tick, long now)
        {
            // The recorder times itself on its own clock, so a supplied
            // interval timestamp cannot distort its reported overhead.
            var entered = Stopwatch.GetTimestamp();
            Started(session);
            lock (Gate)
            {
                _hooked = true;
                if (_last != 0)
                {
                    var ticks = now - _last;
                    if (ticks < 0) ticks = 0;
                    _updates++;
                    _elapsedTicks += ticks;
                    var ms = ObservationWork.Ms(ticks);
                    if (ms > _maxIntervalMs) _maxIntervalMs = ms;
                    for (var i = 0; i < Thresholds.Length; i++) if (ms > Thresholds[i]) Slow[i]++;
                    Keep(_updates, ms, ObservationWork.Ms(_openObservationTicks), _openTick, _openTrace);
                }
                _last = now;
                _openObservationTicks = 0; _openTrace = null; _openTraceTicks = 0; _openTick = tick;
                _recorderTicks += Math.Max(0, Stopwatch.GetTimestamp() - entered);
            }
        }

        /// The id of the interval now open, which a hop running on the game
        /// thread belongs to.
        internal static ulong OpenFrame() { lock (Gate) return _updates + 1; }

        /// One main-thread observation hop took stopwatchTicks under the open
        /// interval; trace names it for the worst ring.
        internal static void Observed(long stopwatchTicks, string? trace)
        {
            var began = Stopwatch.GetTimestamp();
            var ticks = Math.Max(0, stopwatchTicks);
            lock (Gate)
            {
                _observations++;
                _observationTicks += ticks;
                _openObservationTicks += ticks;
                if (ticks > _openTraceTicks) { _openTraceTicks = ticks; _openTrace = trace; }
                _recorderTicks += Math.Max(0, Stopwatch.GetTimestamp() - began);
            }
        }

        /// One hop the caller cancelled before the game thread ran it.
        internal static void Cancelled() { lock (Gate) _cancelled++; }

        /// Keeps the interval when it is among the widest of the session.
        private static void Keep(ulong frame, double intervalMs, double observationMs, int tick, string? trace)
        {
            if (WorstFrames.Count == WorstKept)
            {
                var least = 0;
                for (var i = 1; i < WorstFrames.Count; i++) if (WorstFrames[i].IntervalMs < WorstFrames[least].IntervalMs) least = i;
                if (WorstFrames[least].IntervalMs >= intervalMs) return;
                WorstFrames.RemoveAt(least);
            }
            WorstFrames.Add(new Worst { Frame = frame, IntervalMs = intervalMs, ObservationMs = observationMs, Tick = tick, Trace = trace });
        }

        /// The session's account as the timing block carries it, or null
        /// before any update was observed. The returned dictionary is shared
        /// and must not be mutated.
        internal static Dictionary<string, object?>? Report()
        {
            lock (Gate)
            {
                if (!_hooked) return null;
                if (_report != null && _reportUpdates == _updates) return _report;
                var slow = new List<object?>(Thresholds.Length);
                for (var i = 0; i < Thresholds.Length; i++)
                    slow.Add(new Dictionary<string, object?>(StringComparer.Ordinal) { ["thresholdMs"] = Thresholds[i], ["count"] = Slow[i] });
                var worst = new List<object?>(WorstFrames.Count);
                WorstFrames.Sort((a, b) => b.IntervalMs.CompareTo(a.IntervalMs));
                foreach (var frame in WorstFrames)
                {
                    var row = new Dictionary<string, object?>(StringComparer.Ordinal)
                    { ["update"] = frame.Frame, ["intervalMs"] = frame.IntervalMs, ["observationMs"] = frame.ObservationMs, ["tick"] = frame.Tick };
                    if (frame.Trace != null) row["trace"] = frame.Trace;
                    worst.Add(row);
                }
                _report = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["hooked"] = true,
                    ["updates"] = _updates,
                    ["elapsedMs"] = ObservationWork.Ms(_elapsedTicks),
                    ["maxUpdateMs"] = _maxIntervalMs,
                    ["observationMs"] = ObservationWork.Ms(_observationTicks),
                    ["observations"] = _observations,
                    ["cancelled"] = _cancelled,
                    ["recorderMs"] = ObservationWork.Ms(_recorderTicks),
                    ["slow"] = slow,
                    ["worst"] = worst,
                };
                _reportUpdates = _updates;
                return _report;
            }
        }

        /// Session totals for a probe: updates, the accounted interval wall
        /// and the observation work charged to them.
        internal static void Totals(out ulong updates, out double elapsedMs, out double observationMs, out ulong observations, out ulong cancelled)
        {
            lock (Gate)
            {
                updates = _updates; elapsedMs = ObservationWork.Ms(_elapsedTicks);
                observationMs = ObservationWork.Ms(_observationTicks); observations = _observations; cancelled = _cancelled;
            }
        }
    }
}
