#nullable enable
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    internal static class NativePawnControlObservation
    {
        internal static void Apply(Pawn pawn, Obs.PawnState row, Common.ObservationContext context)
        {
            var reason = Common.UnavailableReason.NativeComponentMissing;
            var detail = "Native pawn control snapshot is unavailable.";
            if (!pawn.Spawned || pawn.Map != Find.CurrentMap)
            {
                reason = Common.UnavailableReason.NotApplicable;
                detail = "Pawn is not spawned on the current map.";
            }
            else
            {
                var identity = new NativeControlIdentity(Current.Game, Find.CurrentMap, context.Identity.ColonyId, context.Identity.LoadToken);
                var result = NativePawnControlState.Observe(identity, pawn, out var snapshot);
                if (result == NativePawnControlResult.Ready && snapshot != null)
                {
                    var reference = new Obs.SnapshotRef { Context = context.Clone(), EntityId = snapshot.PawnId, Token = snapshot.Token };
                    row.Pawn.Snapshot = reference;
                    row.DraftClaim = snapshot.Claim == null
                        ? new Obs.DraftClaimObservation { Unowned = new Obs.NoOwnedDraftClaim() }
                        : new Obs.DraftClaimObservation { Owned = new Obs.OwnedDraftClaim {
                            ClaimId = snapshot.Claim.ClaimId, Owner = snapshot.Claim.Owner, PawnSnapshot = reference.Clone() } };
                    return;
                }
                detail += " " + result;
            }
            var unavailable = new Common.Unavailable { Reason = reason, Detail = detail };
            row.Issues.Add(new Obs.ReadIssue { Field = "pawn.snapshot", Unavailable = unavailable });
            row.DraftClaim = new Obs.DraftClaimObservation { Unavailable = unavailable.Clone() };
        }
    }
}
