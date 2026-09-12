#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    // Stateless, deterministic per-row observation CAS tokens and list-page cursors.
    // No per-entity memory or Harmony hooks: a token/cursor is recomputed from the
    // exact fields the current read exposes, so it needs no invalidation path of
    // its own. See contracts/native-state-ownership.md and native-static-state.md.
    internal static class NativeObservationSnapshot
    {
        internal static string Hash(string prefix, Common.Identity identity, Action<BinaryWriter> write)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    writer.Write(identity.ColonyId); writer.Write(identity.LoadToken); writer.Write(identity.MapId);
                    write(writer);
                }
                using (var hash = SHA256.Create())
                    return prefix + "-" + BitConverter.ToString(hash.ComputeHash(stream.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }

        // Row-level CAS token: same shape as NativeWorkSettings.Token/NativeZoneCreation.MapSnapshot,
        // generalized. `write` must serialize exactly the fields this row's current read exposes.
        internal static Obs.SnapshotRef Snapshot(string prefix, Common.ObservationContext context, string entityId, Action<BinaryWriter> write)
            => new Obs.SnapshotRef { Context = context.Clone(), EntityId = entityId,
                Token = Hash(prefix, context.Identity, w => { w.Write(entityId); write(w); }) };

        // Opaque list-page cursor: binds the resumed page to identity + the caller's
        // effective filters (`querySeed`) so a stale/mismatched cursor fails closed
        // instead of silently resuming a different query. Payload is the last
        // returned row's stable ordering key.
        internal static class Cursor
        {
            internal static string Encode(Common.Identity identity, string querySeed, string lastKey)
            {
                using (var stream = new MemoryStream())
                {
                    using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                    {
                        writer.Write(FilterHash(identity, querySeed));
                        writer.Write(lastKey);
                    }
                    return Convert.ToBase64String(stream.ToArray());
                }
            }

            internal static bool TryDecode(Common.Identity identity, string querySeed, string cursor, out string lastKey)
            {
                lastKey = "";
                try
                {
                    var bytes = Convert.FromBase64String(cursor);
                    using (var stream = new MemoryStream(bytes))
                    using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
                    {
                        var expected = FilterHash(identity, querySeed);
                        var actual = reader.ReadBytes(expected.Length);
                        if (actual.Length != expected.Length) return false;
                        for (var i = 0; i < expected.Length; i++) if (actual[i] != expected[i]) return false;
                        lastKey = reader.ReadString();
                        return stream.Position == stream.Length;
                    }
                }
                catch (Exception) { return false; }
            }

            private static byte[] FilterHash(Common.Identity identity, string querySeed)
            {
                using (var stream = new MemoryStream())
                {
                    using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                    { writer.Write(identity.ColonyId); writer.Write(identity.LoadToken); writer.Write(identity.MapId); writer.Write(querySeed); }
                    using (var hash = SHA256.Create()) return hash.ComputeHash(stream.ToArray());
                }
            }
        }
    }
}
