#nullable enable
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using RimGovernor.Contracts.Wire;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// The per-row compare of a keyed family: an 8-byte hash of each
    /// row's encoding, nested context ticks cleared, kept per row id. Step
    /// names the rows whose hash differs from the previous step (or that are
    /// new) and the ids that left the family. Used by the combat pawn rows (CombatMirror).
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
        internal static ulong Hash<T>(T row) where T : class
        {
            ulong h = 14695981039346656037UL;
            foreach (var b in Normalized(row)) { h ^= b; h *= 1099511628211UL; }
            return h;
        }

        /// Compares rows with the previous step's and keeps their hashes.
        /// A duplicate id keeps its last row.
        internal Result Step<T>(IEnumerable<KeyValuePair<string, T>> rows) where T : class
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

        // The section's JSON bytes with every nested context tick cleared: a
        // section whose content holds still encodes the same at every tick.
        // An ObservationContext is the one wire object that holds both an
        // identity and a tick.
        internal static byte[] Normalized<T>(T section) where T : class
        {
            var bytes = WireJson.SerializeToUtf8Bytes(section);
            using var document = JsonDocument.Parse(bytes);
            var output = new ArrayBufferWriter<byte>(bytes.Length);
            using (var writer = new Utf8JsonWriter(output)) WriteWithoutContextTicks(document.RootElement, writer);
            return output.WrittenSpan.ToArray();
        }

        private static void WriteWithoutContextTicks(JsonElement element, Utf8JsonWriter writer)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var context = element.TryGetProperty("identity", out _) && element.TryGetProperty("tick", out _);
                    writer.WriteStartObject();
                    foreach (var property in element.EnumerateObject())
                    {
                        if (context && property.NameEquals("tick")) continue;
                        writer.WritePropertyName(property.Name);
                        WriteWithoutContextTicks(property.Value, writer);
                    }
                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray()) WriteWithoutContextTicks(item, writer);
                    writer.WriteEndArray();
                    break;
                default:
                    element.WriteTo(writer);
                    break;
            }
        }
    }
}
