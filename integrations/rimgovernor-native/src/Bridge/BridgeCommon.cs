#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using RimBridgeServer.Sdk;
using Verse;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// Helpers shared by the bridge tools. Nothing here knows about a specific
    /// tool: it is the floor the tool files stand on.
    ///
    /// The one non-obvious member is <see cref="RawArguments"/>. The SDK binder
    /// (RimBridgeServer.AnnotatedExtensionCapabilityProvider.BindArguments) hands
    /// a tool method only the parameters it declares, by name, and drops every
    /// other key the caller sent without a word. IRimBridgeContext carries no
    /// argument dictionary, so the raw keys are unreachable through the SDK's
    /// public surface. They ARE reachable one step to the side: the capability
    /// registry writes the whole caller dictionary into the operation journal as
    /// metadata["arguments"] BEFORE it invokes the handler, and the journal is a
    /// process-wide singleton. Reading it back by the operation id in
    /// IRimBridgeContext.OperationId is how a tool learns what it was actually
    /// sent. Every step is reflection into another mod's internals, so every step
    /// is guarded and a failure is reported rather than defaulted.
    /// </summary>
    internal static class BridgeCommon
    {

        // ------------------------------------------------------------------
        // Unknown-argument detection
        // ------------------------------------------------------------------

        private static readonly object CacheGate = new object();

        private static PropertyInfo? _journalProperty;

        /// <summary>
        /// The caller's raw argument dictionary for the operation currently
        /// running, or null with a reason. Pure reflection: the companion does
        /// not reference RimBridgeServer.dll or RimBridgeServer.Core.dll, and
        /// RimBridgeCapabilities is internal to the mod assembly.
        /// Chain: RimBridgeServer.RimBridgeCapabilities.Journal (public static
        /// on an internal type) -> OperationJournal.GetOperation(id, false) ->
        /// OperationEnvelope.Metadata["arguments"].
        /// </summary>
        internal static IDictionary<string, object?>? RawArguments(IRimBridgeContext ctx, out string? unavailable)
        {
            unavailable = null;

            string? operationId;
            try
            {
                operationId = ctx == null ? null : ctx.OperationId;
            }
            catch (Exception ex)
            {
                unavailable = "the invocation context could not be read (" + ex.GetType().Name + ")";
                return null;
            }

            if (operationId == null || operationId.Length == 0)
            {
                unavailable = "this invocation carries no operation id";
                return null;
            }

            try
            {
                var journalProperty = JournalProperty();
                if (journalProperty == null)
                {
                    unavailable = "RimBridgeServer.RimBridgeCapabilities.Journal was not found in the loaded bridge assembly";
                    return null;
                }

                var journal = journalProperty.GetValue(null, null);
                if (journal == null)
                {
                    unavailable = "the bridge operation journal is not initialised";
                    return null;
                }

                var getOperation = journal.GetType().GetMethod(
                    "GetOperation",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(string), typeof(bool) },
                    null);
                if (getOperation == null)
                {
                    unavailable = "OperationJournal.GetOperation(string, bool) was not found";
                    return null;
                }

                var envelope = getOperation.Invoke(journal, new object[] { operationId, false });
                if (envelope == null)
                {
                    unavailable = "operation " + operationId + " was not in the journal";
                    return null;
                }

                var metadataProperty = envelope.GetType().GetProperty("Metadata", BindingFlags.Public | BindingFlags.Instance);
                var metadata = metadataProperty == null
                    ? null
                    : metadataProperty.GetValue(envelope, null) as IDictionary<string, object?>;
                if (metadata == null)
                {
                    unavailable = "the journalled operation carried no metadata dictionary";
                    return null;
                }

                object? raw;
                if (!metadata.TryGetValue("arguments", out raw))
                {
                    unavailable = "the journalled operation carried no arguments entry";
                    return null;
                }

                var arguments = raw as IDictionary<string, object?>;
                if (arguments == null)
                {
                    unavailable = "the journalled arguments were not a string-keyed dictionary";
                    return null;
                }

                return arguments;
            }
            catch (Exception ex)
            {
                unavailable = "reading the bridge operation journal threw " + ex.GetType().Name;
                return null;
            }
        }

        private static PropertyInfo? JournalProperty()
        {
            lock (CacheGate)
            {
                if (_journalProperty != null)
                    return _journalProperty;

                var assembly = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "RimBridgeServer", StringComparison.Ordinal));
                if (assembly == null)
                    return null;

                var type = assembly.GetType("RimBridgeServer.RimBridgeCapabilities", false);
                if (type == null)
                    return null;

                _journalProperty = type.GetProperty("Journal", BindingFlags.Public | BindingFlags.Static);
                return _journalProperty;
            }
        }

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
