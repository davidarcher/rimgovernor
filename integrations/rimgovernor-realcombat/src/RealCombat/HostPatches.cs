using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;
using Waylith.Assets;

namespace RimGovernor.RealCombat;

/// <summary>
/// The Harmony patches that let the game's tick run in a bare process. Each skipped method is UI, audio, rendering or
/// a non-combat ticker (storyteller, world, quests...) that needs a window, a camera or a populated colony; combat
/// itself is not patched. The list is skips.txt, one <c>Type::Method</c> per line, <c>uninit</c> before a getter that
/// should answer an uninitialised instance (a UI singleton whose callers only call void methods on it).
/// </summary>
internal static class HostPatches
{
    private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    private static readonly Dictionary<Type, object> UninitInstances = [];

    private static AssetPack? s_pack;

    /// <summary>Verb shots fired per shooter, by thing id.</summary>
    internal static readonly Dictionary<int, int> Shots = [];

    /// <summary>Damage applications per target, by thing id.</summary>
    internal static readonly Dictionary<int, int> Hits = [];

    /// <summary>The game tick of each shooter first shot, by thing id.</summary>
    internal static readonly Dictionary<int, int> FirstShot = [];

    internal static void Install(Harmony harmony, AssetPack pack)
    {
        s_pack = pack;
        var skip = new HarmonyMethod(typeof(HostPatches).GetMethod(nameof(Skip))!);
        var uninit = new HarmonyMethod(typeof(HostPatches).GetMethod(nameof(UninitGetter))!);
        foreach (var raw in ReadSkips())
        {
            var isUninit = raw.StartsWith("uninit ", StringComparison.Ordinal);
            var parts = (isUninit ? raw["uninit ".Length..] : raw).Split("::");
            var type = typeof(Def).Assembly.GetType(parts[0])
                ?? throw new InvalidOperationException($"realcombat: skip target type {parts[0]} is gone from this game build");
            var methods = type.GetMethods(Declared).Where(m => m.Name == parts[1]).ToList();
            if (methods.Count == 0)
            {
                throw new InvalidOperationException($"realcombat: skip target {raw} is gone from this game build");
            }

            foreach (var m in methods)
            {
                harmony.Patch(m, prefix: isUninit ? uninit : skip);
            }
        }

        harmony.Patch(
            typeof(GenFile).GetMethod(nameof(GenFile.TextFromResourceFile))!,
            prefix: new HarmonyMethod(typeof(HostPatches).GetMethod(nameof(ResourceText))!));
        harmony.Patch(
            typeof(Verb_LaunchProjectile).GetMethod("TryCastShot", Declared)!,
            postfix: new HarmonyMethod(typeof(HostPatches).GetMethod(nameof(CountShot))!));
        harmony.Patch(
            typeof(Pawn).GetMethod("PostApplyDamage", Declared)!,
            postfix: new HarmonyMethod(typeof(HostPatches).GetMethod(nameof(CountHit))!));
    }

    private static IEnumerable<string> ReadSkips()
    {
        using var stream = typeof(HostPatches).Assembly.GetManifestResourceStream("skips.txt")!;
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (line.Contains("::", StringComparison.Ordinal) && !line.StartsWith('#'))
            {
                yield return line.Trim();
            }
        }
    }

    /// <summary>Prefix that skips the original (and leaves a default result).</summary>
    public static bool Skip() => false;

    /// <summary>Prefix for a getter: answers an uninitialised instance of its return type.</summary>
    public static bool UninitGetter(MethodBase __originalMethod, ref object __result)
    {
        var type = ((MethodInfo)__originalMethod).ReturnType;
        if (!UninitInstances.TryGetValue(type, out var instance))
        {
            instance = RuntimeHelpers.GetUninitializedObject(type);
            UninitInstances[type] = instance;
        }

        __result = instance;
        return false;
    }

    /// <summary>Reads Resources/Text from the asset pack: the game reads it through Unity's Resources, which the bare process lacks.</summary>
    public static bool ResourceText(string filePath, ref string? __result)
    {
        if (s_pack is null || !s_pack.TryRead("Resources/Text/" + filePath + ".txt", out var data))
        {
            __result = null;
            return false;
        }

        __result = Encoding.UTF8.GetString(data).TrimStart('﻿');
        return false;
    }

    public static void CountShot(Verb __instance, bool __result)
    {
        if (__result && __instance.CasterPawn is { } p)
        {
            Shots[p.thingIDNumber] = Shots.GetValueOrDefault(p.thingIDNumber) + 1;
            FirstShot.TryAdd(p.thingIDNumber, Find.TickManager.TicksGame);
        }
    }

    public static void CountHit(Pawn __instance) => Hits[__instance.thingIDNumber] = Hits.GetValueOrDefault(__instance.thingIDNumber) + 1;
}
