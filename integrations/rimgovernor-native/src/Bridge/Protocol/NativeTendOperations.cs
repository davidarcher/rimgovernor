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
    // Pawn-target order for PAWN_ORDER_KIND_TEND, the doctor dispatch of
    // MaintainMedicalCare and CriticalMedical (#217: the controller's tend
    // planner, boundary and receipts existed, but the adapter refused the
    // kind in preview and execute, so an injured colonist suspended every
    // routine goal for the rest of a startup run). Ports the legacy JSON
    // home/order tool's tend paths (OrderTool.PrepareTend): first
    // WorkGiver_Tend's own JobOnThing, which is what "Prioritize tending X"
    // issues for a patient in a bed and chooses the medicine; when that
    // yields nothing and the patient is down on the ground, the job the
    // drafted float menu builds (JobDefOf.TendPatient with draftedTend, the
    // best inventory medicine or none). The job is issued through
    // TryTakeOrderedJob like every other pawn-target order, and the
    // Work-tab priority is not consulted -- only a disabled Doctor work type
    // or a missing capacity refuses. Both pawns are checked against their
    // pawn-control snapshot tokens, the tokens every list_pawns row carries.
    internal sealed class NativeTendRecord
    {
        private readonly NativeControlIdentity identity;
        private readonly Pawn pawn;
        private readonly Pawn patient;
        private readonly string patientId;
        private readonly int jobId;
        private readonly string jobDef;
        private readonly Common.ObservationContext admitted;
        internal NativeTendRecord(NativeControlIdentity identity, Pawn pawn, Pawn patient, Job job, Common.ObservationContext context)
        { this.identity = identity; this.pawn = pawn; this.patient = patient; patientId = patient.GetUniqueLoadID(); jobId = job.loadID; jobDef = job.def?.defName ?? ""; admitted = context.Clone(); }

        internal Receipts.EffectEvidence Evidence(NativePawnSnapshot snapshot, bool issued, bool verified) => new Receipts.EffectEvidence
        {
            Job = new Receipts.JobEffect
            {
                PawnId = snapshot.PawnId, JobId = jobId, JobDef = jobDef,
                TargetA = new Receipts.JobTarget { ThingId = patientId },
                Issued = issued, Verified = verified,
                VerifiedReason = verified ? "Exact issued native tend job observed, or the patient no longer needs tending after it ran." : "Issued job outcome requires observation.",
                Drafted = pawn.Drafted, ResultingSnapshotToken = snapshot.Token,
            }
        };

        internal Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = false };
            try
            {
                if (!context.Identity.Equals(admitted.Identity) || context.Tick < admitted.Tick
                    || NativePawnControlState.Observe(identity, pawn, out var snapshot) != NativePawnControlResult.Ready || snapshot == null)
                    throw new InvalidOperationException("Current tend pawn context cannot be inspected.");
                result.CompleteInspection = true;
                bool current = pawn.CurJob != null && pawn.CurJob.loadID == jobId;
                if (patient.Dead || patient.Destroyed)
                    result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.Interrupted,
                        Evidence = Evidence(snapshot, false, false), Detail = "The patient died before the native tend job completed." };
                else if (!patient.health.HasHediffsNeedingTend())
                    result.Completed = new Receipts.CompletedEffect { Evidence = Evidence(snapshot, false, true) };
                else if (current)
                    result.Pending = new Receipts.PendingEffect { Evidence = Evidence(snapshot, false, true) };
                else
                    result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.Interrupted,
                        Evidence = Evidence(snapshot, false, false),
                        Detail = "Native tend job is no longer active and the patient still needs tending." };
            }
            catch (Exception error) { result.CompleteInspection = false; result.Unknown = new Receipts.UnknownEffect { Reason = "Tend inspection unavailable: " + error.GetType().Name }; }
            return result;
        }
    }

    internal static class NativeTendOperations
    {
        internal static bool Valid(Operations.PawnTargetOrder? command) => command != null
            && command.HasKind && command.Kind == Operations.PawnOrderKind.Tend
            && NativeDraftProtocol.ValidEntity(command.Pawn) && NativeDraftProtocol.ValidEntity(command.Target)
            && command.HasRequireSafeStorage && !command.RequireSafeStorage;

        private static WorkGiver_Tend? Giver() => DefDatabase<WorkGiverDef>.AllDefsListForReading
            .Where(d => d.giverClass != null && typeof(WorkGiver_Tend).IsAssignableFrom(d.giverClass))
            .Select(d => d.Worker as WorkGiver_Tend).FirstOrDefault(w => w != null);

        // Prepare names the first gate the order fails; the job it would
        // issue is built here so preview and execute agree on the path.
        private static bool Prepare(Operations.PawnTargetOrder command, Common.ObservationContext context, out NativeControlIdentity identity,
            out Pawn? pawn, out Pawn? patient, out Job? job, out NativePawnSnapshot? snapshot, out Common.Failure failure)
        {
            identity = new NativeControlIdentity(Current.Game, ProtoBoundary.LoadedMap(context), context.Identity.ColonyId, context.Identity.LoadToken);
            pawn = null; patient = null; job = null; snapshot = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Live native pawn control hooks are required.");
            if (!NativePawnControlState.IsReady) return false;
            var map = ProtoBoundary.LoadedMap(context);
            pawn = map.mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == command.Pawn.EntityId);
            if (pawn == null) { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact doctor pawn is not spawned on this map."); return false; }
            var check = NativePawnControlState.Check(identity, pawn, command.Pawn.ExpectedSnapshotToken, out snapshot);
            if (check != NativePawnControlResult.Ready) { failure = NativeDraftProtocol.Failure(check, context); return false; }
            if (!snapshot!.Eligible)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Tend requires an eligible doctor."); return false; }
            patient = map.mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == command.Target.EntityId);
            if (patient == null) { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact patient pawn is not spawned on this map."); return false; }
            var patientCheck = NativePawnControlState.Check(identity, patient, command.Target.ExpectedSnapshotToken, out _);
            if (patientCheck != NativePawnControlResult.Ready) { failure = NativeDraftProtocol.Failure(patientCheck, context); return false; }
            if (patient.Dead) { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The patient is dead and cannot be tended."); return false; }
            if (!patient.health.HasHediffsNeedingTend())
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The patient has nothing that needs tending right now."); return false; }
            if (ReferenceEquals(pawn, patient) && !(pawn.playerSettings != null && pawn.playerSettings.selfTend))
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A pawn may only tend itself when self-tend is on."); return false; }
            if (patient.InAggroMentalState)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The patient is in an aggressive mental state and cannot be tended until it ends."); return false; }
            if (pawn.WorkTypeIsDisabled(WorkTypeDefOf.Doctor))
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The doctor has the Doctor work type disabled."); return false; }
            var giver = Giver();
            if (giver == null)
            { failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "WorkGiver_Tend is unavailable in this game."); return false; }
            var missing = giver.MissingRequiredCapacity(pawn);
            if (missing != null)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The doctor is missing a capacity tending needs (" + missing.defName + ")."); return false; }
            if (!pawn.CanReach(patient, PathEndMode.ClosestTouch, Danger.Deadly))
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The doctor cannot reach the patient."); return false; }
            // The ordinary path: what "Prioritize tending X" issues, medicine
            // and bed chosen by the work giver. It refuses a patient on the
            // ground, which is the drafted float menu's job instead.
            if (giver.HasJobOnThing(pawn, patient, true)) job = giver.JobOnThing(pawn, patient, true);
            if (job == null)
            {
                if (patient.InBed())
                { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The patient is in a bed and WorkGiver_Tend makes no job for this doctor (already being tended, or the bed is reserved)."); return false; }
                if (!patient.Downed)
                { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The patient is up and not in a bed; WorkGiver_Tend makes no job and ground tending needs a downed patient."); return false; }
                var medicine = HealthAIUtility.FindBestMedicine(pawn, patient, onlyUseInventory: true);
                job = medicine == null ? JobMaker.MakeJob(JobDefOf.TendPatient, patient) : JobMaker.MakeJob(JobDefOf.TendPatient, patient, medicine);
                job.count = 1;
                job.draftedTend = true;
            }
            if (job.def != JobDefOf.TendPatient)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "WorkGiver_Tend produced a " + job.def.defName + " job rather than TendPatient."); return false; }
            return true;
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            var command = request.Operation.PawnTargetOrder; var pre = request.Precondition;
            if (!Valid(command))
                return Refuse(Common.FailureCode.InvalidRequest, "Tend requires an exact doctor, exact patient pawn and require_safe_storage:false.");
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            try
            {
                if (!Prepare(command, context, out var identity, out var pawn, out var patient, out var job, out var snapshot, out var failure))
                    return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return Refuse(Common.FailureCode.AuthorityRequired, "Current native authority is required.");
                var guard = authority.Check(pre.ExpectedGeneration);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admission = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admission.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admission.DecidedReply;
                handle = admission.AdmittedHandle;
                bool accepted = false; Exception? effectError = null;
                using (authority.Owned())
                {
                    if (!NativePawnControlState.IsReady || !Prepare(command, context, out identity, out pawn, out patient, out job, out snapshot, out failure))
                        throw new InvalidOperationException("Tend prerequisites changed after admission.");
                    guard = authority.Check(pre.ExpectedGeneration);
                    if (!guard.Success) throw new InvalidOperationException("Tend authority changed before native effect.");
                    var record = new NativeTendRecord(identity, pawn!, patient!, job!, context);
                    state.Tends.Add(pre.Attempt.Clone(), record);
                    try { accepted = pawn!.jobs.TryTakeOrderedJob(job!, JobTag.Misc); }
                    catch (Exception error) { effectError = error; }
                    if (NativePawnControlState.Observe(identity, pawn!, out snapshot) != NativePawnControlResult.Ready || snapshot == null)
                        throw new InvalidOperationException("Native tend readback unavailable.");
                    var current = pawn!.CurJob;
                    bool correlated = accepted && current != null && current.loadID == job!.loadID;
                    evidence = record.Evidence(snapshot, accepted, correlated);
                    if (effectError != null || !accepted || !correlated) throw new InvalidOperationException("Native tend order requires causal observation.", effectError);
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null
                    ? Refuse(Common.FailureCode.NativeFailure, "Tend validation failed: " + error.GetType().Name)
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence, "Admitted tend order requires observation: " + error.GetType().Name) };
            }
        }

        internal static Operations.PreviewReply Preview(Operations.PawnTargetOrder command, Common.ObservationContext context)
        {
            if (!Valid(command))
                return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Tend requires an exact doctor, exact patient pawn and require_safe_storage:false.") };
            try
            {
                if (!Prepare(command, context, out _, out var pawn, out var patient, out var job, out var snapshot, out var failure))
                    return new Operations.PreviewReply { Failure = failure };
                return NativeOperationEnvelope.Preview(new Operations.PreviewReply
                {
                    Evaluated = new Operations.PreviewEvaluation
                    {
                        Context = context.Clone(), Accepted = true,
                        Reason = job!.draftedTend
                            ? "Native tend gates pass; the patient is down on the ground, so the drafted float menu's TendPatient job is issued."
                            : "Native tend gates pass; WorkGiver_Tend makes a TendPatient job for this doctor and patient.",
                        Projected = new Receipts.EffectEvidence
                        {
                            Job = new Receipts.JobEffect
                            {
                                PawnId = snapshot!.PawnId, JobDef = JobDefOf.TendPatient.defName, CanTry = true, Issued = false, Verified = false,
                                TargetA = new Receipts.JobTarget { ThingId = patient!.GetUniqueLoadID() },
                                Drafted = pawn!.Drafted,
                            }
                        }
                    }
                });
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Tend preview failed: " + error.GetType().Name) }; }
        }

        private static Operations.ExecuteReply Refuse(Common.FailureCode code, string detail) => new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(code, detail) };
    }
}
