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
    // Undrafted pawn-target order for PAWN_ORDER_KIND_CLEAN, the bounded
    // cleaning response of MaintainCleanFacilities (issue #6 slice 2). Ports
    // the legacy JSON home/order tool's (OrderTool.PrepareUpkeep) gates --
    // exact spawned filth inside the home area, an undrafted worker who can
    // reach it and needs no tending -- plus WorkGiver_CleanFilth's own
    // HasJobOnThing (reservable, thickened at least 600 ticks ago), then
    // issues one JobDefOf.Clean job whose target queue holds the exact filth
    // and nothing else. The work giver's JobOnThing would also sweep up to
    // fifteen neighbouring filth into the same job; the controller's order is
    // bounded to the one target its review admitted, so the job is built
    // here. Like the float menu, the Work-tab priority is not consulted: a
    // player-forced order overrides priorities, and only WorkTags/capacity
    // incapability refuses (FinishWorkGiverPlan's rationale). The filth
    // target's CAS token is NativeWasteOperations.Token, the hash every
    // observations_get_cells thing row carries, which is the only read path
    // (bridge.ReadFilthTarget) that refreshes a filth entity.
    internal sealed class NativeCleanRecord
    {
        private readonly NativeControlIdentity identity;
        private readonly Pawn pawn;
        private readonly Filth filth;
        private readonly string filthId;
        private readonly int jobId;
        private readonly string jobDef;
        private readonly Common.ObservationContext admitted;
        // Job instances return to JobMaker's pool (and are cleared) once they
        // end, so the id and def are captured at issue time; only the pawn's
        // current job is compared by loadID afterwards.
        internal NativeCleanRecord(NativeControlIdentity identity, Pawn pawn, Filth filth, Job job, Common.ObservationContext context)
        { this.identity = identity; this.pawn = pawn; this.filth = filth; filthId = filth.GetUniqueLoadID(); jobId = job.loadID; jobDef = job.def?.defName ?? ""; admitted = context.Clone(); }

        internal Receipts.EffectEvidence Evidence(NativePawnSnapshot snapshot, bool issued, bool verified) => new Receipts.EffectEvidence
        {
            Job = new Receipts.JobEffect
            {
                PawnId = snapshot.PawnId, JobId = jobId, JobDef = jobDef,
                TargetA = new Receipts.JobTarget { ThingId = filthId },
                Issued = issued, Verified = verified,
                VerifiedReason = verified ? "Exact issued native clean job observed, or the exact filth is gone after it ran." : "Issued job outcome requires observation.",
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
                    throw new InvalidOperationException("Current clean pawn context cannot be inspected.");
                result.CompleteInspection = true;
                bool current = pawn.CurJob != null && pawn.CurJob.loadID == jobId;
                if (filth.Destroyed || !filth.Spawned)
                    result.Completed = new Receipts.CompletedEffect { Evidence = Evidence(snapshot, false, true) };
                else if (current)
                    result.Pending = new Receipts.PendingEffect { Evidence = Evidence(snapshot, false, true) };
                else
                    result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.Interrupted,
                        Evidence = Evidence(snapshot, false, false),
                        Detail = "Native clean job is no longer active and the exact filth remains." };
            }
            catch (Exception error) { result.CompleteInspection = false; result.Unknown = new Receipts.UnknownEffect { Reason = "Clean inspection unavailable: " + error.GetType().Name }; }
            return result;
        }
    }

    internal static class NativeCleanOperations
    {
        internal static bool Valid(Operations.PawnTargetOrder? command) => command != null
            && command.HasKind && command.Kind == Operations.PawnOrderKind.Clean
            && NativeDraftProtocol.ValidEntity(command.Pawn) && NativeDraftProtocol.ValidEntity(command.Target)
            && command.Pawn.EntityId != command.Target.EntityId
            && command.HasRequireSafeStorage && !command.RequireSafeStorage;

        private static WorkGiver_CleanFilth? Giver() => DefDatabase<WorkGiverDef>.AllDefsListForReading
            .Where(d => d.giverClass != null && typeof(WorkGiver_CleanFilth).IsAssignableFrom(d.giverClass))
            .Select(d => d.Worker as WorkGiver_CleanFilth).FirstOrDefault(w => w != null);

        private static bool Prepare(Operations.PawnTargetOrder command, Common.ObservationContext context, out NativeControlIdentity identity,
            out Pawn? pawn, out Filth? filth, out NativePawnSnapshot? snapshot, out Common.Failure failure)
        {
            identity = new NativeControlIdentity(Current.Game, ProtoBoundary.ResolveMap(context), context.Identity.ColonyId, context.Identity.LoadToken);
            pawn = null; filth = null; snapshot = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Live native pawn control hooks are required.");
            if (!NativePawnControlState.IsReady) return false;
            var map = ProtoBoundary.ResolveMap(context);
            pawn = map.mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == command.Pawn.EntityId);
            if (pawn == null) { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact pawn is not spawned on this map."); return false; }
            var check = NativePawnControlState.Check(identity, pawn, command.Pawn.ExpectedSnapshotToken, out snapshot);
            if (check != NativePawnControlResult.Ready) { failure = NativeDraftProtocol.Failure(check, context); return false; }
            if (snapshot!.Drafted || !snapshot.Eligible)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Clean requires an eligible undrafted pawn."); return false; }
            filth = map.listerThings.AllThings.SingleOrDefault(t => t.GetUniqueLoadID() == command.Target.EntityId) as Filth;
            if (filth == null || filth.Destroyed || !filth.Spawned || filth.Map != map)
            { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact spawned filth is unavailable."); return false; }
            if (NativeWasteOperations.Token(context.Identity, filth) != command.Target.ExpectedSnapshotToken)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Filth snapshot changed; observe before new admission."); return false; }
            if (!map.areaManager.Home[filth.Position])
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Clean targets only filth inside the current home area."); return false; }
            if (pawn.health.HasHediffsNeedingTend())
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Pawn needs tending and cannot be ordered to clean."); return false; }
            var giver = Giver();
            var giverDef = giver?.def;
            if (giver == null || giverDef == null)
            { failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "WorkGiver_CleanFilth is unavailable in this game."); return false; }
            var missing = giver.MissingRequiredCapacity(pawn);
            if (missing != null)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Pawn is missing a capacity cleaning needs (" + missing.defName + ")."); return false; }
            if (pawn.WorkTagIsDisabled(giverDef.workTags) || (giverDef.workType != null && pawn.WorkTypeIsDisabled(giverDef.workType)))
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Pawn is incapable of cleaning work."); return false; }
            if (!pawn.CanReach(filth, PathEndMode.Touch, Danger.None))
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Pawn cannot safely reach the filth."); return false; }
            if (!giver.HasJobOnThing(pawn, filth, true))
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The native clean work giver refuses this filth for this pawn right now (reserved, or thickened within the last 600 ticks)."); return false; }
            return true;
        }

        private static Job MakeJob(Filth filth)
        {
            var job = JobMaker.MakeJob(JobDefOf.Clean);
            job.AddQueuedTarget(TargetIndex.A, filth);
            return job;
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            var command = request.Operation.PawnTargetOrder; var pre = request.Precondition;
            if (!Valid(command))
                return Refuse(Common.FailureCode.InvalidRequest, "Clean requires an exact pawn, exact filth target and require_safe_storage:false.");
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            try
            {
                if (!Prepare(command, context, out var identity, out var pawn, out var filth, out var snapshot, out var failure))
                    return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return Refuse(Common.FailureCode.AuthorityRequired, "Current native authority is required.");
                var guard = authority.Check(pre.ExpectedGeneration);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admission = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admission.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admission.Reply!;
                handle = admission.Handle!;
                bool accepted = false; Exception? effectError = null;
                using (authority.Owned())
                {
                    if (!NativePawnControlState.IsReady || !Prepare(command, context, out identity, out pawn, out filth, out snapshot, out failure))
                        throw new InvalidOperationException("Clean prerequisites changed after admission.");
                    guard = authority.Check(pre.ExpectedGeneration);
                    if (!guard.Success) throw new InvalidOperationException("Clean authority changed before native effect.");
                    var job = MakeJob(filth!);
                    var record = new NativeCleanRecord(identity, pawn!, filth!, job, context);
                    state.Cleans.Add(pre.Attempt.Clone(), record);
                    try { accepted = pawn!.jobs.TryTakeOrderedJob(job, JobTag.Misc); }
                    catch (Exception error) { effectError = error; }
                    if (NativePawnControlState.Observe(identity, pawn!, out snapshot) != NativePawnControlResult.Ready || snapshot == null)
                        throw new InvalidOperationException("Native clean readback unavailable.");
                    var current = pawn!.CurJob;
                    bool correlated = accepted && current != null && current.loadID == job.loadID;
                    evidence = record.Evidence(snapshot, accepted, correlated);
                    if (effectError != null || !accepted || !correlated) throw new InvalidOperationException("Native clean order requires causal observation.", effectError);
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null
                    ? Refuse(Common.FailureCode.NativeFailure, "Clean validation failed: " + error.GetType().Name)
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence!, "Admitted clean order requires observation: " + error.GetType().Name) };
            }
        }

        internal static Operations.PreviewReply Preview(Operations.PawnTargetOrder command, Common.ObservationContext context)
        {
            if (!Valid(command))
                return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Clean requires an exact pawn, exact filth target and require_safe_storage:false.") };
            try
            {
                if (!Prepare(command, context, out _, out var pawn, out var filth, out var snapshot, out var failure))
                    return new Operations.PreviewReply { Failure = failure };
                return NativeOperationEnvelope.Preview(new Operations.PreviewReply
                {
                    Evaluated = new Operations.PreviewEvaluation
                    {
                        Context = context.Clone(), Accepted = true,
                        Reason = "Native clean gates (home area, undrafted eligible pawn, reach, cleaning capability, WorkGiver_CleanFilth.HasJobOnThing) pass for this pawn and filth.",
                        Projected = new Receipts.EffectEvidence
                        {
                            Job = new Receipts.JobEffect
                            {
                                PawnId = snapshot!.PawnId, JobDef = JobDefOf.Clean.defName, CanTry = true, Issued = false, Verified = false,
                                TargetA = new Receipts.JobTarget { ThingId = filth!.GetUniqueLoadID() },
                            }
                        }
                    }
                });
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Clean preview failed: " + error.GetType().Name) }; }
        }

        private static Operations.ExecuteReply Refuse(Common.FailureCode code, string detail) => new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(code, detail) };
    }
}
