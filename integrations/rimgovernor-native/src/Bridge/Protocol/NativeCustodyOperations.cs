#nullable enable
using System;
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
    // Undrafted-or-drafted pawn-target order for PAWN_ORDER_KIND_CAPTURE and
    // PAWN_ORDER_KIND_RESCUE. Both are single fixed vanilla jobs (Capture,
    // Rescue) that carry a downed patient to a bed -- a prisoner bed for
    // capture, an ordinary/guest bed for rescue -- so they share one CAS,
    // admission and job-dispatch shape here the way NativeHusbandryOperations
    // shares one class across SetAnimalTraining/SlaughterAnimal. The actual
    // eligibility mechanics (CanBeCaptured, HealthAIUtility.CanRescueNow,
    // RestUtility.FindBedFor, HostileTo) are ported field-for-field from
    // OrderTool.cs's PrepareCapture/PrepareRescue, which are not reachable
    // from the typed operations_execute/operations_preview dispatch.
    //
    // Unlike NativeHaulOperations, the patient here is a Pawn, not a loose
    // Thing: its CAS token is the same NativePawnControlState per-pawn
    // snapshot domain the acting pawn uses (observations_list_pawns is the
    // read path that discovers it, via NativeCombatObservationTools), not
    // NativeSupplyAllow's "allow-" domain.
    internal sealed class NativeCustodyRecord
    {
        private readonly Pawn pawn;
        private readonly Pawn patient;
        private readonly NativeControlIdentity identity;
        private readonly Job job;
        private readonly int jobId;
        private readonly bool capture;
        private readonly Common.ObservationContext admitted;
        internal NativeCustodyRecord(NativeControlIdentity identity, Pawn pawn, Pawn patient, Job job, bool capture, Common.ObservationContext context)
        { this.identity = identity; this.pawn = pawn; this.patient = patient; this.job = job; jobId = job.loadID; this.capture = capture; admitted = context.Clone(); }

        // Issued describes only whether THIS call just issued a new job; the
        // pawn-order evidence contract requires Progress to always report
        // Issued=false (no observation call issues a job).
        internal Receipts.EffectEvidence Evidence(NativePawnSnapshot snapshot, bool issued, bool verified) => new Receipts.EffectEvidence
        {
            Job = new Receipts.JobEffect
            {
                PawnId = snapshot.PawnId, JobId = jobId, JobDef = job.def?.defName ?? "",
                TargetA = new Receipts.JobTarget { ThingId = patient.GetUniqueLoadID() },
                Issued = issued, Verified = verified,
                VerifiedReason = verified
                    ? ("Exact issued native " + (capture ? "capture" : "rescue") + " job and quantity ledger observed.")
                    : "Issued job outcome requires observation.",
                Drafted = false, ResultingSnapshotToken = snapshot.Token,
            }
        };

        // Capture succeeds when the patient is an admitted colony prisoner;
        // rescue succeeds when the patient is carried into a bed. Neither can
        // be inferred from a completion event -- there is no native hook for
        // one here -- so this polls exact roster/status the way a live tick
        // loop would.
        private bool Succeeded() => !patient.Destroyed && patient.Spawned && patient.Map == Find.CurrentMap
            && (capture ? patient.IsPrisonerOfColony : patient.CurrentBed() != null);

        internal Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = false };
            try
            {
                if (!context.Identity.Equals(admitted.Identity) || context.Tick < admitted.Tick
                    || NativePawnControlState.Observe(identity, pawn, out var snapshot) != NativePawnControlResult.Ready || snapshot == null)
                    throw new InvalidOperationException("Current custody pawn context cannot be inspected.");
                result.CompleteInspection = true;
                if (Succeeded())
                {
                    result.Completed = new Receipts.CompletedEffect { Evidence = Evidence(snapshot, false, true) };
                }
                else
                {
                    var current = pawn.Destroyed || !pawn.Spawned ? null : pawn.CurJob;
                    bool stillRunning = current != null && current.loadID == jobId;
                    if (stillRunning)
                        result.Pending = new Receipts.PendingEffect { Evidence = Evidence(snapshot, false, true) };
                    else
                        result.Unsuccessful = new Receipts.UnsuccessfulEffect
                        {
                            Reason = Receipts.UnsuccessfulReason.Interrupted, Evidence = Evidence(snapshot, false, false),
                            Detail = patient.Destroyed || !patient.Spawned
                                ? "Patient is no longer observable on this map."
                                : "The issued custody job is no longer the pawn's current job and completion was not observed.",
                        };
                }
            }
            catch (Exception error) { result.CompleteInspection = false; result.Unknown = new Receipts.UnknownEffect { Reason = "Custody inspection unavailable: " + error.GetType().Name }; }
            return result;
        }
    }

    internal static class NativeCustodyOperations
    {
        internal static bool Valid(Operations.PawnTargetOrder? command) => command != null
            && command.HasKind && (command.Kind == Operations.PawnOrderKind.Capture || command.Kind == Operations.PawnOrderKind.Rescue)
            && NativeDraftProtocol.ValidEntity(command.Pawn) && NativeDraftProtocol.ValidEntity(command.Target)
            && command.Pawn.EntityId != command.Target.EntityId
            && command.HasRequireSafeStorage && !command.RequireSafeStorage;

        // Ports OrderTool.HostileToPlayer: manhunter mental state or a
        // hostile faction. Rescue refuses a hostile patient (vanilla offers
        // only capture for one); capture requires it.
        private static bool HostileToPlayer(Pawn patient)
        {
            try
            {
                var mental = patient.MentalState?.def?.defName;
                if (!string.IsNullOrEmpty(mental) && mental!.IndexOf("Manhunter", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                var faction = patient.Faction;
                if (faction == null) return false;
                var player = Faction.OfPlayerSilentFail;
                return player != null && faction != player && faction.HostileTo(player);
            }
            catch { return false; }
        }

        // Ports OrderTool.PrepareCapture's eligibility gate exactly.
        private static bool CaptureEligible(Pawn pawn, Pawn patient) => patient != null && !patient.Dead && patient.Spawned
            && patient.Map == Find.CurrentMap && patient.CanBeCaptured() && HealthAIUtility.CanRescueNow(pawn, patient, true)
            && pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) && patient.HostileTo(Faction.OfPlayerSilentFail);

        // Ports OrderTool.PrepareRescue's drafted-or-undrafted fallback path
        // (FloatMenuOptionProvider_RescuePawn), which needs no draft change
        // either way and is a complete, self-contained mechanism independent
        // of the WorkGiver-priority optimization PrepareRescue tries first.
        private static bool RescueEligible(Pawn pawn, Pawn patient) => patient != null && !patient.Dead && patient.Spawned
            && patient.Map == Find.CurrentMap && !ReferenceEquals(patient, pawn)
            && HealthAIUtility.CanRescueNow(pawn, patient, true) && !HostileToPlayer(patient);

        private static bool Eligible(Operations.PawnOrderKind kind, Pawn pawn, Pawn patient) =>
            kind == Operations.PawnOrderKind.Capture ? CaptureEligible(pawn, patient) : RescueEligible(pawn, patient);

        // Ports OrderTool.PrepareCapture/BuildRescueJob's bed search exactly.
        private static bool FindBed(Operations.PawnOrderKind kind, Pawn pawn, Pawn patient, out Building_Bed? bed)
        {
            bed = kind == Operations.PawnOrderKind.Capture
                ? RestUtility.FindBedFor(patient, pawn, false, false, GuestStatus.Prisoner)
                : RestUtility.FindBedFor(patient, pawn, checkSocialProperness: false)
                    ?? RestUtility.FindBedFor(patient, pawn, false, ignoreOtherReservations: true);
            return bed != null;
        }

        private static bool Recheck(NativeControlIdentity identity, Pawn pawn, Operations.PawnTargetOrder command, Pawn patient,
            Common.ObservationContext context, out NativePawnSnapshot? snapshot)
        {
            snapshot = null;
            if (NativePawnControlState.Check(identity, pawn, command.Pawn.ExpectedSnapshotToken, out snapshot) != NativePawnControlResult.Ready
                || snapshot == null || !snapshot.Eligible)
                return false;
            if (NativePawnControlState.Check(identity, patient, command.Target.ExpectedSnapshotToken, out _) != NativePawnControlResult.Ready)
                return false;
            return Eligible(command.Kind, pawn, patient);
        }

        private static bool Prepare(Operations.PawnTargetOrder command, Common.ObservationContext context, out NativeControlIdentity identity,
            out Pawn? pawn, out Pawn? patient, out Building_Bed? bed, out NativePawnSnapshot? snapshot, out Common.Failure failure)
        {
            identity = new NativeControlIdentity(Current.Game, Find.CurrentMap, context.Identity.ColonyId, context.Identity.LoadToken);
            pawn = null; patient = null; bed = null; snapshot = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Live native pawn control hooks are required.");
            if (!NativePawnControlState.IsReady) return false;
            pawn = Find.CurrentMap.mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == command.Pawn.EntityId);
            if (pawn == null) { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact pawn is not spawned on this map."); return false; }
            var check = NativePawnControlState.Check(identity, pawn, command.Pawn.ExpectedSnapshotToken, out snapshot);
            if (check != NativePawnControlResult.Ready) { failure = NativeDraftProtocol.Failure(check, context); return false; }
            if (!snapshot!.Eligible) { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Custody dispatch requires an eligible pawn."); return false; }
            patient = Find.CurrentMap.mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == command.Target.EntityId);
            if (patient == null) { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact patient pawn is not spawned on this map."); return false; }
            var patientCheck = NativePawnControlState.Check(identity, patient, command.Target.ExpectedSnapshotToken, out _);
            if (patientCheck != NativePawnControlResult.Ready) { failure = NativeDraftProtocol.Failure(patientCheck, context); return false; }
            if (!Eligible(command.Kind, pawn, patient))
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Native capture/rescue eligibility refused."); return false; }
            if (!FindBed(command.Kind, pawn, patient, out bed) || bed == null)
            { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "No native bed is available for this worker and patient."); return false; }
            if (!pawn.CanReserveAndReach(patient, PathEndMode.Touch, Danger.Deadly))
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Native target reservation or reachability refused for this worker."); return false; }
            return true;
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            var command = request.Operation.PawnTargetOrder; var pre = request.Precondition;
            if (!Valid(command))
                return Refuse(Common.FailureCode.InvalidRequest, "Capture/Rescue requires an exact pawn, exact patient pawn and require_safe_storage=false.");
            bool capture = command.Kind == Operations.PawnOrderKind.Capture;
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            try
            {
                if (!Prepare(command, context, out var identity, out var pawn, out var patient, out var bed, out var snapshot, out var failure))
                    return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return Refuse(Common.FailureCode.AuthorityRequired, "Current native authority is required.");
                var guard = authority.Check(pre.ExpectedGeneration);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                guard = authority.Check(pre.ExpectedGeneration);
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                if (!Recheck(identity, pawn!, command, patient!, context, out snapshot))
                    return Refuse(Common.FailureCode.OwnerConflict, "Pawn or patient snapshot changed before admission.");
                var admission = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admission.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admission.Reply!;
                handle = admission.Handle!;
                bool accepted = false; Exception? effectError = null;
                using (authority.Owned())
                {
                    if (!NativePawnControlState.IsReady || !Recheck(identity, pawn!, command, patient!, context, out snapshot)
                        || !FindBed(command.Kind, pawn!, patient!, out bed) || bed == null)
                        throw new InvalidOperationException("Custody prerequisites changed after admission.");
                    guard = authority.Check(pre.ExpectedGeneration);
                    if (!guard.Success) throw new InvalidOperationException("Custody authority changed before native effect.");
                    var jobDef = capture ? JobDefOf.Capture : JobDefOf.Rescue;
                    var job = JobMaker.MakeJob(jobDef, patient, bed);
                    job.count = 1;
                    var record = new NativeCustodyRecord(identity, pawn!, patient!, job, capture, context);
                    state.Custody.Add(pre.Attempt.Clone(), record);
                    try { accepted = pawn!.jobs.TryTakeOrderedJob(job, JobTag.Misc); }
                    catch (Exception error) { effectError = error; }
                    if (NativePawnControlState.Observe(identity, pawn!, out snapshot) != NativePawnControlResult.Ready || snapshot == null)
                        throw new InvalidOperationException("Native custody readback unavailable.");
                    var current = pawn!.CurJob;
                    bool correlated = accepted && current != null && ReferenceEquals(current, job);
                    evidence = record.Evidence(snapshot, accepted, correlated);
                    if (effectError != null || !accepted || !correlated) throw new InvalidOperationException("Native custody dispatch requires causal observation.", effectError);
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null
                    ? Refuse(Common.FailureCode.NativeFailure, "Custody validation failed: " + error.GetType().Name)
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence!, "Admitted custody order requires observation: " + error.GetType().Name) };
            }
        }

        internal static Operations.PreviewReply Preview(Operations.PawnTargetOrder command, Common.ObservationContext context)
        {
            if (!Valid(command))
                return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Capture/Rescue requires an exact pawn, exact patient pawn and require_safe_storage=false.") };
            try
            {
                if (!Prepare(command, context, out _, out var pawn, out var patient, out _, out var snapshot, out var failure))
                    return new Operations.PreviewReply { Failure = failure };
                var jobDef = command.Kind == Operations.PawnOrderKind.Capture ? "Capture" : "Rescue";
                return NativeOperationEnvelope.Preview(new Operations.PreviewReply
                {
                    Evaluated = new Operations.PreviewEvaluation
                    {
                        Context = context.Clone(), Accepted = true,
                        Reason = "Native " + jobDef + " eligibility, bed and reachability confirmed for this worker and patient.",
                        Projected = new Receipts.EffectEvidence
                        {
                            Job = new Receipts.JobEffect
                            {
                                PawnId = snapshot!.PawnId, JobDef = jobDef, CanTry = true, Issued = false, Verified = false,
                                TargetA = new Receipts.JobTarget { ThingId = patient!.GetUniqueLoadID() },
                            }
                        }
                    }
                });
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Custody preview failed: " + error.GetType().Name) }; }
        }

        private static Operations.ExecuteReply Refuse(Common.FailureCode code, string detail) => new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(code, detail) };
    }
}
