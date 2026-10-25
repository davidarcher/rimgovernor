using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

// Exercises the actual compiled production mapper and the Contracts wire records.
// No game is created, no static definition database is seeded, no native outcome claimed.
internal static class NativeProtoPlacementProbe
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
        var type = typeof(RimGovernor.Contracts.Wire.WireJson).Assembly.GetType(name, true)!;
        return typeof(RimGovernor.Contracts.Wire.WireJson).GetMethod("Deserialize", new[] { typeof(string) })!.MakeGenericMethod(type).Invoke(null, new object[] { json })!;
    }
    private static bool Valid(string json)
    {
        var request = Wire("RimGovernor.Contracts.Wire.Placement.PlacementRequest", json);
        var arguments = new[] { request, null };
        return (bool)mapper.GetMethod("Validate", Members)!.Invoke(null, arguments)!;
    }
    private static void ParserRefuses(string json)
    {
        try { Wire("RimGovernor.Contracts.Wire.Placement.PlacementRequest", json); }
        catch (TargetInvocationException error)
        {
            var kind = error.InnerException?.GetType().FullName;
            Assert(error.InnerException is System.Text.Json.JsonException,
                "Wire decoder raises a classified boundary exception: " + kind);
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
    internal static int Invoke(string[] args)
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
        const string row = "{\"defName\":\"SleepingSpot\",\"x\":0,\"z\":0,\"rotation\":\"ROTATION_NORTH\",\"stuff\":\"\",\"ignoreNaturalRock\":false}";
        const string identity = "\"identity\":{\"colonyId\":\"colony\",\"loadToken\":\"load\",\"mapId\":0}";
        Func<string, string> request = rows => "{" + identity + ",\"placements\":[" + rows + "]}";
        Assert(Valid(request(row)), "valid zero-coordinate request");
        Assert(Valid(request(row.Replace("NORTH", "ALL"))), "all rotations allowed for preview");
        Assert(Valid(request(row)), "empty material means default");
        Assert(Valid(request(row.Replace("\"stuff\":\"\"", "\"stuff\":\"WoodLog\""))), "explicit material allowed");
        ParserRefuses("{\"placements\":[" + row + "]}");
        Assert(!Valid(request("")), "empty batch refused");
        Assert(Valid(request(string.Join(",", Enumerable.Repeat(row, 64)))), "sixty-four candidates accepted");
        Assert(!Valid(request(string.Join(",", Enumerable.Repeat(row, 65)))), "sixty-five candidates refused");
        foreach (var missing in new[] { "\"defName\":\"SleepingSpot\",", "\"x\":0,", "\"z\":0,", ",\"rotation\":\"ROTATION_NORTH\"", ",\"stuff\":\"\"", ",\"ignoreNaturalRock\":false" })
            ParserRefuses(request(row.Replace(missing, "")));
        ParserRefuses(request(row.Replace("ROTATION_NORTH", "ROTATION_UNSPECIFIED")));
        Assert(!Valid(request(row.Replace("SleepingSpot", new string('x', 257)))), "identifier byte limit");
        Assert(!Valid(request(row.Replace("SleepingSpot", " "))), "blank definition refused");
        foreach (var malformed in new[] { "{", "[]", "{\"unknown\":1}", "{\"placements\":{}}",
            request(row.Replace("\"x\":0", "\"x\":2147483648")),
            request(row.Replace("\"x\":0", "\"x\":[]")),
            // an enum is only ever its wire name: a bare number is refused by the decoder (it parsed and was refused by Validate under protobuf)
            request(row.Replace("\"ROTATION_NORTH\"", "999")),
            request(row.Replace("ROTATION_NORTH", "ROTATION_FUTURE")) }) ParserRefuses(malformed);
        // a JSON null list parsed to no candidates under protobuf; the wire decoder refuses it before Validate runs
        ParserRefuses("{" + identity + ",\"placements\":null}");
        ParserRefuses(request(row.Replace("\"x\":0", "\"x\":null")));
        var context = Wire("RimGovernor.Contracts.Wire.Common.ObservationContext", "{" + identity + ",\"tick\":0}");
        var domain = Evaluation();
        var projected = Map(domain, context);
        Assert(Get(projected, "Outcome").GetType().Name == "EvaluatedCase", "false native placement is evaluated");
        var evaluated = Get(Get(projected, "Outcome"), "Evaluated");
        Assert(Get(evaluated, "CanPlace") is false, "explicit false preserved");
        Assert(!((IEnumerable)Get(Get(evaluated, "Materials"), "Rows")).Cast<object>().Any(), "complete empty material list is whole");
        var mapped = ((IEnumerable)Get(evaluated, "Rotations")).Cast<object>().Single();
        Assert(Get(mapped, "WatchCells").ToString() == "NotWatched", "a definition nothing watches is not watched");
        Assert(Get(mapped, "Wind").GetType().Name == "NotWindTurbine", "a definition that is no turbine has no wind catch");
        var rotation = ((IList)Get(domain, "rotations"))[0]!;
        Set(rotation, "watchCellsAccessible", true);
        Set(rotation, "windBlockedCells", 3);
        mapped = ((IEnumerable)Get(Get(Get(Map(domain, context), "Outcome"), "Evaluated"), "Rotations")).Cast<object>().Single();
        Assert(Get(mapped, "WatchCells").ToString() == "Reachable", "a watch building every colonist reaches is reachable");
        Assert(Get(mapped, "Wind").GetType().Name == "WindTurbine" && Get(Get(mapped, "Wind"), "BlockedCells") is 3u, "an obstructed turbine catch zone is named");
        Set(rotation, "watchCellsAccessible", false);
        mapped = ((IEnumerable)Get(Get(Get(Map(domain, context), "Outcome"), "Evaluated"), "Rotations")).Cast<object>().Single();
        Assert(Get(mapped, "WatchCells").ToString() == "Unreachable", "a watch building a colonist cannot reach is unreachable");
        Set(rotation, "watchCellsAccessible", null!);
        Set(rotation, "windBlockedCells", null!);
        Set(Get(domain, "materials"), "unreadable", true);
        Assert(Get(Get(Map(domain, context), "Outcome"), "Failure") != null, "unreadable materials fail the candidate");
        Set(Get(domain, "materials"), "unreadable", false);
        ((IList)Get(rotation, "occupiedCells")).Clear();
        Assert(Get(Get(Map(domain, context), "Outcome"), "Failure") != null, "empty footprint fails candidate");
        domain = Evaluation();
        Set(domain, "passability", "future-native-value");
        Assert(Get(Get(Map(domain, context), "Outcome"), "Failure") != null, "unknown passability fails candidate");
        domain = Evaluation();
        ((IList)Get(domain, "rotations")).Add(((IList)Get(domain, "rotations"))[0]);
        Assert(Get(Get(Map(domain, context), "Outcome"), "Failure") != null, "duplicate rotations fail candidate");
        var oversized = Wire("RimGovernor.Contracts.Wire.Placement.PlacementReply", "{\"outcome\":{\"type\":\"failure\",\"value\":{\"code\":\"FAILURE_CODE_UNAVAILABLE\",\"detail\":\"" + new string('x', 1024 * 1024) + "\"}}}");
        var bounded = mapper.GetMethod("Bounded", Members)!.Invoke(null, new[] { oversized })!;
        Assert(Get(Get(Get(bounded, "Outcome"), "Failure"), "Code").ToString() == "CapacityExhausted", "oversized result explicitly fails");
        Console.WriteLine(checks + " compiled placement protocol assertions passed; no gameplay assertions.");
        return 0;
    }
}
