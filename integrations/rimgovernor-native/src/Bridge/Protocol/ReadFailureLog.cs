#nullable enable
using System;
using System.Collections.Generic;
using RimGovernor.Host.Sdk;

namespace HomeBridge.BridgeTools
{
    // A native read that cannot produce its facts answers with the facts absent: the brain checks only
    // for the observed value and the wire carries no failure arm. This is where the reason is kept, so a
    // failure stays diagnosable: each distinct reason is logged once per read kind through ModLog (the
    // runtime log), however often the read repeats. The set is bounded; past the cap new reasons are dropped.
    internal static class ReadFailureLog
    {
        private const int MaxReasons = 512;
        private static readonly object Gate = new object();
        private static readonly HashSet<string> Seen = new HashSet<string>(StringComparer.Ordinal);

        internal static void Note(string readKind, string? reason)
        {
            reason ??= "(no detail)";
            lock (Gate)
            {
                if (Seen.Count >= MaxReasons || !Seen.Add(readKind + "\n" + reason)) return;
            }
            ModLog.Warn("observe", "read " + readKind + " gave no facts: " + reason, "observe.read:" + readKind + ":" + reason);
        }

        internal static void Note(string readKind, Exception error) => Note(readKind, error.GetType().Name + ": " + error.Message);
    }
}
