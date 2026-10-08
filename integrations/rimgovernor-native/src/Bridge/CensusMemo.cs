#nullable enable
using System;
using System.Collections.Generic;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// Facts one gear census reads many times over (#1575): a policy's sorted
    /// def lists and signature, the outfit database's joined signatures and a
    /// pawn's identity are the same for every pawn and every call within one
    /// read. Open a scope around the read; outside one, Of just computes.
    /// Game thread only.
    /// </summary>
    internal sealed class CensusMemo : IDisposable
    {
        [ThreadStatic] private static CensusMemo? current;

        // The equality probe's switch (test/gear_memo_equality): a read with
        // it set computes every fact afresh.
        internal static bool Off;
        private readonly CensusMemo? previous;
        private readonly Dictionary<(string, object?), object> values = new Dictionary<(string, object?), object>();

        internal CensusMemo() { previous = current; current = this; }
        public void Dispose() { current = previous; }

        internal static T Of<T>((string, object?) key, Func<T> make) where T : class
        {
            var scope = current;
            if (scope == null || Off) return make();
            if (!scope.values.TryGetValue(key, out var value)) scope.values[key] = value = make();
            return (T)value;
        }
    }
}
