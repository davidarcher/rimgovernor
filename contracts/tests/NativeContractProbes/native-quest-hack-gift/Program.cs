#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;

internal static class NativeQuestHackGiftProbe
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static Assembly bridge = null!;
    private static int checks;
    private static void Check(bool result, string why) { if (!result) throw new Exception(why); checks++; }
    private static object Wire(string name, string json)
    {
        var parser = bridge.GetType("RimGovernor.Protocol.Operations." + name, true)!.GetProperty("Parser")!.GetValue(null)!;
        return parser.GetType().GetMethod("ParseJson")!.Invoke(parser, new object[] { json })!;
    }
    internal static int Invoke(string[] args)
    {
        var dirs = args.Skip(1).Concat(new[] { Path.GetDirectoryName(Path.GetFullPath(args[0]))! }).ToArray();
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => { var path = dirs.Select(d => Path.Combine(d, new AssemblyName(e.Name).Name + ".dll")).FirstOrDefault(File.Exists); return path == null ? null : Assembly.LoadFrom(path); };
        bridge = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        var hack = bridge.GetType("HomeBridge.BridgeTools.NativeHackDesignationOperations", true)!.GetMethod("ValidShape", Flags)!;
        foreach (var json in new[] { "{\"target\":{\"id\":\"Thing_1\"},\"enabled\":true}", "{\"target\":{\"id\":\"Thing_1\"},\"enabled\":false}" })
            Check((bool)hack.Invoke(null, new[] { Wire("HackDesignationIntent", json) })!, "explicit native toggle state accepted");
        foreach (var json in new[] { "{}", "{\"target\":{\"id\":\"Thing_1\"}}", "{\"enabled\":true}", "{\"target\":{},\"enabled\":false}" })
            Check(!(bool)hack.Invoke(null, new[] { Wire("HackDesignationIntent", json) })!, "missing exact hack field refused");
        var give = bridge.GetType("HomeBridge.BridgeTools.NativeGiveItemOperations", true)!.GetMethod("ValidShape", Flags)!;
        string Gift(string def, long count, string hauler = "Pawn_1", string recipient = "Pawn_2") => "{\"hauler\":{\"id\":\"" + hauler + "\"},\"recipient\":{\"id\":\"" + recipient + "\"},\"definition\":\"" + def + "\",\"expectedRemaining\":" + count + "}";
        foreach (var def in new[] { "Silver", "MedicineHerbal", "MedicineIndustrial", "Penoxycyline", "Beer" })
            Check((bool)give.Invoke(null, new[] { Wire("GiveItemIntent", Gift(def, 7)) })!, "closed native gift definition accepted");
        foreach (var json in new[] { "{}", "{\"hauler\":{\"id\":\"Pawn_1\"}}", Gift("Silver", 0), Gift("Silver", -1), Gift("Silver", (long)int.MaxValue + 1), Gift("WoodLog", 1), Gift("Silver", 1, "Pawn_1", "Pawn_1"), Gift("Silver", 1, "", "Pawn_2") })
            Check(!(bool)give.Invoke(null, new[] { Wire("GiveItemIntent", json) })!, "invalid native whole request refused");
        var dispatch = bridge.GetType("HomeBridge.BridgeTools.NativeActionDispatch", true)!;
        dispatch.GetMethod("AssertComplete", Flags)!.Invoke(null, null);
        var handlers = (System.Collections.IDictionary)dispatch.GetField("Handlers", Flags)!.GetValue(null)!;
        foreach (var arm in new[] { "HackDesignation", "GiveItem" }) Check(handlers.Keys.Cast<object>().Any(k => k.ToString() == arm), "native handler registered");
        Console.WriteLine($"native-quest-hack-gift: {checks} checks passed; actual pawn work requires native cases.");
        return 0;
    }
}
