using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;
using Verse.AI;
namespace RimBot.Colony
{
    public static class ColonyDevelopment
    {
        private static int Int(JObject a,string key) { if(a[key]?.Type!=JTokenType.Integer) throw new ArgumentException("Supply integer "+key); return a[key].Value<int>(); }
        private static string Text(JObject a,string key) { if(a[key]?.Type!=JTokenType.String || string.IsNullOrWhiteSpace(a[key].Value<string>())) throw new ArgumentException("Supply "+key); return a[key].Value<string>(); }
        public static bool IsAction(string name)=>new[]{"zones_growing_designate","bills_configure","bills_add","research_select","orders_hunt","equipment_equip"}.Contains(name);
        public static string Execute(Map map,string name,JObject a)
        {
            switch(name) {
                case "plants_sowable":
                    return new JArray(DefDatabase<ThingDef>.AllDefs.Where(d=>d.plant?.Sowable==true && (d.plant.sowResearchPrerequisites==null || d.plant.sowResearchPrerequisites.All(r=>r.IsFinished)))
                        .OrderBy(d=>d.plant.growDays).Select(d=>new JObject { ["defName"]=d.defName,["label"]=d.label,["minFertility"]=d.plant.fertilityMin,["fertilitySensitivity"]=d.plant.fertilitySensitivity,["sowTags"]=new JArray(d.plant.sowTags??new System.Collections.Generic.List<string>()),["growDays"]=d.plant.growDays,["minGrowingSkill"]=d.plant.sowMinSkill,
                            ["harvestProduct"]=d.plant.harvestedThingDef?.defName,["harvestLabel"]=d.plant.harvestedThingDef?.label,
                            ["harvestYield"]=d.plant.harvestYield,["nutritionPerProduct"]=d.plant.harvestedThingDef?.GetStatValueAbstract(StatDefOf.Nutrition)??0,
                            ["humanEdible"]=d.plant.harvestedThingDef?.ingestible?.HumanEdible??false,
                            ["description"]=ActivitySummary.Short(d.description,220) })).ToString(Formatting.None);
                case "zones_growing_designate":
                    int x=Int(a,"x"),z=Int(a,"z"),w=Int(a,"width"),h=Int(a,"height");
                    if(w<1 || h<1 || x<0 || z<0 || w>map.Size.x-x || h>map.Size.z-z) throw new ArgumentException("Growing area must be within the map.");
                    var crop=DefDatabase<ThingDef>.GetNamedSilentFail(Text(a,"crop"));
                    if(crop?.plant?.Sowable!=true || crop.plant.sowResearchPrerequisites?.Any(r=>!r.IsFinished)==true) throw new ArgumentException("Unknown or unavailable crop. Use the exact defName from plants_sowable, not a product name. Available: "+string.Join(", ",DefDatabase<ThingDef>.AllDefs.Where(d=>d.plant?.Sowable==true && (d.plant.sowResearchPrerequisites==null || d.plant.sowResearchPrerequisites.All(r=>r.IsFinished))).Select(d=>d.defName)));
                    var cells=CellRect.FromLimits(x,z,x+w-1,z+h-1).Cells.ToList();
                    return PlayerOrders.Zone(map,cells,true,crop,a.Value<int?>("zoneId"));
                case "bills_list":
                    return new JArray(map.listerBuildings.allBuildingsColonist.OfType<Building_WorkTable>().Take(12).Select(t=>new JObject { ["id"]=t.thingIDNumber,["label"]=t.LabelShort,["defName"]=t.def.defName,
                        ["recipes"]=new JArray(t.def.AllRecipes.Where(r=>r.AvailableNow).Take(15).Select(r=>new JObject{["defName"]=r.defName,["label"]=r.label})),
                        ["bills"]=new JArray(t.BillStack.Bills.OfType<Bill_Production>().Select(b=>new JObject{["billId"]=b.GetUniqueLoadID(),["recipe"]=b.recipe.defName,["mode"]=b.repeatMode.defName,["target"]=b.targetCount,["repeatCount"]=b.repeatCount,["supportsTarget"]=b.recipe.WorkerCounter.CanCountProducts(b),["repeatModes"]=new JArray(BillRepeatModeDefOf.RepeatCount.defName,BillRepeatModeDefOf.TargetCount.defName,BillRepeatModeDefOf.Forever.defName)})) })).ToString(Formatting.None);
                case "bills_add":
                    var giver=map.listerBuildings.allBuildingsColonist.OfType<Building_WorkTable>().FirstOrDefault(t=>t.thingIDNumber==Int(a,"workstationId"));
                    if(giver==null || giver.BillStack.Count>=BillStack.MaxCount) throw new ArgumentException("Worktable unavailable or bill stack full.");
                    var recipe=giver.def.AllRecipes.FirstOrDefault(r=>r.defName==Text(a,"recipe") && r.AvailableNow);
                    if(recipe==null) throw new ArgumentException("Recipe unavailable at this worktable.");
                    var added=recipe.MakeNewBill(); giver.BillStack.AddBill(added);
                    return new JObject{["billId"]=added.GetUniqueLoadID(),["recipe"]=recipe.defName,["note"]="Bill added with native defaults. Configure by billId."}.ToString(Formatting.None);
                case "bills_configure":
                    var table=map.listerBuildings.allBuildingsColonist.OfType<Building_WorkTable>().FirstOrDefault(t=>t.thingIDNumber==Int(a,"workstationId"));
                    var bill=table?.BillStack.Bills.OfType<Bill_Production>().FirstOrDefault(b=>b.GetUniqueLoadID()==Text(a,"billId"));
                    if(bill==null) throw new ArgumentException("Choose an existing production bill by ID from bills_list.");
                    var mode=DefDatabase<BillRepeatModeDef>.GetNamedSilentFail(Text(a,"mode"));
                    if(mode!=BillRepeatModeDefOf.Forever && mode!=BillRepeatModeDefOf.RepeatCount && mode!=BillRepeatModeDefOf.TargetCount) throw new ArgumentException("Use a repeat mode from bills_list.");
                    if(mode==BillRepeatModeDefOf.TargetCount && !bill.recipe.WorkerCounter.CanCountProducts(bill)) throw new ArgumentException("RimWorld cannot count this recipe's products for a target bill.");
                    int count=a["count"]==null?(mode==BillRepeatModeDefOf.RepeatCount?bill.repeatCount:bill.targetCount):Int(a,"count");
                    if(count<0) throw new ArgumentException("Count cannot be negative.");
                    bill.repeatMode=mode;
                    if(mode==BillRepeatModeDefOf.RepeatCount) bill.repeatCount=count;
                    if(mode==BillRepeatModeDefOf.TargetCount) bill.targetCount=count;
                    return "Bill updated: "+bill.LabelCap+"; "+bill.RepeatInfoText+".";
                case "research_list":
                    return new JArray(DefDatabase<ResearchProjectDef>.AllDefs.Where(r=>!r.IsFinished).OrderBy(r=>r.baseCost).Take(25).Select(r=>new JObject{["defName"]=r.defName,["label"]=r.label,["canStart"]=r.CanStartNow,
                        ["prerequisites"]=new JArray(r.prerequisites?.Select(p=>p.defName)??Enumerable.Empty<string>())})).ToString(Formatting.None);
                case "research_select":
                    var project=DefDatabase<ResearchProjectDef>.GetNamedSilentFail(Text(a,"research"));
                    if(project==null || project.IsFinished || !project.CanStartNow) throw new ArgumentException("Research is unavailable, finished, or missing prerequisites/bench. Use research_list and build the required bench normally.");
                    Find.ResearchManager.SetCurrentProject(project);
                    return "Research selected: "+project.label+". Researchers still need normal bench access and work priorities.";
                case "orders_hunt":
                    var animal=map.mapPawns.AllPawnsSpawned.FirstOrDefault(p=>p.thingIDNumber==Int(a,"animalId"));
                    return PlayerOrders.Hunt(map,animal);
                case "equipment_equip":
                    var pawn=map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p=>p.thingIDNumber==Int(a,"pawnId"));
                    var weapon=map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver).FirstOrDefault(t=>t.thingIDNumber==Int(a,"itemId"));
                    return PlayerOrders.Equip(map,pawn,weapon);
                default: throw new ArgumentException("Unknown development tool: "+name);
            }
        }
    }
}
