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
        new object?[] { Wire("ListSuppliesRequest", json), null })!;
    private static int Main(string[] args)
    {
        var directories = args.Skip(1).Concat(new[] { Path.GetDirectoryName(Path.GetFullPath(args[0]))! }).ToArray();
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
            var path = directories.Select(d => Path.Combine(d, new AssemblyName(e.Name).Name + ".dll")).FirstOrDefault(File.Exists);
            return path == null ? null : Assembly.LoadFrom(path);
        };
        bridge = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        foreach (var reference in bridge.GetReferencedAssemblies()) Assembly.Load(reference);
        tools = bridge.GetType("HomeBridge.BridgeTools.NativeSuppliesObservationTools", true)!;
        const string scope = "\"scope\":{\"expectedIdentity\":{\"colonyId\":\"colony\",\"loadToken\":\"load\",\"mapId\":0}}";
        Func<string, string> request = fields => "{" + scope + (fields.Length == 0 ? "" : "," + fields) + "}";
        Check(Valid(request("")), "Default census accepted");
        foreach (var category in new[] { "haulable", "food", "weapons", "all", "buildings" })
            Check(Valid(request("\"filter\":{\"category\":\"" + category + "\",\"ownership\":\"all\",\"includeHeld\":false}")), "Supported category/explicit false");
        Check(Valid(request("\"filter\":{\"defNames\":[\"Modded_Resourceα\"],\"corpses\":true,\"forbiddenOnly\":true,\"excludeChunks\":true}")), "Open native def identifiers and combined filters");
        foreach (var invalid in new[] { "{}", request("\"page\":{\"limit\":0}"), request("\"page\":{\"limit\":257}"),
            request("\"page\":{\"cursor\":\"stale\"}"), request("\"filter\":{\"category\":\"FOOD\"}"),
            request("\"filter\":{\"ownership\":\"enemy\"}"), request("\"filter\":{\"defNames\":[\"a\",\"a\"]}"),
            request("\"filter\":{\"defNames\":[\"\"]}"), request("\"filter\":{\"defNames\":[\"bad\\u0000id\"]}"),
            request("\"filter\":{\"region\":{\"minimum\":{\"x\":1,\"z\":0},\"maximum\":{\"x\":0,\"z\":0}}}") })
            Check(!Valid(invalid), "Invalid bounded request refused");
        var filter = Wire("StockFilter", "{}");
        Check((string)tools.GetMethod("Category", Flags)!.Invoke(null, new[] { filter })! == "haulable", "Default category unchanged");
        Check((string)tools.GetMethod("Ownership", Flags)!.Invoke(null, new[] { filter })! == "ours", "Default ownership unchanged");
        Check((bool)tools.GetMethod("IncludeHeld", Flags)!.Invoke(null, new[] { filter })!, "Held stock included by default");
        Func<bool, bool, bool, bool, bool, bool> ours = (fog, held, player, other, dead) =>
            (bool)tools.GetMethod("IsOurs", Flags)!.Invoke(null, new object[] { fog, held, player, other, dead })!;
        Check(ours(false, false, false, false, false), "Visible neutral spawned stock is usable");
        Check(!ours(true, false, true, false, false), "Fogged stock not usable even if player faction");
        Check(!ours(false, false, false, true, false), "Other faction spawned stock not ours");
        Check(ours(false, true, true, false, false), "Living player pawn held stock ours");
        Check(!ours(false, true, true, false, true), "Dead pawn held stock not ours");
        Check(!ours(false, true, false, true, false), "Trader held stock not ours");
        var server = Assembly.LoadFrom(directories.Select(d => Path.Combine(d, "RimBridgeServer.dll")).First(File.Exists));
        var binder = server.GetType("RimBridgeServer.AnnotatedExtensionCapabilityProvider", true)!.GetMethod("BindArguments", Flags)!;
        foreach (var value in new object?[] { "{}", new Dictionary<string, object>(), new List<object>(), null, 17, true })
        {
            var bound = (object[])binder.Invoke(null, new object?[] { tools.GetMethod("ListSupplies"),
                new Dictionary<string, object?> { ["request"] = value }, null, CancellationToken.None })!;
            Check(ReferenceEquals(bound[2], value), "Real SDK preserves raw request");
        }
        var reply = Wire("ListSuppliesReply", "{\"observed\":{\"stocks\":[{\"definition\":{\"defName\":\"Modded_Resourceα\"},\"units\":\"2147483648\",\"ours\":\"0\",\"holdersCompleteness\":{\"page\":{\"complete\":false}},\"issues\":[{\"field\":\"carried\",\"unavailable\":{\"reason\":\"UNAVAILABLE_REASON_NOT_REQUESTED\"}}]}]}}");
        var envelope = tools.GetMethod("Encode", Flags)!.Invoke(null, new[] { reply })!;
        var normalize = server.GetType("RimBridgeServer.LegacyToolExecution", true)!.GetMethod("ToDictionary", Flags)!;
        var normalized = (IDictionary)normalize.Invoke(null, new[] { envelope })!;
        Check(normalized.Count == 1 && normalized["payload"] is string, "SDK preserves ProtoJSON string");
        var snapshot = Get(Wire("ListSuppliesReply", (string)normalized["payload"]!), "Observed");
        var row = ((IList)Get(snapshot, "Stocks"))[0]!;
        Check((long)Get(row, "Units") == 2147483648L, "64-bit quantities roundtrip");
        Check((bool)Get(row, "HasOurs") && (long)Get(row, "Ours") == 0, "Known zero usable stock retained");
        Check(!(bool)Get(row, "HasCarried") && !(bool)Get(row, "HasInContainer"), "Unrequested held stock remains unknown");
        var oversized = Wire("ListSuppliesReply", "{\"unavailable\":{\"detail\":\"" + new string('x', 1024 * 1024) + "\"}}");
        try { tools.GetMethod("Encode", Flags)!.Invoke(null, new[] { oversized }); throw new Exception("Oversized reply accepted"); }
        catch (TargetInvocationException error) { Check(error.InnerException!.GetType().Name == "ReadLimit", "Oversize cannot truncate success"); }
        Console.WriteLine(checks + " compiled supplies boundary assertions passed; no gameplay assertions.");
        return 0;
    }
}
