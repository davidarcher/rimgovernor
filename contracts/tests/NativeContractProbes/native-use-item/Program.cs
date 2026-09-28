#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;

// #1038: the UseItemIntent arm of Actions/Apply. Checks the wire shape, that
// Actions/Apply maps the arm to a handler, and that malformed intents are
// refused InvalidRequest before any live game state is read. The live
// verb and target checks are covered by the combat/shock-lance acceptance
// case. args[0] is the built bridge DLL; further args are dependency
// directories (game Managed, SDK, Harmony).
internal static class NativeUseItemProbe
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

        var action = Wire("Operations.Action", "{\"key\":\"k\",\"useItem\":{\"pawnId\":\"Human1\",\"itemId\":\"Apparel_PsychicShockLance7\",\"targetId\":\"Human2\"}}");
        Check(Get(action, "IntentCase")!.ToString() == "UseItem", "use_item arm parses");
        var intent = Get(action, "UseItem")!;
        Check((string)Get(intent, "PawnId")! == "Human1" && (string)Get(intent, "ItemId")! == "Apparel_PsychicShockLance7" && (string)Get(intent, "TargetId")! == "Human2", "use_item fields round trip");

        var dispatch = bridge.GetType("HomeBridge.BridgeTools.NativeActionDispatch", true)!;
        dispatch.GetMethod("AssertComplete", Flags)!.Invoke(null, null);
        var handlers = (System.Collections.IDictionary)dispatch.GetField("Handlers", Flags)!.GetValue(null)!;
        Check(handlers.Keys.Cast<object>().Any(k => k.ToString() == "UseItem"), "Actions/Apply has a UseItem handler");

        var operations = bridge.GetType("HomeBridge.BridgeTools.NativeUseItemOperations", true)!;
        var context = Wire("Common.ObservationContext", "{\"identity\":{\"colonyId\":\"colony\",\"loadToken\":\"load\",\"mapId\":0},\"tick\":\"10\",\"nativeGeneration\":\"1\"}");
        foreach (var json in new[] {
            "{}",
            "{\"itemId\":\"i\",\"targetId\":\"t\"}",
            "{\"pawnId\":\"p\",\"targetId\":\"t\"}",
            "{\"pawnId\":\"p\",\"itemId\":\"i\"}",
            "{\"pawnId\":\"p\",\"itemId\":\"i\",\"targetId\":\"p\"}",
            "{\"pawnId\":\"\",\"itemId\":\"i\",\"targetId\":\"t\"}",
        })
        {
            var failure = operations.GetMethod("Validate", Flags)!.Invoke(null, new[] { Wire("Operations.UseItemIntent", json), context });
            Check(failure != null && Get(failure, "Code")!.ToString() == "InvalidRequest", "malformed use_item refused: " + json);
        }
        Console.WriteLine($"native-use-item: {checks} checks passed");
        return 0;
    }
}
