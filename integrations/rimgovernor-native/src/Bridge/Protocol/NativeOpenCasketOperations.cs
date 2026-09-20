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
    // Pawn-target order for PAWN_ORDER_KIND_OPEN_CASKET (#460): the opener of
    // ClearAncientShrine's melee lock runs the vanilla Open job on one filled
    // ancient cryptosleep casket. Opening one casket ejects every casket of
    // the shrine group, so a plan carries one order after the drafts and
    // moves that stand a melee colonist at each casket's interaction cell.
    // Unlike Repair the pawn may be drafted: JobDriver_Open is an ordered
    // job the draft does not refuse, and the opener is one of the lockers.
    // JobDriver_Open fails without an Open designation on the target, so the
    // order adds one under authority right before the job and removes it
    // again if the job drops with the casket still full, so no undrafted
    // colonist opens it behind the lock through WorkGiver_Open. The casket's
    // CAS token is the claim token (NativeClaimBuilding.Token: faction and
    // whether it holds anything), which bridge.ReadClaimBuildingTarget
    // refreshes through observations_list_buildings before admission.
    internal sealed class NativeOpenCasketRecord
    {
        private readonly NativeControlIdentity identity;
        private readonly Pawn pawn;
        private readonly Building_Casket casket;
        private readonly string casketId;
        private readonly int jobId;
        private readonly string jobDef;
        private readonly Common.ObservationContext admitted;
        internal NativeOpenCasketRecord(NativeControlIdentity identity, Pawn pawn, Building_Casket casket, Job job, Common.ObservationContext context)
        { this.identity = identity; this.pawn = pawn; this.casket = casket; casketId = casket.GetUniqueLoadID(); jobId = job.loadID; jobDef = job.def?.defName ?? ""; admitted = context.Clone(); }

        internal Receipts.EffectEvidence Evidence(NativePawnSnapshot snapshot, bool issued, bool verified) => new Receipts.EffectEvidence
        {
            Job = new Receipts.JobEffect
            {
                PawnId = snapshot.PawnId, JobId = jobId, JobDef = jobDef,
                TargetA = new Receipts.JobTarget { ThingId = casketId },
                Issued = issued, Verified = verified,
                VerifiedReason = verified ? "Exact issued native Open job observed, or the exact casket is empty after it ran." : "Issued job outcome requires observation.",
                Drafted = snapshot.Drafted, ResultingSnapshotToken = snapshot.Token,
            }
        };

        internal Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = false };
            try
            {
                if (!context.Identity.Equals(admitted.Identity) || context.Tick < admitted.Tick
                    || NativePawnControlState.Observe(identity, pawn, out var snapshot) != NativePawnControlResult.Ready || snapshot == null)
                    throw new InvalidOperationException("Current casket opener context cannot be inspected.");
                result.CompleteInspection = true;
                bool current = pawn.CurJob != null && pawn.CurJob.loadID == jobId;
                bool gone = casket.Destroyed || !casket.Spawned;
                if (!gone && !casket.HasAnyContents)
                    result.Completed = new Receipts.CompletedEffect { Evidence = Evidence(snapshot, false, true) };
                else if (current)
                    result.Pending = new Receipts.PendingEffect { Evidence = Evidence(snapshot, false, true) };
                else if (gone)
                    result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.Interrupted,
                        Evidence = Evidence(snapshot, false, false),
                        Detail = "The exact casket is gone before it was opened." };
                else
                {
                    NativeOpenCasketOperations.Undesignate(casket);
                    result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.Interrupted,
                        Evidence = Evidence(snapshot, false, false),
                        Detail = "Native Open job is no longer active and the exact casket still holds its contents." };
                }
            }
            catch (Exception error) { result.CompleteInspection = false; result.Unknown = new Receipts.UnknownEffect { Reason = "Casket opening inspection unavailable: " + error.GetType().Name }; }
            return result;
        }
    }

    internal static class NativeOpenCasketOperations
    {
        internal static bool Valid(Operations.PawnTargetOrder? command) => command != null
            && command.HasKind && (command.Kind == Operations.PawnOrderKind.OpenCasket || command.Kind == Operations.PawnOrderKind.OpenCasketHeat)
            && NativeDraftProtocol.ValidEntity(command.Pawn) && NativeDraftProtocol.ValidEntity(command.Target)
            && command.Pawn.EntityId != command.Target.EntityId
            && command.HasRequireSafeStorage && !command.RequireSafeStorage;

        internal static void Undesignate(Building_Casket casket)
        {
            if (casket.Destroyed || !casket.Spawned || casket.Map == null) return;
            var designation = casket.Map.designationManager.DesignationOn(casket, DesignationDefOf.Open);
            if (designation != null) casket.Map.designationManager.RemoveDesignation(designation);
        }

        private static bool Prepare(Operations.PawnTargetOrder command, Common.ObservationContext context, out NativeControlIdentity identity,
            out Pawn? pawn, out Building_AncientCryptosleepCasket? casket, out NativePawnSnapshot? snapshot, out Common.Failure failure)
        {
            identity = new NativeControlIdentity(Current.Game, Find.CurrentMap, context.Identity.ColonyId, context.Identity.LoadToken);
            pawn = null; casket = null; snapshot = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Live native pawn control hooks are required.");
            if (!NativePawnControlState.IsReady) return false;
            var map = Find.CurrentMap;
            pawn = map.mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == command.Pawn.EntityId);
            if (pawn == null) { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact pawn is not spawned on this map."); return false; }
            var check = NativePawnControlState.Check(identity, pawn, command.Pawn.ExpectedSnapshotToken, out snapshot);
            if (check != NativePawnControlResult.Ready) { failure = NativeDraftProtocol.Failure(check, context); return false; }
            if (!snapshot!.Eligible)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Opening a casket requires an eligible colonist."); return false; }
            casket = map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial).OfType<Building_AncientCryptosleepCasket>()
                .SingleOrDefault(b => b.GetUniqueLoadID() == command.Target.EntityId);
            if (casket == null || casket.Destroyed || !casket.Spawned || casket.Map != map)
            { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact spawned ancient cryptosleep casket is unavailable."); return false; }
            if (NativeClaimBuilding.Token(context.Identity, casket) != command.Target.ExpectedSnapshotToken)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Casket snapshot changed; observe before new admission."); return false; }
            if (!casket.HasAnyContents || !casket.CanOpen)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Casket is empty."); return false; }
            if (command.Kind == Operations.PawnOrderKind.OpenCasketHeat)
            {
                if (snapshot.Claim == null || !HeatReady(pawn, casket))
                { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Heat opening needs an owned drafted shooter at the doorway of an enclosed room above 60 C, no colonists inside, and a safe direct bullet shot."); return false; }
                return true;
            }
            if (!pawn.CanReach(casket, PathEndMode.InteractionCell, Danger.Some))
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Pawn cannot reach the casket interaction cell."); return false; }
            if (!pawn.CanReserve(casket))
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Casket is reserved by another pawn."); return false; }
            if (pawn.WorkTagIsDisabled(WorkTags.ManualDumb))
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Pawn is incapable of the dumb labor the Open job needs."); return false; }
            return true;
        }

        private static bool HeatReady(Pawn pawn, Building_AncientCryptosleepCasket casket)
        {
            var group = casket.Map.listerThings.AllThings.OfType<Building_AncientCryptosleepCasket>()
                .Where(c => casket.groupID >= 0 ? c.groupID == casket.groupID : c == casket).ToList();
            var heat = NativeShrineHeat.Read(casket.Map, group);
            var verb = pawn.equipment?.PrimaryEq?.PrimaryVerb as Verb_LaunchProjectile;
            var projectile = verb?.Projectile;
            return heat != null && heat.Enclosed && heat.TemperatureCelsius > 60 && !heat.ColonistsInside
                && heat.FiringCells.Any(c => c.X == pawn.Position.x && c.Z == pawn.Position.z)
                && pawn.Drafted && !pawn.WorkTagIsDisabled(WorkTags.Violent)
                && pawn.skills?.GetSkill(SkillDefOf.Shooting)?.TotallyDisabled == false
                && verb != null && verb.Available() && verb.CanHitTarget(casket)
                && NativeRangedCausality.Supports(verb, pawn, casket)
                && projectile?.thingClass == typeof(Bullet) && projectile.projectile.explosionRadius == 0
                && projectile.projectile.damageDef == DamageDefOf.Bullet
                && casket.HitPoints > casket.MaxHitPoints * 0.5f
                && projectile.projectile.GetDamageAmount(pawn.equipment!.Primary) * verb.verbProps.burstShotCount < casket.HitPoints - casket.MaxHitPoints * 0.2f;
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            var command = request.Operation.PawnTargetOrder; var pre = request.Precondition;
            if (!Valid(command))
                return Refuse(Common.FailureCode.InvalidRequest, "OpenCasket requires an exact pawn, exact casket target and require_safe_storage:false.");
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            try
            {
                if (!Prepare(command, context, out var identity, out var pawn, out var casket, out var snapshot, out var failure))
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
                    if (!NativePawnControlState.IsReady || !Prepare(command, context, out identity, out pawn, out casket, out snapshot, out failure))
                        throw new InvalidOperationException("OpenCasket prerequisites changed after admission.");
                    guard = authority.Check(pre.ExpectedGeneration);
                    if (!guard.Success) throw new InvalidOperationException("OpenCasket authority changed before native effect.");
                    var map = casket!.Map;
                    bool heat = command.Kind == Operations.PawnOrderKind.OpenCasketHeat;
                    if (heat && snapshot!.Claim == null) throw new InvalidOperationException("Heat opening requires an owned draft.");
                    if (!heat && map.designationManager.DesignationOn(casket, DesignationDefOf.Open) == null)
                        map.designationManager.AddDesignation(new Designation(casket, DesignationDefOf.Open));
                    var job = JobMaker.MakeJob(heat ? JobDefOf.AttackStatic : JobDefOf.Open, casket);
                    if (heat)
                    {
                        NativeCombatOperations.ConfigureRangedJob(job, pawn!.equipment.PrimaryEq.PrimaryVerb, casket);
                        job.maxNumStaticAttacks = 1;
                        var shooter = pawn;
                        var target = casket;
                        var before = snapshot!;
                        bool Causal(bool launch) => authority.Check(pre.ExpectedGeneration).Success
                            && NativePawnControlState.Observe(identity, shooter, out var currentSnapshot) == NativePawnControlResult.Ready
                            && currentSnapshot != null && currentSnapshot.Eligible && currentSnapshot.Drafted
                            && currentSnapshot.Claim?.ClaimId == before.Claim!.ClaimId
                            && currentSnapshot.Facts.OrderRevision == before.Facts.OrderRevision + 1
                            && (!launch || shooter.CurJob == job && target.HasAnyContents && HeatReady(shooter, target));
                        NativeRangedCausality.Track(Current.Game, shooter, target, job, () => Causal(true), () => Causal(false));
                    }
                    var record = new NativeOpenCasketRecord(identity, pawn!, casket, job, context);
                    state.OpenCaskets.Add(pre.Attempt.Clone(), record);
                    try { accepted = pawn!.jobs.TryTakeOrderedJob(job, JobTag.Misc); }
                    catch (Exception error) { effectError = error; }
                    if (NativePawnControlState.Observe(identity, pawn!, out snapshot) != NativePawnControlResult.Ready || snapshot == null)
                        throw new InvalidOperationException("Native casket opener readback unavailable.");
                    var current = pawn!.CurJob;
                    bool correlated = accepted && current != null && current.loadID == job.loadID;
                    evidence = record.Evidence(snapshot, accepted, correlated);
                    if (effectError != null || !accepted || !correlated)
                    {
                        Undesignate(casket);
                        throw new InvalidOperationException("Native OpenCasket order requires causal observation.", effectError);
                    }
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null
                    ? Refuse(Common.FailureCode.NativeFailure, "OpenCasket validation failed: " + error.GetType().Name)
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence!, "Admitted OpenCasket order requires observation: " + error.GetType().Name) };
            }
        }

        internal static Operations.PreviewReply Preview(Operations.PawnTargetOrder command, Common.ObservationContext context)
        {
            if (!Valid(command))
                return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "OpenCasket requires an exact pawn, exact casket target and require_safe_storage:false.") };
            try
            {
                if (!Prepare(command, context, out _, out var pawn, out var casket, out var snapshot, out var failure))
                {
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
                                    PawnId = command.Pawn.EntityId, JobDef = command.Kind == Operations.PawnOrderKind.OpenCasketHeat ? JobDefOf.AttackStatic.defName : JobDefOf.Open.defName, CanTry = false, Issued = false, Verified = false,
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
                        Reason = "Native casket gates (filled ancient casket, eligible colonist, reach to the interaction cell, reservation) pass for this pawn and casket.",
                        Projected = new Receipts.EffectEvidence
                        {
                            Job = new Receipts.JobEffect
                            {
                                PawnId = snapshot!.PawnId, JobDef = command.Kind == Operations.PawnOrderKind.OpenCasketHeat ? JobDefOf.AttackStatic.defName : JobDefOf.Open.defName, CanTry = true, Issued = false, Verified = false,
                                TargetA = new Receipts.JobTarget { ThingId = casket!.GetUniqueLoadID() },
                            }
                        }
                    }
                });
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "OpenCasket preview failed: " + error.GetType().Name) }; }
        }

        private static Operations.ExecuteReply Refuse(Common.FailureCode code, string detail) => new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(code, detail) };
    }
}
