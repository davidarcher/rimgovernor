#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;

internal static class NativeQuestShuttleProbe
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static Assembly bridge = null!;
    private static int checks;
    private static void Check(bool value, string why) { if (!value) throw new Exception(why); checks++; }
    private static object Wire(string name, string json)
    {
        var parser = bridge.GetType("RimGovernor.Protocol." + name, true)!.GetProperty("Parser")!.GetValue(null)!;
        return parser.GetType().GetMethod("ParseJson")!.Invoke(parser, new object[] { json })!;
    }
    internal static int Invoke(string[] args)
    {
        var dirs = args.Skip(1).Concat(new[] { Path.GetDirectoryName(Path.GetFullPath(args[0]))! }).ToArray();
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => { var path = dirs.Select(d => Path.Combine(d, new AssemblyName(e.Name).Name + ".dll")).FirstOrDefault(File.Exists); return path == null ? null : Assembly.LoadFrom(path); };
        bridge = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        var native = bridge.GetType("HomeBridge.BridgeTools.NativeQuestShuttleOperations", true)!;
        var shape = native.GetMethod("ValidShape", Flags)!;
        foreach (var json in new[] { "{\"questId\":\"Quest_1\",\"autoload\":true}", "{\"questId\":\"Quest_1\",\"autoload\":false}", "{\"questId\":\"Quest_1\",\"explicitPawns\":{\"pawnIds\":[\"Pawn_1\",\"Pawn_2\"]}}", "{\"questId\":\"Quest_1\",\"launch\":true}" })
            Check((bool)shape.Invoke(null, new[] { Wire("Operations.QuestShuttleIntent", json) })!, "valid shuttle mode " + json);
        foreach (var json in new[] { "{}", "{\"questId\":\"Quest_1\"}", "{\"autoload\":true}", "{\"questId\":\"Quest_1\",\"explicitPawns\":{}}", "{\"questId\":\"Quest_1\",\"explicitPawns\":{\"pawnIds\":[\"Pawn_1\",\"Pawn_1\"]}}" })
            Check(!(bool)shape.Invoke(null, new[] { Wire("Operations.QuestShuttleIntent", json) })!, "invalid shuttle shape " + json);
        var conditions = native.GetMethod("LaunchConditionsSatisfied", Flags)!;
        Check((bool)conditions.Invoke(null, new object[] { true, true, false, false })!, "loaded launch allowed");
        foreach (var facts in new[] { new object[] { false, true, false, false }, new object[] { true, false, false, false }, new object[] { true, true, true, false }, new object[] { true, true, false, true } })
            Check(!(bool)conditions.Invoke(null, facts)!, "unavailable launch refused");
        var dispatch = bridge.GetType("HomeBridge.BridgeTools.NativeActionDispatch", true)!;
        dispatch.GetMethod("AssertComplete", Flags)!.Invoke(null, null);
        var handlers = (System.Collections.IDictionary)dispatch.GetField("Handlers", Flags)!.GetValue(null)!;
        Check(handlers.Keys.Cast<object>().Any(k => k.ToString() == "QuestShuttle"), "quest shuttle handler registered");
        Console.WriteLine($"native-quest-shuttle: {checks} contract checks passed; loading and departure require gameplay validation.");
        return 0;
    }
}
