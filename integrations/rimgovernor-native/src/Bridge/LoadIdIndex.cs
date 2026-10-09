#nullable enable

using System;
using System.Collections.Generic;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// A loadId to object map over one live source. A hit is trusted only while the entry
    /// still carries that id and is live in the source; a miss or a stale hit rebuilds once from
    /// the source, so a lookup never answers from an outdated view and never costs more than the
    /// linear scan it replaces. Main thread only. Verse-free so the contract probes exercise it.
    /// </summary>
    internal sealed class LoadIdIndex<T> where T : class
    {
        private readonly Func<IEnumerable<KeyValuePair<string, T>>> _source;
        private readonly Func<T, string?> _id;
        private readonly Func<T, bool> _live;
        private Dictionary<string, T>? _byId;

        /// <param name="source">Every (loadId, object) pair live now; the first pair for an id wins.</param>
        /// <param name="id">The object's current loadId.</param>
        /// <param name="live">Whether the object is still in the source.</param>
        internal LoadIdIndex(Func<IEnumerable<KeyValuePair<string, T>>> source, Func<T, string?> id, Func<T, bool> live)
        {
            _source = source;
            _id = id;
            _live = live;
        }

        internal int Rebuilds { get; private set; }

        internal T? Find(string? id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (_byId != null && _byId.TryGetValue(id!, out var hit) && Valid(hit, id!)) return hit;
            Rebuild();
            return _byId!.TryGetValue(id!, out var found) && Valid(found, id!) ? found : null;
        }

        private bool Valid(T value, string id) => _live(value) && _id(value) == id;

        private void Rebuild()
        {
            Rebuilds++;
            var byId = new Dictionary<string, T>(StringComparer.Ordinal);
            foreach (var pair in _source())
                if (pair.Key != null && pair.Value != null && !byId.ContainsKey(pair.Key)) byId[pair.Key] = pair.Value;
            _byId = byId;
        }
    }
}
