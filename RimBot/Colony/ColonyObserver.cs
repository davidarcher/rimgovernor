using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace RimBot.Colony
{
    public static class ColonyObserver
    {
        public static ColonyFacts Observe(Map map)
        {
            var f = new ColonyFacts();
            var pawns = map.mapPawns.FreeColonistsSpawned;
            f.Colonists = pawns.Count;
            f.Unarmed=pawns.Count(p=>!p.WorkTagIsDisabled(WorkTags.Violent) && p.equipment?.Primary==null);
            f.Patients = pawns.Count(p => p.health.hediffSet.hediffs.Any(h => h.TendableNow()));
            f.Bleeding = pawns.Count(p => p.health.hediffSet.BleedRateTotal > 0);
            f.Downed = pawns.Count(p => p.Downed);
            f.TemperatureInjuries = pawns.Count(p => p.health.hediffSet.hediffs.Any(h => h.def == HediffDefOf.Heatstroke || h.def == HediffDefOf.Hypothermia));
            f.Hostiles = map.mapPawns.AllPawnsSpawned.Count(p => !p.Downed && !p.Dead && !p.Position.Fogged(map) && p.HostileTo(Faction.OfPlayer));
            f.Doctors = pawns.Count(p => Enabled(p, WorkTypeDefOf.Doctor));
            f.Cooks = pawns.Count(p => Enabled(p, DefDatabase<WorkTypeDef>.GetNamedSilentFail("Cooking")));
            f.Builders = pawns.Count(p => Enabled(p, WorkTypeDefOf.Construction));
            f.ActiveTending = pawns.Count(p => p.CurJobDef == JobDefOf.TendPatient);
            f.ActiveConstruction = pawns.Count(p => p.CurJobDef == JobDefOf.FinishFrame);
            f.ActiveCooking = pawns.Count(p => p.CurJob?.bill != null && MakesFood(p.CurJob.bill));
            f.DailyNutrition = pawns.Where(p => p.needs?.food != null).Sum(p => p.needs.food.FoodFallPerTick * 60000f);
            foreach (var item in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver))
            {
                if (item.def.category != ThingCategory.Item || !item.def.IsNutritionGivingIngestible || item.def.IsDrug ||
                    item.def.ingestible.preferability < FoodPreferability.RawBad || item.IsForbidden(Faction.OfPlayer) ||
                    item.GetRotStage() == RotStage.Rotting || item.GetRotStage() == RotStage.Dessicated) continue;
                if (pawns.Any(p => FoodUtility.WillEat(p, item, null, false, false)))
                    f.Nutrition += item.GetStatValue(StatDefOf.Nutrition) * item.stackCount;
            }
            var buildings = map.listerBuildings.allBuildingsColonist;
            foreach (var bed in buildings.OfType<Building_Bed>())
            {
                if (!bed.def.building.bed_humanlike || bed.Medical || bed.ForPrisoners || bed.ForSlaves || bed.def.building.bed_crib) continue;
                f.RegularBedSlots += bed.SleepingSlotsCount;
                var room = bed.GetRoom();
                if (room != null && !room.PsychologicallyOutdoors && room.OpenRoofCount == 0)
                    f.ShelteredSlots += bed.SleepingSlotsCount;
            }
            foreach (var table in buildings.OfType<Building_WorkTable>())
                f.CookingOrders += table.BillStack.Bills.Count(b => MakesFood(b) && b.ShouldDoNow());
            var blueprints = map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint);
            var frames = map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame);
            f.Blueprints = blueprints.Count;
            f.Frames = frames.Count;
            foreach (var order in blueprints.Concat(frames))
            {
                var def = order.def.entityDefToBuild as ThingDef;
                if (def?.thingClass!=null && typeof(Building_Bed).IsAssignableFrom(def.thingClass) && def.building.bed_humanlike && !def.building.bed_crib)
                {
                    f.PendingBedSlots += BedUtility.GetSleepingSlotsCount(def.size);
                    if (order is Frame) f.BedFrames++;
                }
                var frame = order as Frame;
                if (frame != null) f.ConstructionWork += frame.workDone;
            }
            return f;
        }
        private static bool Enabled(Pawn pawn, WorkTypeDef work) => work != null && !pawn.Downed && !pawn.Drafted &&
            pawn.workSettings != null && !pawn.WorkTypeIsDisabled(work) && pawn.workSettings.GetPriority(work) > 0;
        private static bool MakesFood(Bill bill) => bill.recipe?.products != null &&
            bill.recipe.products.Any(p => p.thingDef.IsNutritionGivingIngestible && !p.thingDef.IsDrug);

        public static JArray Materials(Thing order)
        {
            if(!(order is IConstructible construction)) return new JArray();
            var supplies=order.Map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver)
                .Where(t=>t.def.category==ThingCategory.Item && !t.Position.Fogged(order.Map)).ToList();
            return new JArray(construction.TotalMaterialCost().Select(cost=>new JObject{
                ["defName"]=cost.thingDef.defName,["label"]=cost.thingDef.label,
                ["needed"]=construction.ThingCountNeeded(cost.thingDef),
                ["allowedOnMap"]=supplies.Where(t=>t.def==cost.thingDef && !t.IsForbidden(Faction.OfPlayer)).Sum(t=>t.stackCount),
                ["forbiddenOnMap"]=supplies.Where(t=>t.def==cost.thingDef && t.IsForbidden(Faction.OfPlayer)).Sum(t=>t.stackCount)
            }));
        }
        public static JArray Workers(Map map,Thing target)=>new JArray(map.mapPawns.FreeColonistsSpawned.Where(p=>p.CurJob!=null &&
            (p.CurJob.targetA.Thing==target || p.CurJob.targetB.Thing==target || p.CurJob.targetC.Thing==target)).Select(p=>new JObject{
                ["pawnId"]=p.thingIDNumber,["name"]=p.LabelShort,["job"]=p.CurJob.def.defName,
                ["activity"]=p.CurJob.def==JobDefOf.HaulToContainer?"delivering materials":p.CurJob.def==JobDefOf.FinishFrame?"constructing":"working on target"}));
        public static JArray Orders(Map map) => new JArray(map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint)
            .Concat(map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame)).Take(20).Select(t => new JObject {
                ["id"] = t.thingIDNumber, ["defName"] = t.def.entityDefToBuild?.defName,
                ["x"] = t.Position.x, ["z"] = t.Position.z,
                ["stage"] = t is Frame ? "frame" : "blueprint",
                ["workDone"] = t is Frame frame ? (int)frame.workDone : 0,
                ["pawnsTargeting"]=Workers(map,t),
                ["materials"]=Materials(t),["inspectionTool"]="pawns_orders"
            }));
    }
}
