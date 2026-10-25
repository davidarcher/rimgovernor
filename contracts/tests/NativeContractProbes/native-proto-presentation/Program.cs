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
    private static object Wire(Type type, string json)
    {

        return typeof(RimGovernor.Brain.GameOrders.OrdersJson).GetMethod(nameof(RimGovernor.Brain.GameOrders.OrdersJson.Deserialize), new[] { typeof(string) })!.MakeGenericMethod(type).Invoke(null, new object[] { json })!;
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
        tools = bridge.GetType("RimGovernor.Native.NativePresentationReadTools", true)!;
        const string identity = "\"identity\":{\"colonyId\":\"colony\",\"loadToken\":\"load\",\"mapId\":0}";
        foreach (var pair in new[] { ("ValidateRead", typeof(RimGovernor.Brain.GameOrders.Presentation.ReadRequest), ""), ("ValidateColonists", typeof(RimGovernor.Brain.GameOrders.Presentation.ColonistRosterRequest), ",\"currentMapOnly\":false") })
        {
            var validator = tools.GetMethod(pair.Item1, Flags)!;
            Check(!(bool)validator.Invoke(null, new object?[] { Wire(pair.Item2, "{" + pair.Item3.TrimStart(',') + "}"), null })!, "Missing identity refused");
            Check((bool)validator.Invoke(null, new object?[] { Wire(pair.Item2, "{" + identity + pair.Item3 + "}"), null })!, "Identity presence accepted");
        }
        try { Wire(typeof(RimGovernor.Brain.GameOrders.Presentation.ColonistRosterRequest),  "{" + identity + "}"); throw new Exception("Roster scope defaulted"); }
        catch (TargetInvocationException e) { Check(e.InnerException is System.Text.Json.JsonException, "Roster scope is required"); }
        object roster;
        foreach (var value in new[] { "false", "true" })
        {
            roster = Wire(typeof(RimGovernor.Brain.GameOrders.Presentation.ColonistRosterRequest),  "{" + identity + ",\"currentMapOnly\":" + value + "}");
            Check(Get(roster, "CurrentMapOnly") is bool currentMapOnly && currentMapOnly == (value == "true"), "Explicit map scope retained");
        }
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            try { tools.GetMethod("Finite", Flags)!.Invoke(null, new object[] { value }); throw new Exception("Nonfinite camera accepted"); }
            catch (TargetInvocationException e) { Check(e.InnerException is InvalidOperationException, "Nonfinite camera refused"); }
        }
        Check((double)tools.GetMethod("Finite", Flags)!.Invoke(null, new object[] { 0.0 })! == 0, "Zero map coordinate retained");
        var listing = tools.GetMethod("Listing", Flags)!.Invoke(null, new object[] { 0 })!;
        Check(Get(listing, "TotalCount") is uint totalCount && totalCount == 0
            && Get(listing, "Complete") is true && Get(listing, "Truncated") is false, "Complete empty selection differs from unavailable");
        var selected = Wire(typeof(RimGovernor.Brain.GameFacts.Presentation.SelectionSnapshot),  "{\"listing\":{\"totalCount\":0,\"returnedCount\":0,\"complete\":true,\"truncated\":false}}");
        Check(selected.GetType().GetProperty("Fingerprint")!.GetValue(selected) == null, "Unproven optional selection fingerprint remains absent");
        Console.WriteLine(checks + " compiled presentation assertions passed; no gameplay assertions.");
        return 0;
    }
}
