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
    // Ordinary melee damage applies. This is containment, never execution or custody.
    internal sealed class NativeSubdueRecord
    {
        private readonly NativeControlIdentity identity;
        private readonly Pawn pawn, target;
        private readonly int jobId;
        private readonly string claim;
        private readonly Common.ObservationContext admitted;
        private ulong? order;
        internal NativeSubdueRecord(NativeControlIdentity identity, Pawn pawn, Pawn target, Job job, NativePawnSnapshot before, Common.ObservationContext context)
        { this.identity = identity; this.pawn = pawn; this.target = target; jobId = job.loadID; claim = before.Claim!.ClaimId; admitted = context.Clone(); }
        private bool Current() => pawn.CurJob?.loadID == jobId && pawn.CurJob.def == JobDefOf.AttackMelee && pawn.CurJob.targetA.Thing == target;
        internal bool Capture(NativePawnSnapshot before, NativePawnSnapshot after)
        {
            if (before.Facts.OrderRevision == ulong.MaxValue || after.Facts.OrderRevision != before.Facts.OrderRevision + 1
                || after.Facts.DraftRevision != before.Facts.DraftRevision || after.Claim?.ClaimId != claim || !Current()) return false;
            order = after.Facts.OrderRevision; return true;
        }
        internal Receipts.EffectEvidence Evidence(NativePawnSnapshot snapshot, bool issued, bool verified) => new Receipts.EffectEvidence {
            Job = new Receipts.JobEffect { PawnId = pawn.GetUniqueLoadID(), JobId = jobId, JobDef = "AttackMelee",
                TargetA = new Receipts.JobTarget { ThingId = target.GetUniqueLoadID() }, Issued = issued, Verified = verified,
                Drafted = snapshot.Drafted, DraftClaimId = claim, ResultingSnapshotToken = snapshot.Token,
                VerifiedReason = "Owned subdue job; completion requires a living downed target or an ended aggressive break." }
        };
        internal Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone() };
            if (!context.Identity.Equals(admitted.Identity) || context.Tick < admitted.Tick || !order.HasValue
                || NativePawnControlState.Observe(identity, pawn, out var snapshot) != NativePawnControlResult.Ready || snapshot == null)
            { result.Unknown = new Receipts.UnknownEffect { Reason = "Subdue context or correlated order unavailable." }; return result; }
            result.CompleteInspection = true;
            var evidence = Evidence(snapshot, false, true);
            if (snapshot.Claim?.ClaimId != claim || snapshot.Facts.OrderRevision != order)
                result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.Interrupted, Evidence = evidence, Detail = "Subdue ownership or order changed." };
            else if (context.Tick > admitted.Tick && !target.Dead && target.Spawned && target.Map == identity.Map && (target.Downed || !target.InAggroMentalState))
                result.Completed = new Receipts.CompletedEffect { Evidence = evidence };
            else if (!target.Dead && Current()) result.Pending = new Receipts.PendingEffect { Evidence = evidence };
            else result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved, Evidence = evidence, Detail = "Subdue ended without living containment." };
            return result;
        }
    }

    internal static class NativeSubdueOperations
    {
        internal static bool Valid(Operations.PawnTargetOrder? command) => command != null && command.HasKind && command.Kind == Operations.PawnOrderKind.Subdue
            && NativeDraftProtocol.ValidEntity(command.Pawn) && NativeDraftProtocol.ValidEntity(command.Target)
            && command.Pawn.EntityId != command.Target.EntityId && command.HasRequireSafeStorage && !command.RequireSafeStorage;
        private static bool Prepare(Operations.PawnTargetOrder command, Common.ObservationContext context, out NativeControlIdentity identity,
            out Pawn? pawn, out Pawn? target, out NativePawnSnapshot? snapshot, out Common.Failure failure)
        {
            identity = new NativeControlIdentity(Current.Game, ProtoBoundary.LoadedMap(context), context.Identity.ColonyId, context.Identity.LoadToken);
            pawn = null; target = null; snapshot = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Subdue requires exact pawn snapshots and require_safe_storage:false.");
            if (!Valid(command)) return false;
            if (!NativePawnControlState.IsReady) { failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Pawn control hooks unavailable."); return false; }
            pawn = identity.Map.mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == command.Pawn.EntityId);
            target = identity.Map.mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == command.Target.EntityId);
            if (pawn == null || target == null) { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Subdue pawns are not spawned."); return false; }
            var check = NativePawnControlState.Check(identity, pawn, command.Pawn.ExpectedSnapshotToken, out snapshot);
            if (check != NativePawnControlResult.Ready) { failure = NativeDraftProtocol.Failure(check, context); return false; }
            check = NativePawnControlState.Check(identity, target, command.Target.ExpectedSnapshotToken, out _);
            if (check != NativePawnControlResult.Ready) { failure = NativeDraftProtocol.Failure(check, context); return false; }
            string? reason = null;
            if (!snapshot!.Eligible || !pawn.IsColonistPlayerControlled || pawn.WorkTagIsDisabled(WorkTags.Violent)) reason = "incapable colonist";
            else if (snapshot.Drafted && snapshot.Claim == null) reason = "unowned draft";
            else if (pawn.equipment?.Primary != null && !pawn.equipment.Primary.def.IsMeleeWeapon) reason = "ranged weapons are refused";
            else if (target.Faction != Faction.OfPlayer || !target.RaceProps.Humanlike || target.IsPrisonerOfColony || target.Dead || target.Downed || !target.InAggroMentalState) reason = "target must be a standing aggressive colonist";
            else if (!pawn.CanReach(target, PathEndMode.Touch, Danger.Deadly)) reason = "target is unreachable";
            if (reason != null) { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Subdue refused: " + reason); return false; }
            return true;
        }
        internal static Operations.PreviewReply Preview(Operations.PawnTargetOrder command, Common.ObservationContext context)
        {
            if (!Prepare(command, context, out _, out _, out _, out _, out var failure)) return new Operations.PreviewReply { Failure = failure };
            return new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation { Context = context.Clone(), Accepted = true,
                Projected = new Receipts.EffectEvidence { Job = new Receipts.JobEffect { PawnId = command.Pawn.EntityId, JobDef = "AttackMelee",
                    TargetA = new Receipts.JobTarget { ThingId = command.Target.EntityId }, CanTry = true, Issued = false, Verified = false } } } };
        }
        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            var command = request.Operation.PawnTargetOrder; var pre = request.Precondition;
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            try {
                if (!Prepare(command, context, out var identity, out var pawn, out var target, out var before, out var failure)) return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null) return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority required.") };
                var guard = authority.Check(pre.ExpectedGeneration); context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admission = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admission.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admission.DecidedReply;
                handle = admission.AdmittedHandle;
                using (authority.Owned()) {
                    if (!authority.Check(pre.ExpectedGeneration).Success || !Prepare(command, context, out identity, out pawn, out target, out before, out _)) throw new InvalidOperationException("Subdue prerequisites changed.");
                    if (!before!.Drafted) {
                        if (NativePawnControlState.PrepareClaim(identity, pawn!, before.Token, out var ticket, out _) != NativePawnControlResult.Ready) throw new InvalidOperationException("Draft preparation changed.");
                        pawn!.drafter.Drafted = true;
                        if (NativePawnControlState.CompleteClaim(ticket!, out before) != NativePawnControlResult.Ready || before?.Claim == null) throw new InvalidOperationException("Draft claim unverified.");
                    }
                    var victim = target!;
                    var job = JobMaker.MakeJob(JobDefOf.AttackMelee, victim); job.killIncappedTarget = false;
                    var record = new NativeSubdueRecord(identity, pawn!, victim, job, before!, context);
                    state.Subdues.Add(pre.Attempt.Clone(), record);
                    bool accepted = pawn!.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                    if (accepted && pawn.CurJob == job) pawn.jobs.curDriver.AddEndCondition(() => victim.Dead || victim.Destroyed ? JobCondition.Incompletable
                        : victim.Downed || !victim.InAggroMentalState ? JobCondition.Succeeded : JobCondition.Ongoing);
                    if (NativePawnControlState.Observe(identity, pawn, out var after) != NativePawnControlResult.Ready || after == null) throw new InvalidOperationException("Subdue readback unavailable.");
                    bool correlated = record.Capture(before!, after); evidence = record.Evidence(after, accepted, correlated);
                    if (!accepted || !correlated) throw new InvalidOperationException("Subdue dispatch unverified.");
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence!) };
            } catch (Exception error) {
                return handle == null ? new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Subdue failed: " + error.GetType().Name) }
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence, "Subdue dispatch interrupted: " + error.GetType().Name) };
            }
        }
    }
}
