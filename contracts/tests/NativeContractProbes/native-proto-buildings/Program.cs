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
            "\"statuses\":[\"blueprint\",\"built\"],\"defNames\":[\"Wall\"],\"ids\":[\"Wall17\"]" })
            Check(Valid(request(fields)), "Valid request preserves explicit values: " + fields);
        Check(!Valid("{}"), "Missing identity scope refused");
        foreach (var fields in new[] { "\"ids\":[\"same\",\"same\"]", "\"defNames\":[\"\"]", "\"ids\":[\"bad\\u0000id\"]" })
            Check(!Valid(request(fields)), "Invalid request refused: " + fields);
        var ids = string.Join(",", Enumerable.Range(0, 257).Select(i => "\"thing" + i + "\""));
        Check(!Valid(request("\"ids\":[" + ids + "]")), "ID filter bound enforced");
        const string context = "\"context\":{\"identity\":{\"colonyId\":\"colony\",\"loadToken\":\"load\",\"mapId\":0},\"tick\":1}";
        const string build = "{\"type\":\"build\",\"definition\":\"Wall\",\"workLeft\":1,\"resources\":[{\"defName\":\"Steel\",\"stillNeeded\":0}],"
            + "\"qualitySensitive\":false,\"nativeFinishingSkill\":0,\"minimum\":null,\"gate\":{\"type\":\"tiered\",\"tier\":\"survive\"}}";
        Func<string, string> buildings = row => "{" + context + ",\"buildings\":[{\"building\":{\"id\":\"Wall17\"},\"rotation\":\"ROTATION_NORTH\",\"stuff\":null,"
            + "\"faction\":null,\"status\":\"frame\",\"hitPoints\":{\"type\":\"tracked\",\"current\":5,\"maximum\":10},\"service\":{},\"settings\":{},"
            + "\"occupied\":{\"minimum\":{\"x\":1,\"z\":1},\"maximum\":{\"x\":1,\"z\":1}},\"anomaly\":null,\"door\":null" + row + "}]}";
        var snapshot = Wire("BuildingsSnapshot", buildings(",\"burning\":false,\"construction\":" + build));
        var construction = Get(((IList)Get(snapshot, "Buildings"))[0]!, "Construction");
        Check(construction.GetType().Name == "Build" && Get(construction, "Definition") is "Wall", "A frame row carries its build state");
        Check(Get(Get(construction, "Gate"), "Tier").ToString() == "Survive", "A tiered gate names its tier");
        foreach (var (missing, row) in new[] { ("burning", ",\"construction\":" + build), ("construction", ",\"burning\":false") })
        {
            var refused = false;
            try { Wire("BuildingsSnapshot", buildings(row)); }
            catch (TargetInvocationException) { refused = true; }
            Check(refused, "A row missing its " + missing + " is refused");
        }
        Console.WriteLine(checks + " compiled building boundary assertions passed; no gameplay assertions.");
        return 0;
    }
}
