#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

internal static class Program
{
    private static Assembly bridge=null!;
    private static Type tools=null!;
    private static int checks;
    private const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    private static void Check(bool value,string message) { if(!value) throw new InvalidOperationException(message);checks++; }
    private static object Wire(string name,string json) { var type=bridge.GetType("RimGovernor.Protocol.Observations."+name,true)!;var parser=type.GetProperty("Parser")!.GetValue(null)!;return parser.GetType().GetMethod("ParseJson")!.Invoke(parser,new object[]{json})!; }
    private static object Get(object value,string name)=>value.GetType().GetProperty(name)!.GetValue(value)!;
    private static bool Valid(string method,string message,string json)=> (bool)tools.GetMethod(method,Flags)!.Invoke(null,new object?[]{Wire(message,json),null})!;
    private static int Main(string[] args)
    {
        var directories=args.Skip(1).Concat(new[]{Path.GetDirectoryName(Path.GetFullPath(args[0]))!}).ToArray();
        AppDomain.CurrentDomain.AssemblyResolve+=(_,e)=>{var path=directories.Select(d=>Path.Combine(d,new AssemblyName(e.Name).Name+".dll")).FirstOrDefault(File.Exists);return path==null?null:Assembly.LoadFrom(path);};
        bridge=Assembly.LoadFrom(Path.GetFullPath(args[0]));foreach(var reference in bridge.GetReferencedAssemblies())Assembly.Load(reference);
        tools=bridge.GetType("HomeBridge.BridgeTools.NativeObservationTools",true)!;
        foreach (var queued in new[] { 0, 2, 256 }) {
            var idle=tools.GetMethod("JobRow",Flags)!.Invoke(null,new object?[]{null,queued})!;
            Check((bool)Get(idle,"HasPlayerForced") && !(bool)Get(idle,"PlayerForced"),"idle current job is known not player forced");
            Check((bool)Get(idle,"HasQueuedJobs") && (uint)Get(idle,"QueuedJobs")==queued,"idle queue remains independently observed");
            Check(!(bool)Get(idle,"HasDefName") && !(bool)Get(idle,"HasLoadId"),"idle has no invented current job identity");
        }
        foreach (var invalidQueue in new[] {-1,257}) {
            bool refused=false;
            try { tools.GetMethod("JobRow",Flags)!.Invoke(null,new object?[]{null,invalidQueue}); }
            catch(TargetInvocationException) { refused=true; }
            Check(refused,"invalid or oversized job queue is refused");
        }
        const string scope="\"scope\":{\"expectedIdentity\":{\"colonyId\":\"colony\",\"loadToken\":\"load\",\"mapId\":0}}";
        Func<string,string> request=fields=>"{"+scope+(fields.Length==0?"":","+fields)+"}";
        Check(Valid("ValidateStatus","StatusRequest",request("")),"status defaults accepted");
        Check(Valid("ValidateStatus","StatusRequest",request("\"colonists\":false,\"threats\":false,\"predatorRadius\":0")),"known false and zero accepted");
        foreach(var invalid in new[]{"{}",request("\"page\":{\"limit\":0}"),request("\"page\":{\"limit\":257}"),request("\"page\":{\"cursor\":\"stale\"}"),request("\"predatorRadius\":-1"),request("\"predatorRadius\":\"NaN\""),request("\"predatorRadius\":\"Infinity\"")})
            Check(!Valid("ValidateStatus","StatusRequest",invalid),"invalid status refused");
        const string selection="\"exactCells\":{\"cells\":[{\"x\":0,\"z\":0}]}";
        const string noFields="\"fields\":{\"terrain\":false,\"roof\":false,\"visibility\":false,\"traversal\":false,\"zone\":false,\"areas\":false,\"things\":false,\"designations\":false,\"room\":false,\"growth\":false}";
        var anchor=request(selection+","+noFields+",\"page\":{\"limit\":1}");
        Check(Valid("ValidateCells","GetCellsRequest",anchor),"Go singleton map-bounds request accepted");
        var parsed=Wire("GetCellsRequest",anchor);
        var fields=tools.GetMethod("Fields",Flags)!.Invoke(null,new[]{Get(parsed,"Fields")})!;
        foreach(var name in new[]{"Terrain","Roof","Visibility","Traversal","Zone","Areas","Things","Designations","Room","Growth"})
            Check((bool)Get(fields,"Has"+name)&&!(bool)Get(fields,name),"explicit false applied: "+name);
        var cells=(IList)tools.GetMethod("Selection",Flags)!.Invoke(null,new[]{parsed})!;
        Check(cells.Count==1,"singleton complete selection");
        foreach(var invalid in new[]{request(""),request("\"exactCells\":{\"cells\":[]}"),request(selection.Replace("\"z\":0","\"z\":null")),request("\"exactCells\":{\"cells\":[{\"x\":0,\"z\":0},{\"x\":0,\"z\":0}]}"),request(selection+",\"fields\":{\"things\":true}"),request("\"rectangle\":{\"minimum\":{\"x\":0,\"z\":0},\"maximum\":{\"x\":2147483647,\"z\":2147483647}}")})
            Check(!Valid("ValidateCells","GetCellsRequest",invalid),"invalid/unsupported cells refused");
        Check(Valid("ValidateCells","GetCellsRequest",request("\"rectangle\":{\"minimum\":{\"x\":0,\"z\":0},\"maximum\":{\"x\":15,\"z\":15}}")),"bounded rectangle accepted");
        var server=Assembly.LoadFrom(directories.Select(d=>Path.Combine(d,"RimBridgeServer.dll")).First(File.Exists));
        var binder=server.GetType("RimBridgeServer.AnnotatedExtensionCapabilityProvider",true)!.GetMethod("BindArguments",Flags)!;
        foreach(var methodName in new[]{"ReadStatus","GetCells"}) foreach(var value in new object?[]{"{}",new Dictionary<string,object>(),new List<object>(),null,17,true}) {
            var bound=(object[])binder.Invoke(null,new object?[]{tools.GetMethod(methodName),new Dictionary<string,object?>{{"request",value}},null,CancellationToken.None})!;
            Check(ReferenceEquals(bound[2],value),"actual SDK raw value preserved: "+methodName);
        }
        Console.WriteLine(checks+" compiled observation request/binder assertions passed; no gameplay assertions.");return 0;
    }
}
