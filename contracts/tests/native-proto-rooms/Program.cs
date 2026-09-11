using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

internal static class Program
{
    private static Assembly bridge = null!;
    private static Type tools = null!;
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
    private static object Wire(string name, string json)
    {
        var parser = bridge.GetType("RimGovernor.Protocol.Observations." + name, true)!.GetProperty("Parser")!.GetValue(null)!;
        return parser.GetType().GetMethod("ParseJson")!.Invoke(parser, new object[] { json })!;
    }
    private static object Get(object value, string property) => value.GetType().GetProperty(property)!.GetValue(value)!;
    private static object Call(string name, params object?[] values) => tools.GetMethod(name, Flags)!.Invoke(null, values)!;
    private static bool Valid(string json) => (bool)Call("Validate", Wire("ListRoomsRequest", json), null);
    private static void Refused(Action action, string label) { try { action(); } catch (TargetInvocationException) { Check(true, label); return; } throw new Exception(label); }
    private static int Main(string[] args)
    {
        var directories = args.Skip(1).Concat(new[] { Path.GetDirectoryName(Path.GetFullPath(args[0]))! }).ToArray();
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => { var path = directories.Select(d => Path.Combine(d, new AssemblyName(e.Name).Name + ".dll")).FirstOrDefault(File.Exists); return path == null ? null : Assembly.LoadFrom(path); };
        bridge = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        foreach (var reference in bridge.GetReferencedAssemblies()) Assembly.Load(reference);
        tools = bridge.GetType("HomeBridge.BridgeTools.NativeRoomObservationTools", true)!;
        const string scope = "\"scope\":{\"expectedIdentity\":{\"colonyId\":\"colony\",\"loadToken\":\"load\",\"mapId\":0}}";
        Func<string, string> request = fields => "{" + scope + (fields.Length == 0 ? "" : "," + fields) + "}";
        foreach (var fields in new[] { "", "\"includeOutdoors\":false,\"includeBoundary\":false,\"includeCells\":false", "\"roomIds\":[\"0\",\"roomα\"]", "\"page\":{\"limit\":256}", "\"region\":{\"minimum\":{\"x\":0,\"z\":0},\"maximum\":{\"x\":0,\"z\":0}}" }) Check(Valid(request(fields)), "Valid bounded request");
        foreach (var bad in new[] { "{}", request("\"page\":{\"limit\":0}"), request("\"page\":{\"limit\":257}"), request("\"page\":{\"cursor\":\"stale\"}"), request("\"roomIds\":[\"x\",\"x\"]"), request("\"roomIds\":[\"\"]"), request("\"roomIds\":[\"bad\\u0000id\"]"), request("\"region\":{\"minimum\":{\"x\":1,\"z\":0},\"maximum\":{\"x\":0,\"z\":1}}"), request("\"region\":{\"minimum\":{\"x\":0},\"maximum\":{\"x\":0,\"z\":1}}") }) Check(!Valid(bad), "Malformed bounded request refused");
        var defaultRequest = Wire("ListRoomsRequest", request(""));
        Check((bool)Call("Selected", defaultRequest, "0", false, false), "Ordinary room selected by default");
        Check(!(bool)Call("Selected", defaultRequest, "0", true, false), "Psychological outdoors excluded");
        Check(!(bool)Call("Selected", defaultRequest, "0", false, true), "Doorways excluded");
        var exact = Wire("ListRoomsRequest", request("\"includeOutdoors\":true,\"roomIds\":[\"1\"]"));
        Check((bool)Call("Selected", exact, "1", true, true), "Explicit outdoors includes matching doorway");
        Check(!(bool)Call("Selected", exact, "2", false, false), "Filters intersect rather than union");
        var cellType = Assembly.Load("Assembly-CSharp").GetType("Verse.IntVec3", true)!;
        Func<int, int, object> cell = (x,z) => Activator.CreateInstance(cellType, x, 0, z)!;
        var points = Array.CreateInstance(cellType, 3); points.SetValue(cell(0,0),0); points.SetValue(cell(2,0),1); points.SetValue(cell(0,2),2);
        var center = Call("Center", points);
        Check(points.Cast<object>().Contains(center), "Center stays within concave/disconnected input footprint");
        var rectangle = Wire("Rectangle", "{\"minimum\":{\"x\":1,\"z\":1},\"maximum\":{\"x\":2,\"z\":2}}");
        Check((bool)Call("Inside", rectangle, cell(2,2)), "Maximum edge inclusive");
        Check(!(bool)Call("Inside", rectangle, cell(0,2)), "Outside rectangle excluded");
        foreach (var value in new[] { -20d, 0d, 100d }) Check((double)Call("Finite", value) == value, "Negative temperature/cleanliness and known zero preserved");
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity }) Refused(() => Call("Finite", value), "Nonfinite native fact cannot be a number");
        Refused(() => Wire("ListRoomsRequest", "{\"set\":true}"), "Mutation-shaped unknown field rejected");
        var server = Assembly.LoadFrom(directories.Select(d => Path.Combine(d,"RimBridgeServer.dll")).First(File.Exists));
        var binder = server.GetType("RimBridgeServer.AnnotatedExtensionCapabilityProvider",true)!.GetMethod("BindArguments",Flags)!;
        foreach (var value in new object?[] { "{}", new Dictionary<string,object>(), new List<object>(), null, 1, false })
        {
            var bound = (object[])binder.Invoke(null,new object?[] { tools.GetMethod("ListRooms"),new Dictionary<string,object?> { ["request"]=value },null,CancellationToken.None })!;
            Check(ReferenceEquals(bound[2],value),"SDK preserves raw boundary type");
        }
        var reply = (IDictionary)Call("Encode", Wire("ListRoomsReply", "{\"observed\":{\"rooms\":[{\"id\":\"0\",\"temperatureC\":0,\"outdoors\":false,\"stats\":[{\"defName\":\"Wealth\",\"unavailable\":{\"reason\":\"UNAVAILABLE_REASON_READ_FAILED\"}}]}]}}"));
        var room = ((IList)Get(Get(Wire("ListRoomsReply",(string)reply["payload"]!),"Observed"),"Rooms"))[0]!;
        Check((bool)Get(room,"HasTemperatureC") && (double)Get(room,"TemperatureC")==0,"Known zero temperature survives encoding");
        Check((bool)Get(room,"HasOutdoors") && !(bool)Get(room,"Outdoors"),"Known indoor false survives encoding");
        var stat = ((IList)Get(room,"Stats"))[0]!; Check(!(bool)Get(stat,"HasValue") && Get(stat,"Unavailable")!=null,"Unavailable stat is not zero");
        var oversized=(IDictionary)Call("Encode",Wire("ListRoomsReply","{\"unavailable\":{\"detail\":\""+new string('x',1024*1024)+"\"}}"));
        Check(Get(Wire("ListRoomsReply",(string)oversized["payload"]!),"Unavailable")!=null,"Oversized result refuses instead of truncating facts");
        Console.WriteLine(checks+" compiled room boundary assertions passed; native room acceptance is separate.");return 0;
    }
}
