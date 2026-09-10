using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace RimGovernor.ConstructionLedgerFixture
{
    // Optional disposable-test observer. Every native method runs unchanged.
    public sealed class ConstructionLedger
    {
        private static readonly List<object> Events = new List<object>();
        private static bool installed;
        private static bool readable = true;

        [Tool("test/construction_ledger", Description = "Read-only disposable-test observation of actual native construction consumption and failure. Installs observational hooks on first read; never changes pawn work, resources or game rules.")]
        public async Task<object> Read(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync(() =>
            {
                if (!installed)
                {
                    var patch = new Harmony("rimgovernor.test.construction-ledger");
                    foreach (var method in new[] { "CompleteConstruction", "FailConstruction" })
                        patch.Patch(AccessTools.Method(typeof(Frame), method),
                            prefix: new HarmonyMethod(typeof(ConstructionLedger), nameof(Before)),
                            postfix: new HarmonyMethod(typeof(ConstructionLedger), nameof(After)));
                    installed = true;
                }
                return (object)new { success = true, readable, events = Events.ToArray() };
            }, cancellationToken);
        }

        private sealed class Snapshot
        {
            internal Map Map;
            internal string Def, Id, Outcome;
            internal int X, Z, Tick;
            internal Dictionary<string, int> Before;
        }

        private static Dictionary<string, int> Inventory(Map map)
        {
            var things = new HashSet<Thing>(map.listerThings.AllThings);
            var holders = new HashSet<IThingHolder>();
            void Visit(IThingHolder holder)
            {
                if (holder == null || !holders.Add(holder)) return;
                var direct = holder.GetDirectlyHeldThings();
                if (direct != null) foreach (var thing in direct) things.Add(thing);
                var children = new List<IThingHolder>();
                holder.GetChildHolders(children);
                foreach (var child in children) Visit(child);
            }
            foreach (var thing in things.ToArray()) if (thing is IThingHolder holder) Visit(holder);
            return things.Where(t => !t.Destroyed).GroupBy(t => t.def.defName)
                .ToDictionary(g => g.Key, g => g.Sum(t => t.stackCount));
        }

        private static void Before(Frame __instance, MethodBase __originalMethod, out Snapshot __state)
        {
            __state = null;
            try
            {
                __state = new Snapshot { Map = __instance.Map, Def = __instance.def.entityDefToBuild.defName,
                    Id = __instance.GetUniqueLoadID(), X = __instance.Position.x, Z = __instance.Position.z,
                    Tick = Find.TickManager.TicksGame, Outcome = __originalMethod.Name, Before = Inventory(__instance.Map) };
            }
            catch { readable = false; }
        }

        private static void After(Snapshot __state)
        {
            if (__state == null) return;
            try
            {
                var after = Inventory(__state.Map);
                var consumed = __state.Before.Where(p => p.Value > (after.TryGetValue(p.Key, out var count) ? count : 0))
                    .ToDictionary(p => p.Key, p => p.Value - (after.TryGetValue(p.Key, out var count) ? count : 0));
                if (Events.Count >= 512) { readable = false; return; }
                Events.Add(new { outcome = __state.Outcome, frame = __state.Id, defName = __state.Def,
                    x = __state.X, z = __state.Z, tick = __state.Tick, consumed });
            }
            catch { readable = false; }
        }
    }
}
