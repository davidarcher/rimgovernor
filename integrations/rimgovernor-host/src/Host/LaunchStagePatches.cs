using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using HarmonyLib;
using RimGovernor.Host.Sdk;
using Verse;

namespace RimGovernor.Host;

/// <summary>
/// Times RimWorld's load stages for the launch waterfall (#3286). <c>PlayDataLoader</c> and
/// <c>LoadedModManager</c> wrap each stage in <c>DeepProfiler.Start(label)</c>/<c>End()</c>, which do nothing unless
/// <c>Prefs.LogVerbose</c>; a prefix still sees every call. Installed from the mod constructor, so the stages already
/// open then (<c>LoadAllPlayData</c>, "Load all active mods.", <c>CreateModClasses()</c>) are not seen and their
/// <c>End</c> calls find this thread's stack empty. Only stages at depth 0 and 1 of that stack on a thread that is not
/// a pool thread are recorded: <c>Parallel.ForEach</c> workers and deeper nesting would only add noise. The time
/// from process start to the mod constructor is recorded as <c>launch.pre_mods</c>.
/// </summary>
internal static class LaunchStagePatches
{
    private const string HarmonyId = "pardeike.RimGovernor.Host.launch";
    private const int RecordedDepth = 2;

    private sealed class Open
    {
        public int Id;
        public int Parent;
        public string Name;
        public DateTime StartUtc;
        public bool Recorded;
    }

    [ThreadStatic] private static Stack<Open> stack;

    public static void Apply()
    {
        RecordPreMods();
        var harmony = new Harmony(HarmonyId);
        Patch(harmony, AccessTools.Method(typeof(DeepProfiler), nameof(DeepProfiler.Start), [typeof(string)]), nameof(StartPrefix));
        Patch(harmony, AccessTools.Method(typeof(DeepProfiler), nameof(DeepProfiler.End)), nameof(EndPrefix));
    }

    /// <summary>Removes the prefixes once play data is loaded: the launch is over, and DeepProfiler is also called during play.</summary>
    public static void Remove()
    {
        new Harmony(HarmonyId).UnpatchAll(HarmonyId);
    }

    private static void RecordPreMods()
    {
        try
        {
            var started = Process.GetCurrentProcess().StartTime.ToUniversalTime();
            LaunchPhases.Record(LaunchPhases.NextId(), 0, "launch.pre_mods", started, LaunchPhases.UtcNow);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            Log.Warning($"[RimBridge] The launch waterfall has no launch.pre_mods span: the process start time is unreadable ({ex.Message}).");
        }
    }

    private static void Patch(Harmony harmony, System.Reflection.MethodInfo target, string prefix)
    {
        if (target == null)
        {
            Log.Warning($"[RimBridge] The launch waterfall has no RimWorld stages: DeepProfiler.{prefix.Replace("Prefix", "")} was not found.");
            return;
        }
        harmony.Patch(target, prefix: new HarmonyMethod(typeof(LaunchStagePatches), prefix));
    }

    public static void StartPrefix(string label)
    {
        var open = stack ??= new Stack<Open>();
        var recorded = open.Count < RecordedDepth && !Thread.CurrentThread.IsThreadPoolThread;
        var parent = open.Count == 0 ? 0 : open.Peek().Id;
        open.Push(new Open
        {
            Id = recorded ? LaunchPhases.NextId() : 0,
            Parent = parent,
            Name = "rimworld/" + (string.IsNullOrWhiteSpace(label) ? "unlabelled" : label.Trim()),
            StartUtc = recorded ? LaunchPhases.UtcNow : default,
            Recorded = recorded,
        });
    }

    public static void EndPrefix()
    {
        var open = stack;
        if (open == null || open.Count == 0)
            return; // a stage that started before the patch
        var stage = open.Pop();
        if (stage.Recorded)
            LaunchPhases.Record(stage.Id, stage.Parent, stage.Name, stage.StartUtc, LaunchPhases.UtcNow);
    }
}
