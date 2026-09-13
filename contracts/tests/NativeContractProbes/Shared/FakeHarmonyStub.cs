using System.Collections.Generic;
using System.Reflection;

// Fake HarmonyLib surface used only by native-clock's Supervisor double to report whether the
// (fake) hook patches are installed. This file is EXCLUDED from the build whenever a real
// $(HarmonyAssembly) is referenced (see NativeContractProbes.csproj) to avoid a CS0104/CS0433
// ambiguous-reference conflict between this fake HarmonyLib.Harmony and the real 0Harmony.dll's
// HarmonyLib.Harmony needed by native-authority-hooks / native-operation-envelope /
// native-explosive-causality / native-ranged-causality.
namespace HarmonyLib
{
    internal static class AccessTools
    {
        public static MethodInfo Method(System.Type type, string name) =>
            type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
    }
    internal sealed class Patch { public string owner = "homebridge.supervised-play"; public MethodInfo PatchMethod; }
    internal sealed class Patches { public List<Patch> Postfixes = new List<Patch>(); }
    internal static class Harmony
    {
        public static bool Healthy = true;
        public static bool Installed;
        public static Patches GetPatchInfo(MethodInfo method) => new Patches
        {
            Postfixes = Healthy && Installed
                ? new List<Patch> { new Patch { PatchMethod = AccessTools.Method(typeof(HomeBridge.BridgeTools.Supervisor), method.Name == "DoSingleTick" ? "OnTick" : "OnUpdate") } }
                : new List<Patch>()
        };
    }
}
