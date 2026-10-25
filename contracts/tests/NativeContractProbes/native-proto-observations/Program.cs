using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

internal static class NativeProtoObservationsProbe
{
    private static Assembly bridge=null!;
    private static Type tools=null!;
    private static int checks;
    private const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    private static void Check(bool value,string message) { if(!value) throw new InvalidOperationException(message);checks++; }
    private static object Wire(string name,string json) { var type=typeof(RimGovernor.Contracts.Wire.Common.Ref).Assembly.GetType("RimGovernor.Contracts.Wire.Observations."+name,true)!;return typeof(RimGovernor.Contracts.Wire.WireJson).GetMethod("Deserialize",new[]{typeof(string)})!.MakeGenericMethod(type).Invoke(null,new object[]{json})!; }
    private static object Get(object value,string name)=>value.GetType().GetProperty(name)!.GetValue(value)!;
    private static bool Valid(string method,string message,string json)=> (bool)tools.GetMethod(method,Flags)!.Invoke(null,new object?[]{Wire(message,json),null})!;
    internal static int Invoke(string[] args)
    {
        var directories=args.Skip(1).Concat(new[]{Path.GetDirectoryName(Path.GetFullPath(args[0]))!}).ToArray();
        AppDomain.CurrentDomain.AssemblyResolve+=(_,e)=>{var path=directories.Select(d=>Path.Combine(d,new AssemblyName(e.Name).Name+".dll")).FirstOrDefault(File.Exists);return path==null?null:Assembly.LoadFrom(path);};
        bridge=Assembly.LoadFrom(Path.GetFullPath(args[0]));foreach(var reference in bridge.GetReferencedAssemblies())Assembly.Load(reference);
        tools=bridge.GetType("HomeBridge.BridgeTools.NativeObservationTools",true)!;
        foreach (var queued in new[] { 0, 2, 256 }) foreach (var preemptible in new[] { false, true }) {
            var idle=tools.GetMethod("JobRow",Flags)!.Invoke(null,new object?[]{null,queued,preemptible})!;
            Check(Get(idle,"PlayerForced") is false,"idle current job is known not player forced");
            Check(Get(idle,"QueuedJobs") is uint queuedJobs && queuedJobs==queued,"idle queue remains independently observed");
            Check(Get(idle,"DefName")==null,"idle has no invented current job identity");
            Check(Get(idle,"Preemptible") is bool flag && flag==preemptible,"the preemptible flag is carried as given");
        }
        foreach (var invalidQueue in new[] {-1,257}) {
            bool refused=false;
            try { tools.GetMethod("JobRow",Flags)!.Invoke(null,new object?[]{null,invalidQueue,false}); }
            catch(TargetInvocationException) { refused=true; }
            Check(refused,"invalid or oversized job queue is refused");
        }
        const string scope="\"scope\":{\"expectedIdentity\":{\"colonyId\":\"colony\",\"loadToken\":\"load\",\"mapId\":0}}";
        Func<string,string> request=fields=>"{"+scope+(fields.Length==0?"":","+fields)+"}";
        Check(Valid("ValidateStatus","StatusRequest",request("")),"status with an identity scope accepted");
        Check(!Valid("ValidateStatus","StatusRequest","{}"),"status without an identity scope refused");
        Func<int,int,int,int,string> rect=(x0,z0,x1,z1)=>"\"rectangle\":{\"minimum\":{\"x\":"+x0+",\"z\":"+z0+"},\"maximum\":{\"x\":"+x1+",\"z\":"+z1+"}}";
        Check(Valid("ValidateCells","GetCellsRequest",request(rect(2,3,2,3))),"Go singleton map-bounds request accepted");
        Check(Valid("ValidateCells","GetCellsRequest",request(rect(0,0,1023,1023))),"whole-map rectangle accepted");
        foreach(var invalid in new[]{request(""),request(rect(0,0,1024,1023)),request(rect(1,0,0,0)),request(rect(-1,0,0,0)),request(rect(0,0,0,0).Replace("\"z\":0}}","\"z\":null}}")),request(rect(0,0,2147483647,2147483647))})
            Check(!Valid("ValidateCells","GetCellsRequest",invalid),"invalid/oversized cells refused");
        // ---- N01.03: shared row CAS helper (NativeObservationSnapshot over NativeSnapshotVersions).
        var snapshotType=bridge.GetType("HomeBridge.BridgeTools.NativeObservationSnapshot",true)!;
        object ParseCommon(string type,string json) {
            var commonType=typeof(RimGovernor.Contracts.Wire.Common.Ref).Assembly.GetType("RimGovernor.Contracts.Wire.Common."+type,true)!;
            return typeof(RimGovernor.Contracts.Wire.WireJson).GetMethod("Deserialize",new[]{typeof(string)})!.MakeGenericMethod(commonType).Invoke(null,new object[]{json})!;
        }
        var snapshotMethod=snapshotType.GetMethod("Snapshot",Flags)!;
        var writerParam=snapshotMethod.GetParameters().Last().ParameterType;
        var context=ParseCommon("ObservationContext","{\"identity\":{\"colonyId\":\"colony\",\"loadToken\":\"load\",\"mapId\":0},\"tick\":1}");
        Delegate Writer(string name)=>Delegate.CreateDelegate(writerParam,typeof(NativeProtoObservationsProbe).GetMethod(name,Flags)!);
        var tokenField=snapshotMethod.ReturnType.GetProperty("Token")!;
        object? Token(string entity,string writer)=>tokenField.GetValue(snapshotMethod.Invoke(null,new object?[]{"kind",context,entity,Writer(writer)}));
        var tokenA=Token("entity-1",nameof(WriteNothing));
        Check(Equals(tokenA,Token("entity-1",nameof(WriteNothing))),"snapshot token is stable for unchanged facts");
        Check(!Equals(tokenA,Token("entity-2",nameof(WriteNothing))),"snapshot token differs by entity id");
        Check(!Equals(tokenA,Token("entity-1",nameof(WriteOne))),"snapshot token moves when a guarded fact moves");
        Check(!Equals(tokenA,Token("entity-1",nameof(WriteNothing))),"a read before the move never matches again, even once the fact moves back");

        // ---- N01.03: readers accept a cursor within the byte bound instead of refusing any nonempty cursor.
        var boundedCursor=new string('A',64);
        var oversizedCursor=new string('A',4097);
        foreach(var (toolType,requestType) in new[]{
            ("NativePawnObservationTools","ListPawnsRequest"),
            ("NativeRoomObservationTools","ListRoomsRequest"),
            ("NativeResearchObservationTools","ResearchRequest"),
            ("NativeBuildingObservationTools","ListBuildingsRequest"),
        }) {
            var toolTypeRef=bridge.GetType("HomeBridge.BridgeTools."+toolType,true)!;
            object Parse(string cursorValue)=>Wire(requestType,"{"+scope+",\"page\":{\"cursor\":\""+cursorValue+"\"}}");
            bool Accepts(object request)=>(bool)toolTypeRef.GetMethod("Validate",Flags)!.Invoke(null,new object?[]{request,null})!;
            Check(Accepts(Parse(boundedCursor)),"a within-bound nonempty cursor is now accepted by "+toolType);
            Check(!Accepts(Parse(oversizedCursor)),"an oversized cursor is still refused by "+toolType);
        }

        Console.WriteLine(checks+" compiled observation request assertions passed; no gameplay assertions.");return 0;
    }
    private static void WriteNothing(BinaryWriter writer) { }
    private static void WriteOne(BinaryWriter writer) => writer.Write(1);
}
