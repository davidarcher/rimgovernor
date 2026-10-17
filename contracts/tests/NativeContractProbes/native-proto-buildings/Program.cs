#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

internal static class NativeProtoBuildingsProbe
{
    private static Assembly bridge = null!;
    private static Type tools = null!;
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
    private static object Wire(string name, string json)
    {
        var type = typeof(RimGovernor.Contracts.Wire.Common.Ref).Assembly.GetType("RimGovernor.Contracts.Wire.Observations." + name, true)!;
        return typeof(RimGovernor.Contracts.Wire.WireJson).GetMethod("Deserialize", new[] { typeof(string) })!.MakeGenericMethod(type).Invoke(null, new object[] { json })!;
    }
    private static object Get(object value, string property) => value.GetType().GetProperty(property)!.GetValue(value)!;
    private static bool Valid(string json) => (bool)tools.GetMethod("Validate", Flags)!.Invoke(null,
        new object?[] { Wire("ListBuildingsRequest", json), null })!;
    internal static int Invoke(string[] args)
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
        foreach (var fields in new[] { "", "\"anyFaction\":true",
            "\"statuses\":[\"BUILDING_STATUS_BLUEPRINT\",\"BUILDING_STATUS_BUILT\"],\"defNames\":[\"Wall\"],\"ids\":[\"Wall17\"]" })
            Check(Valid(request(fields)), "Valid request preserves explicit values: " + fields);
        Check(!Valid("{}"), "Missing identity scope refused");
        foreach (var fields in new[] { "\"ids\":[\"same\",\"same\"]", "\"defNames\":[\"\"]", "\"ids\":[\"bad\\u0000id\"]" })
            Check(!Valid(request(fields)), "Invalid request refused: " + fields);
        var ids = string.Join(",", Enumerable.Range(0, 257).Select(i => "\"thing" + i + "\""));
        Check(!Valid(request("\"ids\":[" + ids + "]")), "ID filter bound enforced");
        var snapshot = Get(Get(Wire("ListBuildingsReply", "{\"outcome\":{\"type\":\"observed\",\"value\":{\"buildings\":[{\"building\":{\"id\":\"Wall17\"},\"construction\":{}}]}}}"), "Outcome"), "Observed");
        var row = ((IList)Get(snapshot, "Buildings"))[0]!;
        Check(Get(row, "Burning") is false, "Omitted burning reads false");
        Check(Get(Get(row, "Construction"), "ResourcesComplete") is false, "Omitted resources_complete reads false");
        Console.WriteLine(checks + " compiled building boundary assertions passed; no gameplay assertions.");
        return 0;
    }
}
