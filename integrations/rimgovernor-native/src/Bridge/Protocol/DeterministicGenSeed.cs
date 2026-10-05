#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Vanilla seeds several map-generation Rand blocks with System.HashCode.Combine,
    // whose result differs per process (the runtime salts it at start). The same spec
    // on a fresh game therefore generated different ruins, shrines and group
    // contents each time (#2034). The seeds are replaced here with a fixed hash so a
    // spec reproduces its map on one install.
    internal static class DeterministicGenSeed
    {
        private static bool installed;

        internal static void EnsurePatched()
        {
            if (installed) return;
            var harmony = new Harmony("rimgovernor.deterministic-gen-seed");
            var transpiler = new HarmonyMethod(typeof(DeterministicGenSeed), nameof(Swap));
            // Map.NextGenSeed (BaseGen, scatter groups, uniques) and LayoutWorker's room fills.
            foreach (var target in new[]
            {
                AccessTools.PropertyGetter(typeof(Map), nameof(Map.NextGenSeed)),
                AccessTools.Method(typeof(LayoutWorker), "FillAllRooms"),
            })
            {
                if (target == null) throw new InvalidOperationException("A map-generation seed site moved in this game version.");
                harmony.Patch(target, transpiler: transpiler);
            }
            installed = true;
        }

        internal static int Combine(int a, int b) => Gen.HashCombineInt(a, b);

        internal static int Combine(int a, int b, int c) => Gen.HashCombineInt(Gen.HashCombineInt(a, b), c);

        private static IEnumerable<CodeInstruction> Swap(IEnumerable<CodeInstruction> codes)
        {
            foreach (var code in codes)
            {
                if (code.opcode == OpCodes.Call && code.operand is MethodInfo method
                    && method.IsGenericMethod && method.DeclaringType?.FullName == "System.HashCode" && method.Name == "Combine")
                {
                    var args = method.GetGenericArguments();
                    if (Array.TrueForAll(args, t => t == typeof(int)) && (args.Length == 2 || args.Length == 3))
                    {
                        code.operand = typeof(DeterministicGenSeed).GetMethod(nameof(Combine), BindingFlags.Static | BindingFlags.NonPublic,
                            null, Array.ConvertAll(args, _ => typeof(int)), null);
                    }
                }
                yield return code;
            }
        }
    }
}
