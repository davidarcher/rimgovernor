#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;

// #1352: the GiveJobIntent arm of Actions/Apply. Checks the wire shape, that
// Actions/Apply maps the arm to a handler, that each job shape reaches its
// arm, and that malformed intents are refused InvalidRequest before any live
// game state is read. The live game-rule checks are covered by the
// acceptance cases of each job. args[0] is the built bridge DLL; further args
// are dependency directories (game Managed, SDK, Harmony).
internal static class NativeGiveJobProbe
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

        var action = Wire("Operations.Action", "{\"key\":\"k\",\"giveJob\":{\"pawn\":{\"id\":\"Human1\"},\"job\":\"UseItem\",\"targets\":[{\"id\":\"Apparel_PsychicShockLance7\"},{\"id\":\"Human2\"}]}}");
        Check(Get(action, "IntentCase")!.ToString() == "GiveJob", "give_job arm parses");
        var intent = Get(action, "GiveJob")!;
        Check((string)Get(Get(intent, "Pawn")!, "Id")! == "Human1" && (string)Get(intent, "Job")! == "UseItem", "give_job fields round trip");

        var dispatch = bridge.GetType("HomeBridge.BridgeTools.NativeActionDispatch", true)!;
        dispatch.GetMethod("AssertComplete", Flags)!.Invoke(null, null);
        var handlers = (System.Collections.IDictionary)dispatch.GetField("Handlers", Flags)!.GetValue(null)!;
        Check(handlers.Keys.Cast<object>().Any(k => k.ToString() == "GiveJob"), "Actions/Apply has a GiveJob handler");

        var giveJob = bridge.GetType("HomeBridge.BridgeTools.NativeGiveJob", true)!;
        var classify = giveJob.GetMethod("Classify", Flags)!;
        string Arm(string json) => classify.Invoke(null, new object?[] { Wire("Operations.GiveJobIntent", json), null, null })!.ToString();
        Check(Arm("{\"pawn\":{\"id\":\"p\"},\"job\":\"UseItem\",\"targets\":[{\"id\":\"i\"},{\"id\":\"t\"}]}") == "Use", "UseItem arm");
        Check(Arm("{\"pawn\":{\"id\":\"p\"},\"job\":\"Repair\",\"targets\":[{\"id\":\"t\"}]}") == "Order", "Repair arm");
        Check(Arm("{\"pawn\":{\"id\":\"p\"},\"job\":\"Arrest\",\"targets\":[{\"id\":\"t\"},{\"id\":\"b\"}]}") == "Order", "Arrest arm");
        Check(Arm("{\"pawn\":{\"id\":\"p\"},\"options\":{\"relieveNeed\":\"NEED_FOOD\"}}") == "Need", "need relief arm");
        Check(Arm("{\"pawn\":{\"id\":\"p\"},\"job\":\"FinishFrame\",\"targets\":[{\"id\":\"f\"}],\"options\":{\"prioritized\":true}}") == "Prioritized", "prioritized arm");

        var handler = Activator.CreateInstance(bridge.GetType("HomeBridge.BridgeTools.GiveJobActionHandler", true)!, true)!;
        var validate = handler.GetType().GetMethod("Validate", Flags)!;
        var context = Wire("Common.ObservationContext", "{\"identity\":{\"colonyId\":\"colony\",\"loadToken\":\"load\",\"mapId\":0},\"tick\":\"10\",\"nativeGeneration\":\"1\"}");
        foreach (var json in new[] {
            "{}",
            "{\"job\":\"Repair\",\"targets\":[{\"id\":\"t\"}]}",
            "{\"pawn\":{\"id\":\"p\"},\"targets\":[{\"id\":\"t\"}]}",
            "{\"pawn\":{\"id\":\"p\"},\"job\":\"Repair\"}",
            "{\"pawn\":{\"id\":\"p\"},\"job\":\"Repair\",\"targets\":[{\"id\":\"p\"}]}",
            "{\"pawn\":{\"id\":\"p\"},\"job\":\"Arrest\",\"targets\":[{\"id\":\"t\"}]}",
            "{\"pawn\":{\"id\":\"p\"},\"job\":\"UseItem\",\"targets\":[{\"id\":\"i\"}]}",
            "{\"pawn\":{\"id\":\"p\"},\"job\":\"FinishFrame\",\"targets\":[{\"id\":\"f\"}]}",
            "{\"pawn\":{\"id\":\"p\"},\"job\":\"Ingest\",\"options\":{\"relieveNeed\":\"NEED_FOOD\"}}",
        })
        {
            var failure = validate.Invoke(handler, new[] { Wire("Operations.Action", "{\"key\":\"k\",\"giveJob\":" + json + "}"), context });
            Check(failure != null && Get(failure, "Code")!.ToString() == "InvalidRequest", "malformed give_job refused: " + json);
        }
        Console.WriteLine($"native-give-job: {checks} checks passed");
        return 0;
    }
}
