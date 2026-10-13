#nullable enable
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    internal static class NativePawnControlObservation
    {
        internal static Obs.PawnState Apply(Pawn pawn, Obs.PawnState row, Common.ObservationContext context)
        {
            var reason = Common.UnavailableReason.NativeComponentMissing;
            var detail = "Native pawn control snapshot is unavailable.";
            if (!pawn.Spawned || pawn.Map != ProtoBoundary.ResolveMap(context))
            {
                reason = Common.UnavailableReason.NotApplicable;
                detail = "Pawn is not spawned on the current map.";
            }
            else
            {
                var identity = new NativeControlIdentity(Current.Game, ProtoBoundary.LoadedMap(context), (context.Identity?.ColonyId ?? ""), (context.Identity?.LoadToken ?? ""));
                var result = NativePawnControlState.Observe(identity, pawn, out var snapshot);
                if (result == NativePawnControlResult.Ready && snapshot != null)
                {
                    var reference = new Obs.SnapshotRef { Context = context, EntityId = snapshot.PawnId, Token = snapshot.Token };
                    row = row with { PawnSnapshot = reference };
                    return row;
                }
                detail += " " + result;
            }
            var unavailable = new Common.Unavailable { Reason = reason, Detail = detail };
            row = row with { Issues = [.. row.Issues, new Obs.ReadIssue { Field = "pawn.snapshot", Unavailable = unavailable }] };
            return row;
        }
    }
}
