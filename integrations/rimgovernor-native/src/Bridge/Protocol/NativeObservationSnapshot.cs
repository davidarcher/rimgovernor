#nullable enable
using System;
using System.IO;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    // The observation row's CAS snapshot: a NativeSnapshotVersions token over
    // exactly the fields the row's current read exposes.
    internal static class NativeObservationSnapshot
    {
        // `write` must serialize exactly the fields this row's current read exposes.
        internal static Obs.SnapshotRef Snapshot(string prefix, Common.ObservationContext context, string entityId, Action<BinaryWriter> write)
            => new Obs.SnapshotRef { Context = context, EntityId = entityId,
                Token = NativeSnapshotVersions.Token(prefix, context.Identity, entityId, write) };
    }
}
