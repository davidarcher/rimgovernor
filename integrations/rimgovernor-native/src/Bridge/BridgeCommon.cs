#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using Verse;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// Helpers shared by the bridge tools. Nothing here knows about a specific
    /// tool: it is the floor the tool files stand on.
    /// </summary>
    internal static class BridgeCommon
    {
        // ------------------------------------------------------------------
        // Reply pieces
        // ------------------------------------------------------------------

        /// <summary>A cell as {x, z}. The only position shape any tool emits.</summary>
        internal static Dictionary<string, object?> Pos(IntVec3 cell)
        {
            return new Dictionary<string, object?> { { "x", cell.x }, { "z", cell.z } };
        }

        // ------------------------------------------------------------------
        // Reflection into RimWorld's private state
        // ------------------------------------------------------------------

        /// <summary>A private instance field, or null if the game renamed it.
        /// Callers report the null rather than emitting an empty result that
        /// would read as "nothing there".</summary>
        internal static FieldInfo? PrivateInstanceField(Type type, string name)
        {
            return type == null ? null : type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        }

        /// <summary>A private static field, same contract.</summary>
        internal static FieldInfo? PrivateStaticField(Type type, string name)
        {
            return type == null ? null : type.GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
        }


        // ------------------------------------------------------------------
        // Reading values back out of a payload dictionary
        // ------------------------------------------------------------------

        /// <summary>A double out of a payload dictionary; absent or another type
        /// reads as 0.</summary>
        internal static double Num(Dictionary<string, object?> d, string key)
        {
            object? v;
            if (d != null && d.TryGetValue(key, out v) && v is double)
                return (double)v;
            return 0.0;
        }
    }
}
