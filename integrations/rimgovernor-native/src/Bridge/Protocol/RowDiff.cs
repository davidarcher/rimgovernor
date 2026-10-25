using System;
using System.Collections.Generic;

namespace RimGovernor.Native
{
    /// <summary>
    /// The per-row compare of a keyed family: each row is kept per row id and
    /// compared by value with the previous step's. Step names the rows that
    /// differ from the previous step (or that are new) and the ids that left
    /// the family. Used by the combat pawn rows (CombatMirror), whose rows carry
    /// no observation context, so no tick needs clearing before the compare.
    /// </summary>
    internal sealed class RowDiff<T> where T : class, IEquatable<T>
    {
        internal sealed class Result
        {
            /// Ids of the rows that are new or whose content changed.
            internal readonly HashSet<string> Changed = new HashSet<string>(StringComparer.Ordinal);
            /// Ids held at the previous step and absent now.
            internal readonly List<string> Removed = new List<string>();
            internal bool Any => Changed.Count > 0 || Removed.Count > 0;
        }

        private Dictionary<string, T> held = new Dictionary<string, T>(StringComparer.Ordinal);

        /// Forgets every row: the next step reports all of them changed.
        internal void Reset() => held.Clear();

        /// Compares rows with the previous step's and keeps them.
        /// A duplicate id keeps its last row.
        internal Result Step(IEnumerable<KeyValuePair<string, T>> rows)
        {
            var result = new Result();
            var next = new Dictionary<string, T>(held.Count, StringComparer.Ordinal);
            foreach (var row in rows)
            {
                next[row.Key] = row.Value;
                if (!held.TryGetValue(row.Key, out var before) || !before.Equals(row.Value)) result.Changed.Add(row.Key);
                else result.Changed.Remove(row.Key);
            }
            foreach (var id in held.Keys)
                if (!next.ContainsKey(id)) result.Removed.Add(id);
            held = next;
            return result;
        }
    }
}
