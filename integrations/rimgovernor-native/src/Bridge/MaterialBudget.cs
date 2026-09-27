#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Free stock per definition: unforbidden haulables less what construction
    // still needs and what other pawns' live bill jobs have promised.
    internal static class MaterialBudget
    {
        internal static Dictionary<string, int> Budgets(Map map, Pawn? worker = null)
        {
            var stock = map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver)
                .Where(t => t.Spawned && !t.IsForbidden(Faction.OfPlayer))
                .GroupBy(t => t.def.defName).ToDictionary(g => g.Key, g => g.Sum(t => t.stackCount));
            Action<string, int> subtract = (name, count) => { stock.TryGetValue(name, out var current); stock[name] = current - count; };
            foreach (var thing in map.listerThings.AllThings)
            {
                if (!(thing is IConstructible construction) || thing is Blueprint_Install) continue;
                var costs = construction.TotalMaterialCost();
                if (costs != null) foreach (var cost in costs)
                    subtract(cost.thingDef.defName, BridgeCommon.ConstructibleStillNeeded(construction, cost.thingDef, cost.count));
            }
            foreach (var pawn in map.mapPawns.AllPawnsSpawned.Where(p => p != worker))
            {
                var job = pawn.CurJob;
                if (job?.bill == null || job.targetQueueB == null || job.countQueue == null) continue;
                for (var i = 0; i < Math.Min(job.targetQueueB.Count, job.countQueue.Count); i++)
                    if (job.targetQueueB[i].Thing is Thing t && t.Spawned) subtract(t.def.defName, job.countQueue[i]);
                if (job.placedThings != null) foreach (var placed in job.placedThings)
                    if (placed.thing.Spawned) subtract(placed.thing.def.defName, placed.Count);
            }
            return stock;
        }
    }
}
