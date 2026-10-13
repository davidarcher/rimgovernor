#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    // Stateless, deterministic per-row observation CAS tokens.
    // No per-entity memory or Harmony hooks: a token is recomputed from the
    // exact fields the current read exposes, so it needs no invalidation path of
    // its own. See contracts/native-state-ownership.md and native-static-state.md.
    internal static class NativeObservationSnapshot
    {
        internal static string Hash(string prefix, Common.Identity? identity, Action<BinaryWriter> write)
        {
            identity ??= new Common.Identity();
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    writer.Write((identity.ColonyId ?? "")); writer.Write((identity.LoadToken ?? "")); writer.Write(identity.MapId.GetValueOrDefault());
                    write(writer);
                }
                using (var hash = SHA256.Create())
                    return prefix + "-" + BitConverter.ToString(hash.ComputeHash(stream.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }

        // Row-level CAS token. `write` must serialize exactly the fields this row's current read exposes.
        internal static Obs.SnapshotRef Snapshot(string prefix, Common.ObservationContext context, string entityId, Action<BinaryWriter> write)
            => new Obs.SnapshotRef { Context = context, EntityId = entityId,
                Token = Hash(prefix, context.Identity, w => { w.Write(entityId); write(w); }) };
    }
}
