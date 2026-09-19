#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    internal enum NativeMovementPhase { Unknown, Interrupted, Pending, Completed }
    internal sealed class NativeMovementRecord
    {
        private readonly NativeControlIdentity identity;
        private readonly Pawn pawn;
        private readonly IntVec3 destination;
        private readonly Job job;
        private readonly int jobId;
        private readonly NativeDraftClaim claim;
        private readonly NativePawnSnapshot before;
        private readonly Common.ObservationContext admitted;
        private ulong? order;
        private bool started;
        private bool noChange;
        internal NativeMovementRecord(NativeControlIdentity identity,Pawn pawn,IntVec3 destination,Job job,NativePawnSnapshot before,Common.ObservationContext context)
        {this.identity=identity;this.pawn=pawn;this.destination=destination;this.job=job;jobId=job.loadID;this.before=before;claim=before.Claim!;admitted=context.Clone();}
        internal static NativeMovementPhase Classify(bool correlated,bool unchanged,bool current,bool queued,bool started,bool arrived)
        {
            if(!correlated)return NativeMovementPhase.Unknown;
            if(!unchanged)return NativeMovementPhase.Interrupted;
            // A queued order has not moved the pawn, including when another job reaches this cell.
            if(queued)return NativeMovementPhase.Pending;
            if(started && arrived)return NativeMovementPhase.Completed;
            if(current)return NativeMovementPhase.Pending;
            return NativeMovementPhase.Unknown;
        }
        internal void AlreadyAtDestination(NativePawnSnapshot snapshot)
        {order=snapshot.Facts.OrderRevision;started=true;noChange=true;}
        // RimWorld pools Job instances (JobMaker.ReturnToPool): once the issued
        // Goto ends, the very same object can come back as this pawn's next job
        // (a Wait_Combat after arrival, say) under a new loadID, so reference
        // identity alone never proves the issued order is still current or queued.
        private bool Live()=>job.loadID==jobId && job.def==JobDefOf.Goto && job.targetA.Cell==destination;
        private bool Current()=>Live() && ReferenceEquals(pawn.CurJob,job);
        private bool Queued()=>Live() && pawn.jobs.jobQueue.Any(q=>ReferenceEquals(q.job,job));
        internal bool Capture(NativePawnSnapshot snapshot,NativePawnSnapshot before)
        {
            if(before.Facts.OrderRevision==ulong.MaxValue || snapshot.Facts.OrderRevision!=before.Facts.OrderRevision+1
                || snapshot.Facts.DraftRevision!=before.Facts.DraftRevision)return false;
            if(snapshot.Claim?.ClaimId!=claim.ClaimId)return false;
            bool current=Current(),queued=Queued();
            if(!current && !queued)return false;
            order=snapshot.Facts.OrderRevision;started=current;return true;
        }
        internal Receipts.EffectEvidence Evidence(NativePawnSnapshot snapshot,bool issued,bool verified)
        {
            var effect=new Receipts.JobEffect {PawnId=snapshot.PawnId,
                TargetA=new Receipts.JobTarget {Cell=new Common.Cell {X=destination.x,Z=destination.z}},Issued=issued,Verified=verified,
                VerifiedReason=verified?"Exact issued native job and owned draft claim observed.":"Issued job outcome requires observation.",
                Drafted=snapshot.Drafted,ResultingSnapshotToken=snapshot.Token};
            if(snapshot.Claim?.ClaimId==claim.ClaimId)
            {effect.DraftClaimId=claim.ClaimId;}
            if(!noChange){effect.JobId=jobId;effect.JobDef="Goto";}
            else effect.VerifiedReason="Exact destination already observed; no movement job issued.";
            return new Receipts.EffectEvidence {Job=effect};
        }
        internal Receipts.Progress Observe(Common.AttemptKey attempt,Common.ObservationContext context)
        {
            var result=new Receipts.Progress {Attempt=attempt.Clone(),Context=context.Clone(),CompleteInspection=false};
            try {
                if(!context.Identity.Equals(admitted.Identity) || context.Tick<admitted.Tick ||
                    NativePawnControlState.Observe(identity,pawn,out var snapshot)!=NativePawnControlResult.Ready || snapshot==null)
                    throw new InvalidOperationException("Current movement pawn context cannot be inspected.");
                if(!order.HasValue)Capture(snapshot,before);
                bool current=Current(),queued=Queued();
                var changed=new List<string>();
                // The authority generation is not compared: a hold and a same-world resume
                // bump it while the pawn keeps its draft, claim and order (#342, #318, #228).
                // Claim identity and the native order revision are the continuity evidence.
                if(!snapshot.Eligible)changed.Add("eligibility");
                if(!snapshot.Drafted)changed.Add("draft");
                if(snapshot.Claim?.ClaimId!=claim.ClaimId)changed.Add("claim");
                if(snapshot.Facts.OrderRevision!=order)changed.Add("native order revision ("+snapshot.Facts.OrderRevision+" vs "+order+")");
                bool unchanged=changed.Count==0;
                if(current && unchanged)started=true;
                var phase=Classify(order.HasValue,unchanged,current,queued,started,pawn.Position==destination);
                var evidence=Evidence(snapshot,false,phase==NativeMovementPhase.Completed || phase==NativeMovementPhase.Pending);
                if(phase==NativeMovementPhase.Pending){result.CompleteInspection=true;result.Pending=new Receipts.PendingEffect {Evidence=evidence};}
                else if(phase==NativeMovementPhase.Completed){result.CompleteInspection=true;result.Completed=new Receipts.CompletedEffect {Evidence=evidence};}
                else if(phase==NativeMovementPhase.Interrupted){result.CompleteInspection=true;result.Unsuccessful=new Receipts.UnsuccessfulEffect {
                    Reason=Receipts.UnsuccessfulReason.Interrupted,Evidence=evidence,Detail="Changed since admission: "+string.Join(", ",changed.ToArray())+"."};}
                else result.Unknown=new Receipts.UnknownEffect {Reason="The exact issued job has no verified current movement or arrival evidence."};
            } catch(Exception error) {result.CompleteInspection=false;result.Unknown=new Receipts.UnknownEffect {Reason="Movement inspection unavailable: "+error.GetType().Name};}
            return result;
        }
    }

    internal static class NativeMovementOperations
    {
        internal static bool Valid(Operations.MovePawn? command)=>command!=null && NativeDraftProtocol.ValidEntity(command.Pawn)
            && command.Destination!=null && command.Destination.HasX && command.Destination.HasZ;
        // Since there is only ever one bot process (see #52), a claim's mere
        // existence is proof of ownership; no caller-supplied owner token remains.
        internal static bool Owns(NativePawnSnapshot snapshot)=>snapshot.Eligible && snapshot.Drafted && snapshot.Claim!=null;
        private static bool Prepare(Operations.MovePawn command,Common.ObservationContext context,out NativeControlIdentity identity,
            out Pawn? pawn,out NativePawnSnapshot? snapshot,out IntVec3 destination,out Common.Failure failure)
        {
            identity=new NativeControlIdentity(Current.Game, ProtoBoundary.LoadedMap(context),context.Identity.ColonyId,context.Identity.LoadToken);
            pawn=null;snapshot=null;destination=new IntVec3(command.Destination.X,0,command.Destination.Z);
            failure=ProtoBoundary.Fail(Common.FailureCode.Unavailable,"Live native pawn control hooks are required.");
            if(!NativePawnControlState.IsReady)return false;
            pawn=ProtoBoundary.LoadedMap(context).mapPawns.AllPawnsSpawned.SingleOrDefault(p=>p.GetUniqueLoadID()==command.Pawn.EntityId);
            if(pawn==null){failure=ProtoBoundary.Fail(Common.FailureCode.NotFound,"Exact pawn is not spawned on this map.");return false;}
            var check=NativePawnControlState.Check(identity,pawn,command.Pawn.ExpectedSnapshotToken,out snapshot);
            if(check!=NativePawnControlResult.Ready){failure=NativeDraftProtocol.Failure(check,context);return false;}
            if(!Legal(pawn,destination)) {failure=ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,"Exact destination is not a standable, unfogged, normally reachable cell for this pawn.");return false;}
            return true;
        }
        private static bool Legal(Pawn pawn,IntVec3 cell)=>cell.InBounds(pawn.Map) && cell.Standable(pawn.Map) && !cell.Fogged(pawn.Map)
            && pawn.CanReach(cell,PathEndMode.OnCell,Danger.Deadly);
        internal static Operations.ExecuteReply Execute(NativeOperationState state,Operations.ExecuteRequest request,Common.ObservationContext context)
        {
            var command=request.Operation.MovePawn;var pre=request.Precondition;
            if(!Valid(command))return Refuse(Common.FailureCode.InvalidRequest,"Move requires exact pawn snapshot and explicit destination coordinates.");
            NativeAttemptLedger.Admission? handle=null;Receipts.EffectEvidence? evidence=null;
            try {
                if(!Prepare(command,context,out var identity,out var pawn,out var snapshot,out var destination,out var failure))return new Operations.ExecuteReply {Failure=failure};
                if(!NativeControlAuthority.TryGetForGame(Current.Game,out var authority) || authority==null)return Refuse(Common.FailureCode.AuthorityRequired,"Current native authority is required.");
                var guard=authority.Check(pre.ExpectedGeneration);
                context.NativeGeneration=guard.Snapshot.Generation;
                if(!guard.Success)return new Operations.ExecuteReply {Failure=NativeAuthorityControlTools.Refusal(guard.Error,context)};
                if(!Owns(snapshot!))return Refuse(Common.FailureCode.OwnerConflict,"Move requires an eligible drafted pawn with an existing native claim.");
                // Resolve a real native job before reserving an attempt; no effect has run.
                var job=JobMaker.MakeJob(JobDefOf.Goto,destination);
                if(job==null || job.def!=JobDefOf.Goto || job.targetA.Cell!=destination)return Refuse(Common.FailureCode.NativeFailure,"Native Goto job could not be prepared.");
                guard=authority.Check(pre.ExpectedGeneration);
                if(!guard.Success)return new Operations.ExecuteReply {Failure=NativeAuthorityControlTools.Refusal(guard.Error,context)};
                if(NativePawnControlState.Check(identity,pawn!,command.Pawn.ExpectedSnapshotToken,out snapshot)!=NativePawnControlResult.Ready || !Owns(snapshot!))
                    return Refuse(Common.FailureCode.OwnerConflict,"Pawn snapshot or owned claim changed before admission.");
                var admission=state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute",request,context);
                if(admission.Kind!=NativeAttemptLedger.DecisionKind.Admitted)return admission.DecidedReply;
                handle=admission.AdmittedHandle;
                var record=new NativeMovementRecord(identity,pawn!,destination,job,snapshot!,context);
                state.Movements.Add(pre.Attempt.Clone(),record);
                bool accepted=false;Exception? effectError=null;
                using(authority.Owned()) {
                    if(!NativePawnControlState.IsReady || NativePawnControlState.Check(identity,pawn!,command.Pawn.ExpectedSnapshotToken,out snapshot)!=NativePawnControlResult.Ready
                        || !Owns(snapshot!) || !Legal(pawn!,destination))throw new InvalidOperationException("Movement prerequisites changed after admission.");
                    guard=authority.Check(pre.ExpectedGeneration);
                    if(!guard.Success)throw new InvalidOperationException("Movement authority changed before native effect.");
                    if(pawn!.Position==destination) {
                        record.AlreadyAtDestination(snapshot!);
                        evidence=record.Evidence(snapshot!,false,true);
                        var candidate=new Receipts.Receipt {Attempt=pre.Attempt,AdmittedContext=context,
                            NoChange=new Receipts.NoChange {Observed=evidence,Detail="Pawn already occupies the exact destination."}};
                        if(!NativeOperationEnvelope.Fits(new Operations.ExecuteReply {Receipt=candidate}))throw new InvalidOperationException("Movement no-change evidence is not encodable.");
                        return new Operations.ExecuteReply {Receipt=state.Ledger.FinishNoChange(handle,evidence,candidate.NoChange.Detail)};
                    }
                    var before=snapshot!;
                    try {accepted=pawn!.jobs.TryTakeOrderedJob(job,JobTag.Misc);}
                    catch(Exception error){effectError=error;}
                    if(NativePawnControlState.Observe(identity,pawn!,out snapshot)!=NativePawnControlResult.Ready || snapshot==null)
                        throw new InvalidOperationException("Native movement readback unavailable.");
                    bool correlated=record.Capture(snapshot,before);
                    evidence=record.Evidence(snapshot,accepted,correlated);
                    if(effectError!=null || !accepted || !correlated)throw new InvalidOperationException("Native movement requires causal observation.",effectError);
                }
                return new Operations.ExecuteReply {Receipt=NativeOperationEnvelope.Applied(state.Ledger,handle,pre.Attempt,context,evidence)};
            } catch(Exception error) {
                if(handle==null)return Refuse(Common.FailureCode.NativeFailure,"Movement validation failed: "+error.GetType().Name);
                return new Operations.ExecuteReply {Receipt=NativeOperationEnvelope.Uncertain(state.Ledger,handle,pre.Attempt,context,evidence,"Admitted movement requires observation: "+error.GetType().Name)};
            }
        }
        internal static Operations.PreviewReply Preview(Operations.MovePawn command,Common.ObservationContext context)
        {
            if(!Valid(command))return new Operations.PreviewReply {Failure=ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,"Move requires exact pawn snapshot and explicit destination coordinates.")};
            try {
                if(!Prepare(command,context,out _,out _,out var snapshot,out var destination,out var failure))return new Operations.PreviewReply {Failure=failure};
                bool accepted=snapshot!.Eligible && snapshot.Drafted && snapshot.Claim!=null;
                return NativeOperationEnvelope.Preview(new Operations.PreviewReply {Evaluated=new Operations.PreviewEvaluation {Context=context.Clone(),Accepted=accepted,
                    Reason=accepted?"Exact reachable destination; execution requires authority matching the current draft claim.":"An eligible pawn with an existing native draft claim is required.",
                    Projected=new Receipts.EffectEvidence {Job=new Receipts.JobEffect {PawnId=snapshot.PawnId,JobDef="Goto",CanTry=accepted,Issued=false,Verified=false,
                        TargetA=new Receipts.JobTarget {Cell=new Common.Cell {X=destination.x,Z=destination.z}}}}}});
            } catch(Exception error){return new Operations.PreviewReply {Failure=ProtoBoundary.Fail(Common.FailureCode.NativeFailure,"Movement preview failed: "+error.GetType().Name)};}
        }
        private static Operations.ExecuteReply Refuse(Common.FailureCode code,string detail)=>new Operations.ExecuteReply {Failure=ProtoBoundary.Fail(code,detail)};
    }
}
