#nullable enable
using System;
using System.Linq;
using RimWorld;
using Verse;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    internal static class NativeQuestWorkload
    {
        internal static Obs.QuestWorkload? Read(Obs.QuestObjective objective, Quest quest)
        {
            var remaining = Math.Max(0, objective.Count - objective.Produced);
            if (objective.Kind == Obs.QuestObjectiveKind.HarvestPlant)
            {
                var plant = DefDatabase<ThingDef>.GetNamedSilentFail(objective.Def)?.plant;
                return plant == null ? null : new Obs.QuestWorkload { Work = remaining * plant.harvestWork, Stat = StatDefOf.PlantWorkSpeed.defName, RateFactor = 1 };
            }
            if (objective.Kind != Obs.QuestObjectiveKind.ProduceItem) return null;
            var map = quest.QuestLookTargets.Select(t => t.Map).FirstOrDefault(m => m != null);
            if (map == null) return null;
            var stuff = DefDatabase<ThingDef>.GetNamedSilentFail(objective.Stuff);
            // Only an existing usable native bill giver supplies a forecast route.
            foreach (var bench in map.listerThings.AllThings.OfType<Building_WorkTable>().OrderBy(b => b.GetUniqueLoadID(), StringComparer.Ordinal))
            {
                if (!bench.CurrentlyUsableForBills()) continue;
                foreach (var recipe in bench.def.AllRecipes.OrderBy(r => r.defName, StringComparer.Ordinal))
                {
                    var units = recipe.products.Where(p => p.thingDef.defName == objective.Def).Sum(p => p.count);
                    if (units <= 0 || !recipe.AvailableNow) continue;
                    var iterations = Math.Ceiling((double)remaining / units);
                    return new Obs.QuestWorkload { Recipe = recipe.defName, Stat = recipe.workSpeedStat?.defName ?? "",
                        Work = iterations * recipe.WorkAmountForStuff(stuff),
                        RateFactor = recipe.workTableSpeedStat == null ? 1 : bench.GetStatValue(recipe.workTableSpeedStat) };
                }
            }
            return null;
        }
    }
}
