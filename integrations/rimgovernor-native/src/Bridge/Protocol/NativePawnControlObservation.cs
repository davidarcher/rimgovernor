using Verse;

namespace RimGovernor.Native
{
    internal static class NativePawnControlObservation
    {
        // The pawn control CAS token; null when native admits no pawn order:
        // the pawn is not spawned on the current map, or its control state is not ready.
        internal static RimGovernor.Brain.GameFacts.Observations.SnapshotRef? Read(Pawn pawn, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            if (!pawn.Spawned || pawn.Map != GameThread.ResolveMap(context)) return null;
            var identity = new NativeControlIdentity(Current.Game, GameThread.LoadedMap(context), (context.Identity?.ColonyId ?? ""), (context.Identity?.LoadToken ?? ""));
            var result = NativePawnControlState.Observe(identity, pawn, out var snapshot);
            return result == NativePawnControlResult.Ready && snapshot != null
                ? new RimGovernor.Brain.GameFacts.Observations.SnapshotRef { Context = context, EntityId = snapshot.PawnId, Token = snapshot.Token }
                : null;
        }
    }
}
