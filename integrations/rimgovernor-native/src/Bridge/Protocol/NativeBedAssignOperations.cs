#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Authority = RimGovernor.Protocol.Authority;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // Typed dispatch for AssignBed: the same eligibility/CAS rules as the
    // legacy JSON home/upkeep_bed tool (UpkeepBedTool.cs), reusing the
    // pawn-state token NativePawnObservationTools.PawnSnapshotToken computes
    // for rimgovernor/observations_list_pawns and the generic building token
    // NativeBuildingObservationTools.Token computes for
    // rimgovernor/observations_list_buildings (bridge.ReadBedTarget already
    // treats a bed like any other repairable building for CAS purposes), so
    // a caller's previously observed tokens are exactly what this checks. A
    // synchronous CompAssignableToPawn.TryAssignPawn write, so unlike a
    // job-driven dispatch (e.g. NativeRecoveryOperations) the effect is
    // known immediately -- no later observation/progress polling is needed.
    // Tracks an admitted AssignBed attempt so a later
    // rimgovernor/receipts_observe_progress can re-check the live assignment;
    // the effect is synchronous and known at execute time, so unlike a
    // job-driven record (e.g. NativeRecoveryServiceRecord) this only needs to
    // re-read current ownership, not poll a pending job to completion.
    internal sealed class NativeBedAssignRecord
    {
        private readonly Pawn pawn;
        private readonly Building_Bed bed;
        private readonly Operations.AssignBed desired;
        private readonly Common.ObservationContext admitted;

        internal NativeBedAssignRecord(Pawn pawn, Building_Bed bed, Operations.AssignBed desired, Common.ObservationContext context)
        { this.pawn = pawn; this.bed = bed; this.desired = desired.Clone(); admitted = context.Clone(); }

        internal Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = true };
            if (pawn.Dead || !pawn.Spawned || pawn.Map != ProtoBoundary.ResolveMap(context) || pawn.ownership == null
                || bed.Destroyed || !bed.Spawned || bed.Map != ProtoBoundary.ResolveMap(context))
            {
                result.CompleteInspection = false;
                result.Unknown = new Receipts.UnknownEffect { Reason = "Bed assignment pawn or bed is no longer observable; absence does not prove completion." };
                return result;
            }
            var assigned = pawn.ownership.OwnedBed == bed;
            var evidence = NativeBedAssignOperations.Evidence(pawn, bed, desired, assigned);
            if (assigned)
                result.Completed = new Receipts.CompletedEffect { Evidence = new Receipts.EffectEvidence { Bed = evidence } };
            else
                result.Unsuccessful = new Receipts.UnsuccessfulEffect
                {
                    Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved,
                    Evidence = new Receipts.EffectEvidence { Bed = evidence },
                    Detail = "Pawn is no longer assigned to the admitted bed.",
                };
            return result;
        }
    }

    internal static class NativeBedAssignOperations
    {
        internal static bool Valid(Operations.AssignBed? command) => command != null
            && NativeDraftProtocol.ValidEntity(command.Pawn) && NativeDraftProtocol.ValidEntity(command.Bed)
            && command.Pawn.EntityId != command.Bed.EntityId
            && command.ExpectedPreviousBed != null
            && command.ExpectedPreviousBed.ValueCase != Operations.Assignment.ValueOneofCase.None
            && (command.ExpectedPreviousBed.ValueCase != Operations.Assignment.ValueOneofCase.EntityId
                || (ProtoBoundary.IsIdentifier(command.ExpectedPreviousBed.EntityId) && command.ExpectedPreviousBed.EntityId != command.Bed.EntityId));

        private static List<Pawn> Colonists(Map map) => map.mapPawns.AllPawnsSpawned.Where(p => !p.Dead && p.IsColonist && p.Spawned).ToList();

        // requireTokens is false for Preview's unconstrained-establish-baseline
        // role (mirrors NativeRecoveryOperations.Prepare's own split); Execute
        // always requires both tokens.
        private static bool Prepare(Operations.AssignBed command, Common.ObservationContext context, bool requireTokens,
            out Pawn? pawn, out Building_Bed? bed, out CompAssignableToPawn? assignable, out Common.Failure failure)
        {
            pawn = null; bed = null; assignable = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Bed assignment requires an exact pawn, exact empty bed and observed previous bed.");
            if (!Valid(command)) return false;
            var map = ProtoBoundary.ResolveMap(context);
            if (map == null || Find.TickManager.CurTimeSpeed != TimeSpeed.Paused)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Paused current map required."); return false; }
            pawn = map.mapPawns.FreeColonistsSpawned.SingleOrDefault(x => x.GetUniqueLoadID() == command.Pawn.EntityId);
            if (pawn == null || pawn.Dead || pawn.Downed || pawn.Drafted || pawn.InMentalState || pawn.ownership == null
                || pawn.CurJob?.playerForced == true || pawn.health.HasHediffsNeedingTend())
            { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Pawn unavailable or player work protected."); pawn = null; return false; }
            if (requireTokens)
            {
                var pawnRow = NativePawnObservationTools.Core(pawn, Colonists(map), context);
                var pawnToken = NativePawnObservationTools.PawnSnapshotToken(pawn, pawnRow, context);
                if (pawnToken.Token != command.Pawn.ExpectedSnapshotToken)
                { failure = ProtoBoundary.Fail(Common.FailureCode.StaleIdentity, "Pawn snapshot changed; observe before new admission."); pawn = null; return false; }
            }
            var previousID = pawn.ownership!.OwnedBed?.GetUniqueLoadID() ?? "";
            var expectPrevious = command.ExpectedPreviousBed.ValueCase == Operations.Assignment.ValueOneofCase.EntityId ? command.ExpectedPreviousBed.EntityId : "";
            if (previousID != expectPrevious)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Previous bed assignment changed; observe before recovery."); pawn = null; return false; }
            var thePawn = pawn;
            bed = map.listerThings.AllThings.OfType<Building_Bed>().SingleOrDefault(b => b.GetUniqueLoadID() == command.Bed.EntityId);
            if (bed == null || !bed.Spawned || bed.Faction != Faction.OfPlayerSilentFail
                || !bed.def.building.bed_humanlike || bed.Medical || bed.ForPrisoners
                || bed.OwnersForReading.Any() || bed.IsForbidden(pawn) || bed.IsBurning()
                || !bed.OccupiedRect().All(c => c.Roofed(map)
                    && (thePawn.playerSettings?.AreaRestrictionInPawnCurrentMap == null || thePawn.playerSettings.AreaRestrictionInPawnCurrentMap[c]))
                || !pawn.CanReach(bed, PathEndMode.OnCell, Danger.None)
                || bed.AmbientTemperature < pawn.GetStatValue(StatDefOf.ComfyTemperatureMin)
                || bed.AmbientTemperature > pawn.GetStatValue(StatDefOf.ComfyTemperatureMax))
            { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Bed unavailable, assigned, restricted or thermally unsafe."); pawn = null; bed = null; return false; }
            if (requireTokens)
            {
                var bedToken = NativeBuildingObservationTools.Token(bed, context);
                if (bedToken.Token != command.Bed.ExpectedSnapshotToken)
                { failure = ProtoBoundary.Fail(Common.FailureCode.StaleIdentity, "Bed snapshot changed; observe before new admission."); pawn = null; bed = null; return false; }
            }
            assignable = bed.GetComp<CompAssignableToPawn>();
            if (assignable == null || !assignable.AssigningCandidates.Contains(pawn) || !assignable.CanAssignTo(pawn).Accepted || assignable.IdeoligionForbids(pawn))
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Native bed assignment eligibility refused."); pawn = null; bed = null; assignable = null; return false; }
            return true;
        }

        internal static Receipts.BedEffect Evidence(Pawn pawn, Building_Bed bed, Operations.AssignBed command, bool assigned)
        {
            var effect = new Receipts.BedEffect { PawnId = pawn.GetUniqueLoadID(), BedId = bed.GetUniqueLoadID(), Assigned = assigned, Sleeping = pawn.CurrentBed() == bed };
            if (command.ExpectedPreviousBed.ValueCase == Operations.Assignment.ValueOneofCase.EntityId) effect.PreviousBedId = command.ExpectedPreviousBed.EntityId;
            return effect;
        }

        internal static Operations.PreviewReply Preview(Operations.AssignBed command, Common.ObservationContext context)
        {
            try
            {
                if (!Prepare(command, context, false, out var pawn, out var bed, out _, out var failure))
                    return new Operations.PreviewReply { Failure = failure };
                return new Operations.PreviewReply
                {
                    Evaluated = new Operations.PreviewEvaluation
                    {
                        Context = context.Clone(), Accepted = true,
                        Projected = new Receipts.EffectEvidence { Bed = Evidence(pawn!, bed!, command, true) }
                    }
                };
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Bed assignment preview failed: " + error.GetType().Name) }; }
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            var command = request.Operation.AssignBed; var pre = request.Precondition;
            if (!Prepare(command, context, true, out var pawn, out var bed, out var assignable, out var prepareFailure))
                return new Operations.ExecuteReply { Failure = prepareFailure };
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            try
            {
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority required.") };
                var guard = authority.Check(pre.ExpectedGeneration);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admitted = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admitted.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admitted.DecidedReply;
                handle = admitted.AdmittedHandle;
                using (authority.Owned())
                {
                    if (!authority.Check(pre.ExpectedGeneration).Success
                        || !Prepare(command, context, true, out pawn, out bed, out assignable, out var reprepareFailure))
                        throw new InvalidOperationException("Bed assignment scope changed before assignment.");
                    state.BedAssignments[pre.Attempt.Clone()] = new NativeBedAssignRecord(pawn!, bed!, command, context);
                    assignable!.TryAssignPawn(pawn!);
                    if (pawn!.ownership!.OwnedBed != bed) throw new InvalidOperationException("Native bed assignment did not take effect.");
                    evidence = new Receipts.EffectEvidence { Bed = Evidence(pawn, bed!, command, true) };
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null
                    ? new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Bed assignment failed: " + error.GetType().Name) }
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence, "Admitted bed assignment requires observation: " + error.GetType().Name) };
            }
        }
    }
}
