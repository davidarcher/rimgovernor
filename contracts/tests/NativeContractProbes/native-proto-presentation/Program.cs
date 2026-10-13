#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

internal static class NativeProtoPresentationProbe
{
    private static Assembly bridge = null!;
    private static Type tools = null!;
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
    private static object Wire(string name, string json)
    {
        var type = typeof(RimGovernor.Contracts.Wire.Common.Ref).Assembly.GetType("RimGovernor.Contracts.Wire.Presentation." + name, true)!;
        return typeof(RimGovernor.Contracts.Wire.WireJson).GetMethod("Deserialize", new[] { typeof(string) })!.MakeGenericMethod(type).Invoke(null, new object[] { json })!;
    }
    private static object Get(object value, string property) => value.GetType().GetProperty(property)!.GetValue(value)!;
    internal static int Invoke(string[] args)
    {
        var directories = args.Skip(1).Concat(new[] { Path.GetDirectoryName(Path.GetFullPath(args[0]))! }).ToArray();
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
            var path = directories.Select(d => Path.Combine(d, new AssemblyName(e.Name).Name + ".dll")).FirstOrDefault(File.Exists);
            return path == null ? null : Assembly.LoadFrom(path);
        };
        bridge = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        foreach (var reference in bridge.GetReferencedAssemblies()) Assembly.Load(reference);
        tools = bridge.GetType("HomeBridge.BridgeTools.NativePresentationReadTools", true)!;
        const string identity = "\"identity\":{\"colonyId\":\"colony\",\"loadToken\":\"load\",\"mapId\":0}";
        foreach (var pair in new[] { new[] { "ValidateRead", "ReadRequest" }, new[] { "ValidateColonists", "ColonistRosterRequest" } })
        {
            var validator = tools.GetMethod(pair[0], Flags)!;
            Check(!(bool)validator.Invoke(null, new object?[] { Wire(pair[1], "{}"), null })!, "Missing identity refused");
            Check((bool)validator.Invoke(null, new object?[] { Wire(pair[1], "{" + identity + "}"), null })!, "Identity presence accepted");
        }
        var roster = Wire("ColonistRosterRequest", "{" + identity + "}");
        Check(Get(roster, "CurrentMapOnly") is not true, "Default includes all loaded maps");
        foreach (var value in new[] { "false", "true" })
        {
            roster = Wire("ColonistRosterRequest", "{" + identity + ",\"currentMapOnly\":" + value + "}");
            Check(Get(roster, "CurrentMapOnly") is bool currentMapOnly && currentMapOnly == (value == "true"), "Explicit map scope retained");
        }
        var server = Assembly.LoadFrom(directories.Select(d => Path.Combine(d, "RimGovernor.Host.dll")).First(File.Exists));
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            try { tools.GetMethod("Finite", Flags)!.Invoke(null, new object[] { value }); throw new Exception("Nonfinite camera accepted"); }
            catch (TargetInvocationException e) { Check(e.InnerException is InvalidOperationException, "Nonfinite camera refused"); }
        }
        Check((double)tools.GetMethod("Finite", Flags)!.Invoke(null, new object[] { 0.0 })! == 0, "Zero map coordinate retained");
        var listing = tools.GetMethod("Listing", Flags)!.Invoke(null, new object[] { 0 })!;
        Check(Get(listing, "TotalCount") is uint totalCount && totalCount == 0
            && Get(listing, "Complete") is true && Get(listing, "Truncated") is false, "Complete empty selection differs from unavailable");
        var binder = server.GetType("RimGovernor.Host.AnnotatedExtensionCapabilityProvider", true)!.GetMethod("BindArguments", Flags)!;
        foreach (var method in new[] { "Camera", "Selection", "Colonists" })
            foreach (var value in new object?[] { "{}", new Dictionary<string, object>(), new List<object>(), null, 17, true })
            {
                var bound = (object[])binder.Invoke(null, new object?[] { tools.GetMethod(method),
                    new Dictionary<string, object?> { ["request"] = value }, null, CancellationToken.None })!;
                Check(ReferenceEquals(bound[2], value), "Real SDK preserves raw request: " + method);
            }
        var reply = Wire("SelectionReply", "{\"outcome\":{\"type\":\"selection\",\"value\":{\"listing\":{\"totalCount\":0,\"returnedCount\":0,\"complete\":true,\"truncated\":false}}}}");
        var envelope = tools.GetMethod("Encode", Flags)!.Invoke(null, new[] { reply })!;
        var normalize = server.GetType("RimGovernor.Host.LegacyToolExecution", true)!.GetMethod("ToDictionary", Flags)!;
        var normalized = (IDictionary)normalize.Invoke(null, new[] { envelope })!;
        Check(normalized.Count == 1 && normalized["payload"] is string, "SDK preserves sole ProtoJSON payload");
        var selected = Get(Get(Wire("SelectionReply", (string)normalized["payload"]!), "Outcome"), "Selection");
        Check(Get(selected, "Fingerprint") == null && Get(selected, "VisibleGizmoCount") == null, "Unproven optional capture/gizmo facts remain absent");
        var unavailable = Wire("CameraReply", "{\"outcome\":{\"type\":\"failure\",\"value\":{\"code\":\"FAILURE_CODE_UNAVAILABLE\"}}}");
        Check(Get(Get(unavailable, "Outcome"), "Camera") == null, "Unavailable camera never becomes zero geometry");
        Console.WriteLine(checks + " compiled presentation/SDK assertions passed; no gameplay assertions.");
        return 0;
    }
}
