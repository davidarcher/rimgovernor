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
        var allow = bridge.GetType("HomeBridge.BridgeTools.NativeSupplyAllow", true)!;
        Func<string, string, object> parse = (type, json) => {
            var parser = bridge.GetType("RimGovernor.Protocol." + type, true)!.GetProperty("Parser")!.GetValue(null)!;
            return parser.GetType().GetMethod("ParseJson")!.Invoke(parser, new object[] { json })!;
        };
        Func<string, bool> validAllow = json => (bool)allow.GetMethod("Valid", Flags)!.Invoke(null,
            new[] { parse("Operations.DesignateThing", json) })!;
        const string target = "\"target\":{\"entityId\":\"Thing_Supply1\",\"expectedSnapshotToken\":\"token\"}";
        Check(validAllow("{" + target + ",\"designation\":\"THING_DESIGNATION_ALLOW\"}"), "Exact Allow accepted");
        foreach (var designation in new[] { "FORBID", "HUNT", "HARVEST_PLANT", "DECONSTRUCT", "UNSPECIFIED" })
            Check(!validAllow("{" + target + ",\"designation\":\"THING_DESIGNATION_" + designation + "\"}"), "Allow cannot widen to " + designation);
        foreach (var invalid in new[] { "{}", "{" + target + "}",
            "{\"target\":{\"entityId\":\"Thing_Supply1\"},\"designation\":1}",
            "{\"target\":{\"entityId\":\"\",\"expectedSnapshotToken\":\"token\"},\"designation\":1}" })
            Check(!validAllow(invalid), "Missing Allow presence or entity snapshot refused");
        var identity = parse("Common.Identity", "{\"colonyId\":\"colony\",\"loadToken\":\"load\",\"mapId\":0}");
        object[] tokenInputs = { identity, "Thing_Supply1", "Meal", 1, 2, 10, true, "" };
        Func<object[], string> token = values => (string)allow.GetMethod("Token", Flags)!.Invoke(null, values)!;
        var originalToken = token(tokenInputs);
        Check(originalToken == token(tokenInputs), "Unchanged supply token stable across polling");
        var changedValues = new object[] { parse("Common.Identity", "{\"colonyId\":\"colony\",\"loadToken\":\"new-load\",\"mapId\":0}"),
            "Thing_Supply2", "WoodLog", 2, 3, 9, false, "OtherFaction" };
        for (var index = 0; index < tokenInputs.Length; index++) {
            var changed = (object[])tokenInputs.Clone(); changed[index] = changedValues[index];
            Check(originalToken != token(changed), "Supply CAS binds field " + index);
        }
        foreach (var otherIdentity in new[] { "{\"colonyId\":\"other\",\"loadToken\":\"load\",\"mapId\":0}",
            "{\"colonyId\":\"colony\",\"loadToken\":\"load\",\"mapId\":1}" }) {
            var changed = (object[])tokenInputs.Clone(); changed[0] = parse("Common.Identity", otherIdentity);
            Check(originalToken != token(changed), "Supply CAS cannot cross colony/map");
        }
        const string scope = "\"scope\":{\"expectedIdentity\":{\"colonyId\":\"colony\",\"loadToken\":\"load\",\"mapId\":0}}";
        var attempt = parse("Common.AttemptKey", "{\"controllerSessionId\":\"owner\",\"actionId\":\"allow\",\"attemptId\":\"1\"}");
        var context = parse("Common.ObservationContext", "{\"identity\":{\"colonyId\":\"colony\",\"loadToken\":\"load\",\"mapId\":0},\"tick\":\"1\"}");
        const string effectFields = "\"thingId\":\"Thing_Supply1\",\"resourceDef\":\"Meal\",\"designationDef\":\"Allow\"";
        var original = parse("Receipts.DesignationEffect", "{" + effectFields + ",\"present\":true}");
        Func<object?, object> progress = observed => allow.GetMethod("ObservedProgress", Flags)!.Invoke(null, new[] { attempt, context, original, observed })!;
        Check(Get(progress(original), "EffectCase").ToString() == "Completed", "Exact later allowed observation completes");
        var forbiddenAgain = parse("Receipts.DesignationEffect", "{" + effectFields + ",\"present\":false}");
        Check(Get(progress(forbiddenAgain), "EffectCase").ToString() == "Unsuccessful", "Re-forbidden supply cannot certify recovery");
        foreach (var unknown in new object?[] { null, parse("Receipts.DesignationEffect", "{" + effectFields + "}"),
            parse("Receipts.DesignationEffect", "{\"thingId\":\"other\",\"resourceDef\":\"Meal\",\"designationDef\":\"Allow\",\"present\":true}"),
            parse("Receipts.DesignationEffect", "{\"thingId\":\"Thing_Supply1\",\"resourceDef\":\"WoodLog\",\"designationDef\":\"Allow\",\"present\":true}") }) {
            var result = progress(unknown);
            Check(Get(result, "EffectCase").ToString() == "Unknown" && !(bool)Get(result, "CompleteInspection"), "Missing/foreign evidence cannot complete");
        }
        Func<string, string> request = fields => "{" + scope + (fields.Length == 0 ? "" : "," + fields) + "}";
        Check(Valid(request("")), "Default census accepted");
        foreach (var category in new[] { "haulable", "food", "weapons", "all", "buildings" })
            Check(Valid(request("\"filter\":{\"category\":\"" + category + "\",\"ownership\":\"all\",\"includeHeld\":false}")), "Supported category/explicit false");
        Check(Valid(request("\"filter\":{\"defNames\":[\"Modded_Resourceα\"],\"corpses\":true,\"forbiddenOnly\":true,\"excludeChunks\":true}")), "Open native def identifiers and combined filters");
        // N01.03: frozen paging is no longer refused outright -- a cursor within the
        // byte bound is accepted (its actual freshness is checked at read time by the
        // shared NativeObservationSnapshot.Cursor helper, not by Validate).
        Check(Valid(request("\"page\":{\"cursor\":\"" + new string('a', 4096) + "\"}")), "A within-bound cursor is accepted by Validate");
        foreach (var invalid in new[] { "{}", request("\"page\":{\"limit\":0}"), request("\"page\":{\"limit\":257}"),
            request("\"page\":{\"cursor\":\"" + new string('a', 4097) + "\"}"), request("\"filter\":{\"category\":\"FOOD\"}"),
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
