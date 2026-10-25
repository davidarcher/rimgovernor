using System;
using System.Linq;
using RimWorld;
using Verse;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    internal static class NativeQuestWorkload
    {
        internal static Obs.QuestWorkload? Read(Obs.QuestObjective objective, Quest quest)
        {
            if (!(objective.Goal is Obs.QuestObjectiveGoal.Things things)) return null;
            var remaining = Math.Max(0, things.Count - things.Done);
            if (objective.Kind == Obs.QuestObjectiveKind.HarvestPlant)
            {
                var plant = DefDatabase<ThingDef>.GetNamedSilentFail(things.Def)?.plant;
                return plant == null ? null : new Obs.QuestWorkload { Recipe = null, Work = remaining * plant.harvestWork, Stat = StatDefOf.PlantWorkSpeed.defName, RateFactor = 1 };
            }
            if (objective.Kind != Obs.QuestObjectiveKind.ProduceItem) return null;
            var map = quest.QuestLookTargets.Select(t => t.Map).FirstOrDefault(m => m != null);
            if (map == null) return null;
            var stuff = things.Stuff == null ? null : DefDatabase<ThingDef>.GetNamedSilentFail(things.Stuff);
            // Only an existing usable native bill giver supplies a forecast route.
            foreach (var bench in map.listerThings.AllThings.OfType<Building_WorkTable>().OrderBy(b => b.GetUniqueLoadID(), StringComparer.Ordinal))
            {
                if (!bench.CurrentlyUsableForBills()) continue;
                foreach (var recipe in bench.def.AllRecipes.OrderBy(r => r.defName, StringComparer.Ordinal))
                {
                    var units = recipe.products.Where(p => p.thingDef.defName == things.Def).Sum(p => p.count);
                    if (units <= 0 || !recipe.AvailableNow) continue;
                    var iterations = Math.Ceiling((double)remaining / units);
                    return new Obs.QuestWorkload { Recipe = recipe.defName, Stat = recipe.workSpeedStat?.defName,
                        Work = iterations * recipe.WorkAmountForStuff(stuff),
                        RateFactor = recipe.workTableSpeedStat == null ? 1 : bench.GetStatValue(recipe.workTableSpeedStat) };
                }
            }
            return null;
        }
    }
}
