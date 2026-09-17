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
    // Undrafted-or-drafted pawn-target order for PAWN_ORDER_KIND_EQUIP,
    // Population-*'s equip sub-step. Ports the legacy JSON home/order tool's
    // (OrderTool.PrepareEquip) FloatMenuOptionProvider_Equip gates, in the
    // same order, then issues the exact JobDefOf.Equip job a player's
    // float-menu click would produce. Unlike haul, equip applies no draft
    // gate at all (OrderTool's own comment: "rescue and equip need no draft
    // change"), so this checks pawn eligibility but not Drafted. The
    // weapon target's CAS token reuses NativeSupplyAllow's "allow-" domain,
    // the same reuse NativeHaulOperations documents: observations_list_supplies
    // (via NativeSuppliesObservationTools) is the only read path that
    // discovers loose weapons and already emits that token.
    internal sealed class NativeEquipRecord
    {
        private readonly NativeControlIdentity identity;
        private readonly Pawn pawn;
        private readonly Thing weapon;
        private readonly Job job;
        private readonly int jobId;
        private readonly Common.ObservationContext admitted;
        internal NativeEquipRecord(NativeControlIdentity identity, Pawn pawn, Thing weapon, Job job, Common.ObservationContext context)
        { this.identity = identity; this.pawn = pawn; this.weapon = weapon; this.job = job; jobId = job.loadID; admitted = context.Clone(); }

        internal Receipts.EffectEvidence Evidence(NativePawnSnapshot snapshot, bool issued, bool verified) => new Receipts.EffectEvidence
        {
            Job = new Receipts.JobEffect
            {
                PawnId = snapshot.PawnId, JobId = jobId, JobDef = job.def?.defName ?? "",
                TargetA = new Receipts.JobTarget { ThingId = weapon.GetUniqueLoadID() },
                Issued = issued, Verified = verified,
                VerifiedReason = verified ? "Exact issued native equip job or immediate equip readback observed." : "Issued job outcome requires observation.",
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
                    throw new InvalidOperationException("Current equip pawn context cannot be inspected.");
                result.CompleteInspection = true;
                if (ReferenceEquals(pawn.equipment?.Primary, weapon))
                    result.Completed = new Receipts.CompletedEffect { Evidence = Evidence(snapshot, false, true) };
                else if (pawn.CurJob != null && pawn.CurJob.loadID == jobId)
                    result.Pending = new Receipts.PendingEffect { Evidence = Evidence(snapshot, false, true) };
                else
                    result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.Interrupted,
                        Evidence = Evidence(snapshot, false, false),
                        Detail = "Native equip job is no longer active and the weapon is not equipped." };
            }
            catch (Exception error) { result.CompleteInspection = false; result.Unknown = new Receipts.UnknownEffect { Reason = "Equip inspection unavailable: " + error.GetType().Name }; }
            return result;
        }
    }

    internal static class NativeEquipOperations
    {
        internal static bool Valid(Operations.PawnTargetOrder? command) => command != null
            && command.HasKind && command.Kind == Operations.PawnOrderKind.Equip
            && NativeDraftProtocol.ValidEntity(command.Pawn) && NativeDraftProtocol.ValidEntity(command.Target)
            && command.Pawn.EntityId != command.Target.EntityId
            && command.HasRequireSafeStorage && !command.RequireSafeStorage;

        internal static bool Eligible(Thing? weapon) => weapon != null && NativeSupplyAllow.Eligible(weapon)
            && weapon.def.IsWeapon && (weapon as ThingWithComps)?.GetComp<CompEquippable>() != null;

        private static bool Prepare(Operations.PawnTargetOrder command, Common.ObservationContext context, out NativeControlIdentity identity,
            out Pawn? pawn, out Thing? weapon, out NativePawnSnapshot? snapshot, out Common.Failure failure)
        {
            identity = new NativeControlIdentity(Current.Game, ProtoBoundary.ResolveMap(context), context.Identity.ColonyId, context.Identity.LoadToken);
            pawn = null; weapon = null; snapshot = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Live native pawn control hooks are required.");
            if (!NativePawnControlState.IsReady) return false;
            pawn = ProtoBoundary.ResolveMap(context).mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == command.Pawn.EntityId);
            if (pawn == null) { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact pawn is not spawned on this map."); return false; }
            var check = NativePawnControlState.Check(identity, pawn, command.Pawn.ExpectedSnapshotToken, out snapshot);
            if (check != NativePawnControlResult.Ready) { failure = NativeDraftProtocol.Failure(check, context); return false; }
            if (!snapshot!.Eligible) { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Equip requires an eligible pawn."); return false; }
            weapon = ProtoBoundary.ResolveMap(context).listerThings.AllThings.SingleOrDefault(t => t.GetUniqueLoadID() == command.Target.EntityId);
            if (weapon == null || !Eligible(weapon)) { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact equippable weapon is unavailable."); return false; }
            if (NativeSupplyAllow.Snapshot(weapon, context)?.Token != command.Target.ExpectedSnapshotToken)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Weapon snapshot changed; observe before new admission."); return false; }
            if (pawn.equipment == null)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Pawn has no equipment tracker and can carry no weapon."); return false; }
            if (pawn.WorkTagIsDisabled(WorkTags.Violent))
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Pawn has WorkTags.Violent disabled and cannot equip a weapon."); return false; }
            if (weapon.def.IsRangedWeapon && pawn.WorkTagIsDisabled(WorkTags.Shooting))
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Pawn has WorkTags.Shooting disabled and cannot equip a ranged weapon."); return false; }
            if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Pawn is incapable of manipulation and cannot pick anything up."); return false; }
            if (weapon.IsBurning()) { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Weapon is on fire."); return false; }
            if (!pawn.CanReach(weapon, PathEndMode.ClosestTouch, Danger.Deadly))
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Pawn cannot reach the weapon."); return false; }
            if (!EquipmentUtility.CanEquip(weapon, pawn, out var cantReason, false))
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Pawn cannot equip the weapon: " + (string.IsNullOrEmpty(cantReason) ? "refused" : cantReason)); return false; }
            return true;
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            var command = request.Operation.PawnTargetOrder; var pre = request.Precondition;
            if (!Valid(command))
                return Refuse(Common.FailureCode.InvalidRequest, "Equip requires an exact pawn, exact equippable target and require_safe_storage:false.");
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            try
            {
                if (!Prepare(command, context, out var identity, out var pawn, out var weapon, out var snapshot, out var failure))
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
                    if (!NativePawnControlState.IsReady || !Prepare(command, context, out identity, out pawn, out weapon, out snapshot, out failure))
                        throw new InvalidOperationException("Equip prerequisites changed after admission.");
                    guard = authority.Check(pre.ExpectedGeneration);
                    if (!guard.Success) throw new InvalidOperationException("Equip authority changed before native effect.");
                    var job = JobMaker.MakeJob(JobDefOf.Equip, weapon);
                    var record = new NativeEquipRecord(identity, pawn!, weapon!, job, context);
                    state.Equips.Add(pre.Attempt.Clone(), record);
                    try { accepted = pawn!.jobs.TryTakeOrderedJob(job, JobTag.Misc); }
                    catch (Exception error) { effectError = error; }
                    if (NativePawnControlState.Observe(identity, pawn!, out snapshot) != NativePawnControlResult.Ready || snapshot == null)
                        throw new InvalidOperationException("Native equip readback unavailable.");
                    var current = pawn!.CurJob;
                    // Equipping the exact target at the pawn's own feet can complete
                    // within the same call; either the issued job is still current or
                    // the weapon is already equipped counts as correlated.
                    bool correlated = accepted && (ReferenceEquals(pawn.equipment?.Primary, weapon) || (current != null && current.loadID == job.loadID));
                    evidence = record.Evidence(snapshot, accepted, correlated);
                    if (effectError != null || !accepted || !correlated) throw new InvalidOperationException("Native equip order requires causal observation.", effectError);
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null
                    ? Refuse(Common.FailureCode.NativeFailure, "Equip validation failed: " + error.GetType().Name)
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence!, "Admitted equip order requires observation: " + error.GetType().Name) };
            }
        }

        internal static Operations.PreviewReply Preview(Operations.PawnTargetOrder command, Common.ObservationContext context)
        {
            if (!Valid(command))
                return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Equip requires an exact pawn, exact equippable target and require_safe_storage:false.") };
            try
            {
                if (!Prepare(command, context, out _, out var pawn, out var weapon, out var snapshot, out var failure))
                    return new Operations.PreviewReply { Failure = failure };
                return NativeOperationEnvelope.Preview(new Operations.PreviewReply
                {
                    Evaluated = new Operations.PreviewEvaluation
                    {
                        Context = context.Clone(), Accepted = true,
                        Reason = "Native equip gates (equipment tracker, work tags, reach, manipulation, EquipmentUtility.CanEquip) pass for this pawn and weapon.",
                        Projected = new Receipts.EffectEvidence
                        {
                            Job = new Receipts.JobEffect
                            {
                                PawnId = snapshot!.PawnId, JobDef = JobDefOf.Equip.defName, CanTry = true, Issued = false, Verified = false,
                                TargetA = new Receipts.JobTarget { ThingId = weapon!.GetUniqueLoadID() },
                            }
                        }
                    }
                });
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Equip preview failed: " + error.GetType().Name) }; }
        }

        private static Operations.ExecuteReply Refuse(Common.FailureCode code, string detail) => new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(code, detail) };
    }
}
