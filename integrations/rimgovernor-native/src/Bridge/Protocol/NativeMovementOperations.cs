#nullable enable
using System;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Contracts.Wire.Common;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

namespace HomeBridge.BridgeTools
{
    internal static class NativeMovementOperations
    {
        // Drafts are plan-owned: an eligible drafted pawn takes orders;
        // the controller undrafts pawns no live plan needs.
        internal static bool Owns(NativePawnSnapshot snapshot)=>snapshot.Eligible && snapshot.Drafted;
        internal static bool Legal(Pawn pawn,IntVec3 cell)=>cell.InBounds(pawn.Map) && cell.Standable(pawn.Map) && !cell.Fogged(pawn.Map)
            && pawn.CanReach(cell,PathEndMode.OnCell,Danger.Deadly);
        // The order already matches: the pawn stands on the cell or its
        // current job is a Goto there.
        internal static bool Matches(Pawn pawn,IntVec3 cell)=>pawn.Position==cell
            || pawn.CurJob!=null && pawn.CurJob.def==JobDefOf.Goto && pawn.CurJob.targetA.Cell==cell;

        // Resolves the move's pawn and checks it is alive, spawned, owned and
        // drafted, and the cell standable and reachable; null when it applies.
        internal static Common.Failure? Resolve(Operations.MoveIntent? move,Common.ObservationContext context,out Pawn? pawn,out IntVec3 destination)
        {
            pawn=null;destination=IntVec3.Invalid;
            if(move==null || move.PawnId is null || !ProtoBoundary.IsIdentifier(move.PawnId) || move.Destination==null || move.Destination.X is null || move.Destination.Z is null)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,"A move requires a pawn id and explicit destination coordinates.");
            if(!NativePawnControlState.IsReady)return ProtoBoundary.Fail(Common.FailureCode.Unavailable,"Live native pawn control hooks are required.");
            destination=new IntVec3(move.Destination.X.GetValueOrDefault(),0,move.Destination.Z.GetValueOrDefault());
            var map=ProtoBoundary.LoadedMap(context);
            pawn=map.mapPawns.AllPawnsSpawned.SingleOrDefault(p=>p.GetUniqueLoadID()==(move.PawnId ?? ""));
            if(pawn==null || pawn.Dead)return ProtoBoundary.Fail(Common.FailureCode.NotFound,"The pawn is not alive and spawned on this map.");
            var identity=new NativeControlIdentity(Current.Game,map,(context.Identity?.ColonyId ?? ""),(context.Identity?.LoadToken ?? ""));
            var check=NativePawnControlState.Observe(identity,pawn,out var snapshot);
            if(check!=NativePawnControlResult.Ready || snapshot==null)return NativeDraftProtocol.Failure(check,context);
            if(!Owns(snapshot))return ProtoBoundary.Fail(Common.FailureCode.OwnerConflict,"A move requires an eligible drafted pawn.");
            if(Matches(pawn,destination))return null;
            if(!Legal(pawn,destination))return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,"The destination is not a standable, unfogged cell this pawn can reach.");
            return null;
        }

        internal static Receipts.EffectEvidence Evidence(Pawn pawn,IntVec3 destination,bool issued)=>new Receipts.EffectEvidence {Effect=new Receipts.JobEffect {
            PawnId=pawn.GetUniqueLoadID(),JobDef="Goto",Issued=issued,Verified=issued,Drafted=pawn.Drafted,
            TargetA=new Receipts.JobTarget {Target=new Common.Cell {X=destination.x,Z=destination.z}},
            VerifiedReason=issued?"Native Goto ordered.":"The order already matched; nothing issued."}};

        internal static Receipts.EffectEvidence Apply(Operations.MoveIntent move,Common.ObservationContext context)
        {
            var failure=Resolve(move,context,out var pawn,out var destination);
            if(failure!=null)throw new InvalidOperationException("Move prerequisites changed before apply: "+(failure.Detail ?? ""));
            if(Matches(pawn!,destination))return Evidence(pawn!,destination,false);
            var job=JobMaker.MakeJob(JobDefOf.Goto,destination);
            if(!pawn!.jobs.TryTakeOrderedJob(job,JobTag.Misc))throw new InvalidOperationException("Native Goto order was not taken.");
            return Evidence(pawn,destination,true);
        }
    }

    internal sealed class MoveActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action,Common.ObservationContext context)=>
            NativeMovementOperations.Resolve(action.Intent?.Move,context,out _,out _);
        public Receipts.EffectEvidence Apply(Operations.Action action,Common.ObservationContext context)=>
            NativeMovementOperations.Apply(action.Intent?.Move ?? throw new InvalidOperationException("Action carries no move intent."),context);
    }
}
