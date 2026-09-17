#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace HomeBridge.BridgeTools
{
    // One synchronous native factory/completion scope. Matching geometry never
    // substitutes for observing the exact factory result passed to Spawn.
    internal sealed class NativeConstructionCausality
    {
        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
        private readonly HashSet<object> created = new HashSet<object>(ReferenceComparer.Instance);
        private readonly HashSet<object> spawned = new HashSet<object>(ReferenceComparer.Instance);
        private bool replaced;
        internal void Created(object value) { if (value != null) created.Add(value); }
        internal void Spawned(object input, object? result)
        {
            if (input == null || !created.Contains(input)) return;
            if (!ReferenceEquals(input, result)) { replaced = true; return; }
            spawned.Add(result);
        }
        internal bool TryComplete(bool previousDestroyed, Exception? error, out object? successor)
        {
            successor = null;
            if (!previousDestroyed || error != null || replaced || created.Count != 1 || spawned.Count != 1) return false;
            foreach (var value in spawned) successor = value;
            return true;
        }
    }
}
