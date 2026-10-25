using RimGovernor.Host.Sdk;

using System;
using System.Collections.Generic;

namespace HomeBridge.BridgeTools
{
    /// Where one frame capture's game-thread time went: the capture spans the
    /// snapshot sections and their families declare, with the rows each
    /// produced. The snapshot stream opens a scope per capture and logs a slow
    /// capture's spans; the capture profile and the waste fixture read the
    /// same spans. Outside a scope every call is a no-op.
    ///
    /// The scope is thread-static: a capture runs to completion on the game
    /// thread, so no locking is needed.
    internal static class ObservationWork
    {
        /// Bounds the section list so a malformed shape cannot grow the
        /// account; a capture declares far fewer sections than this.
        internal const int MaxSections = 96;

        internal sealed class Section
        {
            internal readonly string Name;
            internal long Ticks;
            internal long Rows;
            // Bytes this thread allocated since the previous span of its kind closed (profiling only).
            internal long Bytes;
            // A span inside a Captured family (Detail), not a family.
            internal bool Detail;
            internal Section(string name) { Name = name; }
        }

        internal sealed class Hop
        {
            internal readonly List<Section> Sections = new List<Section>();
            internal long CaptureMark = GC.GetAllocatedBytesForCurrentThread(), AnyMark = GC.GetAllocatedBytesForCurrentThread();
        }

        [ThreadStatic] private static Hop? _current;

        /// Opens a scope for the capture about to run, replacing any scope a
        /// previous capture failed to close.
        internal static Hop BeginCapture()
        {
            var hop = new Hop();
            _current = hop;
            return hop;
        }

        /// Closes the scope and returns its spans, or null when no scope was
        /// open.
        internal static Hop? End()
        {
            var hop = _current;
            _current = null;
            return hop;
        }

        /// One capture span: stopwatchTicks of game-state reading for the
        /// named section, producing rows.
        internal static void Captured(string section, long stopwatchTicks, long rows = 0)
            => Record(section, stopwatchTicks, rows, false);

        /// A span inside a section already Captured, listed beside the
        /// families so a slow capture line breaks the family down. Inside a
        /// facade hop (the colony facts read on the game thread) the span is
        /// also the hop's part on the brain's hop_run (ProtoBoundary.HopPart).
        internal static void Detail(string section, long stopwatchTicks, long rows = 0)
        {
            ProtoBoundary.HopPart(section, Math.Max(0, stopwatchTicks));
            Record(section, stopwatchTicks, rows, true);
        }

        /// The named section threw and is missing from the frame. Logs an
        /// error row with the exception's stack, keyed per section so a
        /// sustained failure is rate-limited.
        internal static void Failed(string section, Exception ex)
            => ModLog.Error("observe", "snapshot section " + section + " failed: " + ex, "observe.section:" + section);

        private static void Record(string section, long stopwatchTicks, long rows, bool detail)
        {
            var hop = _current;
            if (hop == null) return;
            GameThreadTrace.Mark("snapshot capture after " + section);
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var bytes = allocated - (detail ? hop.AnyMark : hop.CaptureMark);
            hop.AnyMark = allocated;
            if (!detail) hop.CaptureMark = allocated;
            Section? entry = null;
            foreach (var known in hop.Sections) if (string.Equals(known.Name, section, StringComparison.Ordinal)) { entry = known; break; }
            if (entry == null)
            {
                if (hop.Sections.Count >= MaxSections) return;
                entry = new Section(section) { Detail = detail };
                hop.Sections.Add(entry);
            }
            entry.Ticks += Math.Max(0, stopwatchTicks);
            entry.Bytes += bytes;
            entry.Rows += Math.Max(0, rows);
        }
    }
}
