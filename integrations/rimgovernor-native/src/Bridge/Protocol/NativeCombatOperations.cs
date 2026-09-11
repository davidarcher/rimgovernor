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
    internal enum NativeCombatPhase { Unknown, Pending, Interrupted, TargetDead, Completed }
    internal sealed class NativeCombatRecord
    {
        private readonly NativeControlIdentity identity;
        private readonly Pawn pawn,target;
        private readonly Job job;
        private readonly int jobId;
        private readonly string jobDef,targetId;
        private readonly NativePawnSnapshot before;
        private readonly Common.ObservationContext admitted;
        private readonly bool requireStanding;
        private readonly Authority.WritePrecondition precondition;
        private readonly NativeControlAuthority authority;
        private readonly NativeCombatDamageRecord damage;
        private readonly bool ranged;
        private readonly Verb? attackVerb;
        private bool dispatching;
        private ulong? order;
        internal NativeCombatRecord(NativeControlIdentity identity,Pawn pawn,Pawn target,Job job,NativePawnSnapshot before,
            Common.ObservationContext context,bool requireStanding,Authority.WritePrecondition precondition,NativeControlAuthority authority)
        {this.identity=identity;this.pawn=pawn;this.target=target;this.job=job;jobId=job.loadID;jobDef=job.def.defName;
            targetId=target.GetUniqueLoadID();this.before=before;admitted=context.Clone();this.requireStanding=requireStanding;
            this.precondition=precondition.Clone();this.authority=authority;ranged=job.def==JobDefOf.AttackStatic;attackVerb=job.verbToUse;
            damage=ranged?NativeRangedCausality.Track(Current.Game,pawn,target,job,DamageGuard,ImpactGuard)
                :NativeCombatCausality.Track(Current.Game,pawn,target,job,DamageGuard);}
        internal static NativeCombatPhase Classify(bool correlated,bool unchanged,bool dead,bool downed,bool requireStanding,bool liveJob,bool causedDeath,bool causedDowning)
        {
            if(causedDeath || requireStanding && causedDowning)return NativeCombatPhase.Completed;
            if(!correlated)return NativeCombatPhase.Unknown;
            if(!unchanged)return NativeCombatPhase.Interrupted;
            if(dead)return NativeCombatPhase.TargetDead;
            if(downed && requireStanding)return NativeCombatPhase.Interrupted;
            return liveJob?NativeCombatPhase.Pending:NativeCombatPhase.Unknown;
        }
        internal static NativeCombatPhase WithTrackingLoss(NativeCombatPhase phase,bool loss)=>loss
            && phase!=NativeCombatPhase.Completed && phase!=NativeCombatPhase.Interrupted?NativeCombatPhase.Unknown:phase;
        internal void BeginDispatch()=>dispatching=true;
        internal void EndDispatch()=>dispatching=false;
        private bool DamageGuard()=>CausalGuard(true);
        private bool ImpactGuard()=>CausalGuard(false);
        internal static bool CausalOrderAllows(ulong beforeOrder,ulong currentOrder,bool dispatching)=>beforeOrder!=ulong.MaxValue
            && (currentOrder==beforeOrder+1 || dispatching && currentOrder==beforeOrder);
        private bool CausalGuard(bool requireJob)
        {
            try {
                if(!(ranged?NativeRangedCausality.IsReady:NativeCombatCausality.IsReady)
                    || requireJob && (job.loadID!=jobId || job.def?.defName!=jobDef || !ReferenceEquals(job.targetA.Thing,target)
                        || ranged && (!ReferenceEquals(job.verbToUse,attackVerb) || !ReferenceEquals(pawn.equipment?.PrimaryEq?.PrimaryVerb,attackVerb)))
                    || Current.Game!=identity.Game || Find.CurrentMap!=identity.Map
                    || !authority.Check(precondition.ExpectedGeneration,precondition.LeaseId,precondition.Attempt.ControllerSessionId).Success
                    || NativePawnControlState.Observe(identity,pawn,out var snapshot)!=NativePawnControlResult.Ready || snapshot==null
                    || !snapshot.Eligible || !snapshot.Drafted || snapshot.Claim?.ClaimId!=before.Claim!.ClaimId || !snapshot.Claim.Owner.Equals(before.Claim.Owner)
                    || snapshot.Facts.DraftRevision!=before.Facts.DraftRevision)return false;
                return CausalOrderAllows(before.Facts.OrderRevision,snapshot.Facts.OrderRevision,dispatching);
            }catch{return false;}
        }
        private bool LiveJob()=>job.loadID==jobId && job.def?.defName==jobDef && ReferenceEquals(job.targetA.Thing,target)
            && (!ranged || ReferenceEquals(job.verbToUse,attackVerb))
            && (ReferenceEquals(pawn.CurJob,job) || pawn.jobs.jobQueue.Any(q=>ReferenceEquals(q.job,job)));
        internal bool Capture(NativePawnSnapshot snapshot)
        {
            if(before.Facts.OrderRevision==ulong.MaxValue || snapshot.Facts.OrderRevision!=before.Facts.OrderRevision+1
                || snapshot.Facts.DraftRevision!=before.Facts.DraftRevision || snapshot.Claim?.ClaimId!=before.Claim!.ClaimId
                || !snapshot.Claim.Owner.Equals(before.Claim.Owner) || !LiveJob())return false;
            order=snapshot.Facts.OrderRevision;return true;
        }
        internal Receipts.EffectEvidence Evidence(NativePawnSnapshot snapshot,bool issued,bool verified)
        {
            var effect=new Receipts.JobEffect {PawnId=snapshot.PawnId,JobId=jobId,JobDef=jobDef,
                TargetA=new Receipts.JobTarget {ThingId=targetId},Issued=issued,Verified=verified,Drafted=snapshot.Drafted,
                ResultingSnapshotToken=snapshot.Token,VerifiedReason=verified?"Exact native attack job observed; combat outcome is not certified.":"Attack outcome requires observation."};
            if(snapshot.Claim?.ClaimId==before.Claim!.ClaimId && snapshot.Claim.Owner.Equals(before.Claim.Owner))
            {effect.DraftClaimId=snapshot.Claim.ClaimId;effect.DraftOwner=snapshot.Claim.Owner.ControllerSessionId;}
            return new Receipts.EffectEvidence {Job=effect};
        }
        internal Receipts.Progress Observe(Common.AttemptKey attempt,Common.ObservationContext context)
        {
            var result=new Receipts.Progress {Attempt=attempt.Clone(),Context=context.Clone(),CompleteInspection=false};
            try {
                if(!context.Identity.Equals(admitted.Identity) || context.Tick<admitted.Tick ||
                    NativePawnControlState.Observe(identity,pawn,out var snapshot)!=NativePawnControlResult.Ready || snapshot==null)
                    throw new InvalidOperationException("Original attacker context cannot be inspected.");
                if(!order.HasValue)Capture(snapshot);
                bool unchanged=context.NativeGeneration==admitted.NativeGeneration && snapshot.Eligible && snapshot.Drafted
                    && snapshot.Claim?.ClaimId==before.Claim!.ClaimId && snapshot.Claim.Owner.Equals(before.Claim.Owner)
                    && snapshot.Facts.OrderRevision==order;
                bool damageObserved=damage.ObservedDamage && damage.ObservedTick>=admitted.Tick;
                var phase=Classify(order.HasValue,unchanged,target.Dead,target.Downed,requireStanding,LiveJob(),damageObserved && damage.CausedDeath,damageObserved && damage.CausedDowning);
                bool trackingLoss=ranged && NativeRangedCausality.HasTrackingLoss(damage);
                phase=WithTrackingLoss(phase,trackingLoss);
                if(phase==NativeCombatPhase.Unknown){result.Unknown=new Receipts.UnknownEffect {Reason=trackingLoss
                    ?"Native direct-bullet lineage is incomplete; no combat outcome can be certified."
                    :"No causally attributed combat outcome or exact live attack job is available."};return result;}
                var evidence=Evidence(snapshot,false,phase==NativeCombatPhase.Pending || phase==NativeCombatPhase.Completed);
                result.CompleteInspection=true;
                if(phase==NativeCombatPhase.Completed) {
                    evidence.Job.VerifiedReason="Native positive damage by this exact attacker/job caused the observed target "+(damage.CausedDeath?"death.":"downing.");
                    result.Completed=new Receipts.CompletedEffect {Evidence=evidence};
                }
                else if(phase==NativeCombatPhase.Pending)result.Pending=new Receipts.PendingEffect {Evidence=evidence};
                else result.Unsuccessful=new Receipts.UnsuccessfulEffect {Evidence=evidence,
                    Reason=phase==NativeCombatPhase.TargetDead?Receipts.UnsuccessfulReason.TargetDead:Receipts.UnsuccessfulReason.Interrupted,
                    Detail=phase==NativeCombatPhase.TargetDead?"The exact target is dead. No attacker kill attribution is claimed.":"Authority, owned claim, later order or required standing target changed."};
            } catch(Exception error){result.CompleteInspection=false;result.Unknown=new Receipts.UnknownEffect {Reason="Combat inspection unavailable: "+error.GetType().Name};}
            return result;
        }
    }

    internal static class NativeCombatOperations
    {
        internal static bool Valid(Operations.AttackTarget? command)=>command!=null && NativeDraftProtocol.ValidEntity(command.Pawn)
            && NativeDraftProtocol.ValidEntity(command.Target) && command.Pawn.EntityId!=command.Target.EntityId && command.HasMode
            && (command.Mode==Operations.AttackMode.Auto || command.Mode==Operations.AttackMode.Melee || command.Mode==Operations.AttackMode.Ranged);
        internal static bool CombatHealthAllows(float[] health,bool[] downed)=>health.Length>0 && health.Length==downed.Length
            && health.Select((value,index)=>!float.IsNaN(value) && !float.IsInfinity(value) && value>0.5005f && !downed[index]).All(value=>value);
        private static bool Health(Map map)
        {
            var colonists=map.mapPawns.FreeColonistsSpawned;
            if(colonists.Any(p=>p.health?.summaryHealth==null))return false;
            return CombatHealthAllows(colonists.Select(p=>p.health.summaryHealth.SummaryHealthPercent).ToArray(),colonists.Select(p=>p.Dead || p.Downed).ToArray());
        }
        internal static void ConfigureRangedJob(Job job,Verb verb,Pawn target)
        {
            // Exact ordinary Verse.Verb.OrderForceTarget weapon job shape.
            job.verbToUse=verb;job.targetA=target;job.endIfCantShootInMelee=true;
        }
        private static bool Ranged(Operations.AttackTarget command,Pawn pawn)=>command.Mode==Operations.AttackMode.Ranged
            || command.Mode==Operations.AttackMode.Auto && FloatMenuUtility.UseRangedAttack(pawn);
        private static bool Legal(Operations.AttackTarget command,Pawn pawn,Pawn target,out JobDef? definition,out Verb? verb)
        {
            definition=null;verb=null;
            if(pawn.WorkTagIsDisabled(WorkTags.Violent) || !pawn.Spawned || !target.Spawned || pawn.Map!=target.Map || target.Dead || target.Destroyed
                || command.RequireStanding && target.Downed || command.RequireHostile && !target.HostileTo(Faction.OfPlayer)
                || command.RequireCombatHealth && !Health(pawn.Map))return false;
            bool ranged=Ranged(command,pawn);
            if(ranged) {
                if(!FloatMenuUtility.UseRangedAttack(pawn) || pawn.equipment?.PrimaryEq?.PrimaryVerb==null
                    || pawn.skills?.GetSkill(SkillDefOf.Shooting)?.TotallyDisabled!=false
                    || !pawn.equipment.PrimaryEq.PrimaryVerb.CanHitTarget(target)
                    || FloatMenuUtility.GetRangedAttackAction(pawn,target,out _)==null)return false;
                verb=pawn.equipment.PrimaryEq.PrimaryVerb;
                if(!verb.verbProps.ai_IsWeapon || verb.verbProps.IsMeleeAttack || verb.CasterPawn!=pawn || !verb.Available())return false;
                var minimum=verb.verbProps.EffectiveMinRange(target,pawn);
                if(pawn.Position.DistanceToSquared(target.Position)<minimum*minimum && pawn.Position.AdjacentTo8WayOrInside(target.Position))return false;
                definition=JobDefOf.AttackStatic;
            } else {
                if(!pawn.CanReach(target,PathEndMode.Touch,Danger.Deadly) || pawn.meleeVerbs?.TryGetMeleeVerb(target)==null)return false;
                definition=JobDefOf.AttackMelee;
            }
            return definition!=null;
        }
        private static bool Resolve(Operations.AttackTarget command,Common.ObservationContext context,out NativeControlIdentity identity,
            out Pawn? pawn,out Pawn? target,out NativePawnSnapshot? snapshot,out JobDef? definition,out Verb? verb,out Common.Failure failure)
        {
            identity=new NativeControlIdentity(Current.Game,Find.CurrentMap,context.Identity.ColonyId,context.Identity.LoadToken);
            pawn=null;target=null;snapshot=null;definition=null;verb=null;
            failure=ProtoBoundary.Fail(Common.FailureCode.Unavailable,"Live canonical pawn snapshot and combat attribution hooks are required.");
            if(!NativePawnControlState.IsReady)return false;
            pawn=Find.CurrentMap.mapPawns.AllPawnsSpawned.SingleOrDefault(p=>p.GetUniqueLoadID()==command.Pawn.EntityId);
            target=Find.CurrentMap.mapPawns.AllPawnsSpawned.SingleOrDefault(p=>p.GetUniqueLoadID()==command.Target.EntityId);
            if(pawn==null){failure=ProtoBoundary.Fail(Common.FailureCode.NotFound,"Exact attacker is not spawned on this map.");return false;}
            if(target==null){failure=ProtoBoundary.Fail(Common.FailureCode.Unsupported,"This attack adapter requires a spawned pawn target with canonical pawn CAS.");return false;}
            bool ranged=Ranged(command,pawn);
            if(!(ranged?NativeRangedCausality.IsReady:NativeCombatCausality.IsReady))return false;
            if(ranged && (pawn.equipment?.PrimaryEq?.PrimaryVerb==null || !NativeRangedCausality.Supports(pawn.equipment.PrimaryEq.PrimaryVerb,pawn,target)))
            {failure=ProtoBoundary.Fail(Common.FailureCode.Unsupported,"Ranged attribution supports only verified ordinary pawn weapon direct bullets; this verb/projectile path is unsupported.");return false;}
            var check=NativePawnControlState.Check(identity,pawn,command.Pawn.ExpectedSnapshotToken,out snapshot);
            if(check!=NativePawnControlResult.Ready){failure=NativeDraftProtocol.Failure(check,context);return false;}
            check=NativePawnControlState.Check(identity,target,command.Target.ExpectedSnapshotToken,out _);
            if(check!=NativePawnControlResult.Ready){failure=NativeDraftProtocol.Failure(check,context);return false;}
            if(!Legal(command,pawn,target,out definition,out verb)){failure=ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,"Native attack weapon, reach, target or requested combat predicates refuse this order.");return false;}
            return true;
        }
        internal static Operations.ExecuteReply Execute(NativeOperationState state,Operations.ExecuteRequest request,Common.ObservationContext context)
        {
            var command=request.Operation.AttackTarget;var pre=request.Precondition;
            if(!Valid(command))return Refuse(Common.FailureCode.InvalidRequest,"Attack requires distinct exact attacker/target snapshots and explicit Auto, Melee or Ranged mode.");
            NativeAttemptLedger.Admission? handle=null;Authority.Owner? owner=null;Receipts.EffectEvidence? evidence=null;
            try {
                if(!Resolve(command,context,out var identity,out var pawn,out var target,out var before,out var definition,out var verb,out var failure))return new Operations.ExecuteReply {Failure=failure};
                if(!NativeControlAuthority.TryGetForGame(Current.Game,out var authority) || authority==null)return Refuse(Common.FailureCode.AuthorityRequired,"Current native authority is required.");
                var guard=authority.Check(pre.ExpectedGeneration,pre.LeaseId,pre.Attempt.ControllerSessionId);context.NativeGeneration=guard.Snapshot.Generation;
                if(!guard.Success)return new Operations.ExecuteReply {Failure=NativeAuthorityControlTools.Refusal(guard.Error,context)};
                owner=new Authority.Owner {ControllerSessionId=guard.Snapshot.Lease!.ControllerSessionId,PlayerDirection=guard.Snapshot.Lease.PlayerDirection};
                if(!NativeMovementOperations.Owns(before!,owner))return Refuse(Common.FailureCode.OwnerConflict,"Attack requires an eligible drafted attacker with the exact current owner's native claim.");
                var job=JobMaker.MakeJob(definition,target);job.killIncappedTarget=definition==JobDefOf.AttackMelee && target!.Downed;
                if(verb!=null)ConfigureRangedJob(job,verb,target!);
                if(!Resolve(command,context,out identity,out pawn,out target,out before,out var rechecked,out var recheckedVerb,out failure))return new Operations.ExecuteReply {Failure=failure};
                if(rechecked!=definition || !ReferenceEquals(recheckedVerb,verb) || !NativeMovementOperations.Owns(before!,owner))return Refuse(Common.FailureCode.OwnerConflict,"Attack prerequisites changed before admission.");
                guard=authority.Check(pre.ExpectedGeneration,pre.LeaseId,pre.Attempt.ControllerSessionId);
                if(!guard.Success)return new Operations.ExecuteReply {Failure=NativeAuthorityControlTools.Refusal(guard.Error,context)};
                var admission=state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute",request,context,owner);
                if(admission.Kind!=NativeAttemptLedger.DecisionKind.Admitted)return admission.Reply!;
                handle=admission.Handle!;
                var record=new NativeCombatRecord(identity,pawn!,target!,job,before!,context,command.RequireStanding,pre,authority);
                state.Combat.Add(pre.Attempt.Clone(),record);
                bool accepted=false;Exception? effectError=null;
                using(authority.Owned()) {
                    if(!Resolve(command,context,out var finalIdentity,out var finalPawn,out var finalTarget,out var checkedPawn,out rechecked,out recheckedVerb,out failure)
                        || !ReferenceEquals(finalPawn,pawn) || !ReferenceEquals(finalTarget,target) || rechecked!=definition || !ReferenceEquals(recheckedVerb,verb) || !NativeMovementOperations.Owns(checkedPawn!,owner))
                        throw new InvalidOperationException("Exact combat prerequisites changed after admission.");
                    guard=authority.Check(pre.ExpectedGeneration,pre.LeaseId,pre.Attempt.ControllerSessionId);
                    if(!guard.Success)throw new InvalidOperationException("Attack authority changed before native effect.");
                    record.BeginDispatch();
                    try{accepted=pawn!.jobs.TryTakeOrderedJob(job,JobTag.Misc);}catch(Exception error){effectError=error;}
                    finally{record.EndDispatch();}
                    if(NativePawnControlState.Observe(identity,pawn!,out var after)!=NativePawnControlResult.Ready || after==null)
                        throw new InvalidOperationException("Native attack readback is unavailable.");
                    bool correlated=record.Capture(after);evidence=record.Evidence(after,accepted,correlated);
                    if(effectError!=null || !accepted || !correlated)throw new InvalidOperationException("Attack job requires causal observation.",effectError);
                }
                return new Operations.ExecuteReply {Receipt=NativeOperationEnvelope.Applied(state.Ledger,handle,pre.Attempt,context,owner,evidence!)};
            }catch(Exception error){return handle==null?Refuse(Common.FailureCode.NativeFailure,"Attack validation failed: "+error.GetType().Name)
                :new Operations.ExecuteReply {Receipt=NativeOperationEnvelope.Uncertain(state.Ledger,handle,pre.Attempt,context,owner!,evidence!,"Admitted attack requires observation: "+error.GetType().Name)};}
        }
        internal static Operations.PreviewReply Preview(Operations.AttackTarget command,Common.ObservationContext context)
        {
            if(!Valid(command))return new Operations.PreviewReply {Failure=ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,"Attack requires exact snapshots and explicit supported mode.")};
            try {
                if(!Resolve(command,context,out _,out _,out _,out var snapshot,out var definition,out _,out var failure))return new Operations.PreviewReply {Failure=failure};
                bool accepted=snapshot!.Eligible && snapshot.Drafted && snapshot.Claim!=null;
                return NativeOperationEnvelope.Preview(new Operations.PreviewReply {Evaluated=new Operations.PreviewEvaluation {Context=context.Clone(),Accepted=accepted,
                    Reason=accepted?"Native attack predicates hold; execution requires matching current authority.":"An eligible attacker with existing native draft claim is required.",
                    Projected=new Receipts.EffectEvidence {Job=new Receipts.JobEffect {PawnId=snapshot.PawnId,JobDef=definition!.defName,
                        TargetA=new Receipts.JobTarget {ThingId=command.Target.EntityId},CanTry=accepted,Issued=false,Verified=false}}}});
            }catch(Exception error){return new Operations.PreviewReply {Failure=ProtoBoundary.Fail(Common.FailureCode.NativeFailure,"Attack preview failed: "+error.GetType().Name)};}
        }
        private static Operations.ExecuteReply Refuse(Common.FailureCode code,string detail)=>new Operations.ExecuteReply {Failure=ProtoBoundary.Fail(code,detail)};
    }
}
