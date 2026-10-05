using System.Collections.Generic;
using System.Reflection;

// Fake HarmonyLib surface used only by native-clock's Supervisor double and NativeClockRuntime.cs to
// report whether the (fake) hook patches are installed. This project never references a real 0Harmony.dll.
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
                ? new List<Patch> { new Patch { PatchMethod = AccessTools.Method(typeof(HomeBridge.BridgeTools.Supervisor), method.Name == "DoSingleTick" ? "OnTick" : "OnFrame") } }
                : new List<Patch>()
        };
    }
}
