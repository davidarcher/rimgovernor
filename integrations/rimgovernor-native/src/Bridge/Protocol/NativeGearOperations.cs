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
    // ImproveGear: the gear family's wear order (issue #233). Ports the
    // legacy JSON home/gear_upkeep execution (GearUpkeepTools.Run with a
    // target) onto the operations contract: the pawn's control snapshot
    // token, the candidate's supply ("allow-") token that the colony gear
    // census emits (NativeGearFacts) and the loadout signature
    // (GearUpkeepTools.Identity) all have to match, then the exact
    // JobDefOf.Wear job an apparel float-menu order would produce is issued
    // as ordered work so no forced-outfit entry is created. Weapons keep
    // going through PAWN_ORDER_KIND_EQUIP; this operation is apparel only.
    internal sealed class NativeGearRecord
    {
        private readonly NativeControlIdentity identity;
        private readonly Pawn pawn;
        private readonly Apparel apparel;
        private readonly string jobDef;
        private readonly int jobId;
        private readonly Common.ObservationContext admitted;
        internal NativeGearRecord(NativeControlIdentity identity, Pawn pawn, Apparel apparel, Job job, Common.ObservationContext context)
        { this.identity = identity; this.pawn = pawn; this.apparel = apparel; jobId = job.loadID; jobDef = job.def?.defName ?? ""; admitted = context.Clone(); }

        // The bridge's gear-replace evidence contract allows only the job
        // identity, target and verification fields (no drafted flag or
        // resulting snapshot token), unlike equip's.
        internal Receipts.EffectEvidence Evidence(NativePawnSnapshot snapshot, bool issued, bool verified) => new Receipts.EffectEvidence
        {
            Job = new Receipts.JobEffect
            {
                PawnId = snapshot.PawnId, JobId = jobId, JobDef = jobDef,
                TargetA = new Receipts.JobTarget { ThingId = apparel.GetUniqueLoadID() },
                Issued = issued, Verified = verified,
                VerifiedReason = verified ? "Exact issued native wear job or immediate worn readback observed." : "Issued job outcome requires observation.",
            }
        };

        private bool Worn => pawn.apparel != null && pawn.apparel.WornApparel.Contains(apparel);

        internal Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = false };
            try
            {
                if (!context.Identity.Equals(admitted.Identity) || context.Tick < admitted.Tick
                    || NativePawnControlState.Observe(identity, pawn, out var snapshot) != NativePawnControlResult.Ready || snapshot == null)
                    throw new InvalidOperationException("Current wear pawn context cannot be inspected.");
                result.CompleteInspection = true;
                if (Worn)
                    result.Completed = new Receipts.CompletedEffect { Evidence = Evidence(snapshot, false, true) };
                else if (pawn.CurJob != null && pawn.CurJob.loadID == jobId)
                    result.Pending = new Receipts.PendingEffect { Evidence = Evidence(snapshot, false, true) };
                else
                    result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.Interrupted,
                        Evidence = Evidence(snapshot, false, false),
                        Detail = "Native wear job is no longer active and the apparel is not worn." };
            }
            catch (Exception error) { result.CompleteInspection = false; result.Unknown = new Receipts.UnknownEffect { Reason = "Wear inspection unavailable: " + error.GetType().Name }; }
            return result;
        }
    }

    internal static class NativeGearOperations
    {
        internal static bool Valid(Operations.ImproveGear? command) => command != null
            && NativeDraftProtocol.ValidEntity(command.Pawn) && NativeDraftProtocol.ValidEntity(command.Target)
            && command.Pawn.EntityId != command.Target.EntityId
            && command.HasExpectedLoadoutToken && ProtoBoundary.IsIdentifier(command.ExpectedLoadoutToken);

        private static bool Prepare(Operations.ImproveGear command, Common.ObservationContext context, out NativeControlIdentity identity,
            out Pawn? pawn, out Apparel? apparel, out NativePawnSnapshot? snapshot, out Common.Failure failure)
        {
            var map = ProtoBoundary.LoadedMap(context);
            identity = new NativeControlIdentity(Current.Game, map, context.Identity.ColonyId, context.Identity.LoadToken);
            pawn = null; apparel = null; snapshot = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Live native pawn control hooks are required.");
            if (!NativePawnControlState.IsReady) return false;
            pawn = map.mapPawns.FreeColonistsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == command.Pawn.EntityId);
            if (pawn == null) { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact free colonist is not spawned on this map."); return false; }
            var check = NativePawnControlState.Check(identity, pawn, command.Pawn.ExpectedSnapshotToken, out snapshot);
            if (check != NativePawnControlResult.Ready) { failure = NativeDraftProtocol.Failure(check, context); return false; }
            if (GearUpkeepTools.Identity(pawn) != command.ExpectedLoadoutToken)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Loadout or apparel assignment changed; observe before new admission."); return false; }
            var blocked = GearUpkeepTools.Available(pawn);
            if (blocked != null) { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Wear requires an available pawn: " + blocked); return false; }
            apparel = map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel).OfType<Apparel>().SingleOrDefault(a => a.GetUniqueLoadID() == command.Target.EntityId);
            if (apparel == null) { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact loose apparel is unavailable."); return false; }
            if (NativeSupplyAllow.Snapshot(apparel, context)?.Token != command.Target.ExpectedSnapshotToken)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Apparel snapshot changed; observe before new admission."); return false; }
            var refusal = GearUpkeepTools.Eligible(pawn, apparel);
            if (refusal != null) { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Pawn cannot wear the apparel: " + refusal); return false; }
            if (GearUpkeepTools.Gain(pawn, apparel) < .05f)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "No material native apparel improvement."); return false; }
            return true;
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            var command = request.Operation.ImproveGear; var pre = request.Precondition;
            if (!Valid(command))
                return Refuse(Common.FailureCode.InvalidRequest, "ImproveGear requires an exact pawn, exact apparel target and the observed loadout token.");
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            try
            {
                if (!Prepare(command, context, out var identity, out var pawn, out var apparel, out var snapshot, out var failure))
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
                    if (!NativePawnControlState.IsReady || !Prepare(command, context, out identity, out pawn, out apparel, out snapshot, out failure))
                        throw new InvalidOperationException("Wear prerequisites changed after admission.");
                    guard = authority.Check(pre.ExpectedGeneration);
                    if (!guard.Success) throw new InvalidOperationException("Wear authority changed before native effect.");
                    var job = JobMaker.MakeJob(JobDefOf.Wear, apparel);
                    var record = new NativeGearRecord(identity, pawn!, apparel!, job, context);
                    state.Gear.Add(pre.Attempt.Clone(), record);
                    // Ordered work, not a forced outfit entry: the outfit policy
                    // keeps deciding what the pawn may drop later.
                    try { accepted = pawn!.jobs.TryTakeOrderedJob(job, JobTag.Misc); }
                    catch (Exception error) { effectError = error; }
                    if (NativePawnControlState.Observe(identity, pawn!, out snapshot) != NativePawnControlResult.Ready || snapshot == null)
                        throw new InvalidOperationException("Native wear readback unavailable.");
                    var current = pawn!.CurJob;
                    bool correlated = accepted && ((pawn.apparel != null && pawn.apparel.WornApparel.Contains(apparel!)) || (current != null && current.loadID == job.loadID));
                    evidence = record.Evidence(snapshot, accepted, correlated);
                    if (effectError != null || !accepted || !correlated) throw new InvalidOperationException("Native wear order requires causal observation.", effectError);
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null
                    ? Refuse(Common.FailureCode.NativeFailure, "Wear validation failed: " + error.GetType().Name)
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence, "Admitted wear order requires observation: " + error.GetType().Name) };
            }
        }

        internal static Operations.PreviewReply Preview(Operations.ImproveGear command, Common.ObservationContext context)
        {
            if (!Valid(command))
                return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "ImproveGear requires an exact pawn, exact apparel target and the observed loadout token.") };
            try
            {
                if (!Prepare(command, context, out _, out var pawn, out var apparel, out var snapshot, out var failure))
                    return new Operations.PreviewReply { Failure = failure };
                return NativeOperationEnvelope.Preview(new Operations.PreviewReply
                {
                    Evaluated = new Operations.PreviewEvaluation
                    {
                        Context = context.Clone(), Accepted = true,
                        Reason = "Native wear gates (availability, loadout signature, apparel policy, body, reach, forced/locked apparel, score gain) pass for this pawn and apparel.",
                        Projected = new Receipts.EffectEvidence
                        {
                            Job = new Receipts.JobEffect
                            {
                                PawnId = snapshot!.PawnId, JobDef = JobDefOf.Wear.defName, CanTry = true, Issued = false, Verified = false,
                                TargetA = new Receipts.JobTarget { ThingId = apparel!.GetUniqueLoadID() },
                            }
                        }
                    }
                });
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Wear preview failed: " + error.GetType().Name) }; }
        }

        private static Operations.ExecuteReply Refuse(Common.FailureCode code, string detail) => new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(code, detail) };
    }
}
