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

                try
                {
                    var assembly = AppDomain.CurrentDomain
                        .GetAssemblies()
                        .FirstOrDefault(a =>
                        {
                            try { return string.Equals(a.GetName().Name, "RimBridgeServer", StringComparison.Ordinal); }
                            catch { return false; }
                        });
                    if (assembly == null)
                        return null;

                    var type = assembly.GetType("RimBridgeServer.RimBridgeCapabilities", false);
                    if (type == null)
                        return null;

                    _journalProperty = type.GetProperty("Journal", BindingFlags.Public | BindingFlags.Static);
                }
                catch
                {
                    _journalProperty = null;
                }

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
        // Guards
        // ------------------------------------------------------------------

        /// <summary>
        /// Swallow-and-return-default. Every game read in this companion is
        /// behind one: an exception thrown inside a companion read reaches
        /// Verse.Log.Error, which calls TickManager.Pause(), which the harness
        /// reads as a person pausing the game.
        /// </summary>
        internal static T Try<T>(Func<T> read, T fallback)
        {
            try { return read(); }
            catch { return fallback; }
        }

        /// <summary>Same guard, but a throwing read becomes null rather than a
        /// value that cannot be told apart from a real one.</summary>
        internal static T? TryN<T>(Func<T> read) where T : struct
        {
            try { return read(); }
            catch { return null; }
        }

        /// <summary>A string read that becomes null rather than throwing.</summary>
        internal static string? SafeString(Func<string?> read)
        {
            try { return read(); }
            catch { return null; }
        }

        // ------------------------------------------------------------------
        // Reflection into RimWorld's private state
        // ------------------------------------------------------------------

        /// <summary>A private instance field, or null if the game renamed it.
        /// Callers report the null rather than emitting an empty result that
        /// would read as "nothing there".</summary>
        internal static FieldInfo? PrivateInstanceField(Type type, string name)
        {
            try { return type == null ? null : type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance); }
            catch { return null; }
        }

        /// <summary>A private static field, same contract.</summary>
        internal static FieldInfo? PrivateStaticField(Type type, string name)
        {
            try { return type == null ? null : type.GetField(name, BindingFlags.NonPublic | BindingFlags.Static); }
            catch { return null; }
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

        // ------------------------------------------------------------------
        // Construction deficit — ONE definition, two tools
        // ------------------------------------------------------------------

        /// <summary>
        /// How much of <paramref name="def"/> this blueprint or frame is still
        /// waiting to be delivered. <c>IConstructible.ThingCountNeeded</c> is
        /// implemented by both <c>Blueprint</c> and <c>Frame</c> (and so by
        /// <c>Blueprint_Install</c> and <c>Blueprint_Storage</c>, which subclass
        /// them), which is why one call covers every construction site.
        ///
        /// <see cref="MaterialBudget"/> sums it map-wide; this is its one copy of
        /// the arithmetic.
        ///
        /// A throwing read falls back to the full <paramref name="need"/> — the
        /// pessimistic answer, never a confident zero.
        /// </summary>
        internal static int ConstructibleStillNeeded(RimWorld.IConstructible? constructible, ThingDef def, int need)
        {
            var stillNeeded = need;
            try
            {
                if (constructible != null && def != null && !IsInstallBlueprint(constructible))
                    stillNeeded = constructible.ThingCountNeeded(def);
            }
            catch { stillNeeded = need; }
            return stillNeeded < 0 ? 0 : stillNeeded;
        }

        /// <summary>
        /// Every resource every blueprint and frame on the map is still waiting
        /// for, summed by def, from the same calls as
        /// <see cref="ConstructibleStillNeeded"/>.
        ///
        /// <c>Blueprint</c> and <c>Frame</c> are NOT in
        /// <c>ThingRequestGroup.BuildingArtificial</c>; they have their own
        /// groups, <c>Blueprint</c> and <c>BuildingFrame</c>. Asking only for the
        /// first would silently miss half the sites.
        ///
        /// One pass over two lister groups. The caller does this ONCE per call,
        /// never per rotation.
        /// </summary>
        /// <summary>
        /// A reinstall blueprint, whose material cost MUST NOT be asked for.
        ///
        /// <c>Blueprint_Install.TotalMaterialCost()</c> is, in full,
        /// <c>Log.Error("Called MaterialsNeededTotal on a Blueprint_Install."); return new List&lt;&gt;();</c>
        /// — and <c>Verse.Log.Error</c> calls <c>TickManager.Pause()</c>. So
        /// asking a reinstall blueprint what it needs PAUSES THE COLONY, which
        /// the harness reads as a person pressing space. Vanilla guards the same
        /// way: <c>GenConstruct.CanGetResources_NewTemp</c> opens with
        /// <c>if (thing is Blueprint_Install) return true;</c>.
        ///
        /// A reinstall costs nothing anyway — it moves a minified thing that
        /// already exists — so skipping it loses no information.
        /// </summary>
        internal static bool IsInstallBlueprint(object? constructible)
        {
            try { return constructible is RimWorld.Blueprint_Install; }
            catch { return false; }
        }
    }
}
