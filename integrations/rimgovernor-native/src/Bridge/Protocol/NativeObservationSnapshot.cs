using System;
using System.IO;

namespace RimGovernor.Native
{
    // The observation row's CAS snapshot: a NativeSnapshotVersions token over
    // exactly the fields the row's current read exposes.
    internal static class NativeObservationSnapshot
    {
        // `write` must serialize exactly the fields this row's current read exposes.
        internal static RimGovernor.Brain.GameFacts.Observations.SnapshotRef Snapshot(string prefix, RimGovernor.Brain.GameFacts.Common.ObservationContext context, string entityId, Action<BinaryWriter> write)
            => new RimGovernor.Brain.GameFacts.Observations.SnapshotRef { Context = context, EntityId = entityId,
                Token = NativeSnapshotVersions.Token(prefix, context.Identity, entityId, write) };
    }
}
