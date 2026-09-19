#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

internal static class NativeDraftOperationsProbe
{
    private const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    private static Assembly bridge=null!;
    private static int checks;
    private const string Identity="{\"colonyId\":\"colony\",\"loadToken\":\"load\",\"mapId\":0}";
    private const string Pawn="{\"entityId\":\"Human1\",\"expectedSnapshotToken\":\"native-token\"}";
    private static Type Native(string name)=>bridge.GetType("HomeBridge.BridgeTools."+name,true)!;
    private static object Get(object obj,string name)=>obj.GetType().GetProperty(name,Flags)!.GetValue(obj)!;
    private static void Field(object obj,string name,object value)=>obj.GetType().GetField(name,Flags)!.SetValue(obj,value);
    private static void Check(bool condition,string why) {if(!condition) throw new InvalidOperationException(why);checks++;}
    private static object Wire(string name,string json) {
        var parser=bridge.GetType("RimGovernor.Protocol."+name,true)!.GetProperty("Parser")!.GetValue(null)!;
        return parser.GetType().GetMethod("ParseJson")!.Invoke(parser,new object[]{json})!;
    }
    private static bool TryParse(string name,string json) {try{Wire(name,json);return true;}catch(TargetInvocationException){return false;}}
    private static object Call(Type type,string name,params object?[] args)=>type.GetMethod(name,Flags)!.Invoke(null,args)!;
    private static object Instance(object value,string method,params object?[] args)=>value.GetType().GetMethod(method,Flags)!.Invoke(value,args)!;
    private static object Construct(string type,params object?[] args)=>Activator.CreateInstance(Native(type),Flags,null,args,null)!;
    // Authority.Owner was removed by #52: a NativeDraftClaim is identified by claim ID alone, so
    // "claimed" is a bool where the pre-#52 probe passed an owner message.
    private static object Snapshot(bool drafted,bool claimed=false,string claimId="claim-1",string token="native-token") {
        var facts=Construct("NativePawnFacts");
        Field(facts,"PawnId","Human1");Field(facts,"Drafted",drafted);Field(facts,"Spawned",true);Field(facts,"PlayerControlled",true);
        Field(facts,"Drafter",System.Runtime.Serialization.FormatterServices.GetUninitializedObject(
            AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="Assembly-CSharp").GetType("RimWorld.Pawn_DraftController",true)!));
        var claim=claimed?Construct("NativeDraftClaim",claimId):null;
        return Construct("NativePawnSnapshot",token,facts,claim);
    }
    private static string Draft(bool value,string extra="")=>"{\"pawn\":"+Pawn+",\"drafted\":"+(value?"true":"false")+extra+"}";
    private static string Request(int id,string extra="")=>"{\"precondition\":{\"identity\":"+Identity+",\"expectedGeneration\":\"1\",\"attempt\":{\"controllerSessionId\":\"owner\",\"actionId\":\"draft\",\"attemptId\":\""+id+"\"}},\"operation\":{\"setDrafted\":"+Draft(true,extra)+"}}";
    private static string Release()=>"{\"identity\":"+Identity+",\"pawn\":"+Pawn+",\"expectedClaimId\":\"claim-1\"}";
    internal static void Invoke(string[] args)
    {
        var directories=args.Skip(1).Concat(new[]{Path.GetDirectoryName(Path.GetFullPath(args[0]))!}).ToArray();
        AppDomain.CurrentDomain.AssemblyResolve+=(_,e)=>{var path=directories.Select(d=>Path.Combine(d,new AssemblyName(e.Name).Name+".dll")).FirstOrDefault(File.Exists);return path==null?null:Assembly.LoadFrom(path);};
        bridge=Assembly.LoadFrom(Path.GetFullPath(args[0]));foreach(var reference in bridge.GetReferencedAssemblies()) Assembly.Load(reference);
        var protocol=Native("NativeDraftProtocol");var operations=Native("NativeDraftOperations");
        Func<string,bool> valid=json=>(bool)Call(protocol,"Validate",Wire("Operations.SetDrafted",json),null);
        Check(valid(Draft(true)),"explicit draft true accepted");Check(valid(Draft(false)),"explicit draft false accepted");
        Check(valid(Draft(true,",\"allowPersistentDraft\":false")),"explicit nonpersistent value accepted");
        foreach(var json in new[]{"{}","{\"pawn\":"+Pawn+"}",Draft(true).Replace("native-token",""),Draft(true).Replace("Human1",""),Draft(true,",\"expectedDraftOwner\":\"\"")})
            Check(!valid(json),"missing or empty exact precondition refused");
        var persistentArgs=new object?[]{Wire("Operations.SetDrafted",Draft(true,",\"allowPersistentDraft\":true")),null};
        Check(!(bool)Call(protocol,"Validate",persistentArgs),"boolean never grants persistent draft permission");
        Check(Get(persistentArgs[1]!,"Code").ToString()=="Unsupported","persistent policy remains explicit unsupported");
        Func<string,bool> releaseValid=json=>(bool)Call(protocol,"ValidateRelease",Wire("Operations.ReleaseOwnedDraftRequest",json),null);
        Check(releaseValid(Release()),"exact release request accepted");
        foreach(var json in new[]{"{}",Release().Replace("claim-1",""),Release().Replace("native-token",""),Release().Replace("\"claim-1\"","\"\"")}) Check(!releaseValid(json),"incomplete cleanup claim refused");
        Check(!TryParse("Operations.ReleaseOwnedDraftRequest",Release().Insert(Release().Length-1,",\"originalOwner\":{\"controllerSessionId\":\"owner\"}")),"removed originalOwner field is rejected on the wire, not silently ignored");
        // Owner comparisons were removed by #52 (single bot actor): ownership is claim presence, and
        // expectedDraftOwner now names the expected claim ID rather than a controller session.
        Func<object,string,bool> eligible=(snapshot,json)=>(bool)Call(operations,"Eligible",Wire("Operations.SetDrafted",json),snapshot,null);
        Check(eligible(Snapshot(false),Draft(true)),"eligible undrafted pawn can receive new claim");
        Check(eligible(Snapshot(true),Draft(true)),"existing unclaimed draft is adopted under a fresh claim (#461)");
        Check(!eligible(Snapshot(true),Draft(false)),"existing unclaimed player draft cannot be cleared");
        Check(eligible(Snapshot(true,true),Draft(true)),"existing native claim is observed as no change");
        Check(eligible(Snapshot(true,true),Draft(false)),"held claim can be released through ordinary operation");
        Check(!eligible(Snapshot(false),Draft(false)),"ordinary undraft requires a held claim");
        Check(eligible(Snapshot(true,true),Draft(true,",\"expectedDraftOwner\":\"claim-1\"")),"expected claim ID matching the held claim is accepted");
        Check(!eligible(Snapshot(true,true),Draft(true,",\"expectedDraftOwner\":\"claim-2\"")),"expected claim ID is an actual precondition");
        Check(!eligible(Snapshot(true,true),Draft(false,",\"expectedDraftOwner\":\"claim-2\"")),"release with a stale expected claim ID is refused");
        foreach(var field in new[]{"Dead","Downed","Mental","Spawned","PlayerControlled"}) {
            var snapshot=Snapshot(false);Field(Get(snapshot,"Facts"),field,field!="Spawned"&&field!="PlayerControlled");
            Check(!eligible(snapshot,Draft(true)),"native eligibility refuses "+field);
        }
        var identity=Wire("Common.Identity",Identity);
        var context=Wire("Common.ObservationContext","{\"identity\":"+Identity+",\"tick\":\"10\",\"nativeGeneration\":\"1\"}");
        var ledger=Construct("NativeAttemptLedger",identity);
        const string method="rimgovernor.operations.v1.Operations/Execute";
        var first=Wire("Operations.ExecuteRequest",Request(1));
        var admitted=Instance(ledger,"Admit",method,first,context);
        Check(Get(admitted,"Kind").ToString()=="Admitted","draft shares ordinary attempt admission");
        var effect=Wire("Receipts.EffectEvidence","{\"job\":{\"pawnId\":\"Human1\",\"drafted\":true,\"draftClaimId\":\"claim-1\",\"resultingSnapshotToken\":\"after\",\"issued\":true,\"verified\":true}}");
        var receipt=Instance(ledger,"FinishApplied",Get(admitted,"Handle"),effect);
        var replay=Instance(ledger,"Inspect",method,first);
        Check(Get(replay,"Kind").ToString()=="Replay"&&receipt.Equals(Get(Get(replay,"Reply"),"Receipt")),"exact draft retry returns immutable original receipt");
        var changed=Instance(ledger,"Inspect",method,Wire("Operations.ExecuteRequest",Request(1,",\"allowPersistentDraft\":false")));
        Check(Get(Get(Get(changed,"Reply"),"Failure"),"Code").ToString()=="AttemptConflict","absent versus false remains part of exact attempt identity");
        for(var i=2;i<=4096;i++) {
            var decision=Instance(ledger,"Admit",method,Wire("Operations.ExecuteRequest",Request(i)),context);
            Check(Get(decision,"Kind").ToString()=="Admitted","ordinary shared admission "+i);
        }
        var full=Instance(ledger,"Admit",method,Wire("Operations.ExecuteRequest",Request(4097)),context);
        Check(Get(Get(Get(full,"Reply"),"Failure"),"Code").ToString()=="CapacityExhausted","new draft cannot bypass4096capacity");
        var released=Call(operations,"Released",Wire("Operations.ReleaseOwnedDraftRequest",Release()),context,Snapshot(false),true);
        Check(!(bool)Get(Get(released,"Observed"),"Drafted")&&(string)Get(Get(released,"Observed"),"DraftClaimId")=="claim-1","cleanup carries original released claim evidence");
        Check(Get(released,"Request").Equals(Wire("Operations.ReleaseOwnedDraftRequest",Release())),"cleanup retains exact original request");
        Check((bool)Get(Get(released,"Observed"),"Issued")&&(bool)Get(Get(released,"Observed"),"Verified"),"released state records actual verified setter");
        var already=Call(operations,"Released",Wire("Operations.ReleaseOwnedDraftRequest",Release()),context,Snapshot(false),false);
        Check(!(bool)Get(Get(already,"Observed"),"Issued"),"same-claim already released never reports another setter");
        var unknownRecord=Construct("NativeDraftRecord",null,null,context,true);
        var attempt=Get(Get(first,"Precondition"),"Attempt");
        var progress=Instance(unknownRecord,"Observe",attempt,context);
        Check(Get(progress,"EffectCase").ToString()=="Unknown"&&!(bool)Get(progress,"CompleteInspection"),"unverified draft cannot infer completion from missing state");
        // Context replacement: even a fully confirmed draft record must not report
        // completion once later observed under a different colony/load identity --
        // matching a reload/colony swap between admission and readback.
        var confirmedRecord=Construct("NativeDraftRecord",null,null,context,true);
        Instance(confirmedRecord,"Confirm",Snapshot(true,true),null);
        var replacedIdentityContext=Wire("Common.ObservationContext","{\"identity\":{\"colonyId\":\"colony-replaced\",\"loadToken\":\"load\",\"mapId\":0},\"tick\":\"10\",\"nativeGeneration\":\"1\"}");
        var replacedIdentity=Instance(confirmedRecord,"Observe",attempt,replacedIdentityContext);
        Check(Get(replacedIdentity,"EffectCase").ToString()=="Unknown"&&!(bool)Get(replacedIdentity,"CompleteInspection"),"a confirmed draft outcome cannot be reported once the observation colony identity is replaced");
        var replacedLoadContext=Wire("Common.ObservationContext","{\"identity\":{\"colonyId\":\"colony\",\"loadToken\":\"load-replaced\",\"mapId\":0},\"tick\":\"10\",\"nativeGeneration\":\"1\"}");
        var replacedLoad=Instance(confirmedRecord,"Observe",attempt,replacedLoadContext);
        Check(Get(replacedLoad,"EffectCase").ToString()=="Unknown"&&!(bool)Get(replacedLoad,"CompleteInspection"),"a confirmed draft outcome cannot be reported once the observation load token is replaced");
        // Other pawn orders: NativeDraftRecord.Matches isolates the exact claim/token
        // agreement that Observe requires once the identity/tick guard and native
        // readback both succeed. A later draft setter or any other pawn order that
        // redrafts or releases the pawn between admission and readback produces a
        // different claim ID (or, on release, a different token) even though
        // the boolean Drafted state can coincidentally match -- proving a bare bool
        // comparison could not attribute the observed state to this operation alone.
        var matches=Native("NativeDraftRecord").GetMethod("Matches",Flags)!;
        Func<bool,object,object,bool> matched=(wanted,verified,current)=>(bool)matches.Invoke(null,new object?[]{wanted,verified,current})!;
        Check(matched(true,Snapshot(true,true),Snapshot(true,true)),"same claim ID still held is a match");
        Check(!matched(true,Snapshot(true,true),Snapshot(true,true,claimId:"claim-2")),"a later draft setter or other order granting a new claim ID is not this operation's outcome");
        Check(!matched(true,Snapshot(true,true),Snapshot(false)),"an intervening release leaves no drafted state to match");
        Check(!matched(true,Snapshot(true,true),Snapshot(true)),"a drafted pawn with no current claim cannot match a claimed outcome");
        Check(matched(false,Snapshot(true,true),Snapshot(false,token:"native-token")),"exact token after release still matches");
        Check(!matched(false,Snapshot(true,true),Snapshot(false,token:"after-other-order")),"a different resulting token after another pawn order is not this release's outcome");
        Check(!matched(false,Snapshot(true,true),Snapshot(true,true)),"a later re-draft by another order leaves the release unmatched");
        var server=Assembly.LoadFrom(directories.Select(d=>Path.Combine(d,"RimBridgeServer.dll")).First(File.Exists));
        var binder=server.GetType("RimBridgeServer.AnnotatedExtensionCapabilityProvider",true)!.GetMethod("BindArguments",Flags)!;
        foreach(var name in new[]{"Execute","Preview","ReleaseOwnedDraft"}) foreach(var value in new object?[]{"{}",new Dictionary<string,object>(),null,17,true}) {
            var bound=(object[])binder.Invoke(null,new object?[]{Native("NativeOperationTools").GetMethod(name),new Dictionary<string,object?>{{"request",value}},null,CancellationToken.None})!;
            Check(ReferenceEquals(bound[2],value),"SDK raw type preserved "+name);
        }
        Console.WriteLine(checks+" compiled draft admission/ownership/cleanup/binder assertions passed; no gameplay assertions.");
    }
}
