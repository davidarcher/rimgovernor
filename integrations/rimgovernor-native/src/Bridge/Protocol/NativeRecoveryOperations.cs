#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Authority = RimGovernor.Protocol.Authority;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // Typed dispatch for RecoverDisasterServices' service jobs: repair,
    // breakdown restoration and refuel. Reuses the same real native
    // WorkGiverDispatch/WorkGiver_Scanner path the legacy JSON
    // HomeRecoveryTools.Recover (home/recover_service) issues, so an order
    // here is exactly the job a player's float-menu click would produce.
    // The building CAS token is self-computed and self-checked the same way
    // NativeSupplyAllow does for loose items; no existing observation reply
    // exposes it yet (colony_recovery's BuildingState is validated with a
    // strict "no extra EntityRef fields" contract in the Go bridge), so a
    // caller must obtain the token from a future dedicated read rather than
    // the existing recovery census. That follow-up is out of scope here.
    internal sealed class NativeRecoveryServiceRecord
    {
        private readonly NativeControlIdentity identity;
        private readonly Pawn pawn;
        private readonly Building target;
        private readonly Job job;
        private readonly int jobId;
        private readonly Operations.ServiceMethod method;
        private readonly Common.ObservationContext admitted;

        internal NativeRecoveryServiceRecord(NativeControlIdentity identity, Pawn pawn, Building target, Job job,
            Operations.ServiceMethod method, Common.ObservationContext context)
        {
            this.identity = identity; this.pawn = pawn; this.target = target; this.job = job;
            jobId = job.loadID; this.method = method; admitted = context.Clone();
        }

        // Issued describes only whether THIS call just issued a new job; Progress
        // always reports Issued=false, matching the haul/gear evidence contract.
        internal Receipts.EffectEvidence Evidence(NativePawnSnapshot snapshot, bool issued, bool verified) => new Receipts.EffectEvidence
        {
            Job = new Receipts.JobEffect
            {
                PawnId = snapshot.PawnId, JobId = jobId, JobDef = job.def?.defName ?? "",
                TargetA = new Receipts.JobTarget { ThingId = target.GetUniqueLoadID() },
                Issued = issued, Verified = verified,
                VerifiedReason = verified ? "Exact issued native service job observed." : "Issued job outcome requires observation.",
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
                    throw new InvalidOperationException("Current service pawn context cannot be inspected.");
                if (target.Destroyed || !target.Spawned || target.Map != ProtoBoundary.ResolveMap(context))
                {
                    result.Unknown = new Receipts.UnknownEffect { Reason = "The exact service target is no longer observable; absence does not prove completion." };
                    return result;
                }
                result.CompleteInspection = true;
                if (NativeRecoveryOperations.Satisfied(target, method))
                    result.Completed = new Receipts.CompletedEffect { Evidence = Evidence(snapshot, false, true) };
                else if (pawn.CurJob != null && pawn.CurJob.loadID == jobId)
                    result.Pending = new Receipts.PendingEffect { Evidence = Evidence(snapshot, false, true) };
                else
                    result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.Interrupted,
                        Evidence = Evidence(snapshot, false, false),
                        Detail = "Native service job is no longer active and the target is not yet serviced." };
            }
            catch (Exception error) { result.CompleteInspection = false; result.Unknown = new Receipts.UnknownEffect { Reason = "Service inspection unavailable: " + error.GetType().Name }; }
            return result;
        }
    }

    internal static class NativeRecoveryOperations
    {
        // Target only needs identity: its recovery-specific CAS token travels
        // on the decoupled expected_target_snapshot_token field (checked in
        // Prepare, conditionally), not on this EntityPrecondition's own token.
        internal static bool Valid(Operations.RecoverService? command) => command != null
            && NativeDraftProtocol.ValidEntityId(command.Target) && NativeDraftProtocol.ValidEntity(command.Pawn)
            && command.HasMethod && command.Method != Operations.ServiceMethod.Unspecified;

        internal static bool Eligible(Building? building) => building != null && !building.Destroyed && building.Spawned
            && ProtoBoundary.IsLoaded(building.Map) && !building.Position.Fogged(building.Map)
            && !building.IsForbidden(Faction.OfPlayer) && !building.IsBurning();

        // Mirrors RecoveryTools.Order's "observed service no longer needs this
        // method" refusal: repair only while under max HP, breakdown only while
        // broken, refuel only while under target fuel level.
        internal static bool Satisfied(Building building, Operations.ServiceMethod method)
        {
            switch (method)
            {
                case Operations.ServiceMethod.Repair:
                    return !building.def.useHitPoints || building.HitPoints >= building.MaxHitPoints;
                case Operations.ServiceMethod.Breakdown:
                    return building.TryGetComp<CompBreakdownable>()?.BrokenDown != true;
                case Operations.ServiceMethod.Refuel:
                    var fuel = building.TryGetComp<CompRefuelable>();
                    return fuel == null || fuel.Fuel >= fuel.TargetFuelLevel;
                default:
                    return true;
            }
        }

        private static Type? WorkGiverType(Operations.ServiceMethod method) => method switch
        {
            Operations.ServiceMethod.Repair => typeof(WorkGiver_Repair),
            Operations.ServiceMethod.Breakdown => typeof(WorkGiver_FixBrokenDownBuilding),
            Operations.ServiceMethod.Refuel => typeof(WorkGiver_Refuel),
            _ => null,
        };

        // Deliberately independent of NativeSupplyAllow.Token: the fields that
        // invalidate a repair/breakdown/refuel CAS token differ (hit points,
        // breakdown state, fuel level) from a loose item's (position/stack/forbid).
        internal static string Token(Common.Identity identity, Building building)
        {
            var breakdown = building.TryGetComp<CompBreakdownable>();
            var fuel = building.TryGetComp<CompRefuelable>();
            using (var bytes = new MemoryStream())
            {
                using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
                {
                    writer.Write(identity.ColonyId); writer.Write(identity.LoadToken); writer.Write(identity.MapId);
                    writer.Write(building.GetUniqueLoadID()); writer.Write(building.def.defName);
                    writer.Write(building.def.useHitPoints); writer.Write(building.HitPoints); writer.Write(building.MaxHitPoints);
                    writer.Write(breakdown?.BrokenDown ?? false);
                    writer.Write(fuel?.Fuel ?? -1f); writer.Write(fuel?.TargetFuelLevel ?? -1f);
                    writer.Write(building.IsForbidden(Faction.OfPlayer)); writer.Write(building.IsBurning());
                }
                using (var hash = SHA256.Create())
                    return "recover-" + BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }

        // requireExpected additionally enforces the caller's
        // expected_target_snapshot_token precondition (Execute always
        // requires it; Preview only when the caller supplied it, letting an
        // unconstrained call establish the current baseline), mirroring
        // NativeSurgeryOperations.Prepare's requireExpected split. token is
        // always the current native-computed value, returned so Preview can
        // report it regardless of requireExpected.
        private static bool Prepare(Operations.RecoverService command, Common.ObservationContext context, bool requireExpected, out NativeControlIdentity identity,
            out Pawn? pawn, out Building? building, out NativePawnSnapshot? snapshot, out string token, out Common.Failure failure)
        {
            identity = new NativeControlIdentity(Current.Game, ProtoBoundary.LoadedMap(context), context.Identity.ColonyId, context.Identity.LoadToken);
            pawn = null; building = null; snapshot = null; token = "";
            failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Live native pawn control hooks are required.");
            if (!NativePawnControlState.IsReady) return false;
            pawn = ProtoBoundary.LoadedMap(context).mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == command.Pawn.EntityId);
            if (pawn == null) { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact pawn is not spawned on this map."); return false; }
            var check = NativePawnControlState.Check(identity, pawn, command.Pawn.ExpectedSnapshotToken, out snapshot);
            if (check != NativePawnControlResult.Ready) { failure = NativeDraftProtocol.Failure(check, context); return false; }
            if (snapshot!.Drafted || !snapshot.Eligible)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Service order requires an eligible undrafted pawn."); return false; }
            building = ProtoBoundary.LoadedMap(context).listerBuildings.allBuildingsColonist.SingleOrDefault(b => b.GetUniqueLoadID() == command.Target.EntityId);
            if (building == null || !Eligible(building)) { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact serviceable building is unavailable."); return false; }
            token = Token(context.Identity, building);
            if (requireExpected && (!command.HasExpectedTargetSnapshotToken || token != command.ExpectedTargetSnapshotToken))
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Service target snapshot changed; observe before new admission."); return false; }
            if (Satisfied(building, command.Method))
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Observed service no longer needs this method."); return false; }
            return true;
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            var command = request.Operation.RecoverService; var pre = request.Precondition;
            if (!Valid(command))
                return Refuse(Common.FailureCode.InvalidRequest, "Service order requires an exact pawn, exact building target and a repair/breakdown/refuel method.");
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            try
            {
                if (!Prepare(command, context, true, out var identity, out var pawn, out var building, out var snapshot, out _, out var failure))
                    return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return Refuse(Common.FailureCode.AuthorityRequired, "Current native authority is required.");
                var guard = authority.Check(pre.ExpectedGeneration);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var giverType = WorkGiverType(command.Method);
                var result = WorkGiverDispatch.TryJob(pawn!, building!, def => def.giverClass == giverType, out _);
                if (result == null) return Refuse(Common.FailureCode.NativeFailure, "No native service job is available for this pawn and target.");
                guard = authority.Check(pre.ExpectedGeneration);
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                if (!Prepare(command, context, true, out identity, out pawn, out building, out snapshot, out _, out failure))
                    return new Operations.ExecuteReply { Failure = failure };
                var admission = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admission.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admission.DecidedReply;
                handle = admission.AdmittedHandle;
                bool accepted = false; Exception? effectError = null;
                using (authority.Owned())
                {
                    if (!NativePawnControlState.IsReady || !Prepare(command, context, true, out identity, out pawn, out building, out snapshot, out _, out failure))
                        throw new InvalidOperationException("Service prerequisites changed after admission.");
                    guard = authority.Check(pre.ExpectedGeneration);
                    if (!guard.Success) throw new InvalidOperationException("Service authority changed before native effect.");
                    var record = new NativeRecoveryServiceRecord(identity, pawn!, building!, result.Job, command.Method, context);
                    state.RecoveryServices.Add(pre.Attempt.Clone(), record);
                    try { accepted = pawn!.jobs.TryTakeOrderedJobPrioritizedWork(result.Job, result.Scanner, building!.Position); }
                    catch (Exception error) { effectError = error; }
                    if (NativePawnControlState.Observe(identity, pawn!, out snapshot) != NativePawnControlResult.Ready || snapshot == null)
                        throw new InvalidOperationException("Native service readback unavailable.");
                    var current = pawn!.CurJob;
                    bool correlated = accepted && current != null && ReferenceEquals(current, result.Job);
                    evidence = record.Evidence(snapshot, accepted, correlated);
                    if (effectError != null || !accepted || !correlated) throw new InvalidOperationException("Native service order requires causal observation.", effectError);
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null
                    ? Refuse(Common.FailureCode.NativeFailure, "Service validation failed: " + error.GetType().Name)
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence, "Admitted service order requires observation: " + error.GetType().Name) };
            }
        }

        internal static Operations.PreviewReply Preview(Operations.RecoverService command, Common.ObservationContext context)
        {
            if (!Valid(command))
                return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Service order requires an exact pawn, exact building target and a repair/breakdown/refuel method.") };
            try
            {
                // An unconstrained call (no expected_target_snapshot_token set)
                // establishes the current baseline, the same role an
                // unconstrained QueueSurgery call plays for its health token:
                // there is no existing observation read that could produce
                // this recovery-specific building token ahead of time.
                var requireExpected = command.HasExpectedTargetSnapshotToken;
                if (!Prepare(command, context, requireExpected, out _, out var pawn, out var building, out var snapshot, out var token, out var failure))
                    return new Operations.PreviewReply { Failure = failure };
                var giverType = WorkGiverType(command.Method);
                var result = WorkGiverDispatch.TryJob(pawn!, building!, def => def.giverClass == giverType, out _);
                var accepted = result != null;
                var jobDef = accepted ? (result!.Job.def?.defName ?? "") : "";
                return NativeOperationEnvelope.Preview(new Operations.PreviewReply
                {
                    Evaluated = new Operations.PreviewEvaluation
                    {
                        Context = context.Clone(), Accepted = accepted,
                        Reason = accepted ? "Exact native service WorkGiver produced a job for this target." : "No native service WorkGiver would produce a job for this pawn and target.",
                        Projected = new Receipts.EffectEvidence
                        {
                            Job = new Receipts.JobEffect
                            {
                                PawnId = snapshot!.PawnId, JobDef = jobDef, CanTry = accepted, Issued = false, Verified = false,
                                TargetA = new Receipts.JobTarget { ThingId = building!.GetUniqueLoadID() }, TargetSnapshotToken = token,
                            }
                        }
                    }
                });
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Service preview failed: " + error.GetType().Name) }; }
        }

        private static Operations.ExecuteReply Refuse(Common.FailureCode code, string detail) => new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(code, detail) };
    }
}
