using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Counts native orders outside the bridge's synchronous main-thread scope.
    // The game/load identity scopes this evidence; it is never a saved authority.
    internal static class OrderedWorkHistory
    {
        private static object game;
        private static readonly Dictionary<int, long> generations = new Dictionary<int, long>();
        private static bool installed;
        [ThreadStatic] private static int owned;
        internal static IDisposable Owned()
        {
            owned++;
            return new Scope();
        }
        private sealed class Scope : IDisposable { public void Dispose() { owned--; } }
        internal static long? Read(Pawn pawn)
        {
            if (!installed)
            {
                try
                {
                    var target = AccessTools.Method(typeof(Pawn_JobTracker), "TryTakeOrderedJob");
                    if (target == null) return null;
                    var harmony = new Harmony("rimgovernor.ordered-work-history");
                    var prefix = new HarmonyMethod(typeof(OrderedWorkHistory).GetMethod(nameof(BeforeOrder), BindingFlags.Static | BindingFlags.NonPublic));
                    harmony.Patch(target, prefix: prefix);
                    harmony.Patch(AccessTools.Method(typeof(Pawn_DraftController), "GetGizmos"),
                        postfix: new HarmonyMethod(typeof(OrderedWorkHistory).GetMethod(nameof(AfterGizmos), BindingFlags.Static | BindingFlags.NonPublic)));
                    installed = true;
                }
                catch { return null; }
            }
            if (!ReferenceEquals(game, Current.Game)) { game = Current.Game; generations.Clear(); }
            if (pawn == null) return null;
            return generations.TryGetValue(pawn.thingIDNumber, out var value) ? value : 0;
        }
        private static void BeforeOrder(Pawn ___pawn)
        {
            if (owned > 0) return;
            var before = Read(___pawn);
            if (before.HasValue) generations[___pawn.thingIDNumber] = before.Value + 1;
        }
        private static void AfterGizmos(Pawn ___pawn, ref IEnumerable<Gizmo> __result)
        {
            __result = Wrap(___pawn, __result);
        }
        private static IEnumerable<Gizmo> Wrap(Pawn pawn, IEnumerable<Gizmo> gizmos)
        {
            foreach (var gizmo in gizmos) {
                if (gizmo is Command_Toggle toggle) {
                    var action = toggle.toggleAction;
                    toggle.toggleAction = () => { BeforeOrder(pawn); action(); };
                }
                yield return gizmo;
            }
        }
    }
}
