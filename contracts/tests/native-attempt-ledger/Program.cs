using System;
using System.Linq;
using System.Threading;
using Google.Protobuf;
using HomeBridge.BridgeTools;
using Common = RimGovernor.Protocol.Common;
using Authority = RimGovernor.Protocol.Authority;
using Operations = RimGovernor.Protocol.Operations;
using Placement = RimGovernor.Protocol.Placement;
using Receipts = RimGovernor.Protocol.Receipts;
using Kind = HomeBridge.BridgeTools.NativeAttemptLedger.DecisionKind;

internal static class Program
{
    private const string Method = "rimgovernor.operations.v1.Operations/Execute";
    private static int checks;
    private static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }
    private static void Throws(Action action, string name)
    {
        bool threw=false;
        try { action(); } catch(ArgumentException) { threw=true; } catch(InvalidOperationException) { threw=true; }
        Check(threw,name);
    }
    private static Common.Identity Identity(int map=0) => new Common.Identity { ColonyId="colony",LoadToken="load",MapId=map };
    private static Common.ObservationContext Context(int map=0) => new Common.ObservationContext { Identity=Identity(map),Tick=0,NativeGeneration=3 };
    private static Authority.Owner Owner() => new Authority.Owner { ControllerSessionId="controller",PlayerDirection=4 };
    private static Operations.ExecuteRequest Request(ulong attempt=1, int map=0) => new Operations.ExecuteRequest
    {
        Precondition=new Authority.WritePrecondition { Identity=Identity(map),ExpectedGeneration=3,LeaseId="lease",Attempt=new Common.AttemptKey
            { ControllerSessionId="controller",ActionId="action",AttemptId=attempt } },
        Operation=new Operations.Operation { PlaceBuilding=new Operations.PlaceBuilding { Placement=new Placement.PlacementCandidate
            { DefName="Wall",X=0,Z=0,Rotation=Placement.Rotation.North } } }
    };
    private static Receipts.EffectEvidence Evidence() => new Receipts.EffectEvidence { Construction=new Receipts.ConstructionEffect
        { OriginThingId="blueprint",CurrentThingId="blueprint",DefName="Wall",Present=true,Stage=Receipts.ConstructionStage.Blueprint } };
    private static void Refuses(NativeAttemptLedger ledger,Operations.ExecuteRequest request,Common.FailureCode code,string name)
    {
        var result=ledger.Inspect(Method,request);
        Check(result.Kind==Kind.Refused && result.Reply!.Failure.Code==code,name);
    }

    private static void Main()
    {
        var ledger=new NativeAttemptLedger(Identity());
        var request=Request();
        Check(ledger.Inspect(Method,request).Kind==Kind.New && ledger.Count==0,"inspection is not admission");
        var invalidOwner=Owner(); invalidOwner.ControllerSessionId="other";
        Check(ledger.Admit(Method,request,Context(),invalidOwner).Reply!.Failure.Code==Common.FailureCode.InvalidRequest && ledger.Count==0,"guard failure consumes no slot");
        var invalidContext=Context(); invalidContext.NativeGeneration=8;
        Check(ledger.Admit(Method,request,invalidContext,Owner()).Kind==Kind.Refused && ledger.Count==0,"admission generation must match request");
        var admission=ledger.Admit(Method,request,Context(),Owner());
        Check(admission.Kind==Kind.Admitted && admission.Handle!=null && ledger.Count==1,"record before dispatch");
        var inFlight=ledger.Inspect(Method,request);
        Check(inFlight.Kind==Kind.InFlight && inFlight.Reply!.Receipt.Uncertain!=null && inFlight.Reply.Failure==null,"duplicate in-flight reports uncertainty, not preadmission failure");
        Check(inFlight.Reply!.Receipt.Attempt.Equals(request.Precondition.Attempt) && inFlight.Reply.Receipt.AuthorizingOwner.Equals(Owner()),"transient uncertainty correlated");
        Check(ledger.Admit(Method,request,Context(),Owner()).Kind==Kind.InFlight && ledger.Count==1,"atomic admission repeats lookup");
        Check(ledger.Lookup(request.Precondition.Attempt,Context()).OutcomeCase==Receipts.LookupReply.OutcomeOneofCase.InFlight,"lookup records in-flight");
        var changed=request.Clone(); changed.Operation.PlaceBuilding.Placement.DefName="Bed";
        Refuses(ledger,changed,Common.FailureCode.AttemptConflict,"changed operation conflicts");
        changed=request.Clone(); changed.Precondition.ExpectedGeneration++;
        Refuses(ledger,changed,Common.FailureCode.AttemptConflict,"changed original generation conflicts");
        changed=request.Clone(); changed.Precondition.LeaseId="new-lease";
        Refuses(ledger,changed,Common.FailureCode.AttemptConflict,"changed original lease conflicts");
        changed=request.Clone(); changed.Operation.PlaceBuilding.Placement.ClearX();
        Refuses(ledger,changed,Common.FailureCode.AttemptConflict,"zero versus absent conflicts");
        Check(ledger.Inspect(Method.ToLowerInvariant(),request).Reply!.Failure.Code==Common.FailureCode.AttemptConflict,"method equality is ordinal");
        var unknown=Operations.ExecuteRequest.Parser.ParseFrom(request.ToByteArray().Concat(new byte[]{0xf8,0x07,0x01}).ToArray());
        Refuses(ledger,unknown,Common.FailureCode.InvalidRequest,"binary unknown field rejected");
        var effect=Evidence();
        var receipt=ledger.FinishApplied(admission.Handle!,effect);
        Check(receipt.Applied!=null && receipt.Attempt.Equals(request.Precondition.Attempt) && receipt.AdmittedContext.Equals(Context()) && receipt.AuthorizingOwner.Equals(Owner()),"correlated immutable receipt");
        receipt.AuthorizingOwner.PlayerDirection=99; effect.Construction.CurrentThingId="mutated";
        var replay=ledger.Inspect(Method,request);
        Check(replay.Kind==Kind.Replay && replay.Reply!.Receipt.AuthorizingOwner.PlayerDirection==4 && replay.Reply.Receipt.Applied.Observed.Construction.CurrentThingId=="blueprint","caller mutations do not alter retained receipt");
        var exposed=replay.Reply!; exposed.Receipt.Attempt.AttemptId=999;
        Check(replay.Reply!.Receipt.Attempt.AttemptId==1,"decision returns detached reply");
        var later=Context(); later.Tick=500; later.NativeGeneration=100;
        Check(ledger.Lookup(request.Precondition.Attempt,later).Receipt.Equals(replay.Reply!.Receipt),"current generation change does not erase receipt");
        Throws(()=>ledger.FinishUncertain(admission.Handle!,null,"late"),"terminal receipt cannot be overwritten");
        Throws(()=>new NativeAttemptLedger(Identity()).FinishApplied(admission.Handle!,Evidence()),"foreign handle refused");
        Throws(()=>ledger.FinishApplied(new NativeAttemptLedger.Admission(),Evidence()),"fabricated handle refused");

        var mutable=Request(2); var saved=mutable.Clone(); var admittedContext=Context(); var authorizingOwner=Owner();
        var pending=ledger.Admit(Method,mutable,admittedContext,authorizingOwner).Handle!;
        mutable.Precondition.LeaseId="mutated"; mutable.Operation.PlaceBuilding.Placement.DefName="mutated";
        admittedContext.Tick=999; authorizingOwner.PlayerDirection=999;
        Check(ledger.Inspect(Method,saved).Kind==Kind.InFlight,"admission stores detached request");
        var uncertain=ledger.FinishUncertain(pending,null,string.Concat(Enumerable.Repeat("\ud83d\ude00",4097)));
        Check(uncertain.Uncertain.LastObserved==null && uncertain.Uncertain.Detail.Length==8192,"uncertainty preserves unknown evidence and scalar-bound diagnostics");
        Check(uncertain.AdmittedContext.Tick==0 && uncertain.AuthorizingOwner.PlayerDirection==4,"admission context and owner detached");
        Check(ledger.Inspect(Method,saved).Reply!.Receipt.Uncertain!=null,"uncertain replay never grants new dispatch");
        var third=Request(3); var handle=ledger.Admit(Method,third,Context(),Owner()).Handle!;
        Throws(()=>ledger.FinishApplied(handle,new Receipts.EffectEvidence()),"empty evidence is not observed effect");
        Check(ledger.Lookup(third.Precondition.Attempt,Context()).InFlight!=null,"bad completion retains admitted entry");
        Check(ledger.FinishNoChange(handle,Evidence(),"unchanged").NoChange!=null,"no-change requires explicit evidence");
        var mapRequest=Request(4,1);
        Check(ledger.Admit(Method,mapRequest,Context(1),Owner()).Kind==Kind.Admitted,"other map shares same per-load ledger");
        Check(ledger.Lookup(request.Precondition.Attempt,Context(1)).Failure.Code==Common.FailureCode.StaleIdentity,"receipt cannot replay across current map");
        var newLoad=Context(); newLoad.Identity.LoadToken="other-load";
        Check(ledger.Lookup(request.Precondition.Attempt,newLoad).Failure.Code==Common.FailureCode.StaleIdentity,"receipt cannot cross loads");
        Check(new NativeAttemptLedger(newLoad.Identity).Lookup(request.Precondition.Attempt,newLoad).Unknown!=null,"new load unknown is not permission to retry");
        changed=request.Clone(); changed.Precondition.Identity.MapId=1;
        Refuses(ledger,changed,Common.FailureCode.AttemptConflict,"existing key changed map conflicts");

        foreach(var mutate in new Action<Operations.ExecuteRequest>[] {
            r=>r.Precondition.Attempt.AttemptId=0, r=>r.Precondition.Attempt.ClearActionId(),
            r=>r.Precondition.Identity.MapId=-1, r=>r.Precondition.ExpectedGeneration=0,
            r=>r.Precondition.LeaseId="\ud800", r=>r.Operation.ClearCommand() })
        { var bad=Request(100); mutate(bad); Refuses(ledger,bad,Common.FailureCode.InvalidRequest,"invalid envelope refused"); }
        var max=Request(ulong.MaxValue);
        Check(ledger.Admit(Method,max,Context(),Owner()).Kind==Kind.Admitted,"uint64 maximum preserved");
        Exception? threadFailure=null;
        var otherThread=new Thread(()=>{try {ledger.Inspect(Method,request);}catch(Exception e){threadFailure=e;}});
        otherThread.Start(); otherThread.Join(); Check(threadFailure is InvalidOperationException,"cross-thread use refused");
        EqualityCheck();
        CapacityCheck();
        checks += ClockLedgerChecks.Run();
        Console.WriteLine("Native attempt ledger passed "+checks+" checks; pure production source, no native dispatch.");
    }
    private static void EqualityCheck()
    {
        var ledger=new NativeAttemptLedger(Identity());
        var request=Request();
        request.Operation=new Operations.Operation { SetTradeLines=new Operations.SetTradeLines
            { Session=new Operations.EntityPrecondition { EntityId="trade",ExpectedSnapshotToken="snapshot" },AllowPawns=false } };
        request.Operation.SetTradeLines.Lines.Add(new Operations.TradeLine { LineId="a",AbsoluteCount=0 });
        request.Operation.SetTradeLines.Lines.Add(new Operations.TradeLine { LineId="b",AbsoluteCount=1 });
        ledger.Admit(Method,request,Context(),Owner());
        var reordered=Operations.ExecuteRequest.Parser.ParseJson("{\"operation\":"+JsonFormatter.Default.Format(request.Operation)
            +",\"precondition\":"+JsonFormatter.Default.Format(request.Precondition)+"}");
        Check(ledger.Inspect(Method,reordered).Kind==Kind.InFlight,"field wire order does not alter typed identity");
        var changed=request.Clone(); changed.Operation.SetTradeLines.ClearAllowPawns();
        Refuses(ledger,changed,Common.FailureCode.AttemptConflict,"false versus absent conflicts");
        changed=request.Clone(); changed.Operation.SetTradeLines.Session.ClearExpectedSnapshotToken();
        Refuses(ledger,changed,Common.FailureCode.AttemptConflict,"original entity snapshot precondition preserved");
        changed=request.Clone(); changed.Operation.SetTradeLines.Lines[0].ClearAbsoluteCount();
        Refuses(ledger,changed,Common.FailureCode.AttemptConflict,"repeated nested zero presence preserved");
        changed=request.Clone();
        var first=changed.Operation.SetTradeLines.Lines[0];
        changed.Operation.SetTradeLines.Lines[0]=changed.Operation.SetTradeLines.Lines[1];
        changed.Operation.SetTradeLines.Lines[1]=first;
        Refuses(ledger,changed,Common.FailureCode.AttemptConflict,"repeated order preserved");
        changed=request.Clone(); changed.Operation.PlaceBuilding=new Operations.PlaceBuilding();
        Refuses(ledger,changed,Common.FailureCode.AttemptConflict,"different command oneof conflicts");
        changed=request.Clone(); changed.Precondition.Attempt.ActionId="another-action";
        Check(ledger.Inspect(Method,changed).Kind==Kind.New,"attempt namespace includes action id");
        changed=request.Clone(); changed.Precondition.Attempt.ControllerSessionId="another-controller";
        Check(ledger.Inspect(Method,changed).Kind==Kind.New,"attempt namespace includes controller id");
        changed=request.Clone(); changed.Precondition.Identity.ClearMapId();
        Refuses(ledger,changed,Common.FailureCode.InvalidRequest,"absent required map zero precondition refused");
    }
    private static void CapacityCheck()
    {
        var ledger=new NativeAttemptLedger(Identity());
        for(ulong i=1;i<=NativeAttemptLedger.Capacity;i++) {
            var decision=ledger.Admit(Method,Request(i),Context(),Owner());
            Check(decision.Kind==Kind.Admitted,"capacity admission");
            ledger.FinishUncertain(decision.Handle!,null,"not dispatched in test");
        }
        Check(ledger.Count==4096,"fixed capacity");
        Refuses(ledger,Request(4097),Common.FailureCode.CapacityExhausted,"full ledger refuses new admission");
        Check(ledger.Inspect(Method,Request(1)).Kind==Kind.Replay,"oldest attempt retained at capacity");
        var conflict=Request(1); conflict.Precondition.LeaseId="different";
        Refuses(ledger,conflict,Common.FailureCode.AttemptConflict,"conflict still classified at capacity");
        Check(ledger.Count==4096,"no eviction after refusal");
    }
}
