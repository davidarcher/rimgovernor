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
    // A haul target's CAS token reuses NativeSupplyAllow's "allow-" domain
    // rather than a haul-specific one: observations_list_supplies (the only
    // read path that discovers loose haul targets, via
    // NativeSuppliesObservationTools) already emits that token for every
    // loose item, so validating against anything else would make every
    // legitimately observed target unusable here.
    internal sealed class NativeHaulRecord
    {
        private readonly Pawn pawn;
        private readonly Thing thing;
        private readonly NativeControlIdentity identity;
        private readonly Job job;
        private readonly int jobId;
        private readonly string trackingId;
        private readonly Common.ObservationContext admitted;
        internal NativeHaulRecord(NativeControlIdentity identity, Pawn pawn, Thing thing, Job job, string trackingId, Common.ObservationContext context)
        { this.identity = identity; this.pawn = pawn; this.thing = thing; this.job = job; jobId = job.loadID; this.trackingId = trackingId; admitted = context.Clone(); }

        // Issued describes only whether THIS call just issued a new job; the
        // pawn-order evidence contract requires Progress to always report
        // Issued=false (no observation call issues a job). JobId/JobDef
        // identify the one job that was issued at Execute, not a new one.
        internal Receipts.EffectEvidence Evidence(NativePawnSnapshot snapshot, bool issued, bool verified) => new Receipts.EffectEvidence
        {
            Job = new Receipts.JobEffect
            {
                PawnId = snapshot.PawnId, JobId = jobId, JobDef = job.def?.defName ?? "",
                TargetA = new Receipts.JobTarget { ThingId = thing.GetUniqueLoadID() },
                Issued = issued, Verified = verified,
                VerifiedReason = verified ? "Exact issued native haul job and quantity ledger observed." : "Issued job outcome requires observation.",
                Drafted = false, ResultingSnapshotToken = snapshot.Token,
            }
        };

        internal Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = false };
            try
            {
                if (!context.Identity.Equals(admitted.Identity) || context.Tick < admitted.Tick
                    || NativePawnControlState.Observe(identity, pawn, out var snapshot) != NativePawnControlResult.Ready || snapshot == null)
                    throw new InvalidOperationException("Current haul pawn context cannot be inspected.");
                var record = HaulTracking.Lookup(trackingId);
                if (record == null)
                {
                    result.Unknown = new Receipts.UnknownEffect { Reason = "The exact haul tracking record is no longer observable; absence does not prove delivery." };
                }
                else if (record.Complete)
                {
                    result.CompleteInspection = true;
                    result.Completed = new Receipts.CompletedEffect { Evidence = Evidence(snapshot, false, true) };
                }
                else if (record.Blocker != null)
                {
                    result.CompleteInspection = true;
                    result.Unsuccessful = new Receipts.UnsuccessfulEffect
                    { Reason = Receipts.UnsuccessfulReason.Interrupted, Evidence = Evidence(snapshot, false, false), Detail = record.Blocker };
                }
                else
                {
                    result.CompleteInspection = true;
                    result.Pending = new Receipts.PendingEffect { Evidence = Evidence(snapshot, false, true) };
                }
            }
            catch (Exception error) { result.CompleteInspection = false; result.Unknown = new Receipts.UnknownEffect { Reason = "Haul inspection unavailable: " + error.GetType().Name }; }
            return result;
        }
    }

    // Undrafted pawn-target order for PAWN_ORDER_KIND_HAUL. Unlike drafted
    // movement/combat, haul has no claim-ownership gate (Owns()): only the
    // generic native authority/lease check applies. The native storage
    // search (via the real Hauling WorkGiver) picks the destination; this
    // adapter issues exactly the job a player's "Prioritize hauling" click
    // would produce, then tracks exact-quantity delivery through the
    // existing HaulTracking ledger rather than declaring completion at
    // job-issuance time.
    internal static class NativeHaulOperations
    {
        internal static bool Valid(Operations.PawnTargetOrder? command) => command != null
            && command.HasKind && command.Kind == Operations.PawnOrderKind.Haul
            && NativeDraftProtocol.ValidEntity(command.Pawn) && NativeDraftProtocol.ValidEntity(command.Target)
            && command.Pawn.EntityId != command.Target.EntityId
            && command.HasRequireSafeStorage && command.RequireSafeStorage;

        internal static bool Eligible(Thing? thing) => thing != null && !thing.Destroyed && thing.Spawned
            && ProtoBoundary.IsLoaded(thing.Map) && thing.def.EverHaulable && thing.def.category == ThingCategory.Item
            && !thing.Position.Fogged(thing.Map);

        private static bool Recheck(NativeControlIdentity identity, Pawn pawn, Operations.PawnTargetOrder command, Thing thing,
            Common.ObservationContext context, out NativePawnSnapshot? snapshot)
        {
            if (NativePawnControlState.Check(identity, pawn, command.Pawn.ExpectedSnapshotToken, out snapshot) != NativePawnControlResult.Ready
                || snapshot == null || snapshot.Drafted || !snapshot.Eligible)
                return false;
            return Eligible(thing) && NativeSupplyAllow.Snapshot(thing, context)?.Token == command.Target.ExpectedSnapshotToken;
        }

        private static bool Prepare(Operations.PawnTargetOrder command, Common.ObservationContext context, out NativeControlIdentity identity,
            out Pawn? pawn, out Thing? thing, out NativePawnSnapshot? snapshot, out Common.Failure failure)
        {
            identity = new NativeControlIdentity(Current.Game, ProtoBoundary.LoadedMap(context), context.Identity.ColonyId, context.Identity.LoadToken);
            pawn = null; thing = null; snapshot = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Live native pawn control hooks are required.");
            if (!NativePawnControlState.IsReady) return false;
            pawn = ProtoBoundary.LoadedMap(context).mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == command.Pawn.EntityId);
            if (pawn == null) { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact pawn is not spawned on this map."); return false; }
            var check = NativePawnControlState.Check(identity, pawn, command.Pawn.ExpectedSnapshotToken, out snapshot);
            if (check != NativePawnControlResult.Ready) { failure = NativeDraftProtocol.Failure(check, context); return false; }
            if (snapshot!.Drafted || !snapshot.Eligible)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Haul requires an eligible undrafted pawn."); return false; }
            thing = ProtoBoundary.LoadedMap(context).listerThings.AllThings.SingleOrDefault(t => t.GetUniqueLoadID() == command.Target.EntityId);
            if (thing == null || !Eligible(thing)) { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact haulable thing is unavailable."); return false; }
            if (NativeSupplyAllow.Snapshot(thing, context)?.Token != command.Target.ExpectedSnapshotToken)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Haul target snapshot changed; observe before new admission."); return false; }
            return true;
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            var command = request.Operation.PawnTargetOrder; var pre = request.Precondition;
            if (!Valid(command))
                return Refuse(Common.FailureCode.InvalidRequest, "Haul requires an exact undrafted pawn, exact haulable target and require_safe_storage.");
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            try
            {
                if (!Prepare(command, context, out var identity, out var pawn, out var thing, out var snapshot, out var failure))
                    return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return Refuse(Common.FailureCode.AuthorityRequired, "Current native authority is required.");
                var guard = authority.Check(pre.ExpectedGeneration);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                // Resolve a real native job before reserving an attempt; no effect has run.
                var result = WorkGiverDispatch.TryJob(pawn!, thing!, def => def.workType == WorkTypeDefOf.Hauling, out _);
                if (result == null) return Refuse(Common.FailureCode.NativeFailure, "No native hauling job is available for this pawn and target.");
                guard = authority.Check(pre.ExpectedGeneration);
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                if (!Recheck(identity, pawn!, command, thing!, context, out snapshot))
                    return Refuse(Common.FailureCode.OwnerConflict, "Pawn or target snapshot changed before admission.");
                var admission = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admission.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admission.DecidedReply;
                handle = admission.AdmittedHandle;
                bool accepted = false; Exception? effectError = null;
                using (authority.Owned())
                {
                    if (!NativePawnControlState.IsReady || !Recheck(identity, pawn!, command, thing!, context, out snapshot))
                        throw new InvalidOperationException("Haul prerequisites changed after admission.");
                    guard = authority.Check(pre.ExpectedGeneration);
                    if (!guard.Success) throw new InvalidOperationException("Haul authority changed before native effect.");
                    var trackingId = HaulTracking.Begin(thing!, pawn!);
                    if (trackingId == null) throw new InvalidOperationException("Native haul quantity tracking is unavailable.");
                    var record = new NativeHaulRecord(identity, pawn!, thing!, result.Job, trackingId, context);
                    state.Hauls.Add(pre.Attempt.Clone(), record);
                    try { accepted = pawn!.jobs.TryTakeOrderedJobPrioritizedWork(result.Job, result.Scanner, thing!.Position); }
                    catch (Exception error) { effectError = error; }
                    if (NativePawnControlState.Observe(identity, pawn!, out snapshot) != NativePawnControlResult.Ready || snapshot == null)
                        throw new InvalidOperationException("Native haul readback unavailable.");
                    var current = pawn!.CurJob;
                    bool correlated = accepted && current != null && ReferenceEquals(current, result.Job);
                    // Accept reflects verified readback, matching OrderTool.Apply's legacy
                    // haul dispatch: the exact-quantity ledger only starts tracking once the
                    // issued job is confirmed running, not merely accepted by TryTakeOrderedJob.
                    HaulTracking.Accept(trackingId, correlated);
                    evidence = record.Evidence(snapshot, accepted, correlated);
                    if (effectError != null || !accepted || !correlated) throw new InvalidOperationException("Native haul requires causal observation.", effectError);
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null
                    ? Refuse(Common.FailureCode.NativeFailure, "Haul validation failed: " + error.GetType().Name)
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence, "Admitted haul requires observation: " + error.GetType().Name) };
            }
        }

        internal static Operations.PreviewReply Preview(Operations.PawnTargetOrder command, Common.ObservationContext context)
        {
            if (!Valid(command))
                return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Haul requires an exact undrafted pawn, exact haulable target and require_safe_storage.") };
            try
            {
                if (!Prepare(command, context, out _, out var pawn, out var thing, out var snapshot, out var failure))
                    return new Operations.PreviewReply { Failure = failure };
                var result = WorkGiverDispatch.TryJob(pawn!, thing!, def => def.workType == WorkTypeDefOf.Hauling, out var reason);
                var accepted = result != null;
                var jobDef = accepted ? (result!.Job.def?.defName ?? "HaulToCell") : "HaulToCell";
                return NativeOperationEnvelope.Preview(new Operations.PreviewReply
                {
                    Evaluated = new Operations.PreviewEvaluation
                    {
                        Context = context.Clone(), Accepted = accepted,
                        Reason = accepted ? "Exact native hauling WorkGiver produced a job for this target." : "No native hauling WorkGiver would produce a job for this pawn and target" + (string.IsNullOrEmpty(reason) ? "." : ": " + reason),
                        Projected = new Receipts.EffectEvidence
                        {
                            Job = new Receipts.JobEffect
                            {
                                PawnId = snapshot!.PawnId, JobDef = jobDef, CanTry = accepted, Issued = false, Verified = false,
                                TargetA = new Receipts.JobTarget { ThingId = thing!.GetUniqueLoadID() },
                            }
                        }
                    }
                });
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Haul preview failed: " + error.GetType().Name) }; }
        }

        private static Operations.ExecuteReply Refuse(Common.FailureCode code, string detail) => new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(code, detail) };
    }
}
