#nullable enable
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;

// Exercises the actual compiled production mapper and official protobuf classes.
// No game is created, no static definition database is seeded, no native outcome claimed.
internal static class Program
{
    private static Assembly bridge = null!;
    private static Type mapper = null!;
    private static int checks;
    private static readonly BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static object Make(string name) => Activator.CreateInstance(bridge.GetType("HomeBridge.BridgeTools." + name, true)!, true)!;
    private static object Get(object value, string name) => value.GetType().GetProperty(name, Members)!.GetValue(value)!;
    private static void Set(object value, string name, object item) => value.GetType().GetProperty(name, Members)!.SetValue(value, item);
    private static void Assert(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); checks++; }
    private static object Wire(string name, string json)
    {
        var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;
        var parser = type.GetProperty("Parser")!.GetValue(null)!;
        return parser.GetType().GetMethod("ParseJson")!.Invoke(parser, new object[] { json })!;
    }
    private static bool Valid(string json)
    {
        var request = Wire("RimGovernor.Protocol.Placement.PlacementRequest", json);
        var arguments = new[] { request, null };
        return (bool)mapper.GetMethod("Validate", Members)!.Invoke(null, arguments)!;
    }
    private static void ParserRefuses(string json)
    {
        try { Wire("RimGovernor.Protocol.Placement.PlacementRequest", json); }
        catch (TargetInvocationException error)
        {
            var kind = error.InnerException?.GetType().FullName;
            Assert(kind == "Google.Protobuf.InvalidProtocolBufferException" || kind == "Google.Protobuf.InvalidJsonException",
                "Official parser raises a classified boundary exception: " + kind);
            return;
        }
        throw new InvalidOperationException("Malformed request was parsed: " + json);
    }
    private static object Map(object value, object context) => mapper.GetMethod("Map", Members)!.Invoke(null, new[] { value, context })!;
    private static object Evaluation()
    {
        var result = Make("PlacementPreviewEvaluation");
        Set(result, "passability", "Standable");
        Set(result, "researchFinished", true);
        Set(result, "buildableByPlayer", true);
        var rotation = Make("PlacementRotation");
        Set(rotation, "rotation", "north");
        Set(rotation, "accepted", false);
        Set(rotation, "reason", "Native test projection refusal");
        var cellType = bridge.GetType("HomeBridge.BridgeTools.PlacementCell", true)!;
        var constructor = cellType.GetConstructors(Members).Single();
        var nativeCell = Activator.CreateInstance(constructor.GetParameters()[0].ParameterType)!;
        ((IList)Get(rotation, "occupiedCells")).Add(constructor.Invoke(new[] { nativeCell }));
        ((IList)Get(result, "rotations")).Add(rotation);
        return result;
    }
    private static int Main(string[] args)
    {
        if (args.Length < 2) throw new ArgumentException("Pass the privately built Bridge DLL followed by native/SDK dependency directories.");
        var directories = args.Skip(1).Select(Path.GetFullPath).Concat(new[] { Path.GetDirectoryName(Path.GetFullPath(args[0]))! }).ToArray();
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
            var name = new AssemblyName(e.Name).Name + ".dll";
            var path = directories.Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
            return path == null ? null : Assembly.LoadFrom(path);
        };
        bridge = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        foreach (var reference in bridge.GetReferencedAssemblies()) Assembly.Load(reference);
        mapper = bridge.GetType("HomeBridge.BridgeTools.PlacementProtocol", true)!;
        const string row = "{\"defName\":\"SleepingSpot\",\"x\":0,\"z\":0,\"rotation\":\"ROTATION_NORTH\"}";
        const string identity = "\"identity\":{\"colonyId\":\"colony\",\"loadToken\":\"load\",\"mapId\":0}";
        Func<string, string> request = rows => "{" + identity + ",\"placements\":[" + rows + "]}";
        Assert(Valid(request(row)), "valid zero-coordinate request");
        Assert(Valid(request(row.Replace("NORTH", "ALL"))), "all rotations allowed for preview");
        Assert(Valid(request(row.Replace("}", ",\"stuff\":\"\"}"))), "empty material means default");
        Assert(!Valid("{\"placements\":[" + row + "]}"), "identity required");
        Assert(!Valid(request("")), "empty batch refused");
        Assert(Valid(request(string.Join(",", Enumerable.Repeat(row, 16)))), "sixteen candidates accepted");
        Assert(!Valid(request(string.Join(",", Enumerable.Repeat(row, 17)))), "seventeen candidates refused");
        foreach (var missing in new[] { "\"defName\":\"SleepingSpot\",", "\"x\":0,", "\"z\":0,", ",\"rotation\":\"ROTATION_NORTH\"" })
            Assert(!Valid(request(row.Replace(missing, ""))), "required candidate field " + missing);
        Assert(!Valid(request(row.Replace("ROTATION_NORTH", "ROTATION_UNSPECIFIED"))), "zero rotation refused");
        Assert(!Valid(request(row.Replace("\"ROTATION_NORTH\"", "999"))), "unknown numeric rotation refused");
        Assert(!Valid(request(row.Replace("SleepingSpot", new string('x', 257)))), "identifier byte limit");
        Assert(!Valid(request(row.Replace("SleepingSpot", " "))), "blank definition refused");
        foreach (var malformed in new[] { "{", "[]", "{\"unknown\":1}", "{\"placements\":{}}",
            request(row.Replace("\"x\":0", "\"x\":2147483648")),
            request(row.Replace("\"x\":0", "\"x\":[]")),
            request(row.Replace("ROTATION_NORTH", "ROTATION_FUTURE")) }) ParserRefuses(malformed);
        Assert(!Valid("{" + identity + ",\"placements\":null}"), "null repeated field has no candidates");
        Assert(!Valid(request(row.Replace("\"x\":0", "\"x\":null"))), "null coordinate does not have presence");
        var context = Wire("RimGovernor.Protocol.Common.ObservationContext", "{" + identity + ",\"tick\":\"0\"}");
        var domain = Evaluation();
        var projected = Map(domain, context);
        Assert(Get(projected, "OutcomeCase").ToString() == "Evaluated", "false native placement is evaluated");
        var evaluated = Get(projected, "Evaluated");
        Assert((bool)Get(evaluated, "HasCanPlace") && !(bool)Get(evaluated, "CanPlace"), "explicit false preserved");
        Assert(Get(Get(evaluated, "Materials"), "AvailabilityCase").ToString() == "Known", "complete empty material list is known");
        Set(Get(domain, "materials"), "unreadable", true);
        Assert(Get(Get(Get(Map(domain, context), "Evaluated"), "Materials"), "AvailabilityCase").ToString() == "Unavailable", "unreadable materials remain unavailable");
        var rotation = ((IList)Get(domain, "rotations"))[0]!;
        ((IList)Get(rotation, "occupiedCells")).Clear();
        Assert(Get(Map(domain, context), "OutcomeCase").ToString() == "Failure", "empty footprint fails candidate");
        domain = Evaluation();
        Set(domain, "passability", "future-native-value");
        Assert(Get(Map(domain, context), "OutcomeCase").ToString() == "Failure", "unknown passability fails candidate");
        domain = Evaluation();
        ((IList)Get(domain, "rotations")).Add(((IList)Get(domain, "rotations"))[0]);
        Assert(Get(Map(domain, context), "OutcomeCase").ToString() == "Failure", "duplicate rotations fail candidate");
        var oversized = Wire("RimGovernor.Protocol.Placement.PlacementReply", "{\"failure\":{\"code\":\"FAILURE_CODE_UNAVAILABLE\",\"detail\":\"" + new string('x', 1024 * 1024) + "\"}}");
        var bounded = mapper.GetMethod("Bounded", Members)!.Invoke(null, new[] { oversized })!;
        Assert(Get(Get(bounded, "Failure"), "Code").ToString() == "CapacityExhausted", "oversized result explicitly fails");
        Console.WriteLine(checks + " compiled placement protocol assertions passed; no gameplay assertions.");
        return 0;
    }
}
