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
    // Undrafted pawn-target order for PAWN_ORDER_KIND_REPAIR, the bounded
    // repair response of MaintainEssentialRepairs (issue #2 B04h). Mirrors
    // NativeCleanOperations: an exact damaged player building, an undrafted
    // eligible worker who can reach it and needs no tending, WorkGiver_Repair's
    // own HasJobOnThing (home area, reservable, not burning, no deconstruct/
    // uninstall designation), then the work giver's own JobDefOf.Repair job
    // issued as a player-forced order. The building's CAS token is the
    // building census row's (NativeBuildingObservationTools.Token: status,
    // hit points, burning), which bridge.ReadRepairTarget refreshes through
    // observations_list_buildings before every admission. The recover_service
    // command (NativeRecoveryOperations) also repairs, but only serviceable
    // buildings under the disaster-recovery contract with its own token; this
    // order covers any damaged player structure the upkeep census reports.
    internal sealed class NativeRepairRecord
    {
        private readonly NativeControlIdentity identity;
        private readonly Pawn pawn;
        private readonly Building building;
        private readonly string buildingId;
        private readonly int jobId;
        private readonly string jobDef;
        private readonly Common.ObservationContext admitted;
        internal NativeRepairRecord(NativeControlIdentity identity, Pawn pawn, Building building, Job job, Common.ObservationContext context)
        { this.identity = identity; this.pawn = pawn; this.building = building; buildingId = building.GetUniqueLoadID(); jobId = job.loadID; jobDef = job.def?.defName ?? ""; admitted = context.Clone(); }

        private static bool Repaired(Building building) => !building.def.useHitPoints || building.HitPoints >= building.MaxHitPoints;

        internal Receipts.EffectEvidence Evidence(NativePawnSnapshot snapshot, bool issued, bool verified) => new Receipts.EffectEvidence
        {
            Job = new Receipts.JobEffect
            {
                PawnId = snapshot.PawnId, JobId = jobId, JobDef = jobDef,
                TargetA = new Receipts.JobTarget { ThingId = buildingId },
                Issued = issued, Verified = verified,
                VerifiedReason = verified ? "Exact issued native repair job observed, or the exact building is at full hit points after it ran." : "Issued job outcome requires observation.",
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
                    throw new InvalidOperationException("Current repair pawn context cannot be inspected.");
                result.CompleteInspection = true;
                bool current = pawn.CurJob != null && pawn.CurJob.loadID == jobId;
                if (!building.Destroyed && building.Spawned && Repaired(building))
                    result.Completed = new Receipts.CompletedEffect { Evidence = Evidence(snapshot, false, true) };
                else if (current)
                    result.Pending = new Receipts.PendingEffect { Evidence = Evidence(snapshot, false, true) };
                else if (building.Destroyed || !building.Spawned)
                    result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.Interrupted,
                        Evidence = Evidence(snapshot, false, false),
                        Detail = "The exact building is gone before the repair completed." };
                else
                    result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.Interrupted,
                        Evidence = Evidence(snapshot, false, false),
                        Detail = "Native repair job is no longer active and the exact building remains damaged." };
            }
            catch (Exception error) { result.CompleteInspection = false; result.Unknown = new Receipts.UnknownEffect { Reason = "Repair inspection unavailable: " + error.GetType().Name }; }
            return result;
        }
    }

    internal static class NativeRepairOperations
    {
        internal static bool Valid(Operations.PawnTargetOrder? command) => command != null
            && command.HasKind && command.Kind == Operations.PawnOrderKind.Repair
            && NativeDraftProtocol.ValidEntity(command.Pawn) && NativeDraftProtocol.ValidEntity(command.Target)
            && command.Pawn.EntityId != command.Target.EntityId
            && command.HasRequireSafeStorage && !command.RequireSafeStorage;

        private static bool Prepare(Operations.PawnTargetOrder command, Common.ObservationContext context, out NativeControlIdentity identity,
            out Pawn? pawn, out Building? building, out NativePawnSnapshot? snapshot, out WorkGiverJobResult? job, out Common.Failure failure)
        {
            identity = new NativeControlIdentity(Current.Game, Find.CurrentMap, context.Identity.ColonyId, context.Identity.LoadToken);
            pawn = null; building = null; snapshot = null; job = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Live native pawn control hooks are required.");
            if (!NativePawnControlState.IsReady) return false;
            var map = Find.CurrentMap;
            pawn = map.mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == command.Pawn.EntityId);
            if (pawn == null) { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact pawn is not spawned on this map."); return false; }
            var check = NativePawnControlState.Check(identity, pawn, command.Pawn.ExpectedSnapshotToken, out snapshot);
            if (check != NativePawnControlResult.Ready) { failure = NativeDraftProtocol.Failure(check, context); return false; }
            if (snapshot!.Drafted || !snapshot.Eligible)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Repair requires an eligible undrafted pawn."); return false; }
            building = map.listerBuildings.allBuildingsColonist.SingleOrDefault(b => b.GetUniqueLoadID() == command.Target.EntityId);
            if (building == null || building.Destroyed || !building.Spawned || building.Map != map)
            { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact spawned player building is unavailable."); return false; }
            if (NativeBuildingObservationTools.Token(building, context).Token != command.Target.ExpectedSnapshotToken)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Building snapshot changed; observe before new admission."); return false; }
            if (!building.def.useHitPoints || building.HitPoints >= building.MaxHitPoints)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Building is not damaged."); return false; }
            if (pawn.health.HasHediffsNeedingTend())
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Pawn needs tending and cannot be ordered to repair."); return false; }
            if (!pawn.CanReach(building, PathEndMode.Touch, Danger.None))
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Pawn cannot safely reach the building."); return false; }
            // WorkGiver_Repair's HasJobOnThing carries the remaining gates (home
            // area, reservation, burning, pending deconstruction/uninstall) and
            // WorkGiverDispatch adds the float-menu capability refusals.
            job = WorkGiverDispatch.TryJob(pawn, building, def => def.giverClass == typeof(WorkGiver_Repair), out var reason);
            if (job == null || job.Job.def != JobDefOf.Repair)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "WorkGiver_Repair refuses this building for this pawn right now" + (string.IsNullOrEmpty(reason) ? "." : ": " + reason)); return false; }
            return true;
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            var command = request.Operation.PawnTargetOrder; var pre = request.Precondition;
            if (!Valid(command))
                return Refuse(Common.FailureCode.InvalidRequest, "Repair requires an exact pawn, exact building target and require_safe_storage:false.");
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            try
            {
                if (!Prepare(command, context, out var identity, out var pawn, out var building, out var snapshot, out _, out var failure))
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
                    if (!NativePawnControlState.IsReady || !Prepare(command, context, out identity, out pawn, out building, out snapshot, out var result, out failure) || result == null)
                        throw new InvalidOperationException("Repair prerequisites changed after admission.");
                    guard = authority.Check(pre.ExpectedGeneration);
                    if (!guard.Success) throw new InvalidOperationException("Repair authority changed before native effect.");
                    var job = result.Job;
                    var record = new NativeRepairRecord(identity, pawn!, building!, job, context);
                    state.Repairs.Add(pre.Attempt.Clone(), record);
                    try { accepted = pawn!.jobs.TryTakeOrderedJobPrioritizedWork(job, result.Scanner, building!.Position); }
                    catch (Exception error) { effectError = error; }
                    if (NativePawnControlState.Observe(identity, pawn!, out snapshot) != NativePawnControlResult.Ready || snapshot == null)
                        throw new InvalidOperationException("Native repair readback unavailable.");
                    var current = pawn!.CurJob;
                    bool correlated = accepted && current != null && current.loadID == job.loadID;
                    evidence = record.Evidence(snapshot, accepted, correlated);
                    if (effectError != null || !accepted || !correlated) throw new InvalidOperationException("Native repair order requires causal observation.", effectError);
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null
                    ? Refuse(Common.FailureCode.NativeFailure, "Repair validation failed: " + error.GetType().Name)
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence!, "Admitted repair order requires observation: " + error.GetType().Name) };
            }
        }

        internal static Operations.PreviewReply Preview(Operations.PawnTargetOrder command, Common.ObservationContext context)
        {
            if (!Valid(command))
                return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Repair requires an exact pawn, exact building target and require_safe_storage:false.") };
            try
            {
                if (!Prepare(command, context, out _, out var pawn, out var building, out var snapshot, out _, out var failure))
                {
                    // A gate that closed since the target was read (the wall
                    // healed, the token moved) is a preview outcome, not a
                    // malformed request: report it so the plan holds visibly.
                    if (failure.Code != Common.FailureCode.InvalidRequest)
                        return new Operations.PreviewReply { Failure = failure };
                    return NativeOperationEnvelope.Preview(new Operations.PreviewReply
                    {
                        Evaluated = new Operations.PreviewEvaluation
                        {
                            Context = context.Clone(), Accepted = false, Reason = failure.Detail,
                            Projected = new Receipts.EffectEvidence
                            {
                                Job = new Receipts.JobEffect
                                {
                                    PawnId = command.Pawn.EntityId, JobDef = JobDefOf.Repair.defName, CanTry = false, Issued = false, Verified = false,
                                    TargetA = new Receipts.JobTarget { ThingId = command.Target.EntityId },
                                }
                            }
                        }
                    });
                }
                return NativeOperationEnvelope.Preview(new Operations.PreviewReply
                {
                    Evaluated = new Operations.PreviewEvaluation
                    {
                        Context = context.Clone(), Accepted = true,
                        Reason = "Native repair gates (damaged player building, undrafted eligible pawn, reach, WorkGiver_Repair.HasJobOnThing) pass for this pawn and building.",
                        Projected = new Receipts.EffectEvidence
                        {
                            Job = new Receipts.JobEffect
                            {
                                PawnId = snapshot!.PawnId, JobDef = JobDefOf.Repair.defName, CanTry = true, Issued = false, Verified = false,
                                TargetA = new Receipts.JobTarget { ThingId = building!.GetUniqueLoadID() },
                            }
                        }
                    }
                });
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Repair preview failed: " + error.GetType().Name) }; }
        }

        private static Operations.ExecuteReply Refuse(Common.FailureCode code, string detail) => new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(code, detail) };
    }
}
