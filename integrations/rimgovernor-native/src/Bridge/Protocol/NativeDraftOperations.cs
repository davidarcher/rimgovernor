#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    internal sealed class NativeDraftRecord
    {
        private readonly NativeControlIdentity identity;
        private readonly Pawn pawn;
        private readonly Common.ObservationContext admitted;
        private readonly bool wanted;
        private NativePawnSnapshot? verified;
        private NativeDraftClaim? releasedClaim;
        internal NativeDraftRecord(NativeControlIdentity identity, Pawn pawn, Common.ObservationContext admitted, bool wanted)
        { this.identity=identity;this.pawn=pawn;this.admitted=admitted.Clone();this.wanted=wanted; }
        internal void Confirm(NativePawnSnapshot snapshot,NativeDraftClaim? priorClaim=null) { verified=snapshot;releasedClaim=priorClaim; }
        internal Receipts.Progress Observe(Common.AttemptKey attempt,Common.ObservationContext context)
        {
            var result=new Receipts.Progress {Attempt=attempt.Clone(),Context=context.Clone(),CompleteInspection=false};
            if(!context.Identity.Equals(admitted.Identity) || !context.HasTick || context.Tick<admitted.Tick || verified==null) {
                result.Unknown=new Receipts.UnknownEffect {Reason="No causally verified draft outcome is available in this observation context."};return result;
            }
            var read=NativePawnControlState.Observe(identity,pawn,out var current);
            if(read!=NativePawnControlResult.Ready || current==null) {
                result.Unknown=new Receipts.UnknownEffect {Reason="Current pawn/claim could not be inspected: "+read};return result;
            }
            result.CompleteInspection=true;
            bool matches=Matches(wanted,verified,current);
            var correlated=matches ? (wanted?current.Claim:releasedClaim) : null;
            var effect=NativeDraftProtocol.Effect(current.PawnId,current.Drafted,current.Token,false,matches,correlated?.ClaimId);
            if(matches) result.Completed=new Receipts.CompletedEffect {Evidence=new Receipts.EffectEvidence {Job=effect}};
            else result.Unsuccessful=new Receipts.UnsuccessfulEffect {Reason=Receipts.UnsuccessfulReason.Interrupted,
                Evidence=new Receipts.EffectEvidence {Job=effect},Detail="The current pawn state no longer matches the verified owned draft outcome."};
            return result;
        }
        // A later draft setter/order invalidates the claim. Matching a bool alone
        // cannot attribute a replacement draft to the original operation: the exact
        // claim ID (or, on release, the exact token) must still agree with what this
        // operation verified, so any other pawn order that redrafts or releases the
        // pawn in between is reported Interrupted, not Completed.
        internal static bool Matches(bool wanted,NativePawnSnapshot verified,NativePawnSnapshot current) =>
            wanted ? current.Drafted && current.Claim!=null && verified.Claim!=null
                && current.Claim.ClaimId==verified.Claim.ClaimId
                : !current.Drafted && current.Claim==null && current.Token==verified.Token;
    }

    internal static class NativeDraftOperations
    {
        internal static Operations.ExecuteReply Execute(NativeOperationState state,Operations.ExecuteRequest request,Common.ObservationContext context)
        {
            var command=request.Operation.SetDrafted;var precondition=request.Precondition;
            if(!NativeDraftProtocol.Validate(command,out var failure)) return new Operations.ExecuteReply {Failure=failure};
            NativeAttemptLedger.Admission? admitted=null;
            Common.ObservationContext? admittedContext=null;
            try {
                if(!Resolve(command.Pawn,context,out var identity,out var pawn,out var before,out failure))
                    return new Operations.ExecuteReply {Failure=failure};
                if(!NativeControlAuthority.TryGetForGame(Current.Game,out var authority) || authority==null)
                    return Refuse(Common.FailureCode.AuthorityRequired,"Native authority is required.");
                var guard=authority.Check(precondition.ExpectedGeneration);
                if(!guard.Success) return new Operations.ExecuteReply {Failure=NativeAuthorityControlTools.Refusal(guard.Error,context)};
                if(!Eligible(command,before!,out failure)) return new Operations.ExecuteReply {Failure=failure};
                if(!NativePawnControlState.IsReady) return Refuse(Common.FailureCode.Unavailable,"Native pawn control hooks are unavailable.");
                guard=authority.Check(precondition.ExpectedGeneration);
                context.NativeGeneration=guard.Snapshot.Generation;
                if(!guard.Success) return new Operations.ExecuteReply {Failure=NativeAuthorityControlTools.Refusal(guard.Error,context)};
                var check=NativePawnControlState.Check(identity!,pawn!,command.Pawn.ExpectedSnapshotToken,out before);
                if(check!=NativePawnControlResult.Ready) return new Operations.ExecuteReply {Failure=NativeDraftProtocol.Failure(check,context)};
                var admissionContext=context.Clone();
                var admission=state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute",request,admissionContext);
                if(admission.Kind!=NativeAttemptLedger.DecisionKind.Admitted) return admission.DecidedReply;
                admitted=admission.AdmittedHandle;admittedContext=admissionContext;
                return Apply(state,request,admission.AdmittedHandle,admissionContext,authority,identity!,pawn!,before!);
            }
            catch(Exception error) {
                return admitted==null ? Refuse(Common.FailureCode.NativeFailure,"Draft validation failed: "+error.GetType().Name)
                    : Uncertain(state,admitted,precondition.Attempt,admittedContext!,null,"Admitted draft requires observation: "+error.GetType().Name);
            }
        }

        private static Operations.ExecuteReply Apply(NativeOperationState state,Operations.ExecuteRequest request,NativeAttemptLedger.Admission admission,
            Common.ObservationContext context,NativeControlAuthority authority,NativeControlIdentity identity,Pawn pawn,NativePawnSnapshot before)
        {
            var command=request.Operation.SetDrafted;var pre=request.Precondition;
            var record=new NativeDraftRecord(identity,pawn,context,command.Drafted);
            state.Drafts.Add(pre.Attempt.Clone(),record);
            NativePawnSnapshot? after=null;Exception? effectError=null;bool issued=false;bool verified=false;
            try {
                using(authority.Owned()) {
                    if(command.Drafted && before.Drafted) {
                        var unchanged=NativePawnControlState.Check(identity,pawn,command.Pawn.ExpectedSnapshotToken,out after);
                        if(unchanged!=NativePawnControlResult.Ready || after==null || !Eligible(command,after,out _))
                            throw new InvalidOperationException("Owned draft changed after admission.");
                        record.Confirm(after);
                        var unchangedEvidence=new Receipts.EffectEvidence {Job=NativeDraftProtocol.Effect(after.PawnId,true,after.Token,false,true,after.Claim!.ClaimId)};
                        var candidate=new Receipts.Receipt {Attempt=pre.Attempt,AdmittedContext=context,
                            NoChange=new Receipts.NoChange {Observed=unchangedEvidence,Detail="The exact current claim already owns this draft."}};
                        if(!NativeOperationEnvelope.Fits(new Operations.ExecuteReply {Receipt=candidate})) throw new InvalidOperationException("Draft receipt cannot be encoded.");
                        return new Operations.ExecuteReply {Receipt=state.Ledger.FinishNoChange(admission,unchangedEvidence,candidate.NoChange.Detail)};
                    }
                    NativeDraftClaimTicket? claim=null;NativeDraftReleaseTicket? release=null;
                    var prepared=command.Drafted
                        ? NativePawnControlState.PrepareClaim(identity,pawn,command.Pawn.ExpectedSnapshotToken,out claim,out after)
                        : NativePawnControlState.PrepareRelease(identity,pawn,command.Pawn.ExpectedSnapshotToken,before.Claim!.ClaimId,out release,out after);
                    if(prepared!=NativePawnControlResult.Ready) throw new InvalidOperationException("Draft preparation changed after admission: "+prepared);
                    try {
                        var current=authority.Check(pre.ExpectedGeneration);
                        if(!current.Success || !NativePawnControlState.IsReady) throw new InvalidOperationException("Draft authority or hook health changed before effect.");
                        if(NativePawnControlState.Check(identity,pawn,command.Pawn.ExpectedSnapshotToken,out _)!=NativePawnControlResult.Ready)
                            throw new InvalidOperationException("Pawn snapshot changed before effect.");
                        // No await or secondary operation may occur between guard and setter.
                        current=authority.Check(pre.ExpectedGeneration);
                        if(!current.Success) throw new InvalidOperationException("Draft authority expired before effect.");
                        issued=true;pawn.drafter.Drafted=command.Drafted;
                    }
                    catch(Exception error) {effectError=error;}
                    var completed=command.Drafted ? NativePawnControlState.CompleteClaim(claim!,out after)
                        : NativePawnControlState.CompleteRelease(release!,out after);
                    verified=completed==NativePawnControlResult.Ready && after!=null && after.Drafted==command.Drafted;
                    if(verified) record.Confirm(after!,command.Drafted?null:before.Claim);
                    if(!verified && effectError==null) effectError=new InvalidOperationException("Draft outcome could not be certified: "+completed);
                }
                var job=after==null?null:NativeDraftProtocol.Effect(after.PawnId,after.Drafted,after.Token,issued,verified,
                    command.Drafted?after.Claim?.ClaimId:before.Claim?.ClaimId);
                var evidence=job==null?null:new Receipts.EffectEvidence {Job=job};
                if(effectError!=null || !verified) return Uncertain(state,admission,pre.Attempt,context,evidence,"Admitted draft requires observation: "+(effectError?.GetType().Name??"unverified"));
                return new Operations.ExecuteReply {Receipt=NativeOperationEnvelope.Applied(state.Ledger,admission,pre.Attempt,context,evidence!)};
            }
            catch(Exception error) {
                var evidence=after==null?null:new Receipts.EffectEvidence {Job=NativeDraftProtocol.Effect(after.PawnId,after.Drafted,after.Token,issued,false,after.Claim?.ClaimId)};
                return Uncertain(state,admission,pre.Attempt,context,evidence,"Admitted draft requires observation: "+error.GetType().Name);
            }
        }

        internal static Operations.PreviewReply Preview(Operations.SetDrafted command,Common.ObservationContext context)
        {
            if(!NativeDraftProtocol.Validate(command,out var failure)) return new Operations.PreviewReply {Failure=failure};
            try {
                if(!Resolve(command.Pawn,context,out _,out _,out var snapshot,out failure)) return new Operations.PreviewReply {Failure=failure};
                bool accepted=command.Drafted ? snapshot!.Eligible && (!snapshot.Drafted || snapshot.Claim!=null) : snapshot!.Drafted && snapshot.Claim!=null;
                // ExpectedDraftOwner's post-refactor semantics are not documented on the wire;
                // treated here as an expected claim ID, the only remaining ownership token a
                // claim carries once Authority.Owner was removed (mirrors ExpectedClaimId on
                // ReleaseOwnedDraftRequest). Flagged as an interpretive, unconfirmed choice.
                if(command.HasExpectedDraftOwner && snapshot.Claim?.ClaimId!=command.ExpectedDraftOwner) accepted=false;
                return NativeOperationEnvelope.Preview(new Operations.PreviewReply {Evaluated=new Operations.PreviewEvaluation {
                    Context=context.Clone(),Accepted=accepted,Reason=accepted?"Execution still requires current matching authority.":"Native pawn eligibility or draft claim does not match.",
                    Projected=new Receipts.EffectEvidence {Job=new Receipts.JobEffect {PawnId=snapshot.PawnId,Drafted=command.Drafted,CanTry=accepted,Issued=false,Verified=false}}}});
            }
            catch(Exception error) {return new Operations.PreviewReply {Failure=ProtoBoundary.Fail(Common.FailureCode.NativeFailure,"Draft preview failed: "+error.GetType().Name)};}
        }

        internal static Operations.ReleaseOwnedDraftReply Release(Operations.ReleaseOwnedDraftRequest request)
        {
            if(!NativeDraftProtocol.ValidateRelease(request,out var failure)) return new Operations.ReleaseOwnedDraftReply {Failure=failure};
            if(!ProtoBoundary.ValidateIdentity(request.Identity, out var context,out failure))
                return new Operations.ReleaseOwnedDraftReply {Failure=failure};
            NativeDraftReleaseTicket? ticket=null;NativePawnSnapshot? observed=null;bool admitted=false;
            try {
                var identity=new NativeControlIdentity(Current.Game, ProtoBoundary.LoadedMap(context),context.Identity.ColonyId,context.Identity.LoadToken);
                var pawn=FindPawn(ProtoBoundary.LoadedMap(context),request.Pawn.EntityId);
                if(pawn==null) return new Operations.ReleaseOwnedDraftReply {Failure=ProtoBoundary.Fail(Common.FailureCode.NotFound,"Exact pawn is not spawned on the current map.")};
                var prepared=NativePawnControlState.PrepareRelease(identity,pawn,request.Pawn.ExpectedSnapshotToken,request.ExpectedClaimId,out ticket,out observed);
                if(prepared==NativePawnControlResult.AlreadyReleased && observed!=null)
                    return new Operations.ReleaseOwnedDraftReply {AlreadyReleased=Released(request,context,observed,false)};
                if(prepared==NativePawnControlResult.Uncertain) return ReleaseUncertain(request,context,"The exact cleanup was previously admitted and is still uncertain; no second setter was issued.");
                if(prepared!=NativePawnControlResult.Ready) return new Operations.ReleaseOwnedDraftReply {Failure=NativeDraftProtocol.Failure(prepared,context)};
                admitted=true;
                // Foundation stores the exact pending cleanup before this setter;
                // no ordinary attempt slot or active lease is required for cleanup.
                // Releasing the bot's own claim is still the bot's action: under
                // active authority the setter runs in an owned scope so the draft
                // hook does not read the undraft as player control and revoke.
                Exception? effectError=null;
                try {
                    if(!NativePawnControlState.IsReady || NativePawnControlState.Check(identity,pawn,request.Pawn.ExpectedSnapshotToken,out _)!=NativePawnControlResult.Ready)
                        throw new InvalidOperationException("Pawn cleanup state changed before effect.");
                    using(CleanupScope()) pawn.drafter.Drafted=false;
                }
                catch(Exception error) {effectError=error;}
                var completed=NativePawnControlState.CompleteRelease(ticket!,out observed);
                var sameContext=ProtoBoundary.ValidateIdentity(request.Identity, out var afterContext,out _);
                if(afterContext!=null) context=afterContext;
                if(effectError!=null || completed!=NativePawnControlResult.Ready || observed==null || !sameContext)
                    return ReleaseUncertain(request,context,"Admitted cleanup requires observation: "+(effectError?.GetType().Name??completed.ToString()));
                return new Operations.ReleaseOwnedDraftReply {Released=Released(request,context,observed,true)};
            }
            catch(Exception error) {
                return admitted ? ReleaseUncertain(request,context,"Admitted cleanup requires observation: "+error.GetType().Name)
                    : new Operations.ReleaseOwnedDraftReply {Failure=ProtoBoundary.Fail(Common.FailureCode.NativeFailure,"Cleanup validation failed: "+error.GetType().Name)};
            }
        }

        private static IDisposable? CleanupScope()
        {
            if(!NativeControlAuthority.TryGetForGame(Current.Game,out var authority) || authority==null) return null;
            var status=authority.Status();
            return status.Available && status.Active ? authority.Owned() : null;
        }

        private static Operations.DraftRelease Released(Operations.ReleaseOwnedDraftRequest request,Common.ObservationContext context,NativePawnSnapshot snapshot,bool issued)
        {
            var result=new Operations.DraftRelease {Request=request.Clone(),Context=context.Clone(),
                Observed=NativeDraftProtocol.Effect(snapshot.PawnId,snapshot.Drafted,snapshot.Token,issued,true,request.ExpectedClaimId)};
            if(snapshot.Drafted || snapshot.Claim!=null || !NativeOperationEnvelope.Fits(new Operations.ReleaseOwnedDraftReply {Released=result}))
                throw new InvalidOperationException("Cleanup readback is not a complete encodable undraft.");
            return result;
        }
        private static Operations.ReleaseOwnedDraftReply ReleaseUncertain(Operations.ReleaseOwnedDraftRequest request,Common.ObservationContext context,string detail) =>
            new Operations.ReleaseOwnedDraftReply {Uncertain=new Operations.DraftReleaseUncertain {Request=request.Clone(),Context=context.Clone(),Detail=detail}};

        private static bool Resolve(Operations.EntityPrecondition target,Common.ObservationContext context,[NotNullWhen(true)] out NativeControlIdentity? identity,
            out Pawn? pawn,out NativePawnSnapshot? snapshot,out Common.Failure failure)
        {
            identity=null;pawn=null;snapshot=null;
            failure=ProtoBoundary.Fail(Common.FailureCode.Unavailable,"Native pawn control hooks are unavailable.");
            if(!NativePawnControlState.IsReady) return false;
            identity=new NativeControlIdentity(Current.Game, ProtoBoundary.LoadedMap(context),context.Identity.ColonyId,context.Identity.LoadToken);
            pawn=FindPawn(ProtoBoundary.LoadedMap(context),target.EntityId);
            if(pawn==null) {failure=ProtoBoundary.Fail(Common.FailureCode.NotFound,"Exact pawn is not spawned on the current map.");return false;}
            var check=NativePawnControlState.Check(identity,pawn,target.ExpectedSnapshotToken,out snapshot);
            if(check!=NativePawnControlResult.Ready) {failure=NativeDraftProtocol.Failure(check,context);return false;}
            return true;
        }
        private static Pawn? FindPawn(Map map,string id)
        {
            var matching=map.mapPawns.AllPawnsSpawned.Where(p=>p.GetUniqueLoadID()==id).Take(2).ToArray();
            if(matching.Length>1) throw new InvalidOperationException("Native pawn ID is ambiguous.");
            return matching.SingleOrDefault();
        }
        // Ownership is now proven purely by claim existence: with exactly one
        // bot actor, any live NativeDraftClaim on the pawn was created by this
        // adapter under NativeControlAuthority's Owned() scope, so there is no
        // separate owner token left to compare (mirrors NativeMovementOperations.Owns()).
        internal static bool Eligible(Operations.SetDrafted command,NativePawnSnapshot snapshot,out Common.Failure failure)
        {
            failure=ProtoBoundary.Fail(Common.FailureCode.OwnerConflict,"Draft claim does not match the exact current admitted claim.");
            if(command.HasExpectedDraftOwner && snapshot.Claim?.ClaimId!=command.ExpectedDraftOwner) return false;
            if(snapshot.Drafted && snapshot.Claim==null) return false;
            if(!command.Drafted) return snapshot.Drafted && snapshot.Claim!=null;
            if(!snapshot.Eligible) {failure=ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,"Pawn is not currently eligible for ordinary native drafting.");return false;}
            return !snapshot.Drafted || snapshot.Claim!=null;
        }
        private static Operations.ExecuteReply Refuse(Common.FailureCode code,string detail)=>new Operations.ExecuteReply {Failure=ProtoBoundary.Fail(code,detail)};
        private static Operations.ExecuteReply Uncertain(NativeOperationState state,NativeAttemptLedger.Admission admission,Common.AttemptKey attempt,
            Common.ObservationContext context,Receipts.EffectEvidence? evidence,string detail)=>new Operations.ExecuteReply {
                Receipt=NativeOperationEnvelope.Uncertain(state.Ledger,admission,attempt,context,evidence,detail)};
    }
}
