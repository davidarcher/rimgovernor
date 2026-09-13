#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

internal static class Program
{
    private const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    private static Assembly bridge=null!;
    private static int checks;
    private const string Identity="{\"colonyId\":\"colony\",\"loadToken\":\"load\",\"mapId\":0}";
    private const string Owner="{\"controllerSessionId\":\"owner\",\"playerDirection\":\"2\"}";
    private const string Pawn="{\"entityId\":\"Human1\",\"expectedSnapshotToken\":\"native-token\"}";
    private static Type Native(string name)=>bridge.GetType("HomeBridge.BridgeTools."+name,true)!;
    private static object Get(object obj,string name)=>obj.GetType().GetProperty(name,Flags)!.GetValue(obj)!;
    private static void Field(object obj,string name,object value)=>obj.GetType().GetField(name,Flags)!.SetValue(obj,value);
    private static void Check(bool condition,string why) {if(!condition) throw new InvalidOperationException(why);checks++;}
    private static object Wire(string name,string json) {
        var parser=bridge.GetType("RimGovernor.Protocol."+name,true)!.GetProperty("Parser")!.GetValue(null)!;
        return parser.GetType().GetMethod("ParseJson")!.Invoke(parser,new object[]{json})!;
    }
    private static object Call(Type type,string name,params object?[] args)=>type.GetMethod(name,Flags)!.Invoke(null,args)!;
    private static object Instance(object value,string method,params object?[] args)=>value.GetType().GetMethod(method,Flags)!.Invoke(value,args)!;
    private static object Construct(string type,params object?[] args)=>Activator.CreateInstance(Native(type),Flags,null,args,null)!;
    private static object Snapshot(bool drafted,object? owner=null) {
        var facts=Construct("NativePawnFacts");
        Field(facts,"PawnId","Human1");Field(facts,"Drafted",drafted);Field(facts,"Spawned",true);Field(facts,"PlayerControlled",true);
        Field(facts,"Drafter",System.Runtime.Serialization.FormatterServices.GetUninitializedObject(
            AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="Assembly-CSharp").GetType("RimWorld.Pawn_DraftController",true)!));
        var claim=owner==null?null:Construct("NativeDraftClaim","claim-1",owner);
        return Construct("NativePawnSnapshot","native-token",facts,claim);
    }
    private static string Draft(bool value,string extra="")=>"{\"pawn\":"+Pawn+",\"drafted\":"+(value?"true":"false")+extra+"}";
    private static string Request(int id,string extra="")=>"{\"precondition\":{\"identity\":"+Identity+",\"expectedGeneration\":\"1\",\"leaseId\":\"lease\",\"attempt\":{\"controllerSessionId\":\"owner\",\"actionId\":\"draft\",\"attemptId\":\""+id+"\"}},\"operation\":{\"setDrafted\":"+Draft(true,extra)+"}}";
    private static string Release()=>"{\"identity\":"+Identity+",\"pawn\":"+Pawn+",\"expectedClaimId\":\"claim-1\",\"originalOwner\":"+Owner+"}";
    private static void Main(string[] args)
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
        foreach(var json in new[]{"{}",Release().Replace("claim-1",""),Release().Replace("native-token",""),Release().Replace("\"2\"","\"0\""),Release().Replace("\"owner\"","\"\"")}) Check(!releaseValid(json),"incomplete cleanup owner or claim refused");
        var owner=Wire("Authority.Owner",Owner);var foreign=Wire("Authority.Owner",Owner.Replace("owner","foreign"));var later=Wire("Authority.Owner",Owner.Replace("\"2\"","\"3\""));
        Func<object,object,string,bool> eligible=(snapshot,who,json)=>(bool)Call(operations,"Eligible",Wire("Operations.SetDrafted",json),snapshot,who,null);
        Check(eligible(Snapshot(false),owner,Draft(true)),"eligible undrafted pawn can receive new claim");
        Check(!eligible(Snapshot(true),owner,Draft(true)),"existing player draft cannot be adopted");
        Check(!eligible(Snapshot(true),owner,Draft(false)),"existing unowned player draft cannot be cleared");
        Check(eligible(Snapshot(true,owner),owner,Draft(true)),"same original owner can observe existing claim as no change");
        Check(eligible(Snapshot(true,owner),owner,Draft(false)),"current owner can release through ordinary operation");
        Check(!eligible(Snapshot(true,owner),foreign,Draft(false)),"other controller cannot undraft");
        Check(!eligible(Snapshot(true,owner),later,Draft(false)),"new player direction cannot impersonate original owner");
        Check(!eligible(Snapshot(false),owner,Draft(false)),"ordinary undraft requires an owned claim");
        Check(!eligible(Snapshot(true,owner),owner,Draft(true,",\"expectedDraftOwner\":\"foreign\"")),"expected owner is an actual precondition");
        foreach(var field in new[]{"Dead","Downed","Mental","Spawned","PlayerControlled"}) {
            var snapshot=Snapshot(false);Field(Get(snapshot,"Facts"),field,field!="Spawned"&&field!="PlayerControlled");
            Check(!eligible(snapshot,owner,Draft(true)),"native eligibility refuses "+field);
        }
        var identity=Wire("Common.Identity",Identity);
        var context=Wire("Common.ObservationContext","{\"identity\":"+Identity+",\"tick\":\"10\",\"nativeGeneration\":\"1\"}");
        var ledger=Construct("NativeAttemptLedger",identity);
        const string method="rimgovernor.operations.v1.Operations/Execute";
        var first=Wire("Operations.ExecuteRequest",Request(1));
        var admitted=Instance(ledger,"Admit",method,first,context,owner);
        Check(Get(admitted,"Kind").ToString()=="Admitted","draft shares ordinary attempt admission");
        var effect=Wire("Receipts.EffectEvidence","{\"job\":{\"pawnId\":\"Human1\",\"drafted\":true,\"draftClaimId\":\"claim-1\",\"draftOwner\":\"owner\",\"resultingSnapshotToken\":\"after\",\"issued\":true,\"verified\":true}}");
        var receipt=Instance(ledger,"FinishApplied",Get(admitted,"Handle"),effect);
        var replay=Instance(ledger,"Inspect",method,first);
        Check(Get(replay,"Kind").ToString()=="Replay"&&receipt.Equals(Get(Get(replay,"Reply"),"Receipt")),"exact draft retry returns immutable original receipt");
        var changed=Instance(ledger,"Inspect",method,Wire("Operations.ExecuteRequest",Request(1,",\"allowPersistentDraft\":false")));
        Check(Get(Get(Get(changed,"Reply"),"Failure"),"Code").ToString()=="AttemptConflict","absent versus false remains part of exact attempt identity");
        for(var i=2;i<=4096;i++) {
            var decision=Instance(ledger,"Admit",method,Wire("Operations.ExecuteRequest",Request(i)),context,owner);
            Check(Get(decision,"Kind").ToString()=="Admitted","ordinary shared admission "+i);
        }
        var full=Instance(ledger,"Admit",method,Wire("Operations.ExecuteRequest",Request(4097)),context,owner);
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
        Instance(confirmedRecord,"Confirm",Snapshot(true,owner),null);
        var replacedIdentityContext=Wire("Common.ObservationContext","{\"identity\":{\"colonyId\":\"colony-replaced\",\"loadToken\":\"load\",\"mapId\":0},\"tick\":\"10\",\"nativeGeneration\":\"1\"}");
        var replacedIdentity=Instance(confirmedRecord,"Observe",attempt,replacedIdentityContext);
        Check(Get(replacedIdentity,"EffectCase").ToString()=="Unknown"&&!(bool)Get(replacedIdentity,"CompleteInspection"),"a confirmed draft outcome cannot be reported once the observation colony identity is replaced");
        var replacedLoadContext=Wire("Common.ObservationContext","{\"identity\":{\"colonyId\":\"colony\",\"loadToken\":\"load-replaced\",\"mapId\":0},\"tick\":\"10\",\"nativeGeneration\":\"1\"}");
        var replacedLoad=Instance(confirmedRecord,"Observe",attempt,replacedLoadContext);
        Check(Get(replacedLoad,"EffectCase").ToString()=="Unknown"&&!(bool)Get(replacedLoad,"CompleteInspection"),"a confirmed draft outcome cannot be reported once the observation load token is replaced");
        var server=Assembly.LoadFrom(directories.Select(d=>Path.Combine(d,"RimBridgeServer.dll")).First(File.Exists));
        var binder=server.GetType("RimBridgeServer.AnnotatedExtensionCapabilityProvider",true)!.GetMethod("BindArguments",Flags)!;
        foreach(var name in new[]{"Execute","Preview","ReleaseOwnedDraft"}) foreach(var value in new object?[]{"{}",new Dictionary<string,object>(),null,17,true}) {
            var bound=(object[])binder.Invoke(null,new object?[]{Native("NativeOperationTools").GetMethod(name),new Dictionary<string,object?>{{"request",value}},null,CancellationToken.None})!;
            Check(ReferenceEquals(bound[2],value),"SDK raw type preserved "+name);
        }
        Console.WriteLine(checks+" compiled draft admission/ownership/cleanup/binder assertions passed; no gameplay assertions.");
    }
}
