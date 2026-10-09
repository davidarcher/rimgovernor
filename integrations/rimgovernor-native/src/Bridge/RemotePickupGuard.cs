#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Straight-line batching rules for construction delivery (#2517). Pure math,
    // no Verse types: a stack at distance d from the first stack is worth a
    // detour when d < n * D / capacity, where D is the source-to-site distance
    // and n is how many of its items fit in the remaining carry capacity.
    internal static class HaulBatching
    {
        internal static bool IsRemote(float siteDistance, float storageDistance) => siteDistance > storageDistance;
        internal static float RadiusCap(float siteDistance) => siteDistance / 2f;
        internal static bool WorthDetour(float detour, int fits, float siteDistance, int capacity) =>
            fits > 0 && capacity > 0 && detour <= RadiusCap(siteDistance) && detour < fits * siteDistance / capacity;
    }

    // Under supervised play a construction delivery whose source is remote (the
    // first stack is farther from the site than from the nearest accepting
    // storage) takes a full carry load, plus nearby same-def stacks inside the
    // dynamic radius. Vanilla delivers only what the site and its neighbours
    // need and leaves the rest. Surplus is dropped by vanilla beside the site.
    // Bill ingredients are never touched: their placed things are consumed whole.
    internal static class RemotePickupGuard
    {
        private static bool patched;
        internal static void Install()
        {
            if (patched) return;
            var harmony = new Harmony("rimgovernor.remote-pickup");
            harmony.Patch(AccessTools.Method(typeof(WorkGiver_ConstructDeliverResources), "ResourceDeliverJobFor"),
                postfix: new HarmonyMethod(typeof(RemotePickupGuard), nameof(After)));
            patched = true;
        }

        private static void After(Pawn pawn, bool forced, Job? __result)
        {
            if (__result == null || forced || !Supervisor.IsActive || __result.def != JobDefOf.HaulToContainer) return;
            if (!(__result.targetA.Thing is Thing first) || !first.Spawned || !(__result.targetC.Thing is Thing site) || !site.Spawned) return;
            var distance = (first.Position - site.Position).LengthHorizontal;
            if (distance <= 0f || !HaulBatching.IsRemote(distance, StorageDistance(pawn.Map, first))) return;
            var capacity = pawn.carryTracker.MaxStackSpaceEver(first.def);
            var queue = __result.targetQueueA ?? new List<LocalTargetInfo>();
            var total = first.stackCount + queue.Sum(q => q.Thing?.stackCount ?? 0);
            var remaining = capacity - total;
            if (remaining > 0)
            {
                var queued = new HashSet<Thing>(queue.Select(q => q.Thing));
                foreach (var candidate in pawn.Map.listerThings.ThingsOfDef(first.def)
                    .Where(t => t != first && t.Spawned && !queued.Contains(t))
                    .Select(t => new { Thing = t, Detour = (t.Position - first.Position).LengthHorizontal })
                    .Where(c => c.Detour <= HaulBatching.RadiusCap(distance))
                    .OrderBy(c => c.Detour)
                    .ToList())
                {
                    var fits = Math.Min(candidate.Thing.stackCount, remaining);
                    if (!HaulBatching.WorthDetour(candidate.Detour, fits, distance, capacity)
                        || !GenAI.CanUseItemForWork(pawn, candidate.Thing)
                        || !pawn.CanReach(candidate.Thing, PathEndMode.ClosestTouch, pawn.NormalMaxDanger())) continue;
                    queue.Add(candidate.Thing);
                    total += fits;
                    remaining -= fits;
                    if (remaining <= 0) break;
                }
            }
            __result.targetQueueA = queue;
            __result.count = Math.Max(__result.count, Math.Min(total, capacity));
        }

        // Distance from the stack to the nearest cell of any storage that
        // accepts it; zero when it already sits in storage, infinity without any.
        private static float StorageDistance(Map map, Thing thing)
        {
            if (thing.GetSlotGroup() != null) return 0f;
            var best = float.PositiveInfinity;
            foreach (var group in map.haulDestinationManager.AllGroupsListInPriorityOrder)
            {
                if (!group.Settings.AllowedToAccept(thing)) continue;
                foreach (var cell in group.CellsList) best = Math.Min(best, (cell - thing.Position).LengthHorizontal);
            }
            return best;
        }
    }
}
