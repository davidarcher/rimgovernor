#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using RimGovernor.Host.Sdk;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // TickProfile: where a game tick's wall time goes, for the acceptance speed
    // work. Each bucket is the inclusive time of one game method, so the buckets
    // nest (a tick holds its ticker lists, a pawn tick holds its pathing) and do
    // not sum. Installed only under the test-acceleration launch flag, and
    // read-and-reset by the clock epoch timing line.
    internal static class TickProfile
    {
        private sealed class Bucket
        {
            public string Name = "";
            public long Elapsed;
            public long Calls;
        }

        private static readonly Dictionary<MethodBase, Bucket> Methods = new Dictionary<MethodBase, Bucket>();
        private static readonly Dictionary<int, Bucket> Lists = new Dictionary<int, Bucket>();
        private static FieldInfo? _listType;
        private static int _installed;

        internal static void Install()
        {
            if (!Supervisor.TestAccelerationLaunch) return;
            if (Interlocked.CompareExchange(ref _installed, 1, 0) != 0) return;
            var harmony = new Harmony("rimgovernor.tick-profile");
            var pre = new HarmonyMethod(typeof(TickProfile), nameof(Begin));
            var post = new HarmonyMethod(typeof(TickProfile), nameof(End));
            Watch(harmony, pre, post, typeof(TickManager), "DoSingleTick", "tick");
            Watch(harmony, pre, post, typeof(TickManager), "TickManagerUpdate", "frame");
            Watch(harmony, pre, post, typeof(Map), "MapPreTick", "map-pre");
            Watch(harmony, pre, post, typeof(Map), "MapPostTick", "map-post");
            Watch(harmony, pre, post, typeof(World), "WorldTick", "world");
            Watch(harmony, pre, post, typeof(Pawn), "Tick", "pawn-tick");
            Watch(harmony, pre, post, typeof(Pawn_JobTracker), "JobTrackerTick", "job-tracker");
            Watch(harmony, pre, post, typeof(Pawn_JobTracker), "TryFindAndStartJob", "find-job");
            Watch(harmony, pre, post, typeof(Pawn_PathFollower), "PatherTick", "pather");
            Watch(harmony, pre, post, typeof(PathFinder), "FindPath", "findpath");
            Watch(harmony, pre, post, typeof(RegionAndRoomUpdater), "TryRebuildDirtyRegionsAndRooms", "regions");
            Watch(harmony, pre, post, typeof(GenConstruct), "CanConstruct", "can-construct", new[] { typeof(Thing), typeof(Pawn), typeof(bool), typeof(bool), typeof(JobDef) });
            Watch(harmony, pre, post, typeof(Frame), "Tick", "frame-thing");
            Watch(harmony, pre, post, typeof(Frame), "CompleteConstruction", "complete-construction");
            Watch(harmony, pre, post, typeof(Blueprint), "Tick", "blueprint-tick");
            Watch(harmony, pre, post, typeof(GlowGrid), "GlowGridUpdate_First", "glow");
            Watch(harmony, pre, post, typeof(GlowGrid), "RecalculateAllGlow", "glow-recalc");
            Watch(harmony, pre, post, typeof(AutoBuildRoofAreaSetter), "TryGenerateAreaNow", "roof-area");
            Watch(harmony, pre, post, typeof(RoofGrid), "SetRoof", "set-roof");
            Watch(harmony, pre, post, typeof(Room), "Notify_RoofChanged", "room-roof");
            Watch(harmony, pre, post, typeof(Thing), "DeSpawn", "despawn");
            Watch(harmony, pre, post, typeof(GenSpawn), "Spawn", "spawn", new[] { typeof(Thing), typeof(IntVec3), typeof(Map), typeof(Rot4), typeof(WipeMode), typeof(bool), typeof(bool) });

            _listType = AccessTools.Field(typeof(TickList), "tickType");
            var listTick = AccessTools.Method(typeof(TickList), "Tick");
            if (listTick != null && _listType != null)
                harmony.Patch(listTick, prefix: new HarmonyMethod(typeof(TickProfile), nameof(BeginList)), postfix: new HarmonyMethod(typeof(TickProfile), nameof(EndList)));

            var names = new List<string>();
            foreach (var b in Methods.Values) names.Add(b.Name);
            ModLog.Info("clock", "RimGovernor tick profile watching: " + string.Join(",", names) + (listTick != null ? ",ticker-lists" : ""));
        }

        private static void Watch(Harmony harmony, HarmonyMethod pre, HarmonyMethod post, Type type, string method, string name, Type[]? args = null)
        {
            var target = args == null ? AccessTools.Method(type, method) : AccessTools.Method(type, method, args);
            if (target == null || Methods.ContainsKey(target)) return;
            try
            {
                harmony.Patch(target, prefix: pre, postfix: post);
                Methods[target] = new Bucket { Name = name };
            }
            catch (Exception ex)
            {
                ModLog.Info("clock", "RimGovernor tick profile skipped " + name + ": " + ex.GetType().Name);
            }
        }

        private static readonly Dictionary<string, long> Callers = new Dictionary<string, long>();
        private static long _findJobCalls;

        private static void Begin(MethodBase __originalMethod, out long __state)
        {
            __state = System.Diagnostics.Stopwatch.GetTimestamp();
            if (__originalMethod.Name != "TryFindAndStartJob" || Interlocked.Increment(ref _findJobCalls) % 25 != 0) return;
            var trace = new System.Diagnostics.StackTrace(2, false);
            var sb = new StringBuilder();
            for (var i = 0; i < trace.FrameCount && i < 4; i++)
            {
                var m = trace.GetFrame(i)?.GetMethod();
                if (m != null) sb.Append(m.DeclaringType?.Name).Append('.').Append(m.Name).Append('<');
            }
            var key = sb.ToString();
            lock (Callers) { long n; Callers.TryGetValue(key, out n); Callers[key] = n + 1; }
        }

        private static void End(MethodBase __originalMethod, long __state)
        {
            Bucket? b;
            if (!Methods.TryGetValue(__originalMethod, out b)) return;
            Interlocked.Add(ref b.Elapsed, System.Diagnostics.Stopwatch.GetTimestamp() - __state);
            Interlocked.Increment(ref b.Calls);
        }

        private static void BeginList(out long __state) => __state = System.Diagnostics.Stopwatch.GetTimestamp();

        private static void EndList(TickList __instance, long __state)
        {
            var type = Convert.ToInt32(_listType!.GetValue(__instance), CultureInfo.InvariantCulture);
            Bucket? b;
            lock (Lists)
            {
                if (!Lists.TryGetValue(type, out b)) Lists[type] = b = new Bucket { Name = "list-" + ((TickerType)type).ToString().ToLowerInvariant() };
            }
            Interlocked.Add(ref b.Elapsed, System.Diagnostics.Stopwatch.GetTimestamp() - __state);
            Interlocked.Increment(ref b.Calls);
        }

        // Take is the buckets' milliseconds and calls since the last take, zeroed.
        internal static string Take()
        {
            if (_installed == 0) return "";
            var sb = new StringBuilder();
            foreach (var b in Methods.Values) Append(sb, b);
            lock (Lists) foreach (var b in Lists.Values) Append(sb, b);
            lock (Callers) { foreach (var c in Callers) sb.Append(" caller[").Append(c.Value).Append("]=").Append(c.Key); Callers.Clear(); }
            return sb.ToString();
        }

        private static void Append(StringBuilder sb, Bucket b)
        {
            var elapsed = Interlocked.Exchange(ref b.Elapsed, 0);
            var calls = Interlocked.Exchange(ref b.Calls, 0);
            if (calls == 0) return;
            sb.Append(' ').Append(b.Name).Append('=')
              .Append((elapsed * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F0", CultureInfo.InvariantCulture))
              .Append("ms/").Append(calls.ToString(CultureInfo.InvariantCulture));
        }
    }
}
