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
    // Typed dispatch for EnsureMood-* relief, porting the legacy JSON
    // NeedReliefTool.cs (home/relieve_need) behind the typed
    // Operations.RelieveNeed boundary. Never changes needs, thoughts,
    // timetables, restrictions, traits, ideology or mental states; only
    // offers one ordinary native food/rest/recreation job to an undrafted,
    // not-player-forced colonist.
    internal sealed class Recreation : JobGiver_GetJoy
    {
        protected override bool JoyGiverAllowed(JoyGiverDef def) => !(def.Worker is JoyGiver_Ingest);
    }

    internal sealed class NativeMoodReliefRecord
    {
        private readonly NativeControlIdentity identity;
        private readonly Pawn pawn;
        private readonly string jobDef;
        private readonly int jobId;
        private readonly Operations.Need need;
        private readonly Common.ObservationContext admitted;

        internal NativeMoodReliefRecord(NativeControlIdentity identity, Pawn pawn, Job job, Operations.Need need, Common.ObservationContext context)
        { this.identity = identity; this.pawn = pawn; jobId = job.loadID; jobDef = job.def?.defName ?? ""; this.need = need; admitted = context.Clone(); }

        internal Receipts.EffectEvidence Evidence(NativePawnSnapshot snapshot, bool issued, bool verified) => new Receipts.EffectEvidence
        {
            Job = new Receipts.JobEffect
            {
                PawnId = snapshot.PawnId, JobId = jobId, JobDef = jobDef,
                TargetA = new Receipts.JobTarget { ThingId = pawn.GetUniqueLoadID() },
                Issued = issued, Verified = verified,
                VerifiedReason = verified ? "Exact issued native need job observed." : "Issued job outcome requires observation.",
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
                    throw new InvalidOperationException("Current relief pawn context cannot be inspected.");
                result.CompleteInspection = true;
                var level = NativeMoodReliefOperations.NeedLevel(pawn, need);
                if (level != null && level.Value >= 0.5f)
                    result.Completed = new Receipts.CompletedEffect { Evidence = Evidence(snapshot, false, true) };
                else if (pawn.CurJob != null && pawn.CurJob.loadID == jobId)
                    result.Pending = new Receipts.PendingEffect { Evidence = Evidence(snapshot, false, true) };
                else
                    result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.Interrupted,
                        Evidence = Evidence(snapshot, false, false),
                        Detail = "Native need job is no longer active and the need is not yet recovered." };
            }
            catch (Exception error) { result.CompleteInspection = false; result.Unknown = new Receipts.UnknownEffect { Reason = "Relief inspection unavailable: " + error.GetType().Name }; }
            return result;
        }
    }

    internal static class NativeMoodReliefOperations
    {
        internal static bool Valid(Operations.RelieveNeed? command) => command != null
            && NativeDraftProtocol.ValidEntity(command.Pawn) && command.HasNeed && command.Need != Operations.Need.Unspecified
            && command.ExpectedJob != null && command.ExpectedJob.StateCase != Operations.ExpectedJob.StateOneofCase.None
            && command.HasExpectedScheduleDef && ProtoBoundary.IsIdentifier(command.ExpectedScheduleDef);

        internal static float? NeedLevel(Pawn p, Operations.Need need) => need switch
        {
            Operations.Need.Food => p.needs?.food?.CurLevelPercentage,
            Operations.Need.Rest => p.needs?.rest?.CurLevelPercentage,
            Operations.Need.Joy => p.needs?.joy?.CurLevelPercentage,
            _ => null,
        };

        private static ThinkNode_JobGiver? Giver(Operations.Need need) => need switch
        {
            Operations.Need.Food => new JobGiver_GetFood(),
            Operations.Need.Rest => new JobGiver_GetRest(),
            Operations.Need.Joy => new Recreation(),
            _ => null,
        };

        private static bool Prepare(Operations.RelieveNeed command, Common.ObservationContext context, out NativeControlIdentity identity,
            out Pawn? pawn, out ThinkNode_JobGiver? giver, out Common.Failure failure)
        {
            identity = new NativeControlIdentity(Current.Game, ProtoBoundary.LoadedMap(context), context.Identity.ColonyId, context.Identity.LoadToken);
            pawn = null; giver = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Live native pawn control hooks are required.");
            if (!NativePawnControlState.IsReady) return false;
            pawn = ProtoBoundary.LoadedMap(context).mapPawns.FreeColonistsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == command.Pawn.EntityId);
            if (pawn == null) { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact pawn is not spawned on this map."); return false; }
            var check = NativePawnControlState.Check(identity, pawn, command.Pawn.ExpectedSnapshotToken, out _);
            if (check != NativePawnControlResult.Ready) { failure = NativeDraftProtocol.Failure(check, context); return false; }
            if (pawn.Dead || pawn.Downed || pawn.Drafted || pawn.InMentalState || !pawn.IsColonistPlayerControlled)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Pawn unavailable, drafted or in an active mental break."); return false; }
            var expectedJob = command.ExpectedJob.StateCase == Operations.ExpectedJob.StateOneofCase.JobId ? (int?)command.ExpectedJob.JobId : null;
            if (pawn.CurJob?.loadID != expectedJob || pawn.CurJob?.playerForced == true || pawn.jobs.jobQueue.Count != 0)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Current job changed or player work is protected."); return false; }
            if (pawn.timetable?.CurrentAssignment?.defName != command.ExpectedScheduleDef)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Player timetable changed."); return false; }
            if (HealthAIUtility.ShouldSeekMedicalRest(pawn))
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Medical rest takes precedence."); return false; }
            if (!pawn.jobs.IsCurrentJobPlayerInterruptible() || pawn.carryTracker?.CarriedThing != null || pawn.IsBurning())
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Current job, carried cargo or fire prevents safe interruption."); return false; }
            var level = NeedLevel(pawn, command.Need);
            if (level == null || level >= 0.5f)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Need is absent or no longer deficient."); return false; }
            giver = Giver(command.Need);
            if (giver == null) { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Unknown need."); return false; }
            var assignment = pawn.timetable.CurrentAssignment;
            if (command.Need == Operations.Need.Joy
                    ? assignment != TimeAssignmentDefOf.Anything && assignment != TimeAssignmentDefOf.Joy
                    : giver.GetPriority(pawn) <= 0)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Native need priority or player timetable prevents recovery now."); return false; }
            return true;
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            var command = request.Operation.RelieveNeed; var pre = request.Precondition;
            if (!Valid(command))
                return Refuse(Common.FailureCode.InvalidRequest, "Relief requires an exact pawn, need, expected current job/idle and expected timetable.");
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            try
            {
                if (!Prepare(command, context, out var identity, out var pawn, out var giver, out var failure))
                    return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return Refuse(Common.FailureCode.AuthorityRequired, "Current native authority is required.");
                var guard = authority.Check(pre.ExpectedGeneration);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                giver!.ResolveReferences();
                var job = giver.TryIssueJobPackage(pawn!, default(JobIssueParams)).Job;
                if (job == null) return Refuse(Common.FailureCode.NativeFailure, "No eligible native need job; inspect access, resources and recreation tolerance.");
                if (command.Need == Operations.Need.Food && job.def != JobDefOf.Ingest)
                    return Refuse(Common.FailureCode.Unsupported, "Food recovery requires an available ingestible; production remains a separate goal.");
                if (job.targetA.IsValid && (!pawn!.CanReach(job.targetA, PathEndMode.Touch, Danger.None) || job.targetA.Cell.IsForbidden(pawn)))
                    return Refuse(Common.FailureCode.InvalidRequest, "Need target is not safely reachable under current restrictions.");
                guard = authority.Check(pre.ExpectedGeneration);
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                if (!Prepare(command, context, out identity, out pawn, out giver, out failure))
                    return new Operations.ExecuteReply { Failure = failure };
                var admission = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admission.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admission.DecidedReply;
                handle = admission.AdmittedHandle;
                bool accepted = false; Exception? effectError = null;
                using (authority.Owned())
                {
                    if (!NativePawnControlState.IsReady || !Prepare(command, context, out identity, out pawn, out giver, out failure))
                        throw new InvalidOperationException("Relief prerequisites changed after admission.");
                    guard = authority.Check(pre.ExpectedGeneration);
                    if (!guard.Success) throw new InvalidOperationException("Relief authority changed before native effect.");
                    if (!job.TryMakePreToilReservations(pawn!, errorOnFailed: false))
                    { pawn!.ClearReservationsForJob(job); throw new InvalidOperationException("Native job reservations refused recovery."); }
                    var record = new NativeMoodReliefRecord(identity, pawn!, job, command.Need, context);
                    state.MoodRelief.Add(pre.Attempt.Clone(), record);
                    try { pawn!.jobs.StartJob(job, JobCondition.InterruptForced, giver, preToilReservationsCanFail: true); accepted = pawn.CurJob == job; }
                    catch (Exception error) { effectError = error; }
                    if (NativePawnControlState.Observe(identity, pawn!, out var snapshot) != NativePawnControlResult.Ready || snapshot == null)
                        throw new InvalidOperationException("Native relief readback unavailable.");
                    evidence = record.Evidence(snapshot, accepted, accepted);
                    if (effectError != null || !accepted) throw new InvalidOperationException("Native relief order requires causal observation.", effectError);
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null
                    ? Refuse(Common.FailureCode.NativeFailure, "Relief validation failed: " + error.GetType().Name)
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence, "Admitted relief requires observation: " + error.GetType().Name) };
            }
        }

        internal static Operations.PreviewReply Preview(Operations.RelieveNeed command, Common.ObservationContext context)
        {
            if (!Valid(command))
                return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Relief requires an exact pawn, need, expected current job/idle and expected timetable.") };
            try
            {
                if (!Prepare(command, context, out _, out var pawn, out _, out var failure))
                    return new Operations.PreviewReply { Failure = failure };
                var level = NeedLevel(pawn!, command.Need);
                NativePawnControlState.Observe(new NativeControlIdentity(Current.Game, ProtoBoundary.LoadedMap(context), context.Identity.ColonyId, context.Identity.LoadToken), pawn!, out var snapshot);
                return NativeOperationEnvelope.Preview(new Operations.PreviewReply
                {
                    Evaluated = new Operations.PreviewEvaluation
                    {
                        Context = context.Clone(), Accepted = true,
                        Reason = "Exact native need priority and player timetable allow recovery now.",
                        Projected = new Receipts.EffectEvidence
                        {
                            Job = new Receipts.JobEffect
                            {
                                PawnId = snapshot?.PawnId ?? pawn!.GetUniqueLoadID(), CanTry = true, Issued = false, Verified = false,
                                TargetA = new Receipts.JobTarget { ThingId = pawn!.GetUniqueLoadID() },
                            }
                        }
                    }
                });
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Relief preview failed: " + error.GetType().Name) }; }
        }

        private static Operations.ExecuteReply Refuse(Common.FailureCode code, string detail) => new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(code, detail) };
    }
}
