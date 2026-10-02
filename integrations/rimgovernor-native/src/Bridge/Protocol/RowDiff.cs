#nullable enable
using System.Collections.Generic;
using Google.Protobuf;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// The per-row compare of a keyed family (#1348): an 8-byte hash of each
    /// row's encoding, nested context ticks cleared, kept per row id. Step
    /// names the rows whose hash differs from the previous step (or that are
    /// new) and the ids that left the family. Shared by the keyed snapshot
    /// tables (SnapshotSections) and the combat pawn rows (CombatMirror).
    /// </summary>
    internal sealed class RowDiff
    {
        internal sealed class Result
        {
            /// Ids of the rows that are new or whose content changed.
            internal readonly HashSet<string> Changed = new HashSet<string>(System.StringComparer.Ordinal);
            /// Ids held at the previous step and absent now.
            internal readonly List<string> Removed = new List<string>();
            internal bool Any => Changed.Count > 0 || Removed.Count > 0;
        }

        private Dictionary<string, ulong> held = new Dictionary<string, ulong>(System.StringComparer.Ordinal);

        /// Forgets every row: the next step reports all of them changed.
        internal void Reset() => held.Clear();

        /// FNV-1a over the row's encoding with context ticks cleared.
        internal static ulong Hash<T>(T row) where T : class, IMessage<T>
        {
            ulong h = 14695981039346656037UL;
            foreach (var b in SnapshotSections.Normalized(row)) { h ^= b; h *= 1099511628211UL; }
            return h;
        }

        /// Compares rows with the previous step's and keeps their hashes.
        /// A duplicate id keeps its last row.
        internal Result Step<T>(IEnumerable<KeyValuePair<string, T>> rows) where T : class, IMessage<T>
        {
            var result = new Result();
            var next = new Dictionary<string, ulong>(held.Count, System.StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var hash = Hash(row.Value);
                next[row.Key] = hash;
                if (!held.TryGetValue(row.Key, out var before) || before != hash) result.Changed.Add(row.Key);
                else result.Changed.Remove(row.Key);
            }
            foreach (var id in held.Keys)
                if (!next.ContainsKey(id)) result.Removed.Add(id);
            held = next;
            return result;
        }
    }
}
