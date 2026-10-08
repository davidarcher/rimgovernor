#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;

// #2410: the RemoveProductionBillIntent arm of Actions/Apply. Checks the wire
// shape, that Actions/Apply maps the arm to a handler, and that a malformed
// intent is refused InvalidRequest before any live game state is read. The
// live rules (bill gone, bill worked, unfinished item bound, idle bill
// deleted from the bench) need a map and are covered by the nightly
// acceptance. args[0] is the built bridge DLL; further args are dependency
// directories (game Managed, SDK, Harmony).
internal static class NativeRemoveProductionBillProbe
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static Assembly bridge = null!;
    private static int checks;

    private static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }

    private static object Wire(string name, string json)
    {
        var parser = bridge.GetType("RimGovernor.Protocol." + name, true)!.GetProperty("Parser")!.GetValue(null)!;
        return parser.GetType().GetMethod("ParseJson")!.Invoke(parser, new object[] { json })!;
    }

    private static object? Get(object obj, string name) => obj.GetType().GetProperty(name, Flags)!.GetValue(obj);

    internal static int Invoke(string[] args)
    {
        var directories = args.Skip(1).Concat(new[] { Path.GetDirectoryName(Path.GetFullPath(args[0]))! }).ToArray();
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
            var path = directories.Select(d => Path.Combine(d, new AssemblyName(e.Name).Name + ".dll")).FirstOrDefault(File.Exists);
            return path == null ? null : Assembly.LoadFrom(path);
        };
        bridge = Assembly.LoadFrom(Path.GetFullPath(args[0]));

        var action = Wire("Operations.Action", "{\"key\":\"k\",\"removeProductionBill\":{\"benchId\":\"Bench_1\",\"bill\":{\"id\":\"Bill_Production_77\"}}}");
        Check(Get(action, "IntentCase")!.ToString() == "RemoveProductionBill", "remove_production_bill arm parses");
        var intent = Get(action, "RemoveProductionBill")!;
        Check((string)Get(intent, "BenchId")! == "Bench_1" && (string)Get(Get(intent, "Bill")!, "Id")! == "Bill_Production_77", "remove_production_bill fields round trip");

        var dispatch = bridge.GetType("HomeBridge.BridgeTools.NativeActionDispatch", true)!;
        dispatch.GetMethod("AssertComplete", Flags)!.Invoke(null, null);
        var handlers = (System.Collections.IDictionary)dispatch.GetField("Handlers", Flags)!.GetValue(null)!;
        Check(handlers.Keys.Cast<object>().Any(k => k.ToString() == "RemoveProductionBill"), "Actions/Apply has a RemoveProductionBill handler");

        var handler = Activator.CreateInstance(bridge.GetType("HomeBridge.BridgeTools.RemoveProductionBillActionHandler", true)!, true)!;
        var validate = handler.GetType().GetMethod("Validate", Flags)!;
        var context = Wire("Common.ObservationContext", "{\"identity\":{\"colonyId\":\"colony\",\"loadToken\":\"load\",\"mapId\":0},\"tick\":\"10\",\"nativeGeneration\":\"1\"}");
        foreach (var json in new[] {
            "{}",
            "{\"bill\":{\"id\":\"Bill_Production_77\"}}",
            "{\"benchId\":\"Bench_1\"}",
            "{\"benchId\":\"Bench_1\",\"bill\":{}}",
            "{\"benchId\":\"\",\"bill\":{\"id\":\"Bill_Production_77\"}}",
            "{\"benchId\":\"Bench_1\",\"bill\":{\"id\":\" \"}}",
        })
        {
            var failure = validate.Invoke(handler, new[] { Wire("Operations.Action", "{\"key\":\"k\",\"removeProductionBill\":" + json + "}"), context });
            Check(failure != null && Get(failure, "Code")!.ToString() == "InvalidRequest", "malformed remove_production_bill refused: " + json);
        }
        Console.WriteLine($"native-remove-production-bill: {checks} checks passed");
        return 0;
    }
}
