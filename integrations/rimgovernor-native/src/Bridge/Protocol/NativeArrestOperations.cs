#nullable enable
using System;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    internal sealed class NativeArrestRecord
    {
        private readonly NativeControlIdentity identity;
        private readonly Pawn pawn, target;
        private readonly Building_Bed bed;
        private readonly int jobId;
        private readonly string claimId;
        private readonly Common.ObservationContext admitted;
        private ulong? order;

        internal NativeArrestRecord(NativeControlIdentity identity, Pawn pawn, Pawn target, Building_Bed bed,
            Job job, NativePawnSnapshot before, Common.ObservationContext context)
        {
            this.identity = identity; this.pawn = pawn; this.target = target; this.bed = bed;
            jobId = job.loadID; claimId = before.Claim!.ClaimId; admitted = context.Clone();
        }

        internal bool Capture(NativePawnSnapshot before, NativePawnSnapshot after)
        {
            if (before.Facts.OrderRevision == ulong.MaxValue || after.Facts.OrderRevision != before.Facts.OrderRevision + 1
                || after.Facts.DraftRevision != before.Facts.DraftRevision || after.Claim?.ClaimId != claimId || !Current()) return false;
            order = after.Facts.OrderRevision;
            return true;
        }

        private bool Current() => pawn.CurJob?.loadID == jobId && pawn.CurJob.def == JobDefOf.Arrest
            && pawn.CurJob.targetA.Thing == target && pawn.CurJob.targetB.Thing == bed;

        internal Receipts.EffectEvidence Evidence(NativePawnSnapshot snapshot, bool issued, bool verified) => new Receipts.EffectEvidence {
            Job = new Receipts.JobEffect {
                PawnId = pawn.GetUniqueLoadID(), JobId = jobId, JobDef = "Arrest",
                TargetA = new Receipts.JobTarget { ThingId = target.GetUniqueLoadID() },
                TargetB = new Receipts.JobTarget { ThingId = bed.GetUniqueLoadID() },
                Issued = issued, Verified = verified, Drafted = snapshot.Drafted,
                DraftClaimId = claimId, ResultingSnapshotToken = snapshot.Token,
                VerifiedReason = "Arrest admission is an order; completion requires living custody in the exact prisoner bed after the mental state ends."
            }
        };

        internal Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone() };
            if (!context.Identity.Equals(admitted.Identity) || context.Tick < admitted.Tick
                || NativePawnControlState.Observe(identity, pawn, out var snapshot) != NativePawnControlResult.Ready || snapshot == null)
            { result.Unknown = new Receipts.UnknownEffect { Reason = "Arrest context or arrester cannot be inspected." }; return result; }
            if (!order.HasValue)
            { result.Unknown = new Receipts.UnknownEffect { Reason = "Arrest dispatch lacks correlated native order evidence." }; return result; }
            result.CompleteInspection = true;
            var evidence = Evidence(snapshot, false, true);
            // A later player/bot order cannot inherit this attempt's completion.
            if (snapshot.Claim?.ClaimId != claimId || snapshot.Facts.OrderRevision != order)
                result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.Interrupted,
                    Evidence = evidence, Detail = "Arrest draft ownership or order changed." };
            else if (context.Tick > admitted.Tick && !target.Dead && target.Spawned && target.Map == identity.Map
                && !target.InMentalState && target.IsPrisonerOfColony && bed.Spawned && bed.Map == identity.Map
                && bed.ForPrisoners && target.CurrentBed() == bed)
                result.Completed = new Receipts.CompletedEffect { Evidence = evidence };
            else if (!target.Dead && !bed.Destroyed && Current())
                result.Pending = new Receipts.PendingEffect { Evidence = evidence };
            else
                result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved,
                    Evidence = evidence, Detail = "Native arrest ended without living custody in the admitted bed; resistance or interruption is not completion." };
            return result;
        }
    }

    internal static class NativeArrestOperations
    {
        private static bool ValidEntity(Operations.EntityPrecondition? entity) => NativeDraftProtocol.ValidEntityId(entity)
            && (!entity!.HasExpectedSnapshotToken || ProtoBoundary.IsIdentifier(entity.ExpectedSnapshotToken));

        internal static bool Valid(Operations.Arrest? command) => command != null
            && ValidEntity(command.Pawn) && ValidEntity(command.Target) && ValidEntity(command.Bed)
            && command.Pawn.EntityId != command.Target.EntityId;

        private static bool Prepare(Operations.Arrest command, Common.ObservationContext context,
            out NativeControlIdentity identity, out Pawn? pawn, out Pawn? target, out Building_Bed? bed,
            out NativePawnSnapshot? snapshot, out Common.Failure failure)
        {
            identity = new NativeControlIdentity(Current.Game, ProtoBoundary.LoadedMap(context), context.Identity.ColonyId, context.Identity.LoadToken);
            pawn = null; target = null; bed = null; snapshot = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Arrest requires exact arrester, target and prisoner bed identities.");
            if (!Valid(command)) return false;
            if (!NativePawnControlState.IsReady)
            { failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Native pawn control hooks are required."); return false; }
            var map = identity.Map;
            pawn = map.mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == command.Pawn.EntityId);
            target = map.mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == command.Target.EntityId);
            bed = map.listerThings.AllThings.OfType<Building_Bed>().SingleOrDefault(b => b.GetUniqueLoadID() == command.Bed.EntityId);
            if (pawn == null || target == null || bed == null)
            { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact arrest pawn, target or bed is not spawned on this map."); return false; }
            string? reason = null;
            if (target.Dead || !target.InMentalState && !(target.Faction == Faction.OfAncients && !target.HostileTo(pawn) && !target.Downed && target.RaceProps.Humanlike && !target.IsPrisonerOfColony)) reason = "target is not living in a mental state or a standing neutral ancient";
            else if (pawn.equipment?.Primary == null || !pawn.equipment.Primary.def.IsWeapon || pawn.WorkTagIsDisabled(WorkTags.Violent))
                reason = "arrester is unarmed or incapable of violence";
            else if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)) reason = "arrester cannot manipulate";
            else if (!target.CanBeArrestedBy(pawn) || target.Downed && target.guilt.IsGuilty
                || target.InAggroMentalState && !target.health.hediffSet.HasHediff(HediffDefOf.Scaria) && target.HostileTo(pawn))
                reason = "native arrest eligibility refuses the target (hostile aggressive mental states cannot be arrested)";
            else if (pawn.InSameExtraFaction(target, ExtraFactionType.HomeFaction) || pawn.InSameExtraFaction(target, ExtraFactionType.MiniFaction))
                reason = "native same-extra-faction restriction";
            else if (!bed.ForPrisoners || bed.Faction != Faction.OfPlayerSilentFail
                || !RestUtility.IsValidBedFor(bed, target, pawn, false, guestStatus: GuestStatus.Prisoner))
                reason = "destination must be a usable native prisoner bed";
            else if (!pawn.CanReserveAndReach(target, PathEndMode.Touch, Danger.Deadly)) reason = "target cannot be reserved and reached";
            if (reason != null) { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Arrest refused: " + reason + "."); return false; }
            var check = NativePawnControlState.Observe(identity, pawn, out snapshot);
            if (check != NativePawnControlResult.Ready) { failure = NativeDraftProtocol.Failure(check, context); return false; }
            if (!NativeMovementOperations.Owns(snapshot!))
            { failure = ProtoBoundary.Fail(Common.FailureCode.OwnerConflict, "Arrest requires an eligible pawn with an owned draft claim."); return false; }
            if (command.Pawn.HasExpectedSnapshotToken && snapshot!.Token != command.Pawn.ExpectedSnapshotToken)
            { failure = ProtoBoundary.Fail(Common.FailureCode.StaleIdentity, "Arrester snapshot changed."); return false; }
            if (command.Target.HasExpectedSnapshotToken
                && NativePawnControlState.Check(identity, target, command.Target.ExpectedSnapshotToken, out _) != NativePawnControlResult.Ready)
            { failure = ProtoBoundary.Fail(Common.FailureCode.StaleIdentity, "Arrest target snapshot changed."); return false; }
            if (command.Bed.HasExpectedSnapshotToken && NativeBuildingObservationTools.Token(bed, context).Token != command.Bed.ExpectedSnapshotToken)
            { failure = ProtoBoundary.Fail(Common.FailureCode.StaleIdentity, "Prisoner bed snapshot changed."); return false; }
            return true;
        }

        internal static Operations.PreviewReply Preview(Operations.Arrest command, Common.ObservationContext context)
        {
            if (!Prepare(command, context, out _, out _, out _, out _, out _, out var failure))
                return new Operations.PreviewReply { Failure = failure };
            return new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation { Context = context.Clone(), Accepted = true } };
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            var pre = request.Precondition; var command = request.Operation.Arrest;
            try
            {
                if (!Prepare(command, context, out var identity, out var pawn, out var target, out var bed, out var before, out var failure))
                    return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority required.") };
                var guard = authority.Check(pre.ExpectedGeneration);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admission = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admission.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admission.DecidedReply;
                handle = admission.AdmittedHandle;
                using (authority.Owned())
                {
                    if (!authority.Check(pre.ExpectedGeneration).Success
                        || !Prepare(command, context, out identity, out pawn, out target, out bed, out before, out _))
                        throw new InvalidOperationException("Arrest prerequisites changed after admission.");
                    var job = JobMaker.MakeJob(JobDefOf.Arrest, target, bed); job.count = 1;
                    var record = new NativeArrestRecord(identity, pawn!, target!, bed!, job, before!, context);
                    state.Arrests.Add(pre.Attempt.Clone(), record);
                    bool accepted = pawn!.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                    if (NativePawnControlState.Observe(identity, pawn, out var after) != NativePawnControlResult.Ready || after == null)
                        throw new InvalidOperationException("Arrest readback unavailable.");
                    bool correlated = record.Capture(before!, after);
                    evidence = record.Evidence(after, accepted, correlated);
                    if (!accepted || !correlated) throw new InvalidOperationException("Arrest job requires causal observation.");
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null
                    ? new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Arrest failed: " + error.GetType().Name) }
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence, "Arrest dispatch interrupted: " + error.GetType().Name) };
            }
        }
    }
}
