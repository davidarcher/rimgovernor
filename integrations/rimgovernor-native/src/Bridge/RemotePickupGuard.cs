#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Remote construction pickup on vanilla's own delivery (#2517, #3046): a controlled colonist with no committed order runs
    // vanilla job selection (#3669), so its construction delivery is sized here by the rule the brain's Supply step uses
    // (RemotePickup): a remote job takes a full carry load and queues the stacks worth the detour; the site takes its need and
    // vanilla drops the surplus beside it. A committed order, a player-forced job, a mechanoid and a pawn outside supervised
    // play keep vanilla's exact-need pickup. Bill ingredients never pass here: their placed things are consumed whole.
    internal static class RemotePickupGuard
    {
        // The supervisor is armed only while a clock run ticks (the WallLayerGuard pattern, #3596).
        internal static Func<bool> Active = () => Supervisor.IsActive;
        private static bool patched;
        private static readonly object InstallLock = new object();
        internal static void Install()
        {
            lock (InstallLock)
            {
                if (patched) return;
                var harmony = new Harmony("rimgovernor.remote-pickup");
                harmony.Patch(AccessTools.Method(typeof(WorkGiver_ConstructDeliverResources), "ResourceDeliverJobFor"),
                    postfix: new HarmonyMethod(typeof(RemotePickupGuard), nameof(After)));
                patched = true;
            }
        }

        private static void After(Pawn pawn, bool forced, Job? __result)
        {
            if (__result == null || forced || __result.def != JobDefOf.HaulToContainer || !Active() || !NativePawnActivities.Standing(pawn)) return;
            // A carried resource (vanilla's first choice) is not a pickup.
            if (!(__result.targetA.Thing is Thing first) || !first.Spawned || !(__result.targetC.Thing is Thing site) || !site.Spawned) return;
            var distance = (double)(first.Position - site.Position).LengthHorizontal;
            if (NativeConstructionActivity.StackStorageOf(pawn.Map, first) is not RimGovernor.Contracts.Wire.Observations.StackStorage.Accepted { Distance: var storage } || !RemotePickup.IsRemote(distance, storage)) return;
            var capacity = pawn.carryTracker.MaxStackSpaceEver(first.def);
            var queue = __result.targetQueueA ?? new List<LocalTargetInfo>();
            var queued = new HashSet<Thing>(queue.Select(q => q.Thing));
            var held = first.stackCount + queue.Sum(q => q.Thing?.stackCount ?? 0);
            var collect = RemotePickup.Collect(distance, capacity, held, pawn.Map.listerThings.ThingsOfDef(first.def)
                .Where(t => t != first && t.Spawned && !queued.Contains(t))
                .OrderBy(t => t.thingIDNumber)
                .Select(t => (t, (double)(t.Position - first.Position).LengthHorizontal, (double)t.stackCount)),
                t => GenAI.CanUseItemForWork(pawn, t) && pawn.CanReach(t, PathEndMode.ClosestTouch, pawn.NormalMaxDanger()));
            queue.AddRange(collect.Select(t => new LocalTargetInfo(t)));
            __result.targetQueueA = queue;
            __result.count = RemotePickup.Load(__result.count, capacity, held + collect.Sum(t => t.stackCount));
        }
    }
}
