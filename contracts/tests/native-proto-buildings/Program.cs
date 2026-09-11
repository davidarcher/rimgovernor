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
    private static bool Valid(string json) => (bool)tools.GetMethod("Validate", Flags)!.Invoke(null,
        new object?[] { Wire("ListBuildingsRequest", json), null })!;
    private static int Main(string[] args)
    {
        var directories = args.Skip(1).Concat(new[] { Path.GetDirectoryName(Path.GetFullPath(args[0]))! }).ToArray();
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
            var path = directories.Select(d => Path.Combine(d, new AssemblyName(e.Name).Name + ".dll")).FirstOrDefault(File.Exists);
            return path == null ? null : Assembly.LoadFrom(path);
        };
        bridge = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        foreach (var reference in bridge.GetReferencedAssemblies()) Assembly.Load(reference);
        tools = bridge.GetType("HomeBridge.BridgeTools.NativeBuildingObservationTools", true)!;
        const string scope = "\"scope\":{\"expectedIdentity\":{\"colonyId\":\"colony\",\"loadToken\":\"load\",\"mapId\":0}}";
        Func<string, string> request = fields => "{" + scope + (fields.Length == 0 ? "" : "," + fields) + "}";
        foreach (var fields in new[] { "", "\"playerOnly\":false,\"inspect\":false,\"billIngredients\":false",
            "\"category\":\"all\",\"statuses\":[\"pending\",\"built\"],\"defNames\":[\"Wall\"],\"ids\":[\"Wall17\"]",
            "\"damagedBelowFraction\":0", "\"damagedBelowFraction\":1", "\"page\":{\"limit\":256}",
            "\"region\":{\"minimum\":{\"x\":0,\"z\":0},\"maximum\":{\"x\":0,\"z\":0}}" })
            Check(Valid(request(fields)), "Valid request preserves explicit values: " + fields);
        Check(!Valid("{}"), "Missing identity scope refused");
        foreach (var fields in new[] { "\"page\":{\"limit\":0}", "\"page\":{\"limit\":257}", "\"page\":{\"cursor\":\"expired\"}",
            "\"inspect\":true", "\"billIngredients\":true", "\"category\":\"ALL\"", "\"statuses\":[\"unknown\"]",
            "\"statuses\":[\"built\",\"built\"]", "\"ids\":[\"same\",\"same\"]", "\"defNames\":[\"\"]",
            "\"ids\":[\"bad\\u0000id\"]", "\"damagedBelowFraction\":-0.1", "\"damagedBelowFraction\":1.1",
            "\"damagedBelowFraction\":\"NaN\"", "\"damagedBelowFraction\":\"Infinity\"",
            "\"region\":{\"minimum\":{\"x\":0},\"maximum\":{\"x\":0,\"z\":0}}",
            "\"region\":{\"minimum\":{\"x\":1,\"z\":0},\"maximum\":{\"x\":0,\"z\":0}}" })
            Check(!Valid(request(fields)), "Invalid or unsupported request refused: " + fields);
        var ids = string.Join(",", Enumerable.Range(0, 257).Select(i => "\"thing" + i + "\""));
        Check(!Valid(request("\"ids\":[" + ids + "]")), "ID filter bound enforced");
        var server = Assembly.LoadFrom(directories.Select(d => Path.Combine(d, "RimBridgeServer.dll")).First(File.Exists));
        var binder = server.GetType("RimBridgeServer.AnnotatedExtensionCapabilityProvider", true)!.GetMethod("BindArguments", Flags)!;
        foreach (var value in new object?[] { "{}", new Dictionary<string, object>(), new List<object>(), null, 17, true })
        {
            var bound = (object[])binder.Invoke(null, new object?[] { tools.GetMethod("ListBuildings"),
                new Dictionary<string, object?> { ["request"] = value }, null, CancellationToken.None })!;
            Check(ReferenceEquals(bound[2], value), "Real SDK preserves raw request");
        }
        var reply = Wire("ListBuildingsReply", "{\"observed\":{\"buildings\":[{\"building\":{\"id\":\"Wall17\"},\"burning\":false,\"usesHitPoints\":true,\"construction\":{\"resourcesComplete\":false}}],\"completeness\":{\"page\":{\"complete\":true},\"matched\":\"1\",\"returned\":\"1\",\"unreadable\":\"0\"},\"networksCompleteness\":{\"page\":{\"complete\":false}}}}");
        var envelope = tools.GetMethod("Encode", Flags)!.Invoke(null, new[] { reply })!;
        var normalize = server.GetType("RimBridgeServer.LegacyToolExecution", true)!.GetMethod("ToDictionary", Flags)!;
        var normalized = (IDictionary)normalize.Invoke(null, new[] { envelope })!;
        Check(normalized.Count == 1 && normalized["payload"] is string, "SDK retains sole ProtoJSON payload");
        var snapshot = Get(Wire("ListBuildingsReply", (string)normalized["payload"]!), "Observed");
        var row = ((IList)Get(snapshot, "Buildings"))[0]!;
        Check((bool)Get(row, "HasBurning") && !(bool)Get(row, "Burning"), "Known false burning retained");
        Check(Get(Get(row, "Building"), "Snapshot") == null, "No fabricated CAS snapshot");
        var networks = Get(snapshot, "NetworksCompleteness");
        Check(!(bool)Get(Get(networks, "Page"), "Complete") && !(bool)Get(networks, "HasMatched"), "Unimplemented networks remain incomplete and unknown count");
        var oversized = Wire("ListBuildingsReply", "{\"observed\":{\"buildings\":[{\"inspectText\":\"" + new string('x', 1024 * 1024) + "\"}]}}");
        try { tools.GetMethod("Encode", Flags)!.Invoke(null, new[] { oversized }); throw new Exception("Oversized reply accepted"); }
        catch (TargetInvocationException error) { Check(error.InnerException!.GetType().Name == "ReadLimit", "Oversize cannot produce truncated success"); }
        Console.WriteLine(checks + " compiled building boundary assertions passed; no gameplay assertions.");
        return 0;
    }
}
