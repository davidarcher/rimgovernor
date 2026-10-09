#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;

// NativePopulationObservation's capture odds against the game's own
// HealthTuning curves. args[0] is the built bridge DLL; further args are
// dependency directories (game Managed, SDK, Harmony).
internal static class NativePopulationOutlookProbe
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;

    private static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }

    internal static int Invoke(string[] args)
    {
        var directories = args.Skip(1).Concat(new[] { Path.GetDirectoryName(Path.GetFullPath(args[0]))! }).ToArray();
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
            var path = directories.Select(d => Path.Combine(d, new AssemblyName(e.Name).Name + ".dll")).FirstOrDefault(File.Exists);
            return path == null ? null : Assembly.LoadFrom(path);
        };
        var bridge = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        var tools = bridge.GetType("HomeBridge.BridgeTools.NativePopulationObservation", true)!;
        var tuning = Assembly.Load("Assembly-CSharp").GetType("Verse.HealthTuning", true)!;
        Func<string, float, float> curve = (name, x) => {
            var c = tuning.GetField(name, Flags)!.GetValue(null)!;
            return (float)c.GetType().GetMethod("Evaluate", new[] { typeof(float) })!.Invoke(c, new object[] { x })!;
        };
        Func<float, bool, float, float> death = (x, unwavering, factor) => (float)tools.GetMethod("DeathOnDownedChance", Flags)!.Invoke(null, new object[] { x, unwavering, factor })!;
        Func<float, float> unrecruitable = x => (float)tools.GetMethod("UnrecruitableChance", Flags)!.Invoke(null, new object[] { x })!;

        // Decompiled HealthTuning points (RimWorld 1.6): unwavering, wavering, unrecruitable.
        var intents = new[] { -1f, 0f, 1f, 2f, 8f };
        var unwavering = new[] { 0.8667f, 0.8f, 0.5657f, 0.5f, 0.2632f };
        var wavering = new[] { 0.92f, 0.85f, 0.62f, 0.55f, 0.3f };
        var nonRecruitable = new[] { 0.4f, 0.25f, 0.13f, 0.1f, 0.05f };
        for (var i = 0; i < intents.Length; i++)
        {
            var x = intents[i];
            Check(curve("DeathOnDownedChance_NonColonyHumanlikeFromPopulationIntentCurve", x) == unwavering[i], $"game unwavering curve at {x}");
            Check(curve("DeathOnDownedChance_NonColonyHumanlikeFromPopulationIntentCurve_WaveringPrisoners", x) == wavering[i], $"game wavering curve at {x}");
            Check(curve("NonRecruitableChanceOverPopulationIntentCurve", x) == nonRecruitable[i], $"game unrecruitable curve at {x}");
            Check(death(x, true, 1f) == unwavering[i], $"unwavering death chance at {x}");
            Check(death(x, false, 1f) == wavering[i], $"wavering death chance at {x}");
            Check(death(x, false, 0.5f) == wavering[i] * 0.5f, $"enemy factor scales death chance at {x}");
            Check(unrecruitable(x) == nonRecruitable[i], $"unrecruitable chance at {x}");
        }
        Console.WriteLine($"native-population-outlook: {checks} checks passed");
        return 0;
    }
}
