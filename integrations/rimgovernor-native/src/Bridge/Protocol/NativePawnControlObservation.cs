#nullable enable
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    internal static class NativePawnControlObservation
    {
        // The pawn control CAS token; null when native admits no pawn order:
        // the pawn is not spawned on the current map, or its control state is not ready.
        internal static Obs.SnapshotRef? Read(Pawn pawn, Common.ObservationContext context)
        {
            if (!pawn.Spawned || pawn.Map != ProtoBoundary.ResolveMap(context)) return null;
            var identity = new NativeControlIdentity(Current.Game, ProtoBoundary.LoadedMap(context), (context.Identity?.ColonyId ?? ""), (context.Identity?.LoadToken ?? ""));
            var result = NativePawnControlState.Observe(identity, pawn, out var snapshot);
            return result == NativePawnControlResult.Ready && snapshot != null
                ? new Obs.SnapshotRef { Context = context, EntityId = snapshot.PawnId, Token = snapshot.Token }
                : null;
        }
    }
}
